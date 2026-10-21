using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Ui;

/// <summary>Hero connection card, quick actions, drop zone, live transfers, recent files and activity.</summary>
internal sealed class HomePage : PageBase
{
    private readonly TextBlock _phoneName = Fx.Text("", 22, FontWeights.SemiBold);
    private readonly TextBlock _chipText;
    private readonly Ellipse _chipDot;
    private readonly TextBlock _address = Fx.Caption("", wrap: true);
    private readonly Fluent.Button _pair;
    private readonly Border _transfersCard;
    private readonly StackPanel _transferRows = new();
    private readonly Dictionary<string, (TextBlock Meta, ProgressBar Bar)> _rows = new(StringComparer.Ordinal);
    private readonly Border _recentCard;
    private readonly WrapPanel _recent = new();
    private readonly TextBlock _todayClips = Stat("0");
    private readonly TextBlock _todayFiles = Stat("0");
    private readonly TextBlock _todayBytes = Stat("0 B");
    private readonly TextBlock _week = Fx.Caption("", wrap: true);
    private readonly BarChart _chart = new() { Height = 150, Margin = new Thickness(0, 18, 0, 0) };
    private readonly ThumbnailCache _thumbs;
    private readonly System.Windows.Threading.DispatcherTimer _coalesce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public HomePage(IAppHost host, MainWindow window) : base(host, window)
    {
        _thumbs = new ThumbnailCache(Dispatcher, 160);
        Add(Header("Home", "Your clipboard and files, on this PC and your phone"), 0);

        // Hero
        var chip = Fx.Chip("", Fx.Offline, Fx.Surface, out _chipText, out _chipDot);
        _pair = Fx.Primary("Pair a phone", Fluent.SymbolRegular.QrCode24, Host.ShowPairing);
        // The chip keeps its size; the address or pairing hint takes the rest and wraps.
        var info = Fx.Stack(6, _phoneName, Fx.Line(chip, _address, null, 10));
        _address.VerticalAlignment = VerticalAlignment.Center;
        var badge = Fx.Badge(Fluent.SymbolRegular.Phone24, 64, Fx.Green, Brushes.White);
        var hero = Fx.Card(Fx.Line(badge, info, _pair, 20), 24);
        hero.Background = Fx.Hero;
        hero.BorderBrush = Fx.HighlightStrong;
        Add(hero, 24);

        // Actions
        Add(Fx.Wrap(10,
            Fx.Primary("Send files…", Fluent.SymbolRegular.ArrowUpload24, Host.PickAndSendFiles),
            Fx.Secondary("Send clipboard now", Fluent.SymbolRegular.ClipboardPaste24, Host.SendClipboardNow),
            Fx.Secondary("Received files", Fluent.SymbolRegular.FolderOpen24, () => Shell.OpenFolder(Host.Settings.ReceiveFolder))), 16);

        // Drop zone
        Add(DropZone(), 6);

        // Transfers (only while something moves)
        _transfersCard = Fx.Card(Fx.Stack(4, Fx.Heading("Transfers"), _transferRows));
        _transfersCard.Visibility = Visibility.Collapsed;
        Add(_transfersCard, 16);

        // Recent files
        var seeAll = Fx.Subtle("See all", Fluent.SymbolRegular.ChevronRight24, () => Window.ShowPage(MainWindow.FilesName));
        _recentCard = Fx.Card(Fx.Stack(12, Fx.Split(Fx.Heading("Recent files"), seeAll), _recent));
        Add(_recentCard, 16);

        // Activity
        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 14, 0, 0) };
        stats.Children.Add(StatTile(_todayClips, "clips today", Fluent.SymbolRegular.Clipboard24));
        stats.Children.Add(StatTile(_todayFiles, "files today", Fluent.SymbolRegular.Document24));
        stats.Children.Add(StatTile(_todayBytes, "moved today", Fluent.SymbolRegular.ArrowSwap24));
        Add(Fx.Card(Fx.Stack(0, Fx.Heading("Activity"), stats, _chart, _week)), 16);

        _coalesce.Tick += (_, _) =>
        {
            _coalesce.Stop();
            RenderTransfers();
        };
        Host.StatusChanged += OnStatus;
        Host.Transfers.Changed += OnTransfers;
        Host.History.Changed += OnHistory;
        RenderStatus();
        RenderTransfers();
        RenderHistory();
    }

    private static TextBlock Stat(string text)
    {
        var t = Fx.Text(text, 26, FontWeights.SemiBold, Fx.PrimaryDark);
        t.FontFamily = Fx.Display;
        return t;
    }

    private static UIElement StatTile(TextBlock value, string caption, Fluent.SymbolRegular icon)
    {
        var tile = Fx.Line(Fx.Badge(icon, 40), Fx.Stack(0, value, Fx.Caption(caption)), null, 12);
        tile.Margin = new Thickness(0, 0, 12, 0);
        return tile;
    }

    private UIElement DropZone()
    {
        var bg = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0xE6, 0xF4, 0xEE)),
            Padding = new Thickness(20),
            Cursor = Cursors.Hand,
            AllowDrop = true,
        };
        var dash = new Rectangle
        {
            Stroke = Fx.Green,
            StrokeThickness = 1.5,
            StrokeDashArray = [5, 4],
            RadiusX = 14,
            RadiusY = 14,
            IsHitTestVisible = false,
        };
        var icon = Fx.Icon(Fluent.SymbolRegular.ArrowUpload24, 28);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var title = Fx.Text("Drop files here to send them to your phone", 15, FontWeights.SemiBold, Fx.PrimaryDark);
        var sub = Fx.Caption("or click to choose files");
        title.HorizontalAlignment = sub.HorizontalAlignment = HorizontalAlignment.Center;
        bg.Child = Fx.Stack(6, icon, title, sub);
        var zone = new Grid { Children = { bg, dash } };
        bg.MouseLeftButtonUp += (_, _) => Host.PickAndSendFiles();
        bg.DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            Anim.Tint(bg, Color.FromArgb(0xFF, 0xC9, 0xE7, 0xDA));
            title.Text = "Release to send";
        };
        bg.DragLeave += (_, _) =>
        {
            Anim.Tint(bg, Color.FromArgb(0x99, 0xE6, 0xF4, 0xEE));
            title.Text = "Drop files here to send them to your phone";
        };
        bg.Drop += (_, e) =>
        {
            Anim.Tint(bg, Color.FromArgb(0x99, 0xE6, 0xF4, 0xEE));
            title.Text = "Drop files here to send them to your phone";
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var existing = files.Where(File.Exists).ToList();
                if (existing.Count > 0)
                {
                    Host.SendFiles(existing);
                    Anim.Pop(zone);
                    Window.Toast(existing.Count == 1 ? $"Sending {System.IO.Path.GetFileName(existing[0])}" : $"Sending {existing.Count} files");
                }
            }
            e.Handled = true;
        };
        return zone;
    }

    private void OnStatus() => Post(RenderStatus);

    // Progress can tick several times a second per transfer; update at most every 150 ms.
    private void OnTransfers() => Post(() => { if (!_coalesce.IsEnabled) _coalesce.Start(); });

    private void OnHistory() => Post(RenderHistory);

    private void RenderStatus()
    {
        var connected = Host.ConnectedDevices;
        var paired = Host.PairedDevices;
        _phoneName.Text = connected.Count > 0 ? string.Join(", ", connected.Select(d => d.Name))
            : paired.Count > 0 ? string.Join(", ", paired.Select(d => d.Name))
            : "No phone paired yet";
        _chipText.Text = connected.Count > 0 ? "Connected" : paired.Count > 0 ? "Looking for your phone…" : "Not paired";
        _chipDot.Fill = connected.Count > 0 ? Fx.Online : Fx.Offline;
        Anim.Pulse(_chipDot, connected.Count > 0);
        _address.Text = connected.Count > 0 && Host.AddressOf(connected[0]) is { } a ? a
            : paired.Count == 0 ? "Click Pair a phone and scan the code with Midfielder on Android" : "";
        _pair.Visibility = paired.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderTransfers()
    {
        var active = Host.Transfers.Active();
        _transfersCard.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var ids = active.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList()) _rows.Remove(gone);
        if (_transferRows.Children.Count != active.Count || active.Any(t => !_rows.ContainsKey(t.Id)))
        {
            _transferRows.Children.Clear();
            foreach (var t in active)
            {
                var row = Row(t, out var meta, out var bar);
                _rows[t.Id] = (meta, bar);
                Fx.Add(_transferRows, row, 14);
                Anim.Enter(row);
            }
        }
        foreach (var t in active)
        {
            if (!_rows.TryGetValue(t.Id, out var r)) continue;
            r.Meta.Text = Meta(t);
            r.Bar.IsIndeterminate = t.Percent < 0;
            if (t.Percent >= 0) Anim.Glide(r.Bar, RangeBase.ValueProperty, t.Percent, 300);
        }
    }

    private UIElement Row(ActiveTransfer t, out TextBlock meta, out ProgressBar bar)
    {
        var name = Fx.Text(t.Title, 14.5, FontWeights.SemiBold);
        meta = Fx.Caption(Meta(t));
        string id = t.Id;
        var cancel = Fx.IconButton(Fluent.SymbolRegular.Dismiss24, "Cancel transfer", () => Host.CancelTransfer(id));
        var icon = Fx.Badge(t.Direction == TransferDirection.Sent ? Fluent.SymbolRegular.ArrowUpload24 : Fluent.SymbolRegular.ArrowDownload24, 40);
        bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, Margin = new Thickness(54, 10, 0, 0), Value = Math.Max(0, t.Percent) };
        return Fx.Stack(0, Fx.Line(icon, Fx.Stack(2, name, meta), cancel), bar);
    }

    private static string Meta(ActiveTransfer t)
    {
        string dir = t.Direction == TransferDirection.Sent ? "to" : "from";
        string pct = t.Percent >= 0 ? $" · {t.Percent}%" : "";
        return $"{Fx.Bytes(t.Done)} of {Fx.Bytes(t.Total)} {dir} {t.Peer}{pct}";
    }

    private void RenderHistory()
    {
        // Recent files
        var recent = Host.History.Records().Take(8).ToList();
        _recentCard.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _recent.Children.Clear();
        foreach (var r in recent) _recent.Children.Add(RecentTile(r));
        Anim.Stagger(_recent.Children.Cast<UIElement>(), 30);

        // Activity
        var today = DateTime.Today;
        var days = Host.History.LastDays(7, today);
        var t = days[^1];
        _todayClips.Text = t.Clips.ToString(CultureInfo.InvariantCulture);
        _todayFiles.Text = t.Files.ToString(CultureInfo.InvariantCulture);
        _todayBytes.Text = Fx.Bytes(t.Bytes);
        _chart.SetData(days.Select(d => d.Clips + d.Files).ToArray(),
            Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6).ToString("ddd", CultureInfo.InvariantCulture)).ToArray());
        var week = days.Aggregate(new Counts(), (a, b) => a + b);
        var all = Host.History.Totals();
        _week.Text = $"Last 7 days: {week.Clips} clips · {week.Files} files · {Fx.Bytes(week.Bytes)}     " +
                     $"All time: {all.ClipsSent + all.ClipsReceived} clips · {all.FilesSent + all.FilesReceived} files";
        _week.Margin = new Thickness(0, 10, 0, 0);
    }

    private UIElement RecentTile(TransferRecord r)
    {
        var image = new System.Windows.Controls.Image { Stretch = Stretch.UniformToFill };
        var glyph = Fx.Icon(r.IsImage ? Fluent.SymbolRegular.Image24 : Fluent.SymbolRegular.Document24, 30);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var frame = new Border
        {
            Width = 112,
            Height = 112,
            CornerRadius = new CornerRadius(12),
            Background = Fx.Highlight,
            ClipToBounds = true,
            Child = new Grid { Children = { glyph, image } },
        };
        frame.Clip = new RectangleGeometry(new Rect(0, 0, 112, 112), 12, 12);
        if (r.IsImage) _thumbs.Request(r.Location, src => image.Source = src);
        var name = Fx.Caption(r.Name);
        name.Width = 112;
        name.Margin = new Thickness(0, 6, 0, 0);
        name.TextAlignment = TextAlignment.Center;
        var tile = new StackPanel { Margin = new Thickness(0, 0, 12, 8), Cursor = Cursors.Hand, ToolTip = r.Name, Background = Fx.Transparent };
        tile.Children.Add(frame);
        tile.Children.Add(name);
        tile.MouseLeftButtonUp += (_, _) => FilesPage.Open(Window, Host, r);
        tile.MouseEnter += (_, _) => Anim.Glide(frame, OpacityProperty, 0.85, 120);
        tile.MouseLeave += (_, _) => Anim.Glide(frame, OpacityProperty, 1, 160);
        return tile;
    }

    protected override void OnDispose()
    {
        Host.StatusChanged -= OnStatus;
        Host.Transfers.Changed -= OnTransfers;
        Host.History.Changed -= OnHistory;
        _coalesce.Stop();
        _thumbs.Dispose();
    }
}

