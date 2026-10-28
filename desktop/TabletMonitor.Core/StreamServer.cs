using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TabletMonitor.Core;

/// <summary>O que rodar para um tablet que acabou de se conectar.</summary>
/// <param name="QueueFrames">Quadros aguardando envio antes de descartar.</param>
/// <param name="MaxLatencyMs">Quadros que esperaram mais que isso para sair são descartados.</param>
/// <param name="Codec">H.264 (FLV na saída do ffmpeg) ou JPEG (MJPEG cru).</param>
public sealed record SessionPlan(string FfmpegPath, IReadOnlyList<string> Args, int Width, int Height, int Fps,
    int QueueFrames = 3, int MaxLatencyMs = 250, Codec Codec = Codec.H264);

/// <summary>
/// Servidor: aceita um tablet por vez — por TCP (USB via adb reverse, ou Wi-Fi) ou por uma conexão
/// entregue de fora (Bluetooth RFCOMM) —, lança o ffmpeg com o plano definido pela UI,
/// repassa o vídeo e devolve os toques para quem estiver injetando no Windows.
/// </summary>
public sealed class StreamServer : IDisposable
{
    public event Action<string>? Log;
    public event Action<HelloInfo, Transport>? ClientConnected;
    public event Action? ClientDisconnected;

    /// <summary>Decide resolução/encoder/argumentos do ffmpeg para o tablet recém-conectado.</summary>
    public Func<HelloInfo, Transport, SessionPlan>? PlanSession { get; set; }
    public Action<TouchEvent>? OnTouch { get; set; }
    public Action? OnSessionEnd { get; set; }

    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _running;
    private Session? _current;
    private readonly object _sessionLock = new();

    public int Port { get; private set; }
    public bool IsRunning => _running;
    public bool HasClient => _current != null;
    public Transport? CurrentTransport => _current?.Transport;

