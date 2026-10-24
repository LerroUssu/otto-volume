using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Reflection;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;
using static Otto.Native.NativeMethods;

namespace Otto.Tray;

/// <summary>
/// Notification-area icon (WinForms NotifyIcon) with a WPF-styled context menu. Self-repairing: right after logon the
/// shell can drop the first add (Explorer still starting) and NotifyIcon never retries, so Otto ran with no icon. A
/// watchdog asks the shell whether the icon is there (Shell_NotifyIconGetRect) at 3, 10, 30, 60, 120 s, then every 30 s,
/// and after resume / unlock, and adds it again when it is missing.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly Icon _drawnIcon;
    private readonly Window _anchor;
    private readonly ContextMenu _menu;
    private readonly MenuItem _autoStartItem;
    private readonly MenuItem _updateItem;
    private readonly Separator _updateSeparator;

    public event Action? LeftClick;
    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;
    public event Action<bool>? AutoStartToggled;
    public event Action? UpdateRequested;
    public event Action? CheckUpdatesRequested;
    public event Action? BalloonClicked;
    public Func<bool> IsAutoStart { get; set; } = () => false;

    // NotifyIcon keeps its owner window and icon id private; read once by reflection (field names differ across
    // WinForms versions, so match by type). Unreadable = the watchdog re-adds blindly, only at the first checks.
    private static readonly FieldInfo? WindowField = typeof(WF.NotifyIcon).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .FirstOrDefault(f => typeof(WF.NativeWindow).IsAssignableFrom(f.FieldType));
    private static readonly FieldInfo? IdField = typeof(WF.NotifyIcon).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .FirstOrDefault(f => (f.FieldType == typeof(uint) || f.FieldType == typeof(int)) && f.Name.TrimStart('_').Equals("id", StringComparison.OrdinalIgnoreCase));
    /// <summary>True when this WinForms build lets us ask the shell about our icon (Probe checks it).</summary>
    public static bool CanInspect => WindowField != null && IdField != null;

    private static readonly int[] EarlyChecksSec = { 3, 10, 30, 60, 120 };
    private const int SteadyCheckSec = 30;
    private readonly DispatcherTimer _watch;
    private readonly DateTime _started = DateTime.UtcNow;
    private int _checkIdx;
    private bool _distrust; // a re-add the shell still reports missing: stop trusting the check (no re-add loop)

    public TrayIcon()
    {
        _drawnIcon = DrawIcon();
        _icon = new WF.NotifyIcon { Text = "Otto — per-app volume", Icon = _drawnIcon, Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) LeftClick?.Invoke();
            else if (e.Button == WF.MouseButtons.Right) ShowMenu();
        };
        _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke();

        // Invisible 1×1 window: a WPF ContextMenu needs an owning window to take focus, otherwise it never closes.
        _anchor = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, Topmost = true,
            Opacity = 0, ResizeMode = ResizeMode.NoResize, ShowActivated = true
        };

        _menu = new ContextMenu
        {
            Style = (Style)Application.Current.FindResource("DarkContextMenu"),
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
        };
        _updateItem = AddItem("Update", () => UpdateRequested?.Invoke());
        _updateItem.FontWeight = FontWeights.SemiBold;
        _updateItem.Visibility = Visibility.Collapsed;
        _updateSeparator = new Separator { Style = (Style)Application.Current.FindResource("DarkMenuSeparator"), Visibility = Visibility.Collapsed };
        _menu.Items.Add(_updateSeparator);
        AddItem("Open mixer", () => OpenRequested?.Invoke());
        AddItem("Settings…", () => SettingsRequested?.Invoke());
        _menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource("DarkMenuSeparator") });
        _autoStartItem = AddItem("Start with Windows", () =>
        {
            bool on = !_autoStartItem!.IsChecked;
            _autoStartItem.IsChecked = on;
            AutoStartToggled?.Invoke(on);
        });
        AddItem("Check for updates", () => CheckUpdatesRequested?.Invoke());
        _menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource("DarkMenuSeparator") });
        AddItem("Quit Otto", () => QuitRequested?.Invoke());
        _menu.Closed += (_, _) => _anchor.Hide();

        _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(EarlyChecksSec[0]) };
        _watch.Tick += (_, _) => OnWatch();
        _watch.Start();
        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    // ---- self-repair ---------------------------------------------------------------------------

    /// <summary>Is the icon in the notification area? null when the shell cannot be asked (no owner window / no reflection).</summary>
    public bool? IsShown()
    {
        try
        {
            if (!CanInspect || _distrust || WindowField!.GetValue(_icon) is not WF.NativeWindow w || w.Handle == IntPtr.Zero) return null;
            var id = new NOTIFYICONIDENTIFIER { hWnd = w.Handle, uID = Convert.ToUInt32(IdField!.GetValue(_icon)) };
            id.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONIDENTIFIER>();
            // S_OK or S_FALSE (seen on Windows 10 for a visible icon) both mean "there"; only a failure code means lost.
            return Shell_NotifyIconGetRect(ref id, out _) >= 0;
        }
        catch { return null; }
    }

    private void OnWatch()
    {
        _checkIdx++;
        EnsureShown(_checkIdx <= EarlyChecksSec.Length ? "startup" : "watchdog", blindOk: _checkIdx <= 2);
        double next = _checkIdx < EarlyChecksSec.Length ? EarlyChecksSec[_checkIdx] - EarlyChecksSec[_checkIdx - 1] : SteadyCheckSec;
        _watch.Interval = TimeSpan.FromSeconds(next);
    }

    /// <summary>
    /// Add the icon again when the shell lost it. blindOk: when the shell cannot be asked, re-add anyway (only at the
    /// first checks after start / resume, so a working icon never flickers every 30 s).
    /// </summary>
    public void EnsureShown(string why, bool blindOk = false)
    {
        try
        {
            if (FindWindow("Shell_TrayWnd", null) == IntPtr.Zero) return; // no taskbar yet: the shell's TaskbarCreated will add it
            bool? shown = IsShown();
            if (shown == true || (shown == null && !blindOk)) return;
            _icon.Visible = false;
            _icon.Visible = true;
            bool? after = IsShown();
            if (after == false) _distrust = true;
            Log.Write($"[tray] icon re-added ({why}, {(shown == null ? "unchecked" : "was missing")}, {(int)(DateTime.UtcNow - _started).TotalSeconds}s after start): {(after == null ? "unknown" : after.Value ? "shown" : "still missing")}");
        }
        catch (Exception ex) { Log.Error("tray repair", ex); }
    }

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) RecheckSoon("resume");
    }
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.ConsoleConnect) RecheckSoon(e.Reason.ToString());
    }
    /// <summary>After resume / unlock: check at 2 s and 10 s (SystemEvents fire on their own thread).</summary>
    private void RecheckSoon(string why)
    {
        Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(2000); EnsureShown(why, blindOk: true);
            await Task.Delay(8000); EnsureShown(why);
        });
    }

    private MenuItem AddItem(string header, Action action)
    {
        var item = new MenuItem { Header = header, Style = (Style)Application.Current.FindResource("DarkMenuItem") };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
        return item;
    }

    private void ShowMenu()
    {
        try
        {
            _autoStartItem.IsChecked = IsAutoStart();
            GetCursorPos(out var pt);
            double scale = GetScaleForPoint(pt);
            _anchor.Left = pt.x / scale;
            _anchor.Top = pt.y / scale;
            _anchor.Show();
            _anchor.Activate();
            SetForegroundWindow(new WindowInteropHelper(_anchor).Handle);
            _menu.PlacementTarget = _anchor;
            _menu.IsOpen = true;
        }
        catch (Exception ex) { Log.Error("tray menu", ex); }
    }

    /// <summary>Shows "Update to vX" at the top of the menu; null hides it.</summary>
    public void SetUpdate(string? label)
    {
        _updateItem.Header = label ?? "Update";
        var v = label == null ? Visibility.Collapsed : Visibility.Visible;
        _updateItem.Visibility = v;
        _updateSeparator.Visibility = v;
        _icon.Text = label == null ? "Otto — per-app volume" : $"Otto — {label.ToLowerInvariant()} available";
    }

    public void Balloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(6000, title, text, WF.ToolTipIcon.None); } catch { }
    }

    /// <summary>Green rounded square (Otto's cloak) with three faders, drawn at the system's small-icon size so it stays crisp per DPI.</summary>
    private static Icon DrawIcon()
    {
        int size = Math.Max(16, WF.SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = RoundedRect(new RectangleF(0.5f, 0.5f, size - 1, size - 1), size * 0.26f);
            using var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), Color.FromArgb(0x46, 0xA8, 0x73), Color.FromArgb(0x24, 0x5E, 0x40));
            g.FillPath(bg, path);

            float[] xs = { 0.30f, 0.50f, 0.70f };
            float[] ys = { 0.62f, 0.40f, 0.72f };
            using var track = new Pen(Color.FromArgb(0x1B, 0x4D, 0x33), Math.Max(1f, size * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var knob = new SolidBrush(Color.White);
            for (int i = 0; i < 3; i++)
            {
                g.DrawLine(track, xs[i] * size, size * 0.22f, xs[i] * size, size * 0.78f);
                float r = size * 0.11f;
                g.FillEllipse(knob, xs[i] * size - r, ys[i] * size - r, 2 * r, 2 * r);
            }
        }
        var h = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { DestroyIcon(h); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _watch.Stop();
        SystemEvents.PowerModeChanged -= OnPowerMode;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        try { _icon.Visible = false; _icon.Dispose(); _drawnIcon.Dispose(); _anchor.Close(); } catch { }
    }
}
