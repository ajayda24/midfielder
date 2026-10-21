using Microsoft.Toolkit.Uwp.Notifications;
using Midfielder.Core.Feature;
using Midfielder.Core.Net;
using Midfielder.Protocol;
using Windows.Foundation.Collections;
using Power = Windows.System.Power;

namespace Midfielder.App.Continuity;

/// <summary>A phone notification as the PC shows it.</summary>
internal sealed record PhoneNotification(string Key, string App, string Title, string Text, DateTime At, bool CanReply);

/// <summary>What the PC knows about the connected phone's optional features, for the UI.</summary>
internal sealed record PhoneState(
    string? Name,
    BatteryStatus? Battery,
    MediaState? Media,
    IReadOnlyList<PhoneNotification> Notifications,
    IReadOnlySet<string> Capabilities)
{
    public static readonly PhoneState Empty = new(null, null, null, [], new HashSet<string>());

    public bool CanRing => Capabilities.Contains(Features.Ring);
    public bool HasMedia => Media is { Active: true };
}

/// <summary>
/// The optional features on the PC: phone notifications as Windows notifications, battery both ways, find my phone /
/// PC, and music both ways. Each has its own switch in <see cref="FeatureSettings"/>; <see cref="Apply"/> starts what
/// is on and stops what is off (no events subscribed, nothing sent) and tells the phone which features are on.
/// Everything here runs on the UI thread: callers post to it.
/// </summary>
internal sealed class PcFeatures : IDisposable
{
    private const int MaxNotifications = 50;

    private readonly MidfielderNode _node;
    private readonly Func<FeatureSettings> _settings;
    private readonly SynchronizationContext _ui;
    private readonly Action<string?> _showMain;
    private readonly Toasts _toasts = new();
    private readonly BatteryAlerts _alerts = new();
    private readonly List<PhoneNotification> _notifications = [];
    private PhoneMediaControls? _phoneMedia;
    private PcMediaWatcher? _pcMedia;
    private RingOverlay? _ring;
    private bool _batteryOn;
    private BatteryStatus? _phoneBattery;
    private MediaState? _phoneMediaState;
    private BatteryStatus? _lastPcBattery;

    public PcFeatures(MidfielderNode node, Func<FeatureSettings> settings, SynchronizationContext ui, Action<string?> showMain)
    {
        _node = node;
        _settings = settings;
        _ui = ui;
        _showMain = showMain;
        _toasts.Activated += (args, input) => _ui.Post(_ => OnToast(args, input), null);
    }

    /// <summary>Phone state changed (battery, music, notifications, capabilities). Raised on the UI thread.</summary>
    public event Action? Changed;

    public PhoneState Phone { get; private set; } = PhoneState.Empty;

    private FeatureSettings S => _settings();

    public void Apply()
    {
        var s = S;
        if (!s.Notify) ClearNotifications();
        if (s.Battery) StartBattery(); else StopBattery();
        if (!(s.Media && s.MediaPhoneToPc)) ClearPhoneMedia();
        if (s.Media && s.MediaPcToPhone)
        {
            _pcMedia ??= new PcMediaWatcher(state => _node.SendFeature(Features.Media, new Envelope { MediaState = state }));
            _ = _pcMedia.StartAsync();
        }
        else if (_pcMedia is not null)
        {
            _pcMedia.Dispose();
            _pcMedia = null;
            _node.SendFeature(Features.Media, new Envelope { MediaState = new MediaState { Active = false } });
        }
        if (!s.Ring) StopRinging();
        _alerts.FullAt = s.BatteryFullAt;
        _node.SetFeatures(Enabled());
        Refresh();
    }

    public IEnumerable<string> Enabled()
    {
        var s = S;
        if (s.Notify) yield return Features.Notify;
        if (s.Battery) yield return Features.Battery;
        if (s.Ring) yield return Features.Ring;
        if (s.Media) yield return Features.Media;
    }

    public void OnPeerConnected(PeerSession session)
    {
        if (session.Has(Features.Battery) && _lastPcBattery is { } b) session.TrySend(new Envelope { BatteryStatus = b });
        if (session.Has(Features.Media)) _pcMedia?.Resend();
        Refresh();
    }

