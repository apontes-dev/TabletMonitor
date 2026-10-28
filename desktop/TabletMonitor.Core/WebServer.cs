using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TabletMonitor.Core;

/// <summary>
/// Servidor web do cliente para iPhone/iPad (ou qualquer navegador):
///   http://PC:27185/      página instaladora (certificado, Tela de Início, dicas)
///   http://PC:27185/app   cliente em modo compatível (JPEG — funciona sem certificado)
///   https://PC:27186/app  cliente H.264 via WebCodecs (precisa do certificado instalado e confiável)
///   /ws                   WebSocket com o mesmo protocolo TMON do app Android
/// Sem HttpListener de propósito: no Windows ele exige administrador para ouvir fora do localhost.
/// </summary>
public sealed class WebServer : IDisposable
{
    public const int DefaultHttpPort = 27185;
    public const int DefaultHttpsPort = 27186;

    private readonly StreamServer _server;
    private readonly CertManager? _certs;
    private readonly Action<string> _log;
    private readonly List<TcpListener> _listeners = new();
    private volatile bool _running;
    private SslStreamCertificateContext? _certContext;

    public int HttpPort { get; private set; }
    public int HttpsPort { get; private set; }

    public WebServer(StreamServer server, CertManager? certs, Action<string> log)
    {
        _server = server;
        _certs = certs;
        _log = log;
    }

    public void Start(int httpPort = DefaultHttpPort, int httpsPort = DefaultHttpsPort)
    {
        Stop();
        _running = true;
        HttpPort = httpPort;
        HttpsPort = httpsPort;
        Listen(httpPort, tls: false);
        if (_certs != null)
        {
            _certContext = SslStreamCertificateContext.Create(_certs.Leaf, new X509Certificate2Collection(_certs.Ca), offline: true);
            Listen(httpsPort, tls: true);
        }
        _log($"Cliente web: http://<IP do PC>:{httpPort}/ (instalador) e https://<IP do PC>:{httpsPort}/app");
    }

    private void Listen(int port, bool tls)
    {
        var l = new TcpListener(IPAddress.Any, port);
        l.Start();
        _listeners.Add(l);
        new Thread(() => AcceptLoop(l, tls)) { IsBackground = true, Name = tls ? "https" : "http" }.Start();
    }

    public void Stop()
    {
        _running = false;
        foreach (var l in _listeners) { try { l.Stop(); } catch { } }
        _listeners.Clear();
    }

    public void Dispose() => Stop();

    private void AcceptLoop(TcpListener l, bool tls)
    {
        while (_running)
        {
            TcpClient c;
            try { c = l.AcceptTcpClient(); }
            catch { if (_running) Thread.Sleep(100); continue; }
            new Thread(() => Handle(c, tls)) { IsBackground = true, Name = "web-conn" }.Start();
        }
    }

    private void Handle(TcpClient client, bool tls)
    {
        Stream stream = client.GetStream();
        bool handedOff = false;
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 15000;
            if (tls)
            {
                var ssl = new SslStream(stream, false);
                ssl.AuthenticateAsServer(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = _certContext,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                });
                stream = ssl;
            }

            var (method, path, headers) = ReadRequest(stream);
            if (method == null) return;
            string route = path.Split('?')[0];

            if (route == "/ws" && headers.TryGetValue("upgrade", out var up) && up.Equals("websocket", StringComparison.OrdinalIgnoreCase))
            {
                if (!headers.TryGetValue("sec-websocket-key", out var key)) { Respond(stream, 400, "text/plain", "sem chave"u8.ToArray()); return; }
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                stream.Write(Encoding.ASCII.GetBytes(resp));
                stream.Flush();
                client.ReceiveTimeout = 0;
                client.SendBufferSize = 64 * 1024;
                var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true,
                    KeepAliveInterval = TimeSpan.FromSeconds(15),
                });
                var endpoint = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                var s = stream;
                _server.AcceptExternal(new WebSocketStream(ws, () => { try { s.Dispose(); } catch { } client.Close(); }),
                    $"navegador {endpoint}", Transport.Web, client.Close);
                handedOff = true;
                return;
            }

            if (method != "GET" && method != "HEAD") { Respond(stream, 405, "text/plain", "método não suportado"u8.ToArray()); return; }
            Serve(stream, route, tls);
        }
        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
        {
            // cliente desistiu / certificado ainda não confiável no iPad — normal
        }
        catch (Exception ex)
        {
            _log("web: " + ex.Message);
        }
        finally
        {
            if (!handedOff)
            {
                try { stream.Dispose(); } catch { }
                client.Close();
            }
        }
    }

    private void Serve(Stream stream, string route, bool tls)
    {
        switch (route)
        {
            case "/":
            case "/index.html":
                RespondResource(stream, "index.html");
                return;
            case "/app":
            case "/app/":
                RespondResource(stream, "app.html");
                return;
            case "/ca.mobileconfig" when _certs != null:
                Respond(stream, 200, "application/x-apple-aspen-config", _certs.MobileConfig(),
                    "Content-Disposition: attachment; filename=\"TabletMonitor.mobileconfig\"\r\n");
                return;
            case "/ca.cer" when _certs != null:
                Respond(stream, 200, "application/x-x509-ca-cert", _certs.CaDer,
                    "Content-Disposition: attachment; filename=\"TabletMonitor-CA.cer\"\r\n");
                return;
            case "/info.json":
                var json = $"{{\"http\":{HttpPort},\"https\":{(_certs != null ? HttpsPort : 0)},\"secure\":{(tls ? "true" : "false")},\"pc\":\"{Environment.MachineName}\"}}";
                Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(json),
                    "Access-Control-Allow-Origin: *\r\n");
                return;
            case "/ping":
                Respond(stream, 200, "text/plain", "ok"u8.ToArray(), "Access-Control-Allow-Origin: *\r\n");
                return;
        }
        string name = route.TrimStart('/');
        if (name.Contains('/') || name.Contains("..") || !RespondResource(stream, name))
            Respond(stream, 404, "text/plain; charset=utf-8", "não encontrado"u8.ToArray());
    }

    private static bool RespondResource(Stream stream, string name)
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name);
        if (res == null) return false;
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        Respond(stream, 200, ContentType(name), ms.ToArray());
        return true;
    }

    private static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".webmanifest" => "application/manifest+json",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    private static void Respond(Stream s, int code, string type, byte[] body, string extraHeaders = "")
    {
        string status = code switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = $"HTTP/1.1 {code} {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                   "Cache-Control: no-cache\r\nConnection: close\r\n" + extraHeaders + "\r\n";
        s.Write(Encoding.ASCII.GetBytes(head));
        s.Write(body);
        s.Flush();
    }

    /// <summary>Lê a linha de requisição e os cabeçalhos (até 16 KB), sem consumir além do "\r\n\r\n".</summary>
    private static (string? Method, string Path, Dictionary<string, string> Headers) ReadRequest(Stream s)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (sb.Length >= 16384 || s.Read(one, 0, 1) <= 0) return (null, "", headers);
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') break;
        }
        var lines = sb.ToString().Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return (null, "", headers);
        foreach (var line in lines.Skip(1))
        {
            int i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return (first[0].ToUpperInvariant(), first[1], headers);
    }
}
