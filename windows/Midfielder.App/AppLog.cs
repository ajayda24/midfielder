using System.Globalization;
using System.Text;

namespace Midfielder.App;

/// <summary>
/// Small diagnostic log at %LOCALAPPDATA%\Midfielder\midfielder.log (tray menu → Open log).
/// Bounded: rotates to midfielder.log.old at 1 MB, so it can never fill the disk. No clipboard contents are
/// written, only kinds and sizes.
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Midfielder", "midfielder.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message}: {e.GetType().Name}: {e.Message}{Environment.NewLine}{e.StackTrace}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}";
        lock (Gate)
        {
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
    }
}
