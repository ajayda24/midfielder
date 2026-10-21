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
    private readonly TextBlock _battery = Fx.Caption("");
    private readonly Fluent.Button _ring;
    private readonly Fluent.Button _phoneAudio;
    private readonly Fluent.Button _pcAudio;
    private readonly Border _mediaCard;
    private readonly TextBlock _mediaTitle = Fx.Text("", 15, FontWeights.SemiBold);
    private readonly TextBlock _mediaArtist = Fx.Caption("");
    private readonly Image _mediaArt = new() { Width = 56, Height = 56, Stretch = Stretch.UniformToFill };
    private readonly Fluent.Button _mediaToggle;
    private readonly Border _notifyCard;
    private readonly Border _callCard;
    private readonly TextBlock _callWho = Fx.Text("", 15, FontWeights.SemiBold);
    private readonly TextBlock _callTime = Fx.Caption("");
    private readonly System.Windows.Threading.DispatcherTimer _callClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly StackPanel _notifyRows = new();
    private string? _artFor;
    private readonly System.Windows.Threading.DispatcherTimer _coalesce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public HomePage(IAppHost host, MainWindow window) : base(host, window)
    {
        _thumbs = new ThumbnailCache(Dispatcher, 160);
        Add(Header("Home", "Your clipboard and files, on this PC and your phone"), 0);

        // Hero
        var chip = Fx.Chip("", Fx.Offline, Fx.Surface, out _chipText, out _chipDot);
        _pair = Fx.Primary("Pair a phone", Fluent.SymbolRegular.QrCode24, Host.ShowPairing);
        // The chip keeps its size; the address or pairing hint takes the rest and wraps.
        var info = Fx.Stack(6, _phoneName, Fx.Line(chip, _address, null, 10), _battery);
        _battery.Visibility = Visibility.Collapsed;
        _address.VerticalAlignment = VerticalAlignment.Center;
        var badge = Fx.Badge(Fluent.SymbolRegular.Phone24, 64, Fx.Green, Brushes.White);
        var hero = Fx.Card(Fx.Line(badge, info, _pair, 20), 24);
        hero.Background = Fx.Hero;
        hero.BorderBrush = Fx.HighlightStrong;
        Add(hero, 24);

        // Actions
        _ring = Fx.Secondary("Ring phone", Fluent.SymbolRegular.Speaker224, () =>
        {
            if (Host.RingPhone()) Window.Toast("Ringing your phone");
        });
        _phoneAudio = Fx.Secondary("Phone audio here", Fluent.SymbolRegular.HeadphonesSoundWave24, () => _ = TogglePhoneAudio());
        _pcAudio = Fx.Secondary("Play on phone", Fluent.SymbolRegular.PhoneSpeaker24, TogglePcAudio);
        Add(Fx.Wrap(10,
            Fx.Primary("Send files…", Fluent.SymbolRegular.ArrowUpload24, Host.PickAndSendFiles),
            Fx.Secondary("Send clipboard now", Fluent.SymbolRegular.ClipboardPaste24, Host.SendClipboardNow),
            Fx.Secondary("Received files", Fluent.SymbolRegular.FolderOpen24, () => Shell.OpenFolder(Host.Settings.ReceiveFolder)),
            _ring, _phoneAudio, _pcAudio), 16);

        // On a call (only while Calls is on and the phone is in a call): its own row, not more hero buttons.
        var callBadge = Fx.Badge(Fluent.SymbolRegular.Call24, 44, Fx.Green, Brushes.White, circle: true);
        var callActions = Fx.Row(8,
            Fx.Secondary("Silence", Fluent.SymbolRegular.SpeakerMute24, () => Host.CallCommand(Protocol.CallAction.Silence)),
            Fx.Button("End", Fluent.SymbolRegular.CallEnd24, Fluent.ControlAppearance.Danger, () => Host.CallCommand(Protocol.CallAction.End)));
        _callCard = Fx.Card(Fx.Line(callBadge, Fx.Stack(2, _callWho, _callTime), callActions, 16), 16);
        _callCard.BorderBrush = Fx.HighlightStrong;
        _callCard.Visibility = Visibility.Collapsed;
        Add(_callCard, 16);
        _callClock.Tick += (_, _) => RenderCallTime();

        // Drop zone
        Add(DropZone(), 6);

        // Transfers (only while something moves)
        _transfersCard = Fx.Card(Fx.Stack(4, Fx.Heading("Transfers"), _transferRows));
        _transfersCard.Visibility = Visibility.Collapsed;
        Add(_transfersCard, 16);

        // Now playing on the phone (only while music control is on and something plays)
        _mediaToggle = Fx.IconButton(Fluent.SymbolRegular.Pause24, "Play or pause", () => Host.SendMediaCommand(Protocol.MediaAction.Toggle));
        var controls = Fx.Row(4,
            Fx.IconButton(Fluent.SymbolRegular.Previous24, "Previous", () => Host.SendMediaCommand(Protocol.MediaAction.Previous)),
            _mediaToggle,
            Fx.IconButton(Fluent.SymbolRegular.Next24, "Next", () => Host.SendMediaCommand(Protocol.MediaAction.Next)));
        var art = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Background = Fx.Highlight, Child = _mediaArt, Width = 56, Height = 56 };
        _mediaCard = Fx.Card(Fx.Stack(10, Fx.Heading("Playing on your phone"), Fx.Line(art, Fx.Stack(2, _mediaTitle, _mediaArtist), controls)));
        _mediaCard.Visibility = Visibility.Collapsed;
        Add(_mediaCard, 16);

        // Phone notifications (only while that feature is on)
        _notifyCard = Fx.Card(Fx.Stack(6, Fx.Heading("Phone notifications"), _notifyRows));
        _notifyCard.Visibility = Visibility.Collapsed;
        Add(_notifyCard, 16);

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
        Host.PhoneChanged += OnPhone;
        Host.Transfers.Changed += OnTransfers;
        Host.History.Changed += OnHistory;
        RenderStatus();
        RenderPhone();
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

    private bool _wasConnected;

    private void RenderStatus()
    {
        var connected = Host.ConnectedDevices;
        var paired = Host.PairedDevices;
        _phoneName.Text = connected.Count > 0 ? string.Join(", ", connected.Select(d => d.Name))
            : paired.Count > 0 ? string.Join(", ", paired.Select(d => d.Name))
            : "No phone paired yet";
        _chipText.Text = connected.Count > 0 ? "Connected" : paired.Count > 0 ? "Looking for your phone…" : "Not paired";
        _chipDot.Fill = connected.Count > 0 ? Fx.Online : Fx.Offline;
        if ((connected.Count > 0) != _wasConnected) Anim.Pulse(_chipDot, connected.Count > 0);
        _wasConnected = connected.Count > 0;
        _address.Text = connected.Count > 0 && Host.AddressOf(connected[0]) is { } a ? a
            : paired.Count == 0 ? "Click Pair a phone and scan the code with Midfielder on Android" : "";
        _pair.Visibility = paired.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPhone() => Post(RenderPhone);

    /// <summary>Phone battery, Ring button, now playing and notifications: each shown only while its feature is on.</summary>
    private void RenderPhone()
    {
        var phone = Host.Phone;
        var f = Host.Settings.Features;
        if (phone.Battery is { } b)
        {
            _battery.Text = $"Battery {b.Level}%{(b.Charging ? " · charging" : b.Plugged ? " · plugged in" : "")}";
            _battery.Visibility = Visibility.Visible;
        }
        else
        {
            _battery.Visibility = Visibility.Collapsed;
        }
        _ring.Visibility = f.Ring && phone.CanRing ? Visibility.Visible : Visibility.Collapsed;
        var audio = Host.Audio;
        _phoneAudio.Visibility = f.Audio && f.AudioPhoneToPc ? Visibility.Visible : Visibility.Collapsed;
        _phoneAudio.Content = audio.Connecting ? "Connecting…" : audio.PhoneAudioOn is not null ? "Stop phone audio" : "Phone audio here";
        _phoneAudio.Appearance = audio.PhoneAudioOn is not null ? Fluent.ControlAppearance.Primary : Fluent.ControlAppearance.Secondary;
        _pcAudio.Visibility = f.Audio && f.AudioPcToPhone && phone.Capabilities.Contains(Midfielder.Core.Feature.Features.Audio)
            ? Visibility.Visible : Visibility.Collapsed;
        _pcAudio.Content = audio.PcAudioOnPhone ? "Stop playing on phone" : "Play on phone";
        _pcAudio.Appearance = audio.PcAudioOnPhone ? Fluent.ControlAppearance.Primary : Fluent.ControlAppearance.Secondary;

        RenderCall(phone);

        if (phone.Media is { Active: true } m)
        {
            _mediaTitle.Text = m.Title.Length > 0 ? m.Title : m.AppName;
            _mediaArtist.Text = string.Join(" · ", new[] { m.Artist, m.AppName }.Where(x => x.Length > 0));
            if (_mediaPlaying != m.Playing)
            {
                _mediaPlaying = m.Playing;
                _mediaToggle.Icon = new Fluent.SymbolIcon { Symbol = m.Playing ? Fluent.SymbolRegular.Pause24 : Fluent.SymbolRegular.Play24, FontSize = 16 };
            }
            if (!m.ArtUnchanged || _artFor is null)
            {
                _artFor = m.Title;
                _mediaArt.Source = m.ArtJpeg.IsEmpty ? null : Decode(m.ArtJpeg.ToByteArray());
            }
            _mediaCard.Visibility = Visibility.Visible;
        }
        else
        {
            _artFor = null;
            _mediaCard.Visibility = Visibility.Collapsed;
        }

        _notifyCard.Visibility = f.Notify && phone.Notifications.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderNotifications(phone.Notifications.Take(5).ToList());
    }

    private bool? _mediaPlaying;
    private readonly Dictionary<string, (Continuity.PhoneNotification N, UIElement Row)> _notifyByKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Keeps each notification's row (and the reply being typed in it) as long as the notification exists; only
    /// new, changed or removed ones change. A battery or music update never wipes a half-written reply.
    /// </summary>
    private void RenderNotifications(IReadOnlyList<Continuity.PhoneNotification> list)
    {
        var rows = new List<UIElement>(list.Count);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in list)
        {
            keep.Add(n.Key);
            if (!_notifyByKey.TryGetValue(n.Key, out var known) || known.N != n)
            {
                known = (n, NotificationRow(n));
                _notifyByKey[n.Key] = known;
            }
            rows.Add(known.Row);
        }
        foreach (var gone in _notifyByKey.Keys.Where(k => !keep.Contains(k)).ToList()) _notifyByKey.Remove(gone);
        if (rows.SequenceEqual(_notifyRows.Children.Cast<UIElement>())) return;
        _notifyRows.Children.Clear();
        foreach (var row in rows)
        {
            if (row is FrameworkElement fe) fe.Margin = new Thickness(0, _notifyRows.Children.Count == 0 ? 0 : 10, 0, 0);
            _notifyRows.Children.Add(row);
        }
    }

    private async Task TogglePhoneAudio()
    {
        if (Host.Audio.PhoneAudioOn is not null)
        {
            Host.StopPhoneAudio();
            return;
        }
        string? problem = await Host.PlayPhoneAudioAsync();
        if (Disposed) return;
        Window.Toast(problem ?? "Your phone's sound now plays on this PC");
        RenderPhone();
    }

    private void TogglePcAudio()
    {
        if (Host.Audio.PcAudioOnPhone)
        {
            Host.StopPcAudioOnPhone();
        }
        else
        {
            Window.Toast(Host.PlayPcAudioOnPhone() ?? "This PC's sound now plays on your phone");
        }
        RenderPhone();
    }

    private UIElement NotificationRow(Continuity.PhoneNotification n)
    {
        string key = n.Key;
        var head = Fx.Caption($"{n.App} · {Fx.When(n.At)}");
        var title = Fx.Text(n.Title, 14, FontWeights.SemiBold);
        var text = Fx.Text(n.Text, 13.5, color: Fx.Muted, wrap: true);
        text.MaxHeight = 60;
        var dismiss = Fx.IconButton(Fluent.SymbolRegular.Dismiss24, "Dismiss on phone", () => Host.DismissNotification(key));
        var body = Fx.Stack(2, head, title, text);
        if (n.CanReply)
        {
            var box = new Fluent.TextBox { PlaceholderText = "Reply", MinWidth = 200 };
            void Send()
            {
                string reply = box.Text.Trim();
                if (reply.Length == 0) return;
                Host.ReplyToNotification(key, reply);
                box.Text = "";
                Window.Toast("Reply sent");
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Send();
                    e.Handled = true;
                }
            };
            var reply = Fx.Line(null, box, Fx.IconButton(Fluent.SymbolRegular.Send24, "Send reply", Send), 8);
            reply.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(reply);
        }
        return Fx.Line(Fx.Badge(Fluent.SymbolRegular.Alert24, 36), body, dismiss, 12);
    }

    private static ImageSource? Decode(byte[] jpeg)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(jpeg);
            image.DecodePixelWidth = 112;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or IOException)
        {
            return null;
        }
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

    private string? _recentIds;

    private void RenderHistory()
    {
        // Recent files
        var recent = Host.History.Records().Take(8).ToList();
        _recentCard.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Only when the files themselves changed (a synced clip also changes History): no flicker, no replayed fade.
        string ids = string.Join('|', recent.Select(r => r.Id + r.Status));
        if (ids != _recentIds)
        {
            bool first = _recentIds is null;
            _recentIds = ids;
            _recent.Children.Clear();
            foreach (var r in recent) _recent.Children.Add(RecentTile(r));
            if (first) Anim.Stagger(_recent.Children.Cast<UIElement>(), 30);
        }

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

    private void RenderCall(Continuity.PhoneState phone)
    {
        if (phone.Call is { } call && Host.Settings.Features.Calls)
        {
            string who = call.ContactName.Length > 0 ? call.ContactName : call.Number.Length > 0 ? call.Number : "Unknown caller";
            _callWho.Text = call.Phase == Protocol.CallPhase.Ringing ? $"{who} is calling" : who;
            _callCard.Visibility = Visibility.Visible;
            RenderCallTime();
            if (call.Phase == Protocol.CallPhase.Active) _callClock.Start(); else _callClock.Stop();
        }
        else
        {
            _callClock.Stop();
            _callCard.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderCallTime()
    {
        if (Host.Phone.Call is not { } call) return;
        if (call.Phase != Protocol.CallPhase.Active)
        {
            _callTime.Text = "Ringing on your phone";
            return;
        }
        var d = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(call.StartedAtMs);
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        _callTime.Text = $"On a call · {(int)d.TotalMinutes}:{d.Seconds:00}";
    }

    protected override void OnDispose()
    {
        _callClock.Stop();
        Host.StatusChanged -= OnStatus;
        Host.PhoneChanged -= OnPhone;
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
