namespace Midfielder.App.Ui;

/// <summary>
/// One page of the main window. Created when shown and disposed when another page is shown, so a closed page
/// holds no subscriptions, timers or images. Host events arrive on any thread; <see cref="Post"/> marshals.
/// </summary>
internal abstract class Page : VStack
{
    protected Page(IAppHost host)
    {
        Host = host;
        AutoScroll = true;
        Padding = new Padding(32, 24, 32, 28);
        Spacing = 14;
        BackColor = Theme.Background;
    }

    protected IAppHost Host { get; }

    /// <summary>Called once after the page is added to the window: subscribe here.</summary>
    public virtual void Activate() { }

    /// <summary>Called before disposal: unsubscribe here.</summary>
    public virtual void Deactivate() { }

    protected void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed between the check and the call.
        }
    }

    protected static Control Header(string title, string? subtitle)
    {
        var box = new VStack { AutoHeight = true, Spacing = 2, Padding = Padding.Empty };
        box.Controls.Add(Labels.Title(title));
        if (subtitle is not null) box.Controls.Add(Labels.Muted(subtitle, 10f));
        return box;
    }

    protected static HStack SettingRow(string title, string? detail, Control trailing)
    {
        var text = new VStack { AutoHeight = true, Spacing = 2, Padding = Padding.Empty, BackColor = Theme.Surface };
        text.Controls.Add(Labels.Make(title, Theme.Body(10.5f), Theme.Text));
        if (detail is not null) text.Controls.Add(Labels.Muted(detail));
        var row = new HStack { Fill = text, MinHeight = 44 };
        return row.With(text, trailing);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Deactivate();
        base.Dispose(disposing);
    }
}

/// <summary>Light, pale-green menus (tray and right-click) to match the window.</summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer() : base(new Colors()) => RoundedEdges = true;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.Highlight;
        public override Color MenuItemSelectedGradientBegin => Theme.Highlight;
        public override Color MenuItemSelectedGradientEnd => Theme.Highlight;
        public override Color MenuItemBorder => Theme.HighlightStrong;
        public override Color MenuBorder => Theme.Outline;
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Outline;
        public override Color SeparatorLight => Theme.Surface;
        public override Color CheckBackground => Theme.HighlightStrong;
        public override Color CheckSelectedBackground => Theme.HighlightStrong;
        public override Color CheckPressedBackground => Theme.HighlightStrong;
    }
}

/// <summary>Dashed drop target: "Drop files here to send them to your phone".</summary>
internal sealed class DropZone : ThemedControl
{
    private bool _active;

    public DropZone()
    {
        Height = S(96);
        AllowDrop = true;
        Cursor = Cursors.Hand;
    }

    public event Action<IReadOnlyList<string>>? FilesDropped;

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        if (drgevent.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            drgevent.Effect = DragDropEffects.Copy;
            _active = true;
            Invalidate();
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _active = false;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        _active = false;
        Invalidate();
        if (drgevent.Data?.GetData(DataFormats.FileDrop) is string[] files) FilesDropped?.Invoke(files.Where(File.Exists).ToList());
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        Theme.FillRounded(g, _active ? Theme.HighlightStrong : Theme.Highlight, r, S(12f));
        using (var path = Theme.Rounded(r, S(12f)))
        using (var pen = new Pen(Theme.Primary, S(1.5f)) { DashPattern = [4f, 3f] })
        {
            g.DrawPath(pen, path);
        }
        var top = new Rectangle(0, S(14), Width, S(34));
        TextRenderer.DrawText(g, Theme.Glyph.Upload, Theme.Icons(16f), top, Theme.Primary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, _active ? "Release to send" : "Drop files here to send them to your phone", Theme.Strong(10f),
            new Rectangle(0, S(50), Width, S(22)), Theme.PrimaryDark, TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, "or click to choose files", Theme.Body(9f), new Rectangle(0, S(70), Width, S(20)), Theme.Muted, TextFormatFlags.HorizontalCenter);
    }
}
