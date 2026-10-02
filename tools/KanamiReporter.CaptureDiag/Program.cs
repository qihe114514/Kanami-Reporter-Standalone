using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using KanamiReporter.CaptureDiag;

// 采集方式自检：让用户选一个目标，然后用多种互不相同的机制去采，挑出真正能拿到画面的那一种，
// 并把设备信息、系统信息、窗口状态和每次尝试的完整结果写成一份日志一起打包回来。

Console.OutputEncoding = Encoding.UTF8;
TryEnablePerMonitorDpi();

var arguments = ParseArguments(args);
var outputRoot = CreateOutputDirectory();
var logPath = Path.Combine(outputRoot, "capture-diag.log");
var snapshotDirectory = Path.Combine(outputRoot, "snapshots");

using var log = new DiagLog(logPath);
log.Line($"采集方式自检：日志目录 {outputRoot}");

try
{
    var adapters = Dxgi.Enumerate(out var adapterError);
    if (adapterError is not null)
    {
        log.Line($"适配器枚举失败：{adapterError}");
    }

    var targets = TargetDiscovery.Discover();
    var monitorLines = targets
        .Where(target => target.Kind == DiagTargetKind.Display)
        .Select(target => $"{target.Label}  区域 {target.Bounds}")
        .ToList();
    SystemReport.Write(log, Dxgi.DescribeAll(adapters), monitorLines, targets);

    var selected = SelectTarget(targets, arguments);
    if (selected is null)
    {
        log.Problem("没有选中任何采集目标，已结束。");
        return 1;
    }

    log.Section("本次测试");
    log.Line($"目标：{selected.Describe()}");
    if (selected.Kind == DiagTargetKind.Window)
    {
        log.Line($"窗口状态：最小化 {Yes(selected.IsMinimized)}，可见 {Yes(selected.IsVisible)}，被 DWM 隐藏 {Yes(selected.IsCloaked)}，防截屏标记 {selected.DisplayAffinity}");
        if (selected.IsMinimized)
        {
            log.Problem("注意：这个窗口当前是最小化的，最小化的窗口基本采不到画面。建议先切到游戏画面再测一次。");
        }
    }

    log.Line($"每个方式采样 {arguments.SampleSeconds} 秒，首帧超时 5 秒。");
    log.Section("采集结果");

    var attempts = CaptureMethods.RunAll(selected, adapters, arguments.SampleSeconds, snapshotDirectory, log);

    log.Section("汇总");
    for (var index = 0; index < attempts.Count; index++)
    {
        var attempt = attempts[index];
        log.Line($"{index + 1,2}. {attempt.Method}");
        log.Line($"    结果：{attempt.Outcome}" +
                 $"{(attempt.Metrics.Length == 0 ? string.Empty : $"｜{attempt.Metrics}")}");
        if (attempt.Notes is not null)
        {
            log.Line($"    备注：{attempt.Notes}");
        }

        if (attempt.Snapshot is not null)
        {
            log.Line($"    快照：{Path.GetFileName(attempt.Snapshot)}");
        }
    }

    log.Section("结论");
    foreach (var line in BuildConclusion(attempts, selected))
    {
        log.Line(line);
    }
}
catch (Exception exception)
{
    log.Problem($"自检过程中出错：{exception}");
}
finally
{
    log.Line();
    log.Line($"日志文件：{logPath}");
}

var archive = ZipOutput(outputRoot, log);
Console.WriteLine();
Console.WriteLine($"把这个文件发回来即可：{archive}");
return 0;

static void TryEnablePerMonitorDpi()
{
    try
    {
        // -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2：GDI 抓屏必须按真实像素坐标。
        _ = NativeMethods.SetDpiAwareness(new nint(-4));
    }
    catch (Exception)
    {
        // 老系统没有这个 API，按系统默认处理。
    }
}

static string CreateOutputDirectory()
{
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    foreach (var root in new[] { AppContext.BaseDirectory, Path.GetTempPath() })
    {
        try
        {
            var directory = Path.Combine(root, $"kanami-capture-diag-{stamp}");
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception)
        {
            // 目录不可写就换下一个位置。
        }
    }

    return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"kanami-capture-diag-{stamp}")).FullName;
}

