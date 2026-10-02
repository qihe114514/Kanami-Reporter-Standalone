namespace KanamiReporter.Core;

public enum CaptureTargetKind
{
    Window,
    Display
}

/// <summary>
/// 采集后端。Auto：优先用 Windows 图形捕获，读不出像素时自动改用抓屏；
/// GraphicsCapture：只用图形捕获；Grab：直接用抓屏（部分机器上图形捕获拿不到画面时的兼容模式）。
/// </summary>
public enum CaptureBackend
{
    Auto,
    GraphicsCapture,
    Grab
}

public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed record CaptureTargetDescriptor(
    string Id,
    string DisplayName,
    CaptureTargetKind Kind,
    nint NativeHandle,
    RectI Bounds,
    string? ProcessName = null,
    string? MonitorDeviceName = null);

public sealed record CapturedFrame(
    byte[] Bgra,
    int Width,
    int Height,
    TimeSpan Timestamp,
    CaptureTargetDescriptor? Source = null);

public readonly record struct Roi(int X, int Y, int Width, int Height)
{
    public static Roi Empty => new(0, 0, 0, 0);
}

/// <summary>
/// 购买阶段横幅上「攻方 / 守方」标签的匹配结果。
/// 横幅每个回合的开局都会出现，因此可以持续校正攻守阵营，
/// 而不是只在整局或半场开始时才拿到一次阵营信息。
/// </summary>
public readonly record struct SideSignal(
    bool AttackerHit,
    bool DefenderHit,
    double AttackerScore,
    double DefenderScore)
{
    public static SideSignal None { get; } = new(false, false, -1.0, -1.0);
}

public sealed record DetectionResult(
    StateId? StateId,
    string StateName,
    double BestScore,
    TimeSpan StateDuration,
    IReadOnlyList<double> Scores,
    int RoundNumber = 0,
    int Side = 0,
    TimeSpan? EstimatedPhaseRemaining = null,
    string PhaseTimingLabel = "");

public sealed record MatchOffsetDetail(
    int OffsetX,
    int OffsetY,
    double Sum,
    double Squared,
    double Product,
    double Energy,
    double Score);

public sealed record TemplateMatchDetails(
    double BestScore,
    int BestOffsetX,
    int BestOffsetY,
    IReadOnlyList<MatchOffsetDetail> Offsets);

/// <summary>
/// 采集状态。Warning 用于"采集仍在运行但需要用户注意"的情况（画面中断、已回退到显示器），
/// 界面会把它当提示显示，但不会改变运行状态。
/// </summary>
public sealed record CaptureStatus(
    bool IsRunning,
    string Message,
    bool UsedFallback = false,
    Exception? Error = null,
    bool IsWarning = false);

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    /// <summary>跟随系统输出：每次播放时重新解析系统当前的默认输出设备。</summary>
    public static AudioDeviceInfo FollowSystem { get; } = new(string.Empty, "跟随系统输出", false);

    public bool IsFollowSystem => Id.Length == 0;

    /// <summary>用于播放和保存的设备 ID；跟随系统输出时为 null。</summary>
    public string? PlaybackDeviceId => IsFollowSystem ? null : Id;
}

public sealed record AnnouncementRequest(string EventId, IReadOnlyList<string> FileNames);

public sealed record AnnouncementPlaybackStatus(
    string EventId,
    string FileName,
    bool IsPlaying,
    bool IsCompleted,
    Exception? Error = null);

