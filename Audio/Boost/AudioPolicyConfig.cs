using System.Runtime.InteropServices;

namespace Otto.Audio.Boost;

/// <summary>
/// Per-app output device, the same setting as Settings → Sound → App volume and device preferences.
/// Undocumented WinRT factory (layout as in EarTrumpet). Windows keeps the choice per executable, so
/// every route Otto sets must be undone (see <see cref="RouteStore"/>). Apps that follow default-device
/// changes (Chrome, Discord, Spotify…) are notified and move their stream at once.
/// </summary>
public static unsafe class AudioPolicyConfig
{
    private static readonly Guid Iid21H2 = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Guid IidDownlevel = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");
    // IUnknown (3) + IInspectable (3) + 19 methods Otto does not use
    private const int SlotSet = 6 + 19, SlotGet = SlotSet + 1;
    private const string Token = @"\\?\SWD#MMDEVAPI#", RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const int E_INVALIDARG = unchecked((int)0x80070057);

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    private static IntPtr _factory;
    private static readonly object Gate = new();

    public static bool IsAvailable
    {
        get { try { return Factory() != IntPtr.Zero; } catch { return false; } }
    }

    private static IntPtr Factory()
    {
        lock (Gate)
        {
            if (_factory != IntPtr.Zero) return _factory;
            const string cls = "Windows.Media.Internal.AudioPolicyConfig";
            Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var h));
            try
            {
                var iid = Environment.OSVersion.Version.Build >= 21390 ? Iid21H2 : IidDownlevel;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(h, ref iid, out var f));
                return _factory = f;
            }
            finally { WindowsDeleteString(h); }
        }
    }

    private static void* Slot(IntPtr obj, int i) => (*(void***)obj)[i];

    /// <summary>Send a process's output to a device (MMDevice id), or back to the Windows default with null.</summary>
    public static void Set(uint pid, string? deviceId)
    {
        var f = Factory();
        IntPtr h = IntPtr.Zero;
        if (!string.IsNullOrEmpty(deviceId))
        {
            var s = Token + deviceId + RenderInterface;
            Marshal.ThrowExceptionForHR(WindowsCreateString(s, s.Length, out h));
        }
        try
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)Slot(f, SlotSet);
            foreach (int role in new[] { 1 /* eMultimedia */, 0 /* eConsole */ })
                Marshal.ThrowExceptionForHR(fn(f, pid, 0 /* eRender */, role, h));
        }
        finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
    }

    /// <summary>The device a process is sent to (MMDevice id), or null when it follows the default.</summary>
    public static string? Get(uint pid)
    {
        var f = Factory();
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr*, int>)Slot(f, SlotGet);
        IntPtr h;
        int hr = fn(f, pid, 0, 1, &h);
        if (hr == E_INVALIDARG) return null; // the process has no audio yet
        Marshal.ThrowExceptionForHR(hr);
        if (h == IntPtr.Zero) return null;
        try
        {
            var s = new string(WindowsGetStringRawBuffer(h, out uint len), 0, (int)len);
            if (s.StartsWith(Token, StringComparison.OrdinalIgnoreCase)) s = s[Token.Length..];
            if (s.EndsWith(RenderInterface, StringComparison.OrdinalIgnoreCase)) s = s[..^RenderInterface.Length];
            return s.Length == 0 ? null : s;
        }
        finally { WindowsDeleteString(h); }
    }
}
