using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using Otto.Audio.Boost;
using Otto.Native;
using Otto.Rules;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using static Otto.Native.NativeMethods;

namespace Otto.Audio;

/// <summary>
/// Tracks every audio session on the default output device, grouped per application, and keeps
/// the groups in sync with Core Audio notifications. All public state lives on the UI dispatcher.
/// </summary>
public sealed class AudioSessionService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly NotificationClient _notifications;
    private readonly Dictionary<string, AppAudio> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SessionEntry> _sessions = new();
    private readonly Dictionary<string, DateTime> _lastGone = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _userAdjusted = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _housekeepingTimer;
    private readonly DispatcherTimer _rebindTimer;
    private readonly DispatcherTimer _devicesTimer;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private MMDevice? _device;
    private AudioSessionManager? _manager;
    private bool _metersEnabled;

    /// <summary>When the current device was bound; sessions re-created right after a device switch are not launches.</summary>
    public DateTime LastBindUtc { get; private set; } = DateTime.MinValue;

    public ObservableCollection<AppAudio> Apps { get; } = new();
    public MasterAudio Master { get; }
    /// <summary>Volume above 100 %; null in the headless probe.</summary>
    public BoostEngine? Boost { get; }

    /// <summary>(app, session, initialEnumeration). Fired after the session is in the model.</summary>
    public event Action<AppAudio, SessionEntry, bool>? SessionAdded;
    /// <summary>A session's volume changed outside Otto (Windows mixer, the app itself, …).</summary>
    public event Action<AppAudio, SessionEntry, float>? SessionVolumeChanged;
    public event Action? DeviceChanged;

    public AudioSessionService(Dispatcher ui, Func<Settings>? settings = null)
    {
        _ui = ui;
        Master = new MasterAudio(ui);
        _enumerator = new MMDeviceEnumerator();
        _notifications = new NotificationClient(this);
        try { _enumerator.RegisterEndpointNotificationCallback(_notifications); } catch (Exception ex) { Log.Error("device notifications", ex); }

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render, ui) { Interval = TimeSpan.FromMilliseconds(40) };
        _meterTimer.Tick += (_, _) => MeterTick();
        _housekeepingTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(3) };
        _housekeepingTimer.Tick += (_, _) => Housekeeping();
        _rebindTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(400) };
        _rebindTimer.Tick += (_, _) =>
        {
            _rebindTimer.Stop();
            // Moving an app to another output notifies too; rebind only when the default really changed.
            try { if (_device != null && _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID == _device.ID) return; } catch { }
            BindDefaultDevice();
        };
        _devicesTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(800) };
        _devicesTimer.Tick += (_, _) => { _devicesTimer.Stop(); Boost?.RefreshSink(); };

        if (settings != null)
        {
            Boost = new BoostEngine(ui, this, settings);
            Boost.Changed += ApplyBoostRange;
            Master.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MasterAudio.VolumePercent) or nameof(MasterAudio.Muted)) Boost.Evaluate(); };
        }
    }

    private void ApplyBoostRange()
    {
        if (Boost == null) return;
        Master.MaxPercent = Boost.MaxPercent;
        foreach (var a in Apps) a.MaxPercent = a.IsSystemSounds || Boost.IsBlocked(a.Key) ? 100 : Boost.MaxPercent;
        Boost.Evaluate();
    }

    /// <summary>Settings for boost changed (on/off, limit, spare output).</summary>
    public void BoostSettingsChanged() => Boost?.RefreshSink();

    public void Start()
    {
        BindDefaultDevice();
        _housekeepingTimer.Start();
    }

    public bool MetersEnabled
    {
        get => _metersEnabled;
        set
        {
            _metersEnabled = value;
            _meterTimer.IsEnabled = value;
            if (!value) { foreach (var a in Apps) a.Peak = 0; Master.Peak = 0; }
        }
    }

    public string DeviceId => _device?.ID ?? string.Empty;

    // ---- device binding ------------------------------------------------------------------

    private void BindDefaultDevice()
    {
        ClearSessions();
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) { Master.Unbind(); Boost?.BindRender(null); DeviceChanged?.Invoke(); return; }
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex) { Log.Error("default device", ex); Master.Unbind(); return; }

        Master.Bind(_device);
        try
        {
            _manager = _device.AudioSessionManager;
            _manager.OnSessionCreated += OnSessionCreatedFromCom;   // registers IAudioSessionNotification
            _manager.RefreshSessions();                              // the manager must hold a session enumerator for notifications to be delivered
            var sessions = _manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                try { AddSession(sessions[i], initial: true); }
                catch (Exception ex) { Log.Error("enumerate session", ex); }
            }
            Log.Write($"[device] bound '{_device.FriendlyName}' with {_sessions.Count} session(s)");
        }
        catch (Exception ex) { Log.Error("session manager", ex); }
        LastBindUtc = DateTime.UtcNow;
        Boost?.BindRender(_device.ID);
        DeviceChanged?.Invoke();
    }

    private void ClearSessions()
    {
        if (_manager != null)
        {
            try { _manager.OnSessionCreated -= OnSessionCreatedFromCom; } catch { }
            try { _manager.Dispose(); } catch { GC.SuppressFinalize(_manager); }
            _manager = null;
        }
        var now = DateTime.UtcNow;
        foreach (var key in _apps.Keys) _lastGone[key] = now; // re-created on the next device within the cooldown = not a launch
        foreach (var s in _sessions) s.Dispose();
        _sessions.Clear();
        _apps.Clear();
        Apps.Clear();
        if (_device != null) { try { _device.Dispose(); } catch { } _device = null; }
    }

    // ---- sessions ------------------------------------------------------------------------

    private void OnSessionCreatedFromCom(object sender, IAudioSessionControl newSession)
    {
        _ui.BeginInvoke(() =>
        {
            try { AddSession(new AudioSessionControl(newSession), initial: false); }
            catch (Exception ex) { Log.Error("session created", ex); }
        });
    }

    private void AddSession(AudioSessionControl control, bool initial)
    {
        string instanceId;
        try { instanceId = control.GetSessionInstanceIdentifier; } catch { instanceId = Guid.NewGuid().ToString("N"); }
        if (_sessions.Any(s => s.InstanceId == instanceId)) return;

        uint pid = 0;
        try { pid = control.GetProcessID; } catch { }
        bool isSystem = pid == 0;
        try { isSystem |= control.IsSystemSoundsSession; } catch { }
        if (pid == _ownPid) return;

        var info = isSystem ? new ProcessInfo(0, null, null, null, null, true) : ProcessInfo.Get(pid);
        if (!isSystem && !info.Alive) return;
        var key = isSystem ? "system" : (info.ExeName?.ToLowerInvariant() ?? $"pid:{pid}");

        // A quick relaunch can arrive before housekeeping noticed the old process died.
        if (_apps.TryGetValue(key, out var existing))
            foreach (var old in existing.Sessions.ToList())
                if (!old.IsSystemSounds && !ProcessInfo.IsAlive(old.Pid)) RemoveSession(old, "process exited");

        if (!_apps.TryGetValue(key, out var app))
        {
            string? iconPath = null;
            try { iconPath = control.IconPath; } catch { }
            app = new AppAudio(key, ResolveName(control, info, isSystem), info.ExePath, isSystem);
            app.AdjustedByUser += a => _userAdjusted.Add(a.Key);
            if (Boost != null)
            {
                app.MaxPercent = isSystem || Boost.IsBlocked(key) ? 100 : Boost.MaxPercent;
                app.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppAudio.VolumePercent) or nameof(AppAudio.Muted)) Boost.Evaluate(); };
            }
            _apps[key] = app;
            InsertSorted(app);
            LoadIconAsync(app, iconPath, info.ExePath);
        }

        var entry = new SessionEntry(control, pid, instanceId, info, isSystem, key, this);
        _sessions.Add(entry);
        app.Sessions.Add(entry);
        app.SyncFromSessions();
        Log.Write($"[session] + {app.Name} ({key}) pid={pid} vol={entry.Volume:0.00} mute={entry.Mute} state={entry.State} initial={initial}");
        SessionAdded?.Invoke(app, entry, initial);
        Boost?.Evaluate(); // a boosted app opened another audio process
    }

    /// <summary>Shell icon extraction can take 100 ms cold; never on the UI thread.</summary>
    private void LoadIconAsync(AppAudio app, string? iconPath, string? exePath)
    {
        Task.Run(() => IconExtractor.ForApp(iconPath, exePath)).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion) _ui.BeginInvoke(() => app.Icon = t.Result);
        });
    }

    private void InsertSorted(AppAudio app)
    {
        int i = 0;
        // system sounds always last; others alphabetical
        while (i < Apps.Count && !Apps[i].IsSystemSounds && (app.IsSystemSounds || string.Compare(Apps[i].Name, app.Name, StringComparison.CurrentCultureIgnoreCase) < 0)) i++;
        Apps.Insert(i, app);
    }

    private static string ResolveName(AudioSessionControl control, ProcessInfo info, bool isSystem)
    {
        string? display = null;
        try { display = control.DisplayName; } catch { }
        if (!string.IsNullOrWhiteSpace(display) && display.StartsWith('@')) display = LoadIndirectString(display);
        if (isSystem) return string.IsNullOrWhiteSpace(display) ? "System sounds" : display!;
        if (!string.IsNullOrWhiteSpace(display)) return display!;
        return info.Description ?? ProcessInfo.PrettyName(info.ExeName);
    }

    private void RemoveSession(SessionEntry entry, string reason)
    {
        if (!_sessions.Remove(entry)) return;
        entry.Dispose();
        if (_apps.TryGetValue(entry.Key, out var app))
        {
            app.Sessions.Remove(entry);
            if (app.Sessions.Count == 0)
            {
                _apps.Remove(entry.Key);
                Apps.Remove(app);
                _lastGone[entry.Key] = DateTime.UtcNow;
                Log.Write($"[session] - {app.Name} ({reason})");
            }
            else app.SyncFromSessions();
            Boost?.Evaluate();
        }
    }

    private void Housekeeping()
    {
        foreach (var e in _sessions.ToList())
        {
            if (e.IsSystemSounds) continue;
            if (!ProcessInfo.IsAlive(e.Pid)) RemoveSession(e, "process exited");
        }
    }

    // ---- COM callbacks (arrive on RPC threads) -------------------------------------------

    internal void SessionVolumeChangedFromCom(SessionEntry entry, float volume, bool muted)
    {
        _ui.BeginInvoke(() =>
        {
            bool changed = Math.Abs(volume - entry.Volume) > 0.0005f;
            bool echo = entry.IsOurEcho(volume);
            entry.Volume = volume; entry.Mute = muted;
            if (changed && !echo) entry.UserVolume = volume; // the Windows mixer or the app itself set a new level: that is the new intent
            if (!_apps.TryGetValue(entry.Key, out var app)) return;
            app.SyncFromSessions();
            if (changed && !echo) SessionVolumeChanged?.Invoke(app, entry, volume);
        });
    }

    internal void SessionStateChangedFromCom(SessionEntry entry, AudioSessionState state)
    {
        _ui.BeginInvoke(() =>
        {
            entry.State = state;
            if (_apps.TryGetValue(entry.Key, out var app)) app.SyncFromSessions();
            if (state == AudioSessionState.AudioSessionStateExpired) Housekeeping();
        });
    }

    internal void SessionDisconnectedFromCom(SessionEntry entry) => _ui.BeginInvoke(() => RemoveSession(entry, "disconnected"));

    // ---- queries used by rules and gestures ----------------------------------------------

    public bool WasRecentlyGone(string key, TimeSpan within) => _lastGone.TryGetValue(key, out var t) && DateTime.UtcNow - t < within;
    public bool UserAdjusted(string key) => _userAdjusted.Contains(key);

    public AppAudio? FindByExe(string? exeName)
        => string.IsNullOrEmpty(exeName) ? null : (_apps.TryGetValue(exeName, out var a) ? a : null);

    /// <summary>Map a taskbar button name ("YouTube - Google Chrome - 2 running windows") to an app.</summary>
    public AppAudio? FindByTaskbarButton(string buttonName, List<(string Title, string ExeName)> windows)
    {
        var byTitle = windows
            .Where(w => w.Title.Length >= 3 && buttonName.StartsWith(w.Title, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.Title.Length)
            .Select(w => FindByExe(w.ExeName))
            .FirstOrDefault(a => a != null);
        if (byTitle != null) return byTitle;

        return Apps.Where(a => !a.IsSystemSounds && a.Name.Length >= 3 && buttonName.Contains(a.Name, StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(a => a.Name.Length)
                   .FirstOrDefault()
            ?? Apps.FirstOrDefault(a => a.ExePath != null && buttonName.Contains(Path.GetFileNameWithoutExtension(a.ExePath), StringComparison.OrdinalIgnoreCase));
    }

    // ---- meters --------------------------------------------------------------------------

    private void MeterTick()
    {
        foreach (var app in Apps)
        {
            float p = 0f;
            foreach (var s in app.Sessions) p = Math.Max(p, s.Peak * (s.Mute ? 0f : s.Volume)); // the session meter is pre-gain: show what is heard
            if (Boost != null && Boost.IsBoosted(app.Key)) p = Math.Max(p, Boost.TakePeak(app.Key)); // boosted: the sound comes out of Otto
            app.Peak = Smooth(app.Peak, p);
        }
        Master.Peak = Smooth(Master.Peak, Master.ReadPeak());
    }

    private static double Smooth(double current, double target) => target >= current ? target : current * 0.80;

    // ---- device notifications ------------------------------------------------------------

    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioSessionService _svc;
        public NotificationClient(AudioSessionService svc) { _svc = svc; }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => DevicesChanged();
        public void OnDeviceRemoved(string deviceId) => DevicesChanged();
        private void DevicesChanged() => _svc._ui.BeginInvoke(() => { _svc._devicesTimer.Stop(); _svc._devicesTimer.Start(); });
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render || role != Role.Multimedia) return;
            _svc._ui.BeginInvoke(() => { _svc._rebindTimer.Stop(); _svc._rebindTimer.Start(); });
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    public void Dispose()
    {
        _meterTimer.Stop(); _housekeepingTimer.Stop(); _rebindTimer.Stop(); _devicesTimer.Stop();
        Boost?.Dispose(); // send boosted apps back before anything else goes away
        ClearSessions();
        Master.Unbind();
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); } catch { }
        try { _enumerator.Dispose(); } catch { }
    }
}
