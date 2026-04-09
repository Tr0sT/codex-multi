using System.Text;

namespace CodexMulti.Infrastructure;

internal sealed class AppLogger
{
    private readonly object _sync = new();
    private readonly string _logsDirectory;

    public AppLogger(string logsDirectory)
    {
        _logsDirectory = logsDirectory;
    }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            FilePermissions.TrySetOwnerOnlyDirectory(_logsDirectory);

            var logFilePath = Path.Combine(_logsDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}{Environment.NewLine}";
            lock (_sync)
            {
                File.AppendAllText(logFilePath, line, TextEncodings.Utf8NoBom);
            }

            FilePermissions.TrySetOwnerOnly(logFilePath);
        }
        catch
        {
        }
    }
}
