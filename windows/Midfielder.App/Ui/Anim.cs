using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Midfielder.App.Ui;

/// <summary>
/// Short, purposeful motion: pages and list items slide in, cards lift on hover, progress glides, the sidebar
/// pill slides between items. Everything is skipped when Windows' "Animation effects" setting is off.
/// </summary>
internal static class Anim
{
    /// <summary>Tests turn motion off so screenshots are deterministic.</summary>
    public static bool Disabled { get; set; }

    public static bool Enabled => !Disabled && SystemParameters.ClientAreaAnimation;

    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Freeze(CubicEase e)
    {
        e.Freeze();
        return e;
    }

    private static DoubleAnimation To(double to, int ms, int delayMs = 0) => new(to, TimeSpan.FromMilliseconds(ms))
    {
        EasingFunction = Ease,
        BeginTime = TimeSpan.FromMilliseconds(delayMs),
    };

    /// <summary>Fade in while sliding up a few pixels.</summary>
    public static void Enter(UIElement e, int delayMs = 0, double fromY = 14, int ms = 260)
    {
        if (!Enabled) return;
        var t = new TranslateTransform(0, fromY);
        e.RenderTransform = t;
        e.Opacity = 0;
        var fade = To(1, ms, delayMs);
        fade.FillBehavior = FillBehavior.Stop;
        fade.Completed += (_, _) => e.Opacity = 1;
        e.BeginAnimation(UIElement.OpacityProperty, fade);
        t.BeginAnimation(TranslateTransform.YProperty, To(0, ms + 40, delayMs));
    }

    /// <summary>Children appear one after another (capped, so long lists don't wait).</summary>
    public static void Stagger(IEnumerable<UIElement> items, int stepMs = 40, int max = 10)
    {
        int i = 0;
        foreach (var e in items) Enter(e, Math.Min(i++, max) * stepMs, 10);
    }

    /// <summary>Slight lift and deeper shadow on hover.</summary>
    public static void HoverLift(Border card)
    {
        var t = new TranslateTransform();
        card.RenderTransform = t;
        card.MouseEnter += (_, _) =>
        {
            if (!Enabled) return;
            t.BeginAnimation(TranslateTransform.YProperty, To(-2, 160));
            if (card.Effect is DropShadowEffect s && !s.IsFrozen) s.BeginAnimation(DropShadowEffect.OpacityProperty, To(0.11, 160));
        };
        card.MouseLeave += (_, _) =>
        {
            if (!Enabled) return;
            t.BeginAnimation(TranslateTransform.YProperty, To(0, 200));
            if (card.Effect is DropShadowEffect s && !s.IsFrozen) s.BeginAnimation(DropShadowEffect.OpacityProperty, To(0.05, 200));
        };
    }

    /// <summary>Moves a value smoothly (progress bars, sidebar pill, widths).</summary>
    public static void Glide(IAnimatable target, DependencyProperty property, double to, int ms = 280)
    {
        if (!Enabled)
        {
            target.BeginAnimation(property, null);
            if (target is DependencyObject d) d.SetValue(property, to);
            return;
        }
        target.BeginAnimation(property, To(to, ms), HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Gentle breathing for the "connected" dot.</summary>
    public static void Pulse(UIElement e, bool on)
    {
        if (!on || !Enabled)
        {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 1;
            return;
        }
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(1100))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>Background color cross-fade (hover, drag-over).</summary>
    public static void Tint(Border b, Color to, int ms = 160)
    {
        if (b.Background is not SolidColorBrush current || current.IsFrozen)
        {
            current = new SolidColorBrush((b.Background as SolidColorBrush)?.Color ?? Colors.Transparent);
            b.Background = current;
        }
        if (!Enabled)
        {
            current.Color = to;
            return;
        }
        current.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
    }

    /// <summary>Scale pop used when something lands (drop zone, new transfer).</summary>
    public static void Pop(FrameworkElement e)
    {
        if (!Enabled) return;
        var s = new ScaleTransform(1, 1);
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        e.RenderTransform = s;
        var a = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    public static void FadeOut(UIElement e, int ms, Action done)
    {
        if (!Enabled)
        {
            done();
            return;
        }
        var a = To(0, ms);
        a.Completed += (_, _) => done();
        e.BeginAnimation(UIElement.OpacityProperty, a);
    }
}
