using System.IO.Compression;
using System.Reflection;
using KanamiReporter.Windows;

namespace KanamiReporter.App;

public sealed class BuiltInResourcePack
{
    private const string ResourceName = "KanamiReporter.App.Assets.builtin-resources.zip";
    private const int PackVersion = 1;

    private readonly AppPaths _paths;
    private readonly FileLogger _logger;

    public BuiltInResourcePack(AppPaths paths, FileLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public bool IsEmbedded => typeof(BuiltInResourcePack).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    public ImportResult EnsureInstalled(bool force = false)
    {
        var marker = Path.Combine(_paths.Root, $".builtin-resources-v{PackVersion}");
        if (!force && File.Exists(marker))
        {
            return new ImportResult(0, 0, 0);
        }

        using var stream = OpenResourceStream();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var templates = 0;
        var voices = 0;
        var skipped = 0;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var normalized = entry.FullName.Replace('\\', '/');
            var extension = Path.GetExtension(entry.Name);
            string? destinationDirectory = null;

            if (HasDirectory(normalized, "templates"))
            {
                destinationDirectory = _paths.Templates;
            }
            else if (HasDirectory(normalized, "voices"))
            {
                destinationDirectory = _paths.Voices;
            }

            if (destinationDirectory is null)
            {
                continue;
            }

            if (!string.Equals(extension, ".krt", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(destinationDirectory, Path.GetFileName(entry.Name));
            if (File.Exists(target))
            {
                skipped++;
                continue;
            }

            entry.ExtractToFile(target, overwrite: false);
            if (string.Equals(extension, ".krt", StringComparison.OrdinalIgnoreCase))
            {
                templates++;
            }
            else
            {
                voices++;
            }
        }

        File.WriteAllText(
            marker,
            $"Built-in resource pack v{PackVersion} applied at {DateTimeOffset.Now:O}.{Environment.NewLine}" +
            $"Templates: {templates}, voices: {voices}, skipped existing files: {skipped}.{Environment.NewLine}");
        _logger.Info(
            $"内置资源包检查完成：新增模板 {templates} 个，新增语音 {voices} 个，保留已有文件 {skipped} 个。");
        return new ImportResult(templates, voices, skipped);
    }

    private static bool HasDirectory(string normalizedPath, string directoryName)
    {
        return normalizedPath.StartsWith(directoryName + "/", StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.Contains("/" + directoryName + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static Stream OpenResourceStream()
    {
        return typeof(BuiltInResourcePack).Assembly.GetManifestResourceStream(ResourceName)
               ?? throw new InvalidOperationException("内置资源包未嵌入到程序中。");
    }
}
