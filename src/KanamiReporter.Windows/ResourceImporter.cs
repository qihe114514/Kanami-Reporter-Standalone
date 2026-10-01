using System.IO.Compression;

namespace KanamiReporter.Windows;

public sealed record ImportResult(int TemplateCount, int VoiceCount, int SkippedCount)
{
    public bool HasChanges => TemplateCount > 0 || VoiceCount > 0;
}

public sealed class ResourceImporter
{
    private readonly AppPaths _paths;

    public ResourceImporter(AppPaths paths)
    {
        _paths = paths;
    }

    public Task<ImportResult> ImportFromObsAsync(CancellationToken cancellationToken = default)
    {
        var obsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio",
            "plugin_config",
            "Kanami-Reporter");
        return ImportDirectoryAsync(obsRoot, overwrite: false, cancellationToken);
    }

    public Task<ImportResult> ImportDirectoryAsync(string sourceDirectory, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException("资源目录不存在。");
        }

        return Task.Run(() => ImportDirectories(
            Directory.EnumerateFiles(sourceDirectory, "*.krt", SearchOption.AllDirectories),
            Directory.EnumerateFiles(sourceDirectory, "*.mp3", SearchOption.AllDirectories),
            overwrite,
            cancellationToken), cancellationToken);
    }

    public async Task<ImportResult> ImportZipAsync(string zipPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "KanamiReporterImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, temporary), cancellationToken);
            return await ImportDirectoryAsync(temporary, overwrite, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响导入结果。
            }
        }
    }

    private ImportResult ImportDirectories(
        IEnumerable<string> templateFiles,
        IEnumerable<string> voiceFiles,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var skipped = 0;
        var templates = CopyFiles(templateFiles, _paths.Templates, overwrite, ref skipped, cancellationToken);
        var voices = CopyFiles(voiceFiles, _paths.Voices, overwrite, ref skipped, cancellationToken);
        return new ImportResult(templates, voices, skipped);
    }

    private static int CopyFiles(
        IEnumerable<string> files,
        string destination,
        bool overwrite,
        ref int skipped,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var copied = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target) && !overwrite)
            {
                skipped++;
                continue;
            }

            File.Copy(file, target, overwrite);
            copied++;
        }

        return copied;
    }
}