    public void OnPeerDisconnected()
    {
        if (_node.ConnectedPeers.Count == 0)
        {
            ClearPhoneMedia();
            _phoneBattery = null;
        }
        Refresh();
    }

    public void OnMessage(PeerSession from, Envelope env)
    {
        var s = S;
        switch (env.BodyCase)
        {
            case Envelope.BodyOneofCase.FeaturesChanged:
                if (from.Has(Features.Battery) && _lastPcBattery is { } b) from.TrySend(new Envelope { BatteryStatus = b });
                if (from.Has(Features.Media)) _pcMedia?.Resend(); else ClearPhoneMedia();
                if (!from.Has(Features.Battery)) _phoneBattery = null;
                break;
            case Envelope.BodyOneofCase.NotificationPosted when s.Notify:
                OnPhoneNotification(from, env.NotificationPosted);
                break;
            case Envelope.BodyOneofCase.NotificationRemoved when s.Notify:
                Toasts.RemovePhoneNotification(env.NotificationRemoved.Key);
                _notifications.RemoveAll(n => n.Key == env.NotificationRemoved.Key);
                break;
            case Envelope.BodyOneofCase.BatteryStatus when s.Battery:
                OnPhoneBattery(from, env.BatteryStatus);
                break;
            case Envelope.BodyOneofCase.RingRequest when s.Ring:
                if (env.RingRequest.Start) StartRinging(from.Peer.Name); else StopRinging();
                break;
            case Envelope.BodyOneofCase.MediaState when s.Media && s.MediaPhoneToPc:
                _phoneMediaState = env.MediaState.Active ? env.MediaState : null;
                _phoneMedia ??= new PhoneMediaControls((action, seek) => _ui.Post(_ => SendMediaCommand(action, seek), null));
                _ = _phoneMedia.ShowAsync(env.MediaState);
                break;
            case Envelope.BodyOneofCase.MediaCommand when s.Media && s.MediaPcToPhone:
                _ = _pcMedia?.CommandAsync(env.MediaCommand);
                break;
            default:
                return;
        }
        Refresh();
    }

    // ---- Actions from the UI -----------------------------------------------------------------------

    public bool RingPhone(bool start = true) =>
        S.Ring && _node.SendFeature(Features.Ring, new Envelope { RingRequest = new RingRequest { Start = start } }) > 0;

    public void SendMediaCommand(MediaAction action, long seekMs = 0)
    {
        if (S.Media) _node.SendFeature(Features.Media, new Envelope { MediaCommand = new MediaCommand { Action = action, SeekMs = seekMs } });
    }

    public void Reply(string key, string text) => Act(new NotificationAction { Key = key, ReplyText = text });

    public void Dismiss(string key)
    {
        Act(new NotificationAction { Key = key, Dismiss = true });
        Toasts.RemovePhoneNotification(key);
        _notifications.RemoveAll(n => n.Key == key);
        Refresh();
    }

    private void Act(NotificationAction action)
    {
        if (S.Notify) _node.SendFeature(Features.Notify, new Envelope { NotificationAction = action });
    }

    // ---- Notifications -----------------------------------------------------------------------------

