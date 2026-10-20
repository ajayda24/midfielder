using System.ComponentModel;
using System.Globalization;

namespace Midfielder.App.Ui;

/// <summary>
/// Virtualized list or gallery of history records: only visible rows are painted, thumbnails come from a
/// bounded cache. Click/Ctrl/Shift select, double-click or Enter opens, Del deletes, right-click for a menu.
/// </summary>
internal sealed class FileBrowser : ThemedControl
{
    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, SmallChange = 40 };
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private IReadOnlyList<TransferRecord> _items = [];
    private ThumbnailCache? _thumbs;
    private bool _gallery;
    private int _anchor = -1;
    private int _focus = -1;
    private int _hover = -1;

    public FileBrowser()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Theme.Background;
        Controls.Add(_scroll);
        _scroll.ValueChanged += (_, _) => Invalidate();
    }

    public event Action<TransferRecord>? ItemActivated;
    public event Action<IReadOnlyList<TransferRecord>, Point>? MenuRequested;
    public event Action<IReadOnlyList<TransferRecord>>? DeleteRequested;
    public event Action? SelectionChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ThumbnailCache? Thumbnails
    {
        get => _thumbs;
        set
        {
            if (_thumbs is not null) _thumbs.Loaded -= OnThumbLoaded;
            _thumbs = value;
            if (_thumbs is not null) _thumbs.Loaded += OnThumbLoaded;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Gallery
    {
        get => _gallery;
        set
        {
            if (_gallery == value) return;
            _gallery = value;
            _scroll.Value = 0;
            UpdateScroll();
            Invalidate();
        }
    }

    public IReadOnlyList<TransferRecord> Selected => _items.Where(r => _selected.Contains(r.Id)).ToList();

    public void SetItems(IReadOnlyList<TransferRecord> items)
    {
        _items = items;
        var ids = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        _selected.RemoveWhere(id => !ids.Contains(id));
        _focus = Math.Min(_focus, items.Count - 1);
        _hover = -1;
        UpdateScroll();
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private void OnThumbLoaded(string path) => Invalidate();

    // --- Layout -----------------------------------------------------------------------------------

    private int RowH => S(64);
    private int Tile => S(150);
    private int Gap => S(10);
    private int ContentWidth => Width - (_scroll.Visible ? _scroll.Width : 0);
    private int Columns => _gallery ? Math.Max(1, (ContentWidth - Gap) / (Tile + Gap)) : 1;
    private int ItemH => _gallery ? Tile + Gap : RowH;
    private int Rows => (_items.Count + Columns - 1) / Columns;
    private int TotalHeight => Rows * ItemH + (_gallery ? Gap : 0);

    private void UpdateScroll()
    {
        int max = Math.Max(0, TotalHeight - Height);
        _scroll.Visible = max > 0;
        _scroll.Maximum = Math.Max(0, TotalHeight);
        _scroll.LargeChange = Math.Max(1, Height);
        if (_scroll.Value > max) _scroll.Value = max;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    private Rectangle ItemBounds(int index)
    {
        int y = -_scroll.Value;
        if (_gallery)
        {
            int cols = Columns;
            int tileW = (ContentWidth - Gap * (cols + 1)) / cols;
            int row = index / cols, col = index % cols;
            return new Rectangle(Gap + col * (tileW + Gap), y + Gap + row * (Tile + Gap), tileW, Tile);
        }
        return new Rectangle(0, y + index * RowH, ContentWidth, RowH);
    }

    private int HitTest(Point p)
    {
        for (int i = FirstVisible(); i < _items.Count && i <= LastVisible(); i++)
        {
            if (ItemBounds(i).Contains(p)) return i;
        }
        return -1;
    }

    private int FirstVisible() => Math.Max(0, _scroll.Value / ItemH * Columns);

    private int LastVisible() => Math.Min(_items.Count - 1, (_scroll.Value + Height) / ItemH * Columns + Columns - 1);

    // --- Painting ---------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);
        Theme.Smooth(g);
        for (int i = FirstVisible(); i <= LastVisible(); i++)
        {
            var r = ItemBounds(i);
            if (!r.IntersectsWith(e.ClipRectangle)) continue;
            if (_gallery) PaintTile(g, _items[i], r, i);
            else PaintRow(g, _items[i], r, i);
        }
    }

    private Color BackFor(int i, TransferRecord r) =>
        _selected.Contains(r.Id) ? Theme.HighlightStrong : i == _hover ? Theme.Highlight : Theme.Surface;

    private void PaintRow(Graphics g, TransferRecord r, Rectangle b, int i)
    {
        var card = new RectangleF(b.X + S(2f), b.Y + S(3f), b.Width - S(4f), b.Height - S(6f));
        Theme.FillRounded(g, BackFor(i, r), card, S(10f));
        if (i == _focus && Focused && ShowFocusCues) Theme.StrokeRounded(g, Theme.Primary, card, S(10f));

        var thumbRect = new Rectangle(b.X + S(12), b.Y + (b.Height - S(44)) / 2, S(44), S(44));
        PaintThumb(g, r, thumbRect, S(8f), 14f);

        int tx = thumbRect.Right + S(14);
        var nameRect = new Rectangle(tx, b.Y + S(12), b.Right - tx - S(12), S(22));
        TextRenderer.DrawText(g, r.Name, Theme.Strong(10f), nameRect, Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
        string arrow = r.Direction == TransferDirection.Sent ? "↑ to " : "↓ from ";
        string state = r.Status switch { TransferStatus.Failed => " · failed", TransferStatus.Cancelled => " · cancelled", _ => "" };
        string meta = $"{arrow}{r.Peer} · {Format.Bytes(r.Size)} · {Format.When(r.Time)}{state}";
        TextRenderer.DrawText(g, meta, Theme.Body(9f), new Rectangle(tx, b.Y + S(34), nameRect.Width, S(20)),
            r.Status == TransferStatus.Done ? Theme.Muted : Theme.Danger, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
    }

    private void PaintTile(Graphics g, TransferRecord r, Rectangle b, int i)
    {
        Theme.FillRounded(g, BackFor(i, r), b, S(12f));
        bool hasThumb = PaintThumb(g, r, b, S(12f), 26f);
        if (!hasThumb)
        {
            TextRenderer.DrawText(g, r.Name, Theme.Body(8.5f), new Rectangle(b.X + S(8), b.Bottom - S(40), b.Width - S(16), S(34)), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        if (_selected.Contains(r.Id))
        {
            Theme.StrokeRounded(g, Theme.Primary, new RectangleF(b.X + 1, b.Y + 1, b.Width - 2, b.Height - 2), S(12f), S(3f));
        }
        else if (i == _focus && Focused && ShowFocusCues)
        {
            Theme.StrokeRounded(g, Theme.Primary, b, S(12f));
        }
    }

    /// <summary>Paints the thumbnail, or a pale tile with a type glyph. Returns true when a real thumbnail was drawn.</summary>
    private bool PaintThumb(Graphics g, TransferRecord r, Rectangle rect, float radius, float glyphPt)
    {
        // Missing files simply fail once in the background and are remembered; no disk access while painting.
        var bmp = (r.IsImage || IsVideo(r)) && r.Location.Length > 0 ? _thumbs?.Get(r.Location) : null;
        using var clip = Theme.Rounded(rect, radius);
        if (bmp is not null)
        {
            var state = g.Save();
            g.SetClip(clip);
            g.DrawImage(bmp, rect);
            g.Restore(state);
            return true;
        }
        using (var b = new SolidBrush(Theme.Highlight)) g.FillPath(b, clip);
        string glyph = r.IsImage ? Theme.Glyph.Photo : Theme.Glyph.Document;
        TextRenderer.DrawText(g, glyph, Theme.Icons(glyphPt), rect, Theme.Primary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return false;
    }

    private static bool IsVideo(TransferRecord r) => r.Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(r.Name).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".webm";

    // --- Input ------------------------------------------------------------------------------------

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_scroll.Visible) return;
        int max = Math.Max(0, TotalHeight - Height);
        _scroll.Value = Math.Clamp(_scroll.Value - e.Delta / 120 * S(80), 0, max);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        if (h != _hover)
        {
            _hover = h;
            Invalidate();
        }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = HitTest(e.Location);
        if (i < 0)
        {
            if (e.Button == MouseButtons.Left) SelectOnly(-1);
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            if (!_selected.Contains(_items[i].Id)) SelectOnly(i);
            MenuRequested?.Invoke(Selected, PointToScreen(e.Location));
            return;
        }
        if ((ModifierKeys & Keys.Control) != 0)
        {
            if (!_selected.Add(_items[i].Id)) _selected.Remove(_items[i].Id);
            _anchor = _focus = i;
        }
        else if ((ModifierKeys & Keys.Shift) != 0 && _anchor >= 0) SelectRange(_anchor, i);
        else SelectOnly(i);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = HitTest(e.Location);
        if (i >= 0 && e.Button == MouseButtons.Left) ItemActivated?.Invoke(_items[i]);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Enter or Keys.Delete or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        int cols = Columns;
        int next = e.KeyCode switch
        {
            Keys.Down => _focus + cols,
            Keys.Up => _focus - cols,
            Keys.Right => _gallery ? _focus + 1 : _focus,
            Keys.Left => _gallery ? _focus - 1 : _focus,
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            _ => int.MinValue,
        };
        if (next != int.MinValue)
        {
            next = Math.Clamp(next, 0, _items.Count - 1);
            if (e.Shift && _anchor >= 0) SelectRange(_anchor, next);
            else SelectOnly(next);
            EnsureVisible(next);
            SelectionChanged?.Invoke();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Enter && _focus >= 0) ItemActivated?.Invoke(_items[_focus]);
        else if (e.KeyCode == Keys.Delete && _selected.Count > 0) DeleteRequested?.Invoke(Selected);
        else if (e.KeyCode == Keys.A && e.Control)
        {
            foreach (var r in _items) _selected.Add(r.Id);
            Invalidate();
            SelectionChanged?.Invoke();
        }
        else if (e.KeyCode == Keys.Apps && _selected.Count > 0) MenuRequested?.Invoke(Selected, PointToScreen(new Point(S(40), S(40))));
    }

    private void EnsureVisible(int i)
    {
        var r = ItemBounds(i);
        int max = Math.Max(0, TotalHeight - Height);
        if (r.Top < 0) _scroll.Value = Math.Clamp(_scroll.Value + r.Top - S(8), 0, max);
        else if (r.Bottom > Height) _scroll.Value = Math.Clamp(_scroll.Value + r.Bottom - Height + S(8), 0, max);
        Invalidate();
    }

    private void SelectOnly(int i)
    {
        _selected.Clear();
        if (i >= 0) _selected.Add(_items[i].Id);
        _anchor = _focus = i;
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private void SelectRange(int a, int b)
    {
        _selected.Clear();
        for (int k = Math.Min(a, b); k <= Math.Max(a, b); k++) _selected.Add(_items[k].Id);
        _focus = b;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Thumbnails = null;
        base.Dispose(disposing);
    }
}

internal static class Format
{
    public static string Bytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{n / (1024.0 * 1024):0.0} MB",
        _ => $"{n / (1024.0 * 1024 * 1024):0.00} GB",
    };

    public static string When(DateTime t)
    {
        var now = DateTime.Now;
        if (t.Date == now.Date) return "Today " + t.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (t.Date == now.Date.AddDays(-1)) return "Yesterday " + t.ToString("HH:mm", CultureInfo.InvariantCulture);
        return t.ToString(t.Year == now.Year ? "d MMM HH:mm" : "d MMM yyyy", CultureInfo.InvariantCulture);
    }
}
