using Midfielder.App.Continuity;
using Midfielder.Core.Net;
using Midfielder.Protocol;

namespace Midfielder.App.Ui;

/// <summary>What the main window needs from the app. Events may fire on any thread; pages marshal to the UI.</summary>
internal interface IAppHost
{
    HistoryStore History { get; }
    TransferTracker Transfers { get; }
    Settings Settings { get; }
    IReadOnlyList<PairedDevice> PairedDevices { get; }
    IReadOnlyList<PairedDevice> ConnectedDevices { get; }
    string DeviceId { get; }
    string Fingerprint { get; }
    int Port { get; }
    string StatusText { get; }

    /// <summary>Connection or pairing state changed.</summary>
    event Action? StatusChanged;

    string? AddressOf(PairedDevice device);
    void PickAndSendFiles();
    void SendFiles(IReadOnlyList<string> paths);
    void SendClipboardNow();
    void CancelTransfer(string id);
    void ShowPairing();
    void ForgetDevices();
    void SetSyncClipboard(bool on);
    void SetAutoAccept(bool on);
    void SetStartWithWindows(bool on);
    void SetContextMenu(bool on);
    void SetReceiveFolder(string path);

    /// <summary>Persists <see cref="Settings"/> (e.g. after the window saved its size).</summary>
    void SaveSettings();

    // ---- Optional features (each switched on or off in Settings → Features) ----

    /// <summary>The connected phone's battery, music, notifications and capabilities.</summary>
    PhoneState Phone { get; }

    /// <summary><see cref="Phone"/> changed. Raised on the UI thread.</summary>
    event Action? PhoneChanged;

    /// <summary>Saves <see cref="Settings.Features"/> and starts or stops features to match.</summary>
    void ApplyFeatures();

    /// <summary>Find my phone. False (and a hint for the user) if the phone can't ring right now.</summary>
    bool RingPhone();

    void SendMediaCommand(MediaAction action);
    void ReplyToNotification(string key, string text);
    void DismissNotification(string key);

    /// <summary>The Windows 11 top-level "Send to phone" entry (sparse package).</summary>
    ModernMenuStatus ModernMenu { get; }

    /// <summary>Turns the Windows 11 entry on (one UAC prompt the first time) or off. Returns a message for the user.</summary>
    Task<string> SetModernMenuAsync(bool on);
}

internal enum ModernMenuStatus
{
    /// <summary>Not available here: Windows 10, or the package files aren't next to the exe.</summary>
    Unavailable,
    Off,
    On,
    /// <summary>Registered for another folder: the exe was moved.</summary>
    NeedsRepair,
}
