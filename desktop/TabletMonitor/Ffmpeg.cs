using TabletMonitor.Core;

namespace TabletMonitor;

/// <summary>Captura no Windows: Desktop Duplication (filtro ddagrab do ffmpeg 6+).</summary>
public static class Ffmpeg
{
    public static readonly string[] EncoderPriority = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

    public static string LocalPath => FfmpegTool.LocalPath;
    public static string? Find() => FfmpegTool.Find();
    public static Task<string> DownloadAsync(Action<string> log, CancellationToken ct = default) => FfmpegTool.DownloadAsync(log, ct);
    public static bool HasDdagrab(string ffmpeg) => FfmpegTool.HasFilterOrDevice(ffmpeg, "filter=ddagrab", "ddagrab");
    public static List<string> ProbeEncoders(string ffmpeg) => FfmpegTool.ProbeEncoders(ffmpeg, EncoderPriority);
    public static (int W, int H) Cap(int w, int h, int maxW, int maxH) => VideoArgs.Cap(w, h, maxW, maxH);
    public static (int Code, string Output) Run(string exe, IEnumerable<string> args, int timeoutMs) => Proc.Run(exe, args, timeoutMs);

    private static List<string> Capture(MonitorInfo mon, int fps, bool drawMouse, string tailFilter)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin" };
        if (mon.AdapterIndex != 0)
            a.AddRange(["-init_hw_device", $"d3d11va=dda:{mon.AdapterIndex}", "-filter_hw_device", "dda"]);
        a.AddRange(["-filter_complex",
            $"ddagrab=output_idx={mon.OutputIndex}:framerate={fps}:draw_mouse={(drawMouse ? 1 : 0)},hwdownload,format=bgra," + tailFilter]);
        return a;
    }

    public static List<string> BuildArgs(MonitorInfo mon, string encoder, int outW, int outH, int fps, int kbps, bool drawMouse, int gopSeconds = 2)
    {
        var a = Capture(mon, fps, drawMouse, VideoArgs.ScaleAndFormat(mon.Bounds.Width, mon.Bounds.Height, outW, outH, false, encoder));
        a.AddRange(VideoArgs.H264Tail(encoder, fps, kbps, gopSeconds));
        return a;
    }

    /// <summary>Saída JPEG para o cliente web sem WebCodecs.</summary>
    public static List<string> BuildJpegArgs(MonitorInfo mon, int outW, int outH, int fps, bool drawMouse)
    {
        var a = Capture(mon, fps, drawMouse, VideoArgs.ScaleAndFormat(mon.Bounds.Width, mon.Bounds.Height, outW, outH, true, "mjpeg"));
        a.AddRange(VideoArgs.JpegTail());
        return a;
    }
}
