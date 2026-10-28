using System.Diagnostics;
using Microsoft.Win32;
using TabletMonitor.Core;

namespace TabletMonitor;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly bool _startHidden;
    private readonly StreamServer _server = new();
    private readonly InputInjector _injector = new();
    private readonly Adb _adb;
    private DiscoveryBeacon? _beacon;
    private readonly BluetoothServer _bt;
    private WebServer? _web;
    private readonly CheckBox _webClient = new() { Text = "Cliente web (iPhone/iPad)", AutoSize = true };
    private readonly PictureBox _qr = new() { Width = 112, Height = 112, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 12, 0) };
    private readonly Label _webInfo = new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 6, 0, 0) };
    private string? _ffmpegPath;
    private List<string> _encoders = new();
    private List<MonitorInfo> _monitors = new();
    private volatile bool _allowExit;
    private bool _initialized;

    // UI
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 12f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _hint = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.TopLeft };
    private readonly ComboBox _monitorBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _encoderBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _fpsBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _bitrate = new() { Minimum = 1, Maximum = 50, Dock = DockStyle.Fill };
    private readonly ComboBox _inputBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _drawMouse = new() { Text = "Mostrar o cursor no tablet", AutoSize = true };
    private readonly CheckBox _usbAuto = new() { Text = "USB automático (adb reverse)", AutoSize = true };
    private readonly CheckBox _bluetooth = new() { Text = "Aceitar via Bluetooth", AutoSize = true };
    private readonly ComboBox _maxRes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _btFps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
    private readonly NumericUpDown _btBitrate = new() { Minimum = 0.5m, Maximum = 4m, Increment = 0.5m, DecimalPlaces = 1, Width = 60 };
    private static readonly int[] MaxWidths = [0, 2560, 1920, 1600, 1280, 1024];
    private readonly CheckBox _startWithWindows = new() { Text = "Iniciar com o Windows (na bandeja)", AutoSize = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(24, 26, 30), ForeColor = Color.Gainsboro };
    private readonly NotifyIcon _tray = new();
    private bool _loadingUi;

    public MainForm(AppSettings settings, bool startHidden)
    {
        _settings = settings;
        _startHidden = startHidden;
        _adb = new Adb(Log);
        _bt = new BluetoothServer(_server, Log);

        Text = "TabletMonitor — segundo monitor no tablet";
        Icon = LoadIcon();
        ClientSize = new Size(780, 760);
        MinimumSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        BuildLayout();
        BuildTray();
        LoadSettingsToUi();

        _server.Log += Log;
        _server.PlanSession = PlanSession;
        _server.OnTouch = _injector.Handle;
        _server.OnSessionEnd = _injector.ReleaseAll;
        _server.ClientConnected += (hello, transport) => UpdateStatus($"Conectado: {hello.Name} via {TransportName(transport)}", Color.SeaGreen);
        _server.ClientDisconnected += () => UpdateStatus("Aguardando o tablet…", Color.DarkOrange);
        _injector.Log = Log;
        _adb.DevicesChanged += d => Log(d.Count == 0 ? "USB: nenhum tablet conectado" : "USB: tablet(s) detectado(s): " + string.Join(", ", d));

        // HandleCreated dispara mesmo quando o app inicia escondido na bandeja (Shown não).
        HandleCreated += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await InitializeAsync();
        };
    }

    private static Icon LoadIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    protected override void SetVisibleCore(bool value)
    {
        // Permite iniciar direto na bandeja sem piscar a janela.
        if (_startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowCount = 6;
        Controls.Add(root);

        root.Controls.Add(_status, 0, 0);
        root.Controls.Add(_hint, 0, 1);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        Label L(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) };

        var refresh = new Button { Text = "Atualizar", AutoSize = true };
        refresh.Click += (_, _) => RefreshMonitors();
        var monRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        monRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        monRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        monRow.Controls.Add(_monitorBox, 0, 0);
        monRow.Controls.Add(refresh, 1, 0);

        grid.Controls.Add(L("Monitor a transmitir:"), 0, 0);
        grid.Controls.Add(monRow, 1, 0);
        grid.SetColumnSpan(monRow, 3);

        grid.Controls.Add(L("Encoder:"), 0, 1);
        grid.Controls.Add(_encoderBox, 1, 1);
        grid.Controls.Add(L("Quadros/s:"), 2, 1);
        grid.Controls.Add(_fpsBox, 3, 1);

        grid.Controls.Add(L("Bitrate (Mbps):"), 0, 2);
        grid.Controls.Add(_bitrate, 1, 2);
        grid.Controls.Add(L("Toque:"), 2, 2);
        grid.Controls.Add(_inputBox, 3, 2);

        var btRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        btRow.Controls.AddRange([_btFps, new Label { Text = "qps", AutoSize = true, Margin = new Padding(2, 8, 10, 0) },
            _btBitrate, new Label { Text = "Mbps", AutoSize = true, Margin = new Padding(2, 8, 0, 0) }]);
        grid.Controls.Add(L("Resolução máx.:"), 0, 3);
        grid.Controls.Add(_maxRes, 1, 3);
        grid.Controls.Add(L("Bluetooth:"), 2, 3);
        grid.Controls.Add(btRow, 3, 3);

        var checks = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        checks.Controls.AddRange([_drawMouse, _usbAuto, _bluetooth, _webClient, _startWithWindows]);
        grid.Controls.Add(checks, 0, 4);
        grid.SetColumnSpan(checks, 4);
        root.Controls.Add(grid, 0, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 6) };
        Button B(string t, Func<Task> a)
        {
            var b = new Button { Text = t, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
            b.Click += async (_, _) => { b.Enabled = false; try { await a(); } catch (Exception ex) { Log("Erro: " + ex.Message); } finally { b.Enabled = true; } };
            return b;
        }
        buttons.Controls.Add(B("Preparar tablet (modo dedicado)…", ProvisionTabletAsync));
        buttons.Controls.Add(B("Atualizar app no tablet", InstallAppAsync));
        buttons.Controls.Add(B("Instalar monitor virtual…", InstallVirtualDisplayAsync));
        buttons.Controls.Add(B("Reconectar tablet", () => { _server.DisconnectClient(); return Task.CompletedTask; }));
        buttons.Controls.Add(B("Reativar apps do tablet", ReenableAppsAsync));
        root.Controls.Add(buttons, 0, 3);

        var webRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        webRow.Controls.AddRange([_qr, _webInfo]);
        root.Controls.Add(webRow, 0, 4);
        root.Controls.Add(_log, 0, 5);

        _fpsBox.Items.AddRange(["30", "60"]);
        _inputBox.Items.AddRange(["Toque nativo do Windows", "Mouse (dedo = cursor)", "Desligado (só imagem)"]);
        _encoderBox.Items.Add("Auto");
        _btFps.Items.AddRange(["10", "15", "20", "30"]);
        _maxRes.Items.AddRange(["Automática (o que o tablet aguenta)", "2560 de largura", "1920 de largura", "1600 de largura", "1280 de largura", "1024 de largura"]);

        _monitorBox.SelectedIndexChanged += (_, _) => OnSettingChanged(reconnect: true);
        _encoderBox.SelectedIndexChanged += (_, _) => OnSettingChanged(reconnect: true);
        _fpsBox.SelectedIndexChanged += (_, _) => OnSettingChanged(reconnect: true);
        _bitrate.ValueChanged += (_, _) => OnSettingChanged(reconnect: true);
        _drawMouse.CheckedChanged += (_, _) => OnSettingChanged(reconnect: true);
        _inputBox.SelectedIndexChanged += (_, _) => OnSettingChanged(reconnect: false);
        _usbAuto.CheckedChanged += (_, _) => { OnSettingChanged(reconnect: false); _ = ApplyUsbAsync(); };
        _bluetooth.CheckedChanged += (_, _) => { OnSettingChanged(reconnect: false); _ = ApplyBluetoothAsync(); };
        _webClient.CheckedChanged += (_, _) => { OnSettingChanged(reconnect: false); ApplyWebClient(); };
        _maxRes.SelectedIndexChanged += (_, _) => OnSettingChanged(reconnect: true);
        _btFps.SelectedIndexChanged += (_, _) => OnSettingChanged(reconnect: _server.CurrentTransport == Transport.Bluetooth);
        _btBitrate.ValueChanged += (_, _) => OnSettingChanged(reconnect: _server.CurrentTransport == Transport.Bluetooth);
        _startWithWindows.CheckedChanged += (_, _) => { OnSettingChanged(reconnect: false); ApplyStartup(); };
    }

    private void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "TabletMonitor";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => ShowFromTray());
        menu.Items.Add("Reconectar tablet", null, (_, _) => _server.DisconnectClient());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => { _allowExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2500, "TabletMonitor", "Continua rodando na bandeja. Clique com o botão direito no ícone para sair.", ToolTipIcon.Info);
            return;
        }
        _settings.Save();
        _adb.Dispose();
        _bt.Dispose();
        _web?.Dispose();
        _beacon?.Dispose();
        _server.Dispose();
        _injector.Dispose();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ settings <-> UI

    private void LoadSettingsToUi()
    {
        _loadingUi = true;
        _fpsBox.SelectedItem = _settings.Fps >= 60 ? "60" : "30";
        _bitrate.Value = Math.Clamp(_settings.BitrateKbps / 1000, 1, 50);
        _inputBox.SelectedIndex = (int)_settings.Input;
        _drawMouse.Checked = _settings.DrawMouse;
        _usbAuto.Checked = _settings.UsbAuto;
        _bluetooth.Checked = _settings.Bluetooth;
        _webClient.Checked = _settings.WebClient;
        _btFps.SelectedItem = _btFps.Items.Contains(_settings.BluetoothFps.ToString()) ? _settings.BluetoothFps.ToString() : "15";
        _btBitrate.Value = Math.Clamp(_settings.BluetoothBitrateKbps / 1000m, 0.5m, 4m);
        _maxRes.SelectedIndex = Math.Max(0, Array.IndexOf(MaxWidths, _settings.MaxWidth));
        _startWithWindows.Checked = _settings.StartWithWindows;
        _encoderBox.SelectedIndex = 0;
        _injector.Mode = _settings.Input;
        _loadingUi = false;
    }

    private void OnSettingChanged(bool reconnect)
    {
        if (_loadingUi) return;
        if (_monitorBox.SelectedItem is MonitorInfo m) _settings.MonitorDeviceName = m.DeviceName;
        _settings.Encoder = _encoderBox.SelectedItem as string ?? "Auto";
        _settings.Fps = _fpsBox.SelectedItem as string == "30" ? 30 : 60;
        _settings.BitrateKbps = (int)_bitrate.Value * 1000;
        _settings.Input = (InputMode)Math.Max(0, _inputBox.SelectedIndex);
        _settings.DrawMouse = _drawMouse.Checked;
        _settings.UsbAuto = _usbAuto.Checked;
        _settings.Bluetooth = _bluetooth.Checked;
        _settings.WebClient = _webClient.Checked;
        _settings.BluetoothFps = int.TryParse(_btFps.SelectedItem as string, out var bf) ? bf : 15;
        _settings.BluetoothBitrateKbps = (int)(_btBitrate.Value * 1000);
        _settings.MaxWidth = MaxWidths[Math.Max(0, _maxRes.SelectedIndex)];
        _settings.StartWithWindows = _startWithWindows.Checked;
        _injector.Mode = _settings.Input;
        _settings.Save();
        if (reconnect && _server.HasClient) _server.DisconnectClient(); // o tablet reconecta sozinho com a nova configuração
    }

    private void RefreshMonitors()
    {
        try
        {
            _monitors = Monitors.Enumerate();
        }
        catch (Exception ex)
        {
            Log("Falha ao listar monitores: " + ex.Message);
            return;
        }
        _loadingUi = true;
        _monitorBox.Items.Clear();
        foreach (var m in _monitors) _monitorBox.Items.Add(m);
        var pick = Monitors.Pick(_monitors, _settings.MonitorDeviceName);
        if (pick != null) _monitorBox.SelectedItem = pick;
        _loadingUi = false;
        if (pick != null) _settings.MonitorDeviceName = pick.DeviceName;

        if (_monitors.Count <= 1)
            SetHint("Só há 1 monitor. Para estender a área de trabalho, instale um monitor virtual (botão abaixo) ou use um plug HDMI \"dummy\". Com 1 monitor, o tablet espelha a tela.");
        else if (pick is { IsPrimary: true })
            SetHint("Atenção: o monitor escolhido é o principal — o tablet vai espelhar sua tela.");
        else
            SetHint("Dica: em Configurações > Sistema > Tela, posicione o monitor do tablet ao lado do principal.");
    }

    // ------------------------------------------------------------------ inicialização

    private async Task InitializeAsync()
    {
        UpdateStatus("Iniciando…", Color.DimGray);
        RefreshMonitors();

        _ffmpegPath = Ffmpeg.Find();
        if (_ffmpegPath == null)
        {
            Log("ffmpeg não encontrado — baixando (necessário para capturar e comprimir a tela, ~100 MB, só uma vez)…");
            try { _ffmpegPath = await Ffmpeg.DownloadAsync(Log); }
            catch (Exception ex)
            {
                Log(ex.Message);
                Log("Baixe manualmente um ffmpeg 6.0 ou mais novo para Windows e coloque o ffmpeg.exe em " + Path.GetDirectoryName(Ffmpeg.LocalPath));
                UpdateStatus("ffmpeg ausente", Color.Firebrick);
                return;
            }
        }
        Log("ffmpeg: " + _ffmpegPath);

        await Task.Run(() =>
        {
            if (!Ffmpeg.HasDdagrab(_ffmpegPath)) Log("AVISO: este ffmpeg não tem captura ddagrab (precisa da versão 6.0+). Apague-o para baixar um novo.");
            _encoders = Ffmpeg.ProbeEncoders(_ffmpegPath);
        });
        Log("Encoders disponíveis: " + (_encoders.Count > 0 ? string.Join(", ", _encoders) : "nenhum!"));
        _loadingUi = true;
        _encoderBox.Items.Clear();
        _encoderBox.Items.Add("Auto");
        foreach (var e in _encoders) _encoderBox.Items.Add(e);
        _encoderBox.SelectedItem = _encoderBox.Items.Contains(_settings.Encoder) ? _settings.Encoder : "Auto";
        _loadingUi = false;

        try
        {
            _server.Start(_settings.Port);
            _beacon = new DiscoveryBeacon(_settings.Port);
        }
        catch (Exception ex)
        {
            Log($"Não foi possível abrir a porta {_settings.Port}: {ex.Message}");
            UpdateStatus("Erro na porta de rede", Color.Firebrick);
            return;
        }
        Log("Wi-Fi: se o Firewall do Windows perguntar, permita o TabletMonitor em redes privadas.");
        UpdateStatus("Aguardando o tablet…", Color.DarkOrange);
        await ApplyUsbAsync();
        await ApplyBluetoothAsync();
        ApplyWebClient();
    }

    /// <summary>Liga/desliga o servidor do cliente web e mostra o QR code com o endereço.</summary>
    private void ApplyWebClient()
    {
        if (!_server.IsRunning) return;
        if (!_settings.WebClient)
        {
            _web?.Dispose();
            _web = null;
            _qr.Image = null;
            _webInfo.Text = "Cliente web desligado.";
            return;
        }
        if (_web != null) return;
        try
        {
            var certs = CertManager.LoadOrCreate(Path.Combine(AppSettings.Dir, "certs"));
            _web = new WebServer(_server, certs, Log);
            _web.Start();
        }
        catch (Exception ex)
        {
            Log("Cliente web: " + ex.Message);
            _web = null;
            return;
        }
        var ip = CertManager.BestLanAddress()?.ToString() ?? "IP-do-PC";
        string url = $"http://{ip}:{_web.HttpPort}/";
        try
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
            _qr.Image = Image.FromStream(new MemoryStream(png));
        }
        catch { }
        _webInfo.Text = $"iPhone / iPad / outro tablet: aponte a câmera para o QR code ou abra no Safari:\n{url}\n" +
                        "A página ensina a instalar o certificado (vídeo H.264) e a adicionar à Tela de Início. Sem App Store e sem conta de desenvolvedor.";
    }

    private async Task ApplyBluetoothAsync()
    {
        if (!_server.IsRunning) return;
        if (_settings.Bluetooth) await _bt.StartAsync();
        else if (_bt.IsRunning) { _bt.Stop(); Log("Bluetooth desligado"); }
    }

    private static string TransportName(Transport t) => t switch
    {
        Transport.Usb => "USB",
        Transport.Wifi => "Wi-Fi",
        Transport.Web => "navegador",
        _ => "Bluetooth",
    };

    private async Task<string?> EnsureAdbAsync()
    {
        var path = Adb.Find();
        if (path != null) return path;
        Log("adb não encontrado — baixando as Android Platform Tools (~7 MB)…");
        try { return await Adb.DownloadAsync(Log); }
        catch (Exception ex) { Log("Falha ao baixar o adb: " + ex.Message); return null; }
    }

    private async Task ApplyUsbAsync()
    {
        if (!_settings.UsbAuto) { _adb.StopAutoReverse(); return; }
        var adb = await EnsureAdbAsync();
        if (adb == null) return;
        _adb.StartAutoReverse(adb, _settings.Port);
    }

    private void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (_settings.StartWithWindows) key.SetValue("TabletMonitor", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("TabletMonitor", throwOnMissingValue: false);
        }
        catch (Exception ex) { Log("Inicialização automática: " + ex.Message); }
    }

    // ------------------------------------------------------------------ sessão

    /// <summary>Chamado na thread do servidor quando um tablet conecta.</summary>
    private SessionPlan PlanSession(HelloInfo hello, Transport transport)
    {
        if (_ffmpegPath == null) throw new InvalidOperationException("ffmpeg ainda não está pronto");
        // Atualiza a lista (o monitor virtual pode ter mudado de resolução/posição).
        List<MonitorInfo> monitors;
        try { monitors = Monitors.Enumerate(); } catch { monitors = _monitors; }
        var mon = Monitors.Pick(monitors, _settings.MonitorDeviceName) ?? throw new InvalidOperationException("Nenhum monitor encontrado");

        string encoder = _settings.Encoder;
        if (encoder == "Auto" || !_encoders.Contains(encoder)) encoder = _encoders.FirstOrDefault() ?? "libx264";

        // Limites: tela do tablet, o que o decodificador dele aguenta e o escolhido aqui.
        int maxW = hello.Width, maxH = hello.Height;
        if (hello.MaxDecodeWidth > 0) { maxW = Math.Min(maxW, hello.MaxDecodeWidth); maxH = Math.Min(maxH, hello.MaxDecodeHeight); }
        if (_settings.MaxWidth > 0) maxW = Math.Min(maxW, _settings.MaxWidth);

        int fps = _settings.Fps, kbps = _settings.BitrateKbps, queue = 3, latency = 250, gop = 2;
        switch (transport)
        {
            case Transport.Usb:
                queue = 4; latency = 300;
                break;
            case Transport.Web when !hello.Codecs.HasFlag(CodecSupport.H264):
                // Navegador sem WebCodecs: JPEG. Cada quadro é independente, então limitamos tamanho e quadros/s pela banda.
                maxW = Math.Min(maxW, 1280);
                fps = Math.Min(fps, 24);
                queue = 2; latency = 200;
                break;
            case Transport.Bluetooth:
                // Bluetooth dá ~1–2 Mbps reais: menos quadros, menos bits, imagem menor, keyframes mais frequentes.
                fps = _settings.BluetoothFps;
                kbps = _settings.BluetoothBitrateKbps;
                maxW = Math.Min(maxW, 1280);
                queue = 2; latency = 400; gop = 2;
                break;
        }

        var (w, h) = Ffmpeg.Cap(mon.Bounds.Width, mon.Bounds.Height, maxW, maxH);
        bool jpeg = transport == Transport.Web && !hello.Codecs.HasFlag(CodecSupport.H264);
        var args = jpeg
            ? Ffmpeg.BuildJpegArgs(mon, w, h, fps, _settings.DrawMouse)
            : Ffmpeg.BuildArgs(mon, encoder, w, h, fps, kbps, _settings.DrawMouse, gop);
        if (jpeg) encoder = "JPEG";

        _injector.Target = mon.Bounds;
        Log($"Transmitindo {mon} com {encoder}, {kbps / 1000.0:0.#} Mbps");

        // Resolução ideal do monitor virtual: proporção do tablet, no tamanho que ele decodifica.
        var (idealW, idealH) = Ffmpeg.Cap(hello.Width, hello.Height,
            hello.MaxDecodeWidth > 0 ? hello.MaxDecodeWidth : 0, hello.MaxDecodeHeight > 0 ? hello.MaxDecodeHeight : 0);
        bool sameAspect = Math.Abs((double)mon.Bounds.Width / mon.Bounds.Height - (double)hello.Width / hello.Height) < 0.02;
        if (!sameAspect)
            SetHint($"Para imagem sem bordas, configure o monitor transmitido para {idealW}x{idealH} (mesma proporção do tablet). Agora: {mon.Bounds.Width}x{mon.Bounds.Height}.");
        else if (mon.Bounds.Width > w)
            SetHint($"O monitor tem {mon.Bounds.Width}x{mon.Bounds.Height} e o vídeo sai em {w}x{h}. Para textos mais nítidos use {idealW}x{idealH} no monitor virtual (e escala de 125–150%).");
        else
            SetHint("Resolução ideal. Dica: em Configurações > Sistema > Tela, posicione o monitor do tablet ao lado do principal.");

        return new SessionPlan(_ffmpegPath, args, w, h, fps, queue, latency, jpeg ? Codec.Jpeg : Codec.H264);
    }

    // ------------------------------------------------------------------ botões

    private async Task<string?> PickDeviceAsync()
    {
        var adb = await EnsureAdbAsync();
        if (adb == null) return null;
        _adb.StartAutoReverse(adb, _settings.Port);
        var devices = await Task.Run(_adb.ListDevices);
        if (devices.Count == 0)
        {
            MessageBox.Show(this,
                "Nenhum tablet encontrado via USB.\n\n" +
                "1. No tablet: Configurações > Sobre o tablet > toque 7x em \"Número da versão\".\n" +
                "2. Configurações > Sistema > Opções do desenvolvedor > ative \"Depuração USB\".\n" +
                "3. Conecte o cabo e toque em \"Permitir\" (marque \"Sempre permitir deste computador\").",
                "TabletMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        return devices[0];
    }

    private async Task ProvisionTabletAsync()
    {
        var serial = await PickDeviceAsync();
        if (serial == null) return;

        using var dlg = new ProvisionDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var opt = new Adb.ProvisionOptions(dlg.Debloat, dlg.DisablePlayStore);

        try
        {
            var disabled = await Task.Run(() => _adb.Provision(serial, opt));
            foreach (var p in disabled) if (!_settings.DisabledPackages.Contains(p)) _settings.DisabledPackages.Add(p);
            _settings.Save();
            MessageBox.Show(this, "Tablet preparado! Ele agora abre direto no TabletMonitor e não sai mais dele.\n\n" +
                                  "Para sair do modo dedicado: com o PC desconectado, toque e segure 3 s na tela do tablet > \"Sair do modo dedicado\".",
                "TabletMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("Preparação interrompida: " + ex.Message);
            MessageBox.Show(this, ex.Message, "TabletMonitor — preparação do tablet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task InstallAppAsync()
    {
        var serial = await PickDeviceAsync();
        if (serial == null) return;
        await Task.Run(() => _adb.InstallOnly(serial));
    }

    private async Task ReenableAppsAsync()
    {
        if (_settings.DisabledPackages.Count == 0) { Log("Nenhum app foi desativado por este PC."); return; }
        var serial = await PickDeviceAsync();
        if (serial == null) return;
        var list = _settings.DisabledPackages.ToList();
        await Task.Run(() => _adb.ReenablePackages(serial, list));
        _settings.DisabledPackages.Clear();
        _settings.Save();
    }

    private async Task InstallVirtualDisplayAsync()
    {
        var r = MessageBox.Show(this,
            "Para o tablet funcionar como monitor ESTENDIDO, o Windows precisa de um monitor extra.\n\n" +
            "Vou instalar o \"Virtual Display Driver\" (código aberto, assinado) via winget. " +
            "O Windows vai pedir permissão de administrador.\n\n" +
            "Depois de instalado, ajuste a resolução do novo monitor para a do tablet em Configurações > Tela.\n\n" +
            "Alternativa sem driver: um plug HDMI \"dummy\" numa saída de vídeo livre.\n\nContinuar?",
            "Monitor virtual", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (r != DialogResult.OK) return;
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/k winget install --id=VirtualDrivers.Virtual-Display-Driver -e --accept-package-agreements --accept-source-agreements & echo. & echo Quando terminar, feche esta janela e clique em Atualizar no TabletMonitor.")
            { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("https://github.com/VirtualDrivers/Virtual-Display-Driver/releases") { UseShellExecute = true });
        }
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ helpers de UI

    private void Log(string text)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}";
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (_log.TextLength > 200_000) _log.Text = _log.Text[^100_000..];
            _log.AppendText(line);
        });
    }

    private void UpdateStatus(string text, Color color)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _status.Text = text;
            _status.ForeColor = color;
            _tray.Text = ("TabletMonitor — " + text).Length > 63 ? "TabletMonitor" : "TabletMonitor — " + text;
        });
    }

    private void SetHint(string text)
    {
        if (!IsHandleCreated) { _hint.Text = text; return; }
        BeginInvoke(() => _hint.Text = text);
    }
}

