using System.Drawing;
using System.Windows.Forms;

namespace Midfielder.App.Ui;

/// <summary>Light, pale-green tray menu to match the window.</summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Highlight = Color.FromArgb(0xE6, 0xF4, 0xEE);
    private static readonly Color HighlightStrong = Color.FromArgb(0xC9, 0xE7, 0xDA);
    private static readonly Color Outline = Color.FromArgb(0xDD, 0xE6, 0xE1);
    private static readonly Color TextColor = Color.FromArgb(0x1B, 0x24, 0x21);
    private static readonly Color Muted = Color.FromArgb(0x5E, 0x6B, 0x66);

    public MenuRenderer() : base(new Colors()) => RoundedEdges = true;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextColor : Muted;
        base.OnRenderItemText(e);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Highlight;
        public override Color MenuItemSelectedGradientBegin => Highlight;
        public override Color MenuItemSelectedGradientEnd => Highlight;
        public override Color MenuItemBorder => HighlightStrong;
        public override Color MenuBorder => Outline;
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Color.White;
        public override Color ImageMarginGradientMiddle => Color.White;
        public override Color ImageMarginGradientEnd => Color.White;
        public override Color SeparatorDark => Outline;
        public override Color SeparatorLight => Color.White;
        public override Color CheckBackground => HighlightStrong;
        public override Color CheckSelectedBackground => HighlightStrong;
        public override Color CheckPressedBackground => HighlightStrong;
    }
}

/// <summary>The Midfielder icon, embedded in the exe. Loaded once; lives for the process.</summary>
internal static class AppIcon
{
    private static readonly Lazy<Icon> Source = new(() =>
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("midfielder.ico");
        return s is null ? (Icon)SystemIcons.Application.Clone() : new Icon(s);
    });

    public static Icon Window => Source.Value;

    public static Icon Tray(Size size) => new(Source.Value, size);

    /// <summary>The icon as a frozen WPF image (largest frame), for window and sidebar.</summary>
    public static System.Windows.Media.ImageSource Image(int px)
    {
        using var icon = new Icon(Source.Value, new Size(px, px));
        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
