using System.Buffers.Binary;
using System.Text;

namespace TabletMonitor.Core;

/// <summary>
/// Protocolo TabletMonitor v1 (TCP, big-endian). Veja Protocol.kt no app Android — os dois lados precisam bater.
/// </summary>
public static class Protocol
{
    public const int DefaultPort = 27183;
    public const int DiscoveryPort = 27184;
    public static ReadOnlySpan<byte> Magic => "TMON"u8;
    /// <summary>
    /// v2: HELLO inclui o tamanho máximo que o decodificador aguenta.
    /// v3: HELLO inclui os codecs que o cliente decodifica (cliente web: H.264 e/ou JPEG).
    /// CONFIG ganhou um byte de codec no fim (clientes antigos leem só os 6 primeiros bytes).
    /// </summary>
    public const byte Version = 3;

    public const byte Hello = 0x00;
    public const byte Config = 0x01;
    public const byte Csd = 0x02;
    public const byte Frame = 0x03;
    public const byte Touch = 0x10;
    public const byte Ping = 0x20;
    public const byte Pong = 0x21;

    public const int MaxPacket = 16 * 1024 * 1024;
}

/// <param name="MaxDecodeWidth">Maior largura que o decodificador do tablet aguenta (0 = desconhecido).</param>
/// <param name="Codecs">Codecs que o cliente decodifica (v3). Clientes v1/v2 só entendem H.264.</param>
public sealed record HelloInfo(int Version, int Width, int Height, int Dpi, string Name, int MaxDecodeWidth = 0, int MaxDecodeHeight = 0,
    CodecSupport Codecs = CodecSupport.H264);

/// <summary>Por onde o tablet está ligado. Web = navegador (iPhone/iPad) via WebSocket.</summary>
public enum Transport { Usb, Wifi, Bluetooth, Web }

public enum Codec : byte { H264 = 0, Jpeg = 1 }

[Flags]
public enum CodecSupport : byte { None = 0, H264 = 1, Jpeg = 2 }

public enum TouchAction : byte { Down = 0, Move = 1, Up = 2, Cancel = 3 }

/// <summary>Toque vindo do tablet. X/Y normalizados em 0..1 relativos à imagem transmitida.</summary>
public readonly record struct TouchEvent(TouchAction Action, int PointerId, double X, double Y);

/// <summary>Escrita de pacotes com trava (vídeo e respostas podem sair de threads diferentes).</summary>
public sealed class PacketWriter
{
    private readonly Stream _stream;
    private readonly object _lock = new();
    private readonly byte[] _header = new byte[5];

    public PacketWriter(Stream stream) => _stream = stream;

    public void Write(byte type, ReadOnlySpan<byte> payload) => Write(type, ReadOnlySpan<byte>.Empty, payload);

    public void Write(byte type, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> payload)
    {
        lock (_lock)
        {
            _header[0] = type;
            BinaryPrimitives.WriteInt32BigEndian(_header.AsSpan(1), prefix.Length + payload.Length);
            _stream.Write(_header);
            if (!prefix.IsEmpty) _stream.Write(prefix);
            if (!payload.IsEmpty) _stream.Write(payload);
            _stream.Flush();
        }
    }

    public static byte[] ConfigPayload(int width, int height, int fps, Codec codec)
    {
        var p = new byte[7];
        BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)fps);
        p[6] = (byte)codec;
        return p;
    }

    public void WriteFrame(bool keyFrame, long ptsMicros, ReadOnlySpan<byte> annexB)
    {
        Span<byte> prefix = stackalloc byte[9];
        prefix[0] = (byte)(keyFrame ? 1 : 0);
        BinaryPrimitives.WriteInt64BigEndian(prefix[1..], ptsMicros);
        Write(Protocol.Frame, prefix, annexB);
    }
}

/// <summary>Leitura de pacotes.</summary>
public sealed class PacketReader
{
    private readonly Stream _stream;
    private byte[] _buffer = new byte[4096];
    private readonly byte[] _header = new byte[5];

    public PacketReader(Stream stream) => _stream = stream;

    /// <summary>Lê o próximo pacote. Retorna false no fim do fluxo.</summary>
    public bool TryRead(out byte type, out ReadOnlyMemory<byte> payload)
    {
        type = 0;
        payload = default;
        if (!StreamUtil.ReadExact(_stream, _header)) return false;
        type = _header[0];
        int len = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(1));
        if (len < 0 || len > Protocol.MaxPacket) throw new InvalidDataException($"Pacote inválido ({len} bytes)");
        if (_buffer.Length < len) _buffer = new byte[len];
        if (!StreamUtil.ReadExact(_stream, _buffer.AsSpan(0, len))) return false;
        payload = _buffer.AsMemory(0, len);
        return true;
    }

    public static HelloInfo ReadHello(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        if (!StreamUtil.ReadExact(stream, magic) || !magic.SequenceEqual(Protocol.Magic))
            throw new InvalidDataException("Cliente não é um TabletMonitor");
        var reader = new PacketReader(stream);
        if (!reader.TryRead(out var type, out var payload) || type != Protocol.Hello || payload.Length < 7)
            throw new InvalidDataException("HELLO ausente");
        var p = payload.Span;
        int version = p[0];
        int w = BinaryPrimitives.ReadUInt16BigEndian(p[1..]);
        int h = BinaryPrimitives.ReadUInt16BigEndian(p[3..]);
        int dpi = BinaryPrimitives.ReadUInt16BigEndian(p[5..]);
        if (version >= 3 && payload.Length >= 12)
        {
            int mw = BinaryPrimitives.ReadUInt16BigEndian(p[7..]);
            int mh = BinaryPrimitives.ReadUInt16BigEndian(p[9..]);
            var codecs = (CodecSupport)p[11];
            if (codecs == CodecSupport.None) codecs = CodecSupport.H264;
            return new HelloInfo(version, w, h, dpi, Encoding.UTF8.GetString(p[12..]), mw, mh, codecs);
        }
        if (version >= 2 && payload.Length >= 11)
        {
            int mw = BinaryPrimitives.ReadUInt16BigEndian(p[7..]);
            int mh = BinaryPrimitives.ReadUInt16BigEndian(p[9..]);
            return new HelloInfo(version, w, h, dpi, Encoding.UTF8.GetString(p[11..]), mw, mh);
        }
        return new HelloInfo(version, w, h, dpi, Encoding.UTF8.GetString(p[7..]));
    }

    public static TouchEvent ParseTouch(ReadOnlySpan<byte> p) => new(
        (TouchAction)p[0],
        p[1],
        BinaryPrimitives.ReadUInt16BigEndian(p[2..]) / 65535.0,
        BinaryPrimitives.ReadUInt16BigEndian(p[4..]) / 65535.0);
}

public static class StreamUtil
{
    public static bool ReadExact(Stream s, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer[read..]);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
