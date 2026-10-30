using System.Windows;
using System.Windows.Media;
using Otto.Audio;
using Otto.Native;
using Otto.Rules;
using Otto.Tray;
using Otto.UI;
using static Otto.Native.NativeMethods;

namespace Otto;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private Settings _settings = new();
    private AudioSessionService? _service;
    private RulesEngine? _rules;
    private FlyoutWindow? _flyout;
    private OsdWindow? _osd;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkey;
    private GestureController? _gesture;
    private TaskbarTracker? _taskbar;
    private UpdateService? _updates;
    private EventWaitHandle? _quitSignal;
    private System.Windows.Threading.DispatcherTimer? _saveTimer;
    private string _appliedHotkey = string.Empty;
    private string _appliedAccent = string.Empty;
    private string _appliedBoost = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("unhandled", a.ExceptionObject as Exception ?? new Exception("unknown"));
        DispatcherUnhandledException += (_, a) => { Log.Error("dispatcher", a.Exception); a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("task", a.Exception); a.SetObserved(); };

        if (e.Args.Length > 0 && e.Args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
        {
            int code = 1;
            try { code = Probe.Run(e.Args.Length > 1 && !e.Args[1].StartsWith("--") ? e.Args[1] : "fader-probe.json", e.Args.Any(a => a.Equals("--download", StringComparison.OrdinalIgnoreCase))); }
            catch (Exception ex) { Log.Error("probe", ex); }
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--tone", StringComparison.OrdinalIgnoreCase)) { Shutdown(BoostSelfTest.Tone()); return; }
        if (e.Args.Length > 0 && e.Args[0].Equals("--fx-selftest", StringComparison.OrdinalIgnoreCase))
        {
            int code = 1;
            try { code = FxSelfTest.Run(e.Args.Length > 1 ? e.Args[1] : "fx-selftest.json", e.Args.Length > 2 ? e.Args[2] : null); }
            catch (Exception ex) { Log.Error("fx selftest", ex); }
            Shutdown(code);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0].Equals("--boost-selftest", StringComparison.OrdinalIgnoreCase))
        {
            int code = 1;
            try { code = BoostSelfTest.Run(e.Args.Length > 1 ? e.Args[1] : "boost-selftest.json", e.Args.Length > 2 ? e.Args[2] : null); }
            catch (Exception ex) { Log.Error("boost selftest", ex); }
            Shutdown(code);
            return;
        }

        bool wantQuit = e.Args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase));
        bool wantAutostart = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        bool quiet = e.Args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        bool updateNow = e.Args.Any(a => a.Equals("--update-now", StringComparison.OrdinalIgnoreCase));
        int updatedIdx = Array.FindIndex(e.Args, a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase));
        bool justUpdated = updatedIdx >= 0;
        // Started by the updater: let the previous version finish exiting before taking the single-instance lock.
        if (justUpdated && updatedIdx + 1 < e.Args.Length && int.TryParse(e.Args[updatedIdx + 1], out int previousPid)) Updater.AfterUpdate(previousPid);

        // Renamed from Fader in 0.4.0. A single-file exe cannot rename itself while it runs (the runtime keeps
        // reading it), so an updated Fader.exe hands over to a fresh Otto.exe copy and exits.
        if (!wantQuit && HandOffFromFaderExe(quiet)) { Shutdown(0); return; }

        // Single instance: a second launch asks the first one to open the mixer (or, with --quit, to exit cleanly).
        _mutex = new Mutex(true, @"Local\Otto.SingleInstance", out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Otto.ShowFlyout");
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Otto.Quit");
        if (!isFirst)
        {
            if (wantQuit) _quitSignal.Set();
            else { AllowSetForegroundWindow(ASFW_ANY); _showSignal.Set(); } // let the running instance bring its flyout to the front
            Shutdown(0);
            return;
        }
        if (wantQuit) { Shutdown(0); return; } // nothing was running
        var signalThread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showSignal, _quitSignal };
            while (true)
            {
                int which = WaitHandle.WaitAny(handles);
                if (which == 1) { Dispatcher.BeginInvoke(() => Shutdown()); return; }
                Dispatcher.BeginInvoke(ToggleFlyout);
            }
        }) { IsBackground = true, Name = "Otto.ShowSignal" };
        signalThread.Start();

        MigrateFromFader();
        _settings = Settings.Load();
        ApplyAccent(_settings.Accent);

        _service = new AudioSessionService(Dispatcher, () => _settings);
        _rules = new RulesEngine(_service, () => _settings);
        _service.Start();

        _osd = new OsdWindow(() => _settings);
        CreateFlyout();

        _taskbar = new TaskbarTracker(Dispatcher, () => _service?.Master.VolumePercent ?? -1);
        _gesture = new GestureController(_service, () => _settings, _osd, Dispatcher, _taskbar);
        _gesture.SpeakerClicked += pt => _flyout?.Toggle(pt);
        _gesture.ClickedOutsideFlyout += () => _flyout?.HideIfUnpinned();
        _hotkey = new HotkeyManager();
        _hotkey.Pressed += ToggleFlyout;
        _hotkey.Register(_settings.Hotkey);
        _appliedHotkey = _settings.Hotkey;
        _appliedAccent = _settings.Accent;
        _appliedBoost = $"{_settings.BoostEnabled}|{_settings.BoostMax}|{_settings.BoostDevice}|{_settings.BoostVia}";

        _tray = new TrayIcon { IsAutoStart = AutoStart.IsEnabled };
        _tray.LeftClick += ToggleFlyout;
        _tray.OpenRequested += ToggleFlyout;
        _tray.SettingsRequested += OpenSettings;
        _tray.QuitRequested += () => Shutdown();
        _tray.AutoStartToggled += on => AutoStart.Set(on);

        _updates = new UpdateService(Dispatcher, () => _settings.AutoCheckUpdates);
        _updates.Found += info =>
        {
            _tray?.SetUpdate(_updates.UpdateLabel);
            _tray?.Balloon($"Otto {info.Version.ToString(3)} is available", "Right-click the tray icon → " + _updates.UpdateLabel + ", or click here for details.");
        };
        _updates.RestartRequested += () => Shutdown();
        _tray.UpdateRequested += () => _ = _updates.InstallAsync();
        _tray.BalloonClicked += () => { if (_updates.HasUpdate) OpenSettings(); };
        _tray.CheckUpdatesRequested += async () =>
        {
            await _updates.CheckAsync(manual: true);
            if (!_updates.HasUpdate) _tray?.Balloon("Otto is up to date", _updates.Status);
        };
        Updater.CleanupLeftovers();

        _rules.Applied += (app, what) => { if (_settings.ShowOsd) _osd.ShowMessage(app.Name, app.Icon, what, IntPtr.Zero, app.VolumePercent); };

        if (!_settings.FirstRunDone)
        {
            _tray.Balloon("Otto is running in the tray",
                $"Click the Windows speaker icon or press {_settings.Hotkey} for the mixer. Scroll over an app's taskbar button, or hold {_settings.ModifierLabel} and scroll over its window, to change its volume.");
            _settings.FirstRunDone = true;
            _settings.Save();
        }
        if (wantAutostart) AutoStart.Set(true);
        if (justUpdated && !quiet) _tray.Balloon($"Otto updated to v{_updates.Current}", "Everything is where you left it. See what's new on the release page.");
        if (updateNow) Dispatcher.BeginInvoke(async () =>
        {
            await _updates.CheckAsync(manual: true);
            if (_updates.HasUpdate) await _updates.InstallAsync(quiet: true);
            else Log.Write("[update] --update-now: " + _updates.Status);
        });
        Log.Write($"[app] started v{typeof(App).Assembly.GetName().Version} hook={_gesture.HookInstalled} data={Log.Dir}");
    }

    /// <summary>Running as Fader.exe: copy to Otto.exe next to it, start that (it waits for this process to exit), and stop here.</summary>
    private static bool HandOffFromFaderExe(bool quiet)
    {
        try
        {
            string exe = Updater.ExePath;
            if (!System.IO.Path.GetFileName(exe).Equals("Fader.exe", StringComparison.OrdinalIgnoreCase)) return false;
            string target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe)!, "Otto.exe");
            System.IO.File.Copy(exe, target, overwrite: true);
            var psi = new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(target)! };
            psi.ArgumentList.Add("--updated");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            if (quiet) psi.ArgumentList.Add("--quiet");
            System.Diagnostics.Process.Start(psi);
            Log.Write($"[migrate] {exe} handed over to {target}");
            return true;
        }
        catch (Exception ex) { Log.Error("migrate hand-off", ex); return false; } // keep running as Fader.exe
    }

    /// <summary>After the hand-off: remove Fader's exe and leftovers, move the start-with-Windows entry to Otto.</summary>
    private static void MigrateFromFader()
    {
        string dir = System.IO.Path.GetDirectoryName(Updater.ExePath)!;
        Task.Run(async () =>
        {
            foreach (var name in new[] { "Fader.exe", "Fader.exe.old", "Fader.update.exe" })
            {
                string path = System.IO.Path.Combine(dir, name);
                for (int i = 0; i < 10 && System.IO.File.Exists(path); i++)
                {
                    try { System.IO.File.Delete(path); Log.Write($"[migrate] removed {path}"); }
                    catch { await Task.Delay(1000); }
                }
            }
        });
        AutoStart.MigrateLegacy();
    }

    private void CreateFlyout()
    {
        _flyout = new FlyoutWindow(_service!, () => _settings);
        _flyout.SettingsRequested += OpenSettings;
        _flyout.OpenStateChanged += () => _gesture?.SetFlyoutRect(_flyout.IsOpen ? _flyout.ScreenRect : null);
    }

    private void ToggleFlyout()
    {
        if (_flyout == null) return;
        GetCursorPos(out var pt);
        _flyout.Toggle(pt);
    }

    private void OpenSettings()
    {
        if (_service == null) return;
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, _service, _updates!, OnSettingsChanged);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Called for every edit in the settings window (slider drags included): save debounced, re-apply only what changed.</summary>
    private void OnSettingsChanged()
    {
        if (_settings.Hotkey != _appliedHotkey) { _hotkey?.Register(_settings.Hotkey); _appliedHotkey = _settings.Hotkey; }
        if (_settings.Accent != _appliedAccent) { ApplyAccent(_settings.Accent); _appliedAccent = _settings.Accent; }
        _gesture?.ApplySettings();
        var boost = $"{_settings.BoostEnabled}|{_settings.BoostMax}|{_settings.BoostDevice}|{_settings.BoostVia}";
        if (boost != _appliedBoost) { _appliedBoost = boost; _service?.BoostSettingsChanged(); }
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTimer_Tick;
        _saveTimer.Tick += SaveTimer_Tick;
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        _settings.Save();
    }

    private void ApplyAccent(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            _appliedAccent = hex;
            Resources["AccentBrush"] = Freeze(new SolidColorBrush(c));
            Resources["AccentSoftBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B)));
            Resources["AccentGradient"] = Freeze(new LinearGradientBrush(Shade(c, 0.88), Shade(c, 1.18), 0));
        }
        catch (Exception ex) { Log.Error("accent " + hex, ex); }
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static Color Shade(Color c, double factor)
        => Color.FromRgb((byte)Math.Clamp(c.R * factor, 0, 255), (byte)Math.Clamp(c.G * factor, 0, 255), (byte)Math.Clamp(c.B * factor, 0, 255));

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_saveTimer is { IsEnabled: true }) { _saveTimer.Stop(); _settings.Save(); }
            _gesture?.Dispose();
            _taskbar?.Dispose();
            _hotkey?.Dispose();
            _tray?.Dispose();
            _service?.Dispose();
            if (_mutex != null) { try { _mutex.ReleaseMutex(); } catch { } _mutex.Dispose(); }
            Log.Write("[app] exit");
        }
        catch (Exception ex) { Log.Error("exit", ex); }
        base.OnExit(e);
    }
}
