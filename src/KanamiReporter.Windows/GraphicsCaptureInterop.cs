using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace KanamiReporter.Windows;

internal static class GraphicsCaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static GraphicsCaptureItem CreateForWindow(nint windowHandle) =>
        CreateItem((IGraphicsCaptureItemInterop interop, ref Guid iid) => interop.CreateForWindow(windowHandle, ref iid));

    public static GraphicsCaptureItem CreateForMonitor(nint monitorHandle) =>
        CreateItem((IGraphicsCaptureItemInterop interop, ref Guid iid) => interop.CreateForMonitor(monitorHandle, ref iid));

    public static IDirect3DDevice CreateDirect3DDevice()
    {
        var featureLevels = new[] { 0xB100, 0xB000, 0xA100, 0xA000 };
        var hr = D3D11CreateDevice(
            nint.Zero,
            1,
            nint.Zero,
            0x20,
            featureLevels,
            featureLevels.Length,
            7,
            out var device,
            out _,
            out var context);

        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                nint.Zero,
                3,
                nint.Zero,
                0x20,
                featureLevels,
                featureLevels.Length,
                7,
                out device,
                out _,
                out context);
        }

        Marshal.ThrowExceptionForHR(hr);

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
}



