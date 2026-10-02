using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KanamiReporter.CaptureDiag;

internal enum DiagTargetKind
{
    Window,
    Display
}

internal sealed record DiagTarget(
    DiagTargetKind Kind,
    nint Handle,
    string Label,
    string Title,
    string ProcessName,
    string? ProcessPath,
    string ClassName,
    string Bounds,
    bool IsMinimized,
    bool IsVisible,
    bool IsCloaked,
    string DisplayAffinity,
    string WindowStyles)
{
    public string Describe() =>
        Kind == DiagTargetKind.Display
            ? $"{Label}（显示器，句柄 0x{Handle:X}）"
            : $"{Label}（窗口，句柄 0x{Handle:X}，进程 {ProcessName}{(ProcessPath is null ? string.Empty : $" = {ProcessPath}")}）";
}

/// <summary>把"这台机器是什么环境"尽可能写全：出问题时这些信息才是定位的起点。</summary>
internal static class SystemReport
{
    public static void Write(DiagLog log, IReadOnlyList<string> adapters, IReadOnlyList<string> monitors, IReadOnlyList<DiagTarget> targets)
    {
        log.Section("环境信息");
        log.Line($"诊断工具  : KanamiCaptureDiag {typeof(SystemReport).Assembly.GetName().Version}（{RuntimeInformation.ProcessArchitecture}）");
        log.Line($"生成时间  : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        log.Line($"进程      : PID {Environment.ProcessId}，会话 {Process.GetCurrentProcess().SessionId}，管理员 {(IsElevated() ? "是" : "否")}");
        log.Line($"命令行    : {Environment.CommandLine}");

        log.Section("系统");
        var version = GetRealOsVersion();
        // ProductName 在 Windows 11 上仍然写着 Windows 10，所以名称和内部版本号一起给出。
        log.Line($"系统版本  : {version}（{ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName")}，" +
                 $"{ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion")}，" +
                 $"内部版本 {ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber")}." +
                 $"{ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR")}）");
        log.Line($"系统架构  : {(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")}，{RuntimeInformation.FrameworkDescription}");
        log.Line($"安装类型  : {ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallationType")}，" +
                 $"界面语言 LCID {GetUserDefaultUILanguage()}");
        log.Line($"处理器    : {ReadRegistryString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")}，可用处理器 {Environment.ProcessorCount}");
        log.Line($"内存      : {(long)GetTotalPhysicalMemoryBytes() / (1024 * 1024)} MB");

        var dpi = GetDpiForSystem();
        log.Line($"DPI       : 系统 {dpi}（{dpi * 100 / 96}%），虚拟屏幕 {GetSystemMetrics(78)}x{GetSystemMetrics(79)}");
        log.Line($"电源/性能 : {(IsUsingBattery() ? "电池供电" : "交流供电")}");

        log.Section("显卡与驱动");
        if (adapters.Count == 0)
        {
            log.Line("（未能枚举到 DXGI 适配器）");
        }

        foreach (var adapter in adapters)
        {
            log.Line(adapter);
        }

        foreach (var driver in ReadDriverVersions())
        {
            log.Line(driver);
        }

        log.Section("显示器");
        foreach (var monitor in monitors)
        {
            log.Line(monitor);
        }

        log.Section("可采集目标");
        log.Line("（本工具会列出所有可见窗口，包含主程序会过滤掉的工具窗口与最小化窗口，方便对照）");
        foreach (var target in targets)
        {
            if (target.Kind == DiagTargetKind.Display)
            {
                log.Line($"[显示器] {target.Label}  区域 {target.Bounds}");
            }
            else
            {
                log.Line(
                    $"[窗口]   {target.Label}  区域 {target.Bounds}  类名 {target.ClassName}" +
                    $"{Environment.NewLine}         最小化 {Yes(target.IsMinimized)} 可见 {Yes(target.IsVisible)} " +
                    $"被 DWM 隐藏 {Yes(target.IsCloaked)}  防截屏标记 {target.DisplayAffinity}  样式 {target.WindowStyles}");
            }
        }

        log.Section("说明");
        log.Line("防截屏标记：无 = 允许采集；WDA_MONITOR = 只有显示器能看到（采集会全黑）；");
        log.Line("            WDA_EXCLUDEFROMCAPTURE = 对所有采集隐藏（采集会全黑或没有画面）。");
        log.Line("最小化 / 被 DWM 隐藏的窗口本身就采不到画面，请先把游戏切回可见状态再测。");
    }

    private static string Yes(bool value) => value ? "是" : "否";

    private static string GetRealOsVersion()
    {
        try
        {
            var info = new OsVersionInfoEx { dwOSVersionInfoSize = Marshal.SizeOf<OsVersionInfoEx>() };
            if (RtlGetVersion(ref info) == 0)
            {
                return $"{info.dwMajorVersion}.{info.dwMinorVersion}.{info.dwBuildNumber}.{info.dwPlatformId}";
            }
        }
        catch (Exception)
        {
            // 取不到就退回托管报告值。
        }

        return Environment.OSVersion.Version.ToString();
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsUsingBattery() => GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    private static ulong GetTotalPhysicalMemoryBytes()
    {
        var memory = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref memory) ? memory.ullTotalPhys : 0;
    }

    private static string ReadRegistryString(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            var value = key?.GetValue(name)?.ToString();
            return string.IsNullOrWhiteSpace(value) ? "未知" : value.Trim();
        }
        catch (Exception)
        {
            return "未知";
        }
    }

    private static IEnumerable<string> ReadDriverVersions()
    {
        const string classPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        var results = new List<string>();
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(classPath);
            if (classKey is null)
            {
                return results;
            }

            foreach (var name in classKey.GetSubKeyNames().Where(name => name.Length == 4 && name.All(char.IsDigit)).OrderBy(name => name))
            {
                using var key = classKey.OpenSubKey(name);
                var description = key?.GetValue("DriverDesc")?.ToString();
                var version = key?.GetValue("DriverVersion")?.ToString();
                var date = key?.GetValue("DriverDate")?.ToString();
                if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                results.Add($"驱动 {name}  : {description ?? "未知"}  版本 {version ?? "未知"}  日期 {date ?? "未知"}");
            }
        }
        catch (Exception exception)
        {
            results.Add($"驱动信息读取失败：{exception.Message}");
        }

        return results;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfoEx
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;

        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OsVersionInfoEx versionInfo);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}

