using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace Midfielder.App.Ui;

/// <summary>
/// The WPF side of the app. WPF's dispatcher is the app's message loop (Program runs it) and pumps the WinForms
/// tray on the same STA thread. The Fluent theme resources load the first time a window opens, so a tray-only
/// session never loads them.
/// </summary>
internal static class WpfApp
{
    /// <summary>The app-wide WPF Application (Program runs its dispatcher loop); cheap, no theme loaded yet.</summary>
    public static Application Create() => Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

    /// <summary>Loads the Fluent theme the first time a window opens.</summary>
    public static void Ensure()
    {
        Create();
        var resources = Application.Current!.Resources;
        if (resources.MergedDictionaries.OfType<ThemesDictionary>().Any()) return;
        resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        resources.MergedDictionaries.Add(new ControlsDictionary());
        ApplicationThemeManager.Apply(ApplicationTheme.Light, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
        ApplicationAccentColorManager.Apply(Fx.PrimaryColor, ApplicationTheme.Light, false, false);
    }
}
