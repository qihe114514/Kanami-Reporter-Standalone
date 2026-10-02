using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using KanamiReporter.Core;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace KanamiReporter.Windows;

public sealed class WindowsGraphicsCaptureSource : IFrameSource
{
    private const int DwmwaCloaked = 14;
    private const uint MonitorDefaultToNearest = 2;
    private const int WindowStyleExtendedIndex = -20;

    private static readonly nint ExtendedStyleToolWindow = 0x00000080;
    private static readonly nint ExtendedStyleAppWindow = 0x00040000;

    /// <summary>启动后这么久还没有收到任何一帧，就判定这次采集拿不到画面。</summary>
    private const int NoPictureTimeoutMilliseconds = 5000;

    /// <summary>画面中断这么久先告警；持续到 <see cref="StallRestartMilliseconds"/> 就重建采集会话。</summary>
    private const int StallWarningMilliseconds = 5000;
    private const int StallRestartMilliseconds = 15000;

    /// <summary>单次识别会话里重建采集会话的上限，避免窗口一直不可用时反复重建。</summary>
    private const int MaxSessionRestarts = 3;

    /// <summary>按这个周期把帧数写进日志：远程排查时"到底有没有收到画面"是最关键的一条信息。</summary>
    private const int StatisticsIntervalMilliseconds = 30000;

    private const int HealthCheckIntervalMilliseconds = 1000;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _captureGate = new();
    private readonly object _framePoolGate = new();
    private readonly CaptureThread _captureThread = new();
    private readonly IAppLogger? _logger;
    private CaptureTargetDescriptor? _target;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private System.Threading.Timer? _publishTimer;
    private System.Threading.Timer? _healthTimer;
    private System.Threading.Timer? _grabTimer;
    private DesktopScreenGrabber? _grabber;
    private CapturedFrame? _latestFrame;
    private IDirect3DDevice? _device;
    private long _lastFrameTimestamp;
    private long _frameVersion;
    private long _lastPublishedFrameVersion;
    private long _sessionStartedTicks;
    private long _lastFrameArrivedTicks;
    private long _lastProcessedTicks;
    private long _lastStatisticsTicks;
    private long _framesThisSession;
    private long _processedFramesThisSession;
    private long _statisticsFrameCount;
    private RectI _grabRect;
    private int _restartCount;
    private int _uniformFrameCount;
    private int _processingFrame;
    private int _frameCallbacksInFlight;
    private int _captureActive;
    private int _noPictureHandled;
    private int _stallReported;
    private int _sessionRestartRequested;
    private int _consecutiveGrabFailures;
    private bool _fallbackTriggered;
    private bool _grabMode;
    private volatile bool _grabPreferred;
    private bool _disposed;
    private bool _isRunning;

