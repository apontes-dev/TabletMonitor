using System.Net.WebSockets;

namespace TabletMonitor.Core;

/// <summary>
/// Faz um WebSocket parecer um Stream comum, para o navegador falar exatamente o mesmo protocolo
/// TMON do app Android. Cada Flush vira uma mensagem binária (o PacketWriter dá Flush por pacote).
/// </summary>
public sealed class WebSocketStream : Stream
{
    private readonly WebSocket _ws;
    private readonly Action _onDispose;
    private readonly MemoryStream _out = new();
    private readonly byte[] _in = new byte[64 * 1024];
    private int _inPos, _inLen;
    private bool _eof;

    public WebSocketStream(WebSocket ws, Action onDispose)
    {
        _ws = ws;
        _onDispose = onDispose;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (_inPos >= _inLen)
        {
            if (_eof) return 0;
            var r = _ws.ReceiveAsync(new ArraySegment<byte>(_in), CancellationToken.None).GetAwaiter().GetResult();
            if (r.MessageType == WebSocketMessageType.Close) { _eof = true; return 0; }
            _inPos = 0;
            _inLen = r.Count;
        }
        int n = Math.Min(buffer.Length, _inLen - _inPos);
        _in.AsSpan(_inPos, n).CopyTo(buffer);
        _inPos += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _out.Write(buffer);

    public override void Flush()
    {
        if (_out.Length == 0) return;
        _ws.SendAsync(new ArraySegment<byte>(_out.GetBuffer(), 0, (int)_out.Length),
            WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult();
        _out.SetLength(0);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _ws.Abort(); } catch { }
            try { _ws.Dispose(); } catch { }
            try { _onDispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
