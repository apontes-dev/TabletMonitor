using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace TabletMonitor.Core;

/// <summary>
/// Anuncia o PC na rede local a cada segundo ("TMON1|nome|porta") para o tablet achar sozinho via Wi-Fi.
/// Envia para o broadcast de cada interface, porque 255.255.255.255 só sai por uma delas no Windows.
/// </summary>
public sealed class DiscoveryBeacon : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Timer _timer;
    private readonly byte[] _message;

    public DiscoveryBeacon(int tcpPort)
    {
        _message = Encoding.UTF8.GetBytes($"TMON1|{Environment.MachineName}|{tcpPort}");
        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        _timer = new Timer(_ => Send(), null, 0, 1000);
    }

    private void Send()
    {
        foreach (var target in BroadcastTargets())
        {
            try { _udp.Send(_message, _message.Length, new IPEndPoint(target, Protocol.DiscoveryPort)); } catch { }
        }
    }

    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var set = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0)) continue;
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    set.Add(new IPAddress(b));
                }
            }
        }
        catch { }
        return set;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _udp.Dispose();
    }
}
