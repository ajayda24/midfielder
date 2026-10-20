using System.Globalization;
using System.Reflection;

namespace Midfielder.App.Ui;

/// <summary>Diagnostics and the live log, kept out of the way of everyday use.</summary>
internal sealed class DeveloperPage : Page
{
    private readonly Card _status = new() { Spacing = 6 };
    private readonly ListView _log = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
        Font = Theme.Mono(9f),
        BackColor = Theme.Surface,
        ForeColor = Theme.Text,
    };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter log…", BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body(10f) };
    private readonly ToggleSwitch _errorsOnly = new(false) { AccessibleName = "Errors only" };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 300 };

    public DeveloperPage(IAppHost host) : base(host)
    {
        Controls.Add(Header("Developer", "Diagnostics for when something doesn't sync"));
        Controls.Add(_status);

        var logCard = new Card();
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
        logCard.Controls.Add(new HStack { Fill = title }.With(title, _filter, Labels.Muted("Errors only"), _errorsOnly, copy, openFile, clear));
        _filter.Width = (int)(220 * DeviceDpi / 96f);
        _log.Columns.Add("Time", (int)(110 * DeviceDpi / 96f));
        _log.Columns.Add("Message", (int)(900 * DeviceDpi / 96f));
        _log.Height = (int)(420 * DeviceDpi / 96f);
        logCard.Controls.Add(_log);
        Controls.Add(logCard);

        _filter.TextChanged += (_, _) => RenderLog();
        _errorsOnly.CheckedChanged += (_, _) => RenderLog();
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            RenderLog();
        };
        _log.Resize += (_, _) => _log.Columns[1].Width = Math.Max(200, _log.ClientSize.Width - _log.Columns[0].Width - 4);
        RenderStatus();
        RenderLog();
    }

    public override void Activate()
    {
        Host.StatusChanged += OnStatus;
        AppLog.Logged += OnLogged;
    }

    public override void Deactivate()
    {
        Host.StatusChanged -= OnStatus;
        AppLog.Logged -= OnLogged;
    }

    private void OnStatus() => Post(RenderStatus);

    // Entries can arrive in bursts; redraw the list at most ~3 times a second.
    private void OnLogged(LogEntry e) => Post(() => { if (!_refresh.Enabled) _refresh.Start(); });

    private void RenderStatus()
    {
        _status.SuspendLayout();
        foreach (Control c in _status.Controls.Cast<Control>().ToList()) c.Dispose();
        _status.Controls.Clear();
        _status.Controls.Add(Labels.Heading("Status"));
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
        var connected = Host.ConnectedDevices;
        var rows = new (string, string)[]
        {
            ("Status", Host.StatusText),
            ("Version", version),
            ("Listening port", Host.Port.ToString(CultureInfo.InvariantCulture)),
            ("Local addresses", string.Join(", ", LanDiscovery.LocalAddresses(Host.Port).Select(a => a.Host))),
            ("Connected", connected.Count == 0 ? "—" : string.Join(", ", connected.Select(d => $"{d.Name} ({Host.AddressOf(d) ?? "?"})"))),
            ("Paired", Host.PairedDevices.Count == 0 ? "—" : string.Join(", ", Host.PairedDevices.Select(d => d.Name))),
            ("Device ID", Host.DeviceId),
            ("Fingerprint", Host.Fingerprint),
            ("Log file", AppLog.Path),
        };
        foreach (var (k, v) in rows)
        {
            var key = Labels.Make(k, Theme.Body(9.5f), Theme.Muted, autoSize: false);
            key.Width = (int)(140 * DeviceDpi / 96f);
            key.Height = (int)(20 * DeviceDpi / 96f);
            var value = Labels.Make(v, Theme.Mono(9.5f), Theme.Text);
            _status.Controls.Add(new HStack { Fill = value, Spacing = 8 }.With(key, value));
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
            var item = new ListViewItem([e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), firstLine])
            {
                ForeColor = e.IsError ? Theme.Danger : Theme.Text,
                BackColor = e.IsError ? Theme.DangerBg : Theme.Surface,
                ToolTipText = e.Message,
            };
            _log.Items.Add(item);
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
        if (disposing) _refresh.Dispose();
        base.Dispose(disposing);
    }
}
