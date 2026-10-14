namespace Midfielder.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Midfielder-" + Environment.UserName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: hand over "Send to" files (or just ask it to show itself) and exit.
            var lines = args.Contains("--send") ? args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)) : ["--show"];
            SendToPipe.Forward(lines);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var context = new TrayContext(args);
        Application.Run(context);
        return 0;
    }
}
