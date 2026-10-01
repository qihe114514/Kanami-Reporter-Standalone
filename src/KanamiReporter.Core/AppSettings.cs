namespace KanamiReporter.Core;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool RecognitionEnabled { get; set; } = true;
    public double MatchThreshold { get; set; } = ReporterStates.DefaultThreshold;
    public string? LastCaptureTargetId { get; set; }
    public string? LastCaptureProcessName { get; set; }
    public string? AudioDeviceId { get; set; }
    public float AudioVolume { get; set; } = 0.8f;
    // 保留字段用于兼容旧版配置；识别启动时不再自动隐藏主窗口。
    public bool MinimizeWhenRecognitionStarts { get; set; }
    public bool FirstRunCompleted { get; set; }
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    public void Normalize()
    {
        SchemaVersion = Math.Max(1, SchemaVersion);
        MatchThreshold = Math.Clamp(MatchThreshold, 0.50, 0.999);
        AudioVolume = Math.Clamp(AudioVolume, 0f, 1f);
        // null 表示跟随系统输出，空字符串统一收敛为该语义。
        AudioDeviceId = string.IsNullOrWhiteSpace(AudioDeviceId) ? null : AudioDeviceId;
    }
}


