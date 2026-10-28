using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace TabletMonitor;

/// <summary>Executa um programa e devolve código de saída + saída (stdout+stderr).</summary>
public static class Proc
{
    public static (int Code, string Output) Run(string exe, IEnumerable<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var x in args) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, "timeout");
            }
            return (p.ExitCode, so.Result + se.Result);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) |
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { }
    }
}

public static class Downloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    static Downloader() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("TabletMonitor/1.0");

    public static async Task DownloadAsync(string url, string dest, Action<string> log, CancellationToken ct)
    {
        log("Baixando " + url);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buf = new byte[1 << 16];
        long done = 0;
        int lastPct = -1;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (total > 0)
            {
                int pct = (int)(done * 100 / total.Value);
                if (pct / 10 != lastPct / 10) { lastPct = pct; log($"  {pct}% ({done / 1048576} MB)"); }
            }
        }
    }
}

/// <summary>Localiza e baixa o ffmpeg conforme o sistema (Windows: .exe; macOS: Homebrew ou build estática).</summary>
public static class FfmpegTool
{
    private static string ExeName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    public static string LocalPath => Path.Combine(AppSettings.ToolsDir, "ffmpeg", ExeName);

    private static string[] DownloadUrls => OperatingSystem.IsWindows()
        ?
        [
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        ]
        : OperatingSystem.IsMacOS()
            ? ["https://evermeet.cx/ffmpeg/getrelease/zip"]
            : [];

    public static string? Find()
    {
        var c = new List<string>
        {
            LocalPath,
            Path.Combine(AppContext.BaseDirectory, ExeName),
            Path.Combine(AppContext.BaseDirectory, "tools", ExeName),
        };
        if (OperatingSystem.IsMacOS())
        {
            // Homebrew (Apple Silicon e Intel) — preferível: nativo e atualizado.
            c.Insert(0, "/opt/homebrew/bin/ffmpeg");
            c.Insert(1, "/usr/local/bin/ffmpeg");
        }
        char sep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries))
            c.Add(Path.Combine(dir.Trim(), ExeName));
        return c.FirstOrDefault(File.Exists);
    }

    public static async Task<string> DownloadAsync(Action<string> log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
        Exception? last = null;
        foreach (var url in DownloadUrls)
        {
            try
            {
                string zip = Path.Combine(AppSettings.ToolsDir, "ffmpeg-download.zip");
                await Downloader.DownloadAsync(url, zip, log, ct);
                log("Extraindo o ffmpeg…");
                using (var archive = ZipFile.OpenRead(zip))
                {
                    var entry = archive.Entries.FirstOrDefault(e =>
                                    e.FullName.EndsWith("bin/" + ExeName, StringComparison.OrdinalIgnoreCase) ||
                                    e.FullName.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidDataException("ffmpeg não encontrado no pacote");
                    entry.ExtractToFile(LocalPath, overwrite: true);
                }
                File.Delete(zip);
                Proc.MakeExecutable(LocalPath);
                log("ffmpeg instalado em " + LocalPath);
                return LocalPath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                log($"Falha ao baixar de {url}: {ex.Message}");
            }
        }
        throw new InvalidOperationException("Não foi possível baixar o ffmpeg. " + last?.Message);
    }

    public static bool HasFilterOrDevice(string ffmpeg, string helpTopic, string name)
    {
        var (code, output) = Proc.Run(ffmpeg, ["-hide_banner", "-h", helpTopic], 8000);
        return code == 0 && output.Contains(name, StringComparison.OrdinalIgnoreCase)
               && !output.Contains("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Testa cada encoder com um vídeo sintético de 5 quadros e devolve os que funcionam nesta máquina.</summary>
    public static List<string> ProbeEncoders(string ffmpeg, IEnumerable<string> candidates)
    {
        var ok = new List<string>();
        foreach (var enc in candidates)
        {
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=30",
                "-frames:v", "5", "-pix_fmt", enc == "libx264" ? "yuv420p" : "nv12", "-c:v", enc, "-f", "null", "-",
            };
            if (Proc.Run(ffmpeg, args, 15000).Code == 0) ok.Add(enc);
        }
        return ok;
    }
}

/// <summary>Partes da linha de comando do ffmpeg que não dependem de como a tela é capturada.</summary>
public static class VideoArgs
{
    /// <summary>Reduz (w,h) mantendo a proporção até caber em maxW x maxH (0 = sem limite). Sempre par.</summary>
    public static (int W, int H) Cap(int w, int h, int maxW, int maxH)
    {
        double s = 1.0;
        if (maxW > 0) s = Math.Min(s, (double)maxW / w);
        if (maxH > 0) s = Math.Min(s, (double)maxH / h);
        return (Math.Max(2, (int)Math.Round(w * s) & ~1), Math.Max(2, (int)Math.Round(h * s) & ~1));
    }

    /// <summary>Formato de pixel que o encoder recebe (os de hardware preferem NV12).</summary>
    public static string PixelFormat(string encoder) => encoder == "libx264" ? "yuv420p" : "nv12";

    /// <summary>Encoder H.264 com ajustes de baixa latência + saída FLV no stdout.</summary>
    public static List<string> H264Tail(string encoder, int fps, int kbps, int gopSeconds)
    {
        var a = new List<string> { "-c:v", encoder };
        switch (encoder)
        {
            case "h264_nvenc":
                a.AddRange(["-preset", "p1", "-tune", "ull", "-zerolatency", "1", "-delay", "0", "-rc", "cbr", "-profile:v", "main"]);
                break;
            case "h264_amf":
                a.AddRange(["-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr", "-profile:v", "main"]);
                break;
            case "h264_qsv":
                a.AddRange(["-preset", "veryfast", "-look_ahead", "0", "-async_depth", "1", "-profile:v", "main"]);
                break;
            case "h264_videotoolbox":
                a.AddRange(["-realtime", "1", "-allow_sw", "1", "-profile:v", "main"]);
                break;
            default:
                a.AddRange(["-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline"]);
                break;
        }
        a.AddRange([
            "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{Math.Max(kbps / 4, 500)}k",
            "-g", (fps * gopSeconds).ToString(), "-bf", "0",
            "-an", "-f", "flv", "-flvflags", "no_duration_filesize", "-flush_packets", "1", "-",
        ]);
        return a;
    }

    /// <summary>JPEG (cliente web sem WebCodecs). Qualidade 2 (melhor) a 31 (pior).</summary>
    public static List<string> JpegTail(int quality = 7) =>
    [
        "-color_range", "pc", "-c:v", "mjpeg", "-q:v", quality.ToString(),
        "-an", "-f", "mjpeg", "-flush_packets", "1", "-",
    ];

    /// <summary>Filtro final: escala (se precisar) e converte para o formato do encoder.</summary>
    public static string ScaleAndFormat(int srcW, int srcH, int outW, int outH, bool jpeg, string encoder)
    {
        bool scale = outW != srcW || outH != srcH;
        if (jpeg) return $"scale={outW}:{outH}:flags=bilinear:out_range=full,format=yuv420p";
        return (scale ? $"scale={outW}:{outH}:flags=bilinear," : "") + $"format={PixelFormat(encoder)}";
    }
}
