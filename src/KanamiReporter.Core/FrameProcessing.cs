namespace KanamiReporter.Core;

public static class FrameProcessing
{
    /// <summary>
    /// 把任意尺寸的采集帧归一化成 1920×1080 的识别基准。
    ///
    /// 缩放以画面宽度为基准：游戏 HUD 的像素尺寸与横向锚定跟随画面宽度，
    /// 按宽度缩放能让 HUD 在归一化后落到与模板一致的位置。
    ///
    /// - 高度不超过 16:9（16:9、21:9 等）：缩放后垂直居中，上下补黑边（等同原行为）。
    /// - 高度超过 16:9（16:10、4:3 等）：缩放后高度超出 1080，保留顶部、裁掉底部。
    ///   16:10 实机录像验证过：HUD 仍按原生像素、原生位置渲染（顶部对齐），
    ///   裁掉下沿不会影响靠上、居中锚定的识别区域，因此 16:9 模板可以继续使用。
    /// </summary>
    public static byte[] Normalize(ReadOnlySpan<byte> sourceBgra, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            sourceBgra.Length < checked(sourceWidth * sourceHeight * 4))
        {
            throw new ArgumentException("源帧尺寸与像素数据不匹配。", nameof(sourceBgra));
        }

        var destination = new byte[ReporterStates.FrameBytes];
        Array.Fill(destination, (byte)0);

        var scale = ReporterStates.FrameWidth / (double)sourceWidth;
        var outputWidth = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, ReporterStates.FrameWidth);
        var outputHeight = (int)Math.Round(sourceHeight * scale);
        var visibleHeight = Math.Min(outputHeight, ReporterStates.FrameHeight);
        var offsetX = (ReporterStates.FrameWidth - outputWidth) / 2;
        var offsetY = outputHeight > ReporterStates.FrameHeight
            ? 0
            : (ReporterStates.FrameHeight - outputHeight) / 2;

        for (var y = 0; y < visibleHeight; y++)
        {
            var sourceY = Math.Clamp((y + 0.5) / scale - 0.5, 0, sourceHeight - 1);
            var y0 = (int)Math.Floor(sourceY);
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var fy = sourceY - y0;

            for (var x = 0; x < outputWidth; x++)
            {
                var sourceX = Math.Clamp((x + 0.5) / scale - 0.5, 0, sourceWidth - 1);
                var x0 = (int)Math.Floor(sourceX);
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var fx = sourceX - x0;

                var p00 = ((y0 * sourceWidth) + x0) * 4;
                var p10 = ((y0 * sourceWidth) + x1) * 4;
                var p01 = ((y1 * sourceWidth) + x0) * 4;
                var p11 = ((y1 * sourceWidth) + x1) * 4;
                var destinationIndex = (((y + offsetY) * ReporterStates.FrameWidth) + x + offsetX) * 4;

                for (var channel = 0; channel < 4; channel++)
                {
                    var top = sourceBgra[p00 + channel] * (1 - fx) + sourceBgra[p10 + channel] * fx;
                    var bottom = sourceBgra[p01 + channel] * (1 - fx) + sourceBgra[p11 + channel] * fx;
                    destination[destinationIndex + channel] = (byte)Math.Clamp(
                        Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                }

                destination[destinationIndex + 3] = 255;
            }
        }

        return destination;
    }

    public static byte[] ToGrayscale(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var expected = checked(width * height * 4);
        if (bgra.Length < expected)
        {
            throw new ArgumentException("帧像素数据不完整。", nameof(bgra));
        }

        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var pixel = i * 4;
            gray[i] = (byte)((29U * bgra[pixel] + 150U * bgra[pixel + 1] + 77U * bgra[pixel + 2]) >> 8);
        }

        return gray;
    }

    public static double Score(TemplateModel model, ReadOnlySpan<byte> gray)
    {
        return ScoreDetailed(model, gray).BestScore;
    }

    /// <summary>
    /// 匹配位置：整数与半像素都要试。
    ///
    /// 半像素位置是为跨分辨率/跨宽高比准备的：游戏在不同画面尺寸下会把 HUD
    /// 放在小数像素上（16:10 实机录像实测偏移约 0.25–0.5px），只搜整数位置时
    /// 相关系数会从 0.95 掉到 0.85 上下，刚好落在阈值以下。
    /// </summary>
    private static readonly IReadOnlyList<(double X, double Y)> MatchOffsets =
    [
        (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1),
        (-0.5, 0), (0.5, 0), (0, -0.5), (0, 0.5),
        (-0.5, -0.5), (-0.5, 0.5), (0.5, -0.5), (0.5, 0.5)
    ];

    public static TemplateMatchDetails ScoreDetailed(TemplateModel model, ReadOnlySpan<byte> gray)
    {
        var roi = model.Roi;
        var count = roi.Width * roi.Height;
        var best = -1.0;
        double bestOffsetX = 0;
        double bestOffsetY = 0;
        var details = new List<MatchOffsetDetail>(MatchOffsets.Count);

        foreach (var (offsetX, offsetY) in MatchOffsets)
        {
            var x0 = roi.X + offsetX;
            var y0 = roi.Y + offsetY;
            if (x0 < 0 || y0 < 0 ||
                x0 + roi.Width > ReporterStates.FrameWidth ||
                y0 + roi.Height > ReporterStates.FrameHeight)
            {
                continue;
            }

            double sum = 0;
            double squared = 0;
            double product = 0;
            var index = 0;
            var isInteger = offsetX == Math.Floor(offsetX) && offsetY == Math.Floor(offsetY);

            for (var y = 0; y < roi.Height; y++)
            {
                var row = ((int)y0 + y) * ReporterStates.FrameWidth;
                for (var x = 0; x < roi.Width; x++, index++)
                {
                    double value = isInteger
                        ? gray[row + (int)x0 + x]
                        : SampleBilinear(gray, x0 + x, y0 + y);
                    sum += value;
                    squared += value * value;
                    product += value * (model.Pixels[index] - model.Mean);
                }
            }

            var energy = squared - sum * sum / count;
            if (energy < 1.0)
            {
                details.Add(new MatchOffsetDetail(offsetX, offsetY, sum, squared, product, energy, -1.0));
                continue;
            }

            var score = product / Math.Sqrt(energy * model.Energy);
            details.Add(new MatchOffsetDetail(offsetX, offsetY, sum, squared, product, energy, score));
            if (score > best)
            {
                best = score;
                bestOffsetX = offsetX;
                bestOffsetY = offsetY;
            }
        }

        return new TemplateMatchDetails(best, bestOffsetX, bestOffsetY, details);
    }

    /// <summary>双线性采样（边界截断），用于半像素匹配位置。</summary>
    private static double SampleBilinear(ReadOnlySpan<byte> gray, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var x1 = Math.Min(x0 + 1, ReporterStates.FrameWidth - 1);
        var y1 = Math.Min(y0 + 1, ReporterStates.FrameHeight - 1);

        var topRow = y0 * ReporterStates.FrameWidth;
        var bottomRow = y1 * ReporterStates.FrameWidth;
        var top = gray[topRow + x0] * (1 - fx) + gray[topRow + x1] * fx;
        var bottom = gray[bottomRow + x0] * (1 - fx) + gray[bottomRow + x1] * fx;
        return top * (1 - fy) + bottom * fy;
    }
}
