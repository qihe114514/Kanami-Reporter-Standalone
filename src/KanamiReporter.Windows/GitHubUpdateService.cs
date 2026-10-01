using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using KanamiReporter.Core;

namespace KanamiReporter.Windows;

public sealed class GitHubUpdateService : IUpdateService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public GitHubUpdateService(string manifestUrl)
    {
        ManifestUrl = manifestUrl;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KanamiReporter/1.0");
    }

    public string ManifestUrl { get; }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var current = ParseVersion(currentVersion);
        await using var stream = await _httpClient.GetStreamAsync(ManifestUrl, cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("更新清单为空。");
        var latest = ParseVersion(manifest.Version);

        return new UpdateCheckResult(
            latest > current,
            current,
            latest,
            manifest.InstallerUrl,
            manifest.Sha256,
            manifest.ReleaseNotes);
    }

    public async Task<string> DownloadAsync(UpdateCheckResult update, CancellationToken cancellationToken = default)
    {
        if (!update.IsUpdateAvailable || string.IsNullOrWhiteSpace(update.InstallerUrl) || string.IsNullOrWhiteSpace(update.Sha256))
        {
            throw new InvalidOperationException("没有可下载的更新。");
        }

        var directory = Path.Combine(Path.GetTempPath(), "KanamiReporterUpdate");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"KanamiReporter-Setup-{update.LatestVersion}.exe");
        await using (var source = await _httpClient.GetStreamAsync(update.InstallerUrl, cancellationToken))
        await using (var destination = File.Create(target))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        var actual = await ComputeSha256Async(target, cancellationToken);
        if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidDataException("更新安装包校验失败，文件已删除。");
        }

        return target;
    }

    public void Dispose() => _httpClient.Dispose();

    private static Version ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var version) ? version : new Version(0, 0);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private sealed record UpdateManifest(
        string Version,
        string InstallerUrl,
        string Sha256,
        string? ReleaseNotes);
}
