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

    private static bool _themed;

    /// <summary>Loads the Fluent theme the first time a window opens.</summary>
    public static void Ensure()
    {
        Create();
        // A flag, not a look for the ThemesDictionary: ApplicationThemeManager.Apply swaps it for a plain dictionary,
        // and merging the theme again restyles open windows ("Cannot change AllowsTransparency after a Window has
        // been shown"), so the second window never opened.
        if (_themed) return;
        _themed = true;
        var resources = Application.Current!.Resources;
        resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        resources.MergedDictionaries.Add(new ControlsDictionary());
        ApplicationThemeManager.Apply(ApplicationTheme.Light, Wpf.Ui.Controls.WindowBackdropType.None, false);
        ApplicationAccentColorManager.Apply(Fx.PrimaryColor, ApplicationTheme.Light, false, false);
    }
}
