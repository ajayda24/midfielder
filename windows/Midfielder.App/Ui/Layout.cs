using System.ComponentModel;

namespace Midfielder.App.Ui;

/// <summary>A control that can say how tall it wants to be at a given width (the "measure" pass).</summary>
internal interface IMeasure
{
    int MeasureHeight(int width);
}

/// <summary>
/// Measure/arrange helpers shared by the stacks. Measuring never changes any control; arranging only sets the
/// bounds of children. That split is what keeps layouts stable: nothing resizes itself mid-layout, so there
/// are no feedback loops (a label measured at 1 px wide can't leave a card 800 px tall).
/// </summary>
internal static class Measure
{
    private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
    private const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    public static int Px(Control c, int v) => (int)Math.Round(v * c.DeviceDpi / 96f);

    public static int Height(Control c, int width)
    {
        switch (c)
        {
            case IMeasure m:
                return m.MeasureHeight(width);
            case Label l:
                if (l.Text.Length == 0) return TextRenderer.MeasureText("X", l.Font, Size.Empty, Line).Height + l.Padding.Vertical;
                return l.AutoEllipsis
                    ? TextRenderer.MeasureText("Xg", l.Font, Size.Empty, Line).Height + l.Padding.Vertical
                    : TextRenderer.MeasureText(l.Text, l.Font, new Size(Math.Max(1, width - l.Padding.Horizontal), int.MaxValue), Wrap).Height + l.Padding.Vertical;
            default:
                return c.Height;
        }
    }

    // Width a control had before any stack arranged it. Arranging may shrink a control to fit a narrow row;
    // measuring must not read that shrunken width back, or the control stays clipped after the row widens.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Natural = new();

    /// <summary>Natural (single-line) width; labels measure their text, everything else its own size.</summary>
    public static int Width(Control c) => c switch
    {
        Label l => Math.Max(l.MinimumSize.Width, TextRenderer.MeasureText(l.Text.Length == 0 ? " " : l.Text, l.Font, Size.Empty, Line).Width + l.Padding.Horizontal + 2),
        HStack h => h.NaturalWidth(),
        PillButton b => b.GetPreferredSize(Size.Empty).Width,
        _ => (int)Natural.GetValue(c, k => k.Width),
    };

    /// <summary>Side padding that keeps a docked page's content in a centered column of at most <paramref name="maxLogical"/> px.</summary>
    public static void CenterColumn(Control page, int maxLogical, int minSideLogical, int top, int bottom)
    {
        int side = Math.Max(Px(page, minSideLogical), (page.ClientSize.Width - Px(page, maxLogical)) / 2);
        var pad = new Padding(side, Px(page, top), side, Px(page, bottom));
        if (page.Padding != pad) page.Padding = pad;
    }

    /// <summary>Arranges a child and, for nested stacks, re-arranges them too (their content may have changed).</summary>
    public static void Place(Control c, int x, int y, int w, int h)
    {
        bool same = c.Bounds == new Rectangle(x, y, w, h);
        c.SetBounds(x, y, w, h);
        if (same && c is VStack or HStack) c.PerformLayout(); // bounds unchanged: no automatic layout
    }
}

/// <summary>
/// Lays children out top to bottom at full width (minus padding), or in <see cref="Columns"/> equal columns.
/// Padding and spacing are in 96-DPI units. A scrolling stack sizes its scroll range to the content; with
/// <see cref="MaxContentWidth"/> the content is a centered column that doesn't stretch across wide windows.
/// </summary>
internal class VStack : Panel, IMeasure
{
    public VStack()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Spacing { get; set; } = 12;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Columns { get; set; } = 1;

    /// <summary>Below this content width (logical px) the columns collapse to one.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MinColumnWidth { get; set; } = 320;

    /// <summary>0 = no limit. Logical px.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MaxContentWidth { get; set; }

    // Kept for source compatibility: the parent always decides the height now.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoHeight { get; set; }

    private int Px(int v) => Measure.Px(this, v);

    private (int X, int Width) Column(int outerWidth)
    {
        int w = Math.Max(0, outerWidth - Px(Padding.Horizontal));
        int x = Px(Padding.Left);
        if (MaxContentWidth > 0 && w > Px(MaxContentWidth))
        {
            x += (w - Px(MaxContentWidth)) / 2;
            w = Px(MaxContentWidth);
        }
        return (x, w);
    }

    private int ColumnsFor(int width) => Columns <= 1 || width < Px(MinColumnWidth) * Columns ? 1 : Columns;

    private List<Control> VisibleChildren() => Controls.Cast<Control>().Where(c => c.Visible).ToList();

    public int MeasureHeight(int width)
    {
        var (_, w) = Column(width);
        return Px(Padding.Vertical) + ContentHeight(w, null, 0, 0);
    }

