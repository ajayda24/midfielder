using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Midfielder.App.Ui;
using Midfielder.Core.Crypto;
using Midfielder.Core.Net;
using Midfielder.Protocol;
using Xunit;
using W = System.Windows;

namespace Midfielder.App.Tests;

/// <summary>
/// Opens the real WPF main window against a fake app at three sizes: every page renders, screenshots go to
/// $MIDFIELDER_SCREENS (published by CI for review), layout rules hold (opens at the top, no empty scroll tail,
/// nothing cut off sideways), and opening/closing the window repeatedly leaks no handles or windows.
/// </summary>
[Collection("ui")]
public sealed partial class MainWindowTests : IDisposable
{
    private static readonly (int W, int H)[] Sizes = [(800, 600), (1280, 800), (1920, 1080)];
    private static readonly string[] Pages = [MainWindow.HomeName, MainWindow.FilesName, MainWindow.SettingsName, MainWindow.DeveloperName];

    private readonly string _dir = Directory.CreateTempSubdirectory("midfielder-ui-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A thumbnail may still be open; temp is cleaned up eventually.
        }
    }

    [Fact]
    public void EveryPageRendersAndNothingLeaks()
    {
        OnSta(() =>
        {
            Anim.Disabled = true;
            var host = new FakeHost(_dir);
            string? screens = Environment.GetEnvironmentVariable("MIDFIELDER_SCREENS");
            if (screens is not null) Directory.CreateDirectory(screens);

            var problems = new List<string>();
            RunWindow(host, screens, Sizes, problems); // warm-up (theme, fonts), with screenshots
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

            long gdi = GuiResources(0), user = GuiResources(1);
            for (int i = 0; i < 4; i++) RunWindow(host, null, Sizes[1..2], problems);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Pump(300);
            long gdiAfter = GuiResources(0), userAfter = GuiResources(1);
            Assert.True(gdiAfter - gdi < 40, $"GDI handles grew from {gdi} to {gdiAfter}");
            Assert.True(userAfter - user < 40, $"USER handles grew from {user} to {userAfter}");
            Assert.Empty(W.Application.Current.Windows);
        });
    }

    private static void RunWindow(FakeHost host, string? screens, (int W, int H)[] sizes, List<string> problems)
    {
        var window = new MainWindow(host) { WindowStartupLocation = W.WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        foreach (var (w, h) in sizes)
        {
            window.Width = w;
            window.Height = h;
            foreach (string page in Pages)
            {
                window.ShowPage(page);
                Pump(page == MainWindow.FilesName ? 1_200 : 300);
                Check(window, $"{w}x{h} {page}", problems);
                Snap(window, screens, $"windows-{w}-{page}.png");
            }
            ToggleGallery(window);
            Pump(1_000);
            Snap(window, screens, $"windows-{w}-files-gallery.png");
            ToggleGallery(window);
        }
        window.Close();
        Pump(150);
    }

    /// <summary>Layout rules: page opens at the top, no empty scroll tail, nothing sticks out to the right.</summary>
    private static void Check(MainWindow window, string where, List<string> problems)
    {
        EnsureLaidOut(window);
        var page = window.Page;
        if (page is null || page.ActualWidth < 100 || page.ActualHeight < 100)
        {
            problems.Add($"{where}: page not laid out ({page?.ActualWidth:0}x{page?.ActualHeight:0}, window visible: {window.IsVisible})");
            return;
        }
        if (page.Scroller is { } sv)
        {
            if (sv.VerticalOffset != 0) problems.Add($"{where}: opened scrolled to {sv.VerticalOffset}");
            double content = page.Body.ActualHeight + page.Body.Margin.Top + page.Body.Margin.Bottom;
            if (sv.ExtentHeight > content + 2) problems.Add($"{where}: scroll range {sv.ExtentHeight:0} > content {content:0}");
        }
        double right = page.ActualWidth;
        foreach (var e in Descendants(page).OfType<W.FrameworkElement>())
        {
            if (!e.IsVisible || e.ActualWidth <= 0 || e is W.Controls.Primitives.ScrollBar) continue;
            if (Ancestors(e).Any(a => a is W.Controls.ScrollViewer s && s != page.Scroller && s.HorizontalScrollBarVisibility != W.Controls.ScrollBarVisibility.Disabled)) continue;
            var edge = e.TransformToAncestor(page).Transform(new W.Point(e.ActualWidth, 0)).X;
            if (edge > right + 1)
            {
                problems.Add($"{where}: {e.GetType().Name} '{(e as W.Controls.TextBlock)?.Text}' sticks out by {edge - right:0}px");
                break;
            }
        }
    }

    private static IEnumerable<W.DependencyObject> Descendants(W.DependencyObject root)
    {
        int n = W.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = W.Media.VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static IEnumerable<W.DependencyObject> LogicalDescendants(W.DependencyObject root)
    {
        foreach (var c in W.LogicalTreeHelper.GetChildren(root).OfType<W.DependencyObject>())
        {
            yield return c;
            foreach (var d in LogicalDescendants(c)) yield return d;
        }
    }

    private static IEnumerable<W.DependencyObject> Ancestors(W.DependencyObject e)
    {
        for (var p = W.Media.VisualTreeHelper.GetParent(e); p is not null; p = W.Media.VisualTreeHelper.GetParent(p)) yield return p;
    }

    private static void ToggleGallery(MainWindow window)
    {
        window.ShowPage(MainWindow.FilesName);
        Pump(100);
        EnsureLaidOut(window);
        var toggle = Descendants(window).Concat(LogicalDescendants(window)).OfType<Wpf.Ui.Controls.Button>()
            .First(b => b.Content is "Gallery" or "List");
        toggle.RaiseEvent(new W.RoutedEventArgs(W.Controls.Primitives.ButtonBase.ClickEvent));
    }

    /// <summary>Renders the window's content over a solid backdrop (Mica isn't captured by RenderTargetBitmap).</summary>
    private static void Snap(MainWindow window, string? dir, string name)
    {
        if (dir is null) return;
        var root = (W.FrameworkElement)window.Content;
        var (w, h) = EnsureLaidOut(window);
        var visual = new W.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new W.Media.SolidColorBrush(W.Media.Color.FromRgb(0xEE, 0xF2, 0xF0)), null, new W.Rect(0, 0, w, h));
            dc.DrawRectangle(new W.Media.VisualBrush(root), null, new W.Rect(0, 0, w, h));
        }
        var bmp = new W.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, W.Media.PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new W.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(W.Media.Imaging.BitmapFrame.Create(bmp));
        using var file = File.Create(Path.Combine(dir, name));
        encoder.Save(file);
    }

    /// <summary>
    /// The CI runner's desktop may never give the window a real client area; lay the content out at the window's
    /// requested size so checks and screenshots see the same layout a user would.
    /// </summary>
    private static (int W, int H) EnsureLaidOut(MainWindow window)
    {
        var root = (W.FrameworkElement)window.Content;
        window.UpdateLayout();
        // CI runners have no interactive desktop, so the window may keep a size of its own (or none, or its
        // content's). Lay the content out at the requested client size so checks and screenshots are real.
        var size = new W.Size(window.Width, window.Height - 32);
        if (Math.Abs(root.ActualWidth - size.Width) > 1 || Math.Abs(root.ActualHeight - size.Height) > 1)
        {
            root.Measure(size);
            root.Arrange(new W.Rect(size));
            root.UpdateLayout();
        }
        return ((int)Math.Max(1, root.ActualWidth), (int)Math.Max(1, root.ActualHeight));
    }

    private static void Pump(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromMinutes(4)), "UI test hung");
        if (error is not null) throw new InvalidOperationException("UI test failed", error);
    }

    private static long GuiResources(uint kind) => GetGuiResources(GetCurrentProcess(), kind);

    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(IntPtr process, uint flags);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    private sealed class FakeHost : IAppHost
    {
        private readonly PairedDevice _phone = new(new Fingerprint(new string('a', 64)), "Realme P3 Ultra", DeviceKind.Android, "192.168.1.23:47810");

        public FakeHost(string dir)
        {
            Settings = new Settings { ReceiveFolder = dir };
            History = new HistoryStore(null);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var records = new List<TransferRecord>();
            Color[] colors = [Color.SeaGreen, Color.SteelBlue, Color.Goldenrod, Color.IndianRed, Color.MediumPurple, Color.Teal];
            for (int i = 0; i < colors.Length; i++)
            {
                string path = Path.Combine(dir, $"IMG_20261020_{i:00}.png");
                using (var bmp = new Bitmap(320, 240))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(colors[i]);
                    using var b = new SolidBrush(Color.FromArgb(120, Color.White));
                    g.FillEllipse(b, 60 + i * 20, 40, 160, 160);
                    bmp.Save(path, ImageFormat.Png);
                }
                records.Add(new TransferRecord($"img{i}", TransferDirection.Received, Path.GetFileName(path), "image/png", new FileInfo(path).Length,
                    "Realme P3 Ultra", now - i * 3_600_000, TransferStatus.Done, path));
            }
            string doc = Path.Combine(dir, "Quarterly report.pdf");
            File.WriteAllBytes(doc, new byte[48_000]);
            records.Add(new TransferRecord("doc", TransferDirection.Sent, "Quarterly report.pdf", "application/pdf", 48_000, "Realme P3 Ultra", now - 90_000_000, TransferStatus.Done, doc));
            records.Add(new TransferRecord("fail", TransferDirection.Sent, "holiday.mov", "video/quicktime", 812_000_000, "Realme P3 Ultra", now - 200_000_000, TransferStatus.Failed, Path.Combine(dir, "holiday.mov")));
            History.Add(records);
            var today = DateTime.Now;
            for (int d = 0; d < 7; d++)
            {
                for (int k = 0; k < (d * 3 + 2) % 9; k++) History.RecordClip(TransferDirection.Sent, today.AddDays(-d));
                History.RecordFiles(TransferDirection.Received, d % 3, 1_500_000L * d, today.AddDays(-d));
            }
            Transfers.Start("t1", TransferDirection.Received, "VID_20261020.mp4", "Realme P3 Ultra", 250_000_000);
            Transfers.Progress("t1", 160_000_000, 250_000_000);
            AppLog.Info("UI test: started");
            AppLog.Error("UI test: an example error line");
        }

        public HistoryStore History { get; }
        public TransferTracker Transfers { get; } = new(minIntervalMs: 0);
        public Settings Settings { get; }
        public IReadOnlyList<PairedDevice> PairedDevices => [_phone];
        public IReadOnlyList<PairedDevice> ConnectedDevices => [_phone];
        public string DeviceId => "0123456789abcdef0123456789abcdef";
        public string Fingerprint => new Fingerprint(new string('b', 64)).Display();
        public int Port => 47810;
        public string StatusText => "Connected: Realme P3 Ultra";
        public ModernMenuStatus ModernMenu => ModernMenuStatus.Off;

        public event Action? StatusChanged;

        public string? AddressOf(PairedDevice device) => "192.168.1.23";
        public void PickAndSendFiles() { }
        public void SendFiles(IReadOnlyList<string> paths) { }
        public void SendClipboardNow() { }
        public void CancelTransfer(string id) => Transfers.Finish(id);
        public void ShowPairing() { }
        public void ForgetDevices() { }
        public void SetSyncClipboard(bool on) => Settings.SyncClipboard = on;
        public void SetAutoAccept(bool on) => Settings.AutoAcceptFiles = on;
        public void SetStartWithWindows(bool on) => Settings.StartWithWindows = on;
        public void SetContextMenu(bool on) => Settings.ContextMenu = on;
        public void SetReceiveFolder(string path) => Settings.ReceiveFolder = path;
        public void SaveSettings() { }
        public Task<string> SetModernMenuAsync(bool on) => Task.FromResult("ok");

        public void RaiseStatus() => StatusChanged?.Invoke();
    }
}
