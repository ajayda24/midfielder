using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Midfielder.App.Ui;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Continuity;

/// <summary>
/// Screen-edge drop: drag files to the screen edge and a "send to phone" target slides in. A low-level mouse hook
/// (installed only while the feature is on and a phone is connected) only notices "left button held at the edge";
/// then an invisible catcher window appears there, and only a real file drag entering it makes the target show. So
/// moving or snapping a window to the edge never shows anything.
/// </summary>
internal sealed class EdgeDrop : IDisposable
{
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly Func<string?> _phoneName;
    private readonly Action<IReadOnlyList<string>> _send;
    private readonly HookProc _proc;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private IntPtr _hook;
    private bool _down;
    private bool _triggered;
    private EdgeDropWindow? _window;

    public EdgeDrop(Func<string?> phoneName, Action<IReadOnlyList<string>> send)
    {
        _phoneName = phoneName;
        _send = send;
        _proc = OnMouse; // kept in a field: the hook holds only a function pointer
    }

    public bool RightEdge { get; set; } = true;

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, Marshal.GetFunctionPointerForDelegate(_proc), NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) AppLog.Info($"Screen-edge drop: couldn't watch the mouse (error {Marshal.GetLastPInvokeError()})");
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _window?.Collapse();
    }

    /// <summary>Runs for every mouse event while installed: only a few comparisons, never blocks.</summary>
    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            long msg = wParam.ToInt64();
            if (msg == NativeMethods.WM_LBUTTONDOWN)
            {
                _down = true;
                _triggered = false;
            }
            else if (msg == NativeMethods.WM_LBUTTONUP)
            {
                _down = false;
                if (_triggered) _dispatcher.BeginInvoke(() => _window?.ButtonReleased());
                _triggered = false;
            }
            else if (msg == NativeMethods.WM_MOUSEMOVE && _down && !_triggered)
            {
                var p = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).Bounds;
                bool atEdge = RightEdge ? p.X >= screen.Right - 2 : p.X <= screen.Left + 1;
                if (atEdge)
                {
                    _triggered = true;
                    _dispatcher.BeginInvoke(() => ShowCatcher(screen));
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void ShowCatcher(System.Drawing.Rectangle screen)
    {
        try
        {
            _window ??= new EdgeDropWindow(_send);
            _window.Catch(screen, RightEdge, _phoneName() ?? "your phone");
        }
        catch (Exception e)
        {
            AppLog.Error("Screen-edge drop couldn't show", e);
        }
    }

    public void Dispose()
    {
        Stop();
        _window?.Close();
        _window = null;
    }
}

/// <summary>The drop target: invisible until files are dragged onto it, then a card slides in from the edge.</summary>
#pragma warning disable CA1001 // the timer and hold end with the window (Collapse/OnClosed)
internal sealed class EdgeDropWindow : Window
#pragma warning restore CA1001
{
    private const int WidthPx = 132;
    private static readonly TimeSpan CatchTimeout = TimeSpan.FromMilliseconds(450);

    private readonly Action<IReadOnlyList<string>> _send;
    private readonly DispatcherTimer _timeout = new() { Interval = CatchTimeout };
    private readonly Border _card;
    private readonly TranslateTransform _slide = new();
    private readonly TextBlock _title = Fx.Text("", 13, FontWeights.SemiBold, wrap: true);
    private readonly TextBlock _sub = Fx.Caption("Drop to send", wrap: true);
    private bool _expanded;
    private bool _right = true;
    private IDisposable? _hold;

    public EdgeDropWindow(Action<IReadOnlyList<string>> send)
    {
        _send = send;
        WpfApp.Ensure();
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        // Almost transparent but hit-testable: a layered window ignores fully transparent pixels for drag and drop.
        Background = new SolidColorBrush(Color.FromArgb(2, 255, 255, 255));
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowDrop = true;
        Title = "Midfielder: drop to send";

        _title.TextAlignment = TextAlignment.Center;
        _sub.TextAlignment = TextAlignment.Center;
        var badge = Fx.Badge(Fluent.SymbolRegular.Phone24, 52, Fx.Green, Brushes.White, circle: true);
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        _card = new Border
        {
            Background = Fx.Surface,
            BorderBrush = Fx.HighlightStrong,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(12, 18, 12, 18),
            Margin = new Thickness(10),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Fx.Stack(10, badge, _title, _sub),
            Opacity = 0,
            RenderTransform = _slide,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 3, Direction = 270, Opacity = 0.18, Color = Colors.Black },
        };
        Content = _card;

        DragEnter += (_, e) => OnDrag(e, entering: true);
        DragOver += (_, e) => OnDrag(e, entering: false);
        DragLeave += (_, _) => _timeout.Start();
        Drop += OnDrop;
        _timeout.Tick += (_, _) => Collapse();
    }

    /// <summary>The button is held at the edge: wait (invisibly) for a file drag to enter.</summary>
    public void Catch(System.Drawing.Rectangle screen, bool rightEdge, string phoneName)
    {
        _right = rightEdge;
        _title.Text = phoneName;
        _sub.Text = "Drop to send";
        int height = screen.Height * 6 / 10;
        int x = rightEdge ? screen.Right - WidthPx : screen.Left;
        int y = screen.Top + (screen.Height - height) / 2;
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, WidthPx, height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        _timeout.Stop();
        _timeout.Start();
    }

    /// <summary>The mouse button went up somewhere: without a drop in progress, go away.</summary>
    public void ButtonReleased()
    {
        if (!_expanded) Collapse();
    }

    private void OnDrag(DragEventArgs e, bool entering)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!files) return;
        _timeout.Stop();
        if (entering || !_expanded) Expand();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            _send(paths);
            _sub.Text = paths.Length == 1 ? "Sending…" : $"Sending {paths.Length} files…";
        }
        _timeout.Interval = TimeSpan.FromMilliseconds(900);
        _timeout.Start();
    }

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        _hold ??= Performance.Hold();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_right ? WidthPx : -WidthPx, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    public void Collapse()
    {
        _timeout.Stop();
        _timeout.Interval = CatchTimeout;
        _expanded = false;
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _card.BeginAnimation(OpacityProperty, null);
        _card.Opacity = 0;
        _hold?.Dispose();
        _hold = null;
        if (IsVisible) Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timeout.Stop();
        _hold?.Dispose();
        _hold = null;
        base.OnClosed(e);
    }
}
