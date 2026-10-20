using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Ui;

/// <summary>
/// Colors, type and small building blocks for the WPF window. One light look: dark green primary, very light
/// green for focus and selection (same palette as the Android app). All brushes are frozen and shared.
/// </summary>
internal static class Fx
{
    public static readonly Color PrimaryColor = Color.FromRgb(0x1F, 0x6F, 0x5C);

    public static readonly Brush Green = Freeze(PrimaryColor);
    public static readonly Brush PrimaryDark = Freeze(Color.FromRgb(0x17, 0x4F, 0x42));
    public static readonly Brush Highlight = Freeze(Color.FromRgb(0xE6, 0xF4, 0xEE));
    public static readonly Brush HighlightStrong = Freeze(Color.FromRgb(0xC9, 0xE7, 0xDA));
    public static readonly Brush Ink = Freeze(Color.FromRgb(0x1B, 0x24, 0x21));
    public static readonly Brush Muted = Freeze(Color.FromRgb(0x5E, 0x6B, 0x66));
    public static readonly Brush Surface = Freeze(Colors.White);
    public static readonly Brush Layer = Freeze(Color.FromArgb(0xF2, 0xF6, 0xF9, 0xF7));
    public static readonly Brush Outline = Freeze(Color.FromRgb(0xE1, 0xE8, 0xE4));
    public static readonly Brush Danger = Freeze(Color.FromRgb(0xB3, 0x26, 0x1E));
    public static readonly Brush DangerBg = Freeze(Color.FromRgb(0xFC, 0xEE, 0xEE));
    public static readonly Brush Online = Freeze(Color.FromRgb(0x2E, 0x9B, 0x6F));
    public static readonly Brush Offline = Freeze(Color.FromRgb(0xB8, 0xC2, 0xBD));
    public static readonly Brush Transparent = Freeze(Colors.Transparent);

