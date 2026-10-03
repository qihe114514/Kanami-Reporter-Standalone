using KanamiReporter.Core;

namespace KanamiReporter.ReplayAnalyzer;

/// <summary>
/// 离线回放分析：把录像抽出的原始帧按真实识别管线归一化，再对模板目录里的
/// 每个模板打分，可选在给定半径内搜索最佳偏移。
///
/// 用途：
/// - 核对某个分辨率（如 16:10）下模板是否还能命中、偏了多少像素；
/// - 用 search 模式测量 UI 元素在两个分辨率之间的实际位移，为坐标适配提供依据。
///
/// 宽范围搜索：方差项用积分图 O(1) 查询，模板乘积项逐位置直接计算（与核心同一套 ZNCC
/// 公式），找到峰值后再用 <see cref="FrameProcessing.ScoreDetailed"/> 复验，
/// 保证报告的分值与正式识别完全一致。
///
/// 帧格式：纯 BGRA 原始字节（宽*高*4），文件名按字典序处理。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0] switch
        {
            "analyze" => RunAnalyze(args),
            "replay" => RunReplay(args),
            "bbox" => RunBbox(args),
            "compare" => RunCompare(args),
            "mktemplate" => RunMakeTemplate(args),
            "selfcheck" => RunSelfCheck(),
            _ => Fail()
        };

        static int Fail()
        {
            PrintUsage();
            return 1;
        }
    }

    /// <summary>
    /// 测量某个区域内"亮像素"（灰度 ≥ 阈值）的包围盒与行/列轮廓，
    /// 用于精确比较模板与实机帧里文字/图标的位置与尺寸。
    ///
    /// 用法：bbox &lt;文件&gt; &lt;宽&gt; &lt;高&gt; &lt;gray|bgra&gt; &lt;x&gt; &lt;y&gt; &lt;区域宽&gt; &lt;区域高&gt; [阈值]
    /// </summary>
    private static int RunBbox(string[] args)
    {
        if (args.Length < 9)
        {
            PrintUsage();
            return 1;
        }

        var path = args[1];
        var width = int.Parse(args[2]);
        var height = int.Parse(args[3]);
        var format = args[4];
        var x0 = int.Parse(args[5]);
        var y0 = int.Parse(args[6]);
        var rectWidth = int.Parse(args[7]);
        var rectHeight = int.Parse(args[8]);
        var threshold = args.Length > 9 ? int.Parse(args[9]) : 128;

        var bytes = File.ReadAllBytes(path);
        var gray = new byte[width * height];
        if (string.Equals(format, "gray", StringComparison.OrdinalIgnoreCase))
        {
            Array.Copy(bytes, gray, Math.Min(bytes.Length, gray.Length));
        }
        else
        {
            var bgra = bytes;
            for (var i = 0; i < gray.Length; i++)
            {
                var pixel = i * 4;
                gray[i] = (byte)((29U * bgra[pixel] + 150U * bgra[pixel + 1] + 77U * bgra[pixel + 2]) >> 8);
            }
        }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (var y = y0; y < y0 + rectHeight && y < height; y++)
        {
            for (var x = x0; x < x0 + rectWidth && x < width; x++)
            {
                if (gray[(y * width) + x] < threshold)
                {
                    continue;
                }

                count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (count == 0)
        {
            Console.WriteLine($"区域 ({x0},{y0}) {rectWidth}x{rectHeight}：没有 ≥{threshold} 的像素。");
            return 0;
        }

        Console.WriteLine($"区域 ({x0},{y0}) {rectWidth}x{rectHeight}，阈值 ≥{threshold}：");
        Console.WriteLine($"  亮像素 {count} 个，包围盒 x={minX}..{maxX} (宽 {maxX - minX + 1})，y={minY}..{maxY} (高 {maxY - minY + 1})");

        // 行轮廓：每行亮像素数，方便看文字基线/行高。
        Console.Write("  行轮廓: ");
        for (var y = minY; y <= maxY; y++)
        {
            var rowCount = 0;
            for (var x = x0; x < x0 + rectWidth && x < width; x++)
            {
                if (gray[(y * width) + x] >= threshold)
                {
                    rowCount++;
                }
            }

            Console.Write(rowCount > 0 ? $"{y}:{rowCount} " : "");
        }

        Console.WriteLine();
        return 0;
    }

    private static int RunAnalyze(string[] args)
    {
        if (args.Length < 5)
        {
            PrintUsage();
            return 1;
        }

        var frameDirectory = args[1];
        var width = int.Parse(args[2]);
        var height = int.Parse(args[3]);
        var templateDirectory = args[4];

        var searchRadius = 0;
        string? dumpDirectory = null;
        var minScore = 0.0;
        for (var i = 5; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--search":
                    searchRadius = int.Parse(args[i + 1]);
                    break;
                case "--dump":
                    dumpDirectory = args[i + 1];
                    break;
                case "--min":
                    minScore = double.Parse(args[i + 1]);
                    break;
            }
        }

        var frames = Directory.Exists(frameDirectory)
            ? Directory.GetFiles(frameDirectory, "*.bgra").OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : [frameDirectory];

        if (frames.Length == 0)
        {
            Console.WriteLine($"没有找到帧文件：{frameDirectory}");
            return 1;
        }

        var templates = LoadTemplates(templateDirectory);
        if (templates.Count == 0)
        {
            Console.WriteLine($"模板目录里没有可用的模板：{templateDirectory}");
            return 1;
        }

        if (dumpDirectory is not null)
        {
            Directory.CreateDirectory(dumpDirectory);
        }

        var expectedBytes = checked(width * height * 4);
        Console.WriteLine($"源分辨率 {width}x{height}，共 {frames.Length} 帧，{templates.Count} 个模板，搜索半径 ±{searchRadius}，只显示 ≥{minScore:F2}。");
        Console.Out.Flush();

        // 全片汇总：每个模板在扫描中拿到的最优分与出现位置。
        var summary = new Dictionary<string, (double Score, string Frame, double OffsetX, double OffsetY)>(StringComparer.Ordinal);

        foreach (var framePath in frames)
        {
            var source = File.ReadAllBytes(framePath);
            if (source.Length < expectedBytes)
            {
                Console.WriteLine($"[跳过] {Path.GetFileName(framePath)}：字节数 {source.Length} 少于 {expectedBytes}。");
                continue;
            }

            var normalized = FrameProcessing.Normalize(source, width, height);
            var gray = FrameProcessing.ToGrayscale(normalized, ReporterStates.FrameWidth, ReporterStates.FrameHeight);

            if (dumpDirectory is not null)
            {
                var dumpPath = Path.Combine(dumpDirectory, Path.GetFileNameWithoutExtension(framePath) + ".normalized.bgra");
                File.WriteAllBytes(dumpPath, normalized);
            }

            var integral = BuildIntegral(gray, static value => value);
            var integralSq = BuildIntegral(gray, static value => (long)value * value);

            var results = new (string Name, double Score, double OffsetX, double OffsetY)[templates.Count];
            Parallel.For(0, templates.Count, i =>
            {
                var (name, model) = templates[i];
                var (score, offsetX, offsetY) = BestMatch(model, gray, searchRadius, integral, integralSq);
                results[i] = (name, score, offsetX, offsetY);
            });

            Console.WriteLine($"=== {Path.GetFileName(framePath)} ===");
            foreach (var (name, score, offsetX, offsetY) in results.OrderByDescending(hit => hit.Score))
            {
                if (score < minScore)
                {
                    continue;
                }

                var flag = score >= ReporterStates.DefaultThreshold ? "HIT " : "miss";
                Console.WriteLine($"  {flag} {name,-28} score={score:F4} offset=({offsetX:+0;-0;0},{offsetY:+0;-0;0})");
            }

            Console.Out.Flush();

            for (var i = 0; i < results.Length; i++)
            {
                var current = summary.GetValueOrDefault(results[i].Name, (Score: -1.0, Frame: string.Empty, OffsetX: 0, OffsetY: 0));
                if (results[i].Score > current.Score)
                {
                    summary[results[i].Name] = (results[i].Score, Path.GetFileName(framePath), results[i].OffsetX, results[i].OffsetY);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 各模板最优分（全片扫描汇总）===");
        foreach (var (name, best) in summary.OrderByDescending(pair => pair.Value.Score))
        {
            var flag = best.Score >= ReporterStates.DefaultThreshold ? "HIT " : "miss";
            Console.WriteLine($"  {flag} {name,-28} best={best.Score:F4} @{best.Frame} offset=({best.OffsetX:+0;-0;0},{best.OffsetY:+0;-0;0})");
        }

        return 0;
    }

    /// <summary>
    /// 在模板 ROI 周围 ±radius 像素内找最佳位置：
    /// radius ≤ 1 时直接用核心的 ±1 精修；否则用快速搜索找峰值，再用核心复验。
    /// </summary>
    private static (double Score, double OffsetX, double OffsetY) BestMatch(
        TemplateModel model,
        byte[] gray,
        int radius,
        long[] integral,
        long[] integralSq)
    {
        if (radius <= 1)
        {
            var details = FrameProcessing.ScoreDetailed(model, gray);
            return (details.BestScore, details.BestOffsetX, details.BestOffsetY);
        }

        var fast = FastSearch(model, gray, radius, integral, integralSq);
        if (fast.Score < 0)
        {
            return (-1.0, 0, 0);
        }

        var verified = FrameProcessing.ScoreDetailed(
            Shift(model, (int)fast.OffsetX, (int)fast.OffsetY),
            gray);
        if (verified.BestScore < 0)
        {
            return (fast.Score, fast.OffsetX, fast.OffsetY);
        }

        return (
            verified.BestScore,
            fast.OffsetX + verified.BestOffsetX,
            fast.OffsetY + verified.BestOffsetY);
    }

    /// <summary>
    /// 快速 ZNCC 搜索网格：方差项用积分图 O(1) 查询，模板乘积项逐位置直接累加。
    /// 返回搜索网格上的峰值（未做 ±1 精修，由调用方用核心复验）。
    /// </summary>
    private static (double Score, double OffsetX, double OffsetY) FastSearch(
        TemplateModel model,
        byte[] gray,
        int radius,
        long[] integral,
        long[] integralSq)
    {
        const int frameWidth = ReporterStates.FrameWidth;
        const int frameHeight = ReporterStates.FrameHeight;

        var roi = model.Roi;
        var w = roi.Width;
        var h = roi.Height;
        var count = (double)w * h;

        // 去均值模板（保留小数，不取整）。
        var template = new double[w * h];
        for (var i = 0; i < template.Length; i++)
        {
            template[i] = model.Pixels[i] - model.Mean;
        }

        var minDx = Math.Max(-radius, -roi.X);
        var maxDx = Math.Min(radius, frameWidth - w - roi.X);
        var minDy = Math.Max(-radius, -roi.Y);
        var maxDy = Math.Min(radius, frameHeight - h - roi.Y);

        var best = -1.0;
        var bestX = 0;
        var bestY = 0;

        for (var dy = minDy; dy <= maxDy; dy++)
        {
            var y0 = roi.Y + dy;
            for (var dx = minDx; dx <= maxDx; dx++)
            {
                var x0 = roi.X + dx;

                double product = 0;
                for (var y = 0; y < h; y++)
                {
                    var row = (y0 + y) * frameWidth + x0;
                    var templateRow = y * w;
                    for (var x = 0; x < w; x++)
                    {
                        product += gray[row + x] * template[templateRow + x];
                    }
                }

                var sum = RectSum(integral, frameWidth, x0, y0, w, h);
                var squared = RectSum(integralSq, frameWidth, x0, y0, w, h);
                var energy = squared - (sum * (double)sum / count);
                if (energy < 1.0)
                {
                    continue;
                }

                var score = product / Math.Sqrt(energy * model.Energy);
                if (score > best)
                {
                    best = score;
                    bestX = dx;
                    bestY = dy;
                }
            }
        }

        return (best, bestX, bestY);
    }

    private static long[] BuildIntegral(byte[] gray, Func<byte, long> selector)
    {
        const int width = ReporterStates.FrameWidth;
        const int height = ReporterStates.FrameHeight;

        var integral = new long[(width + 1) * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            long rowSum = 0;
            var sourceRow = y * width;
            var currentRow = (y + 1) * (width + 1);
            var previousRow = y * (width + 1);
            for (var x = 0; x < width; x++)
            {
                rowSum += selector(gray[sourceRow + x]);
                integral[currentRow + x + 1] = integral[previousRow + x + 1] + rowSum;
            }
        }

        return integral;
    }

    private static long RectSum(long[] integral, int width, int x, int y, int w, int h)
    {
        var stride = width + 1;
        var bottomRight = integral[((y + h) * stride) + x + w];
        var topRight = integral[(y * stride) + x + w];
        var bottomLeft = integral[((y + h) * stride) + x];
        var topLeft = integral[(y * stride) + x];
        return bottomRight - topRight - bottomLeft + topLeft;
    }

    private static TemplateModel Shift(TemplateModel model, int dx, int dy) => new()
    {
        Roi = new Roi(model.Roi.X + dx, model.Roi.Y + dy, model.Roi.Width, model.Roi.Height),
        Pixels = model.Pixels,
        Mean = model.Mean,
        Energy = model.Energy
    };

    /// <summary>
    /// 端到端回放：把帧序列喂给真实的 <see cref="RecognitionEngine"/>（含状态机），
    /// 输出逐帧状态、阶段计时估算和触发的事件时间线。
    ///
    /// 支持 --region：帧文件是原始画面的一个裁剪区域（省磁盘），回放时贴回全尺寸画布
    /// 再走生产归一化，保证与实机路径一致。
    ///
    /// 用法：replay &lt;帧目录&gt; &lt;宽&gt; &lt;高&gt; &lt;模板目录&gt; [--region x y w h] [--threshold 0.9]
    /// 帧时间戳从文件名开头的数字解析（秒）；没有数字时按序号 ×1 秒。
    /// </summary>
    private static int RunReplay(string[] args)
    {
        if (args.Length < 5)
        {
            PrintUsage();
            return 1;
        }

        var frameDirectory = args[1];
        var width = int.Parse(args[2]);
        var height = int.Parse(args[3]);
        var templateDirectory = args[4];

        var threshold = ReporterStates.DefaultThreshold;
        Roi? region = null;
        var concat = false;
        var intervalSeconds = 1.0;
        var probeNames = new List<string>();
        for (var i = 5; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--threshold":
                    threshold = double.Parse(args[i + 1]);
                    break;
                case "--concat":
                    concat = true;
                    break;
                case "--interval":
                    intervalSeconds = double.Parse(args[i + 1]);
                    break;
                case "--probe":
                    probeNames.AddRange(args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--region":
                    region = new Roi(
                        int.Parse(args[i + 1]),
                        int.Parse(args[i + 2]),
                        int.Parse(args[i + 3]),
                        int.Parse(args[i + 4]));
                    break;
            }
        }

        var frames = Directory.Exists(frameDirectory)
            ? Directory.GetFiles(frameDirectory, "*.bgra").OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : [frameDirectory];
        if (frames.Length == 0)
        {
            Console.WriteLine($"没有找到帧文件：{frameDirectory}");
            return 1;
        }

        // 逐帧分数探针：把指定模板单独打分，用于测量阶段边界（不参与状态机）。
        var probeModels = new Dictionary<string, TemplateModel>(StringComparer.Ordinal);
        foreach (var probeName in probeNames)
        {
            var probePath = Path.Combine(templateDirectory, probeName + ".krt");
            if (File.Exists(probePath))
            {
                try
                {
                    probeModels[probeName] = KrtTemplateStore.Load(probePath);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"[探针跳过] {probeName}：{exception.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[探针跳过] 找不到 {probeName}.krt");
            }
        }

        using var engine = new RecognitionEngine(templateDirectory);
        var events = new List<(TimeSpan Time, string EventId)>();
        var currentTime = TimeSpan.Zero;
        engine.AnnouncementRequested += (_, request) => events.Add((currentTime, request.EventId));

        Console.WriteLine($"回放 {frames.Length} 帧，阈值 {threshold:F2}，模板 {engine.LoadedTemplateCount} 个 + 变体 {engine.LoadedVariantCount} 个。");
        Console.WriteLine("时间(秒) | 状态 | 最高分 | 阶段估算 | 事件");
        Console.Out.Flush();

        var lastStateName = string.Empty;

        foreach (var (bytes, timestamp) in EnumerateFrames(frames, region, concat, intervalSeconds))
        {
            currentTime = timestamp;

            byte[] source;
            if (region is { } r)
            {
                var expected = checked(r.Width * r.Height * 4);
                if (bytes.Length < expected)
                {
                    Console.WriteLine($"[跳过] {timestamp.TotalSeconds:F0}s：区域字节数 {bytes.Length} 少于 {expected}。");
                    continue;
                }

                source = new byte[checked(width * height * 4)];
                for (var y = 0; y < r.Height; y++)
                {
                    Buffer.BlockCopy(
                        bytes,
                        y * r.Width * 4,
                        source,
                        ((r.Y + y) * width + r.X) * 4,
                        r.Width * 4);
                }
            }
            else
            {
                if (bytes.Length < checked(width * height * 4))
                {
                    Console.WriteLine($"[跳过] {timestamp.TotalSeconds:F0}s：字节数不足。");
                    continue;
                }

                source = bytes;
            }

            var normalized = FrameProcessing.Normalize(source, width, height);

            if (probeNames.Count > 0)
            {
                var probeGray = FrameProcessing.ToGrayscale(normalized, ReporterStates.FrameWidth, ReporterStates.FrameHeight);
                var parts = new List<string> { $"{timestamp.TotalSeconds,7:F0}s" };
                foreach (var probeName in probeNames)
                {
                    var model = probeModels.GetValueOrDefault(probeName);
                    parts.Add(model is null ? "?" : $"{probeName}={FrameProcessing.Score(model, probeGray):F3}");
                }

                Console.WriteLine(string.Join("  ", parts));
            }

            var frame = new CapturedFrame(normalized, ReporterStates.FrameWidth, ReporterStates.FrameHeight, timestamp);

            var eventCountBefore = events.Count;
            var result = engine.Process(frame, threshold);

            var stateName = result.StateId is null ? "（未识别）" : ReporterStates.GetDisplayName(result.StateId.Value);
            var changed = stateName != lastStateName;
            lastStateName = stateName;

            var newEvents = events.Skip(eventCountBefore).Select(e => ReporterStates.GetEventDisplayName(e.EventId)).ToArray();
            var timing = result.EstimatedPhaseRemaining is { } remaining
                ? $"{result.PhaseTimingLabel} 剩 {remaining.TotalSeconds:F0}s"
                : string.Empty;

            if (changed || newEvents.Length > 0)
            {
                var marker = changed ? "★" : " ";
                Console.WriteLine(
                    $"{marker}{timestamp.TotalSeconds,7:F0}s | {stateName,-10} | 第{result.RoundNumber}回合 阵营{result.Side} | {result.BestScore:F3} | {timing} | {string.Join("、", newEvents)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 事件时间线 ===");
        foreach (var (time, eventId) in events)
        {
            Console.WriteLine($"  {time.TotalSeconds,7:F0}s  {ReporterStates.GetEventDisplayName(eventId)}（{eventId}）");
        }

        return 0;
    }

    /// <summary>
    /// 枚举帧序列：(原始区域字节, 时间戳)。
    /// 目录模式按文件名排序并解析文件名里的秒数；--concat 模式读取单个大文件里按顺序拼接的帧
    /// （ffmpeg -f rawvideo 会这样输出），时间戳 = 序号 × 间隔。
    /// </summary>
    private static IEnumerable<(byte[] Bytes, TimeSpan Timestamp)> EnumerateFrames(
        string[] frames,
        Roi? region,
        bool concat,
        double intervalSeconds)
    {
        if (concat)
        {
            if (region is not { } r)
            {
                throw new InvalidOperationException("--concat 需要同时指定 --region。");
            }

            var frameBytes = checked(r.Width * r.Height * 4);
            if (frames.Length != 1)
            {
                throw new InvalidOperationException("--concat 只接受单个文件。");
            }

            using var stream = File.OpenRead(frames[0]);
            var total = stream.Length / frameBytes;
            var buffer = new byte[frameBytes];
            for (long i = 0; i < total; i++)
            {
                var read = stream.Read(buffer, 0, frameBytes);
                if (read < frameBytes)
                {
                    yield break;
                }

                yield return (buffer.ToArray(), TimeSpan.FromSeconds(i * intervalSeconds));
            }

            yield break;
        }

        var current = TimeSpan.Zero;
        foreach (var framePath in frames)
        {
            var bytes = File.ReadAllBytes(framePath);
            var timestamp = ParseTimestamp(framePath, current);
            current = timestamp;
            yield return (bytes, timestamp);
        }
    }

    private static TimeSpan ParseTimestamp(string path, TimeSpan fallback)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = new string(name.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : fallback + TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// 从原始帧提取模板：按生产管线归一化，再按给定 ROI 裁出灰度像素写成 KRT 文件。
    /// 用于从指定分辨率的录像里制作该分辨率专用的模板变体。
    ///
    /// 用法：mktemplate &lt;原始帧&gt; &lt;宽&gt; &lt;高&gt; &lt;x&gt; &lt;y&gt; &lt;ROI宽&gt; &lt;ROI高&gt; &lt;输出.krt&gt;
    /// </summary>
    private static int RunMakeTemplate(string[] args)
    {
        if (args.Length < 9)
        {
            PrintUsage();
            return 1;
        }

        var width = int.Parse(args[2]);
        var height = int.Parse(args[3]);
        var roi = new Roi(int.Parse(args[4]), int.Parse(args[5]), int.Parse(args[6]), int.Parse(args[7]));
        var outputPath = args[8];

        if (!KrtTemplateStore.IsValidRoi(roi))
        {
            Console.WriteLine($"ROI 无效：{roi.X},{roi.Y},{roi.Width},{roi.Height}");
            return 1;
        }

        var source = File.ReadAllBytes(args[1]);
        if (source.Length < checked(width * height * 4))
        {
            Console.WriteLine("帧数据不完整。");
            return 1;
        }

        var normalized = FrameProcessing.Normalize(source, width, height);
        var gray = FrameProcessing.ToGrayscale(normalized, ReporterStates.FrameWidth, ReporterStates.FrameHeight);

        var pixels = new byte[roi.Width * roi.Height];
        for (var y = 0; y < roi.Height; y++)
        {
            Buffer.BlockCopy(
                gray,
                ((roi.Y + y) * ReporterStates.FrameWidth) + roi.X,
                pixels,
                y * roi.Width,
                roi.Width);
        }

        double sum = 0;
        foreach (var value in pixels)
        {
            sum += value;
        }

        var mean = sum / pixels.Length;
        double squared = 0;
        foreach (var value in pixels)
        {
            squared += (value - mean) * (value - mean);
        }

        KrtTemplateStore.Save(outputPath, new TemplateModel
        {
            Roi = roi,
            Pixels = pixels,
            Mean = mean,
            Energy = squared
        });

        Console.WriteLine($"已写入 {outputPath}：ROI {roi.X},{roi.Y},{roi.Width},{roi.Height}，能量/像素 {squared / pixels.Length:F1}");
        return 0;
    }

    /// <summary>
    /// 对齐诊断：两张同尺寸灰度图，在亚像素范围内搜索最佳 ZNCC 对齐，
    /// 判断差异是"整体偏移/相位"还是"尺度/字形不同"。
    ///
    /// 用法：compare &lt;灰度文件A&gt; &lt;灰度文件B&gt; &lt;宽&gt; &lt;高&gt; [步长]
    /// </summary>
    private static int RunCompare(string[] args)
    {
        if (args.Length < 5)
        {
            PrintUsage();
            return 1;
        }

        var width = int.Parse(args[3]);
        var height = int.Parse(args[4]);
        var step = args.Length > 5 ? double.Parse(args[5]) : 0.125;

        var a = File.ReadAllBytes(args[1]);
        var b = File.ReadAllBytes(args[2]);

        // B 采样函数：以双线性在 (x+dx, y+dy) 取值。
        double SampleB(double x, double y)
        {
            var x0 = (int)Math.Floor(x);
            var y0 = (int)Math.Floor(y);
            var fx = x - x0;
            var fy = y - y0;
            double At(int px, int py) => b[Math.Clamp(py, 0, height - 1) * width + Math.Clamp(px, 0, width - 1)];
            var top = At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx;
            var bottom = At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx;
            return top * (1 - fy) + bottom * fy;
        }

        double meanA = 0;
        foreach (var value in a) meanA += value;
        meanA /= a.Length;

        var best = -2.0;
        var bestDx = 0.0;
        var bestDy = 0.0;
        var results = new List<(double Score, double Dx, double Dy)>();

        for (var dy = -2.0; dy <= 2.0001; dy += step)
        {
            for (var dx = -2.0; dx <= 2.0001; dx += step)
            {
                double sum = 0, squared = 0, product = 0;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var value = SampleB(x + dx, y + dy);
                        sum += value;
                        squared += value * value;
                        product += value * (a[(y * width) + x] - meanA);
                    }
                }

                var count = (double)width * height;
                var energy = squared - (sum * sum / count);
                if (energy < 1.0)
                {
                    continue;
                }

                var meanB = sum / count;
                double energyA = 0;
                foreach (var value in a)
                {
                    energyA += (value - meanA) * (value - meanA);
                }

                var score = product / Math.Sqrt(energy * energyA);
                results.Add((score, dx, dy));
                if (score > best)
                {
                    best = score;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }

        Console.WriteLine($"最佳亚像素对齐：score={best:F6} at dx={bestDx:F3}, dy={bestDy:F3}（步长 {step}）");

        // 缩放搜索：判断是否还存在整体尺度差异（平移对齐解决不了的）。
        var bestScale = 1.0;
        var bestScaleScore = -2.0;
        var bestScaleDx = 0.0;
        var bestScaleDy = 0.0;
        var centerX = (width - 1) / 2.0;
        var centerY = (height - 1) / 2.0;
        for (var scale = 0.95; scale <= 1.0501; scale += 0.005)
        {
            for (var dy = -1.0; dy <= 1.0001; dy += 0.25)
            {
                for (var dx = -1.0; dx <= 1.0001; dx += 0.25)
                {
                    double sum = 0, squared = 0, product = 0;
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var value = SampleB(
                                ((x - centerX) * scale) + centerX + dx,
                                ((y - centerY) * scale) + centerY + dy);
                            sum += value;
                            squared += value * value;
                            product += value * (a[(y * width) + x] - meanA);
                        }
                    }

                    var count = (double)width * height;
                    var energy = squared - (sum * sum / count);
                    if (energy < 1.0)
                    {
                        continue;
                    }

                    double energyA = 0;
                    foreach (var value in a)
                    {
                        energyA += (value - meanA) * (value - meanA);
                    }

                    var score = product / Math.Sqrt(energy * energyA);
                    if (score > bestScaleScore)
                    {
                        bestScaleScore = score;
                        bestScale = scale;
                        bestScaleDx = dx;
                        bestScaleDy = dy;
                    }
                }
            }
        }

        Console.WriteLine($"含缩放搜索：score={bestScaleScore:F6} at scale={bestScale:F3}, dx={bestScaleDx:F3}, dy={bestScaleDy:F3}");
        Console.WriteLine("前 5 名：");
        foreach (var (score, dx, dy) in results.OrderByDescending(item => item.Score).Take(5))
        {
            Console.WriteLine($"  score={score:F6} dx={dx:+0.000;-0.000;0.000} dy={dy:+0.000;-0.000;0.000}");
        }

        // 行列亮度轮廓对比（前 12 个峰值位置），用于看笔画位置是否一致。
        static void PrintProfile(string label, byte[] data, int width, int height, bool columns)
        {
            var count = columns ? width : height;
            var profile = new double[count];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    profile[columns ? x : y] += data[(y * width) + x];
                }
            }

            Console.Write($"  {label}: ");
            for (var i = 0; i < count; i++)
            {
                Console.Write($"{profile[i]:F0} ");
            }

            Console.WriteLine();
        }

        PrintProfile("A 列轮廓", a, width, height, true);
        PrintProfile("B 列轮廓", b, width, height, true);
        PrintProfile("A 行轮廓", a, width, height, false);
        PrintProfile("B 行轮廓", b, width, height, false);
        return 0;
    }

    /// <summary>
    /// 自检：随机帧 + 随机模板，对比"快速搜索 + 核心复验"与穷举核心打分，确保两者一致。
    /// </summary>
    private static int RunSelfCheck()
    {
        var random = new Random(20261003);
        var gray = new byte[ReporterStates.FrameWidth * ReporterStates.FrameHeight];
        random.NextBytes(gray);

        var roi = new Roi(400, 300, 96, 48);
        var pixels = new byte[roi.Width * roi.Height];
        for (var y = 0; y < roi.Height; y++)
        {
            Buffer.BlockCopy(
                gray,
                ((roi.Y + y) * ReporterStates.FrameWidth) + roi.X,
                pixels,
                y * roi.Width,
                roi.Width);
        }

        double sum = 0;
        foreach (var value in pixels)
        {
            sum += value;
        }

        var mean = sum / pixels.Length;
        double squared = 0;
        foreach (var value in pixels)
        {
            squared += (value - mean) * (value - mean);
        }

        var model = new TemplateModel { Roi = roi, Pixels = pixels, Mean = mean, Energy = squared };

        const int radius = 12;
        var integral = BuildIntegral(gray, static value => value);
        var integralSq = BuildIntegral(gray, static value => (long)value * value);
        var fast = BestMatch(model, gray, radius, integral, integralSq);

        var best = -1.0;
        double bestX = 0;
        double bestY = 0;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (!KrtTemplateStore.IsValidRoi(new Roi(roi.X + dx, roi.Y + dy, roi.Width, roi.Height)))
                {
                    continue;
                }

                var details = FrameProcessing.ScoreDetailed(Shift(model, dx, dy), gray);
                if (details.BestScore > best)
                {
                    best = details.BestScore;
                    bestX = dx + details.BestOffsetX;
                    bestY = dy + details.BestOffsetY;
                }
            }
        }

        Console.WriteLine($"快速搜索+复验: score={fast.Score:F10} offset=({fast.OffsetX},{fast.OffsetY})");
        Console.WriteLine($"穷举核心:      score={best:F10} offset=({bestX},{bestY})");
        var ok = Math.Abs(fast.Score - best) < 0.0001;
        Console.WriteLine(ok ? "自检通过：两者一致。" : "自检失败：结果不一致！");
        return ok ? 0 : 1;
    }

    private static List<(string Name, TemplateModel Model)> LoadTemplates(string directory)
    {
        var templates = new List<(string, TemplateModel)>();
        foreach (var path in Directory.GetFiles(directory, "*.krt").OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                templates.Add((Path.GetFileNameWithoutExtension(path), KrtTemplateStore.Load(path)));
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[跳过] {Path.GetFileName(path)}：{exception.Message}");
            }
        }

        return templates;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            用法：
              KanamiReporter.ReplayAnalyzer analyze <帧目录或单个帧文件> <宽> <高> <模板目录> [选项]
              KanamiReporter.ReplayAnalyzer selfcheck

            选项：
              --search 半径   在模板 ROI 周围 ±半径 像素内搜最佳偏移（默认 0，只做内置的 ±1 精修）
              --dump 目录     把归一化后的 1920x1080 BGRA 帧写到该目录，便于转成图片查看
              --min 分值      只显示不低于该分数的模板（默认 0）
            """);
    }
}
