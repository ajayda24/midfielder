using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace Midfielder.App.Ui;

/// <summary>
/// For every Midfielder window: full speed while it's visible (see <see cref="Performance"/>), efficiency mode
/// again once it's hidden or closed; cloaked until its first frame is drawn (no white flash); the time to the first
/// frame is logged (the real-exe CI check reads it). The window also gets its own taskbar identity, so its button
/// shows Midfielder's icon.
/// </summary>
internal static class WindowSpeed
{
    public static void Attach(Window window, string name)
    {
        IDisposable? hold = Performance.Hold(); // before the window is built: building is the slow part
        var clock = Stopwatch.StartNew();
        // Cloaked until the first frame is drawn, so it never appears as a blank white rectangle (with a fallback,
        // so a slow first frame can't keep it invisible).
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            NativeMethods.SetWindowAppId(hwnd, Environment.ProcessPath);
            NativeMethods.Cloak(hwnd, true);
            var fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            fallback.Tick += (_, _) =>
            {
                fallback.Stop();
                NativeMethods.Cloak(hwnd, false);
            };
            fallback.Start();
            window.ContentRendered += (_, _) =>
            {
                fallback.Stop();
                NativeMethods.Cloak(hwnd, false);
            };
        };
        window.ContentRendered += (_, _) =>
        {
            AppLog.Info($"{name} rendered in {clock.ElapsedMilliseconds} ms");
            clock.Stop();
        };
        // A window whose constructor failed is never shown: don't keep the process at full speed for it.
        window.Dispatcher.BeginInvoke(() =>
        {
            if (window.IsVisible) return;
            hold?.Dispose();
            hold = null;
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                hold ??= Performance.Hold();
                if (clock.IsRunning) return; // the first show: ContentRendered reports it
                clock.Restart();
                window.Dispatcher.BeginInvoke(() =>
                {
                    AppLog.Info($"{name} shown again in {clock.ElapsedMilliseconds} ms");
                    clock.Stop();
                }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            else
            {
                clock.Stop();
                hold?.Dispose();
                hold = null;
            }
        };
        window.Closed += (_, _) =>
        {
            hold?.Dispose();
            hold = null;
        };
    }
}
