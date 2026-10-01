using System.Buffers.Binary;

namespace KanamiReporter.Core;

public static class KrtTemplateStore
{
    private const uint Magic = 0x3154524B;
    private const uint Version = 1;
    private const int HeaderBytes = 32;

    public static bool IsValidRoi(Roi roi)
    {
        return roi.X >= 0 && roi.Y >= 0 &&
               roi.Width >= 4 && roi.Height >= 4 &&
               roi.X + roi.Width <= ReporterStates.FrameWidth &&
               roi.Y + roi.Height <= ReporterStates.FrameHeight &&
               (long)roi.Width * roi.Height <= ReporterStates.MaxTemplatePixels;
    }

    public static TemplateModel Load(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[HeaderBytes];
        if (stream.Read(header, 0, header.Length) != header.Length)
        {
            throw new InvalidDataException("KRT 文件头不完整。");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        var width = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        var roi = new Roi(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28, 4)));

        if (magic != Magic || version != Version ||
            width != ReporterStates.FrameWidth || height != ReporterStates.FrameHeight ||
            !IsValidRoi(roi))
        {
            throw new InvalidDataException("KRT 文件格式或分辨率不兼容。");
        }

        var count = roi.Width * roi.Height;
        var pixels = new byte[count];
        if (stream.Read(pixels, 0, pixels.Length) != pixels.Length)
        {
            throw new InvalidDataException("KRT 像素数据不完整。");
        }

        var statistics = CalculateStatistics(roi, pixels);
        if (statistics.Energy / count < 16.0)
        {
            throw new InvalidDataException("模板区域过于均匀，无法可靠匹配。");
        }

        return new TemplateModel
        {
            Roi = roi,
            Pixels = pixels,
            Mean = statistics.Mean,
            Energy = statistics.Energy
        };
    }

    public static void Save(string path, TemplateModel model)
    {
        if (model.Pixels.Length != model.Roi.Width * model.Roi.Height || !IsValidRoi(model.Roi))
        {
            throw new ArgumentException("模板识别区域与像素尺寸不匹配。", nameof(model));
        }

        var statistics = CalculateStatistics(model.Roi, model.Pixels);
        if (statistics.Energy / model.Pixels.Length < 16.0)
        {
            throw new InvalidDataException("模板区域过于均匀，无法可靠匹配。");
        }

        using var stream = File.Create(path);
        Span<byte> header = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], ReporterStates.FrameWidth);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], ReporterStates.FrameHeight);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], (uint)model.Roi.X);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], (uint)model.Roi.Y);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], (uint)model.Roi.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..32], (uint)model.Roi.Height);
        stream.Write(header);
        stream.Write(model.Pixels);
    }

    private static (double Mean, double Energy) CalculateStatistics(Roi roi, byte[] pixels)
    {
        double sum = 0;
        double squared = 0;
        var count = roi.Width * roi.Height;
        for (var i = 0; i < count; i++)
        {
            var value = pixels[i];
            sum += value;
            squared += value * value;
        }

        var mean = sum / count;
        var energy = squared - sum * sum / count;
        return (mean, energy);
    }
}
