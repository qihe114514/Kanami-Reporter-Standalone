using System.Text;

namespace KanamiReporter.Windows;

public interface IAppLogger
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class FileLogger : IAppLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly string _logFile;
    private readonly StreamWriter _writer;

    public FileLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFile = Path.Combine(logDirectory, $"kanami-{DateTime.Now:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(_logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public string LogFile => _logFile;

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            _writer.WriteLine($"{DateTimeOffset.Now:O} [{level}] {message}");
        }
    }
}