static string ZipOutput(string directory, DiagLog log)
{
    try
    {
        var archive = directory.TrimEnd(Path.DirectorySeparatorChar) + ".zip";
        if (File.Exists(archive))
        {
            File.Delete(archive);
        }

        // 先关闭日志文件，否则压缩时会占用文件。
        log.Dispose();
        ZipFile.CreateFromDirectory(directory, archive, CompressionLevel.Optimal, includeBaseDirectory: true);
        return archive;
    }
    catch (Exception exception)
    {
        return $"{directory}（打包失败：{exception.Message}，直接发这个目录也行）";
    }
}

static DiagTarget? SelectTarget(IReadOnlyList<DiagTarget> targets, DiagArguments arguments)
{
    var candidates = targets.Where(target => target.Kind == DiagTargetKind.Display || target.IsVisible || !target.IsMinimized).ToList();
    if (candidates.Count == 0)
    {
        return null;
    }

    if (arguments.Target is not null)
    {
        if (int.TryParse(arguments.Target, out var index) && index >= 1 && index <= candidates.Count)
        {
            return candidates[index - 1];
        }

        var matches = candidates.Where(target =>
            target.Label.Contains(arguments.Target, StringComparison.OrdinalIgnoreCase) ||
            target.Title.Contains(arguments.Target, StringComparison.OrdinalIgnoreCase) ||
            target.ProcessName.Contains(arguments.Target, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count > 0 ? matches[0] : null;
    }

    Console.WriteLine();
    Console.WriteLine("可测试的目标：");
    for (var index = 0; index < candidates.Count; index++)
    {
        var target = candidates[index];
        var hint = target.Kind == DiagTargetKind.Display
            ? "显示器"
            : $"窗口  {target.ProcessName}  {target.Bounds}{(target.IsMinimized ? "  [已最小化]" : string.Empty)}";
        Console.WriteLine($"  {index + 1,2}. {hint}  {target.Label}");
    }

    var suggested = candidates.FirstOrDefault(target =>
        target.Kind == DiagTargetKind.Window &&
        (target.ProcessName.Contains("DeltaForce", StringComparison.OrdinalIgnoreCase) ||
         target.Title.Contains("三角洲", StringComparison.Ordinal) ||
         target.Title.Contains("Delta", StringComparison.OrdinalIgnoreCase)));
    suggested ??= candidates[0];
    var suggestedIndex = candidates.IndexOf(suggested) + 1;

    Console.WriteLine();
    Console.Write($"请输入要测试的目标编号（直接回车 = {suggestedIndex} {suggested.Label}）：");
    var input = arguments.NoPrompt ? null : Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input))
    {
        return suggested;
    }

    return int.TryParse(input.Trim(), out var picked) && picked >= 1 && picked <= candidates.Count
        ? candidates[picked - 1]
        : suggested;
}

