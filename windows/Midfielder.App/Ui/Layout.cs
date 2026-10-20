using System.ComponentModel;

namespace Midfielder.App.Ui;

/// <summary>
/// Lays children out top to bottom at full width (minus padding), with <see cref="Spacing"/> between them.
/// Padding and spacing are in 96-DPI units and scaled to the monitor.
/// Auto-size labels get their wrapped height. With <see cref="AutoHeight"/> the panel grows to fit.
/// </summary>
internal class VStack : Panel
{
    public VStack()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Spacing { get; set; } = 12;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoHeight { get; set; }

    /// <summary>Children listed here keep their own width (left-aligned) instead of stretching.</summary>
    public HashSet<Control> NoStretch { get; } = [];

    private int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var pad = new Padding(Px(Padding.Left), Px(Padding.Top), Px(Padding.Right), Px(Padding.Bottom)); // logical → device
        int offset = AutoScroll ? AutoScrollPosition.Y : 0; // <= 0 when scrolled down
        int x = pad.Left, y = pad.Top + offset;
        int w = Math.Max(0, ClientSize.Width - pad.Horizontal);
        bool first = true;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            if (!first) y += Px(Spacing);
            first = false;
            int cw = NoStretch.Contains(c) ? Math.Min(c.Width, w) : w;
            int ch = c is Label { AutoSize: true } l ? l.GetPreferredSize(new Size(cw, 0)).Height : c.Height;
            if (c is Label { AutoSize: true } label)
            {
                label.MaximumSize = new Size(cw, 0);
            }
            c.SetBounds(x, y, cw, ch);
            y += c.Height;
        }
        int total = y - offset + pad.Bottom;
        if (AutoHeight && Height != total) Height = total;
        if (AutoScroll && AutoScrollMinSize.Height != total) AutoScrollMinSize = new Size(0, total);
    }
}

/// <summary>Lays children out left to right, vertically centered; <see cref="Fill"/> takes the leftover width.</summary>
internal sealed class HStack : Panel
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

    private int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    public HStack With(params Control[] children)
    {
        Controls.AddRange(children);
        return this;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var visible = Controls.Cast<Control>().Where(c => c.Visible).ToList();
        int gap = Px(Spacing);
        int fixedW = visible.Where(c => c != Fill).Sum(c => c is Label { AutoSize: true } l ? l.PreferredWidth : c.Width) + gap * Math.Max(0, visible.Count - 1);
        int left = Px(Padding.Left);
        int fillW = Math.Max(0, ClientSize.Width - left - Px(Padding.Right) - fixedW);
        int h = Math.Max(Px(MinHeight), visible.Count == 0 ? 0 : visible.Max(c => c == Fill && c is Label { AutoSize: true } fl
            ? fl.GetPreferredSize(new Size(fillW, 0)).Height
            : c is Label { AutoSize: true } l2 ? l2.PreferredHeight : c.Height));
        int x = left;
        foreach (var c in visible)
        {
            int w = c == Fill ? fillW : c is Label { AutoSize: true } l ? l.PreferredWidth : c.Width;
            if (c == Fill && c is Label { AutoSize: true } fill) fill.MaximumSize = new Size(Math.Max(1, fillW), 0);
            int ch = c is Label { AutoSize: true } lb ? lb.GetPreferredSize(new Size(w, 0)).Height : c.Height;
            c.SetBounds(x, (h - ch) / 2, w, ch);
            x += w + gap;
        }
        if (Height != h) Height = h;
    }
}
