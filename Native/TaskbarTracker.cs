using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Threading;
using static Otto.Native.NativeMethods;

namespace Otto.Native;

/// <summary>
/// Knows where the taskbar(s), the Windows speaker tray icon and the hovered taskbar button are.
/// The rectangles are refreshed on a worker and read lock-free from the hook thread.
/// </summary>
public sealed class TaskbarTracker : IDisposable
{
    private static readonly Regex PercentAtEnd = new(@"(\d{1,3})\s*%\s*$", RegexOptions.Compiled);

    private readonly System.Threading.Timer _timer;
    private readonly Func<double> _masterPercent;
    private readonly Dispatcher _ui;
    private volatile RECT[] _taskbars = Array.Empty<RECT>();
    private volatile IntPtr[] _taskbarHwnds = Array.Empty<IntPtr>();
    private RECT _speaker;
    private volatile bool _hasSpeaker;
    private string? _speakerPrefix;
    private int _speakerMisses;
    private string _traySignature = "";
    private long _lastSpeakerRefresh;
    private int _busy;
    private volatile IntPtr _speakerTooltip;
    private volatile bool _tooltipOff;
    private uint _explorerPid;
    private IntPtr _tipHook;
    private readonly WinEventProc _tipProc;

    public TaskbarTracker(Dispatcher ui, Func<double> masterPercent)
    {
        _ui = ui;
        _masterPercent = masterPercent;
        _tipProc = OnTooltipShown;
        RefreshRects();
        // Every 300 ms on a worker: cheap checks of the tray layout. The slow UI Automation lookup runs only
        // when a tray icon appeared, vanished or moved (or every 5 s), so the speaker rectangle is never stale.
        _timer = new System.Threading.Timer(_ => Tick(), null, 0, 300);
    }

