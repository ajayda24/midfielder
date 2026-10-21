using System.Windows;
using System.Windows.Controls;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Ui;

/// <summary>
/// The main window: Fluent (rounded, custom title bar, solid background), a navigation rail and one page at a time.
/// Built the first time it's opened; closing it only hides it (its page is dropped, so nothing runs while hidden),
/// so opening it again is instant. Files dropped anywhere on it go to the phone.
/// </summary>
internal sealed class MainWindow : Fluent.FluentWindow
{
    public const string HomeName = "home";
    public const string FilesName = "files";
    public const string SettingsName = "settings";
    public const string DeveloperName = "developer";

    private readonly IAppHost _host;
    private readonly Sidebar _sidebar = new();
    private readonly ContentControl _pageHost = new() { Focusable = false };
    private readonly Fluent.SnackbarPresenter _snackbars = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(24) };
    private readonly Fluent.ContentDialogHost _dialogs = new();
    private PageBase? _page;

    public MainWindow(IAppHost host, string page = HomeName)
    {
        WindowSpeed.Attach(this, "Main window");
        WpfApp.Ensure();
        _host = host;
        Title = "Midfielder";
        Icon = AppIcon.Image(32);
        Width = 1100;
        Height = 740;
        MinWidth = 680;
        MinHeight = 500;
        // Never larger than the screen (a small laptop at 150% has well under 1100x740 to offer).
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width * 0.92));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height * 0.92));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // A solid background, not Mica: the first frame is already our colour (Mica shows white or black until DWM
        // draws it, on every restore too) and the window doesn't change look when it loses focus.
        WindowBackdropType = Fluent.WindowBackdropType.None;
        Background = Fx.WindowBackground;
        WindowCornerPreference = Fluent.WindowCornerPreference.Round;
        ExtendsContentIntoTitleBar = true;
        FontFamily = Fx.Font;
        AllowDrop = true;
        RestoreSavedBounds();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = new Fluent.TitleBar
        {
            Title = "Midfielder",
            Icon = new Fluent.ImageIcon { Source = AppIcon.Image(32) },
            ShowHelp = false,
        };
        root.Children.Add(titleBar);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        body.Children.Add(_sidebar);
        // The content "layer" sits on the Mica backdrop like Windows 11 Settings: rounded top-left corner.
        var layer = new Border
        {
            Background = Fx.Layer,
            BorderBrush = Fx.Outline,
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(10, 0, 0, 0),
            Child = new Grid { Children = { _pageHost, _snackbars } },
            ClipToBounds = true,
        };
        Grid.SetColumn(layer, 1);
        body.Children.Add(layer);
        root.Children.Add(body);
        Grid.SetRowSpan(_dialogs, 2);
        root.Children.Add(_dialogs);
        Content = root;

        _sidebar.Add(HomeName, "Home", Fluent.SymbolRegular.Home24);
        _sidebar.Add(FilesName, "Files", Fluent.SymbolRegular.Folder24);
        _sidebar.Add(SettingsName, "Settings", Fluent.SymbolRegular.Settings24);
        _sidebar.Add(DeveloperName, "Developer", Fluent.SymbolRegular.Code24, footer: true);
        _sidebar.Selected += ShowPage;

        SizeChanged += (_, _) => _sidebar.Compact = ActualWidth < 900;
        _host.StatusChanged += OnStatus;
        UpdateStatus();
        ShowPage(page);
        AppLog.Info("Main window created");
    }

    public string CurrentPage { get; private set; } = "";

    /// <summary>The tray's window hides on close instead of closing (set false to really close, e.g. on exit).</summary>
    public bool HideOnClose { get; set; }

    /// <summary>Shows the window again after it was hidden, on <paramref name="page"/> or the page it was on.</summary>
    public void Reopen(string? page)
    {
        if (_page is null) CurrentPage = "";
        ShowPage(page ?? (CurrentPage.Length > 0 ? CurrentPage : _lastPage));
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // A window shown again appears before WPF has drawn into it (a blank white frame for a moment): keep it
        // cloaked until two frames have rendered, then reveal it complete.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.Cloak(hwnd, true);
        Show();
        Activate();
        int frames = 0;
        var fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        void Reveal()
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
            fallback.Stop();
            NativeMethods.Cloak(hwnd, false);
        }
        void OnFrame(object? sender, EventArgs e)
        {
            if (++frames >= 3) Reveal(); // the event runs before each frame: after the 3rd, two are on screen
        }
        System.Windows.Media.CompositionTarget.Rendering += OnFrame;
        fallback.Tick += (_, _) => Reveal();
        fallback.Start();
    }

    private string _lastPage = HomeName;

    public PageBase? Page => _page;

    public void ShowPage(string name)
    {
        if (name is not (HomeName or FilesName or SettingsName or DeveloperName)) name = HomeName;
        if (name == CurrentPage && _page is not null) return;
        CurrentPage = name;
        _sidebar.Select(name);
        var old = _page;
        _page = name switch
        {
            FilesName => new FilesPage(_host, this),
            SettingsName => new SettingsPage(_host, this),
            DeveloperName => new DeveloperPage(_host, this),
            _ => new HomePage(_host, this),
        };
        _pageHost.Content = _page;
        old?.Dispose();
        Anim.Enter(_page, 0, 18, 280);
    }

    private void OnStatus() => Dispatcher.BeginInvoke(UpdateStatus);

    private void UpdateStatus() => _sidebar.SetStatus(_host.StatusText, _host.ConnectedDevices.Count > 0);

    // --- Dialogs and toasts used by the pages ------------------------------------------------------

    /// <summary>Fluent confirmation dialog inside the window. True when the primary button was chosen.</summary>
    public async Task<bool> ConfirmAsync(string title, string message, string primary, bool danger = false)
    {
        var dialog = new Fluent.ContentDialog(_dialogs)
        {
            Title = title,
            Content = Fx.Text(message, 14, wrap: true),
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            PrimaryButtonAppearance = danger ? Fluent.ControlAppearance.Danger : Fluent.ControlAppearance.Primary,
            DefaultButton = Fluent.ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == Fluent.ContentDialogResult.Primary;
    }

    /// <summary>Short message at the bottom; with <paramref name="undo"/>, an Undo button.</summary>
    public void Toast(string message, Action? undo = null)
    {
        UIElement content = Fx.Text(message, 14);
        if (undo is not null)
        {
            Fluent.Button? button = null;
            button = Fx.Subtle("Undo", Fluent.SymbolRegular.ArrowUndo24, () =>
            {
                undo();
                button!.IsEnabled = false;
                button.Content = "Undone";
            });
            content = Fx.Split(content, button);
        }
        var bar = new Fluent.Snackbar(_snackbars)
        {
            Title = "Midfielder",
            Content = content,
            Appearance = Fluent.ControlAppearance.Secondary,
            Icon = new Fluent.SymbolIcon { Symbol = Fluent.SymbolRegular.Checkmark24 },
            Timeout = TimeSpan.FromSeconds(undo is null ? 3 : 6),
        };
        bar.Show();
    }

    // --- Window behavior ----------------------------------------------------------------------------

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Handled || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var existing = files.Where(File.Exists).ToList();
        if (existing.Count == 0) return;
        _host.SendFiles(existing);
        Toast(existing.Count == 1 ? $"Sending {System.IO.Path.GetFileName(existing[0])}" : $"Sending {existing.Count} files");
    }

    private void RestoreSavedBounds()
    {
        var saved = _host.Settings.WindowBounds;
        if (saved is null || saved.Length != 5) return;
        var area = SystemParameters.WorkArea;
        double w = Math.Clamp(saved[2], MinWidth, Math.Max(MinWidth, area.Width));
        double h = Math.Clamp(saved[3], MinHeight, Math.Max(MinHeight, area.Height));
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // The title bar must be on a screen that is still there; otherwise open centered as usual.
        if (!screen.Contains(new Point(saved[0] + 40, saved[1] + 16)) || !screen.Contains(new Point(saved[0] + w - 40, saved[1] + 16))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved[0];
        Top = Math.Max(screen.Top, Math.Min(saved[1], screen.Bottom - h));
        Width = w;
        Height = h;
        if (saved[4] == 1) WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _host.Settings.WindowBounds = [(int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, WindowState == WindowState.Maximized ? 1 : 0];
        _host.SaveSettings();
        base.OnClosing(e);
        if (e.Cancel || !HideOnClose) return;
        e.Cancel = true;
        Hide();
        // Drop the page: its events, timers and animations stop; it's rebuilt (fast, everything is warm) on reopen.
        _lastPage = CurrentPage;
        _page?.Dispose();
        _page = null;
        _pageHost.Content = null;
        CurrentPage = "";
    }

    protected override void OnClosed(EventArgs e)
    {
        _host.StatusChanged -= OnStatus;
        _page?.Dispose();
        _page = null;
        _pageHost.Content = null;
        base.OnClosed(e);
    }
}
