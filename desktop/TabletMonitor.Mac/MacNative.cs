using System.Runtime.InteropServices;

namespace TabletMonitor.Mac;

/// <summary>Chamadas diretas ao macOS (CoreGraphics / ApplicationServices). Nada aqui roda fora do macOS.</summary>
public static class MacNative
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AS = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [StructLayout(LayoutKind.Sequential)] public struct CGPoint { public double X, Y; public CGPoint(double x, double y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] public struct CGSize { public double Width, Height; }
    [StructLayout(LayoutKind.Sequential)] public struct CGRect { public CGPoint Origin; public CGSize Size; }

    // ---- telas
    [DllImport(CG)] public static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] displays, out uint count);
    [DllImport(CG)] public static extern uint CGMainDisplayID();
    [DllImport(CG)] public static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CG)] public static extern int CGDisplayIsBuiltin(uint display);
    [DllImport(CG)] public static extern uint CGDisplayVendorNumber(uint display);
    [DllImport(CG)] public static extern uint CGDisplayModelNumber(uint display);
    [DllImport(CG)] public static extern IntPtr CGDisplayCopyDisplayMode(uint display);
    [DllImport(CG)] public static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);
    [DllImport(CG)] public static extern nuint CGDisplayModeGetPixelHeight(IntPtr mode);
    [DllImport(CG)] public static extern void CGDisplayModeRelease(IntPtr mode);

    // ---- eventos de mouse/rolagem
    public const int kCGHIDEventTap = 0;
    public const uint LeftMouseDown = 1, LeftMouseUp = 2, RightMouseDown = 3, RightMouseUp = 4, MouseMoved = 5, LeftMouseDragged = 6;
    public const uint ButtonLeft = 0, ButtonRight = 1;
    public const int kCGMouseEventClickState = 1;
    public const int kCGScrollEventUnitPixel = 0;

    [DllImport(CG)] public static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint type, CGPoint position, uint button);
    [DllImport(CG)] public static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, int units, uint wheelCount, int wheel1, int wheel2, int wheel3);
    [DllImport(CG)] public static extern void CGEventSetIntegerValueField(IntPtr evt, int field, long value);
    [DllImport(CG)] public static extern void CGEventPost(int tap, IntPtr evt);
    [DllImport(CF)] public static extern void CFRelease(IntPtr obj);

    // ---- permissões
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CGRequestScreenCaptureAccess();
    [DllImport(AS)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool AXIsProcessTrusted();

    public static bool ScreenCaptureAllowed()
    {
        try { return CGPreflightScreenCaptureAccess(); } catch { return true; }
    }

    public static bool AccessibilityAllowed()
    {
        try { return AXIsProcessTrusted(); } catch { return false; }
    }

    public static void OpenSettings(string pane) => Open("x-apple.systempreferences:com.apple.preference.security?" + pane);

    public static void Open(string target)
    {
        try { System.Diagnostics.Process.Start("open", target); } catch { }
    }
}

/// <summary>Uma tela do Mac, na ordem em que o ffmpeg (avfoundation) as chama de "Capture screen N".</summary>
public sealed record MacDisplay(int Index, uint Id, MacNative.CGRect Bounds, int PixelWidth, int PixelHeight, bool IsMain, bool IsBuiltin)
{
    public string Key => $"cg:{Id}";

    public override string ToString()
    {
        string tag = IsMain ? " — principal" : IsBuiltin ? " — embutida" : "";
        string retina = PixelWidth > Bounds.Size.Width + 1 ? $" (Retina, {PixelWidth}x{PixelHeight} px)" : "";
        return $"Tela {Index + 1}: {Bounds.Size.Width:0}x{Bounds.Size.Height:0}{retina}{tag}";
    }

    public static List<MacDisplay> Enumerate()
    {
        var ids = new uint[16];
        if (MacNative.CGGetActiveDisplayList((uint)ids.Length, ids, out var count) != 0) return [];
        uint main = MacNative.CGMainDisplayID();
        var list = new List<MacDisplay>();
        for (int i = 0; i < count; i++)
        {
            uint id = ids[i];
            var b = MacNative.CGDisplayBounds(id);
            int pw = (int)b.Size.Width, ph = (int)b.Size.Height;
            var mode = MacNative.CGDisplayCopyDisplayMode(id);
            if (mode != IntPtr.Zero)
            {
                pw = (int)MacNative.CGDisplayModeGetPixelWidth(mode);
                ph = (int)MacNative.CGDisplayModeGetPixelHeight(mode);
                MacNative.CGDisplayModeRelease(mode);
            }
            list.Add(new MacDisplay(i, id, b, pw, ph, id == main, MacNative.CGDisplayIsBuiltin(id) != 0));
        }
        return list;
    }

    /// <summary>Tela salva; senão a primeira que não é a principal; senão a principal (espelha).</summary>
    public static MacDisplay? Pick(List<MacDisplay> all, string saved) =>
        all.FirstOrDefault(d => d.Key == saved) ?? all.FirstOrDefault(d => !d.IsMain) ?? all.FirstOrDefault();
}
