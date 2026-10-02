using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace KanamiReporter.CaptureDiag;

internal sealed record CaptureAttempt(
    string Method,
    string Outcome,
    bool HasPicture,
    bool IsMoving,
    string Metrics,
    string? Snapshot,
    string? Notes);

/// <summary>
/// 用多种互不相同的机制去采集同一个目标，看哪一种能真正拿到画面。
/// 这些方式覆盖的失败面并不一样：Windows 图形捕获走 DWM 合成，GDI 走屏幕 DC / 窗口 DC，
/// 而"换显卡适配器""换像素格式"分别对应多显卡机器与 HDR 这两种典型故障。
/// </summary>
internal static class CaptureMethods
{
    private const int FirstFrameTimeoutMilliseconds = 5000;
    private const int CopyIntervalMilliseconds = 100;

    public static IReadOnlyList<CaptureAttempt> RunAll(
        DiagTarget target,
        IReadOnlyList<DxgiAdapterInfo> adapters,
        int sampleSeconds,
        string snapshotDirectory,
        DiagLog log)
    {
        var attempts = new List<CaptureAttempt>();
        var plans = BuildPlans(target, adapters, sampleSeconds);

        for (var index = 0; index < plans.Count; index++)
        {
            var (name, run) = plans[index];
            log.Line($"[{index + 1}/{plans.Count}] {name} …");

            CaptureAttempt attempt;
            try
            {
                var result = run();
                var snapshot = result.SnapshotBgra is null
                    ? null
                    : SaveSnapshot(snapshotDirectory, index + 1, name, result.SnapshotBgra, result.Width, result.Height);

                var notes = result.Notes.Count == 0 ? null : string.Join("；", result.Notes);
                attempt = new CaptureAttempt(name, result.Outcome, result.HasPicture, result.IsMoving, result.Metrics, snapshot, notes);
            }
            catch (Exception exception)
            {
                attempt = new CaptureAttempt(name, "失败", false, false, string.Empty, null, Describe(exception));
            }

            log.Line(
                $"      → {attempt.Outcome}" +
                $"{(attempt.Metrics.Length == 0 ? string.Empty : $"：{attempt.Metrics}")}" +
                $"{(attempt.Notes is null ? string.Empty : $"（{attempt.Notes}）")}");
            if (attempt.Snapshot is not null)
            {
                log.Line($"      快照：{Path.GetFileName(attempt.Snapshot)}");
            }

            attempts.Add(attempt);
        }

        return attempts;
    }

    private static List<(string Name, Func<MethodResult> Run)> BuildPlans(
        DiagTarget target,
        IReadOnlyList<DxgiAdapterInfo> adapters,
        int sampleSeconds)
    {
        var isWindow = target.Kind == DiagTargetKind.Window;
        var kind = isWindow ? "窗口" : "显示器";
        var plans = new List<(string Name, Func<MethodResult> Run)>
        {
            ($"Windows 图形捕获（{kind}，默认显卡，BGRA8）",
                () => RunWgc(target.Kind, target.Handle, adapterIndex: -1, hdr: false, sampleSeconds))
        };

        if (adapters.Count > 1)
        {
            // 同一块显卡可能被枚举出多次，微软的软件适配器（厂商 0x1414，例如 Microsoft Basic Render
            // Driver）测了也没有意义，都跳过。
            var tested = new HashSet<(uint Vendor, uint Device)>();
            foreach (var adapter in adapters)
            {
                if (adapter.VendorId == 0x1414 || !tested.Add((adapter.VendorId, adapter.DeviceId)))
                {
                    continue;
                }

                var index = adapter.Index;
                plans.Add((
                    $"Windows 图形捕获（{kind}，适配器 #{index} {adapter.Name}）",
                    () => RunWgc(target.Kind, target.Handle, index, hdr: false, sampleSeconds)));
            }
        }

        plans.Add((
            $"Windows 图形捕获（{kind}，HDR 浮点像素格式）",
            () => RunWgc(target.Kind, target.Handle, adapterIndex: -1, hdr: true, sampleSeconds)));

        if (isWindow)
        {
            var monitor = MonitorFromWindow(target.Handle, 2);
            if (monitor != nint.Zero)
            {
                plans.Add((
                    "Windows 图形捕获（窗口所在显示器，主程序的黑屏回退路径）",
                    () => RunWgc(DiagTargetKind.Display, monitor, adapterIndex: -1, hdr: false, sampleSeconds)));

                plans.Add(($"GDI 桌面 BitBlt（窗口屏幕区域）", () => RunDesktopBitBlt(target)));
            }

            plans.Add(("GDI 窗口 DC BitBlt", () => RunWindowBitBlt(target)));
            plans.Add(("GDI PrintWindow（PW_RENDERFULLCONTENT）", () => RunPrintWindow(target)));
        }
        else
        {
            plans.Add(("GDI 桌面 BitBlt（该显示器区域）", () => RunDesktopBitBlt(target)));
        }

        return plans;
    }

