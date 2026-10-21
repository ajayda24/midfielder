using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Midfielder.App.Ui;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Continuity;

/// <summary>
/// "Find my PC": a full-screen, topmost overlay with a loud looping alarm, until Stop, Esc, a click or
/// <see cref="MaxRing"/> passes. Created on demand and closed completely.
/// </summary>
#pragma warning disable CA1001 // the sound is disposed in OnClosed: a window's lifetime ends there, not in Dispose
internal sealed class RingOverlay : Window
#pragma warning restore CA1001
{
    private static readonly TimeSpan MaxRing = TimeSpan.FromSeconds(30);
    private readonly SoundPlayer? _sound;
    private readonly DispatcherTimer _timer = new() { Interval = MaxRing };

    public RingOverlay(string phoneName)
    {
        WpfApp.Ensure();
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x0E, 0x1F, 0x1A));
        Topmost = true;
        ShowInTaskbar = false;
        WindowState = WindowState.Maximized;
        Title = "Midfielder: find my PC";

        var badge = Fx.Badge(Fluent.SymbolRegular.Speaker224, 96, Fx.Green, Brushes.White, circle: true);
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        var title = Fx.Title("Here I am!");
        title.Foreground = Brushes.White;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var sub = Fx.Text($"{phoneName} is looking for this PC", 16, color: new SolidColorBrush(Color.FromRgb(0xC9, 0xE7, 0xDA)));
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        var stop = Fx.Primary("Stop", Fluent.SymbolRegular.Dismiss24, Close);
        stop.HorizontalAlignment = HorizontalAlignment.Center;
        stop.MinWidth = 160;
        var stack = Fx.Stack(18, badge, title, sub, stop);
        stack.VerticalAlignment = VerticalAlignment.Center;
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        Content = stack;
        Anim.Pulse(badge, true);

        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space) Close();
        };
        MouseLeftButtonUp += (_, _) => Close();
        _timer.Tick += (_, _) => Close();
        _timer.Start();

        string alarm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
        try
        {
            if (File.Exists(alarm))
            {
                _sound = new SoundPlayer(alarm);
                _sound.PlayLooping();
            }
            else
            {
                SystemSounds.Exclamation.Play();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            SystemSounds.Exclamation.Play();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _sound?.Stop();
        _sound?.Dispose();
        base.OnClosed(e);
    }
}
