using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Otto.Audio;
using Otto.Audio.FxSound;
using Otto.Native;
using Otto.Rules;
using static Otto.Native.NativeMethods;

namespace Otto.UI;

public partial class FlyoutWindow : Window
{
    private const double MarginDip = 16;  // transparent shadow margin around the panel

    private readonly AudioSessionService _service;
    private readonly Func<Settings> _settings;
    private bool _hiding;
    private bool _pinned;
    private DateTime _lastHidden = DateTime.MinValue;
    private POINT _anchor;
    private int _wheelAccum;
    private bool _fxBusy;

    public event Action? SettingsRequested;
    /// <summary>Raised when the flyout becomes open or starts closing (for the click-outside watcher).</summary>
    public event Action? OpenStateChanged;

    public bool IsPinned => _pinned;
    public bool IsOpen => IsVisible && !_hiding;

    /// <summary>Panel rectangle in screen pixels (shadow margin excluded).</summary>
    public RECT? ScreenRect
    {
        get
        {
            if (!IsVisible) return null;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!GetWindowRect(hwnd, out var r)) return null;
            int m = (int)Math.Round(MarginDip * VisualTreeHelper.GetDpi(this).DpiScaleX);
            return new RECT { left = r.left + m, top = r.top + m, right = r.right - m, bottom = r.bottom - m };
        }
    }

    public FlyoutWindow(AudioSessionService service, Func<Settings> settings)
    {
        InitializeComponent();
        _service = service;
        _settings = settings;
        DataContext = service;

        SourceInitialized += (_, _) => WindowEffects.MakeToolWindow(this, noActivate: false);
        Deactivated += (_, _) => { if (!_pinned) HideAnimated(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { HideAnimated(); e.Handled = true; } };
        // SizeToContent grows the panel downwards when a row appears: keep the bottom edge on the anchor (or on screen when pinned).
        SizeChanged += (_, _) =>
        {
            if (!IsVisible) return;
            if (_pinned) KeepOnScreen(); else Position(_anchor);
        };
        service.Apps.CollectionChanged += (_, _) => UpdateEmptyState();
        service.Master.PropertyChanged += Master_PropertyChanged;
        UpdateEmptyState();
        UpdateMasterGlyph();
        UpdatePinGlyph();
        if (service.Boost?.Fx is { } fx) fx.StatusChanged += () => Dispatcher.BeginInvoke(UpdateFxPill);
    }

    // ---- open / close --------------------------------------------------------------------

    public void Toggle(POINT at)
    {
        if (IsVisible) { if (!_hiding) HideAnimated(); return; } // a toggle during the fade just lets it finish closing
        if ((DateTime.UtcNow - _lastHidden).TotalMilliseconds < 350) return; // the click on the tray icon already closed us (Deactivated)
        ShowAt(at);
    }

    public void ShowAt(POINT anchor)
    {
        var s = _settings();
        HintText.Text = $"Hold {s.ModifierLabel} + scroll over any window   ·   {s.Hotkey}";
        _service.MetersEnabled = true;
        UpdateFxPill();
        if (_service.Boost?.Fx is { Installed: true } fx && FxSoundLink.Running) _ = fx.RefreshAsync(); // it may have been switched in its own tray menu
        _hiding = false;
        _anchor = anchor;
        SetPinned(false);

        BeginAnimation(OpacityProperty, null);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        Opacity = 0;
        RootTranslate.Y = 10;

        if (!IsVisible) Show();
        UpdateLayout();
        Position(anchor);
        Activate();
        if (!IsActive)
        {
            // Opened from a hook-swallowed click or another process: Windows refuses the foreground unless we "typed" something first.
            SendCancelAltMenuKey();
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
            Activate();
        }
        OpenStateChanged?.Invoke();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    /// <summary>Close unless the user pinned / dragged the panel.</summary>
    public void HideIfUnpinned()
    {
        if (!_pinned) HideAnimated();
    }

    public void HideAnimated()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        SetPinned(false);
        _lastHidden = DateTime.UtcNow; // at the start: the tray click that deactivated us arrives before the fade ends
        OpenStateChanged?.Invoke();
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return;
            Hide();
            _hiding = false;
            _lastHidden = DateTime.UtcNow;
            _service.MetersEnabled = false;
        };
        BeginAnimation(OpacityProperty, fade);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
    }

    // ---- placement -----------------------------------------------------------------------

    /// <summary>Panel bottom-right corner near the anchor (tray icon / cursor), clamped to the monitor work area. Pixel math.</summary>
    private void Position(POINT anchor)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var work = GetWorkAreaForPoint(anchor, out var mon);
        double scale = GetScaleForMonitor(mon); // the TARGET monitor's DPI: WPF rescales the window once it lands there
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        int m = (int)Math.Round(MarginDip * scale);

        int x = anchor.x - w + m + (int)(12 * scale);
        int y = anchor.y - h + m - (int)(8 * scale);
        x = Math.Clamp(x, work.left - m, Math.Max(work.left - m, work.right - w + m));
        y = Math.Clamp(y, work.top - m, Math.Max(work.top - m, work.bottom - h + m));
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        if (IsOpen) OpenStateChanged?.Invoke();
    }

    /// <summary>Pinned and resized: keep the panel inside the work area of the monitor it is on.</summary>
    private void KeepOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var r)) return;
        var work = GetWorkAreaForWindow(hwnd, out var mon);
        int m = (int)Math.Round(MarginDip * GetScaleForMonitor(mon));
        int x = Math.Clamp(r.left, work.left - m, Math.Max(work.left - m, work.right - r.Width + m));
        int y = Math.Clamp(r.top, work.top - m, Math.Max(work.top - m, work.bottom - r.Height + m));
        if (x != r.left || y != r.top) SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        OpenStateChanged?.Invoke();
    }

    // ---- pin / drag ----------------------------------------------------------------------

    private void SetPinned(bool pinned)
    {
        if (_pinned == pinned) return;
        _pinned = pinned;
        UpdatePinGlyph();
    }

    private void UpdatePinGlyph()
    {
        PinGlyph.Text = _pinned ? "" : "";
        PinGlyph.Foreground = (Brush)FindResource(_pinned ? "AccentBrush" : "TextDimBrush");
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (_pinned) HideAnimated();
        else SetPinned(true);
    }

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var before);
        try { DragMove(); } catch { return; } // DragMove throws if the button was already released
        GetWindowRect(hwnd, out var after);
        if (Math.Abs(after.left - before.left) > 3 || Math.Abs(after.top - before.top) > 3)
        {
            SetPinned(true); // moved it on purpose: it stays until closed
            OpenStateChanged?.Invoke();
        }
    }

    // ---- misc ----------------------------------------------------------------------------

    private void UpdateEmptyState() => EmptyText.Visibility = _service.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Master_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MasterAudio.Muted) or nameof(MasterAudio.VolumePercent)) UpdateMasterGlyph();
    }

    private void UpdateMasterGlyph()
    {
        var m = _service.Master;
        MasterGlyph.Text = m.Muted ? "" : m.VolumePercent switch { <= 0 => "", <= 33 => "", <= 66 => "", _ => "" };
        MasterGlyph.Foreground = (Brush)FindResource(m.Muted ? "TextDimBrush" : "TextBrush");
        MasterPercent.Text = m.Muted ? "Muted" : $"{m.VolumePercent:0}";
    }

    private bool FxOn => FxSoundLink.Running && _service.Boost?.Fx.Status?.Power == true;

    private void UpdateFxPill()
    {
        var fx = _service.Boost?.Fx;
        FxPill.Visibility = fx?.Installed == true ? Visibility.Visible : Visibility.Collapsed;
        bool on = FxOn;
        FxPill.Background = (Brush)FindResource(on ? "AccentSoftBrush" : "ElevatedBrush");
        FxGlyph.Foreground = (Brush)FindResource(on ? "AccentBrush" : "TextDimBrush");
        FxText.Foreground = (Brush)FindResource(on ? "TextBrush" : "TextDimBrush");
        FxPill.Opacity = _fxBusy ? 0.6 : 1;
    }

    // ---- handlers ------------------------------------------------------------------------

    private async void Fx_Click(object sender, RoutedEventArgs e)
    {
        var fx = _service.Boost?.Fx;
        if (fx == null || _fxBusy) return;
        _fxBusy = true; UpdateFxPill();
        try
        {
            await fx.SetPowerAsync(!FxOn);
            await fx.RefreshAsync();
            _service.BoostSettingsChanged(); // Windows moves to FxSound's driver and back: boost follows
        }
        finally { _fxBusy = false; UpdateFxPill(); }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppAudio app) app.Muted = !app.Muted;
    }

    private void MasterMute_Click(object sender, RoutedEventArgs e) => _service.Master.Muted = !_service.Master.Muted;

    /// <summary>Whole notches only; precision touchpads deliver many small deltas.</summary>
    private int Notches(int delta)
    {
        if (Math.Sign(delta) != Math.Sign(_wheelAccum)) _wheelAccum = 0;
        _wheelAccum += delta;
        int n = _wheelAccum / 120;
        _wheelAccum -= n * 120;
        return Math.Clamp(n, -5, 5);
    }

    private void Row_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not AppAudio app) return;
        int n = Notches(e.Delta);
        if (n != 0) app.Nudge(n * _settings().ScrollStep);
    }

    private void MasterRow_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        int n = Notches(e.Delta);
        if (n != 0) _service.Master.Nudge(n * _settings().ScrollStep);
    }

    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        HideAnimated();
        SettingsRequested?.Invoke();
    }
}