    private static MethodResult RunWgc(DiagTargetKind kind, nint handle, int adapterIndex, bool hdr, int sampleSeconds)
    {
        GraphicsCaptureItem? item = null;
        IDirect3DDevice? device = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        nint adapterPointer = nint.Zero;
        var notes = new List<string>();

        try
        {
            if (!GraphicsCaptureSession.IsSupported())
            {
                return MethodResult.Failed("系统不支持 Windows 图形捕获（需要 Windows 10 2004 / 内部版本 19041 或更高）");
            }

            item = kind == DiagTargetKind.Window ? CreateItemForWindow(handle) : CreateItemForMonitor(handle);
            if (item.Size.Width <= 0 || item.Size.Height <= 0)
            {
                return MethodResult.Failed($"捕获目标尺寸为 0（{item.Size.Width}x{item.Size.Height}）");
            }

            if (adapterIndex >= 0)
            {
                adapterPointer = Dxgi.GetAdapterPointer(adapterIndex);
                if (adapterPointer == nint.Zero)
                {
                    return MethodResult.Failed($"取不到适配器 #{adapterIndex}");
                }
            }

            device = CreateDevice(adapterPointer, notes);
            var format = hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
            var sampler = new FrameSampler(hdr);

            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, format, 2, item.Size);
            pool.FrameArrived += (sender, _) => sampler.OnFrame(sender);

            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            session.StartCapture();

            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < FirstFrameTimeoutMilliseconds + (sampleSeconds * 1000))
            {
                Thread.Sleep(20);
                var first = sampler.FirstArrivalMilliseconds;
                if (first >= 0 && clock.ElapsedMilliseconds - first >= sampleSeconds * 1000)
                {
                    break;
                }
            }