    /// <summary>Measures (and, when <paramref name="arrangeAt"/> is set, arranges) the children.</summary>
    private int ContentHeight(int w, int? arrangeAt, int x0, int y0)
    {
        var items = VisibleChildren();
        int cols = ColumnsFor(w);
        int gap = Px(Spacing);
        int colW = cols == 1 ? w : (w - gap * (cols - 1)) / cols;
        int y = 0;
        for (int i = 0; i < items.Count; i += cols)
        {
            if (i > 0) y += gap;
            var row = items.Skip(i).Take(cols).ToList();
            int rowH = row.Max(c => Measure.Height(c, colW));
            if (arrangeAt is not null)
            {
                for (int k = 0; k < row.Count; k++)
                {
                    int h = cols == 1 ? rowH : Measure.Height(row[k], colW);
                    Measure.Place(row[k], x0 + k * (colW + gap), y0 + y, colW, h);
                }
            }
            y += rowH;
        }
        return y;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var (x, w) = Column(ClientSize.Width);
        int offset = AutoScroll ? AutoScrollPosition.Y : 0; // <= 0 when scrolled down
        int content = ContentHeight(w, 0, x, Px(Padding.Top) + offset);
        if (AutoScroll)
        {
            int total = content + Px(Padding.Vertical);
            // Only as tall as the content: no scrollbar when it fits, no empty tail when it doesn't.
            var wanted = new Size(0, total > ClientSize.Height ? total : 0);
            if (AutoScrollMinSize != wanted) AutoScrollMinSize = wanted;
        }
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(proposedSize.Width, MeasureHeight(proposedSize.Width > 0 ? proposedSize.Width : Width));
}

/// <summary>
/// Lays children out left to right, vertically centered; <see cref="Fill"/> takes the leftover width.
/// With <see cref="Wrap"/>, children that don't fit move to the next line instead of being cut off.
/// </summary>
internal sealed class HStack : Panel, IMeasure
{
    public HStack()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control? Fill { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Spacing { get; set; } = 10;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MinHeight { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Wrap { get; set; }

    /// <summary>Smallest width the <see cref="Fill"/> child may get before the row wraps (logical px).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MinFillWidth { get; set; } = 160;

    private int Px(int v) => Measure.Px(this, v);

    public HStack With(params Control[] children)
    {
        Controls.AddRange(children);
        return this;
    }

    private List<Control> VisibleChildren() => Controls.Cast<Control>().Where(c => c.Visible).ToList();

    public int NaturalWidth()
    {
        var items = VisibleChildren();
        return items.Sum(c => c == Fill ? Px(MinFillWidth) : Measure.Width(c)) + Px(Spacing) * Math.Max(0, items.Count - 1) + Px(Padding.Horizontal);
    }

    /// <summary>Splits children into lines (one line unless wrapping), each with its widths.</summary>
    private List<List<(Control C, int W)>> Lines(int width)
    {
        int inner = Math.Max(0, width - Px(Padding.Horizontal));
        int gap = Px(Spacing);
        var lines = new List<List<(Control, int)>> { new() };
        int used = 0;
        foreach (var c in VisibleChildren())
        {
            int w = c == Fill ? Px(MinFillWidth) : Math.Min(Measure.Width(c), inner);
            var line = lines[^1];
            if (Wrap && line.Count > 0 && used + gap + w > inner)
            {
                line = [];
                lines.Add(line);
                used = 0;
            }
            used += (line.Count > 0 ? gap : 0) + w;
            line.Add((c, w));
        }
        // The fill child takes whatever its line has left.
        foreach (var line in lines)
        {
            int idx = line.FindIndex(t => t.Item1 == Fill);
            if (idx < 0) continue;
            int others = line.Where((_, i) => i != idx).Sum(t => t.Item2) + gap * Math.Max(0, line.Count - 1);
            line[idx] = (Fill!, Math.Max(0, inner - others));
        }
        return lines;
    }

    public int MeasureHeight(int width)
    {
        var lines = Lines(width);
        int gap = Px(Spacing);
        int h = lines.Sum(l => l.Count == 0 ? 0 : l.Max(t => Measure.Height(t.C, t.W))) + gap * Math.Max(0, lines.Count - 1);
        return Math.Max(Px(MinHeight), h + Px(Padding.Vertical));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var lines = Lines(ClientSize.Width);
        int gap = Px(Spacing);
        int total = MeasureHeight(ClientSize.Width) - Px(Padding.Vertical);
        int content = lines.Sum(l => l.Count == 0 ? 0 : l.Max(t => Measure.Height(t.C, t.W))) + gap * Math.Max(0, lines.Count - 1);
        int y = Px(Padding.Top) + Math.Max(0, (total - content) / 2);
        foreach (var line in lines)
        {
            if (line.Count == 0) continue;
            int lineH = line.Max(t => Measure.Height(t.C, t.W));
            int x = Px(Padding.Left);
            foreach (var (c, w) in line)
            {
                int ch = Measure.Height(c, w);
                Measure.Place(c, x, y + (lineH - ch) / 2, w, ch);
                x += w + gap;
            }
            y += lineH + gap;
        }
    }
}
