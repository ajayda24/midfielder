using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Ui;

/// <summary>
/// Navigation rail: brand, page entries with a sliding pill indicator, and the connection status. Collapses to
/// icons (animated) when the window is narrow; labels then show as tooltips.
/// </summary>
internal sealed class Sidebar : Grid
{
    public const double OpenWidth = 248;
    public const double CompactWidth = 76;

    private readonly StackPanel _items = new();
    private readonly StackPanel _footerItems = new();
    private readonly Border _pill = new()
    {
        CornerRadius = new CornerRadius(10),
        Background = Fx.HighlightStrong,
        VerticalAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new Thickness(12, 0, 12, 0),
        IsHitTestVisible = false,
        Opacity = 0,
    };
    private readonly TranslateTransform _pillMove = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly TextBlock _brand = Fx.Text("Midfielder", 18, FontWeights.SemiBold);
    private readonly TextBlock _status = Fx.Caption("");
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, Fill = Fx.Offline };
    private readonly Grid _layers = new();
    private string? _selected;
    private bool _compact;

    public Sidebar()
    {
        Width = OpenWidth;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var logo = new System.Windows.Controls.Image { Source = AppIcon.Image(64), Width = 36, Height = 36 };
        var brand = Fx.Row(12, logo, _brand);
        brand.Margin = new Thickness(20, 12, 12, 20);
        _brand.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(brand);

        _pill.RenderTransform = _pillMove;
        var accent = new Border
        {
            Width = 3,
            Height = 18,
            CornerRadius = new CornerRadius(2),
            Background = Fx.Green,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pill.Child = accent;
        var column = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_footerItems, Dock.Bottom);
        column.Children.Add(_footerItems);
        DockPanel.SetDock(_items, Dock.Top);
        column.Children.Add(_items);
        _layers.Children.Add(_pill);
        _layers.Children.Add(column);
        SetRow(_layers, 1);
        Children.Add(_layers);

        var status = Fx.Row(10, _dot, _status);
        _dot.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(26, 14, 12, 18);
        SetRow(status, 2);
        Children.Add(status);

        SizeChanged += (_, _) => MovePill(animate: false);
    }

    public event Action<string>? Selected;

    public void Add(string name, string label, Fluent.SymbolRegular icon, bool footer = false)
    {
        var e = new Entry(this, name, label, icon);
        _entries[name] = e;
        (footer ? _footerItems : _items).Children.Add(e);
    }

    public void Select(string name)
    {
        if (!_entries.ContainsKey(name)) return;
        bool first = _selected is null;
        _selected = name;
        foreach (var (k, e) in _entries) e.SetSelected(k == name);
        MovePill(animate: !first);
    }

    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            Anim.Glide(this, WidthProperty, value ? CompactWidth : OpenWidth, 220);
            Anim.Glide(_brand, OpacityProperty, value ? 0 : 1, 160);
            Anim.Glide(_status, OpacityProperty, value ? 0 : 1, 160);
            foreach (var e in _entries.Values) e.SetCompact(value);
        }
    }

    public void SetStatus(string text, bool connected)
    {
        _status.Text = text;
        _dot.Fill = connected ? Fx.Online : Fx.Offline;
        ToolTip = text;
        Anim.Pulse(_dot, connected);
    }

    private void MovePill(bool animate)
    {
        if (_selected is null || !_entries.TryGetValue(_selected, out var e) || !e.IsLoaded)
        {
            Dispatcher.BeginInvoke(() => { if (_selected is not null && _entries[_selected].IsLoaded) MovePill(false); }, System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        var top = e.TransformToAncestor(_layers).Transform(new Point(0, 0)).Y;
        _pill.Height = e.ActualHeight;
        _pill.Opacity = 1;
        if (animate) Anim.Glide(_pillMove, TranslateTransform.YProperty, top, 240);
        else
        {
            _pillMove.BeginAnimation(TranslateTransform.YProperty, null);
            _pillMove.Y = top;
        }
    }

    /// <summary>One navigation item: icon (filled when selected) and label; keyboard-focusable.</summary>
    private sealed class Entry : Border
    {
        private readonly Fluent.SymbolIcon _icon;
        private readonly TextBlock _label;
        private bool _selected;

        public Entry(Sidebar owner, string name, string label, Fluent.SymbolRegular icon)
        {
            Height = 44;
            Margin = new Thickness(12, 2, 12, 2);
            CornerRadius = new CornerRadius(10);
            Background = new SolidColorBrush(Colors.Transparent);
            Focusable = true;
            Cursor = Cursors.Hand;
            ToolTip = label;
            System.Windows.Automation.AutomationProperties.SetName(this, label);
            _icon = Fx.Icon(icon, 20, Fx.Muted);
            _label = Fx.Text(label, 14.5, color: Fx.Muted);
            _label.VerticalAlignment = VerticalAlignment.Center;
            _icon.VerticalAlignment = VerticalAlignment.Center;
            var row = Fx.Row(14, _icon, _label);
            row.Margin = new Thickness(16, 0, 8, 0);
            Child = row;
            MouseEnter += (_, _) => { if (!_selected) Anim.Tint(this, Color.FromArgb(0x80, 0xE6, 0xF4, 0xEE)); };
            MouseLeave += (_, _) => Anim.Tint(this, Colors.Transparent);
            MouseLeftButtonUp += (_, _) => owner.Selected?.Invoke(name);
            KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    owner.Selected?.Invoke(name);
                    e.Handled = true;
                }
            };
            FocusVisualStyle = null;
            GotKeyboardFocus += (_, _) => BorderBrush = Fx.Green;
            LostKeyboardFocus += (_, _) => BorderBrush = null;
            BorderThickness = new Thickness(1.5);
        }

        public void SetSelected(bool on)
        {
            _selected = on;
            _icon.Filled = on;
            _icon.Foreground = on ? Fx.PrimaryDark : Fx.Muted;
            _label.Foreground = on ? Fx.Ink : Fx.Muted;
            _label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            if (on) Anim.Tint(this, Colors.Transparent);
        }

        public void SetCompact(bool compact)
        {
            Anim.Glide(_label, OpacityProperty, compact ? 0 : 1, 140);
            ((StackPanel)Child).Margin = new Thickness(compact ? 15 : 16, 0, 8, 0);
        }
    }
}