    /// <summary>Soft green gradient for the hero card.</summary>
    public static readonly Brush Hero = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xE3, 0xF5, 0xEC), 0),
            new(Color.FromRgb(0xF4, 0xFB, 0xF7), 0.55),
            new(Color.FromRgb(0xD5, 0xEE, 0xE3), 1),
        },
        new Point(0, 0), new Point(1, 1)));

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private static Brush Freeze(Color c) => Freeze(new SolidColorBrush(c));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    // --- Text ---------------------------------------------------------------------------------------

    public static TextBlock Text(string text, double size = 14, FontWeight? weight = null, Brush? color = null, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = color ?? Ink,
        FontFamily = Font,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Title(string text)
    {
        var t = Text(text, 30, FontWeights.SemiBold);
        t.FontFamily = Display;
        return t;
    }

    public static TextBlock Heading(string text) => Text(text, 16, FontWeights.SemiBold);

    public static TextBlock Caption(string text, bool wrap = false) => Text(text, 12.5, color: Muted, wrap: wrap);

    // --- Surfaces -----------------------------------------------------------------------------------

    /// <summary>A white rounded card that lifts a little on hover.</summary>
    public static Border Card(UIElement child, double padding = 20, bool lift = true)
    {
        var card = new Border
        {
            Background = Surface,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(padding),
            Child = child,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Direction = 270, Opacity = 0.05, Color = Colors.Black },
        };
        if (lift) Anim.HoverLift(card);
        return card;
    }

    public static StackPanel Stack(double spacing, params UIElement[] children)
    {
        var s = new StackPanel();
        foreach (var c in children) Add(s, c, spacing);
        return s;
    }

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i > 0) fe.Margin = new Thickness(spacing, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            s.Children.Add(children[i]);
        }
        return s;
    }

    /// <summary>Adds with a top gap (except for the first child).</summary>
    public static void Add(Panel panel, UIElement child, double spacing)
    {
        if (child is FrameworkElement fe && panel.Children.Count > 0)
        {
            fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + spacing, fe.Margin.Right, fe.Margin.Bottom);
        }
        panel.Children.Add(child);
    }

    /// <summary>Wrapping row of buttons/chips with even gaps.</summary>
    public static WrapPanel Wrap(double gap, params UIElement[] children)
    {
        var w = new WrapPanel();
        foreach (var c in children)
        {
            if (c is FrameworkElement fe) fe.Margin = new Thickness(0, 0, gap, gap);
            w.Children.Add(c);
        }
        return w;
    }

    // --- Controls -----------------------------------------------------------------------------------

    public static Fluent.SymbolIcon Icon(Fluent.SymbolRegular symbol, double size = 18, Brush? color = null, bool filled = false) => new()
    {
        Symbol = symbol,
        FontSize = size,
        Filled = filled,
        Foreground = color ?? Green,
    };

    public static Fluent.Button Button(string text, Fluent.SymbolRegular? icon, Fluent.ControlAppearance appearance, Action onClick)
    {
        var b = new Fluent.Button
        {
            Content = text,
            Appearance = appearance,
            Padding = new Thickness(16, 8, 16, 8),
            MinHeight = 36,
            FontSize = 14,
            Cursor = Cursors.Hand,
        };
        if (icon is { } s) b.Icon = new Fluent.SymbolIcon { Symbol = s, FontSize = 16 };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Fluent.Button Primary(string text, Fluent.SymbolRegular? icon, Action onClick) => Button(text, icon, Fluent.ControlAppearance.Primary, onClick);

    public static Fluent.Button Secondary(string text, Fluent.SymbolRegular? icon, Action onClick) => Button(text, icon, Fluent.ControlAppearance.Secondary, onClick);

    public static Fluent.Button Subtle(string text, Fluent.SymbolRegular? icon, Action onClick) => Button(text, icon, Fluent.ControlAppearance.Transparent, onClick);

    public static Fluent.Button IconButton(Fluent.SymbolRegular icon, string tooltip, Action onClick)
    {
        var b = Button("", icon, Fluent.ControlAppearance.Transparent, onClick);
        b.Padding = new Thickness(8);
        b.MinHeight = 32;
        b.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetName(b, tooltip);
        return b;
    }

    /// <summary>A glyph in a soft rounded square (or circle).</summary>
    public static Border Badge(Fluent.SymbolRegular symbol, double size = 44, Brush? background = null, Brush? color = null, bool circle = false) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(circle ? size / 2 : size * 0.28),
        Background = background ?? Highlight,
        Child = new Fluent.SymbolIcon
        {
            Symbol = symbol,
            FontSize = size * 0.46,
            Foreground = color ?? Green,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>Pill-shaped status chip with a dot.</summary>
    public static Border Chip(string text, Brush dot, Brush background, out TextBlock label, out System.Windows.Shapes.Ellipse dotShape)
    {
        dotShape = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = dot, VerticalAlignment = VerticalAlignment.Center };
        label = Text(text, 12.5, FontWeights.SemiBold, PrimaryDark);
        label.Margin = new Thickness(8, 0, 0, 0);
        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Row(0, dotShape, label),
        };
    }

    /// <summary>Two-column grid: <paramref name="left"/> stretches, <paramref name="right"/> sizes to content.</summary>
    public static Grid Split(UIElement left, UIElement right, double gap = 16)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1);
        if (right is FrameworkElement fe)
        {
            fe.Margin = new Thickness(gap, 0, 0, 0);
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        if (left is FrameworkElement lf) lf.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    /// <summary>Leading element, then a stretching middle, then an optional trailing element.</summary>
    public static Grid Line(UIElement? leading, UIElement middle, UIElement? trailing, double gap = 14)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (leading is not null)
        {
            if (leading is FrameworkElement l)
            {
                l.Margin = new Thickness(0, 0, gap, 0);
                l.VerticalAlignment = VerticalAlignment.Center;
            }
            g.Children.Add(leading);
        }
        if (middle is FrameworkElement m) m.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(middle, 1);
        g.Children.Add(middle);
        if (trailing is not null)
        {
            if (trailing is FrameworkElement t)
            {
                t.Margin = new Thickness(gap, 0, 0, 0);
                t.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(trailing, 2);
            g.Children.Add(trailing);
        }
        return g;
    }

    public static string Bytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{n / (1024.0 * 1024):0.0} MB",
        _ => $"{n / (1024.0 * 1024 * 1024):0.00} GB",
    };

    public static string When(DateTime t)
    {
        var now = DateTime.Now;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (t.Date == now.Date) return "Today " + t.ToString("HH:mm", inv);
        if (t.Date == now.Date.AddDays(-1)) return "Yesterday " + t.ToString("HH:mm", inv);
        return t.ToString(t.Year == now.Year ? "d MMM HH:mm" : "d MMM yyyy", inv);
    }
}
