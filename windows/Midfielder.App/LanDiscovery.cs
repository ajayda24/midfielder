using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;
using Midfielder.Core.Crypto;
using Midfielder.Core.Discovery;
using System.Drawing;
using System.Windows.Forms;

namespace Midfielder.App;

/// <summary>
/// Makes the PC findable: an mDNS service (_midfielder._tcp) the phone looks up in short bursts, plus a
/// UDP "knock" to the phone's last known address when the PC has something to deliver and no session is up.
/// The mDNS responder only wakes when a query arrives; nothing is sent periodically.
/// </summary>
internal sealed class LanDiscovery : IDisposable
{
    public const string ServiceType = "_midfielder._tcp";
    private ServiceDiscovery? _sd;
    private ServiceProfile? _profile;
    private long _lastKnockMs;

    public void Advertise(string deviceId, string name, int port)
    {
        _sd = new ServiceDiscovery();
        _profile = new ServiceProfile(deviceId, ServiceType, (ushort)port);
        _profile.AddProperty("id", deviceId);
        _profile.AddProperty("n", name.Length > 60 ? name[..60] : name);
        _profile.AddProperty("v", "1");
        _sd.Advertise(_profile);
        _sd.Announce(_profile);
    }

    /// <summary>After resume or a network change, so the phone sees the new address quickly.</summary>
    public void Reannounce()
    {
        if (_sd is not null && _profile is not null) _sd.Announce(_profile);
    }

    /// <summary>Rate limited to one knock per 5 seconds.</summary>
    public void Knock(string host, string myDeviceId, int myPort)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastKnockMs) < 5_000) return;
        Interlocked.Exchange(ref _lastKnockMs, now);
        try
        {
            using var udp = new UdpClient();
            byte[] packet = new Knock(myDeviceId, myPort).Encode();
            udp.Send(packet, packet.Length, host, Midfielder.Core.Discovery.Knock.DefaultPort);
        }
        catch (SocketException)
        {
            // Phone not on this network right now: nothing to do.
        }
    }

    /// <summary>LAN addresses to put in the pairing QR code (Wi-Fi/Ethernet first).</summary>
    public static List<HostPort> LocalAddresses(int port)
    {
        var result = new List<HostPort>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                     .OrderBy(n => n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet ? 0 : 1))
        {
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                    result.Add(new HostPort(addr.Address.ToString(), port));
            }
        }
        if (result.Count == 0) result.Add(new HostPort("127.0.0.1", port));
        return result.Take(PairingInvite.MaxAddresses).ToList();
    }

    public void Dispose()
    {
        if (_sd is not null)
        {
            _sd.Unadvertise();
            _sd.Dispose();
        }
    }
}
