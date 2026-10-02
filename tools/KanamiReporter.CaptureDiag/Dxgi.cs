using System.Runtime.InteropServices;

namespace KanamiReporter.CaptureDiag;

internal sealed record DxgiAdapterInfo(int Index, string Name, uint VendorId, uint DeviceId, ulong DedicatedVideoMemory, ulong SharedSystemMemory);

/// <summary>
/// DXGI 适配器枚举：多显卡机器上"应用用的显卡"和"游戏渲染的显卡"不是同一块时，采集会一帧都拿不到，
/// 所以这份适配器清单是排查黑屏的关键信息之一。
/// 注意这里必须用 CreateDXGIFactory1：老的 CreateDXGIFactory（DXGI 1.0）在部分系统上对任何 IID
/// 都返回 E_NOINTERFACE，实测本机就是这样。
/// </summary>
internal static class Dxgi
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private static readonly Guid Factory1Iid = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid AdapterIid = new("2411E7E1-12AC-4CCF-BD14-9798E8534DC0");

    public static IReadOnlyList<DxgiAdapterInfo> Enumerate() => Enumerate(out _);

    public static IReadOnlyList<DxgiAdapterInfo> Enumerate(out string? error)
    {
        error = null;
        var results = new List<DxgiAdapterInfo>();
        nint factory = nint.Zero;
        try
        {
            var factoryError = CreateFactory(out factory);
            if (factoryError is not null)
            {
                error = factoryError;
                return results;
            }

            var interop = (IDxgiFactory1)Marshal.GetObjectForIUnknown(factory);
            for (uint index = 0; index < 8; index++)
            {
                nint adapter = nint.Zero;
                try
                {
                    if (interop.EnumAdapters1(index, out adapter) < 0 || adapter == nint.Zero)
                    {
                        break;
                    }
                }
                catch (COMException exception) when (exception.HResult == DxgiErrorNotFound)
                {
                    // DXGI_ERROR_NOT_FOUND：枚举到头了，这是正常结束，不是错误。
                    break;
                }

                try
                {
                    var info = Describe(adapter);
                    if (info is null)
                    {
                        error ??= "读取适配器描述失败";
                    }
                    else
                    {
                        results.Add(info with { Index = (int)index });
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        catch (Exception exception)
        {
            // 枚举失败不影响其它采集方式，但原因必须写进日志。
            error = $"{exception.GetType().Name}：{exception.Message}";
        }
        finally
        {
            if (factory != nint.Zero)
            {
                Marshal.Release(factory);
            }
        }

        return results;
    }

    public static IReadOnlyList<string> DescribeAll(IReadOnlyList<DxgiAdapterInfo> adapters)
    {
        var lines = new List<string>();
        foreach (var adapter in adapters)
        {
            lines.Add(
                $"适配器 #{adapter.Index}: {adapter.Name}（厂商 {adapter.VendorId:X4}，设备 {adapter.DeviceId:X4}，" +
                $"专用显存 {adapter.DedicatedVideoMemory / (1024 * 1024)} MB，共享内存 {adapter.SharedSystemMemory / (1024 * 1024)} MB）");
        }

        return lines;
    }

    /// <summary>取指定适配器的 IDXGIAdapter 指针，调用方负责 Release。</summary>
    public static nint GetAdapterPointer(int index)
    {
        nint factory = nint.Zero;
        nint adapter = nint.Zero;
        try
        {
            if (CreateFactory(out factory) is not null)
            {
                return nint.Zero;
            }

            var interop = (IDxgiFactory1)Marshal.GetObjectForIUnknown(factory);
            return interop.EnumAdapters1((uint)index, out adapter) < 0 ? nint.Zero : adapter;
        }
        catch (Exception)
        {
            return nint.Zero;
        }
        finally
        {
            if (factory != nint.Zero)
            {
                Marshal.Release(factory);
            }
        }
    }

    private static string? CreateFactory(out nint factory)
    {
        factory = nint.Zero;
        var iid = Factory1Iid;
        var hr = CreateDXGIFactory1(ref iid, out factory);
        return hr < 0 ? $"CreateDXGIFactory1 失败（0x{hr:X8}）" : null;
    }

    private static DxgiAdapterInfo? Describe(nint adapter)
    {
        nint adapterInterface = nint.Zero;
        try
        {
            var iid = AdapterIid;
            if (Marshal.QueryInterface(adapter, ref iid, out adapterInterface) < 0)
            {
                return null;
            }

            var dxgiAdapter = (IDxgiAdapter)Marshal.GetObjectForIUnknown(adapterInterface);
            if (dxgiAdapter.GetDesc(out var desc) < 0)
            {
                return null;
            }

            return new DxgiAdapterInfo(
                0,
                string.IsNullOrWhiteSpace(desc.Description) ? "未知适配器" : desc.Description.Trim(),
                desc.VendorId,
                desc.DeviceId,
                desc.DedicatedVideoMemory,
                desc.SharedSystemMemory);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (adapterInterface != nint.Zero)
            {
                Marshal.Release(adapterInterface);
            }
        }
    }

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiFactory1
    {
        // IDXGIObject
        int SetPrivateData(ref Guid name, uint dataSize, nint data);
        int SetPrivateDataInterface(ref Guid name, nint unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, nint data);
        int GetParent(ref Guid iid, out nint parent);

        // IDXGIFactory（用不到，但槽位不能省）
        int EnumAdapters(uint index, out nint adapter);
        int MakeWindowAssociation(nint window, uint flags);
        int GetWindowAssociation(out nint window);
        int CreateSwapChain(nint device, nint description, out nint swapChain);
        int CreateSoftwareAdapter(nint module, out nint adapter);

        // IDXGIFactory1
        int EnumAdapters1(uint index, out nint adapter);
        int IsCurrent();
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
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public long AdapterLuid;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
}
