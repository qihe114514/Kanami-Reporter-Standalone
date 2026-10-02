using System.IO.Compression;
using System.Text;

namespace KanamiReporter.CaptureDiag;

/// <summary>同时写控制台和日志文件：用户可以一边看进度，一边把日志原样发回来。</summary>
internal sealed class DiagLog : IDisposable
{
    private readonly StreamWriter _writer;

    public DiagLog(string path)
    {
        Path = path;
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(true)) { AutoFlush = true };
    }

    public string Path { get; }

    public void Line(string text = "")
    {
        Console.WriteLine(text);
        _writer.WriteLine(text);
    }

    public void Section(string title) => Line($"{Environment.NewLine}===== {title} =====");

    public void Problem(string text)
    {
        Console.WriteLine(text);
        _writer.WriteLine(text);
        Console.Error.WriteLine(text);
    }

    public void Dispose() => _writer.Dispose();
}

internal readonly record struct FrameStats(double AverageLuminance, double NonBlackRatio, int MaxComponent);

internal static class FrameAnalysis
{
    /// <summary>抽样统计亮度：黑屏、纯色、没内容都会在这里露出来。</summary>
    public static FrameStats Analyze(byte[] buffer, int width, int height, int stride, int bytesPerPixel, bool floatingPoint)
    {
        if (width <= 0 || height <= 0 || bytesPerPixel <= 0 || stride < width * bytesPerPixel)
        {
            return new FrameStats(0, 0, 0);
        }

        var rowStep = Math.Max(1, height / 64);
        var columnStep = Math.Max(1, width / 128);
        double sum = 0;
        var samples = 0;
        var nonBlack = 0;
        var maximum = 0;

        for (var y = 0; y < height; y += rowStep)
        {
            var row = y * stride;
            for (var x = 0; x < width; x += columnStep)
            {
                var offset = row + (x * bytesPerPixel);
                var luminance = floatingPoint
                    ? FloatLuminance(buffer, offset)
                    : ByteLuminance(buffer, offset);
                sum += luminance;
                samples++;
                if (luminance > 8)
                {
                    nonBlack++;
                }

                maximum = Math.Max(maximum, (int)Math.Min(255, Math.Round(luminance)));
            }
        }

        return samples == 0
            ? new FrameStats(0, 0, 0)
            : new FrameStats(sum / samples, (double)nonBlack / samples, maximum);
    }

    /// <summary>把采集到的原始像素统一转成紧凑的 BGRA8，便于存快照和比较两帧差异。</summary>
    public static byte[] ToBgra8(byte[] buffer, int width, int height, int stride, int bytesPerPixel, bool floatingPoint)
    {
        var destination = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = y * stride;
            var destinationRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var source = sourceRow + (x * bytesPerPixel);
                var target = destinationRow + (x * 4);
                if (floatingPoint)
                {
                    destination[target] = ToSrgbByte(HalfToFloat(buffer, source + 4));
                    destination[target + 1] = ToSrgbByte(HalfToFloat(buffer, source + 2));
                    destination[target + 2] = ToSrgbByte(HalfToFloat(buffer, source));
                    destination[target + 3] = 255;
                }
                else
                {
                    destination[target] = buffer[source];
                    destination[target + 1] = buffer[source + 1];
                    destination[target + 2] = buffer[source + 2];
                    destination[target + 3] = 255;
                }
            }
        }

        return destination;
    }

    /// <summary>两帧的平均亮度差：接近 0 说明画面是冻结的（有帧但没有变化）。</summary>
    public static double Difference(byte[] left, byte[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        if (length == 0)
        {
            return 0;
        }

        double sum = 0;
        var samples = 0;
        var step = Math.Max(4, (length / 4000) / 4 * 4);
        for (var i = 0; i + 3 < length; i += step)
        {
            sum += Math.Abs(ByteLuminance(left, i) - ByteLuminance(right, i));
            samples++;
        }

        return samples == 0 ? 0 : sum / samples;
    }

    private static double ByteLuminance(byte[] buffer, int offset) =>
        (buffer[offset] + buffer[offset + 1] + buffer[offset + 2]) / 3.0;

    private static double FloatLuminance(byte[] buffer, int offset) =>
        (ToDisplayValue(HalfToFloat(buffer, offset)) +
         ToDisplayValue(HalfToFloat(buffer, offset + 2)) +
         ToDisplayValue(HalfToFloat(buffer, offset + 4))) / 3.0 * 255;

    private static float HalfToFloat(byte[] buffer, int offset) =>
        (float)BitConverter.UInt16BitsToHalf((ushort)(buffer[offset] | (buffer[offset + 1] << 8)));

    /// <summary>
    /// HDR 采集给到的是线性 scRGB 值，直接乘 255 会比肉眼看到的暗很多，
    /// 也会让"平均亮度"与 BGRA8 路径没法互相比较，所以先按 sRGB 曲线编码一次。
    /// </summary>
    private static double ToDisplayValue(float linear) =>
        linear switch
        {
            <= 0 => 0,
            >= 1 => 1,
            _ => linear <= 0.0031308f ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055
        };

    private static byte ToSrgbByte(float linear) => (byte)Math.Clamp((int)Math.Round(ToDisplayValue(linear) * 255), 0, 255);
}

/// <summary>手写 PNG 编码（zlib 用 BCL 的 ZLibStream）：不想为一个诊断工具再引第三方图像库。</summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static uint[]? _crcTable;

    public static void Write(string path, byte[] bgra, int width, int height)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.Write(Signature);

        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;   // 位深
        header[9] = 2;   // 颜色类型：真彩色 RGB
        header[10] = 0;  // 压缩方式
        header[11] = 0;  // 过滤方式
        header[12] = 0;  // 隔行扫描
        WriteChunk(file, "IHDR", header);

        // 每行前面加一个过滤器字节（0 = 不过滤），转成 RGB 去掉 alpha。
        var raw = new byte[height * ((width * 3) + 1)];
        var cursor = 0;
        for (var y = 0; y < height; y++)
        {
            raw[cursor++] = 0;
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                raw[cursor++] = bgra[row + (x * 4) + 2];
                raw[cursor++] = bgra[row + (x * 4) + 1];
                raw[cursor++] = bgra[row + (x * 4)];
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)crc));
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var table = _crcTable ??= BuildCrcTable();
        var crc = 0xFFFFFFFFu;
        foreach (var value in first)
        {
            crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (var value in second)
        {
            crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
