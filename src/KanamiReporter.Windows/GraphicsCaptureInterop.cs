using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace KanamiReporter.Windows;

internal static class GraphicsCaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid DxgiAdapterIid = new("2411E7E1-12AC-4CCF-BD14-9798E8534DC0");

    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeSoftware = 3;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const int WindowCreateRetryDelayMilliseconds = 250;
    private const int Bgra8FeatureLevel = 0xB100;
    private const int FeatureLevel11 = 0xB000;
    private const int FeatureLevel10_1 = 0xA100;
    private const int FeatureLevel10 = 0xA000;

    public static GraphicsCaptureItem CreateForWindow(nint windowHandle)
    {
        try
        {
            return CreateItemForWindow(windowHandle);
        }
        catch (ArgumentException) when (IsWindow(windowHandle))
        {
            // 窗口还在，但系统此刻拒绝为它创建捕获项（E_INVALIDARG）。常见于窗口刚被还原
            // 或刚重建的瞬间，等一拍再试一次；窗口确实已经消失时不做无谓重试。
            Thread.Sleep(WindowCreateRetryDelayMilliseconds);
            return CreateItemForWindow(windowHandle);
        }
    }

    public static GraphicsCaptureItem CreateForMonitor(nint monitorHandle) =>
        CreateItem((IGraphicsCaptureItemInterop interop, ref Guid iid) => interop.CreateForMonitor(monitorHandle, ref iid));

    /// <summary>
    /// 创建采集用的 D3D11 设备。<paramref name="description"/> 用于日志：适配器、特性级别，
    /// 以及是否退到了 WARP 软件设备——软件设备上 Windows 图形捕获很可能收不到画面，
    /// 所以这种情况必须能事后看出来，而不是悄悄继续。
    /// </summary>
    public static IDirect3DDevice CreateDirect3DDevice(out string description, out bool isSoftware)
    {
        var withFeatureLevel11_1 = new[] { Bgra8FeatureLevel, FeatureLevel11, FeatureLevel10_1, FeatureLevel10 };
        var withoutFeatureLevel11_1 = new[] { FeatureLevel11, FeatureLevel10_1, FeatureLevel10 };

        isSoftware = false;
        var hr = D3D11CreateDevice(
            nint.Zero,
            D3DDriverTypeHardware,
            nint.Zero,
            D3D11CreateDeviceBgraSupport,
            withFeatureLevel11_1,
            withFeatureLevel11_1.Length,
            7,
            out var device,
            out var featureLevel,
            out var context);

        if (hr < 0)
        {
            // 11_1 在个别系统/驱动上会让 D3D11CreateDevice 直接失败，去掉它再试一次硬件设备。
            hr = D3D11CreateDevice(
                nint.Zero,
                D3DDriverTypeHardware,
                nint.Zero,
                D3D11CreateDeviceBgraSupport,
                withoutFeatureLevel11_1,
                withoutFeatureLevel11_1.Length,
                7,
                out device,
                out featureLevel,
                out context);
        }

        var hardwareFailure = hr;
        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                nint.Zero,
                D3DDriverTypeSoftware,
                nint.Zero,
                D3D11CreateDeviceBgraSupport,
                withFeatureLevel11_1,
                withFeatureLevel11_1.Length,
                7,
                out device,
                out featureLevel,
                out context);
            isSoftware = true;
        }

        Marshal.ThrowExceptionForHR(hr);

        var prefix = isSoftware ? $"WARP 软件设备（硬件设备创建失败 0x{hardwareFailure:X8}） " : string.Empty;
        description = $"{prefix}{DescribeAdapter(device)}，特性级别 {DescribeFeatureLevel(featureLevel)}";

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

    private static GraphicsCaptureItem CreateItemForWindow(nint windowHandle) =>
        CreateItem((IGraphicsCaptureItemInterop interop, ref Guid iid) => interop.CreateForWindow(windowHandle, ref iid));

    private static string DescribeAdapter(nint device)
    {
        object? deviceObject = null;
        object? adapterObject = null;
        nint adapter = nint.Zero;
        try
        {
            deviceObject = Marshal.GetObjectForIUnknown(device);
            var dxgiDevice = (IDxgiDevice)deviceObject;
            Marshal.ThrowExceptionForHR(dxgiDevice.GetAdapter(out adapter));
            if (adapter == nint.Zero)
            {
                return "未知适配器";
            }

            adapterObject = Marshal.GetObjectForIUnknown(adapter);
            var dxgiAdapter = (IDxgiAdapter)adapterObject;
            Marshal.ThrowExceptionForHR(dxgiAdapter.GetDesc(out var info));

            var name = info.Description?.Trim() ?? string.Empty;
            var identity = $"{info.VendorId:X4}:{info.DeviceId:X4}";
            var memoryMegabytes = (long)info.DedicatedVideoMemory / (1024 * 1024);
            return string.IsNullOrWhiteSpace(name)
                ? $"适配器 {identity}"
                : $"{name}（{identity}，专用显存 {memoryMegabytes} MB）";
        }
        catch (Exception)
        {
            // 适配器信息只用于诊断，读不到不能影响采集本身。
            return "适配器信息读取失败";
        }
        finally
        {
            if (deviceObject is not null)
            {
                Marshal.ReleaseComObject(deviceObject);
            }

            if (adapterObject is not null)
            {
                Marshal.ReleaseComObject(adapterObject);
            }

            if (adapter != nint.Zero)
            {
                Marshal.Release(adapter);
            }
        }
    }

    private static string DescribeFeatureLevel(int featureLevel) =>
        featureLevel switch
        {
            Bgra8FeatureLevel => "11.1",
            FeatureLevel11 => "11.0",
            FeatureLevel10_1 => "10.1",
            FeatureLevel10 => "10.0",
            _ => $"0x{featureLevel:X}"
        };

    private static GraphicsCaptureItem CreateItem(CreateItemDelegate factory)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var classNameHandle));
        nint factoryPointer = nint.Zero;
        nint itemPointer = nint.Zero;
        try
        {
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classNameHandle, ref interopIid, out factoryPointer));
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

            _ = WindowsDeleteString(classNameHandle);
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

    /// <summary>DXGI 设备只用到 GetAdapter，后面的方法不用声明；但前面的槽位一个都不能省。</summary>
    [ComImport]
    [Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiDevice
    {
        int SetPrivateData(ref Guid name, uint dataSize, nint data);
        int SetPrivateDataInterface(ref Guid name, nint unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, nint data);
        int GetParent(ref Guid iid, out nint parent);
        int GetAdapter(out nint adapter);
    }

    [ComImport]
    [Guid("2411E7E1-12AC-4CCF-BD14-9798E8534DC0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiAdapter
    {
        int SetPrivateData(ref Guid name, uint dataSize, nint data);
        int SetPrivateDataInterface(ref Guid name, nint unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, nint data);
        int GetParent(ref Guid iid, out nint parent);
        int EnumOutputs(uint index, out nint output);
        int GetDesc(out DxgiAdapterDesc description);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        nint activatableClassId,
        ref Guid iid,
        out nint factory);

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
    private static extern bool IsWindow(nint window);
}
