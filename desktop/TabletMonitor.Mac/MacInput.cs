using TabletMonitor.Core;
using static TabletMonitor.Mac.MacNative;

namespace TabletMonitor.Mac;

/// <summary>
/// Toques do tablet → mouse/trackpad no macOS (o macOS não aceita toque injetado).
/// Os gestos são resolvidos pelo GestureMapper; aqui só viram CGEvent.
/// Precisa de permissão em Ajustes do Sistema > Privacidade e Segurança > Acessibilidade.
/// </summary>
public sealed class MacInput : IPointerSink, IDisposable
{
    private readonly GestureMapper _gestures;

    /// <summary>Área da tela transmitida, em pontos (coordenadas globais do macOS).</summary>
    public CGRect Target { get; set; }

    public InputMode Mode
    {
        get => _gestures.Mode;
        set => _gestures.Mode = value;
    }

    public MacInput() => _gestures = new GestureMapper(this);

    public void Handle(TouchEvent e)
    {
        if (Target.Size.Width <= 0) return;
        _gestures.Handle(e.Action, e.PointerId,
            Target.Origin.X + e.X * Target.Size.Width,
            Target.Origin.Y + e.Y * Target.Size.Height);
    }

    public void ReleaseAll() => _gestures.ReleaseAll();

    void IPointerSink.Mouse(MouseKind kind, double x, double y, int clickState)
    {
        var (type, button) = kind switch
        {
            MouseKind.LeftDown => (LeftMouseDown, ButtonLeft),
            MouseKind.LeftUp => (LeftMouseUp, ButtonLeft),
            MouseKind.LeftDrag => (LeftMouseDragged, ButtonLeft),
            MouseKind.RightDown => (RightMouseDown, ButtonRight),
            MouseKind.RightUp => (RightMouseUp, ButtonRight),
            _ => (MouseMoved, ButtonLeft),
        };
        var ev = CGEventCreateMouseEvent(IntPtr.Zero, type, new CGPoint(x, y), button);
        if (ev == IntPtr.Zero) return;
        if (clickState > 0) CGEventSetIntegerValueField(ev, kCGMouseEventClickState, clickState);
        CGEventPost(kCGHIDEventTap, ev);
        CFRelease(ev);
    }

    void IPointerSink.Scroll(int dy, int dx)
    {
        var ev = CGEventCreateScrollWheelEvent2(IntPtr.Zero, kCGScrollEventUnitPixel, 2, dy, dx, 0);
        if (ev == IntPtr.Zero) return;
        CGEventPost(kCGHIDEventTap, ev);
        CFRelease(ev);
    }

    public void Dispose() => _gestures.Dispose();
}
