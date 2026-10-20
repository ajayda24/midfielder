using System.Globalization;
using System.Reflection;

namespace Midfielder.App.Ui;

/// <summary>
/// Diagnostics and the live log, kept out of the way of everyday use. Docked layout: a compact status card on
/// top, the log fills the rest of the window with its own scrollbar (no page scrolling, no empty tail).
/// </summary>
internal sealed class DeveloperPage : Panel
{
    private readonly IAppHost _host;
    private readonly VStack _top = new() { Dock = DockStyle.Top, Padding = Padding.Empty, Spacing = 14 };
    private readonly Card _status = new() { Columns = 2, MinColumnWidth = 300, Spacing = 6 };
    private readonly HStack _toolbar;
    private readonly ListView _log = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.None,
        BorderStyle = BorderStyle.None,
        Font = Theme.Mono(9f),
        BackColor = Theme.Surface,
        ForeColor = Theme.Text,
        Dock = DockStyle.Fill,
        ShowItemToolTips = true,
    };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter log…", BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body(10f) };
    private readonly ToggleSwitch _errorsOnly = new(false) { AccessibleName = "Errors only" };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 300 };

    public DeveloperPage(IAppHost host)
    {
        _host = host;
        BackColor = Theme.Background;
        _filter.Width = Measure.Px(this, 220);

        var header = new VStack { Spacing = 2, Padding = Padding.Empty };
        header.Controls.Add(Labels.Title("Developer"));
        header.Controls.Add(Labels.Muted("Diagnostics for when something doesn't sync", 10f));
        _top.Controls.Add(header);
        _top.Controls.Add(_status);

        var copy = new PillButton("Copy", ButtonKind.Outline, Theme.Glyph.Copy);
        copy.Click += (_, _) => CopyLog();
        var openFile = new PillButton("Open log file", ButtonKind.Outline, Theme.Glyph.OpenFile);
        openFile.Click += (_, _) => Shell.OpenFile(AppLog.Path);
        var clear = new PillButton("Clear view", ButtonKind.Subtle);
        clear.Click += (_, _) =>
        {
            AppLog.ClearRecent();
            RenderLog();
        };
        var title = Labels.Heading("Activity log");
        _toolbar = new HStack { Dock = DockStyle.Top, Fill = title, Wrap = true, MinFillWidth = 120, Padding = new Padding(0, 0, 0, 8) }
            .With(title, _filter, Labels.Make("Errors only", Theme.Body(9f), Theme.Muted, singleLine: true), _errorsOnly, copy, openFile, clear);
        var logCard = new Surface { Dock = DockStyle.Fill, Padding = new Padding(Measure.Px(this, 16)) };
        logCard.Controls.Add(_log);
        logCard.Controls.Add(_toolbar);

        Controls.Add(logCard);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = Measure.Px(this, 14), BackColor = Theme.Background });
        Controls.Add(_top);

        _log.Columns.Add("Time", Measure.Px(this, 100));
        _log.Columns.Add("Message", Measure.Px(this, 800));
        _log.Resize += (_, _) => _log.Columns[1].Width = Math.Max(Measure.Px(this, 200), _log.ClientSize.Width - _log.Columns[0].Width - 4);

        _filter.TextChanged += (_, _) => RenderLog();
        _errorsOnly.CheckedChanged += (_, _) => RenderLog();
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            RenderLog();
        };
        _host.StatusChanged += OnStatus;
        AppLog.Logged += OnLogged;
        RenderStatus();
        RenderLog();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        Measure.CenterColumn(this, 1100, 32, 24, 24);
        int w = ClientSize.Width - Padding.Horizontal;
        if (w > 0)
        {
            int top = _top.MeasureHeight(w);
            if (_top.Height != top) _top.Height = top;
            int tb = _toolbar.MeasureHeight(w - Measure.Px(this, 32));
            if (_toolbar.Height != tb) _toolbar.Height = tb;
        }
        base.OnLayout(levent);
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnStatus() => Post(RenderStatus);

    // Entries can arrive in bursts; redraw the list at most ~3 times a second.
    private void OnLogged(LogEntry e) => Post(() => { if (!_refresh.Enabled) _refresh.Start(); });

    private void RenderStatus()
    {
        _status.SuspendLayout();
        foreach (Control c in _status.Controls.Cast<Control>().ToList()) c.Dispose();
        _status.Controls.Clear();
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
        var connected = _host.ConnectedDevices;
        var rows = new (string, string)[]
        {
            ("Status", _host.StatusText),
            ("Version", version),
            ("Connected", connected.Count == 0 ? "—" : string.Join(", ", connected.Select(d => $"{d.Name} ({_host.AddressOf(d) ?? "?"})"))),
            ("Listening port", _host.Port.ToString(CultureInfo.InvariantCulture)),
            ("Paired", _host.PairedDevices.Count == 0 ? "—" : string.Join(", ", _host.PairedDevices.Select(d => d.Name))),
            ("Local addresses", string.Join(", ", LanDiscovery.LocalAddresses(_host.Port).Select(a => a.Host))),
            ("Device ID", _host.DeviceId),
            ("Fingerprint", _host.Fingerprint),
            ("Log file", AppLog.Path),
        };
        foreach (var (k, v) in rows)
        {
            var key = Labels.Make(k, Theme.Body(9f), Theme.Muted, singleLine: true);
            key.MinimumSize = new Size(Measure.Px(this, 110), 0);
            var value = Labels.Make(v, Theme.Mono(9f), Theme.Text);
            _status.Controls.Add(new HStack { Fill = value, Spacing = 8, MinFillWidth = 120 }.With(key, value));
        }
        _status.ResumeLayout();
        PerformLayout();
    }

    private void RenderLog()
    {
        string q = _filter.Text.Trim();
        var entries = AppLog.Entries()
            .Where(e => !_errorsOnly.Checked || e.IsError)
            .Where(e => q.Length == 0 || e.Message.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _log.BeginUpdate();
        _log.Items.Clear();
        foreach (var e in entries)
        {
            string firstLine = e.Message.Split('\n', 2)[0].TrimEnd('\r');
            _log.Items.Add(new ListViewItem([e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), firstLine])
            {
                ForeColor = e.IsError ? Theme.Danger : Theme.Text,
                BackColor = e.IsError ? Theme.DangerBg : Theme.Surface,
                ToolTipText = e.Message,
            });
        }
        _log.EndUpdate();
    }

    private static void CopyLog()
    {
        string text = string.Join(Environment.NewLine, AppLog.Entries().Select(e =>
            $"{e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {(e.IsError ? "ERROR" : "INFO ")} {e.Message}"));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.StatusChanged -= OnStatus;
            AppLog.Logged -= OnLogged;
            _refresh.Dispose();
        }
        base.Dispose(disposing);
    }
}