/// <summary>Confirmação da preparação do tablet com as opções.</summary>
public sealed class ProvisionDialog : Form
{
    private readonly CheckBox _debloat = new() { Text = "Desativar apps desnecessários (YouTube, Chrome, Gmail, apps do fabricante…) — reversível", AutoSize = true, Checked = true };
    private readonly CheckBox _playStore = new() { Text = "Desativar também a Play Store (evita atualizações em segundo plano)", AutoSize = true, Checked = false };

    public bool Debloat => _debloat.Checked;
    public bool DisablePlayStore => _playStore.Checked;

    public ProvisionDialog()
    {
        Text = "Preparar tablet — modo dedicado";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);

        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(14), MaximumSize = new Size(620, 0) };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(590, 0),
            Text = "Isto vai:\n" +
                   "• instalar o TabletMonitor no tablet;\n" +
                   "• torná-lo \"dono do dispositivo\": vira a tela inicial, sem barra de status, sem tela de bloqueio, sem como sair para outros apps;\n" +
                   "• deixar a tela sempre ligada quando estiver carregando e desligar animações.\n\n" +
                   "IMPORTANTE: o Android só permite isso se NÃO houver nenhuma conta (Google, Samsung…) no tablet. " +
                   "O ideal é restaurar de fábrica, pular o login e então ativar a depuração USB.\n",
        });
        panel.Controls.Add(_debloat);
        panel.Controls.Add(_playStore);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var ok = new Button { Text = "Preparar", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([ok, cancel]);
        panel.Controls.Add(buttons);
        Controls.Add(panel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