/// <summary>捕获目标枚举：显示器用 EnumDisplayMonitors，窗口用 EnumWindows（不过滤，全列出来）。</summary>
internal static class TargetDiscovery
{
    public static IReadOnlyList<DiagTarget> Discover()
    {
        var targets = new List<DiagTarget>();
        targets.AddRange(EnumerateMonitors());
        targets.AddRange(EnumerateWindows());
        return targets;
    }

    /// <summary>窗口当前在屏幕上的区域——GDI 方式按屏幕坐标抓取。</summary>
    public static (int X, int Y, int Width, int Height) GetWindowScreenBounds(nint window) =>
        GetWindowRect(window, out var rect)
            ? (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : (0, 0, 0, 0);

    /// <summary>显示器区域。</summary>
    public static (int X, int Y, int Width, int Height) GetMonitorBounds(nint monitor)
    {
        var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return (0, 0, 0, 0);
        }

        return (info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top);
    }

    private static IEnumerable<DiagTarget> EnumerateMonitors()
    {
        var handles = new List<nint>();
        EnumDisplayMonitors(nint.Zero, nint.Zero, (monitor, _, _, _) =>
        {
            handles.Add(monitor);
            return true;
        }, nint.Zero);

        var results = new List<DiagTarget>();
        foreach (var handle in handles)
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(handle, ref info))
            {
                continue;
            }

