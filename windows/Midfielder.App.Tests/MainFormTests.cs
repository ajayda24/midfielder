using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Midfielder.App.Ui;
using Midfielder.Core.Crypto;
using Midfielder.Core.Net;
using Midfielder.Protocol;
using Xunit;

namespace Midfielder.App.Tests;

/// <summary>
/// Opens the real main window against a fake app: every page renders, screenshots go to $MIDFIELDER_SCREENS
/// (uploaded by CI for review), and opening/closing the window repeatedly doesn't leak GDI or USER handles.
/// </summary>
[Collection("ui")]
public sealed partial class MainFormTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("midfielder-ui-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A thumbnail handle may still be closing; the temp folder is cleaned up eventually.
        }
    }

    [Fact]
    public void EveryPageRendersAndNothingLeaks()
    {
        OnSta(() =>
        {
            var host = new FakeHost(_dir);
            string? screens = Environment.GetEnvironmentVariable("MIDFIELDER_SCREENS");
            if (screens is not null) Directory.CreateDirectory(screens);

            // Warm-up round (fonts and other process-lifetime caches), with screenshots at every size.
            RunWindow(host, screens);
            long gdi = GuiResources(0), user = GuiResources(1);
            for (int i = 0; i < 4; i++) RunWindow(host, null);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Pump(200);
            long gdiAfter = GuiResources(0), userAfter = GuiResources(1);
            Assert.True(gdiAfter - gdi < 40, $"GDI handles grew from {gdi} to {gdiAfter}");
            Assert.True(userAfter - user < 40, $"USER handles grew from {user} to {userAfter}");
        });
    }

    private static readonly Size[] Sizes = [new(800, 600), new(1280, 800), new(1920, 1080)];

    private static void RunWindow(FakeHost host, string? screens)
    {
        var problems = new List<string>();
        using var form = new MainForm(host);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(0, 0);
        form.Show();
        foreach (var size in screens is null ? Sizes[1..2] : Sizes)
        {
            form.Size = size;
            foreach (string page in new[] { MainForm.HomeName, MainForm.FilesName, MainForm.SettingsName, MainForm.DeveloperName })
            {
                form.ShowPage(page);
                Pump(page == MainForm.FilesName ? 1_200 : 250); // let thumbnails load
                Check(form, $"{size.Width}x{size.Height} {page}", problems);
                Snap(form, screens, $"windows-{size.Width}-{page}.png");
            }
            FakeHost.FlipGallery(form);
            Pump(800);
            Snap(form, screens, $"windows-{size.Width}-files-gallery.png");
            FakeHost.FlipGallery(form);
        }
        form.Close();
        Pump(100);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static void Snap(Form form, string? dir, string name)
    {
        if (dir is null) return;
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
        bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
    }

    /// <summary>Layout rules: page opens at the top, no empty scroll tail, cards fit content, nothing overflows sideways.</summary>
    private static void Check(MainForm form, string where, List<string> problems)
    {
        var page = FakeHost.All(form).FirstOrDefault(c => c is Page or FilesPage or DeveloperPage);
        if (page is null)
        {
            problems.Add($"{where}: no page");
            return;
        }
        if (page is ScrollableControl { AutoScroll: true } sc && sc.AutoScrollPosition != Point.Empty)
        {
            problems.Add($"{where}: opened scrolled to {sc.AutoScrollPosition}");
        }
        if (page is VStack stack && stack.AutoScrollMinSize.Height > 0 && stack.AutoScrollMinSize.Height > stack.MeasureHeight(stack.ClientSize.Width) + 2)
        {
            problems.Add($"{where}: scroll range {stack.AutoScrollMinSize.Height} > content {stack.MeasureHeight(stack.ClientSize.Width)}");
        }
        var pageRect = page.RectangleToScreen(page.ClientRectangle);
        foreach (var c in FakeHost.All(page).Where(c => c.Visible))
        {
            if (c is Card card && Math.Abs(card.Height - card.MeasureHeight(card.Width)) > 2)
            {
                problems.Add($"{where}: card height {card.Height} vs content {card.MeasureHeight(card.Width)}");
            }
            if (c is PillButton b && b.Width < b.GetPreferredSize(Size.Empty).Width - 1)
            {
                problems.Add($"{where}: button '{b.Text}' clipped ({b.Width} < {b.GetPreferredSize(Size.Empty).Width})");
            }
            if (c is VScrollBar or HScrollBar) continue;
            var r = c.RectangleToScreen(c.ClientRectangle);
            if (r.Width > 0 && r.Right > pageRect.Right + 1) problems.Add($"{where}: {c.GetType().Name} '{c.Text}' overflows by {r.Right - pageRect.Right}px");
        }
    }

    private static void Pump(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Application.DoEvents();
            Thread.Sleep(15);
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
        Assert.True(t.Join(TimeSpan.FromMinutes(2)), "UI test hung");
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

        public void RaiseStatus() => StatusChanged?.Invoke();

        /// <summary>Clicks the Files page's Gallery/List toggle.</summary>
        public static void FlipGallery(MainForm form)
        {
            form.ShowPage(MainForm.FilesName);
            var toggle = All(form).OfType<PillButton>().First(b => b.Text is "Gallery" or "List");
            toggle.PerformClick();
        }

        public static IEnumerable<Control> All(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var d in All(c)) yield return d;
            }
        }
    }
}
