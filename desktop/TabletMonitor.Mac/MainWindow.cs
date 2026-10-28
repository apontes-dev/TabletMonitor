using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TabletMonitor.Core;

namespace TabletMonitor.Mac;

/// <summary>Janela principal do TabletMonitor para macOS (mesmas funções do app do Windows, sem Bluetooth).</summary>
public sealed class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly StreamServer _server = new();
    private readonly MacInput _input = new();
    private readonly Adb _adb;
    private DiscoveryBeacon? _beacon;
    private WebServer? _web;
    private string? _ffmpegPath;
    private List<string> _encoders = new();
    private List<MacDisplay> _displays = new();
    private bool _loading;
    public bool AllowExit { get; set; }

    private readonly TextBlock _status = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, Text = "Iniciando…" };
    private readonly TextBlock _hint = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _perm = new() { Foreground = new SolidColorBrush(Color.Parse("#C27C0E")), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _permButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ComboBox _display = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _encoder = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _fps = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _bitrate = new() { Minimum = 1, Maximum = 50, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _inputMode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _maxRes = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _drawMouse = new() { Content = "Mostrar o cursor no tablet" };
    private readonly CheckBox _usbAuto = new() { Content = "USB automático (adb reverse)" };
    private readonly CheckBox _webClient = new() { Content = "Cliente web (iPhone/iPad)" };
    private readonly CheckBox _startAtLogin = new() { Content = "Abrir ao iniciar a sessão" };
    private readonly Image _qr = new() { Width = 112, Height = 112, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _webInfo = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Menlo, Monaco, monospace"), FontSize = 11.5,
        Background = new SolidColorBrush(Color.Parse("#181A1E")), Foreground = new SolidColorBrush(Color.Parse("#DCDCDC")),
    };
    private static readonly int[] MaxWidths = [0, 2560, 1920, 1600, 1280, 1024];

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        _adb = new Adb(Log);
        Title = "TabletMonitor — segundo monitor no tablet";
        Width = 820; Height = 780; MinWidth = 640; MinHeight = 560;
        Icon = new WindowIcon(App.IconStream());

        BuildLayout();
        LoadSettings();

        _server.Log += Log;
        _server.PlanSession = PlanSession;
        _server.OnTouch = _input.Handle;
        _server.OnSessionEnd = _input.ReleaseAll;
        _server.ClientConnected += (hello, t) => SetStatus($"Conectado: {hello.Name} via {TransportName(t)}", "#2E8B57");
        _server.ClientDisconnected += () => SetStatus("Aguardando o tablet…", "#D2691E");
        _adb.DevicesChanged += d => Log(d.Count == 0 ? "USB: nenhum tablet conectado" : "USB: tablet(s): " + string.Join(", ", d));

        Closing += (_, e) =>
        {
            if (AllowExit) return;
            e.Cancel = true;
            Hide();
        };
        Opened += async (_, _) => await InitializeAsync();
    }

    // ------------------------------------------------------------------ layout

    private static TextBlock L(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

    private void BuildLayout()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            Margin = new Thickness(0, 8, 0, 4),
        };
        void Put(Control c, int row, int col, int span = 1)
        {
            Grid.SetRow(c, row); Grid.SetColumn(c, col); Grid.SetColumnSpan(c, span);
            c.Margin = new Thickness(0, 3, col % 2 == 1 ? 12 : 0, 3);
            grid.Children.Add(c);
        }
        var refresh = new Button { Content = "Atualizar" };
        refresh.Click += (_, _) => RefreshDisplays();
        var displayRow = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        refresh.Margin = new Thickness(8, 0, 0, 0);
        displayRow.Children.Add(refresh);
        displayRow.Children.Add(_display);

        Put(L("Tela a transmitir:"), 0, 0); Put(displayRow, 0, 1, 3);
        Put(L("Encoder:"), 1, 0); Put(_encoder, 1, 1); Put(L("Quadros/s:"), 1, 2); Put(_fps, 1, 3);
        Put(L("Bitrate (Mbps):"), 2, 0); Put(_bitrate, 2, 1); Put(L("Toque:"), 2, 2); Put(_inputMode, 2, 3);
        Put(L("Resolução máx.:"), 3, 0); Put(_maxRes, 3, 1);

        var checks = new WrapPanel();
        foreach (var c in new Control[] { _drawMouse, _usbAuto, _webClient, _startAtLogin }) { c.Margin = new Thickness(0, 2, 18, 2); checks.Children.Add(c); }

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        Button B(string text, Func<Task> action)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6) };
            b.Click += async (_, _) =>
            {
                b.IsEnabled = false;
                try { await action(); } catch (Exception ex) { Log("Erro: " + ex.Message); } finally { b.IsEnabled = true; }
            };
            return b;
        }
        buttons.Children.Add(B("Preparar tablet (modo dedicado)…", ProvisionAsync));
        buttons.Children.Add(B("Atualizar app no tablet", InstallAppAsync));
        buttons.Children.Add(B("Criar tela virtual (BetterDisplay)…", VirtualDisplayAsync));
        buttons.Children.Add(B("Reconectar tablet", () => { _server.DisconnectClient(); return Task.CompletedTask; }));
        buttons.Children.Add(B("Reativar apps do tablet", ReenableAsync));

        var web = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_qr, Dock.Left);
        web.Children.Add(_qr);
        web.Children.Add(_webInfo);

        var top = new StackPanel { Spacing = 2 };
        top.Children.Add(_status);
        top.Children.Add(_hint);
        top.Children.Add(_perm);
        top.Children.Add(_permButtons);
        top.Children.Add(grid);
        top.Children.Add(checks);
        top.Children.Add(buttons);
        top.Children.Add(web);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_log);
        Content = root;

        _fps.ItemsSource = new[] { "30", "60" };
        _inputMode.ItemsSource = new[] { "Toque → mouse e trackpad", "Mouse simples (1 dedo)", "Desligado (só imagem)" };
        _maxRes.ItemsSource = new[] { "Automática (o que o tablet aguenta)", "2560 de largura", "1920 de largura", "1600 de largura", "1280 de largura", "1024 de largura" };
        _encoder.ItemsSource = new List<string> { "Auto" };

        _display.SelectionChanged += (_, _) => Changed(true);
        _encoder.SelectionChanged += (_, _) => Changed(true);
        _fps.SelectionChanged += (_, _) => Changed(true);
        _bitrate.ValueChanged += (_, _) => Changed(true);
        _maxRes.SelectionChanged += (_, _) => Changed(true);
        _drawMouse.IsCheckedChanged += (_, _) => Changed(true);
        _inputMode.SelectionChanged += (_, _) => Changed(false);
        _usbAuto.IsCheckedChanged += (_, _) => { Changed(false); _ = ApplyUsbAsync(); };
        _webClient.IsCheckedChanged += (_, _) => { Changed(false); ApplyWebClient(); };
        _startAtLogin.IsCheckedChanged += (_, _) => { Changed(false); LoginItem.Apply(_settings.StartWithWindows, Log); };
    }

    private void LoadSettings()
    {
        _loading = true;
        _fps.SelectedItem = _settings.Fps >= 60 ? "60" : "30";
        _bitrate.Value = Math.Clamp(_settings.BitrateKbps / 1000, 1, 50);
        _inputMode.SelectedIndex = (int)_settings.Input;
        _maxRes.SelectedIndex = Math.Max(0, Array.IndexOf(MaxWidths, _settings.MaxWidth));
        _drawMouse.IsChecked = _settings.DrawMouse;
        _usbAuto.IsChecked = _settings.UsbAuto;
        _webClient.IsChecked = _settings.WebClient;
        _startAtLogin.IsChecked = _settings.StartWithWindows;
        _encoder.SelectedIndex = 0;
        _input.Mode = _settings.Input;
        _loading = false;
    }

    private void Changed(bool reconnect)
    {
        if (_loading) return;
        if (_display.SelectedItem is MacDisplay d) _settings.MonitorDeviceName = d.Key;
        _settings.Encoder = _encoder.SelectedItem as string ?? "Auto";
        _settings.Fps = _fps.SelectedItem as string == "30" ? 30 : 60;
        _settings.BitrateKbps = (int)(_bitrate.Value ?? 10) * 1000;
        _settings.Input = (InputMode)Math.Max(0, _inputMode.SelectedIndex);
        _settings.MaxWidth = MaxWidths[Math.Max(0, _maxRes.SelectedIndex)];
        _settings.DrawMouse = _drawMouse.IsChecked == true;
        _settings.UsbAuto = _usbAuto.IsChecked == true;
        _settings.WebClient = _webClient.IsChecked == true;
        _settings.StartWithWindows = _startAtLogin.IsChecked == true; // no Mac: "abrir ao iniciar a sessão"
        _input.Mode = _settings.Input;
        _settings.Save();
        if (reconnect && _server.HasClient) _server.DisconnectClient();
    }

    private void RefreshDisplays()
    {
        try { _displays = MacDisplay.Enumerate(); }
        catch (Exception ex) { Log("Falha ao listar telas: " + ex.Message); return; }
        _loading = true;
        _display.ItemsSource = _displays;
        var pick = MacDisplay.Pick(_displays, _settings.MonitorDeviceName);
        _display.SelectedItem = pick;
        _loading = false;
        if (pick != null) _settings.MonitorDeviceName = pick.Key;
        if (_displays.Count <= 1)
            SetHint("Só há 1 tela. Para estender a área de trabalho, crie uma tela virtual (BetterDisplay, grátis) ou use um plug HDMI \"dummy\". Com 1 tela, o tablet espelha a principal.");
        else if (pick is { IsMain: true })
            SetHint("Atenção: a tela escolhida é a principal — o tablet vai espelhar sua tela.");
        else
            SetHint("Dica: em Ajustes do Sistema > Telas > Organizar, posicione a tela do tablet ao lado da principal.");
    }

    private void RefreshPermissions()
    {
        _permButtons.Children.Clear();
        var missing = new List<string>();
        if (!MacNative.ScreenCaptureAllowed())
        {
            missing.Add("Gravação de Tela (para capturar a imagem)");
            var b = new Button { Content = "Permitir gravação de tela" };
            b.Click += (_, _) => { MacNative.CGRequestScreenCaptureAccess(); MacNative.OpenSettings("Privacy_ScreenCapture"); };
            _permButtons.Children.Add(b);
        }
        if (_settings.Input != InputMode.Off && !MacNative.AccessibilityAllowed())
        {
            missing.Add("Acessibilidade (para o toque controlar o mouse)");
            var b = new Button { Content = "Permitir acessibilidade" };
            b.Click += (_, _) => MacNative.OpenSettings("Privacy_Accessibility");
            _permButtons.Children.Add(b);
        }
        if (missing.Count > 0)
        {
            var again = new Button { Content = "Verificar de novo" };
            again.Click += (_, _) => RefreshPermissions();
            _permButtons.Children.Add(again);
        }
        _perm.Text = missing.Count == 0 ? "" :
            "O macOS precisa de permissão para: " + string.Join("; ", missing) +
            ". Depois de permitir em Ajustes do Sistema > Privacidade e Segurança, feche e abra o TabletMonitor.";
    }

    // ------------------------------------------------------------------ inicialização

    private async Task InitializeAsync()
    {
        RefreshDisplays();
        RefreshPermissions();

        _ffmpegPath = FfmpegTool.Find();
        if (_ffmpegPath == null)
        {
            Log("ffmpeg não encontrado — baixando (captura e compressão da tela, ~80 MB, só uma vez)…");
            Log("Dica: no Apple Silicon, \"brew install ffmpeg\" dá uma versão nativa mais rápida.");
            try { _ffmpegPath = await FfmpegTool.DownloadAsync(Log); }
            catch (Exception ex)
            {
                Log(ex.Message);
                Log("Instale com Homebrew: brew install ffmpeg — e reabra o TabletMonitor.");
                SetStatus("ffmpeg ausente", "#B22222");
                return;
            }
        }
        Log("ffmpeg: " + _ffmpegPath);
        await Task.Run(() =>
        {
            if (!MacCapture.HasAvfoundation(_ffmpegPath)) Log("AVISO: este ffmpeg não tem captura avfoundation.");
            _encoders = FfmpegTool.ProbeEncoders(_ffmpegPath, MacCapture.EncoderPriority);
        });
        Log("Encoders disponíveis: " + (_encoders.Count > 0 ? string.Join(", ", _encoders) : "nenhum!"));
        _loading = true;
        var list = new List<string> { "Auto" };
        list.AddRange(_encoders);
        _encoder.ItemsSource = list;
        _encoder.SelectedItem = list.Contains(_settings.Encoder) ? _settings.Encoder : "Auto";
        _loading = false;

        try
        {
            _server.Start(_settings.Port);
            _beacon = new DiscoveryBeacon(_settings.Port);
        }
        catch (Exception ex)
        {
            Log($"Não foi possível abrir a porta {_settings.Port}: {ex.Message}");
            SetStatus("Erro na porta de rede", "#B22222");
            return;
        }
        Log("Se o macOS perguntar sobre conexões de rede ou rede local, permita (Wi-Fi e iPhone/iPad).");
        SetStatus("Aguardando o tablet…", "#D2691E");
        await ApplyUsbAsync();
        ApplyWebClient();
    }

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
        if (adb != null) _adb.StartAutoReverse(adb, _settings.Port);
    }

    private void ApplyWebClient()
    {
        if (!_server.IsRunning) return;
        if (!_settings.WebClient)
        {
            _web?.Dispose();
            _web = null;
            _qr.Source = null;
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
        catch (Exception ex) { Log("Cliente web: " + ex.Message); _web = null; return; }
        string url = $"http://{CertManager.BestLanAddress()?.ToString() ?? "IP-do-Mac"}:{_web.HttpPort}/";
        try
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
            _qr.Source = new Bitmap(new MemoryStream(new QRCoder.PngByteQRCode(data).GetGraphic(6)));
        }
        catch { }
        _webInfo.Text = "iPhone / iPad / outro tablet: aponte a câmera para o QR code ou abra no Safari:\n" + url +
                        "\nA página ensina a instalar o certificado (vídeo H.264) e a adicionar à Tela de Início.\n" +
                        "Dica: iPad recente + Mac recente também têm o Sidecar, nativo da Apple.";
    }

    // ------------------------------------------------------------------ sessão

    private SessionPlan PlanSession(HelloInfo hello, Transport transport)
    {
        if (_ffmpegPath == null) throw new InvalidOperationException("ffmpeg ainda não está pronto");
        List<MacDisplay> displays;
        try { displays = MacDisplay.Enumerate(); } catch { displays = _displays; }
        var d = MacDisplay.Pick(displays, _settings.MonitorDeviceName) ?? throw new InvalidOperationException("Nenhuma tela encontrada");

        string encoder = _settings.Encoder;
        if (encoder == "Auto" || !_encoders.Contains(encoder)) encoder = _encoders.FirstOrDefault() ?? "libx264";
        var p = SessionProfile.For(hello, transport, _settings);

        // Captura em pixels físicos; a proporção é a mesma dos pontos.
        var (w, h) = VideoArgs.Cap(d.PixelWidth, d.PixelHeight, p.MaxW, p.MaxH);
        var args = MacCapture.Build(d, encoder, w, h, p.Fps, p.Kbps, _settings.DrawMouse, p.GopSeconds, p.Jpeg);
        _input.Target = d.Bounds;
        Log($"Transmitindo {d} com {(p.Jpeg ? "JPEG" : encoder)}, {w}x{h}@{p.Fps}");

        var (iw, ih) = SessionProfile.Ideal(hello);
        double aspectScreen = d.Bounds.Size.Width / d.Bounds.Size.Height, aspectTab = (double)hello.Width / hello.Height;
        if (Math.Abs(aspectScreen - aspectTab) > 0.02)
            SetHint($"Para imagem sem bordas, deixe a tela transmitida na proporção do tablet (ex.: {iw}x{ih} no BetterDisplay). Agora: {d.Bounds.Size.Width:0}x{d.Bounds.Size.Height:0}.");
        else
            SetHint("Proporção ideal. Dica: em Ajustes do Sistema > Telas, posicione a tela do tablet ao lado da principal.");

        return new SessionPlan(_ffmpegPath, args, w, h, p.Fps, p.QueueFrames, p.LatencyMs, p.Jpeg ? Codec.Jpeg : Codec.H264);
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
            await Dialogs.Info(this, "Nenhum tablet encontrado via USB",
                "1. No tablet: Configurações > Sobre o tablet > toque 7x em \"Número da versão\".\n" +
                "2. Configurações > Sistema > Opções do desenvolvedor > ative \"Depuração USB\".\n" +
                "3. Conecte o cabo e toque em \"Permitir\" (marque \"Sempre permitir deste computador\").");
            return null;
        }
        return devices[0];
    }

    private async Task ProvisionAsync()
    {
        var serial = await PickDeviceAsync();
        if (serial == null) return;
        var opt = await Dialogs.Provision(this);
        if (opt == null) return;
        try
        {
            var disabled = await Task.Run(() => _adb.Provision(serial, opt));
            foreach (var p in disabled) if (!_settings.DisabledPackages.Contains(p)) _settings.DisabledPackages.Add(p);
            _settings.Save();
            await Dialogs.Info(this, "Tablet preparado!", "Ele agora abre direto no TabletMonitor e não sai mais dele.\n\n" +
                "Para sair do modo dedicado: com o computador desconectado, toque e segure 3 s na tela do tablet > \"Sair do modo dedicado\".");
        }
        catch (Exception ex)
        {
            Log("Preparação interrompida: " + ex.Message);
            await Dialogs.Info(this, "Preparação do tablet", ex.Message);
        }
    }

    private async Task InstallAppAsync()
    {
        var serial = await PickDeviceAsync();
        if (serial != null) await Task.Run(() => _adb.InstallOnly(serial));
    }

    private async Task ReenableAsync()
    {
        if (_settings.DisabledPackages.Count == 0) { Log("Nenhum app foi desativado por este computador."); return; }
        var serial = await PickDeviceAsync();
        if (serial == null) return;
        var list = _settings.DisabledPackages.ToList();
        await Task.Run(() => _adb.ReenablePackages(serial, list));
        _settings.DisabledPackages.Clear();
        _settings.Save();
    }

    private async Task VirtualDisplayAsync()
    {
        await Dialogs.Info(this, "Tela virtual no Mac",
            "O macOS não cria telas extras sozinho. O BetterDisplay (grátis para isso) cria uma tela virtual:\n\n" +
            "1. Instale: brew install --cask betterdisplay (ou baixe na página que vai abrir).\n" +
            "2. No ícone do BetterDisplay na barra de menus: Create New Virtual Screen.\n" +
            "3. Escolha a proporção do tablet (16:10 para o SM-T520) e conecte a tela.\n" +
            "4. Clique em Atualizar aqui no TabletMonitor.\n\n" +
            "Alternativa sem programa: um plug HDMI \"dummy\" numa saída de vídeo do Mac.");
        MacNative.Open("https://github.com/waydabber/BetterDisplay/releases");
    }

    // ------------------------------------------------------------------ helpers

    private static string TransportName(Transport t) => t switch
    {
        Transport.Usb => "USB", Transport.Wifi => "Wi-Fi", Transport.Web => "navegador", _ => "Bluetooth",
    };

    public void Log(string text)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {text}\n";
        Dispatcher.UIThread.Post(() =>
        {
            var t = _log.Text ?? "";
            if (t.Length > 200_000) t = t[^100_000..];
            _log.Text = t + line;
            _log.CaretIndex = _log.Text.Length;
        });
    }

    private void SetStatus(string text, string color) => Dispatcher.UIThread.Post(() =>
    {
        _status.Text = text;
        _status.Foreground = new SolidColorBrush(Color.Parse(color));
        App.Current?.SetTrayTooltip("TabletMonitor — " + text);
    });

    private void SetHint(string text) => Dispatcher.UIThread.Post(() => _hint.Text = text);

    public void Shutdown()
    {
        _settings.Save();
        _adb.Dispose();
        _web?.Dispose();
        _beacon?.Dispose();
        _server.Dispose();
        _input.Dispose();
    }
}
