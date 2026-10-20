using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Midfielder.App.Ui;

/// <summary>
/// The one light look: dark green primary, very light green for focus/selection. Keep in sync with the Android
/// app's Palette. Fonts are created once and live for the process (a handful of handles).
/// </summary>
internal static class Theme
{
    public static readonly Color Primary = Color.FromArgb(0x1F, 0x6F, 0x5C);
    public static readonly Color PrimaryDark = Color.FromArgb(0x17, 0x4F, 0x42);
    public static readonly Color PrimaryHover = Color.FromArgb(0x26, 0x80, 0x6B);
    public static readonly Color Highlight = Color.FromArgb(0xE6, 0xF4, 0xEE);
    public static readonly Color HighlightStrong = Color.FromArgb(0xC9, 0xE7, 0xDA);
    public static readonly Color Background = Color.FromArgb(0xF5, 0xF8, 0xF6);
    public static readonly Color Surface = Color.White;
    public static readonly Color Outline = Color.FromArgb(0xDD, 0xE6, 0xE1);
    public static readonly Color Text = Color.FromArgb(0x1B, 0x24, 0x21);
    public static readonly Color Muted = Color.FromArgb(0x5E, 0x6B, 0x66);
    public static readonly Color Danger = Color.FromArgb(0xB3, 0x26, 0x1E);
    public static readonly Color DangerBg = Color.FromArgb(0xFC, 0xEE, 0xEE);
    public static readonly Color Online = Color.FromArgb(0x2E, 0x9B, 0x6F);
    public static readonly Color Offline = Color.FromArgb(0xB8, 0xC2, 0xBD);

    private static readonly Dictionary<(string, float, FontStyle), Font> Fonts = [];
    private static readonly Lazy<string> TextFamily = new(() => Installed("Segoe UI Variable Text") ?? "Segoe UI");
    private static readonly Lazy<string> DisplayFamily = new(() => Installed("Segoe UI Variable Display") ?? "Segoe UI Semibold");
    private static readonly Lazy<string> IconFamily = new(() => Installed("Segoe Fluent Icons") ?? "Segoe MDL2 Assets");

    public static Font Body(float size = 10f, FontStyle style = FontStyle.Regular) => Get(TextFamily.Value, size, style);
    public static Font Strong(float size = 10f) => Get(Installed("Segoe UI Variable Text Semibold") ?? "Segoe UI Semibold", size, FontStyle.Regular);
    public static Font Title(float size = 20f) => Get(DisplayFamily.Value, size, FontStyle.Bold);
    public static Font Icons(float size = 12f) => Get(IconFamily.Value, size, FontStyle.Regular);
    public static Font Mono(float size = 9f) => Get(Installed("Cascadia Mono") ?? "Consolas", size, FontStyle.Regular);

    private static Font Get(string family, float size, FontStyle style)
    {
        lock (Fonts)
        {
            if (!Fonts.TryGetValue((family, size, style), out var f))
            {
                f = new Font(family, size, style, GraphicsUnit.Point);
                Fonts[(family, size, style)] = f;
            }
            return f;
        }
    }

    private static readonly Lazy<HashSet<string>> Families = new(() =>
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    });

    private static string? Installed(string name) => Families.Value.Contains(name) ? name : null;

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, RectangleF r, float radius)
    {
        using var path = Rounded(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void StrokeRounded(Graphics g, Color color, RectangleF r, float radius, float width = 1f)
    {
        using var path = Rounded(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    /// <summary>Glyphs from Segoe Fluent Icons / MDL2 Assets.</summary>
    public static class Glyph
    {
        public const string Home = "";
        public const string Folder = "";
        public const string Settings = "";
        public const string Code = "";
        public const string Send = "";
        public const string Upload = "";
        public const string Download = "";
        public const string Delete = "";
        public const string Copy = "";
        public const string Cancel = "";
        public const string More = "";
        public const string Phone = "";
        public const string Link = "";
        public const string Document = "";
        public const string Photo = "";
        public const string List = "";
        public const string Grid = "";
        public const string Check = "";
        public const string OpenFile = "";
        public const string Paste = "";
    }
}
