using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Midfielder.App.Ui;

/// <summary>Owner-drawn base: double-buffered, repaints on resize, scales sizes with the monitor's DPI.</summary>
internal abstract class ThemedControl : Control
{
    protected ThemedControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.Body();
    }

    protected float Scale1 => DeviceDpi / 96f;

    protected int S(int v) => (int)Math.Round(v * Scale1);

    protected float S(float v) => v * Scale1;
}

/// <summary>A rounded white card with a hairline border; stacks its children vertically and sizes to fit.</summary>
internal sealed class Card : VStack
{
    public Card()
    {
        BackColor = Theme.Surface;
        Padding = new Padding(20, 16, 20, 18);
        Spacing = 10;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Theme.Surface;

    protected override void OnPaintBackground(PaintEventArgs e) => Surface.PaintCard(this, e.Graphics, Fill);
}

/// <summary>Card look for a panel whose children are docked (e.g. a list that fills the card).</summary>
internal sealed class Surface : Panel
{
    public Surface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => PaintCard(this, e.Graphics, Theme.Surface);

    public static void PaintCard(Control c, Graphics g, Color fill)
    {
        g.Clear(c.Parent?.BackColor ?? Theme.Background);
        Theme.Smooth(g);
        float r = 12 * c.DeviceDpi / 96f;
        var rect = new RectangleF(0.5f, 0.5f, c.Width - 1.5f, c.Height - 1.5f);
        Theme.FillRounded(g, fill, rect, r);
        Theme.StrokeRounded(g, Theme.Outline, rect, r);
    }
}

internal enum ButtonKind { Primary, Tonal, Outline, Danger, Subtle }

/// <summary>Rounded button with an optional icon glyph. Hover/pressed tints; a pale-green ring when keyboard-focused.</summary>
internal sealed class PillButton : ThemedControl, IButtonControl
{
    private bool _hover;
    private bool _pressed;

