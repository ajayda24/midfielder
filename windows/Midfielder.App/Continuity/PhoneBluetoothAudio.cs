using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace Midfielder.App.Continuity;

/// <summary>A Bluetooth device that can play its audio through this PC.</summary>
internal sealed record BluetoothAudioSource(string Id, string Name);

/// <summary>
/// The phone's audio through this PC's speakers: Windows' AudioPlaybackConnection makes the PC a Bluetooth speaker
/// (A2DP sink) for a phone paired with it over Bluetooth. Every app's sound comes along, nothing to allow on the
/// phone. Watches for devices only while the feature is on; connects only when asked.
/// </summary>
internal sealed class PhoneBluetoothAudio(SynchronizationContext ui) : IDisposable
{
    private readonly Dictionary<string, BluetoothAudioSource> _devices = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;
    private AudioPlaybackConnection? _connection;

    /// <summary>Devices or connection state changed. Raised on the UI thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<BluetoothAudioSource> Devices => _devices.Values.OrderBy(d => d.Name, StringComparer.CurrentCulture).ToList();

    public BluetoothAudioSource? Connected { get; private set; }

    public bool IsConnecting { get; private set; }

    public bool IsWatching => _watcher is not null;

    public void StartWatching()
    {
        if (_watcher is not null) return;
        try
        {
            _watcher = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector());
            _watcher.Added += (_, d) => Post(() => _devices[d.Id] = new BluetoothAudioSource(d.Id, d.Name));
            _watcher.Removed += (_, d) => Post(() =>
            {
                _devices.Remove(d.Id);
                if (Connected?.Id == d.Id) Disconnect();
            });
            _watcher.Start();
        }
        catch (Exception e)
        {
            AppLog.Error("Can't look for Bluetooth audio devices", e);
            _watcher = null;
        }
    }

    public void StopWatching()
    {
        Disconnect();
        if (_watcher is null) return;
        try
        {
            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
        }
        catch (InvalidOperationException)
        {
        }
        _watcher = null;
        _devices.Clear();
        Changed?.Invoke();
    }

    /// <summary>The device to use: the one picked in Settings, else the one named like the paired phone.</summary>
    public BluetoothAudioSource? Pick(string? preferredId, string? phoneName)
    {
        if (preferredId is not null && _devices.TryGetValue(preferredId, out var chosen)) return chosen;
        if (phoneName is null) return _devices.Count == 1 ? _devices.Values.First() : null;
        return _devices.Values.FirstOrDefault(d => string.Equals(d.Name, phoneName, StringComparison.OrdinalIgnoreCase))
            ?? _devices.Values.FirstOrDefault(d => d.Name.Contains(phoneName, StringComparison.OrdinalIgnoreCase)
                || phoneName.Contains(d.Name, StringComparison.OrdinalIgnoreCase))
            ?? (_devices.Count == 1 ? _devices.Values.First() : null);
    }

    /// <summary>Opens the connection; returns null on success, else a message for the user.</summary>
    public async Task<string?> ConnectAsync(BluetoothAudioSource device)
    {
        Disconnect();
        IsConnecting = true;
        Changed?.Invoke();
        try
        {
            var connection = AudioPlaybackConnection.TryCreateFromId(device.Id);
            if (connection is null) return $"{device.Name} can't play through this PC";
            connection.StateChanged += (c, _) => Post(() =>
            {
                if (c.State == AudioPlaybackConnectionState.Closed && ReferenceEquals(c, _connection)) Disconnect();
            });
            await connection.StartAsync();
            var result = await connection.OpenAsync();
            if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
            {
                connection.Dispose();
                return result.Status switch
                {
                    AudioPlaybackConnectionOpenResultStatus.RequestTimedOut => $"{device.Name} didn't answer. Is Bluetooth on, and the phone nearby?",
                    AudioPlaybackConnectionOpenResultStatus.DeniedBySystem => "Windows didn't allow it. Check Bluetooth settings.",
                    _ => $"Couldn't connect to {device.Name} ({result.Status})",
                };
            }
            _connection = connection;
            Connected = device;
            AppLog.Info($"Playing {device.Name}'s audio on this PC (Bluetooth)");
            return null;
        }
        catch (Exception e)
        {
            AppLog.Error("Bluetooth audio connection failed", e);
            return "Couldn't connect: " + e.Message;
        }
        finally
        {
            IsConnecting = false;
            Changed?.Invoke();
        }
    }

    public void Disconnect()
    {
        var c = _connection;
        _connection = null;
        bool was = Connected is not null;
        Connected = null;
        c?.Dispose();
        if (was) AppLog.Info("Stopped playing the phone's audio on this PC");
        Changed?.Invoke();
    }

    private void Post(Action action) => ui.Post(_ =>
    {
        action();
        Changed?.Invoke();
    }, null);

    public void Dispose() => StopWatching();
}
