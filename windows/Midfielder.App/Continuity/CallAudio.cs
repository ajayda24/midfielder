using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;

namespace Midfielder.App.Continuity;

/// <summary>
/// Experimental "talk through this PC": registers the phone as this PC's Bluetooth hands-free line (what Windows
/// itself uses for phone calls), so an answered call's audio uses the PC's microphone and speakers. Needs the phone
/// paired over Bluetooth and the phoneLineTransportManagement capability in the sparse package; any refusal is
/// reported and the option turns itself off.
/// </summary>
internal static class CallAudio
{
    /// <summary>Connects the phone as the hands-free line; null on success, else why not.</summary>
    public static async Task<string?> ConnectAsync(string? phoneName)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth));
            if (devices.Count == 0) return "Pair your phone with this PC over Bluetooth first.";
            var info = devices.FirstOrDefault(d => phoneName is not null && (d.Name.Contains(phoneName, StringComparison.OrdinalIgnoreCase)
                || phoneName.Contains(d.Name, StringComparison.OrdinalIgnoreCase))) ?? (devices.Count == 1 ? devices[0] : null);
            if (info is null) return "Couldn't tell which Bluetooth device is your phone.";
            var line = PhoneLineTransportDevice.FromId(info.Id);
            if (line is null) return "Not available on this PC.";
            var access = await line.RequestAccessAsync();
            if (access != DeviceAccessStatus.Allowed) return $"Windows didn't allow it ({access}).";
            if (!line.IsRegistered()) line.RegisterApp();
            if (!await line.ConnectAsync()) return $"{info.Name} didn't connect. Is Bluetooth on, and the phone nearby?";
            AppLog.Info($"Calls: {info.Name} connected as this PC's hands-free line");
            return null;
        }
        catch (Exception e)
        {
            AppLog.Error("Talk through this PC isn't available", e);
            return "Not available on this PC.";
        }
    }
}
