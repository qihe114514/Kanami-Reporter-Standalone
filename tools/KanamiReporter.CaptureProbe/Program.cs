using System.Diagnostics;
using System.Text;
using KanamiReporter.Core;
using KanamiReporter.Windows;

Console.OutputEncoding = Encoding.UTF8;

await using var source = new WindowsGraphicsCaptureSource();
var targets = await source.DiscoverTargetsAsync();

if (args.Length == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
{
    foreach (var target in targets)
    {
        Console.WriteLine($"{target.Kind,-7} {target.Id,-20} {target.DisplayName}");
    }

    return;
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
    return;
}

var frameCount = 0;
var firstFrame = new TaskCompletionSource<CapturedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
var lastFrame = DateTime.UtcNow;
source.FrameAvailable += (_, frame) =>
{
    Interlocked.Increment(ref frameCount);
    firstFrame.TrySetResult(frame);
    lastFrame = DateTime.UtcNow;
};
source.StatusChanged += (_, status) => Console.WriteLine($"[状态] {status.Message} {status.Error}");

Console.WriteLine($"开始捕获：{selected.DisplayName}");
await source.StartAsync(selected);
var frame = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
await Task.Delay(TimeSpan.FromSeconds(3));
await source.StopAsync();

var average = SampleAverage(frame.Bgra);
Console.WriteLine($"帧数={frameCount}，尺寸={frame.Width}x{frame.Height}，抽样平均亮度={average:0.0}，最后帧间隔={(DateTime.UtcNow - lastFrame).TotalMilliseconds:0}ms");
Console.WriteLine(average < 1.0 ? "警告：画面接近纯黑，可能被系统或游戏阻止捕获。" : "采集成功：画面包含有效像素。");

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

