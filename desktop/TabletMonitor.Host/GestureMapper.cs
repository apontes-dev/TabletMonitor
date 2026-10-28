using TabletMonitor.Core;

namespace TabletMonitor;

public enum MouseKind { Move, LeftDown, LeftUp, LeftDrag, RightDown, RightUp }

/// <summary>Quem recebe os eventos de mouse/rolagem já traduzidos (macOS: CGEvent).</summary>
public interface IPointerSink
{
    void Mouse(MouseKind kind, double x, double y, int clickState);
    void Scroll(int dy, int dx);
}

/// <summary>
/// Para sistemas que não aceitam toque injetado (macOS): traduz toques em mouse/trackpad.
///   1 dedo: toque = clique; dois toques rápidos = clique duplo; arrastar = arrastar;
///   2 dedos: arrastar = rolar; tocar com 2 dedos = botão direito (como no trackpad).
/// Ao encostar o primeiro dedo, espera ~70 ms antes de "apertar" o botão — tempo de chegar o segundo
/// dedo sem gerar um clique indesejado. Coordenadas já no espaço da tela de destino.
/// </summary>
public sealed class GestureMapper : IDisposable
{
    private readonly IPointerSink _sink;
    private readonly object _lock = new();
    private readonly Dictionary<int, (double X, double Y)> _touches = new();
    private readonly Timer _pendingTimer;

    public InputMode Mode { get; set; } = InputMode.Touch;
    public int DelayMs { get; init; } = 70;
    private const double SlopPx = 8;

    private bool _pendingDown, _leftDown;
    private (double X, double Y) _downAt, _lastClickAt;
    private long _lastClickTime;
    private int _clickCount;

    private bool _twoFinger;
    private (double X, double Y) _lastCentroid;
    private double _twoFingerTravel;
    private long _twoFingerStart;
    private double _scrollRemX, _scrollRemY;

    public GestureMapper(IPointerSink sink)
    {
        _sink = sink;
        _pendingTimer = new Timer(_ => OnPendingTimeout(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static long Now => Environment.TickCount64;

    public void Handle(TouchAction action, int id, double x, double y)
    {
        if (Mode == InputMode.Off) return;
        lock (_lock)
        {
            switch (action)
            {
                case TouchAction.Down: OnDown(id, (x, y)); break;
                case TouchAction.Move: OnMove(id, (x, y)); break;
                default: OnUp(id, (x, y)); break;
            }
        }
    }

    private void OnDown(int id, (double X, double Y) pt)
    {
        _touches[id] = pt;
        bool firstFinger = _touches.Count == 1;
        if (firstFinger)
        {
            _pendingDown = true;
            _downAt = pt;
            _sink.Mouse(MouseKind.Move, pt.X, pt.Y, 0);
            _pendingTimer.Change(DelayMs, Timeout.Infinite);
        }
        else if (_touches.Count == 2 && Mode == InputMode.Touch)
        {
            _pendingTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pendingDown = false;
            if (_leftDown) { _sink.Mouse(MouseKind.LeftUp, _downAt.X, _downAt.Y, 1); _leftDown = false; }
            _twoFinger = true;
            _lastCentroid = Centroid();
            _twoFingerTravel = 0;
            _twoFingerStart = Now;
            _scrollRemX = _scrollRemY = 0;
        }
    }

    private void OnMove(int id, (double X, double Y) pt)
    {
        if (!_touches.ContainsKey(id)) return;
        _touches[id] = pt;
        if (_twoFinger)
        {
            if (_touches.Count < 2) return;
            var c = Centroid();
            double dx = c.X - _lastCentroid.X, dy = c.Y - _lastCentroid.Y;
            _lastCentroid = c;
            _twoFingerTravel += Math.Abs(dx) + Math.Abs(dy);
            _scrollRemX += dx;
            _scrollRemY += dy;
            int sx = (int)_scrollRemX, sy = (int)_scrollRemY;
            if (sx != 0 || sy != 0)
            {
                _scrollRemX -= sx;
                _scrollRemY -= sy;
                _sink.Scroll(sy, sx); // rolagem natural: o conteúdo acompanha os dedos
            }
            return;
        }
        if (_touches.Count != 1) return;
        if (_pendingDown && Dist(pt, _downAt) > SlopPx) StartDrag();
        if (_leftDown) _sink.Mouse(MouseKind.LeftDrag, pt.X, pt.Y, 1);
        else if (!_pendingDown) _sink.Mouse(MouseKind.Move, pt.X, pt.Y, 0);
    }

    private void OnUp(int id, (double X, double Y) pt)
    {
        if (!_touches.Remove(id)) return;
        if (_twoFinger)
        {
            if (_touches.Count > 0) return;
            _twoFinger = false;
            if (_twoFingerTravel < 12 && Now - _twoFingerStart < 350)
            {
                _sink.Mouse(MouseKind.RightDown, _lastCentroid.X, _lastCentroid.Y, 1);
                _sink.Mouse(MouseKind.RightUp, _lastCentroid.X, _lastCentroid.Y, 1);
            }
            return;
        }
        if (_pendingDown)
        {
            _pendingTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pendingDown = false;
            bool again = Now - _lastClickTime < 400 && Dist(_downAt, _lastClickAt) < 20;
            _clickCount = again ? Math.Min(_clickCount + 1, 3) : 1;
            _sink.Mouse(MouseKind.LeftDown, _downAt.X, _downAt.Y, _clickCount);
            _sink.Mouse(MouseKind.LeftUp, _downAt.X, _downAt.Y, _clickCount);
            _lastClickTime = Now;
            _lastClickAt = _downAt;
        }
        else if (_leftDown)
        {
            _sink.Mouse(MouseKind.LeftUp, pt.X, pt.Y, 1);
            _leftDown = false;
        }
    }

    private void OnPendingTimeout()
    {
        lock (_lock)
        {
            if (_pendingDown && !_twoFinger && _touches.Count == 1) StartDrag();
        }
    }

    private void StartDrag()
    {
        _pendingTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _pendingDown = false;
        _sink.Mouse(MouseKind.LeftDown, _downAt.X, _downAt.Y, 1);
        _leftDown = true;
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            _pendingTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (_leftDown) _sink.Mouse(MouseKind.LeftUp, _downAt.X, _downAt.Y, 1);
            _leftDown = _pendingDown = _twoFinger = false;
            _touches.Clear();
        }
    }

    private (double X, double Y) Centroid()
    {
        double x = 0, y = 0;
        foreach (var p in _touches.Values) { x += p.X; y += p.Y; }
        int n = Math.Max(1, _touches.Count);
        return (x / n, y / n);
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public void Dispose()
    {
        ReleaseAll();
        _pendingTimer.Dispose();
    }
}