    public void Start(int port)
    {
        Stop();
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "accept" };
        _acceptThread.Start();
        Log?.Invoke($"Servidor ouvindo na porta {port} (USB e Wi-Fi)");
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        DisconnectClient();
    }

    public void DisconnectClient()
    {
        Session? s;
        lock (_sessionLock) { s = _current; _current = null; }
        s?.Cancel();
        s?.Join(2000);
    }

    /// <summary>Recebe uma conexão que chegou por outro meio (ex.: Bluetooth RFCOMM).</summary>
    public void AcceptExternal(Stream stream, string endpoint, Transport transport, Action close)
    {
        StartSession(new Session(this, stream, endpoint, transport, close));
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = _listener!.AcceptTcpClient(); }
            catch { if (_running) Thread.Sleep(200); continue; }

            try
            {
                client.NoDelay = true;
                // Buffer pequeno: o que não couber fica na fila, onde pode ser descartado (buffer grande = atraso).
                client.SendBufferSize = 64 * 1024;
            }
            catch { }
            var ep = client.Client.RemoteEndPoint as IPEndPoint;
            // adb reverse entrega a conexão do tablet como se viesse do próprio PC.
            var transport = ep != null && IPAddress.IsLoopback(ep.Address) ? Transport.Usb : Transport.Wifi;
            StartSession(new Session(this, client.GetStream(), ep?.ToString() ?? "?", transport, client.Close));
        }
    }

    private void StartSession(Session session)
    {
        // Um tablet por vez: o novo derruba o anterior.
        DisconnectClient();
        lock (_sessionLock) _current = session;
        session.Start();
    }

    private void SessionEnded(Session s)
    {
        lock (_sessionLock) { if (_current == s) _current = null; }
        try { OnSessionEnd?.Invoke(); } catch { }
        ClientDisconnected?.Invoke();
    }

    public void Dispose() => Stop();

    private sealed class Session
    {
        private readonly StreamServer _server;
        private readonly Stream _stream;
        private readonly string _endpoint;
        private readonly Action _close;
        private readonly CancellationTokenSource _cts = new();
        private Process? _ffmpeg;
        private Thread? _thread;
        private SendQueue? _queue;

        public Transport Transport { get; }

        public Session(StreamServer server, Stream stream, string endpoint, Transport transport, Action close)
        {
            _server = server;
            _stream = stream;
            _endpoint = endpoint;
            Transport = transport;
            _close = close;
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "session", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void Cancel()
        {
            try { _cts.Cancel(); } catch { }
            _queue?.Close();
            try { _close(); } catch { }
            try { _stream.Dispose(); } catch { }
            KillFfmpeg();
        }

        public void Join(int ms) => _thread?.Join(ms);

        private void KillFfmpeg()
        {
            try { if (_ffmpeg is { HasExited: false }) _ffmpeg.Kill(entireProcessTree: true); } catch { }
        }

        private static string Name(Transport t) => t switch
        {
            Transport.Usb => "USB",
            Transport.Wifi => "Wi-Fi",
            Transport.Web => "navegador",
            _ => "Bluetooth",
        };

        private void Run()
        {
            var log = _server.Log;
            try
            {
                // O tablet tem alguns segundos para se apresentar.
                using var helloTimeout = new Timer(_ => Cancel(), null, 8000, Timeout.Infinite);
                var hello = PacketReader.ReadHello(_stream);
                helloTimeout.Change(Timeout.Infinite, Timeout.Infinite);

                var plan = _server.PlanSession?.Invoke(hello, Transport) ?? throw new InvalidOperationException("Sem plano de sessão");
                log?.Invoke($"Tablet conectado via {Name(Transport)}: {hello.Name} (tela {hello.Width}x{hello.Height}, " +
                            $"decodifica até {(hello.MaxDecodeWidth > 0 ? $"{hello.MaxDecodeWidth}x{hello.MaxDecodeHeight}" : "?")}) " +
                            $"→ vídeo {plan.Width}x{plan.Height}@{plan.Fps}");
                _server.ClientConnected?.Invoke(hello, Transport);

                var writer = new PacketWriter(_stream);
                var queue = _queue = new SendQueue(plan.QueueFrames, plan.MaxLatencyMs);
                using var reg = _cts.Token.Register(Cancel);

                // Thread que escreve no socket: o ffmpeg nunca fica esperando a rede.
                var sender = new Thread(() =>
                {
                    try
                    {
                        while (queue.TryTake(out var type, out var data)) writer.Write(type, data);
                    }
                    catch { }
                    Cancel();
                }) { IsBackground = true, Name = "session-send", Priority = ThreadPriority.AboveNormal };
                sender.Start();

                var input = new Thread(() => InputLoop(writer)) { IsBackground = true, Name = "session-input" };
                input.Start();

                var psi = new ProcessStartInfo(plan.FfmpegPath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in plan.Args) psi.ArgumentList.Add(a);
                _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg");
                try { _ffmpeg.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
                _ffmpeg.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke("ffmpeg: " + e.Data); };
                _ffmpeg.BeginErrorReadLine();

                // CONFIG só sai junto com o primeiro dado de vídeo: se o ffmpeg falhar, o tablet continua na tela de espera.
                bool configSent = false;
                void EnsureConfig()
                {
                    if (configSent) return;
                    configSent = true;
                    queue.EnqueueControl(Protocol.Config, PacketWriter.ConfigPayload(plan.Width, plan.Height, plan.Fps, plan.Codec));
                }

                long lastDropLog = 0;
                void OnFrame(bool key, long ptsMicros, ReadOnlySpan<byte> data)
                {
                    EnsureConfig();
                    queue.EnqueueFrame(key, ptsMicros, data);
                    if (queue.Dropped - lastDropLog >= 30)
                    {
                        lastDropLog = queue.Dropped;
                        log?.Invoke($"Conexão lenta: {queue.Dropped} quadros descartados até agora (baixe o bitrate ou os quadros/s)");
                    }
                }

                var ffout = _ffmpeg.StandardOutput.BaseStream;
                if (plan.Codec == Codec.Jpeg)
                {
                    // Cada JPEG é independente: todo quadro é "keyframe" e pode ser descartado sem corromper os seguintes.
                    var sw = Stopwatch.StartNew();
                    new MjpegReader(ffout).Run(jpeg => OnFrame(true, sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency, jpeg.Span), _cts.Token);
                }
                else
                {
                    new FlvReader(ffout).Run(
                        cfg => { EnsureConfig(); if (cfg.Length > 0) queue.EnqueueControl(Protocol.Csd, cfg); },
                        (key, ptsMs, au) => OnFrame(key, ptsMs * 1000, au.Span),
                        _cts.Token);
                }

                if (!_cts.IsCancellationRequested)
                {
                    _ffmpeg.WaitForExit(1000);
                    string code = _ffmpeg.HasExited ? _ffmpeg.ExitCode.ToString() : "?";
                    log?.Invoke($"ffmpeg terminou (código {code}) após {queue.Sent} quadros");
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException or OperationCanceledException)
            {
                if (!_cts.IsCancellationRequested) log?.Invoke($"Conexão encerrada: {ex.Message}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Erro na sessão: {ex.Message}");
            }
            finally
            {
                Cancel();
                _server.SessionEnded(this);
                log?.Invoke("Tablet desconectado");
            }
        }

        private void InputLoop(PacketWriter writer)
        {
            try
            {
                var reader = new PacketReader(_stream);
                while (!_cts.IsCancellationRequested && reader.TryRead(out var type, out var payload))
                {
                    switch (type)
                    {
                        case Protocol.Touch when payload.Length >= 6:
                            try { _server.OnTouch?.Invoke(PacketReader.ParseTouch(payload.Span)); }
                            catch (Exception ex) { _server.Log?.Invoke("Toque: " + ex.Message); }
                            break;
                        case Protocol.Ping:
                            _queue?.EnqueueControl(Protocol.Pong, payload.Span);
                            break;
                    }
                }
            }
            catch { }
            Cancel();
        }
    }
}
