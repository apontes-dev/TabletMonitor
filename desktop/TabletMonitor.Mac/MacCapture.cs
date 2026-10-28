namespace TabletMonitor.Mac;

/// <summary>
/// Captura no macOS: ffmpeg com o dispositivo avfoundation ("Capture screen N", mesma ordem do
/// CGGetActiveDisplayList) e encoder de hardware VideoToolbox. Precisa da permissão "Gravação de Tela".
/// </summary>
public static class MacCapture
{
    public static readonly string[] EncoderPriority = ["h264_videotoolbox", "libx264"];

    public static bool HasAvfoundation(string ffmpeg) =>
        FfmpegTool.HasFilterOrDevice(ffmpeg, "demuxer=avfoundation", "avfoundation");

    public static List<string> Build(MacDisplay d, string encoder, int outW, int outH, int fps, int kbps, bool drawMouse, int gopSeconds, bool jpeg)
    {
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin",
            "-f", "avfoundation",
            "-capture_cursor", drawMouse ? "1" : "0",
            "-capture_mouse_clicks", "0",
            "-framerate", fps.ToString(),
            "-pixel_format", "nv12",
            "-probesize", "32", "-fflags", "nobuffer",
            "-i", $"Capture screen {d.Index}:none",
            // Telas Retina chegam em pixels físicos (2x): sempre escala para o tamanho pedido.
            "-vf", jpeg
                ? $"scale={outW}:{outH}:flags=bilinear:out_range=full,format=yuv420p"
                : $"scale={outW}:{outH}:flags=bilinear,format={VideoArgs.PixelFormat(encoder)}",
        };
        a.AddRange(jpeg ? VideoArgs.JpegTail() : VideoArgs.H264Tail(encoder, fps, kbps, gopSeconds));
        return a;
    }
}
