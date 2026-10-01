namespace KanamiReporter.Core;

public sealed class TemplateModel
{
    public Roi Roi { get; init; }
    public byte[] Pixels { get; init; } = [];
    public double Mean { get; init; }
    public double Energy { get; init; }
}
