using System.Diagnostics;
using System.Text;

namespace AutoPower.Windows;

public sealed class TechnicalLogger
{
    private static readonly object GlobalGate = new();
    private readonly string _directory;

    public TechnicalLogger(string? directory = null)
    {
        _directory = directory ?? AppPaths.LogDirectory;
        Directory.CreateDirectory(_directory);
        RemoveExpiredLogs();
    }

    public void Information(string eventName, string detail) => Write("INFO", eventName, detail, null);
    public void Warning(string eventName, string detail) => Write("WARN", eventName, detail, null);
    public void Error(string eventName, Exception exception, string? detail = null) =>
        Write("ERROR", eventName, detail ?? exception.Message, exception);

    private void Write(string level, string eventName, string detail, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Append('\t').Append(level)
            .Append('\t').Append(Sanitize(eventName))
            .Append('\t').Append(Sanitize(detail));
        if (exception is not null)
        {
            line.Append('\t').Append(Sanitize(exception.ToString()));
        }

        lock (GlobalGate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(
                    Path.Combine(_directory, $"autopower-{DateTime.Today:yyyy-MM-dd}.log"),
                    line.AppendLine().ToString(),
                    Encoding.UTF8);
            }
            catch (IOException error)
            {
                Debug.WriteLine($"eslee Auto Power technical log write failed: {error.Message}");
            }
            catch (UnauthorizedAccessException error)
            {
                Debug.WriteLine($"eslee Auto Power technical log access failed: {error.Message}");
            }
        }
    }

    private void RemoveExpiredLogs()
    {
        var files = Directory.EnumerateFiles(_directory, "autopower-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(7);
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A log currently held by another app instance is retained until a later cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Logging must never block application startup; the failure is visible in the folder.
            }
        }
    }

    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
