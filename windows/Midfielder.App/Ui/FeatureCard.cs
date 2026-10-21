using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Ui;

/// <summary>
/// One optional feature in Settings: a card with icon, title, description, a status line and the main switch. The
/// feature's options live inside the same card and slide open while the switch is on.
/// </summary>
internal sealed class FeatureCard : Border
{
    private readonly Fluent.ToggleSwitch _switch;
    private readonly StackPanel _options = new();
    private readonly Border _optionsHost;
    private readonly TextBlock _status = Fx.Text("", 12.5, FontWeights.SemiBold);
    private readonly Ellipse _statusDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _iconBox;

    public FeatureCard(Fluent.SymbolRegular icon, string title, string description, bool on, Action<bool> changed)
    {
        Background = Fx.Surface;
        BorderBrush = Fx.CardEdge;
        BorderThickness = new Thickness(1, 1, 1, 1.5);
        CornerRadius = new CornerRadius(14);
        Anim.HoverLift(this);

        _switch = new Fluent.ToggleSwitch { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(_switch, title);
        _iconBox = Fx.Badge(icon, 44);
        var text = Fx.Stack(3, Fx.Text(title, 15, FontWeights.SemiBold), Fx.Caption(description, wrap: true), Fx.Row(0, _statusDot, _status));
        var header = Fx.Line(_iconBox, text, _switch, 16);
        header.Margin = new Thickness(20, 16, 20, 16);

        _optionsHost = new Border
        {
            BorderBrush = Fx.Outline,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Fx.Layer,
            CornerRadius = new CornerRadius(0, 0, 13, 13),
            Padding = new Thickness(80, 4, 20, 8),
            Child = _options,
            ClipToBounds = true,
        };
        Child = Fx.Stack(0, header, _optionsHost);

        _switch.Checked += (_, _) => Toggle(true, changed);
        _switch.Unchecked += (_, _) => Toggle(false, changed);
        IsOn = on;
        Loaded += (_, _) => Show(on, animate: false);
    }

    public bool IsOn { get; private set; }

    /// <summary>A row inside the card: title and description on the left, a control on the right.</summary>
    public FeatureCard Option(string title, string description, UIElement trailing) => Option(title, Fx.Caption(description, wrap: true), trailing);

    /// <summary>A row whose description is live (e.g. a status line the caller keeps updating).</summary>
    public FeatureCard Option(string title, UIElement description, UIElement trailing)
    {
        var text = Fx.Stack(1, Fx.Text(title, 14), description);
        var row = Fx.Line(null, text, trailing, 16);
        row.Margin = new Thickness(0, 10, 0, 10);
        if (_options.Children.Count > 0)
        {
            _options.Children.Add(new Border { Height = 1, Background = Fx.Outline, Opacity = 0.6 });
        }
        _options.Children.Add(row);
        return this;
    }

    public FeatureCard OptionToggle(string title, string description, bool on, Action<bool> changed)
    {
        var sw = new Fluent.ToggleSwitch { IsChecked = on };
        System.Windows.Automation.AutomationProperties.SetName(sw, title);
        sw.Checked += (_, _) => changed(true);
        sw.Unchecked += (_, _) => changed(false);
        return Option(title, description, sw);
    }

    /// <summary>The line under the description: whether the feature is actually working, and what's missing.</summary>
    public void SetStatus(string text, FeatureStatus status)
    {
        _status.Text = text;
        var brush = status switch
        {
            FeatureStatus.Working => Fx.Online,
            FeatureStatus.Waiting => Fx.Warning,
            _ => Fx.Offline,
        };
        _statusDot.Fill = brush;
        _status.Foreground = status == FeatureStatus.Working ? Fx.PrimaryDark : status == FeatureStatus.Waiting ? Fx.WarningText : Fx.Muted;
        _iconBox.Background = status == FeatureStatus.Off ? Fx.Layer : Fx.Highlight;
    }

    private void Toggle(bool on, Action<bool> changed)
    {
        if (on == IsOn) return;
        IsOn = on;
        changed(on);
        Show(on, animate: true);
    }

    private void Show(bool on, bool animate)
    {
        bool hasOptions = _options.Children.Count > 0;
        if (!hasOptions || !animate || !Anim.Enabled)
        {
            _optionsHost.BeginAnimation(MaxHeightProperty, null);
            _optionsHost.BeginAnimation(OpacityProperty, null);
            _optionsHost.MaxHeight = double.PositiveInfinity;
            _optionsHost.Opacity = 1;
            _optionsHost.Visibility = on && hasOptions ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        _optionsHost.Visibility = Visibility.Visible;
        _optionsHost.Measure(new Size(Math.Max(1, ActualWidth), double.PositiveInfinity));
        double full = _optionsHost.DesiredSize.Height;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var height = new DoubleAnimation(on ? 0 : full, on ? full : 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
        height.Completed += (_, _) =>
        {
            _optionsHost.BeginAnimation(MaxHeightProperty, null);
            _optionsHost.MaxHeight = double.PositiveInfinity;
            if (!IsOn) _optionsHost.Visibility = Visibility.Collapsed;
        };
        _optionsHost.BeginAnimation(MaxHeightProperty, height);
        _optionsHost.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0 : 1, on ? 1 : 0, TimeSpan.FromMilliseconds(200)));
    }
}

internal enum FeatureStatus
{
    Off,
    Waiting,
    Working,
}