    private void OnPhoneNotification(PeerSession from, NotificationPosted n)
    {
        _notifications.RemoveAll(x => x.Key == n.Key);
        _notifications.Insert(0, new PhoneNotification(n.Key, n.AppName, n.Title, n.Text,
            DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, n.PostedAtMs)).LocalDateTime, n.CanReply && S.NotifyReply));
        if (_notifications.Count > MaxNotifications) _notifications.RemoveRange(MaxNotifications, _notifications.Count - MaxNotifications);
        try
        {
            _toasts.ShowPhoneNotification(n, from.Peer.Name, S.NotifyReply);
        }
        catch (Exception e)
        {
            AppLog.Error("Showing a phone notification failed", e);
        }
    }

    private void OnToast(ToastArguments args, ValueSet input)
    {
        if (!args.TryGetValue("action", out string? action))
        {
            _showMain(null); // clicked the notification itself
            return;
        }
        args.TryGetValue("key", out string? key);
        if (key is null) return;
        switch (action)
        {
            case "reply" when input.TryGetValue("reply", out object? text) && text is string reply && reply.Trim().Length > 0:
                Reply(key, reply.Trim());
                AppLog.Info("Replied to a phone notification");
                break;
            case "button" when args.TryGetValue("id", out string? id):
                Act(new NotificationAction { Key = key, ButtonId = id });
                break;
            case "dismiss":
                Dismiss(key);
                break;
        }
    }

    private void ClearNotifications()
    {
        if (_notifications.Count == 0) return;
        foreach (var n in _notifications) Toasts.RemovePhoneNotification(n.Key);
        _notifications.Clear();
    }

    // ---- Battery ------------------------------------------------------------------------------------

    private void OnPhoneBattery(PeerSession from, BatteryStatus b)
    {
        _phoneBattery = b;
        if (!S.BatteryAlerts) return;
        switch (_alerts.Update((int)b.Level, b.Charging))
        {
            case BatteryAlert.Full:
                _toasts.Show($"{from.Peer.Name} is charged to {b.Level}%", "Unplug it to keep the battery healthy", "battery");
                break;
            case BatteryAlert.Low:
                _toasts.Show($"{from.Peer.Name} battery is low: {b.Level}%", "Plug it in soon", "battery");
                break;
        }
    }

    private void StartBattery()
    {
        if (_batteryOn) return;
        _batteryOn = true;
        Power.PowerManager.RemainingChargePercentChanged += OnPcPower;
        Power.PowerManager.BatteryStatusChanged += OnPcPower;
        Power.PowerManager.PowerSupplyStatusChanged += OnPcPower;
        SendPcBattery();
    }

    private void StopBattery()
    {
        _phoneBattery = null;
        if (!_batteryOn) return;
        _batteryOn = false;
        Power.PowerManager.RemainingChargePercentChanged -= OnPcPower;
        Power.PowerManager.BatteryStatusChanged -= OnPcPower;
        Power.PowerManager.PowerSupplyStatusChanged -= OnPcPower;
        _lastPcBattery = null;
    }

    private void OnPcPower(object? sender, object e) => _ui.Post(_ => SendPcBattery(), null);

    private void SendPcBattery()
    {
        if (!_batteryOn) return;
        try
        {
            // Desktops without a battery have nothing to share.
            if (Power.PowerManager.BatteryStatus == Power.BatteryStatus.NotPresent) return;
            var b = new BatteryStatus
            {
                Level = (uint)Math.Clamp(Power.PowerManager.RemainingChargePercent, 0, 100),
                Charging = Power.PowerManager.BatteryStatus == Power.BatteryStatus.Charging,
                Plugged = Power.PowerManager.PowerSupplyStatus != Power.PowerSupplyStatus.NotPresent,
            };
            if (b.Equals(_lastPcBattery)) return;
            _lastPcBattery = b;
            _node.SendFeature(Features.Battery, new Envelope { BatteryStatus = b });
        }
        catch (Exception e)
        {
            AppLog.Error("Reading the PC battery failed", e);
        }
    }

    // ---- Ring and media -----------------------------------------------------------------------------

    private void StartRinging(string phoneName)
    {
        if (_ring is not null)
        {
            _ring.Activate();
            return;
        }
        try
        {
            _ring = new RingOverlay(phoneName);
            _ring.Closed += (_, _) => _ring = null;
            _ring.Show();
            _ring.Activate();
            AppLog.Info($"Ringing: {phoneName} is looking for this PC");
        }
        catch (Exception e)
        {
            AppLog.Error("Couldn't show the ring overlay", e);
            _ring = null;
        }
    }

    private void StopRinging() => _ring?.Close();

    private void ClearPhoneMedia()
    {
        _phoneMediaState = null;
        _phoneMedia?.Dispose();
        _phoneMedia = null;
    }

    private void Refresh()
    {
        var peers = _node.ConnectedPeers;
        var peer = peers.Count > 0 ? peers[0] : null;
        var session = peer is null ? null : _node.Session(peer.Fingerprint);
        Phone = new PhoneState(
            peer?.Name,
            S.Battery ? _phoneBattery : null,
            S.Media && S.MediaPhoneToPc ? _phoneMediaState : null,
            S.Notify ? _notifications.ToList() : [],
            session?.Capabilities ?? new HashSet<string>());
        Changed?.Invoke();
    }

    public void Dispose()
    {
        StopBattery();
        _pcMedia?.Dispose();
        _phoneMedia?.Dispose();
        _ring?.Close();
        _toasts.Dispose();
    }
}
