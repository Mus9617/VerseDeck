using System.IO;

namespace VerseDeck.App.Services;

public sealed class DebugLog : IDebugLog
{
    private readonly object _gate = new();

    public DebugLog(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                File.AppendAllText(Path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Debug logging must never break input execution.
        }
    }
}
