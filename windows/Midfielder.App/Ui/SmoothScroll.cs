using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Midfielder.App.Ui;

/// <summary>
/// Smooth mouse-wheel scrolling: each notch moves a target offset and the view eases towards it over a few frames
/// (like Windows 11 Settings), instead of jumping 48 px at a time. The per-frame handler runs only while moving.
/// Touchpads and touch keep their own (already smooth) scrolling.
/// </summary>
internal static class SmoothScroll
{
    private const double PixelsPerNotch = 110;
    private const double Ease = 0.22; // share of the remaining distance covered each frame (~120 ms to settle at 60 Hz)

    public static void Attach(ScrollViewer viewer)
    {
        double target = 0;
        bool running = false;

        void OnFrame(object? sender, EventArgs e)
        {
            double current = viewer.VerticalOffset;
            double next = current + (target - current) * Ease;
            if (Math.Abs(target - next) < 0.5 || !viewer.IsVisible)
            {
                viewer.ScrollToVerticalOffset(target);
                CompositionTarget.Rendering -= OnFrame;
                running = false;
                return;
            }
            viewer.ScrollToVerticalOffset(next);
        }

        viewer.PreviewMouseWheel += (_, e) =>
        {
            if (!Anim.Enabled || viewer.ScrollableHeight <= 0 || e.Handled) return;
            // A precision touchpad sends small deltas many times a second: let WPF handle those as usual.
            if (Math.Abs(e.Delta) < Mouse.MouseWheelDeltaForOneLine) return;
            if (InnerScroller(e.OriginalSource as DependencyObject, viewer)) return; // a list inside the page scrolls itself
            if (!running) target = viewer.VerticalOffset;
            target = Math.Clamp(target - e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * PixelsPerNotch, 0, viewer.ScrollableHeight);
            e.Handled = true;
            if (running) return;
            running = true;
            CompositionTarget.Rendering += OnFrame;
        };
        // Anything else that scrolls (scrollbar drag, keyboard, ScrollToTop) wins over a wheel animation in flight.
        viewer.PreviewMouseDown += (_, _) => Stop();
        viewer.PreviewKeyDown += (_, _) => Stop();
        viewer.Unloaded += (_, _) => Stop();

        void Stop()
        {
            if (!running) return;
            CompositionTarget.Rendering -= OnFrame;
            running = false;
        }
    }

    private static bool InnerScroller(DependencyObject? from, ScrollViewer outer)
    {
        for (var d = from; d is not null && d != outer; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ScrollViewer inner && inner.ScrollableHeight > 0) return true;
        }
        return false;
    }
}
