using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TabletMonitor.Core;

/// <summary>
/// Autoridade certificadora local do PC. O Safari só libera o WebCodecs (decodificação H.264) em
/// páginas seguras (HTTPS), então o PC gera:
///   • uma CA própria (guardada e estável — o iPhone/iPad instala UMA vez, como perfil);
///   • um certificado de servidor novo a cada início, com os IPs atuais do PC (o IP pode mudar por DHCP).
/// Regras da Apple respeitadas: RSA 2048, SHA-256, SAN com IP/DNS, EKU serverAuth, validade ≤ 397 dias.
/// </summary>
public sealed class CertManager
{
    public X509Certificate2 Ca { get; }
    public X509Certificate2 Leaf { get; }

    private CertManager(X509Certificate2 ca, X509Certificate2 leaf)
    {
        Ca = ca;
        Leaf = leaf;
    }

    public static CertManager LoadOrCreate(string dir)
    {
        Directory.CreateDirectory(dir);
        string caPath = Path.Combine(dir, "tabletmonitor-ca.pfx");
        X509Certificate2? ca = null;
        if (File.Exists(caPath))
        {
            try
            {
                ca = LoadPfx(File.ReadAllBytes(caPath));
                if (!ca.HasPrivateKey || ca.NotAfter < DateTime.Now.AddDays(400)) ca = null;
            }
            catch { ca = null; }
        }
        if (ca == null)
        {
            ca = CreateCa();
            File.WriteAllBytes(caPath, ca.Export(X509ContentType.Pkcs12));
        }
        return new CertManager(ca, CreateLeaf(ca));
    }

    /// <summary>PFX ida e volta: no Windows o SslStream não aceita chave efêmera.</summary>
    private static X509Certificate2 LoadPfx(byte[] pfx) =>
        new(pfx, (string?)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

    private static X509Certificate2 CreateCa()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=TabletMonitor CA ({Environment.MachineName}), O=TabletMonitor",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        return LoadPfx(cert.Export(X509ContentType.Pkcs12));
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 ca)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=TabletMonitor, O=TabletMonitor", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        string host = Environment.MachineName.ToLowerInvariant();
        san.AddDnsName(host);
        san.AddDnsName(host + ".local");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in LocalAddresses()) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var signed = req.Create(ca, now.AddDays(-1), now.AddDays(396), serial);
        using var withKey = signed.CopyWithPrivateKey(rsa);
        return LoadPfx(withKey.Export(X509ContentType.Pkcs12));
    }

    /// <summary>IPv4 das interfaces ativas (o que o iPad usa para chegar ao PC).</summary>
    public static List<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        list.Add(ua.Address);
            }
        }
        catch { }
        return list;
    }

    /// <summary>Endereço mais provável da rede local (prefere 192.168.x, 10.x, 172.16–31.x com gateway).</summary>
    public static IPAddress? BestLanAddress()
    {
        IPAddress? fallback = null;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    var b = ua.Address.GetAddressBytes();
                    bool priv = b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
                    if (priv && hasGateway) return ua.Address;
                    fallback ??= ua.Address;
                }
            }
        }
        catch { }
        return fallback;
    }

    public byte[] CaDer => Ca.Export(X509ContentType.Cert);

    /// <summary>Perfil de configuração do iOS/iPadOS com a CA (instala em Ajustes > Perfil baixado).</summary>
    public byte[] MobileConfig()
    {
        string b64 = Convert.ToBase64String(CaDer);
        string thumb = Ca.Thumbprint;
        string uuid1 = GuidFrom("payload" + thumb), uuid2 = GuidFrom("profile" + thumb);
        string xml = $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>PayloadContent</key>
  <array>
    <dict>
      <key>PayloadCertificateFileName</key><string>TabletMonitor-CA.cer</string>
      <key>PayloadContent</key><data>{b64}</data>
      <key>PayloadDescription</key><string>Certificado do PC para o TabletMonitor (conexão segura na rede local).</string>
      <key>PayloadDisplayName</key><string>TabletMonitor CA ({Escape(Environment.MachineName)})</string>
      <key>PayloadIdentifier</key><string>com.apontes.tabletmonitor.ca.{uuid1}</string>
      <key>PayloadType</key><string>com.apple.security.root</string>
      <key>PayloadUUID</key><string>{uuid1}</string>
      <key>PayloadVersion</key><integer>1</integer>
    </dict>
  </array>
  <key>PayloadDescription</key><string>Permite que o TabletMonitor use vídeo H.264 com baixo atraso. Só vale para este PC.</string>
  <key>PayloadDisplayName</key><string>TabletMonitor — {Escape(Environment.MachineName)}</string>
  <key>PayloadIdentifier</key><string>com.apontes.tabletmonitor.profile.{uuid2}</string>
  <key>PayloadRemovalDisallowed</key><false/>
  <key>PayloadType</key><string>Configuration</string>
  <key>PayloadUUID</key><string>{uuid2}</string>
  <key>PayloadVersion</key><integer>1</integer>
</dict>
</plist>
""";
        return Encoding.UTF8.GetBytes(xml);
    }

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static string GuidFrom(string seed)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(h.AsSpan(0, 16)).ToString().ToUpperInvariant();
    }
}
