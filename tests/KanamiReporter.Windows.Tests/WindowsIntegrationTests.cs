using System.IO.Compression;
using KanamiReporter.Core;
using KanamiReporter.Windows;

namespace KanamiReporter.Windows.Tests;

public sealed class WindowsIntegrationTests
{
    [Fact]
    public async Task SettingsStoreRoundTripsJson()
    {
        var root = CreateTempDirectory();
        var paths = new AppPaths(root);
        var store = new SettingsStore(paths);
        var settings = new AppSettings
        {
            MatchThreshold = 0.93,
            AudioVolume = 0.45f,
            LastCaptureTargetId = "display:1",
            FirstRunCompleted = true
        };

        await store.SaveAsync(settings);
        var restored = await store.LoadAsync();

        Assert.Equal(0.93, restored.MatchThreshold);
        Assert.Equal(0.45f, restored.AudioVolume);
        Assert.Equal("display:1", restored.LastCaptureTargetId);
        Assert.True(restored.FirstRunCompleted);
    }

    [Fact]
    public async Task ResourceZipImportsKrtAndMp3()
    {
        var root = CreateTempDirectory();
        var paths = new AppPaths(root);
        var importer = new ResourceImporter(paths);
        var zip = Path.Combine(root, "resources.zip");

        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "Kanami-Reporter/templates/round_start.krt", [1, 2, 3, 4]);
            WriteEntry(archive, "Kanami-Reporter/voices/test.mp3", [5, 6, 7]);
        }

        var result = await importer.ImportZipAsync(zip);

        Assert.Equal(1, result.TemplateCount);
        Assert.Equal(1, result.VoiceCount);
        Assert.True(File.Exists(Path.Combine(paths.Templates, "round_start.krt")));
        Assert.True(File.Exists(Path.Combine(paths.Voices, "test.mp3")));
    }

    [Fact]
    public void AppPathsCreatesExpectedDirectories()
    {
        var root = CreateTempDirectory();
        var paths = new AppPaths(root);

        Assert.True(Directory.Exists(paths.Templates));
        Assert.True(Directory.Exists(paths.Voices));
        Assert.True(Directory.Exists(paths.Logs));
        Assert.EndsWith("config.json", paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KanamiReporterWindowsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
