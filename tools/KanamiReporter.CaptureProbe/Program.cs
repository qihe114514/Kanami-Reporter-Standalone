using System.Diagnostics;
using System.Text;
using KanamiReporter.Core;
using KanamiReporter.Windows;

// 采集探针：独立于主程序验证"这台机器能不能采到画面"，并把采集层的诊断日志打到控制台。
// 退出码：0=采到有效画面，1=没找到目标，2=一直没有收到帧，3=收到的帧接近纯黑。

const int FirstFrameTimeoutSeconds = 10;
const int SamplingSeconds = 3;

Console.OutputEncoding = Encoding.UTF8;

await using var source = new WindowsGraphicsCaptureSource(new ConsoleLogger());
var targets = await source.DiscoverTargetsAsync();

if (args.Length == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
{
    foreach (var target in targets)
    {
        Console.WriteLine($"{target.Kind,-7} {target.Id,-20} {target.DisplayName}");
    }

    return 0;
}

var mode = args[0];
CaptureTargetDescriptor? selected = mode.ToLowerInvariant() switch
{
    "display" => targets.FirstOrDefault(target => target.Kind == CaptureTargetKind.Display),
    "window" when args.Length > 1 => targets.FirstOrDefault(target =>
        (target.ProcessName?.Contains(args[1], StringComparison.OrdinalIgnoreCase) ?? false) ||
        target.DisplayName.Contains(args[1], StringComparison.OrdinalIgnoreCase)),
    _ => null
};

if (selected is null)
{
    Console.Error.WriteLine("未找到匹配的捕获目标。使用 list 查看可用目标。");
    return 1;
}

var frameCount = 0;
var firstFrame = new TaskCompletionSource<CapturedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
var lastFrame = DateTime.UtcNow;
var startedAt = Stopwatch.StartNew();
source.FrameAvailable += (_, frame) =>
{
    Interlocked.Increment(ref frameCount);
    firstFrame.TrySetResult(frame);
    lastFrame = DateTime.UtcNow;
};
source.StatusChanged += (_, status) => Console.WriteLine($"[状态] {status.Message} {status.Error}");

Console.WriteLine($"开始捕获：{selected.DisplayName}（{selected.Kind}，句柄 0x{selected.NativeHandle:X}）");
await source.StartAsync(selected);

CapturedFrame first;
try
{
    first = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(FirstFrameTimeoutSeconds));
}
catch (TimeoutException)
{
    Console.Error.WriteLine($"失败：{FirstFrameTimeoutSeconds} 秒内没有收到任何一帧（采集会话已建立，但系统没有送来画面）。");
    Console.Error.WriteLine("常见原因：窗口已最小化、游戏以独占全屏运行、画面被反作弊或系统保护挡住、");
    Console.Error.WriteLine("采集设备与游戏不在同一块显卡上。可先用 display 模式确认显示器采集是否正常。");
    await source.StopAsync();
    return 2;
}

Console.WriteLine($"收到首帧：启动后 {startedAt.ElapsedMilliseconds} 毫秒。");
await Task.Delay(TimeSpan.FromSeconds(SamplingSeconds));
await source.StopAsync();

var average = SampleAverage(first.Bgra);
Console.WriteLine(
    $"帧数={frameCount}，尺寸={first.Width}x{first.Height}，抽样平均亮度={average:0.0}，" +
    $"最后帧间隔={(DateTime.UtcNow - lastFrame).TotalMilliseconds:0}ms");
if (average < 1.0)
{
    Console.WriteLine("警告：画面接近纯黑，可能被系统或游戏阻止捕获。");
    return 3;
}

Console.WriteLine("采集成功：画面包含有效像素。");
return 0;

static double SampleAverage(byte[] bgra)
{
    double sum = 0;
    var count = 0;
    for (var i = 0; i < bgra.Length; i += 4096)
    {
        var pixel = i - (i % 4);
        sum += (bgra[pixel] + bgra[pixel + 1] + bgra[pixel + 2]) / 3.0;
        count++;
    }

    return count == 0 ? 0 : sum / count;
}

internal sealed class ConsoleLogger : IAppLogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");

    public void Warning(string message) => Console.WriteLine($"[WARN] {message}");

    public void Error(string message, Exception? exception = null) =>
        Console.Error.WriteLine(
            exception is null ? $"[ERROR] {message}" : $"[ERROR] {message}{Environment.NewLine}{exception}");
}
