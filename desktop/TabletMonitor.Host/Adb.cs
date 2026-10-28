using System.IO.Compression;
using System.Reflection;

namespace TabletMonitor;

/// <summary>
/// adb: mantém o "adb reverse" ativo para o modo USB e faz a preparação do tablet (modo dedicado).
/// </summary>
public sealed class Adb : IDisposable
{
    public const string Package = "com.apontes.tabletmonitor";
    public const string AdminComponent = Package + "/.KioskAdminReceiver";
    public const string MainActivity = Package + "/.MainActivity";

    private static string PlatformToolsUrl => "https://dl.google.com/android/repository/platform-tools-latest-" +
        (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux") + ".zip";
    private static string AdbName => OperatingSystem.IsWindows() ? "adb.exe" : "adb";
    public static string LocalPath => Path.Combine(AppSettings.ToolsDir, "platform-tools", AdbName);

    private readonly Action<string> _log;
    private System.Threading.Timer? _timer;
    private readonly Dictionary<string, DateTime> _reversed = new();
    private int _busy;

    public int Port { get; set; }
    public string? Path_ { get; private set; }
    public event Action<IReadOnlyList<string>>? DevicesChanged;
    public IReadOnlyList<string> Devices { get; private set; } = [];

    public Adb(Action<string> log) => _log = log;

    public static string? Find()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            LocalPath,
            Path.Combine(AppContext.BaseDirectory, AdbName),
            Path.Combine(AppContext.BaseDirectory, "platform-tools", AdbName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", AdbName),
            Path.Combine(home, "Library", "Android", "sdk", "platform-tools", AdbName),
            "/opt/homebrew/bin/adb",
            "/usr/local/bin/adb",
        };
        char sep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), AdbName));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<string> DownloadAsync(Action<string> log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppSettings.ToolsDir);
        string zip = System.IO.Path.Combine(AppSettings.ToolsDir, "platform-tools.zip");
        await Downloader.DownloadAsync(PlatformToolsUrl, zip, log, ct);
        string target = System.IO.Path.Combine(AppSettings.ToolsDir, "platform-tools");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        ZipFile.ExtractToDirectory(zip, AppSettings.ToolsDir, overwriteFiles: true);
        File.Delete(zip);
        Proc.MakeExecutable(LocalPath);
        log("adb instalado em " + LocalPath);
        return LocalPath;
    }

    public void StartAutoReverse(string adbPath, int port)
    {
        Path_ = adbPath;
        Port = port;
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Tick(), null, 500, 3000);
    }

    public void StopAutoReverse()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Tick()
    {
        if (Path_ == null || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var devices = ListDevices();
            if (!devices.SequenceEqual(Devices))
            {
                Devices = devices;
                DevicesChanged?.Invoke(devices);
            }
            foreach (var gone in _reversed.Keys.Except(devices).ToList()) _reversed.Remove(gone);
            foreach (var serial in devices)
            {
                // Refaz periodicamente: o reverse some se o servidor adb reiniciar.
                if (_reversed.TryGetValue(serial, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(20)) continue;
                var (code, output) = Proc.Run(Path_, ["-s", serial, "reverse", $"tcp:{Port}", $"tcp:{Port}"], 8000);
                if (code == 0)
                {
                    if (!_reversed.ContainsKey(serial)) _log($"USB: túnel ativo para o tablet {serial}");
                    _reversed[serial] = DateTime.UtcNow;
                }
                else if (!_reversed.ContainsKey(serial))
                {
                    _log($"USB: falha no adb reverse ({serial}): {output.Trim()}");
                    _reversed[serial] = DateTime.UtcNow; // evita repetir a mensagem a cada 3 s
                }
            }
        }
        catch (Exception ex) { _log("adb: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Seriais em estado "device". Avisa se algum estiver "unauthorized".</summary>
    public List<string> ListDevices()
    {
        if (Path_ == null) return [];
        var (code, output) = Proc.Run(Path_, ["devices"], 8000);
        var list = new List<string>();
        if (code != 0) return list;
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length < 2) continue;
            if (parts[1] == "device") list.Add(parts[0]);
            else if (parts[1] == "unauthorized" && WarnOnce(parts[0]))
            {
                _log($"Tablet {parts[0]} não autorizado: desbloqueie o tablet e toque em \"Permitir\" (marque \"Sempre permitir\").");
            }
        }
        return list;
    }

    private readonly HashSet<string> _warnedUnauthorized = new();
    private bool WarnOnce(string serial) { lock (_warnedUnauthorized) return _warnedUnauthorized.Add(serial); }

    private (int Code, string Output) Shell(string serial, params string[] cmd)
    {
        var args = new List<string> { "-s", serial, "shell" };
        args.AddRange(cmd);
        return Proc.Run(Path_!, args, 30000);
    }

    // ------------------------------------------------------------------ preparação do tablet

    /// <summary>
    /// Pacotes típicos que só consomem memória/CPU num tablet dedicado a ser monitor.
    /// São DESATIVADOS (pm disable-user), não removidos — dá para reativar depois.
    /// Lançador, teclado, WebView, Configurações, Google Play Services e componentes do sistema ficam intactos.
    /// </summary>
    private static readonly string[] Bloat =
    [
        "com.google.android.youtube", "com.google.android.apps.youtube.music", "com.google.android.apps.youtube.kids",
        "com.google.android.music", "com.google.android.videos", "com.google.android.apps.photos",
        "com.google.android.apps.docs", "com.google.android.apps.docs.editors.docs", "com.google.android.apps.docs.editors.sheets",
        "com.google.android.apps.docs.editors.slides", "com.google.android.gm", "com.google.android.apps.maps",
        "com.google.android.apps.tachyon", "com.google.android.apps.meetings", "com.google.android.apps.magazines",
        "com.google.android.apps.books", "com.google.android.play.games", "com.google.android.googlequicksearchbox",
        "com.google.android.apps.googleassistant", "com.google.android.keep", "com.google.android.calendar",
        "com.google.android.apps.subscriptions.red", "com.google.android.apps.wellbeing", "com.google.android.feedback",
        "com.google.android.apps.podcasts", "com.google.android.apps.chromecast.app", "com.google.ar.core",
        "com.android.chrome", "com.facebook.katana", "com.facebook.appmanager", "com.facebook.services", "com.facebook.system",
        "com.netflix.mediaclient", "com.netflix.partner.activation", "com.spotify.music", "com.microsoft.skydrive",
        "com.microsoft.office.officehubrow", "com.linkedin.android", "com.amazon.mShop.android.shopping", "com.instagram.android",
        "com.whatsapp", "com.android.email", "com.android.calendar", "com.android.gallery3d", "com.android.music",
        "com.android.browser", "com.android.dreams.basic", "com.android.dreams.phototable",
        // Samsung
        "com.samsung.android.bixby.agent", "com.samsung.android.bixby.wakeup", "com.samsung.android.app.spage",
        "com.samsung.android.visionintelligence", "com.samsung.android.email.provider", "com.samsung.android.calendar",
        "com.samsung.android.app.notes", "com.samsung.android.game.gamehome", "com.samsung.android.game.gametools",
        "com.samsung.android.app.tips", "com.samsung.android.kidsinstaller", "com.samsung.android.arzone",
        "com.samsung.android.aremoji", "com.sec.android.app.sbrowser", "com.samsung.android.voc",
        "com.samsung.android.mobileservice", "com.samsung.android.scloud", "com.samsung.android.app.watchmanagerstub",
        "com.sec.android.app.samsungapps", "com.samsung.android.themestore", "com.samsung.android.smartswitchassistant",
        "com.samsung.android.da.daagent", "com.samsung.android.forest", "com.samsung.android.svoiceime",
        // Lenovo / Motorola / Xiaomi / Huawei (os que não existirem são ignorados)
        "com.lenovo.leos.appstore", "com.lenovo.menu_assistant", "com.lenovo.ue.device", "com.lenovo.lsf.user",
        "com.motorola.ccc.notification", "com.miui.analytics", "com.miui.msa.global", "com.xiaomi.mipicks",
        "com.huawei.appmarket", "com.huawei.himovie.overseas", "com.huawei.music",
    ];

    public sealed record ProvisionOptions(bool Debloat, bool DisablePlayStore);

    /// <summary>Instala o app, define como dono do dispositivo e ajusta o sistema. Retorna pacotes desativados.</summary>
    public List<string> Provision(string serial, ProvisionOptions opt)
    {
        if (Path_ == null) throw new InvalidOperationException("adb não encontrado");
        var disabled = new List<string>();

        _log($"[1/6] Instalando o app no tablet {serial}…");
        string apk = ExtractApk();
        var (code, output) = Proc.Run(Path_, ["-s", serial, "install", "-r", "-g", apk], 120000);
        if (code != 0 || !output.Contains("Success"))
            throw new InvalidOperationException("Falha ao instalar o APK: " + output.Trim());

        // Permite ao app ajustar o sistema sozinho (tela sempre ligada, animações, Wi-Fi…).
        Shell(serial, "pm", "grant", Package, "android.permission.WRITE_SECURE_SETTINGS");

        _log("[2/6] Definindo o TabletMonitor como dono do dispositivo (modo dedicado)…");
        (code, output) = Shell(serial, "dpm", "set-device-owner", AdminComponent);
        if (output.Contains("account", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "O Android recusou porque há contas no tablet (ex.: conta Google).\n" +
                "Remova todas as contas em Configurações > Contas (ou restaure de fábrica e pule o login) e rode de novo.\n\n" + output.Trim());
        }
        else if (output.Contains("several users", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("multiple users", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Há mais de um usuário no tablet. Remova os usuários extras e rode de novo.\n\n" + output.Trim());
        }
        else if (output.Contains("Success", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("device owner is already set", StringComparison.OrdinalIgnoreCase))
        {
            _log("      ok");
        }
        else
        {
            throw new InvalidOperationException("Não foi possível ativar o modo dedicado:\n" + output.Trim());
        }

        _log("[3/6] Ajustando o sistema (animações, tela sempre ligada, economia de bateria)…");
        string[][] tweaks =
        [
            ["settings", "put", "global", "window_animation_scale", "0"],
            ["settings", "put", "global", "transition_animation_scale", "0"],
            ["settings", "put", "global", "animator_duration_scale", "0"],
            ["settings", "put", "global", "stay_on_while_plugged_in", "7"],
            ["settings", "put", "system", "screen_off_timeout", "2147483647"],
            ["settings", "put", "secure", "screensaver_enabled", "0"],
            ["settings", "put", "secure", "screensaver_activate_on_sleep", "0"],
            ["settings", "put", "secure", "screensaver_activate_on_dock", "0"],
            ["settings", "put", "global", "wifi_sleep_policy", "2"],
            ["dumpsys", "deviceidle", "whitelist", "+" + Package],
            ["cmd", "appops", "set", Package, "RUN_IN_BACKGROUND", "allow"],
        ];
        foreach (var t in tweaks) Shell(serial, t);

        if (opt.Debloat)
        {
            _log("[4/6] Desativando apps desnecessários (reversível)…");
            var (_, pkgsOut) = Shell(serial, "pm", "list", "packages", "-e");
            var installed = pkgsOut.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:"))
                .Select(l => l["package:".Length..]).ToHashSet();
            var targets = Bloat.Where(installed.Contains).ToList();
            if (opt.DisablePlayStore && installed.Contains("com.android.vending")) targets.Add("com.android.vending");
            foreach (var pkg in targets)
            {
                var (_, r) = Shell(serial, "pm", "disable-user", "--user", "0", pkg);
                if (r.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    disabled.Add(pkg);
                    _log("      desativado: " + pkg);
                }
            }
            _log($"      {disabled.Count} apps desativados");
        }
        else _log("[4/6] Desativar apps: pulado");

        _log("[5/6] Abrindo o TabletMonitor no tablet…");
        Shell(serial, "am", "start", "-n", MainActivity);

        _log("[6/6] Pronto! O tablet agora só roda o TabletMonitor.");
        return disabled;
    }

    public void ReenablePackages(string serial, IEnumerable<string> packages)
    {
        foreach (var pkg in packages)
        {
            Shell(serial, "pm", "enable", pkg);
            _log("reativado: " + pkg);
        }
    }

    public void InstallOnly(string serial)
    {
        string apk = ExtractApk();
        var (code, output) = Proc.Run(Path_!, ["-s", serial, "install", "-r", "-g", apk], 120000);
        _log(code == 0 ? "App instalado/atualizado no tablet." : "Falha ao instalar: " + output.Trim());
        if (code == 0)
        {
            Shell(serial, "pm", "grant", Package, "android.permission.WRITE_SECURE_SETTINGS");
            Shell(serial, "am", "start", "-n", MainActivity);
        }
    }

    private static string ExtractApk()
    {
        string dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TabletMonitor.apk");
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("TabletMonitor.apk")
            ?? throw new InvalidOperationException("APK não embutido neste executável (compile o app Android antes do Windows).");
        using var f = File.Create(dest);
        res.CopyTo(f);
        return dest;
    }

    public void Dispose() => StopAutoReverse();
}
