using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace Midfielder.App.Ui;

/// <summary>
/// The WPF side of the app. The tray's WinForms message loop stays the main loop; WPF windows share it (same
/// STA thread). The WPF Application object and the Fluent theme resources are created once, the first time a
/// window opens, so a tray-only session never loads them.
/// </summary>
internal static class WpfApp
{
    public static void Ensure()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        var resources = Application.Current!.Resources;
        if (resources.MergedDictionaries.OfType<ThemesDictionary>().Any()) return;
        resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        resources.MergedDictionaries.Add(new ControlsDictionary());
        ApplicationThemeManager.Apply(ApplicationTheme.Light, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
        ApplicationAccentColorManager.Apply(Fx.PrimaryColor, ApplicationTheme.Light, false, false);
    }
}
