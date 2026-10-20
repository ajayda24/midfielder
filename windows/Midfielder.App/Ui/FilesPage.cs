namespace Midfielder.App.Ui;

/// <summary>Sent and received files as a list or a gallery. Open, show in folder, delete to the Recycle Bin.</summary>
internal sealed class FilesPage : Panel
{
    private enum Filter { All, Received, Sent, Images }

    private static Filter s_filter = Filter.All;
    private static bool s_gallery;

    private readonly IAppHost _host;
    private readonly FileBrowser _browser = new() { Dock = DockStyle.Fill };
    private readonly ThumbnailCache _thumbs;
    private readonly Dictionary<Filter, Chip> _chips = [];
    private readonly PillButton _open = new("Open", ButtonKind.Outline, Theme.Glyph.OpenFile);
    private readonly PillButton _reveal = new("Show in folder", ButtonKind.Outline, Theme.Glyph.Folder);
    private readonly PillButton _delete = new("Delete", ButtonKind.Danger, Theme.Glyph.Delete);
    private readonly PillButton _view = new("Gallery", ButtonKind.Tonal, Theme.Glyph.Grid);
    private readonly Label _empty = Labels.Make("No files yet.\nFiles you send or receive show up here — drop some on Home to start.", Theme.Body(10.5f), Theme.Muted);
    private readonly ContextMenuStrip _menu = new() { Renderer = new MenuRenderer() };
    private readonly HStack _header;
    private readonly HStack _toolbar;

