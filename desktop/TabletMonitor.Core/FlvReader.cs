using System.Buffers.Binary;

namespace TabletMonitor.Core;

/// <summary>
/// Lê a saída FLV do ffmpeg (pipe) e entrega cada access unit H.264 já em Annex-B.
/// FLV foi escolhido porque cada tag traz o tamanho exato do quadro: dá para repassar o quadro
/// no instante em que ele termina, sem esperar o próximo start code (o que somaria 1 quadro de atraso).
/// </summary>
public sealed class FlvReader
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];
    private readonly Stream _stream;
    private int _nalLengthSize = 4;
    private byte[] _tag = new byte[256 * 1024];
    private readonly MemoryStream _annexB = new(256 * 1024);

    public FlvReader(Stream stream) => _stream = stream;

    /// <param name="onConfig">SPS/PPS em Annex-B (pode vir vazio se o encoder mandar in-band).</param>
    /// <param name="onFrame">(keyframe, pts em ms, access unit Annex-B)</param>
    public void Run(Action<byte[]> onConfig, Action<bool, long, ReadOnlyMemory<byte>> onFrame, CancellationToken ct)
    {
        Span<byte> header = stackalloc byte[9];
        if (!StreamUtil.ReadExact(_stream, header)) return;
        if (header[0] != 'F' || header[1] != 'L' || header[2] != 'V')
            throw new InvalidDataException("Saída do ffmpeg não é FLV");
        int dataOffset = BinaryPrimitives.ReadInt32BigEndian(header[5..]);
        if (dataOffset > 9) Skip(dataOffset - 9);
        Span<byte> prev = stackalloc byte[4];
        if (!StreamUtil.ReadExact(_stream, prev)) return;

        Span<byte> th = stackalloc byte[11];
        while (!ct.IsCancellationRequested)
        {
            if (!StreamUtil.ReadExact(_stream, th)) return;
            int tagType = th[0] & 0x1F;
            int size = (th[1] << 16) | (th[2] << 8) | th[3];
            long ts = (th[4] << 16) | (th[5] << 8) | th[6] | (th[7] << 24);
            if (_tag.Length < size) _tag = new byte[size + size / 2];
            var data = _tag.AsSpan(0, size);
            if (!StreamUtil.ReadExact(_stream, data)) return;
            if (!StreamUtil.ReadExact(_stream, prev)) return;

            if (tagType != 9 || size < 5) continue; // só vídeo
            int frameType = data[0] >> 4;
            int codecId = data[0] & 0x0F;
            if (codecId != 7) continue; // AVC
            int packetType = data[1];
            int cts = (data[2] << 16) | (data[3] << 8) | data[4];
            if ((cts & 0x800000) != 0) cts -= 0x1000000;
            var body = data[5..];

            if (packetType == 0)
            {
                onConfig(ParseAvcConfig(body));
            }
            else if (packetType == 1)
            {
                _annexB.SetLength(0);
                int pos = 0;
                while (pos + _nalLengthSize <= body.Length)
                {
                    int nalLen = 0;
                    for (int i = 0; i < _nalLengthSize; i++) nalLen = (nalLen << 8) | body[pos + i];
                    pos += _nalLengthSize;
                    if (nalLen <= 0 || pos + nalLen > body.Length) break;
                    _annexB.Write(StartCode);
                    _annexB.Write(body.Slice(pos, nalLen));
                    pos += nalLen;
                }
                if (_annexB.Length > 0)
                    onFrame(frameType == 1, ts + cts, _annexB.GetBuffer().AsMemory(0, (int)_annexB.Length));
            }
        }
    }

    /// <summary>AVCDecoderConfigurationRecord → SPS/PPS em Annex-B.</summary>
    private byte[] ParseAvcConfig(ReadOnlySpan<byte> rec)
    {
        using var ms = new MemoryStream();
        if (rec.Length < 7) return [];
        _nalLengthSize = (rec[4] & 0x03) + 1;
        int pos = 5;
        int numSps = rec[pos++] & 0x1F;
        for (int i = 0; i < numSps && pos + 2 <= rec.Length; i++)
        {
            int len = (rec[pos] << 8) | rec[pos + 1];
            pos += 2;
            if (pos + len > rec.Length) return ms.ToArray();
            ms.Write(StartCode);
            ms.Write(rec.Slice(pos, len));
            pos += len;
        }
        if (pos >= rec.Length) return ms.ToArray();
        int numPps = rec[pos++];
        for (int i = 0; i < numPps && pos + 2 <= rec.Length; i++)
        {
            int len = (rec[pos] << 8) | rec[pos + 1];
            pos += 2;
            if (pos + len > rec.Length) break;
            ms.Write(StartCode);
            ms.Write(rec.Slice(pos, len));
            pos += len;
        }
        return ms.ToArray();
    }

    private void Skip(int count)
    {
        var buf = new byte[count];
        StreamUtil.ReadExact(_stream, buf);
    }
}
