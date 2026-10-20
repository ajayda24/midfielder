using System.Runtime.InteropServices;

namespace Midfielder.App.Ui;

/// <summary>
/// The main window: navigation on the left, one page on the right. Opened from the tray and fully disposed when
/// closed, so the tray app keeps no UI in memory. Files dropped anywhere on the window go to the phone.
/// </summary>
internal sealed partial class MainForm : Form
{
    public const string HomeName = "home";
    public const string FilesName = "files";
    public const string SettingsName = "settings";
    public const string DeveloperName = "developer";

    private static readonly (string Name, string Label, string Glyph)[] Pages =
    [
        (HomeName, "Home", Theme.Glyph.Home),
        (FilesName, "Files", Theme.Glyph.Folder),
        (SettingsName, "Settings", Theme.Glyph.Settings),
        (DeveloperName, "Developer", Theme.Glyph.Code),
    ];

    private readonly IAppHost _host;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly Dictionary<string, NavItem> _nav = [];
    private readonly Label _navStatus = Labels.Muted("", 8.5f);
    private readonly Bitmap _logo;
    private Control? _page;

    public MainForm(IAppHost host, string page = HomeName)
    {
        _host = host;
        Text = "Midfielder";
        Icon = AppIcon.Window;
        // Sizes below are scaled by hand (Px) and controls scale themselves, so no WinForms auto-scaling on top.
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Background;
        Font = Theme.Body(10f);
        ClientSize = new Size(Px(1040), Px(700));
        MinimumSize = new Size(Px(820), Px(560));
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        var nav = new Panel { Dock = DockStyle.Left, Width = Px(220), BackColor = Theme.Surface, Padding = new Padding(0, Px(20), 0, Px(16)) };
        nav.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Outline);
            e.Graphics.DrawLine(pen, nav.Width - 1, 0, nav.Width - 1, nav.Height);
        };
        var brand = new HStack { Dock = DockStyle.Top, MinHeight = 56, Padding = new Padding(20, 0, 0, 0), Spacing = 10 };
        _logo = AppIcon.Logo(64);
        var logo = new PictureBox { Image = _logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(Px(32), Px(32)), BackColor = Color.Transparent };
        brand.With(logo, Labels.Make("Midfielder", Theme.Strong(13f), Theme.Text));
        var items = new VStack { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0), Spacing = 4, BackColor = Theme.Surface };
        foreach (var (name, label, glyph) in Pages)
        {
            var item = new NavItem(label, glyph) { BackColor = Theme.Surface };
            item.Click += (_, _) => ShowPage(name);
            _nav[name] = item;
            items.Controls.Add(item);
        }
        _navStatus.Dock = DockStyle.Bottom;
        _navStatus.AutoSize = false;
        _navStatus.Height = Px(40);
        _navStatus.Padding = new Padding(Px(20), 0, Px(12), 0);
        _navStatus.TextAlign = ContentAlignment.MiddleLeft;
        nav.Controls.Add(items);
        nav.Controls.Add(_navStatus);
        nav.Controls.Add(brand);

        Controls.Add(_content);
        Controls.Add(nav);
        _host.StatusChanged += OnStatus;
        UpdateNavStatus();
        ShowPage(page);
    }

    public string CurrentPage { get; private set; } = "";

    private int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    public void ShowPage(string name)
    {
        if (!_nav.ContainsKey(name)) name = HomeName;
        if (name == CurrentPage && _page is not null) return;
        CurrentPage = name;
        foreach (var (k, item) in _nav) item.Selected = k == name;
        var old = _page;
        Control page = name switch
        {
            FilesName => new FilesPage(_host),
            SettingsName => new SettingsPage(_host),
            DeveloperName => new DeveloperPage(_host),
            _ => new HomePage(_host),
        };
        page.Dock = DockStyle.Fill;
        _content.SuspendLayout();
        _content.Controls.Add(page);
        if (old is not null)
        {
            _content.Controls.Remove(old);
            old.Dispose();
        }
        _content.ResumeLayout();
        _page = page;
        (page as Page)?.Activate();
    }

    private void OnStatus()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(UpdateNavStatus);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void UpdateNavStatus()
    {
        _navStatus.Text = "● " + _host.StatusText;
        _navStatus.ForeColor = _host.ConnectedDevices.Count > 0 ? Theme.Primary : Theme.Muted;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: light caption tinted like the window background, rounded corners. Ignored on Windows 10.
        int light = 0;
        _ = DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref light, sizeof(int));
        int caption = ColorTranslator.ToWin32(Theme.Surface);
        _ = DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        int round = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        if (drgevent.Data?.GetDataPresent(DataFormats.FileDrop) == true) drgevent.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        if (drgevent.Data?.GetData(DataFormats.FileDrop) is string[] files) _host.SendFiles(files.Where(File.Exists).ToList());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.StatusChanged -= OnStatus;
            _page?.Dispose();
            _page = null;
            _logo.Dispose();
        }
        base.Dispose(disposing);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWCP_ROUND = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
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

    public static Bitmap Logo(int px)
    {
        using var icon = new Icon(Source.Value, new Size(256, 256));
        using var big = icon.ToBitmap();
        var small = new Bitmap(px, px);
        using var g = Graphics.FromImage(small);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(big, 0, 0, px, px);
        return small;
    }
}
