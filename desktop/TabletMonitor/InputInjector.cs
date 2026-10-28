using System.Drawing;
using System.Runtime.InteropServices;
using TabletMonitor.Core;

namespace TabletMonitor;

/// <summary>
/// Converte os toques do tablet em entrada no Windows, dentro dos limites do monitor transmitido.
/// Modo Touch: toque nativo do Windows (InjectTouchInput) — rolar com o dedo, pinça, segurar = botão direito.
/// Modo Mouse: o dedo vira o cursor (SendInput) — útil se o toque nativo não funcionar em algum app.
/// </summary>
public sealed class InputInjector : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, POINT> _active = new();
    private readonly System.Threading.Timer _keepAlive;
    private bool _touchInitialized;
    private bool _mouseDown;

    public InputMode Mode { get; set; } = InputMode.Touch;
    public Rectangle Target { get; set; }
    public Action<string>? Log { get; set; }

    public InputInjector()
    {
        // Contatos parados (ex.: dedo segurando) precisam ser "renovados", senão o Windows os cancela.
        _keepAlive = new System.Threading.Timer(_ => KeepAlive(), null, 50, 50);
    }

    public void Handle(TouchEvent e)
    {
        if (Mode == InputMode.Off || Target.Width <= 0) return;
        int x = Target.Left + (int)Math.Round(e.X * (Target.Width - 1));
        int y = Target.Top + (int)Math.Round(e.Y * (Target.Height - 1));
        if (Mode == InputMode.Touch) HandleTouch(e.Action, e.PointerId % MaxContacts, x, y);
        else HandleMouse(e.Action, e.PointerId, x, y);
    }

    /// <summary>Solta tudo (fim de sessão).</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            if (_active.Count > 0 && _touchInitialized)
            {
                var frame = _active.Select(kv => Contact(kv.Key, kv.Value, POINTER_FLAG_UP)).ToArray();
                _active.Clear();
                InjectTouchInput((uint)frame.Length, frame);
            }
            _active.Clear();
            if (_mouseDown)
            {
                SendMouse(0, 0, MOUSEEVENTF_LEFTUP, absolute: false);
                _mouseDown = false;
            }
        }
    }

    // ------------------------------------------------------------------ touch

    private const int MaxContacts = 10;

    private void HandleTouch(TouchAction action, int id, int x, int y)
    {
        lock (_lock)
        {
            if (!_touchInitialized)
            {
                if (!InitializeTouchInjection(MaxContacts, TOUCH_FEEDBACK_DEFAULT))
                {
                    Log?.Invoke($"Toque nativo indisponível (erro {Marshal.GetLastWin32Error()}); usando mouse.");
                    Mode = InputMode.Mouse;
                    HandleMouse(action, id, x, y);
                    return;
                }
                _touchInitialized = true;
            }

            var pt = new POINT { X = x, Y = y };
            uint flags;
            switch (action)
            {
                case TouchAction.Down:
                    if (_active.ContainsKey(id))
                    {
                        // Já estava em contato (pacote perdido): solta antes.
                        InjectFrame(id, _active[id], POINTER_FLAG_UP);
                        _active.Remove(id);
                    }
                    flags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                    _active[id] = pt;
                    InjectFrame(id, pt, flags);
                    break;
                case TouchAction.Move:
                    if (!_active.ContainsKey(id)) return;
                    _active[id] = pt;
                    InjectFrame(id, pt, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);
                    break;
                case TouchAction.Up:
                case TouchAction.Cancel:
                    if (!_active.ContainsKey(id)) return;
                    uint up = POINTER_FLAG_UP | (action == TouchAction.Cancel ? POINTER_FLAG_CANCELED : 0);
                    InjectFrame(id, _active[id], up);
                    _active.Remove(id);
                    break;
            }
        }
    }

    /// <summary>Um "frame" de injeção inclui todos os contatos ativos; o alterado leva a flag do evento.</summary>
    private void InjectFrame(int changedId, POINT changedPt, uint changedFlags)
    {
        var frame = new List<POINTER_TOUCH_INFO> { Contact(changedId, changedPt, changedFlags) };
        foreach (var kv in _active)
        {
            if (kv.Key == changedId) continue;
            frame.Add(Contact(kv.Key, kv.Value, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT));
        }
        if (!InjectTouchInput((uint)frame.Count, frame.ToArray()))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 0) Log?.Invoke($"InjectTouchInput falhou (erro {err})");
        }
    }

    private void KeepAlive()
    {
        lock (_lock)
        {
            if (_active.Count == 0 || !_touchInitialized) return;
            var frame = _active.Select(kv =>
                Contact(kv.Key, kv.Value, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT)).ToArray();
            InjectTouchInput((uint)frame.Length, frame);
        }
    }

    private static POINTER_TOUCH_INFO Contact(int id, POINT pt, uint flags) => new()
    {
        pointerInfo = new POINTER_INFO
        {
            pointerType = PT_TOUCH,
            pointerId = (uint)id,
            pointerFlags = flags,
            ptPixelLocation = pt,
        },
        touchFlags = 0,
        touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE,
        rcContact = new RECT { left = pt.X - 2, top = pt.Y - 2, right = pt.X + 2, bottom = pt.Y + 2 },
        orientation = 90,
        pressure = 32000,
    };

    // ------------------------------------------------------------------ mouse

    private void HandleMouse(TouchAction action, int id, int x, int y)
    {
        lock (_lock)
        {
            // No modo mouse só o primeiro dedo conta.
            if (id != 0) return;
            var (nx, ny) = ToVirtualDesk(x, y);
            switch (action)
            {
                case TouchAction.Down:
                    SendMouse(nx, ny, MOUSEEVENTF_MOVE, absolute: true);
                    SendMouse(nx, ny, MOUSEEVENTF_LEFTDOWN, absolute: true);
                    _mouseDown = true;
                    break;
                case TouchAction.Move:
                    SendMouse(nx, ny, MOUSEEVENTF_MOVE, absolute: true);
                    break;
                case TouchAction.Up:
                case TouchAction.Cancel:
                    if (_mouseDown) SendMouse(nx, ny, MOUSEEVENTF_LEFTUP, absolute: true);
                    _mouseDown = false;
                    break;
            }
        }
    }

    private static (int, int) ToVirtualDesk(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(GetSystemMetrics(SM_CXVIRTUALSCREEN), 2), vh = Math.Max(GetSystemMetrics(SM_CYVIRTUALSCREEN), 2);
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        return (nx, ny);
    }

    private static void SendMouse(int nx, int ny, uint flags, bool absolute)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = nx,
                dy = ny,
                dwFlags = flags | (absolute ? MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE : 0),
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        ReleaseAll();
    }

    // ------------------------------------------------------------------ Win32

    private const uint TOUCH_FEEDBACK_DEFAULT = 0x1;
    private const uint PT_TOUCH = 2;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;
    private const uint POINTER_FLAG_CANCELED = 0x00008000;
    private const uint TOUCH_MASK_CONTACTAREA = 0x1;
    private const uint TOUCH_MASK_ORIENTATION = 0x2;
    private const uint TOUCH_MASK_PRESSURE = 0x4;

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