static IEnumerable<string> BuildConclusion(IReadOnlyList<CaptureAttempt> attempts, DiagTarget target)
{
    var withPicture = attempts.Where(attempt => attempt.HasPicture).ToList();
    var moving = withPicture.Where(attempt => attempt.IsMoving).ToList();

    if (withPicture.Count == 0)
    {
        yield return "所有采集方式都没能拿到画面。";
        yield return "这种结果基本可以确定不是主程序的问题，而是这个目标本身被系统或游戏挡住了：";
        yield return "  · 看上面的「防截屏标记」：WDA_MONITOR / WDA_EXCLUDEFROMCAPTURE 会让所有采集全黑；";
        yield return "  · 独占全屏（真全屏）时窗口和显示器都可能采不到，请在游戏里切到无边框窗口模式；";
        yield return "  · 反作弊（ACE）也可能直接屏蔽第三方采集。";
        yield return "请把这份日志和上面每个方式的快照一起发回来。";
        yield break;
    }

    yield return $"能拿到画面的方式共 {withPicture.Count} 种：";
    foreach (var attempt in moving)
    {
        yield return $"  ✓ {attempt.Method}（{attempt.Outcome}，{attempt.Metrics}）";
    }

    foreach (var attempt in withPicture.Where(attempt => !attempt.IsMoving))
    {
        yield return $"  ○ {attempt.Method}（有画面，但测试期间内容没有变化；{attempt.Metrics}）";
    }

    // 推荐顺序：会变化的窗口采集 > 静止的窗口采集 > 会变化的其它方式 > 静止的其它方式。
    // 窗口采集是主程序的主路径，只要它能出画面就不该推荐换成别的。
    var baseline = attempts.FirstOrDefault(attempt => attempt.Method.Contains("默认显卡", StringComparison.Ordinal));
    var baselineWorks = baseline?.HasPicture == true;
    var gdiWorks = withPicture.Any(attempt => attempt.Method.StartsWith("GDI", StringComparison.Ordinal));
    var best = (baselineWorks && baseline!.IsMoving ? baseline : null)
        ?? moving.FirstOrDefault()
        ?? (baselineWorks ? baseline : null)
        ?? withPicture[0];
    yield return $"建议：用「{best!.Method}」。请对照它的快照确认里面确实是游戏画面。";
    if (baselineWorks && !baseline!.IsMoving && best != baseline)
    {
        yield return "     注意：窗口采集这次拿到的是一张不变化的画面，而别的方式是动的。这通常说明游戏窗口在后台（被别的窗口挡住时，";
        yield return "     系统不会继续更新它的画面）：请把游戏切到前台、画面动起来再跑一次，确认窗口采集拿到的是活画面。";
    }

    if (moving.Count == 0)
    {
        yield return "注意：本次测试期间所有方式抓到的画面都没有变化。如果你测试时画面本来就是静止的（菜单、加载、观战），这属于正常；";
        yield return "      请在实际对局画面下再跑一次，确认拿到的是活画面而不是一张冻结的旧图。";
    }

    if (!baselineWorks && gdiWorks)
    {
        yield return "判断：Windows 图形捕获（DWM 路径）在这个目标上拿不到画面，但 GDI 抓屏可以——";
        yield return "     说明问题出在系统采集接口这一侧，主程序需要换成 GDI 方式或换采集目标。";
    }

    if (!baselineWorks && withPicture.Any(attempt => attempt.Method.Contains("适配器 #", StringComparison.Ordinal)))
    {
        yield return "判断：指定其它显卡适配器后才能拿到画面，这台机器是多显卡环境——";
        yield return "     主程序需要把采集设备建在游戏实际渲染的那块显卡上。";
    }

    if (!baselineWorks && withPicture.Any(attempt => attempt.Method.Contains("HDR", StringComparison.Ordinal)))
    {
        yield return "判断：只有 HDR 浮点格式能拿到画面，说明显示器处于 HDR 模式——";
        yield return "     主程序需要按 HDR 像素格式取帧，或提示用户关闭 HDR。";
    }

    if (!baselineWorks && withPicture.Any(attempt => attempt.Method.Contains("窗口所在显示器", StringComparison.Ordinal)))
    {
        yield return "判断：窗口本身采不到、但所在显示器可以——这正是主程序黑屏后自动回退到显示器的那条路径，";
        yield return "     游戏多半跑在独占全屏，切到无边框窗口模式即可恢复窗口采集。";
    }

    if (target.Kind == DiagTargetKind.Window && target.DisplayAffinity != "无（允许采集）")
    {
        yield return $"提醒：这个窗口带防截屏标记（{target.DisplayAffinity}），采集全黑属于预期行为。";
    }
}

static string Yes(bool value) => value ? "是" : "否";

static DiagArguments ParseArguments(string[] args)
{
    string? target = null;
    var sampleSeconds = 3;
    var noPrompt = false;

    for (var index = 0; index < args.Length; index++)
    {
        switch (args[index])
        {
            case "--target" when index + 1 < args.Length:
                target = args[++index];
                break;
            case "--seconds" when index + 1 < args.Length && int.TryParse(args[index + 1], out var seconds):
                sampleSeconds = Math.Clamp(seconds, 1, 30);
                index++;
                break;
            case "--no-prompt":
                noPrompt = true;
                break;
        }
    }

    return new DiagArguments(target, sampleSeconds, noPrompt);
}

internal sealed record DiagArguments(string? Target, int SampleSeconds, bool NoPrompt);

internal static class EnumerableExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> list, T value)
    {
        for (var index = 0; index < list.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(list[index], value))
            {
                return index;
            }
        }

        return -1;
    }
}

internal static partial class NativeMethods
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint context);

    public static bool SetDpiAwareness(nint context) => SetProcessDpiAwarenessContext(context);
}
