using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using KanamiReporter.Core;

namespace KanamiReporter.Windows;

/// <summary>
/// 从 GitHub Releases 检查并下载更新。
///
/// 国内直连 github.com 经常超时或被重置，所以每个请求都按「上次成功过的路由 → 直连 →
/// 依次尝试各加速镜像」的顺序回退：直连可用时行为与原来完全一致，直连不通时自动改走
/// gh-proxy 等镜像，用户不需要手工代理。
///
/// 安装包下载后一律校验 SHA256（校验失败会删掉文件并换下一个路由重试），
/// 因此镜像内容错误不会导致装错包。
/// </summary>
public sealed class GitHubUpdateService : IUpdateService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// GitHub 加速镜像前缀，按顺序尝试。用法：前缀 + 原始 GitHub 链接。
    /// 第一个就是 gh-proxy；后面的用于它临时不可用时兜底。
    /// 2026-10-03 实测（当时本机直连 github.com 下载已不通）：前三个都能取到
    /// 更新清单与安装包（含 Range 请求），其余常见镜像已失效，不要加回来。
    /// </summary>
    private static readonly string[] MirrorPrefixes =
    [
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
        "https://ghfast.top/"
    ];

    private const string DirectRouteLabel = "直连";

    /// <summary>单次尝试的时限：清单很小，超时短一点可以尽快切换路由。</summary>
    private static readonly TimeSpan ManifestAttemptTimeout = TimeSpan.FromSeconds(12);

    /// <summary>安装包下载的单次尝试时限。</summary>
    private static readonly TimeSpan InstallerAttemptTimeout = TimeSpan.FromMinutes(15);

    private readonly HttpClient _httpClient;
    private string? _preferredPrefix;
    private string _lastRoute = string.Empty;

    public GitHubUpdateService(string manifestUrl)
    {
        ManifestUrl = manifestUrl;
        _httpClient = new HttpClient
        {
            // 总超时交给每次尝试自己的 CancellationToken 控制，
            // 否则 HttpClient 的 30 秒上限会把大安装包的下载掐断。
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KanamiReporter/1.0");
    }

    public string ManifestUrl { get; }

    /// <summary>最近一次网络请求实际使用的路由（“直连”或镜像域名），用于界面提示与日志。</summary>
    public string LastRoute => _lastRoute;

    /// <summary>最近一次请求是否走了加速镜像。</summary>
    public bool LastRequestUsedMirror => _lastRoute.Length > 0 && _lastRoute != DirectRouteLabel;

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var current = ParseVersion(currentVersion);
        var manifest = await WithFallbackAsync(
            ManifestUrl,
            ManifestAttemptTimeout,
            async (stream, token) => await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, token)
                ?? throw new InvalidDataException("更新清单为空。"),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidDataException("更新清单缺少版本号。");
        }

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

        await WithFallbackAsync(
            update.InstallerUrl,
            InstallerAttemptTimeout,
            async (stream, token) =>
            {
                await using (var destination = File.Create(target))
                {
                    await stream.CopyToAsync(destination, token);
                }

                var actual = await ComputeSha256Async(target, cancellationToken);
                if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(target);
                    // 抛出去让外层换下一个路由重试：镜像内容损坏时不能装。
                    throw new InvalidDataException("更新安装包校验失败。");
                }

                return true;
            },
            cancellationToken);

        return target;
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// 依次尝试各路由请求同一个 GitHub 链接，成功的路由会被记住（下次优先使用）。
    /// </summary>
    private async Task<T> WithFallbackAsync<T>(
        string url,
        TimeSpan attemptTimeout,
        Func<Stream, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var (label, prefix) in BuildRoutes())
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(attemptTimeout);
                using var response = await _httpClient.GetAsync(
                    prefix + url,
                    HttpCompletionOption.ResponseHeadersRead,
                    attempt.Token);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
                var result = await read(stream, cancellationToken);

                _preferredPrefix = prefix;
                _lastRoute = label;
                return result;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidDataException)
            {
                lastError = exception;
            }
        }

        throw new InvalidOperationException(
            $"更新服务器无法访问（已尝试{DirectRouteLabel}与 {MirrorPrefixes.Length} 个加速镜像），请稍后重试或检查网络。",
            lastError);
    }

    /// <summary>路由顺序：上次成功的路由优先，其次是直连，最后依次是其余镜像。</summary>
    private IEnumerable<(string Label, string Prefix)> BuildRoutes()
    {
        if (_preferredPrefix is { Length: > 0 } preferred)
        {
            yield return (DescribePrefix(preferred), preferred);
        }

        yield return (DirectRouteLabel, string.Empty);

        foreach (var prefix in MirrorPrefixes)
        {
            if (prefix != _preferredPrefix)
            {
                yield return (DescribePrefix(prefix), prefix);
            }
        }
    }

    private static string DescribePrefix(string prefix) =>
        Uri.TryCreate(prefix, UriKind.Absolute, out var uri) ? uri.Host : prefix;

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
