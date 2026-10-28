namespace TabletMonitor.Core;

/// <summary>
/// Fila entre o ffmpeg e o socket. Se a conexão não der conta (Wi-Fi ruim, Bluetooth), descarta quadros
/// em vez de acumular atraso: ao descartar um quadro P, descarta tudo até o próximo keyframe
/// (sem ele a imagem ficaria corrompida). Pacotes de controle nunca são descartados.
/// </summary>
public sealed class SendQueue
{
    private readonly struct Item
    {
        public readonly byte Type;
        public readonly byte[] Data;
        public readonly bool IsFrame;
        public readonly bool IsKey;
        public readonly long Enqueued;
        public Item(byte type, byte[] data, bool isFrame, bool isKey = false)
        {
            Type = type; Data = data; IsFrame = isFrame; IsKey = isKey; Enqueued = Environment.TickCount64;
        }
    }

    private readonly LinkedList<Item> _items = new();
    private readonly object _lock = new();
    private readonly int _maxFrames;
    private readonly int _maxAgeMs;
    private int _frames;
    private bool _waitKey;
    private bool _closed;

    public long Dropped { get; private set; }
    public long Sent { get; private set; }

    /// <param name="maxFrames">Quadros na fila antes de começar a descartar.</param>
    /// <param name="maxAgeMs">Quadros P mais velhos que isso na hora de enviar são descartados.</param>
    public SendQueue(int maxFrames, int maxAgeMs = 250)
    {
        _maxFrames = Math.Max(1, maxFrames);
        _maxAgeMs = maxAgeMs;
    }

    public void EnqueueControl(byte type, ReadOnlySpan<byte> payload)
    {
        lock (_lock)
        {
            _items.AddLast(new Item(type, payload.ToArray(), false));
            Monitor.Pulse(_lock);
        }
    }

    /// <returns>false se o quadro foi descartado.</returns>
    public bool EnqueueFrame(bool key, long ptsMicros, ReadOnlySpan<byte> annexB)
    {
        lock (_lock)
        {
            if (_closed) return false;
            if (!key && _waitKey) { Dropped++; return false; }
            if (_frames >= _maxFrames)
            {
                if (!key)
                {
                    Dropped++;
                    _waitKey = true;
                    return false;
                }
                // Keyframe novo torna os quadros pendentes inúteis: joga fora e manda o keyframe.
                var node = _items.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value.IsFrame) { _items.Remove(node); _frames--; Dropped++; }
                    node = next;
                }
            }
            var data = new byte[9 + annexB.Length];
            data[0] = (byte)(key ? 1 : 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(1), ptsMicros);
            annexB.CopyTo(data.AsSpan(9));
            _items.AddLast(new Item(Protocol.Frame, data, true, key));
            _frames++;
            if (key) _waitKey = false;
            Monitor.Pulse(_lock);
            return true;
        }
    }

    /// <summary>Bloqueia até ter algo para enviar. Retorna false quando a fila foi fechada.</summary>
    public bool TryTake(out byte type, out byte[] data)
    {
        lock (_lock)
        {
            while (true)
            {
                while (_items.Count == 0 && !_closed) Monitor.Wait(_lock);
                if (_items.Count == 0) { type = 0; data = []; return false; }
                var item = _items.First!.Value;
                _items.RemoveFirst();
                if (item.IsFrame)
                {
                    _frames--;
                    // O socket demorou para liberar espaço: quadro velho só aumentaria o atraso.
                    bool old = Environment.TickCount64 - item.Enqueued > _maxAgeMs;
                    // Keyframe velho só pode cair se já houver outro keyframe na fila (sempre o caso no JPEG).
                    if (item.IsKey && old && HasQueuedKey())
                    {
                        Dropped++;
                        DropUntilNextKey(); // P-frames que dependiam dele também caem
                        continue;
                    }
                    bool stale = !item.IsKey && (_waitKey || old);
                    if (stale)
                    {
                        Dropped++;
                        DropUntilNextKey();
                        continue;
                    }
                    Sent++;
                }
                type = item.Type;
                data = item.Data;
                return true;
            }
        }
    }

    private bool HasQueuedKey()
    {
        for (var n = _items.First; n != null; n = n.Next)
            if (n.Value.IsFrame && n.Value.IsKey) return true;
        return false;
    }

    /// <summary>Depois de descartar um quadro P, os P seguintes também ficam inválidos até o próximo keyframe.</summary>
    private void DropUntilNextKey()
    {
        var node = _items.First;
        while (node != null)
        {
            var next = node.Next;
            if (node.Value.IsFrame)
            {
                if (node.Value.IsKey) { _waitKey = false; return; }
                _items.Remove(node);
                _frames--;
                Dropped++;
            }
            node = next;
        }
        _waitKey = true; // nenhum keyframe na fila: descarta o que chegar até vir um
    }

    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            Monitor.PulseAll(_lock);
        }
    }
}
