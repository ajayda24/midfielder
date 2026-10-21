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

/// <summary>Audio routes as the UI shows them.</summary>
internal sealed record AudioStatus(
    IReadOnlyList<BluetoothAudioSource> BluetoothDevices,
    BluetoothAudioSource? PhoneAudioOn,
    bool Connecting,
    bool PcAudioOnPhone)
{
    public static readonly AudioStatus Empty = new([], null, false, false);
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
    private readonly PhoneBluetoothAudio _bluetooth;
    private PcAudioStream? _pcAudio;
    private IDisposable? _pcAudioHold;

    public PcFeatures(MidfielderNode node, Func<FeatureSettings> settings, SynchronizationContext ui, Action<string?> showMain)
    {
        _node = node;
        _settings = settings;
        _ui = ui;
        _showMain = showMain;
        _toasts.Activated += (args, input) => _ui.Post(_ => OnToast(args, input), null);
        _bluetooth = new PhoneBluetoothAudio(ui);
        _bluetooth.Changed += Refresh;
    }

    public AudioStatus Audio => new(_bluetooth.Devices, _bluetooth.Connected, _bluetooth.IsConnecting, _pcAudio is not null);

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
        if (s.Audio && s.AudioPhoneToPc) _bluetooth.StartWatching(); else _bluetooth.StopWatching();
        if (!(s.Audio && s.AudioPcToPhone)) StopPcAudio("switched off");
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
        if (s.Audio) yield return Features.Audio;
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
            StopPcAudio("phone disconnected");
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
            case Envelope.BodyOneofCase.AudioRequest when s.Audio:
                OnAudioRequest(from, env.AudioRequest);
                break;
            case Envelope.BodyOneofCase.AudioStop when _pcAudio is not null && env.AudioStop.StreamId == _pcAudio.StreamId:
                StopPcAudio("stopped on the phone", tellPhone: false);
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

    // ---- Audio -------------------------------------------------------------------------------------

    private void OnAudioRequest(PeerSession from, AudioRequest r)
    {
        switch (r.Route)
        {
            case AudioRoute.PhoneToPc:
                if (r.Start) _ = PlayPhoneAudioAsync(); else _bluetooth.Disconnect();
                break;
            case AudioRoute.PcToPhone:
                if (r.Start) PlayPcAudioOnPhone(); else StopPcAudio("stopped on the phone");
                break;
        }
    }

    /// <summary>The phone's sound through this PC (Bluetooth). Returns null when playing, else why not.</summary>
    public async Task<string?> PlayPhoneAudioAsync()
    {
        var s = S;
        if (!(s.Audio && s.AudioPhoneToPc)) return "Turn on Audio → Phone audio on this PC in Settings first.";
        _bluetooth.StartWatching();
        var device = _bluetooth.Pick(s.AudioBluetoothId, Phone.Name);
        if (device is null)
        {
            // The watcher may still be enumerating right after it started.
            await Task.Delay(1500).ConfigureAwait(true);
            device = _bluetooth.Pick(s.AudioBluetoothId, Phone.Name);
        }
        if (device is null)
        {
            return _bluetooth.Devices.Count == 0
                ? "Pair your phone with this PC over Bluetooth first (Windows Settings → Bluetooth & devices)."
                : "Choose your phone in Settings → Features → Audio.";
        }
        return await _bluetooth.ConnectAsync(device).ConfigureAwait(true);
    }

    public void StopPhoneAudio() => _bluetooth.Disconnect();

    /// <summary>This PC's sound on the phone (Wi-Fi). Returns null when started, else why not.</summary>
    public string? PlayPcAudioOnPhone()
    {
        var s = S;
        if (!(s.Audio && s.AudioPcToPhone)) return "Turn on Audio → This PC's audio on the phone in Settings first.";
        if (_pcAudio is not null) return null;
        var peers = _node.ConnectedPeers;
        var session = peers.Count > 0 ? _node.Session(peers[0].Fingerprint) : null;
        if (session is null) return "Your phone isn't connected.";
        if (!session.Has(Features.Audio)) return "Turn on Audio in Midfielder on your phone too.";
        try
        {
            var stream = new PcAudioStream(MidfielderNode.NewTransferId(), s.AudioMutePc);
            stream.Ended += () => _ui.Post(_ =>
            {
                if (ReferenceEquals(_pcAudio, stream)) StopPcAudio("stream ended");
            }, null);
            _pcAudio = stream;
            _pcAudioHold = Performance.Hold();
            _node.ServeStream(session.Peer.Fingerprint, stream.StreamId, stream.WriteAsync);
            session.TrySend(new Envelope { AudioOffer = new AudioOffer { StreamId = stream.StreamId, SampleRate = (uint)stream.SampleRate, Channels = 2 } });
            AppLog.Info($"Playing this PC's audio on {session.Peer.Name} ({stream.SampleRate} Hz)");
            Refresh();
            return null;
        }
        catch (Exception e)
        {
            AppLog.Error("Couldn't capture this PC's audio", e);
            StopPcAudio("failed");
            return "Couldn't capture this PC's sound: " + e.Message;
        }
    }

    public void StopPcAudio(string reason, bool tellPhone = true)
    {
        var stream = _pcAudio;
        if (stream is null) return;
        _pcAudio = null;
        _node.StopServing(stream.StreamId);
        if (tellPhone) _node.SendFeature(Features.Audio, new Envelope { AudioStop = new AudioStop { StreamId = stream.StreamId, Reason = reason } });
        stream.Dispose();
        _pcAudioHold?.Dispose();
        _pcAudioHold = null;
        AppLog.Info($"Stopped playing this PC's audio on the phone ({reason})");
        Refresh();
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
        StopPcAudio("closing");
        _bluetooth.Dispose();
        StopBattery();
        _pcMedia?.Dispose();
        _phoneMedia?.Dispose();
        _ring?.Close();
        _toasts.Dispose();
    }
}
