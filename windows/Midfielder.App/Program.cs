using System.Windows.Forms;

namespace Midfielder.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Midfielder-" + Environment.UserName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: hand over files to send (or just ask it to show itself) and exit.
            var files = SendArgs.Paths(args);
            SendToPipe.Forward(files.Count > 0 ? files : ["--show"]);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Crash.Install();
        // WPF's dispatcher is the message loop: it runs the Fluent windows natively and pumps the WinForms
        // tray icon, menu, timers and clipboard listener (all plain window messages) on the same thread.
        var app = Ui.WpfApp.Create();
        Crash.Watch(app);
        using var context = new TrayContext(args);
        context.ThreadExit += (_, _) => app.Shutdown();
        app.Run();
        return 0;
    }
}

/// <summary>
/// Last-resort error handling: everything is logged, the user gets one short message, and a window that keeps
/// failing is closed so the tray (clipboard sync, transfers) carries on.
/// </summary>
internal static class Crash
{
    private static long s_lastShownMs = -60_000;
    private static int s_recent;
    private static long s_windowStartMs;

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Unhandled exception, Midfielder is closing", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
    }

    public static void Watch(System.Windows.Application app) => app.DispatcherUnhandledException += (_, e) =>
    {
        e.Handled = true;
        Report(e.Exception);
    };

    private static void Report(Exception e)
    {
        AppLog.Error("Unexpected error", e);
        long now = Environment.TickCount64;
        if (now - s_windowStartMs > 10_000)
        {
            s_windowStartMs = now;
            s_recent = 0;
        }
        // The same error over and over (e.g. on every layout pass): close the windows, keep the tray.
        if (++s_recent >= 3)
        {
            foreach (var w in System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().ToList() ?? [])
            {
                try { w.Close(); } catch (InvalidOperationException) { }
            }
        }
        if (now - s_lastShownMs < 60_000) return;
        s_lastShownMs = now;
        MessageBox.Show(
            $"Midfielder hit an error: {e.Message}\n\nClipboard sync keeps running. Details are in the log " +
            $"(Settings → Developer → Open log file):\n{AppLog.Path}",
            "Midfielder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}

/// <summary>
/// Files to send, from the command line. <c>--send a b c</c> comes from the classic right-click entry and Send
/// To; <c>--send-list file</c> from the Windows 11 menu handler, which writes the selection (one path per line)
/// to a temp file so a large selection never hits the command-line length limit. The list file is deleted.
/// </summary>
internal static class SendArgs
{
    public static List<string> Paths(string[] args)
    {
        int i = Array.IndexOf(args, "--send-list");
        if (i >= 0 && i + 1 < args.Length)
        {
            string list = args[i + 1];
            try
            {
                var paths = File.ReadAllLines(list, System.Text.Encoding.UTF8).Where(l => l.Length > 0).Take(10_000).ToList();
                File.Delete(list);
                return paths;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
        return args.Contains("--send") ? args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList() : [];
    }

    /// <summary>Files as-is; folders become the files inside them (recursively, at most <paramref name="max"/>).</summary>
    public static List<string> Expand(IEnumerable<string> paths, int max = 1000)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (result.Count >= max) break;
            if (File.Exists(p)) result.Add(p);
            else if (Directory.Exists(p))
            {
                try
                {
                    result.AddRange(Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Take(max - result.Count));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error($"Can't read folder {p}", e);
                }
            }
        }
        return result;
    }

    public static bool IsSend(string[] args) => args.Contains("--send") || args.Contains("--send-list");
}