    public FilesPage(IAppHost host)
    {
        _host = host;
        BackColor = Theme.Background;
        _thumbs = new ThumbnailCache(this, Px(150));
        _browser.Thumbnails = _thumbs;
        _browser.Gallery = s_gallery;

        var title = Labels.Title("Files");
        var openFolder = new PillButton("Open folder", ButtonKind.Tonal, Theme.Glyph.Folder);
        openFolder.Click += (_, _) => Shell.OpenFolder(_host.Settings.ReceiveFolder);
        _header = new HStack { Dock = DockStyle.Top, Fill = title, MinHeight = 48, MinFillWidth = 80 }.With(title, _view, openFolder);
        var header = _header;

        _toolbar = new HStack { Dock = DockStyle.Top, MinHeight = 52, Spacing = 8, Wrap = true, MinFillWidth = 0, Padding = new Padding(0, 6, 0, 6) };
        var toolbar = _toolbar;
        foreach (var f in Enum.GetValues<Filter>())
        {
            var chip = new Chip(f.ToString()) { Selected = f == s_filter };
            chip.Click += (_, _) => SetFilter(f);
            _chips[f] = chip;
            toolbar.Controls.Add(chip);
        }
        var spacer = new Panel { BackColor = Color.Transparent, Width = 1 };
        toolbar.Controls.Add(spacer);
        toolbar.Fill = spacer;
        toolbar.Controls.AddRange([_open, _reveal, _delete]);

        _open.Click += (_, _) => { foreach (var r in _browser.Selected.Take(10)) Open(r); };
        _reveal.Click += (_, _) =>
        {
            var sel = _browser.Selected;
            if (sel.Count > 0) Reveal(sel[0]);
        };
        _delete.Click += (_, _) => Delete(_browser.Selected);
        _view.Click += (_, _) => SetGallery(!_browser.Gallery);

        _empty.Dock = DockStyle.Fill;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.BackColor = Theme.Background;

        Controls.Add(_browser);
        Controls.Add(_empty);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = Px(6), BackColor = Theme.Background });
        Controls.Add(toolbar);
        Controls.Add(header);

        _browser.ItemActivated += Open;
        _browser.DeleteRequested += Delete;
        _browser.MenuRequested += ShowMenu;
        _browser.SelectionChanged += UpdateButtons;
        _host.History.Changed += OnHistoryChanged;
        SetGallery(s_gallery);
        Reload();
    }

    private int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    /// <summary>Docked rows get their measured height for the current width before docking runs.</summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        // Layout can run from base-class code before the constructor has created the rows.
        if (_header is null || _toolbar is null)
        {
            base.OnLayout(levent);
            return;
        }
        Measure.CenterColumn(this, 1100, 32, 24, 16);
        int w = ClientSize.Width - Padding.Horizontal;
        if (w > 0)
        {
            int hh = _header.MeasureHeight(w), th = _toolbar.MeasureHeight(w);
            if (_header.Height != hh) _header.Height = hh;
            if (_toolbar.Height != th) _toolbar.Height = th;
        }
        base.OnLayout(levent);
    }

    private void OnHistoryChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(Reload);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void SetFilter(Filter f)
    {
        s_filter = f;
        foreach (var (k, chip) in _chips) chip.Selected = k == f;
        Reload();
    }

    private void SetGallery(bool on)
    {
        s_gallery = on;
        _browser.Gallery = on;
        _view.SetLabel(on ? "List" : "Gallery", on ? Theme.Glyph.List : Theme.Glyph.Grid);
    }

    private void Reload()
    {
        var all = _host.History.Records();
        var items = s_filter switch
        {
            Filter.Received => all.Where(r => r.Direction == TransferDirection.Received).ToList(),
            Filter.Sent => all.Where(r => r.Direction == TransferDirection.Sent).ToList(),
            Filter.Images => all.Where(r => r.IsImage).ToList(),
            _ => all,
        };
        _browser.SetItems(items);
        _empty.Visible = items.Count == 0;
        _browser.Visible = items.Count > 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var sel = _browser.Selected;
        _open.Enabled = sel.Count > 0;
        _reveal.Enabled = sel.Count == 1;
        _delete.Enabled = sel.Count > 0;
        _open.Invalidate();
        _reveal.Invalidate();
        _delete.Invalidate();
    }

    private void Open(TransferRecord r)
    {
        if (!File.Exists(r.Location))
        {
            Missing(r);
            return;
        }
        Shell.OpenFile(r.Location);
    }

    private void Reveal(TransferRecord r)
    {
        if (File.Exists(r.Location)) Shell.Reveal(r.Location);
        else Missing(r);
    }

    private void Missing(TransferRecord r)
    {
        if (MessageBox.Show(FindForm(), $"{r.Name} is no longer at\n{r.Location}\n\nRemove it from the list?", "Midfielder",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
        {
            _host.History.Remove([r.Id]);
        }
    }

    private void Delete(IReadOnlyList<TransferRecord> records)
    {
        if (records.Count == 0) return;
        var received = records.Where(r => r.Direction == TransferDirection.Received && File.Exists(r.Location)).ToList();
        string what = records.Count == 1 ? $"\"{records[0].Name}\"" : $"{records.Count} files";
        string message = received.Count > 0
            ? $"Move {what} to the Recycle Bin and remove from the list?"
            : $"Remove {what} from the list? (Files you sent stay where they are.)";
        if (MessageBox.Show(FindForm(), message, "Midfielder", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var r in received)
        {
            try
            {
                Shell.Recycle(r.Location);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                AppLog.Error($"Could not delete {r.Name}", e);
            }
        }
        _host.History.Remove(records.Select(r => r.Id));
    }

    private void ShowMenu(IReadOnlyList<TransferRecord> records, Point screen)
    {
        _menu.Items.Clear();
        if (records.Count == 0) return;
        _menu.Items.Add("Open", null, (_, _) => { foreach (var r in records.Take(10)) Open(r); });
        if (records.Count == 1)
        {
            _menu.Items.Add("Show in folder", null, (_, _) => Reveal(records[0]));
            _menu.Items.Add("Copy path", null, (_, _) => Clipboard.SetText(records[0].Location));
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(records.Any(r => r.Direction == TransferDirection.Received) ? "Delete" : "Remove from list", null, (_, _) => Delete(records));
        if (records.Any(r => r.Direction == TransferDirection.Received))
        {
            _menu.Items.Add("Remove from list only", null, (_, _) => _host.History.Remove(records.Select(r => r.Id)));
        }
        _menu.Show(screen);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.History.Changed -= OnHistoryChanged;
            _browser.Thumbnails = null;
            _thumbs.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