            return sampler.Build(item.Size.Width, item.Size.Height, notes);
        }
        catch (Exception exception)
        {
            notes.Add(Describe(exception));
            return MethodResult.Failed(notes);
        }
        finally
        {
            try
            {
                session?.Dispose();
                pool?.Dispose();
                device?.Dispose();
            }
            catch (Exception)
            {
                // 释放期的报错不影响本次结果。
            }

            if (adapterPointer != nint.Zero)
            {
                Marshal.Release(adapterPointer);
            }
        }
    }

    private static MethodResult RunDesktopBitBlt(DiagTarget target)
    {
        var (x, y, width, height) = target.Kind == DiagTargetKind.Display
            ? TargetDiscovery.GetMonitorBounds(target.Handle)
            : TargetDiscovery.GetWindowScreenBounds(target.Handle);

        if (width <= 0 || height <= 0)
        {
            return MethodResult.Failed("目标区域无效");
        }

        return GrabTwice(memoryDc => BitBltScreen(memoryDc, x, y, width, height), width, height, "桌面 BitBlt");
    }

    private static MethodResult RunWindowBitBlt(DiagTarget target)
    {
        var (_, _, width, height) = TargetDiscovery.GetWindowScreenBounds(target.Handle);
        if (width <= 0 || height <= 0)
        {
            return MethodResult.Failed("窗口区域无效");
        }

        return GrabTwice(
            memoryDc =>
            {
                var windowDc = GetWindowDC(target.Handle);
                if (windowDc == nint.Zero)
                {
                    return false;
                }

                try
                {
                    return BitBlt(memoryDc, 0, 0, width, height, windowDc, 0, 0, SourceCopy | CaptureBlt);
                }
                finally
                {
                    _ = ReleaseDC(target.Handle, windowDc);
                }
            },
            width,
            height,
            "窗口 DC BitBlt");
    }

    private static MethodResult RunPrintWindow(DiagTarget target)
    {
        var (_, _, width, height) = TargetDiscovery.GetWindowScreenBounds(target.Handle);
        if (width <= 0 || height <= 0)
        {
            return MethodResult.Failed("窗口区域无效");
        }

        return GrabTwice(
            memoryDc => PrintWindow(target.Handle, memoryDc, PrintWindowRenderFullContent),
            width,
            height,
            "PrintWindow");
    }

    /// <summary>抓两次（间隔 500 毫秒）：一次拿到画面内容，两次对比得出画面是否在变化。</summary>
    private static MethodResult GrabTwice(Func<nint, bool> grab, int width, int height, string label)
    {
        var clock = Stopwatch.StartNew();
        using var dib = DibSection.Create(width, height);
        if (dib.Bits == nint.Zero)
        {
            return MethodResult.Failed("创建 DIB 位图失败");
        }

        if (!grab(dib.MemoryDc))
        {
            return MethodResult.Failed($"{label} 第一次调用失败（Win32 错误码 {Marshal.GetLastWin32Error()}）");
        }

        var first = dib.ReadPixels();
        var firstMilliseconds = clock.ElapsedMilliseconds;
        Thread.Sleep(500);

        if (!grab(dib.MemoryDc))
        {
            return MethodResult.Failed($"{label} 第二次调用失败（Win32 错误码 {Marshal.GetLastWin32Error()}）");
        }

        var second = dib.ReadPixels();
        var stats = FrameAnalysis.Analyze(second, width, height, width * 4, 4, floatingPoint: false);
        var difference = FrameAnalysis.Difference(first, second);
        var moving = difference >= 0.5;
        var notes = new List<string>();
        if (!moving)
        {
            notes.Add("两次抓取内容完全一致（画面是静止的，或抓到的是一张冻结图像）");
        }

        return new MethodResult(
            stats.MaxComponent <= 3 ? "全黑" : moving ? "有效画面" : "有画面但无变化",
            stats.MaxComponent > 3,
            moving,
            $"抓取耗时 {firstMilliseconds} 毫秒，尺寸 {width}x{height}，平均亮度 {stats.AverageLuminance:0.0}，" +
            $"非黑像素 {stats.NonBlackRatio * 100:0.#}%，两帧差异 {difference:0.00}",
            second,
            width,
            height,
            notes);
    }

    private static bool BitBltScreen(nint memoryDc, int x, int y, int width, int height)
    {
        var screenDc = GetDC(nint.Zero);
        if (screenDc == nint.Zero)
        {
            return false;
        }

        try
        {
            return BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SourceCopy | CaptureBlt);
        }
        finally
        {
            _ = ReleaseDC(nint.Zero, screenDc);
        }
    }

    private static IDirect3DDevice CreateDevice(nint adapter, List<string> notes)
    {
        int[] featureLevels = [0xB100, 0xB000, 0xA100, 0xA000];
        var driverType = adapter == nint.Zero ? 1 : 0;   // 指定适配器时必须用 D3D_DRIVER_TYPE_UNKNOWN
        var hr = D3D11CreateDevice(adapter, driverType, nint.Zero, 0x20, featureLevels, featureLevels.Length, 7, out var device, out var featureLevel, out var context);
        if (hr < 0 && adapter != nint.Zero)
        {
            notes.Add($"在指定适配器上创建设备失败（0x{hr:X8}）");
        }

        if (hr < 0)
        {
            hr = D3D11CreateDevice(nint.Zero, 3, nint.Zero, 0x20, featureLevels, featureLevels.Length, 7, out device, out featureLevel, out context);
            if (hr >= 0)
            {
                notes.Add("硬件设备创建失败，退回 WARP 软件设备（软件设备通常收不到画面）");
            }
        }

        Marshal.ThrowExceptionForHR(hr);
        notes.Add($"特性级别 0x{featureLevel:X}");

        nint dxgiDevice = nint.Zero;
        nint inspectable = nint.Zero;
        try
        {
            var iid = DxgiDeviceIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, ref iid, out dxgiDevice));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable));
            return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != nint.Zero)
            {
                Marshal.Release(inspectable);
            }

            if (dxgiDevice != nint.Zero)
            {
                Marshal.Release(dxgiDevice);
            }

            if (context != nint.Zero)
            {
                Marshal.Release(context);
            }

            if (device != nint.Zero)
            {
                Marshal.Release(device);
            }
        }
    }

    private static string SaveSnapshot(string directory, int index, string method, byte[] bgra, int width, int height)
    {
        Directory.CreateDirectory(directory);
        var safeName = new string(method.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
        var path = Path.Combine(directory, $"snapshot-{index:00}-{safeName}.png");
        PngWriter.Write(path, bgra, width, height);
        return path;
    }

    private static string Describe(Exception exception) =>
        exception is ArgumentException
            ? $"{exception.GetType().Name}：{exception.Message}（E_INVALIDARG，通常说明这个目标不可捕获：" +
              "已关闭、不在 Alt+Tab 列表里，或游戏正以独占全屏运行）"
            : $"{exception.GetType().Name}：{exception.Message}";

    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private sealed record MethodResult(
        string Outcome,
        bool HasPicture,
        bool IsMoving,
        string Metrics,
        byte[]? SnapshotBgra,
        int Width,
        int Height,
        IReadOnlyList<string> Notes)
    {
        public static MethodResult Failed(string reason) => Failed([reason]);

        public static MethodResult Failed(IReadOnlyList<string> notes) =>
            new("失败", false, false, string.Empty, null, 0, 0, notes);
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint window) =>
        CreateItem((IGraphicsCaptureItemInterop interop, ref Guid iid) => interop.CreateForWindow(window, ref iid));

    private static GraphicsCaptureItem CreateItemForMonitor(nint monitor) =>
        CreateItem((IGraphicsCaptureItemInterop interop, ref Guid iid) => interop.CreateForMonitor(monitor, ref iid));

    private static GraphicsCaptureItem CreateItem(CreateItemDelegate factory)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var classHandle));
        nint factoryPointer = nint.Zero;
        nint itemPointer = nint.Zero;
        try
        {
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classHandle, ref interopIid, out factoryPointer));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            var itemIid = GraphicsCaptureItemIid;
            itemPointer = factory(interop, ref itemIid);
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            if (itemPointer != nint.Zero)
            {
                Marshal.Release(itemPointer);
            }

            if (factoryPointer != nint.Zero)
            {
                Marshal.Release(factoryPointer);
            }

            _ = WindowsDeleteString(classHandle);
        }
    }

    private delegate nint CreateItemDelegate(IGraphicsCaptureItemInterop interop, ref Guid iid);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    /// <summary>帧回调里统计：到达次数、首帧耗时，并按 10 次/秒限速保存像素用于分析和存快照。</summary>
    private sealed class FrameSampler
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _bytesPerPixel;
        private readonly bool _floating;
        private long _arrivals;
        private long _firstArrivalMilliseconds = -1;
        private long _lastCopyMilliseconds = -1000;
        private int _copyInFlight;
        private byte[]? _first;
        private byte[]? _last;
        private int _stride;
        private int _width;
        private int _height;
        private string? _actualPixelFormat;
        private string? _copyError;

        /// <summary>像素格式按请求的格式算：请求 HDR 就是用 R16G16B16A16Float 读取。</summary>
        public FrameSampler(bool floating)
        {
            _floating = floating;
            _bytesPerPixel = floating ? 8 : 4;
        }

        public long Arrivals => Interlocked.Read(ref _arrivals);

        public long FirstArrivalMilliseconds => Interlocked.Read(ref _firstArrivalMilliseconds);

        public void OnFrame(Direct3D11CaptureFramePool pool)
        {
            Direct3D11CaptureFrame? frame;
            try
            {
                frame = pool.TryGetNextFrame();
            }
            catch (Exception exception)
            {
                RecordCopyError($"取帧失败：{exception.Message}");
                return;
            }

            if (frame is null)
            {
                return;
            }

            using (frame)
            {
                var arrival = Interlocked.Increment(ref _arrivals);
                if (arrival == 1)
                {
                    Interlocked.Exchange(ref _firstArrivalMilliseconds, _clock.ElapsedMilliseconds);
                }

                var now = _clock.ElapsedMilliseconds;
                if (now - Interlocked.Read(ref _lastCopyMilliseconds) < CopyIntervalMilliseconds)
                {
                    return;
                }

                Interlocked.Exchange(ref _lastCopyMilliseconds, now);
                Interlocked.Exchange(ref _copyInFlight, 1);
                try
                {
                    using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().GetAwaiter().GetResult();
                    var (buffer, stride) = CopyBitmap(bitmap);
                    lock (_gate)
                    {
                        _stride = stride;
                        _actualPixelFormat = bitmap.BitmapPixelFormat.ToString();
                        _width = bitmap.PixelWidth;
                        _height = bitmap.PixelHeight;
                        _first ??= buffer;
                        _last = buffer;
                    }
                }
                catch (Exception exception)
                {
                    RecordCopyError($"复制帧失败：{exception.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _copyInFlight, 0);
                }
            }
        }

        public MethodResult Build(int fallbackWidth, int fallbackHeight, List<string> notes)
        {
            byte[]? first;
            byte[]? last;
            int stride;
            int width;
            int height;
            string? actualPixelFormat;
            string? copyError;
            lock (_gate)
            {
                first = _first;
                last = _last;
                stride = _stride;
                width = _width > 0 ? _width : fallbackWidth;
                height = _height > 0 ? _height : fallbackHeight;
                actualPixelFormat = _actualPixelFormat;
                copyError = _copyError;
            }

            var arrivals = Arrivals;
            if (arrivals == 0)
            {
                notes.Add($"首帧超时：{FirstFrameTimeoutMilliseconds / 1000} 秒内系统没有送来任何一帧");
                return MethodResult.Failed(notes);
            }

            notes.Add($"{arrivals} 帧");
            if (actualPixelFormat is not null)
            {
                notes.Add($"系统实际给出的像素格式 {actualPixelFormat}");
            }

            if (last is null || stride == 0)
            {
                // 关键的一条：帧能收到、像素却读不出来，主程序就会一直是一片黑。
                // 只在"一帧都没读出来"时报，避免收尾时正好有一次读取在跑就误报。
                if (Volatile.Read(ref _copyInFlight) != 0)
                {
                    notes.Add("像素读取卡住：帧回调读像素时没有返回，这些帧一帧都用不上");
                }

                if (copyError is not null)
                {
                    notes.Add(copyError);
                }

                return MethodResult.Failed(notes);
            }

            var stats = FrameAnalysis.Analyze(last, width, height, stride, _bytesPerPixel, _floating);
            var lastBgra = FrameAnalysis.ToBgra8(last, width, height, stride, _bytesPerPixel, _floating);
            var firstBgra = first is null ? null : FrameAnalysis.ToBgra8(first, width, height, stride, _bytesPerPixel, _floating);
            var difference = firstBgra is null ? 0 : FrameAnalysis.Difference(firstBgra, lastBgra);
            var moving = difference >= 0.5;
            var black = stats.MaxComponent <= 3;
            var metrics =
                $"首帧 {FirstArrivalMilliseconds} 毫秒，{arrivals} 帧，尺寸 {width}x{height}，" +
                $"平均亮度 {stats.AverageLuminance:0.0}，非黑像素 {stats.NonBlackRatio * 100:0.#}%，两帧差异 {difference:0.00}";

            if (copyError is not null)
            {
                notes.Add(copyError);
            }

            if (!moving && !black)
            {
                notes.Add("画面没有变化（可能是冻结帧，也可能只是画面本身静止）");
            }

            return new MethodResult(
                black ? "全黑" : moving ? "有效画面" : "有画面但无变化",
                !black,
                moving,
                metrics,
                lastBgra,
                width,
                height,
                notes);
        }

        private void RecordCopyError(string message)
        {
            lock (_gate)
            {
                _copyError ??= message;
            }
        }

        private static unsafe (byte[] Buffer, int Stride) CopyBitmap(SoftwareBitmap bitmap)
        {
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
            }

            var stride = plane.Stride;
            var destination = new byte[stride * bitmap.PixelHeight];
            Marshal.Copy((nint)(source + plane.StartIndex), destination, 0, destination.Length);
            return (destination, stride);
        }
    }

    /// <summary>一块可以直接读取像素的 32 位自上而下 DIB 内存位图。</summary>
    private sealed class DibSection : IDisposable
    {
        private nint _bitmap;
        private nint _previousBitmap;
        private nint _windowDc;

        private DibSection(nint windowDc, nint memoryDc, nint bitmap, nint previousBitmap, nint bits, int width, int height)
        {
            _windowDc = windowDc;
            _bitmap = bitmap;
            _previousBitmap = previousBitmap;
            MemoryDc = memoryDc;
            Bits = bits;
            Width = width;
            Height = height;
        }

        public nint MemoryDc { get; }

        public nint Bits { get; }

        public int Width { get; }

        public int Height { get; }

        public static DibSection Create(int width, int height)
        {
            var windowDc = GetDC(nint.Zero);
            var memoryDc = CreateCompatibleDC(windowDc);
            var info = new BitmapInfo
            {
                header = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = width,
                    biHeight = -height,   // 负高度 = 自上而下，像素顺序与 BGRA 一致
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            var bitmap = CreateDIBSection(memoryDc, ref info, 0, out var bits, nint.Zero, 0);
            var previous = SelectObject(memoryDc, bitmap);
            return new DibSection(windowDc, memoryDc, bitmap, previous, bits, width, height);
        }

        public byte[] ReadPixels()
        {
            var buffer = new byte[Width * Height * 4];
            if (Bits != nint.Zero)
            {
                Marshal.Copy(Bits, buffer, 0, buffer.Length);
            }

            return buffer;
        }

        public void Dispose()
        {
            if (_bitmap != nint.Zero)
            {
                _ = SelectObject(MemoryDc, _previousBitmap);
                _ = DeleteObject(_bitmap);
                _bitmap = nint.Zero;
            }

            if (MemoryDc != nint.Zero)
            {
                _ = DeleteDC(MemoryDc);
            }

            if (_windowDc != nint.Zero)
            {
                _ = ReleaseDC(nint.Zero, _windowDc);
                _windowDc = nint.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader header;
        public uint colors;
    }

    private static readonly Guid MemoryBufferByteAccessIid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int GetBufferDelegate(nint instance, out byte* buffer, out uint capacity);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        int[] featureLevels,
        int featureLevelCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint value);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);
}