/// <summary>Seven rounded bars, oldest to today; bars grow in when shown.</summary>
internal sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty GrowProperty = DependencyProperty.Register(
        nameof(Grow), typeof(double), typeof(BarChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private long[] _values = [];
    private string[] _labels = [];

    public double Grow
    {
        get => (double)GetValue(GrowProperty);
        set => SetValue(GrowProperty, value);
    }

    public void SetData(long[] values, string[] labels)
    {
        bool first = _values.Length == 0;
        if (_values.SequenceEqual(values) && _labels.SequenceEqual(labels)) return;
        _values = values;
        _labels = labels;
        System.Windows.Automation.AutomationProperties.SetHelpText(this, string.Join(", ", labels.Zip(values, (l, v) => $"{l} {v}")));
        if (first && Anim.Enabled)
        {
            Grow = 0;
            Anim.Glide(this, GrowProperty, 1, 600);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_values.Length == 0 || ActualWidth <= 0) return;
        double labelH = 22, topPad = 18;
        double chartH = ActualHeight - labelH - topPad;
        double slot = ActualWidth / _values.Length;
        double barW = Math.Min(slot * 0.5, 34);
        long max = Math.Max(1, _values.Max());
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < _values.Length; i++)
        {
            double cx = slot * i + slot / 2;
            double h = (_values[i] == 0 ? 6 : (chartH - 6) * _values[i] / max + 6) * Grow;
            bool today = i == _values.Length - 1;
            var rect = new Rect(cx - barW / 2, topPad + chartH - h, barW, Math.Max(0, h));
            dc.DrawRoundedRectangle(today ? Fx.Green : Fx.HighlightStrong, null, rect, 8, 8);
            if (_values[i] > 0 && (today || _values[i] == max))
            {
                var v = new FormattedText(_values[i].ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Fx.Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12, Fx.PrimaryDark, dpi);
                dc.DrawText(v, new Point(cx - v.Width / 2, rect.Top - v.Height - 2));
            }
            if (i < _labels.Length)
            {
                var t = new FormattedText(_labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Fx.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, Fx.Muted, dpi);
                dc.DrawText(t, new Point(cx - t.Width / 2, ActualHeight - t.Height));
            }
        }
    }
}
