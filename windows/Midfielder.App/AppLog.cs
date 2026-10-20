using System.Globalization;
using System.Text;

namespace Midfielder.App;

internal sealed record LogEntry(DateTime Time, bool IsError, string Message);

/// <summary>
/// Small diagnostic log at %LOCALAPPDATA%\Midfielder\midfielder.log, plus the last <see cref="MaxEntries"/>
/// entries in memory for the Developer page. Bounded: rotates to midfielder.log.old at 1 MB, so it can never fill
/// the disk. No clipboard contents are written, only kinds and sizes.
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    public const int MaxEntries = 500;
    private static readonly object Gate = new();
    private static readonly LinkedList<LogEntry> Recent = new();

    /// <summary>Raised (on the logging thread) after each entry.</summary>
    public static event Action<LogEntry>? Logged;

    public static IReadOnlyList<LogEntry> Entries()
    {
        lock (Gate) return [.. Recent];
    }

    public static void ClearRecent()
    {
        lock (Gate) Recent.Clear();
    }

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Midfielder", "midfielder.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message}: {e.GetType().Name}: {e.Message}{Environment.NewLine}{e.StackTrace}");

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        string line = $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}";
        var entry = new LogEntry(now, level.StartsWith('E'), message);
        lock (Gate)
        {
            Recent.AddFirst(entry);
            if (Recent.Count > MaxEntries) Recent.RemoveLast();
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes) File.Move(Path, Path + ".old", overwrite: true);
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        Logged?.Invoke(entry);
    }
}
