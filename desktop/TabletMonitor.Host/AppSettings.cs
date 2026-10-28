using System.Text.Json;

namespace TabletMonitor;

public enum InputMode { Touch, Mouse, Off }

public sealed class AppSettings
{
    /// <summary>DeviceName do monitor (ex.: \\.\DISPLAY3). Vazio = escolhe o monitor virtual / último não principal.</summary>
    public string MonitorDeviceName { get; set; } = "";
    /// <summary>Auto, h264_nvenc, h264_amf, h264_qsv ou libx264.</summary>
    public string Encoder { get; set; } = "Auto";
    public int Fps { get; set; } = 60;
    public int BitrateKbps { get; set; } = 10000;
    public InputMode Input { get; set; } = InputMode.Touch;
    public bool DrawMouse { get; set; } = true;
    public int Port { get; set; } = Core.Protocol.DefaultPort;
    public bool UsbAuto { get; set; } = true;
    /// <summary>Servir o cliente web (iPhone/iPad/qualquer navegador).</summary>
    public bool WebClient { get; set; } = true;
    /// <summary>Aceitar o tablet via Bluetooth (perfil leve).</summary>
    public bool Bluetooth { get; set; } = true;
    public int BluetoothFps { get; set; } = 15;
    public int BluetoothBitrateKbps { get; set; } = 1500;
    /// <summary>Largura máxima do vídeo (0 = o máximo que o decodificador do tablet aguenta).</summary>
    public int MaxWidth { get; set; } = 0;
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    /// <summary>Pacotes desativados pela preparação do tablet (para poder reativar depois).</summary>
    public List<string> DisabledPackages { get; set; } = new();

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabletMonitor");
    public static string ToolsDir => Path.Combine(Dir, "tools");
    private static string FilePath => Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
