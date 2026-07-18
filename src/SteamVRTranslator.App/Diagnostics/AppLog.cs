using System.Diagnostics;

namespace SteamVRTranslator.App.Diagnostics;

public sealed class AppLog
{
    private readonly object _sync = new();

    public AppLog(string dataDirectory)
    {
        DirectoryPath = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(DirectoryPath);
        FilePath = Path.Combine(DirectoryPath, $"steamvr-translator-{DateTime.Now:yyyyMMdd}.log");
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public event EventHandler<string>? MessageWritten;

    public void Info(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        lock (_sync)
        {
            File.AppendAllText(FilePath, line + Environment.NewLine);
        }

        Console.WriteLine(line);
        Debug.WriteLine(line);
        MessageWritten?.Invoke(this, line);
    }
}