    public bool HasSpeaker => _hasSpeaker;
    public RECT SpeakerRect => _speaker;
    public IReadOnlyList<RECT> Taskbars => _taskbars;
    public bool SpeakerTooltipKnown => _speakerTooltip != IntPtr.Zero;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            RefreshRects();
            string sig = TraySignature();
            long now = Environment.TickCount64;
            bool first = _lastSpeakerRefresh == 0;
            if (first || sig != _traySignature || now - _lastSpeakerRefresh > 5000 || (!_hasSpeaker && now - _lastSpeakerRefresh > 1500))
            {
                _traySignature = sig;
                RefreshSpeaker();
                if (first) ReenableTooltips(); // a crash while the cursor sat on the speaker would have left them off
            }
        }
        catch (Exception ex) { Log.Error("taskbar tick", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Position and button count of every tray toolbar: changes whenever a tray icon comes, goes or moves.</summary>
    private string TraySignature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var tb in _taskbarHwnds)
        {
            var tray = FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero) continue;
            foreach (var bar in TrayToolbars(tray))
            {
                GetWindowRect(bar, out var r);
                SendMessageTimeout(bar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var count);
                sb.Append(r.left).Append(',').Append(r.top).Append(',').Append(r.right).Append(',').Append(r.bottom).Append('#').Append(count).Append(';');
            }
            if (GetWindowRect(tray, out var tr)) sb.Append(tr.left).Append(',').Append(tr.right).Append('|');
        }
        return sb.ToString();
    }

    private static List<IntPtr> TrayToolbars(IntPtr tray)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(tray, (h, _) => { if (GetClassNameOf(h) == "ToolbarWindow32") list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    /// <summary>Inside a taskbar rectangle AND the taskbar is really what is under the cursor
    /// (a fullscreen or borderless window covering it does not count).</summary>
    public bool IsOverTaskbar(POINT p)
    {
        foreach (var r in _taskbars) if (Contains(r, p)) return TaskbarIsOnTopAt(p);
        return false;
    }

    public bool IsOverSpeaker(POINT p) => IsInSpeakerRect(p) && TaskbarIsOnTopAt(p);

    /// <summary>Rectangle only (no window check): cheap enough for every mouse move.</summary>
    public bool IsInSpeakerRect(POINT p) => _hasSpeaker && Contains(_speaker, p);

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>
    /// Top-level window under the point is a taskbar (or one of Otto's own windows sitting on it).
    /// Walks the z-order instead of calling WindowFromPoint, which sends WM_NCHITTEST to the window under the
    /// cursor: a busy or hung window there stalls the mouse hook, and Windows silently removes a slow hook.
    /// </summary>
    public bool TaskbarIsOnTopAt(POINT p)
    {
        var root = TopLevelAt(p, out bool layered);
        if (root == IntPtr.Zero) return false;
        if (layered) // may be see-through where its pixels are clear: let Windows hit-test that one
        {
            var h = WindowFromPoint(p);
            root = h == IntPtr.Zero ? IntPtr.Zero : GetAncestor(h, GA_ROOT);
            if (root == IntPtr.Zero) root = h;
            if (root == IntPtr.Zero) return false;
        }
        foreach (var tb in _taskbarHwnds) if (tb == root) return true;
        GetWindowThreadProcessId(root, out var pid);
        return pid == OwnPid;
    }

    /// <summary>Topmost visible, non-cloaked, hit-testable top-level window containing the point. Sends no messages.</summary>
    private static IntPtr TopLevelAt(POINT p, out bool layered)
    {
        layered = false;
        int guard = 0;
        for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && guard++ < 3000; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(h) || !GetWindowRect(h, out var r) || !Contains(r, p)) continue;
            long ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0) continue; // click-through overlay
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (GetClassNameOf(h) == "tooltips_class32") continue;
            layered = (ex & WS_EX_LAYERED) != 0;
            return h;
        }
        return IntPtr.Zero;
    }

    public static bool Contains(RECT r, POINT p) => p.x >= r.left && p.x < r.right && p.y >= r.top && p.y < r.bottom;

    /// <summary>Cheap: EnumWindows for the shell tray windows.</summary>
    public void RefreshRects()
    {
        var rects = new List<RECT>();
        var hwnds = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            var cls = GetClassNameOf(h);
            if ((cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") && IsWindowVisible(h) && GetWindowRect(h, out var r) && r.Width > 0 && r.Height > 0)
            {
                rects.Add(r); hwnds.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        _taskbars = rects.ToArray();
        _taskbarHwnds = hwnds.ToArray();
    }

    /// <summary>
    /// UI Automation over the notification area: the speaker icon is the button named "output: NN%"
    /// (or "output: muted"), where output is one of the playback devices. Worker thread.
    /// </summary>
    private void RefreshSpeaker()
    {
        _lastSpeakerRefresh = Environment.TickCount64;
        var hwnds = _taskbarHwnds;
        double master = _masterPercent();
        var outputs = OutputNames();
        try
        {
            foreach (var tb in hwnds)
            {
                var tray = FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
                if (tray == IntPtr.Zero) continue;
                var root = AutomationElement.FromHandle(tray);
                var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                AutomationElement? best = null;
                string bestName = "";
                int bestScore = 0;
                foreach (AutomationElement b in buttons)
                {
                    string name;
                    try { name = b.Current.Name ?? string.Empty; } catch { continue; }
                    int score = SpeakerScore(name, master, outputs);
                    if (score > bestScore) { best = b; bestName = name; bestScore = score; }
                }
                if (best == null) continue;
                var rect = best.Current.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0) continue;
                var found = new RECT { left = (int)rect.Left, top = (int)rect.Top, right = (int)rect.Right, bottom = (int)rect.Bottom };
                bool moved = !_hasSpeaker || found.left != _speaker.left || found.top != _speaker.top || found.right != _speaker.right || found.bottom != _speaker.bottom;
                _speaker = found;
                _hasSpeaker = true;
                _speakerMisses = 0;
                var m = Regex.Match(bestName, @"^(.*?):");
                if (m.Success && m.Groups[1].Value.Trim().Length >= 3) _speakerPrefix = m.Groups[1].Value + ":";
                var tip = TooltipOfToolbarAt(tray, found);
                if (tip != _speakerTooltip)
                {
                    if (_tooltipOff && _speakerTooltip != IntPtr.Zero) PostMessage(_speakerTooltip, TTM_ACTIVATE, (IntPtr)1, IntPtr.Zero);
                    _speakerTooltip = tip;
                }
                if (moved) Log.Write($"[taskbar] speaker icon '{bestName}' at {_speaker.left},{_speaker.top} (match {bestScore})");
                return;
            }
            // A miss can be a transient name (the volume changing right then): keep the last rectangle once.
            if (++_speakerMisses >= 2 && _hasSpeaker) { _hasSpeaker = false; Log.Write("[taskbar] speaker icon not found"); }
        }
        catch (Exception ex) { Log.Error("speaker icon", ex); }
    }

    /// <summary>0 = not the speaker; higher = surer.</summary>
    private int SpeakerScore(string name, double master, List<string> outputs)
    {
        if (name.Length == 0) return 0;
        foreach (var o in outputs)
            if (name.StartsWith(o + ":", StringComparison.OrdinalIgnoreCase)) return 3;
        if (_speakerPrefix != null && name.StartsWith(_speakerPrefix, StringComparison.Ordinal)) return 2;
        var m = PercentAtEnd.Match(name);
        if (m.Success && Math.Abs(int.Parse(m.Groups[1].Value) - Math.Min(100, master)) <= 1) return 1;
        return 0;
    }

    /// <summary>
    /// Names the speaker icon can show: each active output's description, i.e. its friendly name
    /// "Speakers (Realtek(R) Audio)" without the adapter part in brackets, plus the full name.
    /// </summary>
    private static List<string> OutputNames()
    {
        var names = new List<string>();
        try
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
            {
                using (d)
                {
                    try
                    {
                        string full = d.FriendlyName, adapter = d.DeviceFriendlyName;
                        string desc = !string.IsNullOrEmpty(adapter) && full.EndsWith($" ({adapter})") ? full[..^(adapter.Length + 3)] : full;
                        if (desc.Length >= 2) names.Add(desc);
                        if (desc != full) names.Add(full);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { Log.Error("output names", ex); }
        return names;
    }

    /// <summary>The tooltip window of the tray toolbar that holds the speaker icon.</summary>
    private IntPtr TooltipOfToolbarAt(IntPtr tray, RECT speaker)
    {
        var c = new POINT((speaker.left + speaker.right) / 2, (speaker.top + speaker.bottom) / 2);
        foreach (var bar in TrayToolbars(tray))
        {
            if (!GetWindowRect(bar, out var r) || !Contains(r, c)) continue;
            SendMessageTimeout(bar, TB_GETTOOLTIPS, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var tip);
            GetWindowThreadProcessId(bar, out var pid);
            if (pid != 0 && pid != _explorerPid) { _explorerPid = pid; _ui.BeginInvoke(HookTooltips); } // Explorer (re)started
            return tip;
        }
        return IntPtr.Zero;
    }

    // ---- nothing on hover over the speaker icon -------------------------------------------

    /// <summary>
    /// From the mouse hook on every move (cheap: a rectangle test and at most two posted messages). While the
    /// cursor is on the speaker icon, the tray toolbar's tooltip is switched off, so hovering it shows nothing;
    /// it comes back on as soon as the cursor leaves the icon.
    /// </summary>
    public void SpeakerHover(bool over)
    {
        if (over == _tooltipOff) return;
        _tooltipOff = over;
        var tip = _speakerTooltip;
        if (tip == IntPtr.Zero) return;
        if (over) { PostMessage(tip, TTM_POP, IntPtr.Zero, IntPtr.Zero); PostMessage(tip, TTM_ACTIVATE, IntPtr.Zero, IntPtr.Zero); }
        else PostMessage(tip, TTM_ACTIVATE, (IntPtr)1, IntPtr.Zero);
    }

    private void ReenableTooltips()
    {
        var tip = _speakerTooltip;
        if (tip != IntPtr.Zero) PostMessage(tip, TTM_ACTIVATE, (IntPtr)1, IntPtr.Zero);
        _tooltipOff = false;
    }

    /// <summary>Backup on the UI thread: an Explorer tooltip that still shows while the cursor is on the speaker is hidden at once.</summary>
    private void HookTooltips()
    {
        if (_tipHook != IntPtr.Zero) { UnhookWinEvent(_tipHook); _tipHook = IntPtr.Zero; }
        if (_explorerPid == 0) return;
        _tipHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _tipProc, _explorerPid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private void OnTooltipShown(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject != 0 || hwnd == IntPtr.Zero || !_tooltipOff) return; // whole windows only, and only while on the speaker
            if (!GetCursorPos(out var p) || !IsInSpeakerRect(p)) return;
            if (GetClassNameOf(hwnd) != "tooltips_class32") return;
            ShowWindowAsync(hwnd, SW_HIDE);
        }
        catch { }
    }

    /// <summary>Everything the probe wants to know about the tray icons.</summary>
    public static List<string> TrayButtonNames()
    {
        var names = new List<string>();
        try
        {
            var tb = FindWindow("Shell_TrayWnd", null);
            var tray = FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero) return names;
            var root = AutomationElement.FromHandle(tray);
            foreach (AutomationElement b in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                try { names.Add(b.Current.Name ?? string.Empty); } catch { }
            }
        }
        catch (Exception ex) { names.Add("ERROR " + ex.Message); }
        return names;
    }

    /// <summary>The taskbar thumbnail preview, if it is currently showing.</summary>
    public static RECT? ThumbnailRect()
    {
        var h = FindWindow("TaskListThumbnailWnd", null);
        if (h == IntPtr.Zero || !IsWindowVisible(h) || !GetWindowRect(h, out var r) || r.Width <= 0) return null;
        return r;
    }

    /// <summary>Taskbar button under the point: its name and rectangle (physical pixels). Slow-ish: call off the UI thread.</summary>
    public static (string Name, RECT Rect)? ButtonAt(POINT pt)
    {
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(pt.x, pt.y));
            var walker = TreeWalker.ControlViewWalker;
            int guard = 0;
            while (el != null && guard++ < 10)
            {
                var ct = el.Current.ControlType;
                if (ct == ControlType.Button || ct == ControlType.MenuItem || ct == ControlType.ListItem)
                {
                    var n = el.Current.Name;
                    var r = el.Current.BoundingRectangle;
                    if (string.IsNullOrWhiteSpace(n) || r.IsEmpty) return null;
                    return (n, new RECT { left = (int)r.Left, top = (int)r.Top, right = (int)r.Right, bottom = (int)r.Bottom });
                }
                if (el.Current.ClassName is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") break;
                el = walker.GetParent(el);
            }
        }
        catch (Exception ex) { Log.Error("uia button", ex); }
        return null;
    }

    public void Dispose()
    {
        _timer.Dispose();
        ReenableTooltips();
        if (_tipHook != IntPtr.Zero) { UnhookWinEvent(_tipHook); _tipHook = IntPtr.Zero; }
    }
}
