namespace KanamiReporter.Core;

public enum CaptureTargetKind
{
    Window,
    Display
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

public sealed record CaptureStatus(
    bool IsRunning,
    string Message,
    bool UsedFallback = false,
    Exception? Error = null);

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public sealed record AnnouncementRequest(string EventId, IReadOnlyList<string> FileNames);

public sealed record AnnouncementPlaybackStatus(
    string EventId,
    string FileName,
    bool IsPlaying,
    bool IsCompleted,
    Exception? Error = null);

