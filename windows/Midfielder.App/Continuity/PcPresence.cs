using Microsoft.Win32;
using Midfielder.Core.Feature;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Midfielder.App.Continuity;

/// <summary>What auto-lock sees right now, for the Settings meter.</summary>
internal sealed record PresenceStatus(bool On, double? Signal, PresenceState State, TimeSpan Remaining, string? Problem)
{
    public static readonly PresenceStatus Off = new(false, null, PresenceState.Waiting, TimeSpan.Zero, null);
}

/// <summary>
/// "Lock when my phone walks away": listens (passive Bluetooth LE scan, filtered in the radio to our beacon) for the
/// phone's rotating token, feeds <see cref="PresenceMonitor"/> and locks the PC when it says so. Runs only while the
/// feature is on. Ticks once a second while armed, every 5 s otherwise.
/// </summary>
internal sealed class PcPresence : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly Func<bool> _blocked;
    private readonly PresenceMonitor _monitor = new();
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly System.Windows.Forms.Timer _timer = new();
    private byte[] _key;
    private bool _disposed;

    public PcPresence(byte[] key, SynchronizationContext ui, Func<bool> blocked)
    {
        _key = key;
        _ui = ui;
        _blocked = blocked;
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        // Manufacturer-specific data starting with our company id (little-endian) and version: matched in the radio.
        var prefix = new DataWriter();
        prefix.WriteBytes([(byte)(PresenceToken.CompanyId & 0xFF), (byte)(PresenceToken.CompanyId >> 8), PresenceToken.Version]);
        _watcher.AdvertisementFilter.BytePatterns.Add(new BluetoothLEAdvertisementBytePattern(0xFF, 0, prefix.DetachBuffer()));
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
        _timer.Tick += (_, _) => Tick();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>The meter changed (about once a second while the phone is heard). Raised on the UI thread.</summary>
    public event Action? Changed;

    public string? Problem { get; private set; }

    public PresenceStatus Status => new(true, _monitor.Signal, _monitor.State, _monitor.Remaining(DateTimeOffset.UtcNow), Problem);

    public void Configure(byte[] key, int nearRssi, TimeSpan delay)
    {
        _key = key;
        _monitor.NearRssi = nearRssi;
        _monitor.Delay = delay;
    }

    public void Start()
    {
        try
        {
            Problem = null;
            _watcher.Start();
            _timer.Interval = 5000;
            _timer.Start();
            AppLog.Info("Auto-lock: listening for the phone's beacon");
        }
        catch (Exception e)
        {
            Problem = "Bluetooth isn't available on this PC";
            AppLog.Error("Auto-lock couldn't start the Bluetooth scan", e);
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var section in args.Advertisement.GetManufacturerDataByCompanyId(PresenceToken.CompanyId))
        {
            var data = new byte[section.Data.Length];
            using (var reader = DataReader.FromBuffer(section.Data)) reader.ReadBytes(data);
            if (!PresenceToken.TryParse(data, out var token) || !PresenceToken.Matches(_key, token, DateTimeOffset.UtcNow)) continue;
            int rssi = args.RawSignalStrengthInDBm;
            _ui.Post(_ =>
            {
                if (_disposed) return;
                _monitor.Sample(rssi, DateTimeOffset.UtcNow);
                Changed?.Invoke();
            }, null);
            return;
        }
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error == Windows.Devices.Bluetooth.BluetoothError.Success) return;
        _ui.Post(_ =>
        {
            Problem = args.Error == Windows.Devices.Bluetooth.BluetoothError.RadioNotAvailable
                ? "Turn on Bluetooth on this PC"
                : $"Bluetooth stopped ({args.Error})";
            Changed?.Invoke();
        }, null);
    }

    private void Tick()
    {
        var before = _monitor.State;
        if (_monitor.Tick(DateTimeOffset.UtcNow, _blocked()))
        {
            AppLog.Info("Auto-lock: the phone walked away, locking this PC");
            if (!NativeMethods.LockWorkStation()) AppLog.Info("LockWorkStation refused");
        }
        _timer.Interval = _monitor.State is PresenceState.Near or PresenceState.Leaving ? 1000 : 5000;
        if (before != _monitor.State || _monitor.State == PresenceState.Leaving) Changed?.Invoke();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        _ui.Post(_ =>
        {
            if (_disposed) return;
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    _monitor.Locked();
                    break;
                case SessionSwitchReason.SessionUnlock:
                    _monitor.Unlocked();
                    break;
            }
            Changed?.Invoke();
        }, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _timer.Dispose();
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        try
        {
            _watcher.Stop();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        AppLog.Info("Auto-lock: off");
    }
}
