using System.Runtime.InteropServices;
using static Otto.Native.NativeMethods;

namespace Otto.Native;

/// <summary>
/// Low-level mouse hook on its own STA thread with a minimal message loop (a busy UI thread can
/// never make the callback miss the LowLevelHooksTimeout, after which Windows silently removes
/// slow hooks). The callback only reads key state and cached rectangles, raises events and
/// returns; subscribers marshal to the UI themselves.
///
/// It intercepts: modifier + wheel anywhere; wheel over the taskbar (no modifier); mouse moves
/// over the taskbar (for the hover pill); left clicks on the Windows speaker tray icon.
/// </summary>
public sealed class MouseWheelHook : IDisposable
{
    private readonly LowLevelMouseProc _proc;   // keep the delegate alive for the lifetime of the hook
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hook;
    private uint _threadId;
    private volatile bool _disposed;
    private bool _wasOverTaskbar;
    private bool _swallowingSpeakerClick;
    private long _lastHoverTick;
    private bool _overSpeaker;
    private long _lastCallbackTick;
    private POINT _watchPos;
    private long _watchTick;
    private long _lastReinstallLog;
    private int _reinstalls;
    private RECT _flyoutRect;
    private volatile bool _hasFlyoutRect;

    /// <summary>Returns true when the current keyboard state matches the gesture modifier. Called on the hook thread.</summary>
    public Func<bool> ModifierHeld { get; set; } = () => false;
    public bool Enabled { get; set; } = true;
    public TaskbarTracker? Taskbar { get; set; }
    public bool TaskbarWheelEnabled { get; set; }
    public bool TaskbarHoverEnabled { get; set; }
    public bool SpeakerClickEnabled { get; set; }

    /// <summary>Raised on the hook thread: wheel delta (±120 per notch, less for precision touchpads) and the cursor position in screen pixels.</summary>
    public event Action<int, POINT>? WheelWithModifier;
    /// <summary>Wheel over the taskbar without any modifier.</summary>
    public event Action<int, POINT>? WheelOverTaskbar;
    /// <summary>Mouse moved over the taskbar (inside = true, throttled), or just left it (inside = false).</summary>
    public event Action<POINT, bool>? TaskbarHover;
    /// <summary>Left click on the Windows speaker icon (both button messages were swallowed).</summary>
    public event Action<POINT>? SpeakerClicked;
    /// <summary>Any mouse button went down outside the flyout while it was open.</summary>
    public event Action? ClickOutside;

    public void SetFlyoutRect(RECT? rect)
    {
        if (rect is RECT r) { _flyoutRect = r; _hasFlyoutRect = true; }
        else _hasFlyoutRect = false;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public MouseWheelHook()
    {
        _proc = Callback;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "Otto.MouseHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write($"[hook] SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        _ready.Set();
        if (_hook == IntPtr.Zero) return;

        // Watchdog: Windows silently removes a low-level hook whose callback once ran past LowLevelHooksTimeout
        // (a GC pause is enough), and the speaker icon then opens Windows' own flyout again. Every 2 s: if the
        // cursor moved but the hook was not called since the last check, it is gone: install it again.
        SetTimer(IntPtr.Zero, UIntPtr.Zero, 2000, IntPtr.Zero);
        GetCursorPos(out _watchPos);
        _watchTick = Environment.TickCount64;
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_TIMER && msg.hwnd == IntPtr.Zero) { Watchdog(); continue; }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private void Watchdog()
    {
        if (_disposed) return;
        if (!GetCursorPos(out var p)) return; // locked / secure desktop
        long now = Environment.TickCount64;
        bool moved = p.x != _watchPos.x || p.y != _watchPos.y;
        if (moved && Interlocked.Read(ref _lastCallbackTick) < _watchTick)
        {
            UnhookWindowsHookEx(_hook); // fails harmlessly when Windows already removed it
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            _reinstalls++;
            if (now - _lastReinstallLog > 60_000)
            {
                _lastReinstallLog = now;
                Log.Write($"[hook] mouse hook was not called while the cursor moved: installed again ({_reinstalls} so far, ok={_hook != IntPtr.Zero})");
            }
        }
        _watchPos = p;
        _watchTick = now;
    }

    /// <summary>How many times the watchdog had to install the hook again.</summary>
    public int Reinstalls => _reinstalls;

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Interlocked.Exchange(ref _lastCallbackTick, Environment.TickCount64);
        if (nCode >= 0 && Enabled && !_disposed)
        {
            try
            {
                int msg = (int)wParam;
                switch (msg)
                {
                    case WM_MOUSEWHEEL:
                    {
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        int delta = (short)((info.mouseData >> 16) & 0xFFFF);
                        if (ModifierHeld())
                        {
                            WheelWithModifier?.Invoke(delta, info.pt);
                            return (IntPtr)1; // swallow: the app under the cursor must not zoom/scroll
                        }
                        if (TaskbarWheelEnabled && Taskbar != null && Taskbar.IsOverTaskbar(info.pt))
                        {
                            WheelOverTaskbar?.Invoke(delta, info.pt);
                            return (IntPtr)1;
                        }
                        break;
                    }
                    case WM_MOUSEMOVE:
                    {
                        if (Taskbar == null) break;
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        // the speaker icon opens Otto: hovering it shows nothing (no Windows tooltip)
                        bool overSpeaker = SpeakerClickEnabled && Taskbar.IsInSpeakerRect(info.pt);
                        if (overSpeaker != _overSpeaker) { _overSpeaker = overSpeaker; Taskbar.SpeakerHover(overSpeaker); }
                        if (!TaskbarHoverEnabled) break;
                        bool inside = Taskbar.IsOverTaskbar(info.pt);
                        long now = Environment.TickCount64;
                        if (inside && now - _lastHoverTick >= 40) { _lastHoverTick = now; TaskbarHover?.Invoke(info.pt, true); }
                        else if (!inside && _wasOverTaskbar) TaskbarHover?.Invoke(info.pt, false);
                        _wasOverTaskbar = inside;
                        break;
                    }
                    case WM_RBUTTONDOWN:
                    case WM_MBUTTONDOWN:
                    {
                        if (!_hasFlyoutRect) break;
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        if (!TaskbarTracker.Contains(_flyoutRect, info.pt)) ClickOutside?.Invoke();
                        break;
                    }
                    case WM_LBUTTONDOWN:
                    {
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        // A click on the speaker icon toggles the flyout itself (on button up): it is not a click outside.
                        if (SpeakerClickEnabled && Taskbar != null && Taskbar.IsOverSpeaker(info.pt)) { _swallowingSpeakerClick = true; return (IntPtr)1; }
                        if (_hasFlyoutRect && !TaskbarTracker.Contains(_flyoutRect, info.pt)) ClickOutside?.Invoke();
                        break;
                    }
                    case WM_LBUTTONUP:
                    {
                        if (!_swallowingSpeakerClick) break;
                        _swallowingSpeakerClick = false;
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        SpeakerClicked?.Invoke(info.pt);
                        return (IntPtr)1;
                    }
                }
            }
            catch (Exception ex) { Log.Error("mouse hook", ex); }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_overSpeaker) Taskbar?.SpeakerHover(false);
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
