using Midfielder.Core.Net;

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
}
