using System.Globalization;

namespace Midfielder.App.Ui;

/// <summary>Connection, quick actions, drag-and-drop, live transfers and a small activity summary.</summary>
internal sealed class HomePage : Page
{
    private readonly Label _phoneName = Labels.Make("", Theme.Strong(13f), Theme.Text);
    private readonly Label _phoneStatus = Labels.Muted("", 10f);
    private readonly StatusDot _dot = new();
    private readonly PillButton _pair;
    private readonly Card _transfers = new() { Visible = false };
    private readonly VStack _transferRows = new() { AutoHeight = true, Padding = Padding.Empty, Spacing = 14, BackColor = Theme.Surface };
    private readonly Label _todayClips = Labels.Make("0", Theme.Title(20f), Theme.PrimaryDark);
    private readonly Label _todayFiles = Labels.Make("0", Theme.Title(20f), Theme.PrimaryDark);
    private readonly Label _todayBytes = Labels.Make("0 B", Theme.Title(20f), Theme.PrimaryDark);
    private readonly Label _week = Labels.Muted("");
    private readonly Label _allTime = Labels.Muted("");
    private readonly BarChart _chart = new();
    private readonly System.Windows.Forms.Timer _coalesce = new() { Interval = 200 };
    private readonly Dictionary<string, (Label Meta, ProgressStrip Bar)> _rowsById = new(StringComparer.Ordinal);

    public HomePage(IAppHost host) : base(host)
    {
        Controls.Add(Header("Home", "Your clipboard and files, on this PC and your phone"));

        // Connection card
        var conn = new Card();
        var texts = new VStack { AutoHeight = true, Padding = Padding.Empty, Spacing = 4, BackColor = Theme.Surface };
        texts.Controls.Add(_phoneName);
        texts.Controls.Add(new HStack { Spacing = 6 }.With(_dot, _phoneStatus));
        _pair = new PillButton("Pair a phone", ButtonKind.Primary, Theme.Glyph.Link);
        _pair.Click += (_, _) => Host.ShowPairing();
        var head = new HStack { Fill = texts, Spacing = 16 };
        conn.Controls.Add(head.With(new GlyphBadge(Theme.Glyph.Phone, 52), texts, _pair));
        Controls.Add(conn);

        // Actions
        var send = new PillButton("Send files…", ButtonKind.Primary, Theme.Glyph.Upload);
        send.Click += (_, _) => Host.PickAndSendFiles();
        var clip = new PillButton("Send clipboard now", ButtonKind.Tonal, Theme.Glyph.Paste);
        clip.Click += (_, _) => Host.SendClipboardNow();
        var folder = new PillButton("Received files", ButtonKind.Outline, Theme.Glyph.Folder);
        folder.Click += (_, _) => Shell.OpenFolder(Host.Settings.ReceiveFolder);
        Controls.Add(new HStack().With(send, clip, folder));

        var drop = new DropZone();
        drop.FilesDropped += files => Host.SendFiles(files);
        drop.Click += (_, _) => Host.PickAndSendFiles();
        Controls.Add(drop);

        // Live transfers
        _transfers.Controls.Add(Labels.Heading("Transfers"));
        _transfers.Controls.Add(_transferRows);
        Controls.Add(_transfers);

        // Activity
        var stats = new Card();
        stats.Controls.Add(Labels.Heading("Today"));
        stats.Controls.Add(new HStack { Spacing = 48 }.With(Stat(_todayClips, "clips synced"), Stat(_todayFiles, "files"), Stat(_todayBytes, "moved")));
        stats.Controls.Add(_chart);
        stats.Controls.Add(_week);
        stats.Controls.Add(_allTime);
        Controls.Add(stats);

        _coalesce.Tick += (_, _) =>
        {
            _coalesce.Stop();
            RenderTransfers();
        };
        RenderStatus();
        RenderTransfers();
        RenderStats();
    }

    private static Control Stat(Label value, string caption)
    {
        var box = new VStack { AutoHeight = true, Padding = Padding.Empty, Spacing = 0, BackColor = Theme.Surface, Width = (int)(140 * value.DeviceDpi / 96f) };
        box.Controls.Add(value);
        box.Controls.Add(Labels.Muted(caption));
        return box;
    }

    public override void Activate()
    {
        Host.StatusChanged += OnStatus;
        Host.Transfers.Changed += OnTransfers;
        Host.History.Changed += OnHistory;
    }

    public override void Deactivate()
    {
        Host.StatusChanged -= OnStatus;
        Host.Transfers.Changed -= OnTransfers;
        Host.History.Changed -= OnHistory;
    }

