using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace TabletMonitor;

/// <summary>Um monitor do Windows como o Desktop Duplication (ddagrab) enxerga: adaptador + índice de saída.</summary>
public sealed record MonitorInfo(
    int AdapterIndex,
    int OutputIndex,
    string AdapterName,
    string DeviceName,
    string FriendlyName,
    Rectangle Bounds,
    bool IsPrimary)
{
    public bool LooksVirtual =>
        FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        FriendlyName.Contains("VDD", StringComparison.OrdinalIgnoreCase) ||
        FriendlyName.Contains("Mtt", StringComparison.OrdinalIgnoreCase) ||
        FriendlyName.Contains("IddSample", StringComparison.OrdinalIgnoreCase) ||
        FriendlyName.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
        FriendlyName.Contains("Sunshine", StringComparison.OrdinalIgnoreCase);

    public override string ToString()
    {
        string display = DeviceName.Replace(@"\\.\", "");
        string tag = IsPrimary ? " — principal" : LooksVirtual ? " — virtual" : "";
        return $"{display}: {FriendlyName} {Bounds.Width}x{Bounds.Height}{tag}";
    }
}

public static class Monitors
{
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            using (adapter)
            {
                string adapterName = adapter!.Description1.Description.Trim();
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                {
                    using (output)
                    {
                        var d = output!.Description;
                        if (!d.AttachedToDesktop) continue;
                        var r = d.DesktopCoordinates;
                        var bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                        string dev = d.DeviceName.TrimEnd('\0');
                        list.Add(new MonitorInfo((int)a, (int)o, adapterName, dev, FriendlyName(dev),
                            bounds, bounds.X == 0 && bounds.Y == 0));
                    }
                }
            }
        }
        return list;
    }

    /// <summary>Escolhe o monitor salvo; senão um virtual; senão o último não principal.</summary>
    public static MonitorInfo? Pick(List<MonitorInfo> all, string savedDeviceName)
    {
        return all.FirstOrDefault(m => m.DeviceName == savedDeviceName)
            ?? all.FirstOrDefault(m => m.LooksVirtual && !m.IsPrimary)
            ?? all.LastOrDefault(m => !m.IsPrimary)
            ?? all.FirstOrDefault();
    }

    // ---- nome amigável via EnumDisplayDevices

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    private static string FriendlyName(string deviceName)
    {
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(deviceName, 0, ref dd, 0) && !string.IsNullOrWhiteSpace(dd.DeviceString))
            {
                string s = dd.DeviceString.Trim();
                if (s.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("Monitor PnP genérico", StringComparison.OrdinalIgnoreCase))
                {
                    // Tenta o nome do adaptador de vídeo correspondente (útil para drivers virtuais).
                    var ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    for (uint i = 0; EnumDisplayDevices(null, i, ref ad, 0); i++)
                    {
                        if (ad.DeviceName == deviceName && ad.DeviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                            return ad.DeviceString.Trim();
                        ad.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
                    }
                }
                return s;
            }
        }
        catch { }
        return "Monitor";
    }
}
