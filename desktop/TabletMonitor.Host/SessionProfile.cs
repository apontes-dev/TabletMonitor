using TabletMonitor.Core;

namespace TabletMonitor;

/// <summary>Parâmetros de uma sessão conforme o tablet, o meio de conexão e as configurações.</summary>
public sealed record SessionProfile(int MaxW, int MaxH, int Fps, int Kbps, int QueueFrames, int LatencyMs, int GopSeconds, bool Jpeg)
{
    public static SessionProfile For(HelloInfo hello, Transport transport, AppSettings s)
    {
        // Limites: tela do tablet, o que o decodificador dele aguenta e o escolhido nas configurações.
        int maxW = hello.Width, maxH = hello.Height;
        if (hello.MaxDecodeWidth > 0) { maxW = Math.Min(maxW, hello.MaxDecodeWidth); maxH = Math.Min(maxH, hello.MaxDecodeHeight); }
        if (s.MaxWidth > 0) maxW = Math.Min(maxW, s.MaxWidth);

        int fps = s.Fps, kbps = s.BitrateKbps, queue = 3, latency = 250, gop = 2;
        bool jpeg = false;
        switch (transport)
        {
            case Transport.Usb:
                queue = 4; latency = 300;
                break;
            case Transport.Bluetooth:
                // ~1–2 Mbps reais: menos quadros, menos bits, imagem menor.
                fps = s.BluetoothFps; kbps = s.BluetoothBitrateKbps;
                maxW = Math.Min(maxW, 1280);
                queue = 2; latency = 400;
                break;
            case Transport.Web when !hello.Codecs.HasFlag(CodecSupport.H264):
                // Navegador sem WebCodecs: JPEG, limitado pela banda.
                jpeg = true;
                maxW = Math.Min(maxW, 1280);
                fps = Math.Min(fps, 24);
                queue = 2; latency = 200;
                break;
        }
        return new SessionProfile(maxW, maxH, fps, kbps, queue, latency, gop, jpeg);
    }

    /// <summary>Resolução ideal para a tela transmitida: proporção do tablet, no tamanho que ele decodifica.</summary>
    public static (int W, int H) Ideal(HelloInfo hello) =>
        VideoArgs.Cap(hello.Width, hello.Height,
            hello.MaxDecodeWidth > 0 ? hello.MaxDecodeWidth : 0, hello.MaxDecodeHeight > 0 ? hello.MaxDecodeHeight : 0);
}