    private void OnStatus() => Post(RenderStatus);

    // Progress can tick several times a second per transfer; repaint at most every 200 ms.
    private void OnTransfers() => Post(() => { if (!_coalesce.Enabled) _coalesce.Start(); });

    private void OnHistory() => Post(RenderStats);

    private void RenderStatus()
    {
        var connected = Host.ConnectedDevices;
        var paired = Host.PairedDevices;
        _phoneName.Text = connected.Count > 0 ? string.Join(", ", connected.Select(d => d.Name))
            : paired.Count > 0 ? string.Join(", ", paired.Select(d => d.Name))
            : "No phone paired yet";
        _dot.On = connected.Count > 0;
        _dot.Visible = paired.Count > 0;
        _phoneStatus.Text = connected.Count > 0
            ? "Connected" + (Host.AddressOf(connected[0]) is { } a ? $" · {a}" : "")
            : paired.Count > 0 ? "Waiting for your phone on this network…" : "Click Pair a phone and scan the code with Midfielder on Android";
        _pair.Visible = paired.Count == 0;
        PerformLayout();
    }

    private void RenderTransfers()
    {
        var active = Host.Transfers.Active();
        _transfers.Visible = active.Count > 0;
        var ids = active.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        if (!ids.SetEquals(_rowsById.Keys))
        {
            _transferRows.SuspendLayout();
            foreach (Control c in _transferRows.Controls.Cast<Control>().ToList()) c.Dispose();
            _transferRows.Controls.Clear();
            _rowsById.Clear();
            foreach (var t in active) _transferRows.Controls.Add(Row(t));
            _transferRows.ResumeLayout();
        }
        foreach (var t in active)
        {
            if (!_rowsById.TryGetValue(t.Id, out var row)) continue;
            row.Meta.Text = Meta(t);
            row.Bar.Value = Math.Max(0, t.Percent);
        }
        PerformLayout();
    }

    private Control Row(ActiveTransfer t)
    {
        var box = new VStack { AutoHeight = true, Padding = Padding.Empty, Spacing = 6, BackColor = Theme.Surface };
        var name = Labels.Make(t.Title, Theme.Strong(10f), Theme.Text, autoSize: false);
        name.Height = (int)(22 * DeviceDpi / 96f);
        var meta = Labels.Muted(Meta(t));
        var texts = new VStack { AutoHeight = true, Padding = Padding.Empty, Spacing = 0, BackColor = Theme.Surface };
        texts.Controls.Add(name);
        texts.Controls.Add(meta);
        var cancel = new PillButton("", ButtonKind.Subtle, Theme.Glyph.Cancel) { AccessibleName = "Cancel transfer" };
        string id = t.Id;
        cancel.Click += (_, _) => Host.CancelTransfer(id);
        var glyph = new GlyphBadge(t.Direction == TransferDirection.Sent ? Theme.Glyph.Upload : Theme.Glyph.Download, 36);
        box.Controls.Add(new HStack { Fill = texts }.With(glyph, texts, cancel));
        var bar = new ProgressStrip { Value = Math.Max(0, t.Percent) };
        box.Controls.Add(bar);
        _rowsById[t.Id] = (meta, bar);
        return box;
    }

    private static string Meta(ActiveTransfer t)
    {
        string dir = t.Direction == TransferDirection.Sent ? "to" : "from";
        string pct = t.Percent >= 0 ? $" · {t.Percent}%" : "";
        return $"{Format.Bytes(t.Done)} of {Format.Bytes(t.Total)} {dir} {t.Peer}{pct}";
    }

    private void RenderStats()
    {
        var today = DateTime.Today;
        var days = Host.History.LastDays(7, today);
        var t = days[^1];
        _todayClips.Text = t.Clips.ToString(CultureInfo.InvariantCulture);
        _todayFiles.Text = t.Files.ToString(CultureInfo.InvariantCulture);
        _todayBytes.Text = Format.Bytes(t.Bytes);
        _chart.SetData(days.Select(d => d.Clips + d.Files).ToArray(),
            Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6).ToString("ddd", CultureInfo.InvariantCulture)).ToArray());
        var week = days.Aggregate(new Counts(), (a, b) => a + b);
        _week.Text = $"Last 7 days: {week.Clips} clips · {week.Files} files · {Format.Bytes(week.Bytes)}";
        var all = Host.History.Totals();
        _allTime.Text = $"All time: {all.ClipsSent} clips sent, {all.ClipsReceived} received · {all.FilesSent} files sent, {all.FilesReceived} received";
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _coalesce.Dispose();
        base.Dispose(disposing);
    }
}
