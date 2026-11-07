using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Threading;
using static Otto.Native.NativeMethods;

namespace Otto.Native;

/// <summary>
/// Knows where the taskbar(s), the Windows speaker tray icon and the hovered taskbar button are.
/// The rectangles are refreshed on the UI thread / a worker and read lock-free from the hook thread.
/// </summary>
public sealed class TaskbarTracker : IDisposable
{
    private static readonly Regex PercentAtEnd = new(@"(\d{1,3})\s*%\s*$", RegexOptions.Compiled);

    private readonly DispatcherTimer _timer;
    private readonly Func<double> _masterPercent;
    private volatile RECT[] _taskbars = Array.Empty<RECT>();
    private volatile IntPtr[] _taskbarHwnds = Array.Empty<IntPtr>();
    private RECT _speaker;
    private volatile bool _hasSpeaker;
    private string? _speakerPrefix;
    private int _ticks;
    private bool _speakerRefreshBusy;

    public TaskbarTracker(Dispatcher ui, Func<double> masterPercent)
    {
        _masterPercent = masterPercent;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => { RefreshRects(); if (++_ticks % 2 == 0) RefreshSpeakerAsync(); };
        RefreshRects();
        RefreshSpeakerAsync();
        _timer.Start();
    }

    public bool HasSpeaker => _hasSpeaker;
    public RECT SpeakerRect => _speaker;
    public IReadOnlyList<RECT> Taskbars => _taskbars;

    /// <summary>Inside a taskbar rectangle AND the taskbar is really what is under the cursor
    /// (a fullscreen or borderless window covering it does not count).</summary>
    public bool IsOverTaskbar(POINT p)
    {
        foreach (var r in _taskbars) if (Contains(r, p)) return TaskbarIsOnTopAt(p);
        return false;
    }

    public bool IsOverSpeaker(POINT p) => _hasSpeaker && Contains(_speaker, p) && TaskbarIsOnTopAt(p);

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Top-level window under the point is a taskbar (or one of Otto's own windows sitting on it).</summary>
    public bool TaskbarIsOnTopAt(POINT p)
    {
        var h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return false;
        var root = GetAncestor(h, GA_ROOT);
        if (root == IntPtr.Zero) root = h;
        foreach (var tb in _taskbarHwnds) if (tb == root) return true;
        GetWindowThreadProcessId(root, out var pid);
        return pid == OwnPid;
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

    /// <summary>UI Automation over the notification area: finds the speaker icon by its "NN%" name matching the master volume.</summary>
    public void RefreshSpeakerAsync()
    {
        if (_speakerRefreshBusy) return;
        _speakerRefreshBusy = true;
        var hwnds = _taskbarHwnds;
        double master = _masterPercent();
        Task.Run(() =>
        {
            try
            {
                foreach (var tb in hwnds)
                {
                    var tray = FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
                    if (tray == IntPtr.Zero) continue;
                    var root = AutomationElement.FromHandle(tray);
                    var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    foreach (AutomationElement b in buttons)
                    {
                        string name;
                        try { name = b.Current.Name ?? string.Empty; } catch { continue; }
                        if (!LooksLikeSpeaker(name, master)) continue;
                        var rect = b.Current.BoundingRectangle;
                        if (rect.IsEmpty || rect.Width <= 0) continue;
                        _speaker = new RECT { left = (int)rect.Left, top = (int)rect.Top, right = (int)rect.Right, bottom = (int)rect.Bottom };
                        _hasSpeaker = true;
                        if (_speakerPrefix == null)
                        {
                            var m = Regex.Match(name, @"^(.*?)\d");
                            _speakerPrefix = m.Success && m.Groups[1].Value.Trim().Length >= 3 ? m.Groups[1].Value : null;
                            Log.Write($"[taskbar] speaker icon '{name}' at {_speaker.left},{_speaker.top}");
                        }
                        return;
                    }
                }
                _hasSpeaker = false;
            }
            catch (Exception ex) { Log.Error("speaker icon", ex); _hasSpeaker = false; }
            finally { _speakerRefreshBusy = false; }
        });
    }

    private bool LooksLikeSpeaker(string name, double master)
    {
        if (_speakerPrefix != null && name.StartsWith(_speakerPrefix, StringComparison.Ordinal)) return true;
        var m = PercentAtEnd.Match(name);
        if (!m.Success) return false;
        int n = int.Parse(m.Groups[1].Value);
        return Math.Abs(n - master) <= 1;
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

    public void Dispose() => _timer.Stop();
}
