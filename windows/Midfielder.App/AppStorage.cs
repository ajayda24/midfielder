using System.Security.Cryptography;
using System.Text.Json;
using Midfielder.Core.Crypto;
using Midfielder.Core.Net;
using Midfielder.Protocol;
using System.Drawing;
using System.Windows.Forms;

namespace Midfielder.App;

/// <summary>
/// Everything Midfielder persists lives in %LOCALAPPDATA%\Midfielder, encrypted with DPAPI (CurrentUser):
/// another Windows account or a copied disk can't read the identity key or the paired-device list.
/// </summary>
internal sealed class AppStorage
{
    private static readonly byte[] Entropy = "midfielder-v1"u8.ToArray();

    public AppStorage()
    {
        Root = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Midfielder")).FullName;
    }

    public string Root { get; }

    public DeviceIdentity LoadOrCreateIdentity()
    {
        string path = Path.Combine(Root, "identity.bin");
        if (File.Exists(path))
        {
            try
            {
                return DeviceIdentity.FromPkcs12(Unprotect(File.ReadAllBytes(path)), null);
            }
            catch (CryptographicException)
            {
                // Corrupt or from another user: start over (the phone will need to pair again).
            }
        }
        var identity = DeviceIdentity.Generate();
        WriteAtomic(path, Protect(identity.ToPkcs12(null)));
        return identity;
    }

    public Settings LoadSettings() => ReadJson<Settings>("settings.json") ?? new Settings();

    public void SaveSettings(Settings settings) => WriteJson("settings.json", settings);

    internal List<StoredDevice> LoadDevices() => ReadJson<List<StoredDevice>>("devices.bin") ?? [];

    internal void SaveDevices(IEnumerable<StoredDevice> devices) => WriteJson("devices.bin", devices.ToList());

    private T? ReadJson<T>(string name)
    {
        string path = Path.Combine(Root, name);
        if (!File.Exists(path)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(Unprotect(File.ReadAllBytes(path)));
        }
        catch (Exception e) when (e is JsonException or CryptographicException)
        {
            return default;
        }
    }

    private void WriteJson<T>(string name, T value) => WriteAtomic(Path.Combine(Root, name), Protect(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    private static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}

internal sealed class Settings
{
    public bool SyncClipboard { get; set; } = true;
    public bool AutoAcceptFiles { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ContextMenu { get; set; } = true;
    public string ReceiveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Midfielder");
    public int Port { get; set; } = 47810;
    /// <summary>Main window x, y, width, height, maximized (0/1); null until it has been closed once.</summary>
#pragma warning disable CA1819 // a plain settings DTO for JSON
    public int[]? Window { get; set; }
#pragma warning restore CA1819
}

internal sealed record StoredDevice(string Fingerprint, string Name, int Kind, string? LastAddress);

/// <summary>Trust store that writes through to DPAPI-protected storage.</summary>
internal sealed class PersistentTrustStore : InMemoryTrustStore
{
    private readonly AppStorage _storage;
    private readonly object _saveLock = new();

    public PersistentTrustStore(AppStorage storage)
    {
        _storage = storage;
        foreach (var d in storage.LoadDevices())
        {
            try
            {
                base.Put(new PairedDevice(new Fingerprint(d.Fingerprint), d.Name, (DeviceKind)d.Kind, d.LastAddress));
            }
            catch (ArgumentException)
            {
                // Skip a malformed entry rather than refusing to start.
            }
        }
    }

    public override void Put(PairedDevice device)
    {
        base.Put(device);
        Save();
    }

    public override void Remove(Fingerprint fingerprint)
    {
        base.Remove(fingerprint);
        Save();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            _storage.SaveDevices(All().Select(d => new StoredDevice(d.Fingerprint.Value, d.Name, (int)d.Kind, d.LastAddress)));
        }
    }
}
