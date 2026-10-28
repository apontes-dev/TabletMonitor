// Harness de teste do núcleo (roda em Linux/macOS/Windows): usa uma fonte de teste do ffmpeg
// no lugar da captura de tela, para validar protocolo + FLV/MJPEG + fila de descarte + servidor web.
//   dotnet CoreTest.dll [porta] [kbps] [filaQuadros] [pastaCertificados]
using TabletMonitor;
using TabletMonitor.Core;

if (args.Length > 0 && args[0] == "gestos") { GestureTest.Run(); return; }

int port = args.Length > 0 ? int.Parse(args[0]) : Protocol.DefaultPort;
int kbps = args.Length > 1 ? int.Parse(args[1]) : 4000;
int queue = args.Length > 2 ? int.Parse(args[2]) : 3;
string certDir = args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "tabletmonitor-test-certs");

using var server = new StreamServer();
server.Log += s => Console.WriteLine("[log] " + s);
server.OnTouch = t => Console.WriteLine($"[touch] {t.Action} id={t.PointerId} x={t.X:0.000} y={t.Y:0.000}");
server.PlanSession = (hello, transport) =>
{
    int maxW = hello.MaxDecodeWidth > 0 ? hello.MaxDecodeWidth : hello.Width;
    int maxH = hello.MaxDecodeHeight > 0 ? hello.MaxDecodeHeight : hello.Height;
    int w = Math.Min(maxW, 1280) & ~1, h = Math.Min(maxH, 800) & ~1;
    bool jpeg = !hello.Codecs.HasFlag(CodecSupport.H264);
    Console.WriteLine($"[plan] transporte={transport} max={maxW}x{maxH} codecs={hello.Codecs} → {(jpeg ? "JPEG" : "H.264")} {w}x{h}");
    var common = new[] { "-hide_banner", "-loglevel", "warning", "-nostdin", "-re", "-f", "lavfi", "-i", $"testsrc2=size={w}x{h}:rate=30" };
    var tail = jpeg
        ? new[] { "-vf", "scale=out_range=full,format=yuv420p", "-color_range", "pc", "-c:v", "mjpeg", "-q:v", "7",
                  "-an", "-f", "mjpeg", "-flush_packets", "1", "-" }
        : new[] { "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline",
                  "-b:v", $"{kbps}k", "-g", "30", "-bf", "0", "-pix_fmt", "yuv420p", "-an",
                  "-f", "flv", "-flvflags", "no_duration_filesize", "-flush_packets", "1", "-" };
    return new SessionPlan("ffmpeg", common.Concat(tail).ToArray(), w, h, 30, queue, 250, jpeg ? Codec.Jpeg : Codec.H264);
};
server.Start(port);
using var beacon = new DiscoveryBeacon(port);
var certs = CertManager.LoadOrCreate(certDir);
using var web = new WebServer(server, certs, s => Console.WriteLine("[web] " + s));
web.Start(WebServer.DefaultHttpPort, WebServer.DefaultHttpsPort);
Console.WriteLine("pronto");
Thread.Sleep(Timeout.Infinite);
