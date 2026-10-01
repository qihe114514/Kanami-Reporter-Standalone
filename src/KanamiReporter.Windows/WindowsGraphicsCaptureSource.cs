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

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _captureGate = new();
    private readonly object _framePoolGate = new();
    private CaptureTargetDescriptor? _target;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private System.Threading.Timer? _publishTimer;
    private CapturedFrame? _latestFrame;
    private IDirect3DDevice? _device;
    private long _lastFrameTimestamp;
    private long _frameVersion;
    private long _lastPublishedFrameVersion;
    private int _uniformFrameCount;
    private int _processingFrame;
    private int _frameCallbacksInFlight;
    private int _captureActive;
    private bool _fallbackTriggered;
    private bool _disposed;
    private bool _isRunning;

    public event EventHandler<CapturedFrame>? FrameAvailable;
    public event EventHandler<CaptureStatus>? StatusChanged;

    public bool IsRunning => _isRunning;

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
            StopCore();
            StartCore(target, usedFallback: false);
            _isRunning = true;
            StatusChanged?.Invoke(this, new CaptureStatus(true, $"正在捕获：{target.DisplayName}"));
        }
        catch (Exception exception)
        {
            StopCore();
            _isRunning = false;
            StatusChanged?.Invoke(this, new CaptureStatus(false, "采集启动失败。", false, exception));
            throw;
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
            StopCore();
            _isRunning = false;
            StatusChanged?.Invoke(this, new CaptureStatus(false, "采集已停止。"));
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
        _lifecycleGate.Dispose();
    }

    private void StartCore(CaptureTargetDescriptor target, bool usedFallback)
    {
        lock (_captureGate)
        {
            _target = target;
            _lastFrameTimestamp = 0;
            _frameVersion = 0;
            _lastPublishedFrameVersion = 0;
            _uniformFrameCount = 0;
            _fallbackTriggered = usedFallback;

            _item = target.Kind == CaptureTargetKind.Window
                ? GraphicsCaptureInterop.CreateForWindow(target.NativeHandle)
                : GraphicsCaptureInterop.CreateForMonitor(target.NativeHandle);

            if (_item.Size.Width <= 0 || _item.Size.Height <= 0)
            {
                throw new InvalidOperationException("捕获目标尺寸无效。");
            }

            _device = GraphicsCaptureInterop.CreateDirect3DDevice();
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
        }

        if (usedFallback)
        {
            StatusChanged?.Invoke(this, new CaptureStatus(true, $"窗口采集不可用，已回退到显示器：{target.DisplayName}", true));
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // 帧回调跑在采集线程上，StopCore 会等这里退出后才释放帧池和设备。
        // 必须先登记再检查标志，否则登记之前 StopCore 可能已经等完并开始释放。
        Interlocked.Increment(ref _frameCallbacksInFlight);
        try
        {
            if (Volatile.Read(ref _captureActive) == 0)
            {
                return;
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
                        if (_uniformFrameCount >= 20)
                        {
                            _ = Task.Run(FallbackToMonitorAsync);
                        }
                    }
                    else
                    {
                        _uniformFrameCount = 0;
                    }

                    var normalized = FrameProcessing.Normalize(pixels, width, height);
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
            StatusChanged?.Invoke(this, new CaptureStatus(_isRunning, "捕获帧处理失败。", _fallbackTriggered, exception));
        }
    }

    private async Task FallbackToMonitorAsync()
    {
        CaptureTargetDescriptor? displayTarget = null;
        lock (_captureGate)
        {
            if (_fallbackTriggered || _target is null || _target.Kind != CaptureTargetKind.Window ||
                Volatile.Read(ref _captureActive) == 0)
            {
                return;
            }

            _fallbackTriggered = true;
            var monitorHandle = MonitorFromWindow(_target.NativeHandle, MonitorDefaultToNearest);
            if (monitorHandle != nint.Zero)
            {
                displayTarget = CreateMonitorDescriptor(monitorHandle);
            }
        }

        if (displayTarget is null)
        {
            StatusChanged?.Invoke(this, new CaptureStatus(false, "窗口采集返回黑屏，且无法定位所在显示器。"));
            return;
        }

        await _lifecycleGate.WaitAsync();
        try
        {
            // 等待期间用户可能已经手动停止，这时不能再把采集重新开起来。
            if (Volatile.Read(ref _captureActive) == 0)
            {
                return;
            }

            StopCore();
            StartCore(displayTarget, usedFallback: true);
            _isRunning = true;
        }
        catch (Exception exception)
        {
            _isRunning = false;
            StatusChanged?.Invoke(this, new CaptureStatus(false, "显示器回退失败。", true, exception));
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
        StatusChanged?.Invoke(this, new CaptureStatus(false, "捕获目标已关闭。"));
    }

    private void StopCore()
    {
        // 先关门：之后进入的帧回调会立刻返回，不会再开始新的帧处理。
        Volatile.Write(ref _captureActive, 0);

        GraphicsCaptureSession? session;
        Direct3D11CaptureFramePool? framePool;
        IDirect3DDevice? device;
        GraphicsCaptureItem? item;
        System.Threading.Timer? publishTimer;

        lock (_captureGate)
        {
            session = _session;
            framePool = _framePool;
            device = _device;
            item = _item;
            publishTimer = _publishTimer;

            _session = null;
            _framePool = null;
            _device = null;
            _item = null;
            _publishTimer = null;
            _latestFrame = null;
            _frameVersion = 0;
            _lastPublishedFrameVersion = 0;
            _uniformFrameCount = 0;
            _fallbackTriggered = false;
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

        // 释放采集对象前先等在途帧回调退出。处理一帧只要几十毫秒，这里留 2 秒余量；
        // 超时说明回调已被卡住，此时继续释放只是退回到修复前的行为，不会更糟。
        var deadline = Environment.TickCount64 + 2000;
        while (Volatile.Read(ref _frameCallbacksInFlight) != 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(1);
        }

        session?.Dispose();
        framePool?.Dispose();
        device?.Dispose();
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}