            var name = info.szDevice.TrimEnd('\0');
            var width = info.rcMonitor.Right - info.rcMonitor.Left;
            var height = info.rcMonitor.Bottom - info.rcMonitor.Top;
            var mode = new DevModeW { dmSize = (ushort)Marshal.SizeOf<DevModeW>() };
            var refresh = EnumDisplaySettings(name, -1, ref mode) ? $"@{mode.dmDisplayFrequency}Hz" : string.Empty;
            results.Add(new DiagTarget(
                DiagTargetKind.Display,
                handle,
                $"显示器 {name} ({width}x{height}{refresh})",
                string.Empty,
                string.Empty,
                null,
                string.Empty,
                $"{info.rcMonitor.Left},{info.rcMonitor.Top} {width}x{height}{((info.dwFlags & 1) != 0 ? " 主显示器" : string.Empty)}",
                false,
                true,
                false,
                "不适用",
                "不适用"));
        }

        return results;
    }

    private static IEnumerable<DiagTarget> EnumerateWindows()
    {
        var handles = new List<nint>();
        EnumWindows((window, _) =>
        {
            handles.Add(window);
            return true;
        }, nint.Zero);

        var results = new List<DiagTarget>();
        foreach (var window in handles)
        {
            var title = GetWindowTitle(window);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            if (!GetWindowRect(window, out var rect))
            {
                continue;
            }

            // 尺寸为 0 的都是 IME、提示条之类的隐藏窗口，列出来只会淹没真正的目标。
            if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0)
            {
                continue;
            }

            GetWindowThreadProcessId(window, out var processId);
            var processName = "未知";
            string? processPath = null;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch (Exception)
            {
                // 受保护进程读不到名字，不影响继续枚举。
            }

            processPath = TryGetProcessPath((int)processId);

            var cloaked = 0;
            var isCloaked = DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0;
            var affinity = GetWindowDisplayAffinity(window, out var affinityValue)
                ? DescribeDisplayAffinity(affinityValue)
                : "读取失败";
            var extendedStyle = GetWindowExtendedStyle(window);
            var styles = DescribeStyles(extendedStyle);

            results.Add(new DiagTarget(
                DiagTargetKind.Window,
                window,
                $"{processName} - {title.Trim()}",
                title.Trim(),
                processName,
                processPath,
                GetClassName(window),
                $"{rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}",
                IsIconic(window),
                IsWindowVisible(window),
                isCloaked,
                affinity,
                styles));
        }

        return results.OrderByDescending(target => target.IsVisible && !target.IsMinimized).ThenBy(target => target.Label, StringComparer.CurrentCultureIgnoreCase);
    }

    private static string DescribeDisplayAffinity(uint value) =>
        value switch
        {
            0 => "无（允许采集）",
            1 => "WDA_MONITOR（采集全黑）",
            0x11 => "WDA_EXCLUDEFROMCAPTURE（对所有采集隐藏）",
            _ => $"0x{value:X}"
        };

    private static string DescribeStyles(nint extendedStyle)
    {
        var parts = new List<string>();
        if (((long)extendedStyle & 0x00000080) != 0)
        {
            parts.Add("TOOLWINDOW");
        }

        if (((long)extendedStyle & 0x00040000) != 0)
        {
            parts.Add("APPWINDOW");
        }

        if (((long)extendedStyle & 0x00200000) != 0)
        {
            parts.Add("NOREDIRECTIONBITMAP");
        }

        if (((long)extendedStyle & 0x00080000) != 0)
        {
            parts.Add("LAYERED");
        }

        return parts.Count == 0 ? "普通" : string.Join("|", parts);
    }

    private static string GetWindowTitle(nint window)
    {
        var length = Math.Max(1, GetWindowTextLength(window) + 1);
        var builder = new StringBuilder(length);
        _ = GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetClassName(nint window)
    {
        var builder = new StringBuilder(256);
        var length = GetClassNameNative(window, builder, builder.Capacity);
        return length <= 0 ? "未知" : builder.ToString(0, length);
    }

    private static string? TryGetProcessPath(int processId)
    {
        nint handle = nint.Zero;
        try
        {
            handle = OpenProcess(0x1000, false, processId);
            if (handle == nint.Zero)
            {
                return null;
            }

            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString(0, size) : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (handle != nint.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    private static nint GetWindowExtendedStyle(nint window) =>
        nint.Size == 8 ? GetWindowLongPtr(window, -20) : GetWindowLong(window, -20);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevModeW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);
    private delegate bool EnumWindowsProc(nint window, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevModeW mode);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameNative(nint window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("kernel32.dll")]
    private static extern nint OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
