namespace KanamiReporter.Core;

public static class FrameProcessing
{
    public static byte[] Normalize(ReadOnlySpan<byte> sourceBgra, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            sourceBgra.Length < checked(sourceWidth * sourceHeight * 4))
        {
            throw new ArgumentException("源帧尺寸与像素数据不匹配。", nameof(sourceBgra));
        }

        var destination = new byte[ReporterStates.FrameBytes];
        Array.Fill(destination, (byte)0);

        var scale = Math.Min(
            ReporterStates.FrameWidth / (double)sourceWidth,
            ReporterStates.FrameHeight / (double)sourceHeight);
        var outputWidth = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, ReporterStates.FrameWidth);
        var outputHeight = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, ReporterStates.FrameHeight);
        var offsetX = (ReporterStates.FrameWidth - outputWidth) / 2;
        var offsetY = (ReporterStates.FrameHeight - outputHeight) / 2;

        for (var y = 0; y < outputHeight; y++)
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

    public static TemplateMatchDetails ScoreDetailed(TemplateModel model, ReadOnlySpan<byte> gray)
    {
        ReadOnlySpan<(int X, int Y)> offsets =
        [
            (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1)
        ];

        var roi = model.Roi;
        var count = roi.Width * roi.Height;
        var best = -1.0;
        var bestOffsetX = 0;
        var bestOffsetY = 0;
        var details = new List<MatchOffsetDetail>(offsets.Length);

        foreach (var (offsetX, offsetY) in offsets)
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

            for (var y = 0; y < roi.Height; y++)
            {
                var row = (y0 + y) * ReporterStates.FrameWidth + x0;
                for (var x = 0; x < roi.Width; x++, index++)
                {
                    double value = gray[row + x];
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
}
