namespace Midfielder.App.Ui;

/// <summary>Sync switches, Explorer integration, the receive folder, the paired phone and statistics.</summary>
internal sealed class SettingsPage : Page
{
    private readonly Card _devices = new();
    private readonly Label _folder;

    public SettingsPage(IAppHost host) : base(host)
    {
        var s = host.Settings;
        Controls.Add(Header("Settings", null));

        var sync = new Card { Spacing = 4 };
        sync.Controls.Add(Labels.Heading("Sync"));
        sync.Controls.Add(Toggle("Sync clipboard", "Copy on one device, paste on the other", s.SyncClipboard, host.SetSyncClipboard));
        sync.Controls.Add(Toggle("Accept files automatically", "Otherwise Midfielder asks before each transfer", s.AutoAcceptFiles, host.SetAutoAccept));
        Controls.Add(sync);

        var shell = new Card { Spacing = 4 };
        shell.Controls.Add(Labels.Heading("Windows"));
        shell.Controls.Add(Toggle("Start with Windows", "Runs quietly in the tray, in efficiency mode", s.StartWithWindows, host.SetStartWithWindows));
        shell.Controls.Add(Toggle("\"Send to phone\" in the right-click menu", "Right-click any file in Explorer (Windows 11: Show more options)", s.ContextMenu, host.SetContextMenu));
        Controls.Add(shell);

        var files = new Card();
        files.Controls.Add(Labels.Heading("Received files"));
        _folder = Labels.Make(s.ReceiveFolder, Theme.Body(10f), Theme.Text);
        var change = new PillButton("Change…", ButtonKind.Outline);
        change.Click += (_, _) => ChooseFolder();
        var open = new PillButton("Open", ButtonKind.Tonal, Theme.Glyph.Folder);
        open.Click += (_, _) => Shell.OpenFolder(Host.Settings.ReceiveFolder);
        files.Controls.Add(new HStack { Fill = _folder }.With(_folder, change, open));
        Controls.Add(files);

        Controls.Add(_devices);

        var stats = new Card();
        stats.Controls.Add(Labels.Heading("Statistics"));
        var reset = new PillButton("Reset statistics", ButtonKind.Outline);
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(FindForm(), "Reset clip and file counts to zero?", "Midfielder", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            {
                Host.History.ClearStats();
            }
        };
        var clear = new PillButton("Clear file history", ButtonKind.Outline);
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(FindForm(), "Clear the list of sent and received files? The files themselves stay.", "Midfielder", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            {
                Host.History.ClearHistory();
            }
        };
        stats.Controls.Add(new HStack().With(reset, clear));
        Controls.Add(stats);
        RenderDevices();
    }

    private static HStack Toggle(string title, string detail, bool on, Action<bool> changed)
    {
        var sw = new ToggleSwitch(on) { AccessibleName = title };
        sw.CheckedChanged += (_, _) => changed(sw.Checked);
        return SettingRow(title, detail, sw);
    }

    public override void Activate() => Host.StatusChanged += OnStatus;

    public override void Deactivate() => Host.StatusChanged -= OnStatus;

    private void OnStatus() => Post(RenderDevices);

    private void RenderDevices()
    {
        _devices.SuspendLayout();
        foreach (Control c in _devices.Controls.Cast<Control>().ToList()) c.Dispose();
        _devices.Controls.Clear();
        _devices.Controls.Add(Labels.Heading("Paired phone"));
        var connected = Host.ConnectedDevices.Select(d => d.Fingerprint).ToHashSet();
        if (Host.PairedDevices.Count == 0) _devices.Controls.Add(Labels.Muted("No phone paired yet.", 10f));
        foreach (var d in Host.PairedDevices)
        {
            bool online = connected.Contains(d.Fingerprint);
            var text = new VStack { AutoHeight = true, Padding = Padding.Empty, Spacing = 2, BackColor = Theme.Surface };
            text.Controls.Add(Labels.Make(d.Name, Theme.Strong(10.5f), Theme.Text));
            text.Controls.Add(Labels.Muted(online ? "Connected" + (Host.AddressOf(d) is { } a ? $" · {a}" : "") : "Not connected right now"));
            _devices.Controls.Add(new HStack { Fill = text, Spacing = 14 }.With(new GlyphBadge(Theme.Glyph.Phone, 40), text));
        }
        var pair = new PillButton(Host.PairedDevices.Count == 0 ? "Pair a phone" : "Pair another phone", ButtonKind.Primary, Theme.Glyph.Link);
        pair.Click += (_, _) => Host.ShowPairing();
        var buttons = new HStack().With(pair);
        if (Host.PairedDevices.Count > 0)
        {
            var forget = new PillButton("Forget", ButtonKind.Danger, Theme.Glyph.Delete);
            forget.Click += (_, _) => Host.ForgetDevices();
            buttons.Controls.Add(forget);
        }
        _devices.Controls.Add(buttons);
        _devices.ResumeLayout();
        PerformLayout();
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Where should files from your phone go?",
            UseDescriptionForTitle = true,
            SelectedPath = Host.Settings.ReceiveFolder,
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        Host.SetReceiveFolder(dialog.SelectedPath);
        _folder.Text = dialog.SelectedPath;
        PerformLayout();
    }
}
