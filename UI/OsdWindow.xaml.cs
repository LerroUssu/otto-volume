using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Otto.Native;
using Otto.Rules;
using static Otto.Native.NativeMethods;

namespace Otto.UI;

/// <summary>
/// The little glass pill that shows a level. Always at the same spot (top / centre / bottom of the
/// monitor the cursor is on, per settings), whatever was scrolled. Never takes focus, clicks pass through it.
/// </summary>
public partial class OsdWindow : Window
{
    private const double BarWidth = 170;
    private readonly DispatcherTimer _hideTimer;
    private readonly Func<Settings> _settings;
    private bool _hiding;
    private bool _sticky;

    public OsdWindow(Func<Settings> settings)
    {
        InitializeComponent();
        _settings = settings;
        SourceInitialized += (_, _) => WindowEffects.MakeToolWindow(this, noActivate: true);
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); FadeOut(); };
    }

    /// <summary>Level pill. The anchor window only decides which monitor; the position on it comes from settings.</summary>
    public void ShowLevel(string name, ImageSource? icon, double percent, bool muted, IntPtr anchorWindow)
    {
        FillLevel(name, icon, percent, muted);
        Present(MonitorFor(anchorWindow), sticky: false);
    }

    /// <summary>Level pill while hovering / scrolling a taskbar button. sticky = stays until <see cref="Release"/>.</summary>
    public void ShowLevelAt(string name, ImageSource? icon, double percent, bool muted, RECT anchor, bool sticky)
    {
        FillLevel(name, icon, percent, muted);
        Present(MonitorFor(anchor), sticky);
    }

    /// <summary>Short notice, e.g. "Chrome · no audio right now" or "Discord · start 50%".</summary>
    public void ShowMessage(string name, ImageSource? icon, string message, IntPtr anchorWindow, double? percent = null)
    {
        FillMessage(name, icon, message, percent);
        Present(MonitorFor(anchorWindow), sticky: false);
    }

    public void ShowMessageAt(string name, ImageSource? icon, string message, RECT anchor)
    {
        FillMessage(name, icon, message, null);
        Present(MonitorFor(anchor), sticky: false);
    }

    /// <summary>End a sticky pill: it fades shortly after.</summary>
    public void Release()
    {
        if (!_sticky) return;
        _sticky = false;
        _hideTimer.Interval = TimeSpan.FromMilliseconds(250);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void FillLevel(string name, ImageSource? icon, double percent, bool muted)
    {
        NameText.Text = name;
        SetIcon(icon, muted ? "" : "");
        Fill.Width = BarWidth * Math.Clamp(percent, 0, 100) / 100.0;
        Fill.Opacity = muted ? 0.35 : 1;
        ValueText.Text = muted ? "Muted" : $"{percent:0}";
        MarkBoost(!muted && percent > 100);
    }

    /// <summary>Above 100 % the number takes the accent colour (the bar is already full).</summary>
    private void MarkBoost(bool boosted)
    {
        if (boosted) ValueText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentBrush");
        else ValueText.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
    }

    private void FillMessage(string name, ImageSource? icon, string message, double? percent)
    {
        NameText.Text = $"{name}  ·  {message}";
        SetIcon(icon, "");
        Fill.Width = percent is double p ? BarWidth * Math.Clamp(p, 0, 100) / 100.0 : 0;
        Fill.Opacity = 1;
        ValueText.Text = percent is double q ? $"{q:0}" : string.Empty;
        MarkBoost(percent > 100);
    }

    private void SetIcon(ImageSource? icon, string glyph)
    {
        if (icon != null)
        {
            IconBrush.ImageSource = icon;
            IconRect.Visibility = Visibility.Visible;
            GlyphText.Visibility = Visibility.Collapsed;
        }
        else
        {
            IconRect.Visibility = Visibility.Collapsed;
            GlyphText.Visibility = Visibility.Visible;
            GlyphText.Text = glyph;
        }
    }

    // ---- placement -----------------------------------------------------------------------

    /// <summary>The monitor to show on: the cursor's, unless the anchor says otherwise.</summary>
    private static IntPtr MonitorFor(IntPtr anchorWindow)
    {
        GetCursorPos(out var pt);
        return MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
    }

    private static IntPtr MonitorFor(RECT anchor)
    {
        var center = new POINT((anchor.left + anchor.right) / 2, (anchor.top + anchor.bottom) / 2);
        return MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
    }

    private void Present(IntPtr monitor, bool sticky)
    {
        _hiding = false;
        BeginAnimation(OpacityProperty, null);
        if (!IsVisible) { Opacity = 0; Show(); } // stays invisible until positioned
        UpdateLayout();

        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);
        var work = mi.rcWork;
        double ms = GetScaleForMonitor(monitor); // the TARGET monitor's DPI: WPF rescales the window once it lands there
        int w = (int)Math.Ceiling(ActualWidth * ms), h = (int)Math.Ceiling(ActualHeight * ms);
        int margin = (int)(48 * ms);

        int x = work.left + (work.Width - w) / 2;
        int y = _settings().OsdPosition switch
        {
            OsdPosition.Top => work.top + margin,
            OsdPosition.Center => work.top + (work.Height - h) / 2,
            _ => work.bottom - h - margin,
        };
        x = Math.Clamp(x, work.left, Math.Max(work.left, work.right - w));
        y = Math.Clamp(y, work.top, Math.Max(work.top, work.bottom - h));

        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Opacity = 1;

        _sticky = sticky;
        _hideTimer.Stop();
        if (!sticky)
        {
            _hideTimer.Interval = TimeSpan.FromMilliseconds(1100);
            _hideTimer.Start();
        }
    }

    private void FadeOut()
    {
        if (!IsVisible || _sticky) return;
        _hiding = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => { if (_hiding) { Hide(); _hiding = false; } };
        BeginAnimation(OpacityProperty, fade);
    }
}