    public WindowsGraphicsCaptureSource(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public event EventHandler<CapturedFrame>? FrameAvailable;
    public event EventHandler<CaptureStatus>? StatusChanged;

    public bool IsRunning => _isRunning;

    /// <summary>采集后端偏好；默认 Auto（图形捕获优先，拿不到画面时自动改用抓屏）。</summary>
    public CaptureBackend Backend { get; set; } = CaptureBackend.Auto;

    public Task<IReadOnlyList<CaptureTargetDescriptor>> DiscoverTargetsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<CaptureTargetDescriptor>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = new List<CaptureTargetDescriptor>();
            targets.AddRange(EnumerateMonitors());
            targets.AddRange(EnumerateWindows(cancellationToken));
            return targets;
        }, cancellationToken);
    }

    public async Task StartAsync(CaptureTargetDescriptor target, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            _restartCount = 0;

            // 采集对象的创建固定在采集线程上（见 CaptureThread 的说明）。
            await Task.Run(
                () => _captureThread.Invoke(() =>
                {
                    StopCore();
                    StartCore(target, usedFallback: false);
                }),
                CancellationToken.None);

            _isRunning = true;
            RaiseStatus(new CaptureStatus(true, $"正在捕获：{target.DisplayName}"));
        }
        catch (Exception exception)
        {
            _logger?.Error($"采集启动失败：{Describe(target)}", exception);

            try
            {
                await Task.Run(() => _captureThread.Invoke(() => StopCore()));
            }
            catch (ObjectDisposedException)
            {
                // 退出流程里采集线程已经收工，无需再清理。
            }

            _isRunning = false;
            var failure = DescribeStartFailure(target, exception);
            RaiseStatus(new CaptureStatus(false, failure.Message, false, failure));
            throw failure;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await Task.Run(() => _captureThread.Invoke(() => StopCore()));
            _isRunning = false;
            RaiseStatus(new CaptureStatus(false, "采集已停止。"));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        _disposed = true;
        _captureThread.Dispose();

        // 不释放 _lifecycleGate：退出流程可能与看门狗任务并发，释放后再 Release 会抛
        // ObjectDisposedException，而 SemaphoreSlim 不释放不占用任何系统句柄。
    }

    private void StartCore(CaptureTargetDescriptor target, bool usedFallback)
    {
        // 显式选了抓屏后端时，从一开始就走抓屏；图形捕获这次运行里已经证明读不出画面时也一样，
        // 免得用户每次停下来再开始都要白等一次超时。都当作"已经回退过"，再拿不到画面就直接报错。
        var grab = usedFallback || Backend == CaptureBackend.Grab || _grabPreferred;

        lock (_captureGate)
        {
            _target = target;
            _lastFrameTimestamp = 0;
            _frameVersion = 0;
            _lastPublishedFrameVersion = 0;
            _uniformFrameCount = 0;
            _fallbackTriggered = grab;
            _grabMode = grab;
            _noPictureHandled = 0;
            _stallReported = 0;
            _sessionRestartRequested = 0;
            _consecutiveGrabFailures = 0;
            _framesThisSession = 0;
            _processedFramesThisSession = 0;
            _statisticsFrameCount = 0;
            _lastFrameArrivedTicks = 0;
            _lastProcessedTicks = 0;
            _sessionStartedTicks = Stopwatch.GetTimestamp();
            _lastStatisticsTicks = _sessionStartedTicks;

            _logger?.Info($"开始采集：{Describe(target)}（{(grab ? "抓屏方式" : "Windows 图形捕获")}）");

            try
            {
                if (grab)
                {
                    StartGrabCore(target);
                }
                else
                {
                    StartGraphicsCaptureCore(target);
                }
            }
            catch
            {
                // 半成品状态必须当场清掉：否则 _captureActive 会停在 1，
                // 看门狗和回退逻辑会把一个不存在的会话当成正在运行的采集。
                StopCore(logSummary: false);
                throw;
            }
        }
    }

    /// <summary>正常路径：Windows 图形捕获。</summary>
    private void StartGraphicsCaptureCore(CaptureTargetDescriptor target)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new InvalidOperationException("当前系统不支持 Windows 图形捕获（需要 Windows 10 2004 / 内部版本 19041 或更高版本）。");
        }

        _item = target.Kind == CaptureTargetKind.Window
            ? GraphicsCaptureInterop.CreateForWindow(target.NativeHandle)
            : GraphicsCaptureInterop.CreateForMonitor(target.NativeHandle);

        if (_item.Size.Width <= 0 || _item.Size.Height <= 0)
        {
            throw new InvalidOperationException("捕获目标尺寸无效。");
        }

        _device = GraphicsCaptureInterop.CreateDirect3DDevice(out var deviceDescription, out var isSoftware);
        if (isSoftware)
        {
            // 软件设备上 Windows 图形捕获通常收不到画面，必须留在日志里，否则这种情况无从查起。
            _logger?.Warning($"采集设备退回到软件设备：{deviceDescription}。软件设备可能收不到画面。");
        }
        else
        {
            _logger?.Info($"采集设备：{deviceDescription}");
        }

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _item.Size);
        _framePool.FrameArrived += OnFrameArrived;
        _item.Closed += OnItemClosed;

        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        Volatile.Write(ref _captureActive, 1);
        _session.StartCapture();
        _publishTimer = new System.Threading.Timer(PublishLatestFrame, null, 0, ReporterStates.CaptureIntervalMilliseconds);
        _healthTimer = new System.Threading.Timer(
            CheckCaptureHealth,
            null,
            HealthCheckIntervalMilliseconds,
            HealthCheckIntervalMilliseconds);
    }

    /// <summary>
    /// 兜底路径：GDI 抓屏。实测有机器（AMD 6750 GRE + Win10 19045）Windows 图形捕获能收到帧、
    /// 却一帧都读不出像素，此时只有绕过 DWM 的抓屏方式能拿到画面。
    /// 先让窗口自己把内容画出来（PrintWindow，不受遮挡影响、也不会把别的窗口画进来），
    /// 失败再退回抓屏幕上的对应区域。代价是窗口最小化或被挡住时抓不到游戏本体。
    /// </summary>
    private void StartGrabCore(CaptureTargetDescriptor target)
    {
        var rect = ResolveGrabRect(target);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new InvalidOperationException("抓屏区域无效。");
        }

        _grabRect = rect;
        _grabber = new DesktopScreenGrabber();
        Volatile.Write(ref _captureActive, 1);
        _grabTimer = new System.Threading.Timer(GrabTick, null, 0, ReporterStates.CaptureIntervalMilliseconds);
        _healthTimer = new System.Threading.Timer(
            CheckCaptureHealth,
            null,
            HealthCheckIntervalMilliseconds,
            HealthCheckIntervalMilliseconds);
        _logger?.Info($"抓屏区域：{rect.X},{rect.Y} {rect.Width}x{rect.Height}");
    }

    private static RectI ResolveGrabRect(CaptureTargetDescriptor target)
    {
        if (target.Kind == CaptureTargetKind.Display)
        {
            return MonitorBounds(target.NativeHandle);
        }

        if (GetWindowRect(target.NativeHandle, out var rect))
        {
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width > 0 && height > 0)
            {
                return new RectI(rect.Left, rect.Top, width, height);
            }
        }

        // 窗口拿不到尺寸就退回它所在的显示器。
        var monitor = MonitorFromWindow(target.NativeHandle, MonitorDefaultToNearest);
        return monitor != nint.Zero ? MonitorBounds(monitor) : default;
    }

    private static RectI MonitorBounds(nint monitor)
    {
        var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        return GetMonitorInfo(monitor, ref info)
            ? new RectI(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top)
            : default;
    }

    private void GrabTick(object? state)
    {
        if (Volatile.Read(ref _captureActive) == 0)
        {
            return;
        }

        var target = _target;
        var grabber = _grabber;
        var rect = _grabRect;
        if (target is null || grabber is null || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        try
        {
            var started = Stopwatch.GetTimestamp();
            byte[] pixels = [];
            var grabbed = target.Kind == CaptureTargetKind.Window &&
                          grabber.TryGrabWindow(target.NativeHandle, rect.Width, rect.Height, out pixels);
            if (!grabbed)
            {
                grabbed = grabber.TryGrabScreen(rect.X, rect.Y, rect.Width, rect.Height, out pixels);
            }

            if (!grabbed || pixels.Length < rect.Width * rect.Height * 4)
            {
                // 采集已经停了（用户点了停止、或正在切换）时的失败不用报，否则日志里会多出一条假故障。
                if (Volatile.Read(ref _captureActive) == 0)
                {
                    return;
                }

                if (Interlocked.Increment(ref _consecutiveGrabFailures) == 1)
                {
                    _logger?.Warning($"抓屏失败：{Describe(target)}{DescribeWindowState(target)}");
                }

                return;
            }

            Interlocked.Exchange(ref _consecutiveGrabFailures, 0);
            Interlocked.Increment(ref _framesThisSession);
            Interlocked.Increment(ref _processedFramesThisSession);
            Volatile.Write(ref _lastProcessedTicks, Stopwatch.GetTimestamp());

            var normalized = FrameProcessing.Normalize(pixels, rect.Width, rect.Height);
            var frame = new CapturedFrame(
                normalized,
                ReporterStates.FrameWidth,
                ReporterStates.FrameHeight,
                TimeSpan.FromSeconds(started / (double)Stopwatch.Frequency),
                target);
            lock (_captureGate)
            {
                _latestFrame = frame;
                _frameVersion++;
            }

            PublishLatestFrame(null);
        }
        catch (Exception exception)
        {
            RaiseStatus(new CaptureStatus(_isRunning, "抓屏失败。", _fallbackTriggered, exception));
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // 帧回调跑在采集线程之外，StopCore 会等这里退出后才释放帧池和设备。
        // 必须先登记再检查标志，否则登记之前 StopCore 可能已经等完并开始释放。
        Interlocked.Increment(ref _frameCallbacksInFlight);
        try
        {
            if (Volatile.Read(ref _captureActive) == 0)
            {
                return;
            }

            // 到达计数与首帧耗时都按"回调被触发"来算：即使这一帧被节流丢弃，
            // 也说明画面是通的，看门狗不该判它没有画面。
            var arrived = Interlocked.Increment(ref _framesThisSession);
            Volatile.Write(ref _lastFrameArrivedTicks, Stopwatch.GetTimestamp());
            if (arrived == 1)
            {
                _logger?.Info($"收到首帧：启动后 {ElapsedMilliseconds(_sessionStartedTicks, Stopwatch.GetTimestamp())} 毫秒。");
            }

            ProcessArrivedFrame(sender);
        }
        finally
        {
            Interlocked.Decrement(ref _frameCallbacksInFlight);
        }
    }

    private void ProcessArrivedFrame(Direct3D11CaptureFramePool sender)
    {
        try
        {
            // 无论是否处理这一帧，都必须先取出并释放帧池缓冲。
            // 否则高刷新率或上一帧仍在处理时，缓冲会占满并停止触发。
            Direct3D11CaptureFrame? frame;
            lock (_framePoolGate)
            {
                frame = sender.TryGetNextFrame();
            }

            if (frame is null)
            {
                return;
            }

            using (frame)
            {
                if (Interlocked.Exchange(ref _processingFrame, 1) != 0)
                {
                    return;
                }

                try
                {
                    var now = Stopwatch.GetTimestamp();
                    var intervalTicks = Stopwatch.Frequency / 10;
                    if (_lastFrameTimestamp != 0 && now - _lastFrameTimestamp < intervalTicks)
                    {
                        return;
                    }

                    _lastFrameTimestamp = now;
                    using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().GetAwaiter().GetResult();
                    var pixels = CopyBitmap(bitmap, out var width, out var height);
                    if (width <= 0 || height <= 0)
                    {
                        return;
                    }

                    if (_target?.Kind == CaptureTargetKind.Window && !_fallbackTriggered && IsUniformBlack(pixels, width, height))
                    {
                        _uniformFrameCount++;
                        if (_uniformFrameCount >= 20 && Interlocked.Exchange(ref _noPictureHandled, 1) == 0)
                        {
                            _uniformFrameCount = 0;
                            _logger?.Warning("窗口采集连续返回全黑画面。");
                            RunInBackground(() => FallbackToGrabAsync("窗口采集连续返回全黑画面"));
                        }
                    }
                    else
                    {
                        _uniformFrameCount = 0;
                    }

                    var normalized = FrameProcessing.Normalize(pixels, width, height);
                    // "读出了像素"才算这一帧真的可用：看门狗据此判断采集有没有画面，
                    // 而不是看回调有没有被触发（实测有机器回调会触发、像素却读不出来）。
                    Interlocked.Increment(ref _processedFramesThisSession);
                    Volatile.Write(ref _lastProcessedTicks, Stopwatch.GetTimestamp());
                    var capturedFrame = new CapturedFrame(
                        normalized,
                        ReporterStates.FrameWidth,
                        ReporterStates.FrameHeight,
                        TimeSpan.FromSeconds((double)now / Stopwatch.Frequency),
                        _target);
                    lock (_captureGate)
                    {
                        _latestFrame = capturedFrame;
                        _frameVersion++;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _processingFrame, 0);
                }
            }
        }
        catch (Exception exception)
        {
            RaiseStatus(new CaptureStatus(_isRunning, "捕获帧处理失败。", _fallbackTriggered, exception));
        }
    }

    /// <summary>
    /// 采集健康检查：既覆盖"启动了但一帧都没有"（用户只看到一片黑、也没有任何提示），
    /// 也覆盖"画面跑到一半断掉"（窗口改尺寸后帧池失效就是这种表现）。
    /// </summary>
    private void CheckCaptureHealth(object? state)
    {
        try
        {
            if (Volatile.Read(ref _captureActive) == 0)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var arrivals = Interlocked.Read(ref _framesThisSession);
            var processed = Interlocked.Read(ref _processedFramesThisSession);

            // 判据是"有没有读出过像素"，不是"有没有收到帧"：图形捕获可能一直收到帧却一帧都读不出来
            // （实测有这种机器），只看帧数会永远等下去，用户看到的是一片黑。
            if (processed == 0)
            {
                if (ElapsedMilliseconds(_sessionStartedTicks, now) < NoPictureTimeoutMilliseconds)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _noPictureHandled, 1) != 0)
                {
                    return;
                }

                var reason = arrivals > 0
                    ? $"收到 {arrivals} 帧但一帧都没能读出像素"
                    : $"{NoPictureTimeoutMilliseconds / 1000} 秒内没有收到任何画面";
                var current = _target;
                _logger?.Warning(current is null ? reason : $"{reason}：{Describe(current)}");
                RunInBackground(() => HandleNoPictureAsync(reason));
                return;
            }

            LogStatisticsIfDue(processed, now);

            var stall = ElapsedMilliseconds(Volatile.Read(ref _lastProcessedTicks), now);
            if (stall >= StallWarningMilliseconds)
            {
                if (Interlocked.Exchange(ref _stallReported, 1) == 0)
                {
                    _logger?.Warning($"采集画面已中断 {stall} 毫秒。");
                    RaiseStatus(new CaptureStatus(true, $"采集画面已中断 {stall / 1000} 秒，正在等待恢复…", IsWarning: true));
                }

                if (stall >= StallRestartMilliseconds &&
                    Volatile.Read(ref _restartCount) < MaxSessionRestarts &&
                    Interlocked.Exchange(ref _sessionRestartRequested, 1) == 0)
                {
                    _logger?.Warning($"采集画面中断超过 {StallRestartMilliseconds / 1000} 秒，重建采集会话。");
                    RunInBackground(RestartSessionAsync);
                }

                return;
            }

            if (Interlocked.Exchange(ref _stallReported, 0) == 1)
            {
                _logger?.Info("采集画面已恢复。");
                RaiseStatus(new CaptureStatus(true, "采集画面已恢复。"));
            }
        }
        catch (Exception exception)
        {
            _logger?.Warning($"采集健康检查失败：{exception.Message}");
        }
    }

    private void LogStatisticsIfDue(long frames, long now)
    {
        var elapsed = ElapsedMilliseconds(_lastStatisticsTicks, now);
        if (elapsed < StatisticsIntervalMilliseconds)
        {
            return;
        }

        var recent = frames - Interlocked.Read(ref _statisticsFrameCount);
        Interlocked.Exchange(ref _statisticsFrameCount, frames);
        _lastStatisticsTicks = now;
        _logger?.Info(
            $"采集统计：本次会话 {frames} 帧，最近 {elapsed / 1000} 秒 {recent} 帧（{recent * 1000.0 / Math.Max(1, elapsed):0.0} 帧/秒）。");
    }

    /// <summary>拿不到画面时的处理：先改用 GDI 抓屏，抓屏也拿不到才停下来明确报错。</summary>
    private async Task HandleNoPictureAsync(string reason)
    {
        var target = _target;
        if (_disposed || target is null || Volatile.Read(ref _captureActive) == 0)
        {
            return;
        }

        var windowState = DescribeWindowState(target);
        if (!IsFallbackTriggered())
        {
            _logger?.Warning($"{reason}：{Describe(target)}{windowState}");
            RaiseStatus(new CaptureStatus(true, $"{reason}，正在改用抓屏方式…", IsWarning: true));
            await FallbackToGrabAsync(reason);
            return;
        }

        var message =
            $"{reason}：{Describe(target)}{windowState}。" +
            "Windows 图形捕获与抓屏两种方式都没能拿到可用画面。请确认游戏画面在前台可见（不要最小化），" +
            "或把游戏切到无边框窗口模式，或改选所在显示器后重新开始识别。";
        _logger?.Error(message);
        await StopAfterFailureAsync(message);
    }

    /// <summary>采集线程上重建采集会话：窗口尺寸变化后帧池会失效，重建是官方给的恢复手段。</summary>
    private async Task RestartSessionAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed || Volatile.Read(ref _captureActive) == 0)
            {
                return;
            }

            await Task.Run(
                () => _captureThread.Invoke(() =>
                {
                    var target = _target;
                    if (target is null)
                    {
                        return;
                    }

                    var usedFallback = _fallbackTriggered;
                    StopCore();
                    StartCore(target, usedFallback);
                    Interlocked.Increment(ref _restartCount);
                }));

            _isRunning = true;
            RaiseStatus(new CaptureStatus(true, $"采集画面中断，已重建采集会话（第 {_restartCount} 次）。", IsWarning: true));
        }
        catch (ObjectDisposedException)
        {
            // 退出流程里采集线程已经收工。
        }
        catch (Exception exception)
        {
            _logger?.Error("重建采集会话失败。", exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 从图形捕获切到 GDI 抓屏，目标不变（窗口还是那个窗口、显示器还是那个显示器），
    /// 只是换一条完全不同的读取路径：图形捕获走 DWM 合成，抓屏走 PrintWindow / 屏幕位图。
    /// </summary>
    private async Task FallbackToGrabAsync(string reason)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            // 等待期间用户可能已经手动停止，这时不能再把采集重新开起来。
            if (_disposed || Volatile.Read(ref _captureActive) == 0)
            {
                return;
            }

            var target = _target;
            if (target is null || IsFallbackTriggered())
            {
                return;
            }

            await Task.Run(
                () => _captureThread.Invoke(() =>
                {
                    StopCore(logSummary: false);
                    StartCore(target, usedFallback: true);
                }));

            _isRunning = true;
            _grabPreferred = true;
            _logger?.Warning($"{reason}，已改用抓屏方式：{Describe(target)}。");
            RaiseStatus(new CaptureStatus(
                true,
                $"{reason}，已改用抓屏方式。注意：这种方式只能抓到屏幕上真实可见的画面，游戏窗口被挡住或最小化时会抓不到。",
                UsedFallback: true));
        }
        catch (ObjectDisposedException)
        {
            // 退出流程里采集线程已经收工。
        }
        catch (Exception exception)
        {
            _isRunning = false;
            _logger?.Error("抓屏回退失败。", exception);
            RaiseStatus(new CaptureStatus(false, "抓屏回退失败。", true, exception));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopAfterFailureAsync(string message)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _captureActive) == 0)
            {
                return;
            }

            await Task.Run(() => _captureThread.Invoke(() => StopCore()));
            _isRunning = false;
            RaiseStatus(new CaptureStatus(false, message, false, new InvalidOperationException(message)));
        }
        catch (ObjectDisposedException)
        {
            // 退出流程里采集线程已经收工。
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void PublishLatestFrame(object? state)
    {
        CapturedFrame? frame;
        long version;
        lock (_captureGate)
        {
            frame = _latestFrame;
            version = _frameVersion;
            if (frame is null || version == _lastPublishedFrameVersion)
            {
                return;
            }

            _lastPublishedFrameVersion = version;
        }

        var timestamp = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        FrameAvailable?.Invoke(this, frame with { Timestamp = timestamp });
    }


    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _isRunning = false;
        RaiseStatus(new CaptureStatus(false, "捕获目标已关闭。"));
    }

    private void StopCore(bool logSummary = true)
    {
        // 先关门：之后进入的帧回调会立刻返回，不会再开始新的帧处理。
        Volatile.Write(ref _captureActive, 0);

        GraphicsCaptureSession? session;
        Direct3D11CaptureFramePool? framePool;
        IDirect3DDevice? device;
        GraphicsCaptureItem? item;
        System.Threading.Timer? publishTimer;
        System.Threading.Timer? healthTimer;
        System.Threading.Timer? grabTimer;
        DesktopScreenGrabber? grabber;

        lock (_captureGate)
        {
            session = _session;
            framePool = _framePool;
            device = _device;
            item = _item;
            publishTimer = _publishTimer;
            healthTimer = _healthTimer;
            grabTimer = _grabTimer;
            grabber = _grabber;

            _session = null;
            _framePool = null;
            _device = null;
            _item = null;
            _publishTimer = null;
            _healthTimer = null;
            _grabTimer = null;
            _grabber = null;
            _latestFrame = null;
            _frameVersion = 0;
            _lastPublishedFrameVersion = 0;
            _uniformFrameCount = 0;
            _fallbackTriggered = false;
            _grabMode = false;
        }

        if (framePool is not null)
        {
            framePool.FrameArrived -= OnFrameArrived;
        }

        if (item is not null)
        {
            item.Closed -= OnItemClosed;
        }

        publishTimer?.Dispose();
        healthTimer?.Dispose();
        grabTimer?.Dispose();
        grabber?.Dispose();

        // 释放采集对象前先等在途帧回调退出。处理一帧只要几十毫秒，这里留 2 秒余量。
        var deadline = Environment.TickCount64 + 2000;
        while (Volatile.Read(ref _frameCallbacksInFlight) != 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(1);
        }

        var stuck = Volatile.Read(ref _frameCallbacksInFlight) != 0;

        if (logSummary && session is not null)
        {
            LogSessionSummary();
        }

        if (stuck)
        {
            // 回调卡在读取像素里（实测有机器会这样）。此时释放对象，等那个回调恢复过来就会撞上
            // 已经释放的内存，直接崩溃；宁可让这几个对象留着泄漏，也不能把进程带走。
            _logger?.Warning("有帧回调卡在读取像素中没有退出，本次不释放图形捕获对象（避免卡住的回调访问已释放内存）。");
            return;
        }

        session?.Dispose();
        framePool?.Dispose();
        device?.Dispose();
    }

    private void LogSessionSummary()
    {
        var frames = Interlocked.Read(ref _framesThisSession);
        var processed = Interlocked.Read(ref _processedFramesThisSession);
        var seconds = ElapsedSeconds(_sessionStartedTicks, Stopwatch.GetTimestamp());
        var mode = _grabMode ? "抓屏方式" : "图形捕获";

        if (processed == 0)
        {
            _logger?.Warning(
                frames > 0
                    ? $"采集结束（{mode}）：收到 {frames} 帧，但没有一帧能读出像素（持续 {seconds:0} 秒）。"
                    : $"采集结束（{mode}）：本次会话没有收到任何画面（持续 {seconds:0} 秒）。");
            return;
        }

        _logger?.Info($"采集结束（{mode}）：本次会话 {processed} 帧，平均 {(seconds <= 0.5 ? processed : processed / seconds):0.0} 帧/秒。");
    }

    private static IReadOnlyList<CaptureTargetDescriptor> EnumerateMonitors()
    {
        var handles = new List<nint>();
        EnumDisplayMonitors(nint.Zero, nint.Zero, (monitor, _, _, _) =>
        {
            handles.Add(monitor);
            return true;
        }, nint.Zero);

        return handles
            .Select(CreateMonitorDescriptor)
            .Where(target => target is not null)
            .Cast<CaptureTargetDescriptor>()
            .OrderBy(target => target.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static CaptureTargetDescriptor? CreateMonitorDescriptor(nint monitorHandle)
    {
        var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitorHandle, ref info))
        {
            return null;
        }

        var deviceName = info.szDevice.TrimEnd('\0');
        var bounds = new RectI(
            info.rcMonitor.Left,
            info.rcMonitor.Top,
            info.rcMonitor.Right - info.rcMonitor.Left,
            info.rcMonitor.Bottom - info.rcMonitor.Top);
        return new CaptureTargetDescriptor(
            $"display:{monitorHandle}",
            $"显示器 {deviceName} ({bounds.Width}x{bounds.Height})",
            CaptureTargetKind.Display,
            monitorHandle,
            bounds,
            MonitorDeviceName: deviceName);
    }

    private static IEnumerable<CaptureTargetDescriptor> EnumerateWindows(CancellationToken cancellationToken)
    {
        var ownProcessId = Environment.ProcessId;
        var handles = new List<nint>();
        EnumWindows((window, _) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindowVisible(window) || IsIconic(window))
            {
                return true;
            }

            var cloaked = 0;
            if (DwmGetWindowAttribute(window, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            var titleLength = GetWindowTextLength(window);
            if (titleLength <= 0)
            {
                return true;
            }

            // 工具窗口（没有 APPWINDOW 标记）不出现在 Alt+Tab 里，Windows 图形捕获会直接拒绝为它
            // 创建捕获项（E_INVALIDARG）。列出来只会让用户选中之后拿到一条看不懂的报错。
            var extendedStyle = GetWindowExtendedStyle(window);
            if ((extendedStyle & ExtendedStyleToolWindow) != 0 && (extendedStyle & ExtendedStyleAppWindow) == 0)
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var processId);
            if (processId == ownProcessId)
            {
                return true;
            }

            handles.Add(window);
            return true;
        }, nint.Zero);

        var results = new List<CaptureTargetDescriptor>();
        foreach (var window in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = GetWindowTitle(window);
            GetWindowThreadProcessId(window, out var processId);
            string? processName = null;
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                // 某些受保护进程无法读取名称，不影响窗口枚举。
            }

            if (!GetWindowRect(window, out var rect))
            {
                continue;
            }

            // 尺寸为 0 的窗口在采集时会被判为无效目标，干脆不列出来。
            if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0)
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(processName)
                ? title
                : $"{processName} - {title}";
            results.Add(new CaptureTargetDescriptor(
                $"window:{window}",
                label,
                CaptureTargetKind.Window,
                window,
                new RectI(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
                processName));
        }

        return results.OrderBy(target => target.DisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    private static string GetWindowTitle(nint window)
    {
        var length = Math.Max(1, GetWindowTextLength(window) + 1);
        var builder = new StringBuilder(length);
        _ = GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private static bool IsUniformBlack(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        var minimum = 255;
        var maximum = 0;
        var step = Math.Max(1, width / 64) * 4;
        for (var y = 0; y < height; y += Math.Max(1, height / 36))
        {
            var row = y * width * 4;
            for (var x = 0; x < width * 4; x += step)
            {
                var value = Math.Max(bgra[row + x], Math.Max(bgra[row + x + 1], bgra[row + x + 2]));
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
        }

        return maximum <= 3 && minimum <= 3;
    }

    private static unsafe byte[] CopyBitmap(SoftwareBitmap bitmap, out int width, out int height)
    {
        width = bitmap.PixelWidth;
        height = bitmap.PixelHeight;
        var destination = new byte[width * height * 4];
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        var inspectable = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
        nint accessPointer = nint.Zero;
        byte* source;
        try
        {
            var accessIid = MemoryBufferByteAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, ref accessIid, out accessPointer));
            var vtable = Marshal.ReadIntPtr(accessPointer, 0);
            var getBufferPointer = Marshal.ReadIntPtr(vtable, 3 * nint.Size);
            var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferDelegate>(getBufferPointer);
            Marshal.ThrowExceptionForHR(getBuffer(accessPointer, out source, out _));
        }
        finally
        {
            if (accessPointer != nint.Zero)
            {
                Marshal.Release(accessPointer);
            }

            // 这里不能释放 inspectable：它只是 RCW 借出来的指针（NativeObject.ThisPtr 不做 AddRef），
            // 那个引用由 RCW 自己在 Dispose/终结时释放。多释放一次会让对象提前销毁，
            // 之后终结器就会去 Release 一块已经回收的内存，表现为随机的 0xc0000005 闪退。
        }
        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy((nint)(source + plane.StartIndex + (y * plane.Stride)), destination, y * rowBytes, rowBytes);
        }

        return destination;
    }

    private static string Describe(CaptureTargetDescriptor target) =>
        $"{target.DisplayName}（{(target.Kind == CaptureTargetKind.Window ? "窗口" : "显示器")}，句柄 0x{target.NativeHandle:X}）";

    /// <summary>窗口侧的可诊断状态：已关闭 / 已最小化 / 不可见都会让窗口采集拿不到画面。</summary>
    private static string DescribeWindowState(CaptureTargetDescriptor target)
    {
        if (target.Kind != CaptureTargetKind.Window)
        {
            return string.Empty;
        }

        if (!IsWindow(target.NativeHandle))
        {
            return "（该窗口已关闭）";
        }

        if (IsIconic(target.NativeHandle))
        {
            return "（该窗口当前已最小化，最小化的窗口采集不到画面）";
        }

        return IsWindowVisible(target.NativeHandle) ? string.Empty : "（该窗口当前不可见）";
    }

    private Exception DescribeStartFailure(CaptureTargetDescriptor target, Exception exception)
    {
        // E_INVALIDARG（.NET 侧是 "Value does not fall within the expected range."）：系统拒绝为这个
        // 窗口创建捕获项。窗口不在 Alt+Tab 列表里、游戏独占全屏、窗口刚被重建都会这样，
        // 所以给一条能照着做的提示，而不是把系统异常原样抛给用户。
        if (target.Kind == CaptureTargetKind.Window && exception is ArgumentException)
        {
            return new InvalidOperationException(
                $"无法采集窗口「{target.DisplayName}」{DescribeWindowState(target)}：系统拒绝为它创建捕获项。" +
                "常见原因：窗口已关闭或刚重建、窗口不在 Alt+Tab 列表中，或游戏正以独占全屏运行。" +
                "请点“立即刷新”重新选择，或改选所在显示器。",
                exception);
        }

        return exception;
    }

    private bool IsFallbackTriggered()
    {
        lock (_captureGate)
        {
            return _fallbackTriggered;
        }
    }

    private void RaiseStatus(CaptureStatus status)
    {
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception exception)
        {
            _logger?.Warning($"采集状态回调失败：{exception.Message}");
        }
    }

    private void RunInBackground(Func<Task> operation)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await operation();
            }
            catch (ObjectDisposedException)
            {
                // 退出流程里采集线程已经收工。
            }
            catch (Exception exception)
            {
                _logger?.Error("后台采集任务失败。", exception);
            }
        });
    }

    private static long ElapsedMilliseconds(long startTicks, long nowTicks) =>
        startTicks == 0 ? 0 : (long)((nowTicks - startTicks) * 1000.0 / Stopwatch.Frequency);

    private static double ElapsedSeconds(long startTicks, long nowTicks) =>
        startTicks == 0 ? 0 : (nowTicks - startTicks) / (double)Stopwatch.Frequency;

    private static readonly Guid MemoryBufferByteAccessIid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int GetBufferDelegate(nint instance, out byte* buffer, out uint capacity);

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);
    private delegate bool EnumWindowsProc(nint window, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    private static nint GetWindowExtendedStyle(nint window) =>
        nint.Size == 8 ? GetWindowLongPtr(window, WindowStyleExtendedIndex) : GetWindowLong(window, WindowStyleExtendedIndex);
}
