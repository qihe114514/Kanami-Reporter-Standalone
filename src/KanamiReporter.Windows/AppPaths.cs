namespace KanamiReporter.Windows;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KanamiReporter");
        Templates = Path.Combine(Root, "templates");
        Voices = Path.Combine(Root, "voices");
        Logs = Path.Combine(Root, "logs");
        SettingsFile = Path.Combine(Root, "config.json");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Templates);
        Directory.CreateDirectory(Voices);
        Directory.CreateDirectory(Logs);
    }

    public string Root { get; }
    public string Templates { get; }
    public string Voices { get; }
    public string Logs { get; }
    public string SettingsFile { get; }
}
