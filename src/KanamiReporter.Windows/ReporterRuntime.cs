using KanamiReporter.Core;

namespace KanamiReporter.Windows;

public sealed class ReporterRuntime : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly IFrameSource _frameSource;
    private readonly RecognitionEngine _engine;
    private readonly IAnnouncementPlayer _player;
    private readonly object _gate = new();
    private CapturedFrame? _latestFrame;
    private long _lastProcessedTicks;
    private bool _disposed;

    public ReporterRuntime(
        AppPaths paths,
        IAppLogger logger,
        IFrameSource frameSource,
        RecognitionEngine engine,
        IAnnouncementPlayer player)
    {
        _paths = paths;
        _logger = logger;
        _frameSource = frameSource;
        _engine = engine;
        _player = player;
        _frameSource.FrameAvailable += OnFrameAvailable;
        _frameSource.StatusChanged += (_, status) => StatusChanged?.Invoke(this, status);
        _engine.DetectionUpdated += (_, result) => DetectionUpdated?.Invoke(this, result);
        _engine.AnnouncementRequested += OnAnnouncementRequested;
    }

    public event EventHandler<CapturedFrame>? FrameAvailable;
    public event EventHandler<CaptureStatus>? StatusChanged;
    public event EventHandler<DetectionResult>? DetectionUpdated;
    public event EventHandler<string>? AnnouncementTriggered;
    public event EventHandler<AnnouncementPlaybackStatus>? AnnouncementPlaybackChanged;

    public CapturedFrame? LatestFrame
    {
        get
        {
            lock (_gate)
            {
                return _latestFrame;
            }
        }
    }

    public int LoadedTemplateCount => _engine.LoadedTemplateCount;
    public TemplateModel? GetTemplate(StateId stateId) => _engine.GetTemplate(stateId);
    public IReadOnlyList<AudioDeviceInfo> GetAudioDevices() => _player.GetDevices();
    public string TemplateDirectory => _engine.TemplateDirectory;
    public double Threshold { get; set; } = ReporterStates.DefaultThreshold;
    public string? AudioDeviceId { get; set; }
    public float AudioVolume { get; set; } = 0.8f;
    public Task<IReadOnlyList<CaptureTargetDescriptor>> DiscoverTargetsAsync(CancellationToken cancellationToken = default) =>
        _frameSource.DiscoverTargetsAsync(cancellationToken);

    public Task StartAsync(CaptureTargetDescriptor target, CancellationToken cancellationToken = default) =>
        _frameSource.StartAsync(target, cancellationToken);

    public Task StopAsync() => _frameSource.StopAsync();

    public void SaveTemplate(StateId stateId, Roi roi)
    {
        var frame = LatestFrame ?? throw new InvalidOperationException("还没有可用于保存模板的捕获帧。");
        _engine.SaveTemplate(stateId, frame, roi);
        _logger.Info($"模板已保存：{ReporterStates.GetName(stateId)} ROI={roi.X},{roi.Y},{roi.Width},{roi.Height}");
    }

    public void ReloadTemplates()
    {
        _engine.ReloadTemplates();
        _logger.Info($"已重新加载 {_engine.LoadedTemplateCount} 个模板。");
    }

    public void ResetRecognition() => _engine.Reset();

    public Task PlayFileAsync(string filePath, CancellationToken cancellationToken = default) =>
        _player.PlayAsync(filePath, AudioDeviceId, AudioVolume, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _frameSource.FrameAvailable -= OnFrameAvailable;
        _engine.AnnouncementRequested -= OnAnnouncementRequested;
        await _frameSource.DisposeAsync();
        await _player.DisposeAsync();
        _engine.Dispose();
        _disposed = true;
    }

    private void OnFrameAvailable(object? sender, CapturedFrame frame)
    {
        lock (_gate)
        {
            _latestFrame = frame;
        }

        FrameAvailable?.Invoke(this, frame);

        var now = Environment.TickCount64;
        if (now - _lastProcessedTicks < ReporterStates.CaptureIntervalMilliseconds)
        {
            return;
        }

        _lastProcessedTicks = now;
        try
        {
            _ = _engine.Process(frame, Threshold);
        }
        catch (Exception exception)
        {
            _logger.Error("识别帧处理失败。", exception);
        }
    }

    private void OnAnnouncementRequested(object? sender, AnnouncementRequest request)
    {
        AnnouncementTriggered?.Invoke(this, request.EventId);
        _ = PlayAnnouncementAsync(request);
    }

    private async Task PlayAnnouncementAsync(AnnouncementRequest request)
    {
        try
        {
            var candidates = request.FileNames
                .Select(name => Path.Combine(_paths.Voices, name))
                .Where(File.Exists)
                .ToArray();
            var file = candidates.Length == 0
                ? null
                : candidates[Random.Shared.Next(candidates.Length)];

            if (file is null)
            {
                if (request.FileNames.Count > 0)
                {
                    _logger.Warning($"未找到事件语音：{request.EventId}");
                }

                AnnouncementPlaybackChanged?.Invoke(
                    this,
                    new AnnouncementPlaybackStatus(request.EventId, "未找到文件", false, false));
                return;
            }

            var fileName = Path.GetFileName(file);
            AnnouncementPlaybackChanged?.Invoke(
                this,
                new AnnouncementPlaybackStatus(request.EventId, fileName, true, false));
            await _player.PlayAsync(file, AudioDeviceId, AudioVolume);
            AnnouncementPlaybackChanged?.Invoke(
                this,
                new AnnouncementPlaybackStatus(request.EventId, fileName, false, true));
        }
        catch (Exception exception)
        {
            _logger.Error($"播放事件语音失败：{request.EventId}", exception);
            AnnouncementPlaybackChanged?.Invoke(
                this,
                new AnnouncementPlaybackStatus(request.EventId, request.EventId, false, false, exception));
        }
    }
}





