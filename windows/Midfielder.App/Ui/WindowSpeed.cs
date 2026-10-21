using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace Midfielder.App.Ui;

/// <summary>
/// For every Midfielder window: full speed while it's open (see <see cref="Performance"/>), and cloaked until its
/// first frame is drawn, so it appears complete instead of as a blank white rectangle.
/// </summary>
internal static class WindowSpeed
{
    public static void Attach(Window window, string name)
    {
        var hold = Performance.Hold();
        var clock = Stopwatch.StartNew();
        window.SourceInitialized += (_, _) => NativeMethods.Cloak(new WindowInteropHelper(window).Handle, true);
        window.ContentRendered += (_, _) =>
        {
            NativeMethods.Cloak(new WindowInteropHelper(window).Handle, false);
            AppLog.Info($"{name} rendered in {clock.ElapsedMilliseconds} ms");
        };
        window.Closed += (_, _) => hold.Dispose();
    }
}
