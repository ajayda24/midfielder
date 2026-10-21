using System.Windows;
using System.Windows.Controls;

namespace Midfielder.App.Ui;

/// <summary>
/// One page of the main window. Scrolling pages put their content in a <see cref="ScrollViewer"/>: the scroll
/// range is exactly the content (no empty tail) and every page opens at the top. Content is a centered column
/// at most <see cref="MaxContentWidth"/> wide. Pages subscribe to the host in their constructor and
/// unsubscribe in <see cref="Dispose"/> (called when another page is shown or the window closes).
/// </summary>
internal abstract class PageBase : UserControl, IDisposable
{
    public const double MaxContentWidth = 1040;

    protected PageBase(IAppHost host, MainWindow window, bool scrolling = true)
    {
        Host = host;
        Window = window;
        Body = new StackPanel { MaxWidth = MaxContentWidth, Margin = new Thickness(36, 28, 36, 36) };
        if (scrolling)
        {
            Scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.VerticalOnly,
                Content = Body,
                Focusable = false,
            };
            Content = Scroller;
            SmoothScroll.Attach(Scroller);
        }
    }

    protected IAppHost Host { get; }

    protected MainWindow Window { get; }

    /// <summary>The page's content column (scrolling pages).</summary>
    public StackPanel Body { get; }

    public ScrollViewer? Scroller { get; }

    protected bool Disposed { get; private set; }

    /// <summary>Runs on the UI thread later (host events arrive on any thread).</summary>
    protected void Post(Action action)
    {
        if (Disposed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!Disposed) action();
        });
    }

    protected static UIElement Header(string title, string? subtitle)
    {
        var t = Fx.Title(title);
        if (subtitle is null) return t;
        var s = Fx.Caption(subtitle);
        s.FontSize = 14;
        s.Margin = new Thickness(0, 4, 0, 0);
        return Fx.Stack(0, t, s);
    }

    /// <summary>Section label above a group of cards.</summary>
    protected static TextBlock Section(string text)
    {
        var t = Fx.Text(text, 14, FontWeights.SemiBold, Fx.Muted);
        t.Margin = new Thickness(4, 10, 0, -4);
        return t;
    }

    /// <summary>Adds a block to the column with standard spacing; the first few slide in with a small stagger.</summary>
    protected void Add(UIElement block, double gap = 16)
    {
        Fx.Add(Body, block, gap);
        // Only what's on screen when the page opens moves; the rest is simply there (cheaper, no late pop-in).
        if (Body.Children.Count <= 4) Anim.Enter(block, Body.Children.Count * 35);
    }

    protected virtual void OnDispose() { }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        OnDispose();
    }
}
