namespace KanamiReporter.Core;

public interface IFrameSource : IAsyncDisposable
{
    event EventHandler<CapturedFrame>? FrameAvailable;
    event EventHandler<CaptureStatus>? StatusChanged;

    Task<IReadOnlyList<CaptureTargetDescriptor>> DiscoverTargetsAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CaptureTargetDescriptor target, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface IRecognitionEngine
{
    int LoadedTemplateCount { get; }

    DetectionResult Process(CapturedFrame normalizedFrame, double threshold);
    void SaveTemplate(StateId stateId, CapturedFrame normalizedFrame, Roi roi);
    void ReloadTemplates();
    void Reset();
}

public interface IAnnouncementPlayer : IAsyncDisposable
{
    IReadOnlyList<AudioDeviceInfo> GetDevices();
    Task PlayAsync(string filePath, string? deviceId, float volume, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);
    Task<string> DownloadAsync(UpdateCheckResult update, CancellationToken cancellationToken = default);
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    Version CurrentVersion,
    Version LatestVersion,
    string? InstallerUrl,
    string? Sha256,
    string? ReleaseNotes);