    public PillButton(string text, ButtonKind kind = ButtonKind.Tonal, string? glyph = null)
    {
        Text = text;
        Kind = kind;
        Glyph = glyph;
        Font = Theme.Strong(9.75f);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Size = GetPreferredSize(Size.Empty);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Glyph { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DialogResult DialogResult { get; set; }

    public void NotifyDefault(bool value) { }

    public void PerformClick() => OnClick(EventArgs.Empty);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        int icon = Glyph is null ? 0 : S(16) + (Text.Length > 0 ? S(8) : 0);
        int padX = Text.Length == 0 ? S(10) : S(16);
        return new Size(t.Width + icon + padX * 2, S(36));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Size = GetPreferredSize(Size.Empty);
    }

    private (Color Bg, Color Fg, Color Border) Colors() => Kind switch
    {
        ButtonKind.Primary => (_pressed ? Theme.PrimaryDark : _hover ? Theme.PrimaryHover : Theme.Primary, Color.White, Color.Empty),
        ButtonKind.Tonal => (_pressed ? Theme.HighlightStrong : _hover ? Color.FromArgb(0xD8, 0xEE, 0xE4) : Theme.Highlight, Theme.PrimaryDark, Color.Empty),
        ButtonKind.Outline => (_pressed ? Theme.HighlightStrong : _hover ? Theme.Highlight : Theme.Surface, Theme.Primary, Theme.Outline),
        ButtonKind.Danger => (_pressed ? Color.FromArgb(0xF6, 0xD9, 0xD7) : _hover ? Theme.DangerBg : Theme.Surface, Theme.Danger, Theme.Outline),
        _ => (_pressed ? Theme.HighlightStrong : _hover ? Theme.Highlight : Color.Transparent, Theme.Muted, Color.Empty),
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var (bg, fg, border) = Colors();
        if (!Enabled) (bg, fg) = (Color.FromArgb(0xEE, 0xF1, 0xEF), Color.FromArgb(0xA0, 0xA8, 0xA4));
        var rect = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
        float r = S(10f);
        if (bg.A > 0) Theme.FillRounded(g, bg, rect, r);
        if (border != Color.Empty) Theme.StrokeRounded(g, border, rect, r);
        if (Focused && ShowFocusCues) Theme.StrokeRounded(g, Theme.HighlightStrong, new RectangleF(1, 1, Width - 3, Height - 3), r, S(2f));

        int x;
        var textSize = TextRenderer.MeasureText(Text, Font);
        int iconW = Glyph is null ? 0 : S(16) + (Text.Length > 0 ? S(8) : 0);
        x = (Width - (textSize.Width + iconW)) / 2;
        if (Glyph is not null)
        {
            TextRenderer.DrawText(g, Glyph, Theme.Icons(11f), new Rectangle(x, 0, S(16), Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            x += iconW;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textSize.Width + 2, Height), fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) PerformClick();
        base.OnKeyUp(e);
    }
}

/// <summary>A filter chip: pale green when selected.</summary>
internal sealed class Chip : ThemedControl
{
    private bool _selected;
    private bool _hover;

    public Chip(string text)
    {
        Text = text;
        Cursor = Cursors.Hand;
        Size = new Size(TextRenderer.MeasureText(text, Theme.Strong(9f)).Width + S(28), S(30));
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float r = (Height - 1) / 2f;
        if (_selected) Theme.FillRounded(g, Theme.HighlightStrong, rect, r);
        else
        {
            Theme.FillRounded(g, _hover ? Theme.Highlight : Theme.Surface, rect, r);
            Theme.StrokeRounded(g, Theme.Outline, rect, r);
        }
        TextRenderer.DrawText(g, Text, _selected ? Theme.Strong(9f) : Theme.Body(9f), ClientRectangle,
            _selected ? Theme.PrimaryDark : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
}

/// <summary>On/off switch.</summary>
internal sealed class ToggleSwitch : ThemedControl
{
    private bool _checked;

    public ToggleSwitch(bool on)
    {
        _checked = on;
        Cursor = Cursors.Hand;
        Size = new Size(S(44), S(24));
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    public event EventHandler? CheckedChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) Checked = !Checked;
        base.OnKeyUp(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var track = new RectangleF(1, 1, Width - 3, Height - 3);
        float r = track.Height / 2;
        Theme.FillRounded(g, _checked ? Theme.Primary : Theme.Outline, track, r);
        if (Focused && ShowFocusCues) Theme.StrokeRounded(g, Theme.HighlightStrong, track, r, S(2f));
        float d = track.Height - S(6f);
        float x = _checked ? track.Right - d - S(3f) : track.X + S(3f);
        using var thumb = new SolidBrush(Color.White);
        g.FillEllipse(thumb, x, track.Y + S(3f), d, d);
    }
}

/// <summary>Thin rounded progress bar (0–100, or -1 for "unknown").</summary>
internal sealed class ProgressStrip : ThemedControl
{
    private int _value;

    public ProgressStrip() => Height = S(6);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        Theme.FillRounded(g, Theme.HighlightStrong, r, r.Height / 2);
        if (_value > 0) Theme.FillRounded(g, Theme.Primary, new RectangleF(0, 0, Math.Max(r.Height, r.Width * Math.Min(_value, 100) / 100f), r.Height), r.Height / 2);
    }
}

/// <summary>Seven rounded bars, oldest to today; today in the primary color.</summary>
internal sealed class BarChart : ThemedControl
{
    private long[] _values = [];
    private string[] _labels = [];

    public BarChart() => Height = S(110);

    public void SetData(long[] values, string[] labels)
    {
        if (_values.SequenceEqual(values) && _labels.SequenceEqual(labels)) return;
        _values = values;
        _labels = labels;
        AccessibleDescription = string.Join(", ", labels.Zip(values, (l, v) => $"{l} {v}"));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_values.Length == 0) return;
        var g = e.Graphics;
        Theme.Smooth(g);
        int labelH = S(20);
        float chartH = Height - labelH;
        float slot = Width / (float)_values.Length;
        float barW = Math.Min(slot * 0.5f, S(30f));
        long max = Math.Max(1, _values.Max());
        for (int i = 0; i < _values.Length; i++)
        {
            float cx = slot * i + slot / 2;
            float h = _values[i] == 0 ? S(4f) : (chartH - S(4f)) * _values[i] / max + S(4f);
            Theme.FillRounded(g, i == _values.Length - 1 ? Theme.Primary : Theme.HighlightStrong, new RectangleF(cx - barW / 2, chartH - h, barW, h), S(6f));
            if (i < _labels.Length)
            {
                TextRenderer.DrawText(g, _labels[i], Theme.Body(8.5f), new Rectangle((int)(cx - slot / 2), (int)chartH, (int)slot, labelH), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
            }
        }
    }
}

/// <summary>Left navigation entry: icon + label, pale-green pill when selected.</summary>
internal sealed class NavItem : ThemedControl
{
    private bool _selected;
    private bool _hover;

    public NavItem(string text, string glyph)
    {
        Text = text;
        Glyph = glyph;
        Cursor = Cursors.Hand;
        Height = S(42);
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTab;
    }

    public string Glyph { get; }

    private bool _compact;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    /// <summary>Icon only (narrow windows); the label shows as a tooltip.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Compact
    {
        get => _compact;
        set { _compact = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var rect = new RectangleF(S(8f), 2, Width - S(16f), Height - 4);
        if (_selected) Theme.FillRounded(g, Theme.HighlightStrong, rect, S(10f));
        else if (_hover) Theme.FillRounded(g, Theme.Highlight, rect, S(10f));
        if (Focused && ShowFocusCues) Theme.StrokeRounded(g, Theme.Primary, rect, S(10f));
        var fg = _selected ? Theme.PrimaryDark : Theme.Muted;
        if (_compact)
        {
            TextRenderer.DrawText(g, Glyph, Theme.Icons(13f), ClientRectangle, fg, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            return;
        }
        TextRenderer.DrawText(g, Glyph, Theme.Icons(12f), new Rectangle(S(20), 0, S(24), Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, Text, _selected ? Theme.Strong(10f) : Theme.Body(10f), new Rectangle(S(54), 0, Width - S(60), Height), _selected ? Theme.Text : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyUp(e);
    }
}

/// <summary>A filled circle with a glyph, e.g. the phone badge on Home.</summary>
internal sealed class GlyphBadge : ThemedControl
{
    private readonly int _logical;

    public GlyphBadge(string glyph, int size = 44)
    {
        Glyph = glyph;
        _logical = size;
        Size = new Size(S(size), S(size));
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Theme.Highlight;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Ink { get; set; } = Theme.Primary;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        using var b = new SolidBrush(Fill);
        g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, Glyph, Theme.Icons(_logical * 0.3f), ClientRectangle, Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>
/// Label factory with the theme's fonts; transparent so cards show through. Labels never auto-size: the stacks
/// measure them (wrapped to the width they get, or one ellipsized line with <c>singleLine</c>).
/// </summary>
internal static class Labels
{
    public static Label Make(string text, Font font, Color color, bool singleLine = false) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        BackColor = Color.Transparent,
        AutoSize = false,
        UseMnemonic = false,
        AutoEllipsis = singleLine,
    };

    public static Label Title(string text) => Make(text, Theme.Title(20f), Theme.Text, singleLine: true);
    public static Label Heading(string text) => Make(text, Theme.Strong(11.5f), Theme.Text, singleLine: true);
    public static Label Body(string text) => Make(text, Theme.Body(10f), Theme.Text);
    public static Label Muted(string text, float size = 9f) => Make(text, Theme.Body(size), Theme.Muted);
}

/// <summary>Dot that shows online/offline.</summary>
internal sealed class StatusDot : ThemedControl
{
    private bool _on;

    public StatusDot() => Size = new Size(S(10), S(10));

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool On
    {
        get => _on;
        set { _on = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(_on ? Theme.Online : Theme.Offline);
        e.Graphics.FillEllipse(b, 0, 0, Width - 1, Height - 1);
    }
}
