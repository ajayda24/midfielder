using Midfielder.Core.Feature;
using System.Windows;
using System.Windows.Controls;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Ui;

/// <summary>Windows 11 Settings-style cards: sync, features, Windows integration, received files, paired phone, data.</summary>
internal sealed class SettingsPage : PageBase
{
    private readonly StackPanel _devices = new();
    private readonly TextBlock _folder = Fx.Caption("");
    private readonly TextBlock _menuState = Fx.Caption("", wrap: true);
    private readonly Fluent.ToggleSwitch _menuSwitch = new();
    private readonly StackPanel _features = new();
    private bool _updatingMenu;

    public SettingsPage(IAppHost host, MainWindow window) : base(host, window)
    {
        var s = host.Settings;
        Add(Header("Settings", null), 0);

        Add(Section("Sync"), 20);
        Add(Toggle(Fluent.SymbolRegular.ClipboardPaste24, "Sync clipboard", "Copy on one device, paste on the other", s.SyncClipboard, host.SetSyncClipboard), 12);
        Add(Toggle(Fluent.SymbolRegular.ArrowDownload24, "Accept files automatically", "Otherwise Midfielder asks before each transfer", s.AutoAcceptFiles, host.SetAutoAccept), 4);

        Add(Section("Features"), 20);
        Add(Fx.Caption("Each feature works when it's on here and in Midfielder on your phone. Off means it doesn't run at all.", wrap: true), 6);
        Add(_features, 0);
        RenderFeatures();

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
        Host.PhoneChanged += OnPhone;
        Host.PresenceChanged += OnPresence;
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

    private readonly List<(FeatureCard Card, string Capability, Func<bool> On)> _cards = [];

    /// <summary>
    /// One card per feature, built once; switching animates the card's options open or closed in place. Each card's
    /// status line says whether the feature actually works right now (on here, and on the phone too).
    /// </summary>
    private void RenderFeatures()
    {
        var f = Host.Settings.Features;
        _features.Children.Clear();
        _cards.Clear();
        FeatureCard Card(Fluent.SymbolRegular icon, string title, string description, string capability, Func<bool> on, Action<bool> set)
        {
            var card = new FeatureCard(icon, title, description, on(), v =>
            {
                set(v);
                Host.ApplyFeatures();
                RenderFeatureStatus();
            });
            _cards.Add((card, capability, on));
            Fx.Add(_features, card, 12);
            return card;
        }
        Action<bool> Save(Action<bool> set) => v =>
        {
            set(v);
            Host.ApplyFeatures();
        };

        Card(Fluent.SymbolRegular.Alert24, "Phone notifications", "See your phone's notifications here, as Windows notifications",
                Features.Notify, () => f.Notify, v => f.Notify = v)
            .OptionToggle("Reply and actions", "A reply box and the notification's own buttons, right in the Windows notification",
                f.NotifyReply, Save(v => f.NotifyReply = v));

        var level = new ComboBox { MinWidth = 100, ItemsSource = new[] { "80%", "90%", "100%" }, SelectedItem = $"{f.BatteryFullAt}%" };
        System.Windows.Automation.AutomationProperties.SetName(level, "Charged alert level");
        level.SelectionChanged += (_, _) =>
        {
            if (level.SelectedItem is string text && int.TryParse(text.TrimEnd('%'), out int v) && v != f.BatteryFullAt)
            {
                f.BatteryFullAt = v;
                Host.ApplyFeatures();
            }
        };
        Card(Fluent.SymbolRegular.Battery524, "Battery sharing", "Your phone's battery here, and this PC's on the phone",
                Features.Battery, () => f.Battery, v => f.Battery = v)
            .OptionToggle("Phone battery alerts", "When it's charged (unplug to protect the battery) and when it's low", f.BatteryAlerts, Save(v => f.BatteryAlerts = v))
            .Option("Charged alert at", "80% is kindest to the battery", level);

        Card(Fluent.SymbolRegular.Speaker224, "Find my phone", "Ring your phone at full volume, even on silent, and let it ring this PC",
            Features.Ring, () => f.Ring, v => f.Ring = v);

        Card(Fluent.SymbolRegular.MusicNote224, "Music control", "Play, pause and skip across your phone and this PC",
                Features.Media, () => f.Media, v => f.Media = v)
            .OptionToggle("Phone music on this PC", "In the Windows media controls, with album art and media keys", f.MediaPhoneToPc, Save(v => f.MediaPhoneToPc = v))
            .OptionToggle("This PC's music on the phone", "In the phone's media controls and on its lock screen", f.MediaPcToPhone, Save(v => f.MediaPcToPhone = v));

        _btDevices = new ComboBox { MinWidth = 180, DisplayMemberPath = nameof(Continuity.BluetoothAudioSource.Name) };
        System.Windows.Automation.AutomationProperties.SetName(_btDevices, "Phone for Bluetooth audio");
        _btDevices.SelectionChanged += (_, _) =>
        {
            if (_btDevices.SelectedItem is Continuity.BluetoothAudioSource d && d.Id != f.AudioBluetoothId)
            {
                f.AudioBluetoothId = d.Id;
                Host.SaveSettings();
            }
        };
        _btHint = Fx.Secondary("Bluetooth settings", Fluent.SymbolRegular.Bluetooth24, () => Shell.OpenUri("ms-settings:bluetooth"));
        Card(Fluent.SymbolRegular.HeadphonesSoundWave24, "Audio", "Play your phone's sound on this PC's speakers, and this PC's sound on your phone",
                Features.Audio, () => f.Audio, v => f.Audio = v)
            .OptionToggle("Phone audio on this PC", "This PC becomes your phone's Bluetooth speaker: every app, full quality", f.AudioPhoneToPc, Save(v => f.AudioPhoneToPc = v))
            .Option("Phone to use", "Pair it with this PC over Bluetooth once, in Windows settings", Fx.Row(8, _btDevices, _btHint))
            .OptionToggle("This PC's audio on the phone", "Streams over Wi-Fi with a short delay: great for music, not for video", f.AudioPcToPhone, Save(v => f.AudioPcToPhone = v))
            .OptionToggle("Mute this PC while it plays on the phone", "So the sound only comes from the phone", f.AudioMutePc, Save(v => f.AudioMutePc = v));

        // Calls: the experimental "talk through this PC" switch turns itself back off when Windows refuses.
        var talk = new Fluent.ToggleSwitch { IsChecked = f.CallsTalkOnPc };
        System.Windows.Automation.AutomationProperties.SetName(talk, "Talk through this PC");
        _callAudioNote = Fx.Caption("Experimental. Uses this PC's microphone and speakers for the call (Bluetooth hands-free)", wrap: true);
        talk.Checked += async (_, _) =>
        {
            f.CallsTalkOnPc = true;
            Host.ApplyFeatures();
            _callAudioNote.Text = "Connecting…";
            string? problem = await Host.ConnectCallAudioAsync().ConfigureAwait(true);
            _callAudioNote.Text = problem ?? "Ready: answered calls use this PC's microphone and speakers";
            if (problem is not null) talk.IsChecked = false;
        };
        talk.Unchecked += (_, _) =>
        {
            f.CallsTalkOnPc = false;
            Host.ApplyFeatures();
        };
        Card(Fluent.SymbolRegular.Call24, "Calls", "See who's calling as a ringing Windows notification; answer or decline from here",
                Features.Call, () => f.Calls, v => f.Calls = v)
            .OptionToggle("Answer and decline buttons", "Also Text back and Silence, when the phone allows them", f.CallsActions, Save(v => f.CallsActions = v))
            .Option("Talk through this PC", _callAudioNote, talk);

        var side = new ComboBox { MinWidth = 100, ItemsSource = new[] { "Right", "Left" }, SelectedItem = f.EdgeDropSide == "left" ? "Left" : "Right" };
        System.Windows.Automation.AutomationProperties.SetName(side, "Screen edge");
        side.SelectionChanged += (_, _) =>
        {
            f.EdgeDropSide = side.SelectedItem as string == "Left" ? "left" : "right";
            Host.ApplyFeatures();
        };
        Card(Fluent.SymbolRegular.ArrowExportLtr24, "Screen-edge drop", "Drag files to the edge of the screen and drop them on your phone",
                "", () => f.EdgeDrop, v => f.EdgeDrop = v)
            .Option("Screen edge", "Where the drop target slides in", side);

        var near = new Slider { Minimum = -90, Maximum = -50, TickFrequency = 5, IsSnapToTickEnabled = true, Value = f.PresenceNearRssi, Width = 180 };
        System.Windows.Automation.AutomationProperties.SetName(near, "How near counts as near");
        var nearLabel = Fx.Caption(NearText(f.PresenceNearRssi));
        var save = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        save.Tick += (_, _) =>
        {
            save.Stop();
            Host.ApplyFeatures();
        };
        near.ValueChanged += (_, e) =>
        {
            f.PresenceNearRssi = (int)e.NewValue;
            nearLabel.Text = NearText(f.PresenceNearRssi);
            save.Stop();
            save.Start();
        };
        var delay = new ComboBox { MinWidth = 100, ItemsSource = new[] { "10 s", "20 s", "30 s", "60 s" }, SelectedItem = $"{f.PresenceDelaySeconds} s" };
        System.Windows.Automation.AutomationProperties.SetName(delay, "Lock after");
        delay.SelectionChanged += (_, _) =>
        {
            if (delay.SelectedItem is string t && int.TryParse(t.Split(' ')[0], out int v))
            {
                f.PresenceDelaySeconds = v;
                Host.ApplyFeatures();
            }
        };
        _presenceMeter = new ProgressBar { Minimum = -100, Maximum = -40, Width = 180, Height = 6 };
        _presenceText = Fx.Caption("", wrap: true);
        _presenceCard = Card(Fluent.SymbolRegular.LockClosed24, "Lock when phone walks away", "Your phone sends a tiny Bluetooth signal; this PC locks when it fades",
                Features.Presence, () => f.Presence, v => f.Presence = v)
            .Option("Phone signal", "Walk away and watch it drop to see where \"away\" starts", Fx.Stack(6, _presenceMeter, _presenceText))
            .Option("Near means", "Lower reaches further; higher locks sooner", Fx.Stack(4, near, nearLabel))
            .Option("Lock after", "How long the phone must be away", delay);

        RenderFeatureStatus();
    }

    private TextBlock _callAudioNote = new();
    private ProgressBar _presenceMeter = new();
    private TextBlock _presenceText = new();
    private FeatureCard? _presenceCard;

    private static string NearText(int rssi) => rssi switch
    {
        >= -60 => $"{rssi} dBm · same desk",
        >= -72 => $"{rssi} dBm · same room",
        _ => $"{rssi} dBm · a few rooms",
    };

    /// <summary>The auto-lock meter: live signal and what it means.</summary>
    private void RenderPresence()
    {
        var p = Host.Presence;
        _presenceMeter.Value = p.Signal ?? _presenceMeter.Minimum;
        _presenceText.Text = !p.On ? "Starts when the phone has the feature on too"
            : p.Problem ?? p.State switch
            {
                Midfielder.Core.Feature.PresenceState.Near => $"{p.Signal:0} dBm · near",
                Midfielder.Core.Feature.PresenceState.Leaving => p.Signal is { } sig ? $"{sig:0} dBm · away: locking in {p.Remaining.TotalSeconds:0} s" : $"Not heard · locking in {p.Remaining.TotalSeconds:0} s",
                Midfielder.Core.Feature.PresenceState.Locked => "Locked. Unlock with the phone nearby to start again",
                _ => p.Signal is { } s ? $"{s:0} dBm · waiting for the phone to come near" : "Listening for your phone…",
            };
    }

    private ComboBox _btDevices = new();
    private Fluent.Button _btHint = new();

    private void RenderBluetooth()
    {
        var audio = Host.Audio;
        var selected = Host.Settings.Features.AudioBluetoothId;
        if (!audio.BluetoothDevices.Select(d => d.Id).SequenceEqual(_btDevices.Items.OfType<Continuity.BluetoothAudioSource>().Select(d => d.Id)))
        {
            _btDevices.ItemsSource = audio.BluetoothDevices;
        }
        _btDevices.SelectedItem = audio.BluetoothDevices.FirstOrDefault(d => d.Id == selected)
            ?? audio.BluetoothDevices.FirstOrDefault(d => Host.Phone.Name is { } n && d.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
        _btDevices.Visibility = audio.BluetoothDevices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _btHint.Visibility = audio.BluetoothDevices.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderFeatureStatus()
    {
        RenderBluetooth();
        var phone = Host.Phone;
        bool connected = phone.Name is not null;
        RenderPresence();
        foreach (var (card, capability, on) in _cards)
        {
            if (!on()) card.SetStatus("Off", FeatureStatus.Off);
            else if (capability.Length == 0) card.SetStatus(connected ? "On · drag files to the screen edge" : "On · works when your phone is connected",
                connected ? FeatureStatus.Working : FeatureStatus.Waiting);
            else if (!connected) card.SetStatus("On · works when your phone is connected", FeatureStatus.Waiting);
            else if (!phone.Capabilities.Contains(capability)) card.SetStatus("Turn it on in Midfielder on your phone too", FeatureStatus.Waiting);
            else card.SetStatus("On · working with " + phone.Name, FeatureStatus.Working);
        }
    }

    private void OnPhone() => Post(RenderFeatureStatus);

    private void OnPresence() => Post(RenderPresence);

    private void OnStatus() => Post(RenderDevices);

    private string? _devicesKey;

    private void RenderDevices()
    {
        // Status changes after every transfer and ping: rebuild only when the devices or who's connected changed.
        string key = string.Join('|', Host.PairedDevices.Select(d => d.Fingerprint.Value + d.Name)) + "#" +
                     string.Join('|', Host.ConnectedDevices.Select(d => d.Fingerprint.Value));
        if (key == _devicesKey) return;
        _devicesKey = key;
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
            ModernMenuStatus.Unavailable => "Needs Windows 11, with MidfielderShell.dll next to Midfielder.exe.",
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

    protected override void OnDispose()
    {
        Host.StatusChanged -= OnStatus;
        Host.PhoneChanged -= OnPhone;
        Host.PresenceChanged -= OnPresence;
    }
}
