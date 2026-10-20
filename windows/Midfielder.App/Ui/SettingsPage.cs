using System.Windows;
using System.Windows.Controls;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Ui;

/// <summary>Windows 11 Settings-style cards: sync, Windows integration, received files, paired phone, data.</summary>
internal sealed class SettingsPage : PageBase
{
    private readonly StackPanel _devices = new();
    private readonly TextBlock _folder = Fx.Caption("");
    private readonly TextBlock _menuState = Fx.Caption("", wrap: true);
    private readonly Fluent.ToggleSwitch _menuSwitch = new();
    private bool _updatingMenu;

    public SettingsPage(IAppHost host, MainWindow window) : base(host, window)
    {
        var s = host.Settings;
        Add(Header("Settings", null), 0);

        Add(Section("Sync"), 20);
        Add(Toggle(Fluent.SymbolRegular.ClipboardPaste24, "Sync clipboard", "Copy on one device, paste on the other", s.SyncClipboard, host.SetSyncClipboard), 12);
        Add(Toggle(Fluent.SymbolRegular.ArrowDownload24, "Accept files automatically", "Otherwise Midfielder asks before each transfer", s.AutoAcceptFiles, host.SetAutoAccept), 4);

        Add(Section("Windows"), 20);
        Add(Toggle(Fluent.SymbolRegular.Power24, "Start with Windows", "Runs quietly in the tray, in efficiency mode", s.StartWithWindows, host.SetStartWithWindows), 12);
        _menuSwitch.Checked += (_, _) => _ = SetModernMenu(true);
        _menuSwitch.Unchecked += (_, _) => _ = SetModernMenu(false);
        Add(Row(Fluent.SymbolRegular.CursorClick24, "\"Send to phone\" in the Windows 11 right-click menu",
            Fx.Stack(2, Fx.Caption("Right-click any file or folder in Explorer — no \"Show more options\" needed"), _menuState), _menuSwitch), 4);
        Add(Toggle(Fluent.SymbolRegular.WindowNew24, "Classic right-click entry", "Under \"Show more options\" (always available)", s.ContextMenu, host.SetContextMenu), 4);

        Add(Section("Received files"), 20);
        _folder.Text = s.ReceiveFolder;
        Add(Row(Fluent.SymbolRegular.FolderOpen24, "Save received files to", _folder,
            Fx.Row(8, Fx.Secondary("Change…", null, ChooseFolder), Fx.Secondary("Open", Fluent.SymbolRegular.Open24, () => Shell.OpenFolder(Host.Settings.ReceiveFolder)))), 12);

        Add(Section("Paired phone"), 20);
        Add(_devices, 12);

        Add(Section("Data"), 20);
        Add(Row(Fluent.SymbolRegular.DataBarVertical24, "Statistics", Fx.Caption("Clip and file counts shown on Home"),
            Fx.Secondary("Reset", null, () => _ = Reset())), 12);
        Add(Row(Fluent.SymbolRegular.History24, "File history", Fx.Caption("The list on the Files page; the files themselves stay"),
            Fx.Secondary("Clear", null, () => _ = ClearHistory())), 4);

        Host.StatusChanged += OnStatus;
        RenderDevices();
        RenderMenuState();
    }

    /// <summary>A Settings row: icon, title and description on the left, a control on the right.</summary>
    private static Border Row(Fluent.SymbolRegular icon, string title, UIElement description, UIElement trailing)
    {
        var text = Fx.Stack(2, Fx.Text(title, 14.5, FontWeights.SemiBold), description);
        var line = Fx.Line(Fx.Badge(icon, 40), text, trailing, 16);
        var card = Fx.Card(line, 16);
        card.Padding = new Thickness(18, 14, 18, 14);
        return card;
    }

    private static Border Toggle(Fluent.SymbolRegular icon, string title, string description, bool on, Action<bool> changed)
    {
        var sw = new Fluent.ToggleSwitch { IsChecked = on };
        System.Windows.Automation.AutomationProperties.SetName(sw, title);
        sw.Checked += (_, _) => changed(true);
        sw.Unchecked += (_, _) => changed(false);
        return Row(icon, title, Fx.Caption(description, wrap: true), sw);
    }

    private void OnStatus() => Post(RenderDevices);

    private void RenderDevices()
    {
        _devices.Children.Clear();
        var connected = Host.ConnectedDevices.Select(d => d.Fingerprint).ToHashSet();
        var pair = Fx.Primary(Host.PairedDevices.Count == 0 ? "Pair a phone" : "Pair another phone", Fluent.SymbolRegular.QrCode24, Host.ShowPairing);
        if (Host.PairedDevices.Count == 0)
        {
            _devices.Children.Add(Row(Fluent.SymbolRegular.Phone24, "No phone paired yet", Fx.Caption("Open Midfielder on your phone and scan the code"), pair));
            return;
        }
        foreach (var d in Host.PairedDevices)
        {
            bool online = connected.Contains(d.Fingerprint);
            var status = Fx.Caption(online ? "Connected" + (Host.AddressOf(d) is { } a ? $" · {a}" : "") : "Not connected right now");
            var forget = Fx.Button("Forget", Fluent.SymbolRegular.Delete24, Fluent.ControlAppearance.Danger, Host.ForgetDevices);
            var row = Row(Fluent.SymbolRegular.Phone24, d.Name, status, Fx.Row(8, pair, forget));
            Fx.Add(_devices, row, 4);
            pair = Fx.Primary("Pair another phone", Fluent.SymbolRegular.QrCode24, Host.ShowPairing);
        }
    }

    private void RenderMenuState()
    {
        _updatingMenu = true;
        var state = Host.ModernMenu;
        _menuSwitch.IsChecked = state is ModernMenuStatus.On or ModernMenuStatus.NeedsRepair;
        _menuSwitch.IsEnabled = state != ModernMenuStatus.Unavailable;
        _menuState.Text = state switch
        {
            ModernMenuStatus.On => "On. Keep Midfielder.exe in this folder.",
            ModernMenuStatus.NeedsRepair => "Midfielder.exe was moved: turn it off and on again to fix.",
            ModernMenuStatus.Unavailable => "Needs Windows 11 and the Midfielder.msix file next to Midfielder.exe.",
            _ => "Asks once for permission to trust Midfielder's certificate.",
        };
        _menuState.Foreground = state == ModernMenuStatus.NeedsRepair ? Fx.Danger : Fx.Muted;
        _updatingMenu = false;
    }

    private async Task SetModernMenu(bool on)
    {
        if (_updatingMenu) return;
        _menuSwitch.IsEnabled = false;
        string message = await Host.SetModernMenuAsync(on);
        if (Disposed) return;
        RenderMenuState();
        Window.Toast(message);
    }

    private void ChooseFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Where should files from your phone go?",
            UseDescriptionForTitle = true,
            SelectedPath = Host.Settings.ReceiveFolder,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        Host.SetReceiveFolder(dialog.SelectedPath);
        _folder.Text = dialog.SelectedPath;
    }

    private async Task Reset()
    {
        if (!await Window.ConfirmAsync("Reset statistics?", "Clip and file counts start again from zero.", "Reset", danger: true)) return;
        Host.History.ClearStats();
        Window.Toast("Statistics reset");
    }

    private async Task ClearHistory()
    {
        if (!await Window.ConfirmAsync("Clear file history?", "The list on the Files page is cleared. The files themselves stay where they are.", "Clear", danger: true)) return;
        Host.History.ClearHistory();
        Window.Toast("File history cleared");
    }

    protected override void OnDispose() => Host.StatusChanged -= OnStatus;
}
