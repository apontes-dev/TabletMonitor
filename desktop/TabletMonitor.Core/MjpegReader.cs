namespace TabletMonitor.Core;

/// <summary>
/// Separa os quadros da saída MJPEG crua do ffmpeg (-f mjpeg): cada JPEG vai de FF D8 até FF D9.
/// O fim do quadro é conhecido no próprio quadro (marcador EOI), então ele sai sem esperar o próximo.
/// </summary>
public sealed class MjpegReader
{
    private readonly Stream _stream;
    private byte[] _buf = new byte[512 * 1024];
    private int _len;

    public MjpegReader(Stream stream) => _stream = stream;

    public void Run(Action<ReadOnlyMemory<byte>> onFrame, CancellationToken ct)
    {
        int scan = 0; // até onde já procuramos o fim do quadro atual
        while (!ct.IsCancellationRequested)
        {
            if (_len == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            int n = _stream.Read(_buf, _len, _buf.Length - _len);
            if (n <= 0) return;
            _len += n;

            while (true)
            {
                // Início: FF D8 na posição 0 (descarta lixo antes dele).
                int start = IndexOf(0xD8, 0);
                if (start < 0) { KeepTail(); scan = 0; break; }
                if (start > 0) { Consume(start); scan = 0; }
                int end = IndexOf(0xD9, Math.Max(2, scan));
                if (end < 0) { scan = Math.Max(2, _len - 1); break; }
                int frameLen = end + 2;
                onFrame(_buf.AsMemory(0, frameLen));
                Consume(frameLen);
                scan = 0;
            }
        }
    }

    /// <summary>Posição do marcador FF xx a partir de "from".</summary>
    private int IndexOf(byte marker, int from)
    {
        for (int i = Math.Max(0, from); i + 1 < _len; i++)
            if (_buf[i] == 0xFF && _buf[i + 1] == marker) return i;
        return -1;
    }

    private void Consume(int count)
    {
        Buffer.BlockCopy(_buf, count, _buf, 0, _len - count);
        _len -= count;
    }

    private void KeepTail()
    {
        // Guarda só o último byte (pode ser o FF de um FF D8 cortado ao meio).
        if (_len > 1) Consume(_len - 1);
    }
}
