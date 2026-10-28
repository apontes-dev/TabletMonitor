using TabletMonitor.Core;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace TabletMonitor;

/// <summary>
/// Publica o serviço TabletMonitor via Bluetooth (RFCOMM/SPP). O tablet, já pareado com o PC,
/// conecta nele quando não acha o PC por USB nem por Wi-Fi. A banda é pequena (~1–2 Mbps),
/// então a sessão Bluetooth usa um perfil mais leve (menos quadros/s e bitrate menor).
/// </summary>
public sealed class BluetoothServer : IDisposable
{
    /// <summary>Mesmo UUID do app Android (Protocol.kt).</summary>
    public static readonly Guid ServiceUuid = new("5b8c9e3a-1f47-4d2e-a6b1-7c0e3d9f2a64");

    private readonly StreamServer _server;
    private readonly Action<string> _log;
    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;

    public bool IsRunning => _provider != null;

    public BluetoothServer(StreamServer server, Action<string> log)
    {
        _server = server;
        _log = log;
    }

    public async Task StartAsync()
    {
        if (_provider != null) return;
        try
        {
            _provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(ServiceUuid));
        }
        catch (Exception ex)
        {
            _provider = null;
            _log("Bluetooth indisponível neste PC (sem adaptador ou desligado): " + ex.Message);
            return;
        }

        _listener = new StreamSocketListener();
        _listener.ConnectionReceived += OnConnection;
        await _listener.BindServiceNameAsync(_provider.ServiceId.AsString(),
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

        // Atributo SDP "nome do serviço" (0x100), para aparecer bonitinho no tablet.
        var writer = new DataWriter();
        var name = System.Text.Encoding.UTF8.GetBytes("TabletMonitor");
        writer.WriteByte((4 << 3) | 5); // tipo string, tamanho em 1 byte
        writer.WriteByte((byte)name.Length);
        writer.WriteBytes(name);
        _provider.SdpRawAttributes.Add(0x100, writer.DetachBuffer());

        _provider.StartAdvertising(_listener, true);
        _log("Bluetooth: aguardando o tablet (pareie o tablet com este PC uma vez nas configurações de Bluetooth)");
    }

    private void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        var socket = args.Socket;
        string name = socket.Information.RemoteHostName?.DisplayName ?? "tablet";
        var stream = new DuplexStream(socket.InputStream.AsStreamForRead(0), socket.OutputStream.AsStreamForWrite(0));
        _server.AcceptExternal(stream, "Bluetooth " + name, Transport.Bluetooth, socket.Dispose);
    }

    public void Stop()
    {
        try { _provider?.StopAdvertising(); } catch { }
        try { _listener?.Dispose(); } catch { }
        _provider = null;
        _listener = null;
    }

    public void Dispose() => Stop();
}

/// <summary>Junta os fluxos de leitura e escrita do socket WinRT num único Stream.</summary>
internal sealed class DuplexStream : Stream
{
    private readonly Stream _read;
    private readonly Stream _write;

    public DuplexStream(Stream read, Stream write)
    {
        _read = read;
        _write = write;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _read.Read(buffer);
    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _write.Write(buffer);
    public override void Flush() => _write.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _read.Dispose(); } catch { }
            try { _write.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
