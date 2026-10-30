using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Otto.Audio.FxSound;
using Otto.Rules;

namespace Otto.Audio.Boost;

/// <summary>
/// Volume above 100 %. Windows' session and endpoint volumes stop at 100 %, so a boosted app is sent to a
/// spare output nobody listens to (a virtual device such as VB-CABLE or VoiceMeeter Input, muted while
/// boosting), recorded there with process loopback, multiplied and played on the real output by Otto.
/// An app slider above 100 % boosts that app; the master slider above 100 % boosts every app.
/// Below that nothing is moved and Otto's own output stream is closed.
/// When FxSound is installed and is the output, boost goes through FxSound instead (<see cref="FxSoundBoost"/>);
/// the spare-output route is only used when picked in settings.
/// </summary>
public sealed class BoostEngine : IDisposable
{
    private static readonly Regex VirtualOutput = new(@"CABLE.*Input|VoiceMeeter.*Input|VB-Audio|Virtual Cable", RegexOptions.IgnoreCase);
    private const double CushionSeconds = 0.03, MaxBacklogSeconds = 0.12;

    private readonly Dispatcher _ui;
    private readonly AudioSessionService _svc;
    private readonly Func<Settings> _settings;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<string, Boosted> _boosted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly RouteStore _store;
    private readonly DispatcherTimer _tick;
    private MMDevice? _render, _sink;
    private WaveFormat? _format;
    private WasapiOut? _out;
    private BoostMixer? _mixer;
    private bool _disposed, _evaluating, _wasViaFx, _fxStarted;
    private readonly FxSoundBoost _fxBoost;

    /// <summary>The installed FxSound, if any.</summary>
    public FxSoundLink Fx { get; } = new();
    internal FxSoundBoost FxBoost => _fxBoost;

    private sealed class Boosted
    {
        public readonly string Key;
        public readonly BoostSource Source = new();
        public readonly Dictionary<uint, LoopbackCapture> Captures = new();
        public readonly Dictionary<uint, DateTime> RoutedAt = new();
        /// <summary>Audio processes found only on the spare output (Windows moves a whole program, so a new process of it starts there).</summary>
        public readonly HashSet<uint> FoundOnSpare = new();
        public string Exe = "";
        public int Strikes;
        public Boosted(string key) { Key = key; }
    }

    /// <summary>Availability or the slider range changed.</summary>
    public event Action? Changed;

    public BoostEngine(Dispatcher ui, AudioSessionService svc, Func<Settings> settings)
    {
        _ui = ui; _svc = svc; _settings = settings;
        _store = RouteStore.Load();
        _tick = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(1500) };
        _tick.Tick += (_, _) => Tick();
        _fxBoost = new FxSoundBoost(ui, svc, Fx);
        RecoverFromLastRun();
        _tick.Start();
    }

    /// <summary>Boost through FxSound: picked (the default), FxSound running and Windows playing through its driver.</summary>
    public bool ViaFxSound => _settings().BoostEnabled && _settings().BoostVia != BoostRoute.Spare && Fx.Installed && FxSoundLink.Running
                              && (_render?.FriendlyName.Contains(FxSoundLink.DriverName, StringComparison.OrdinalIgnoreCase) ?? false);
    public bool Available => ViaFxSound || (_settings().BoostEnabled && _settings().BoostVia == BoostRoute.Spare && _render != null && _sink != null && _format != null);
    /// <summary>What FxSound adds after Windows' meters.</summary>
    public double OutputGain => ViaFxSound ? _fxBoost.Gain : 1.0;
    public string? SinkName { get; private set; }
    public string? SinkId => _sink?.ID;
    /// <summary>Self-test only: play the boosted sound into this (muted) output instead of the real one.</summary>
    internal string? SelfTestOutputId { get; set; }
    /// <summary>Self-test only: boost nothing but the test tone.</summary>
    internal Func<AppAudio, bool>? SelfTestOnly { get; set; }
    /// <summary>Self-test only: the test tone's session is muted on the speakers so it can never be heard; boost it anyway.</summary>
    internal bool SelfTestIgnoresMute { get; set; }
    public int MaxPercent => Available ? Math.Clamp(_settings().BoostMax, 100, 400) : 100;
    public bool IsActive => _boosted.Count > 0;
    public bool IsBoosted(string key) => _boosted.ContainsKey(key);
    public bool IsBlocked(string key) => _blocked.Contains(key);
    public float TakePeak(string key) => _boosted.TryGetValue(key, out var b) ? b.Source.TakePeak() : 0f;

    /// <summary>Active outputs that could carry boosted apps (everything except the real output).</summary>
    public List<(string Id, string Name)> Candidates()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (d) if (d.ID != _render?.ID) list.Add((d.ID, d.FriendlyName));
        }
        catch (Exception ex) { Log.Error("boost candidates", ex); }
        return list;
    }

    // ---- devices -------------------------------------------------------------------------

    /// <summary>The real output changed (or went away). Everything boosted is put back first.</summary>
    public void BindRender(string? deviceId)
    {
        if (_render?.ID == deviceId && deviceId != null) return;
        ReleaseAll();
        _render?.Dispose(); _render = null; _format = null;
        if (deviceId != null)
        {
            try
            {
                _render = _enumerator.GetDevice(deviceId);
                using var client = _render.AudioClient;
                var mix = client.MixFormat;
                _format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
            }
            catch (Exception ex) { Log.Error("boost render device", ex); _render = null; _format = null; }
        }
        RefreshSink();
    }

    /// <summary>Pick the spare output: the one chosen in settings, else the first virtual cable.</summary>
    public void RefreshSink()
    {
        string? wanted = _settings().BoostDevice;
        var candidates = Candidates();
        string? id = candidates.Any(c => c.Id == wanted) ? wanted
                   : candidates.FirstOrDefault(c => VirtualOutput.IsMatch(c.Name)).Id;
        if (id == _sink?.ID) { Changed?.Invoke(); return; }

        ReleaseAll();
        _sink?.Dispose(); _sink = null; SinkName = null;
        if (id != null)
        {
            try { _sink = _enumerator.GetDevice(id); SinkName = _sink.FriendlyName; }
            catch (Exception ex) { Log.Error("boost sink", ex); _sink = null; }
        }
        Log.Write($"[boost] output '{_render?.FriendlyName}', spare '{SinkName ?? "none"}'");
        Changed?.Invoke();
        Evaluate();
    }

    // ---- what to boost -------------------------------------------------------------------

    /// <summary>Bring routes, captures and gains in line with the sliders. UI thread; cheap when nothing changed.</summary>
    public void Evaluate()
    {
        if (_disposed || _evaluating) return;
        _evaluating = true;
        try
        {
            bool viaFx = ViaFxSound;
            if (viaFx != _wasViaFx) { _wasViaFx = viaFx; Log.Write($"[boost] through FxSound: {viaFx}"); }
            if (viaFx)
            {
                if (!_fxStarted) { _fxStarted = true; _ = _fxBoost.StartAsync(); }
                if (_boosted.Count > 0) ReleaseAll();
                _fxBoost.Evaluate(MaxPercent);
                return;
            }
            _fxBoost.Release();
            bool available = Available;
            double master = available ? Math.Max(1.0, _svc.Master.VolumePercent / 100.0) : 1.0;
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in _svc.Apps)
            {
                if (app.IsSystemSounds || !available || _blocked.Contains(app.Key)) continue;
                if (SelfTestOnly != null && !SelfTestOnly(app)) continue;
                if (app.VolumePercent <= 100 && master <= 1.0001) continue;
                wanted.Add(app.Key);
                if (!_boosted.TryGetValue(app.Key, out var b)) _boosted[app.Key] = b = new Boosted(app.Key) { Exe = ExeOf(app) };
                b.Source.Gain = app.Muted && !SelfTestIgnoresMute ? 0f :(float)(app.VolumePercent / 100.0 * master);
                SyncPids(b, app);
            }
            foreach (var key in _boosted.Keys.Where(k => !wanted.Contains(k)).ToList()) Release(key);
            UpdateRenderer();
        }
        finally { _evaluating = false; }
    }

    private void SyncPids(Boosted b, AppAudio app)
    {
        var pids = app.Sessions.Where(s => !s.IsSystemSounds && ProcessInfo.IsAlive(s.Pid)).Select(s => s.Pid).Distinct().ToHashSet();
        // A capture includes child processes: record only the top-most audio process of the app, once.
        if (pids.Count > 1)
        {
            var parents = ProcessTree.Parents();
            pids.RemoveWhere(p => ProcessTree.HasAncestorIn(p, pids, parents));
        }

        bool changed = false;
        foreach (var pid in b.Captures.Keys.Where(p => !pids.Contains(p) && !(b.FoundOnSpare.Contains(p) && ProcessInfo.IsAlive(p))).ToList())
        {
            // Windows routes the whole program: the others keep it on the spare output, so only forget this process
            if (_store.Routes.Count(r => r.Pid == pid) > 0 && _store.Routes.Count(r => string.Equals(r.Exe, b.Exe, StringComparison.OrdinalIgnoreCase)) > 1)
            { _store.Routes.RemoveAll(r => r.Pid == pid); _store.Save(); }
            b.Captures[pid].Dispose(); b.Captures.Remove(pid); b.RoutedAt.Remove(pid); b.FoundOnSpare.Remove(pid);
            changed = true;
        }
        foreach (var pid in pids.Where(p => !b.Captures.ContainsKey(p)))
        {
            if (!Route(pid, app)) continue;
            AddCapture(b, pid);
            changed = true;
        }
        if (changed)
        {
            b.Source.Captures = b.Captures.Values.ToArray();
            // the app's session on the spare output keeps its own remembered level: set it to 100 % quickly
            FixSinkSessionsSoon();
        }
    }

    private void AddCapture(Boosted b, uint pid)
    {
        var cap = new LoopbackCapture(pid, _format!);
        cap.Ring.Cushion = Samples(CushionSeconds);
        cap.Ring.MaxBacklog = Samples(MaxBacklogSeconds);
        b.Captures[pid] = cap; b.RoutedAt[pid] = DateTime.UtcNow;
    }

    private static string ExeOf(AppAudio app) => app.ExePath != null ? Path.GetFileName(app.ExePath) : app.Key;

    private int Samples(double seconds) => (int)(seconds * _format!.SampleRate) * _format.Channels;

    private bool Route(uint pid, AppAudio app)
    {
        try
        {
            string? previous = AudioPolicyConfig.Get(pid);
            if (previous == _sink!.ID) previous = null;
            var exe = ExeOf(app);
            _store.SinkId = _sink.ID;
            _store.Routes.RemoveAll(r => r.Pid == pid);
            _store.Routes.Add(new RouteStore.Route { Exe = exe, Pid = pid, Previous = previous });
            _store.Save(); // before moving the app: a crash right after must still find it
            AudioPolicyConfig.Set(pid, _sink.ID);
            Log.Write($"[boost] + {app.Name} pid {pid} → '{SinkName}'" + (previous != null ? $" (was {previous})" : ""));
            return true;
        }
        catch (Exception ex) { Log.Error($"boost route pid {pid}", ex); return false; }
    }

    /// <summary>
    /// Put a program back on its own output. Windows keys the route by program, so one living process of it
    /// is enough; with none left the record stays and the program is sent back when it next plays.
    /// </summary>
    private void Unroute(string exe, IEnumerable<uint> pids)
    {
        var mine = _store.Routes.Where(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase)).ToList();
        string? previous = mine.Select(r => r.Previous).FirstOrDefault(p => p != null);
        uint live = pids.Concat(mine.Select(r => r.Pid)).FirstOrDefault(p => ProcessInfo.IsAlive(p) && string.Equals(ProcessInfo.Get(p).ExeName, exe, StringComparison.OrdinalIgnoreCase));
        if (live == 0) { Log.Write($"[boost] {exe} is closed: it goes back to its output when it next plays"); return; }
        try
        {
            AudioPolicyConfig.Set(live, previous);
            _store.Routes.RemoveAll(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase));
            _store.Save();
            Log.Write($"[boost] - {exe}");
        }
        catch (Exception ex) { Log.Error($"boost unroute {exe}", ex); }
    }

    private void Release(string key)
    {
        if (!_boosted.Remove(key, out var b)) return;
        Unroute(b.Exe, b.Captures.Keys);
        foreach (var cap in b.Captures.Values) cap.Dispose();
        b.Captures.Clear();
        if (_mixer != null) _mixer.Sources = _boosted.Values.Select(x => x.Source).ToArray();
    }

    private void ReleaseAll()
    {
        foreach (var key in _boosted.Keys.ToList()) Release(key);
        UpdateRenderer();
    }

    // ---- Otto's own output ---------------------------------------------------------------

    private void UpdateRenderer()
    {
        if (_boosted.Count == 0)
        {
            if (_out != null)
            {
                try { _out.Stop(); } catch { }
                _out.Dispose(); _out = null; _mixer = null;
                Log.Write("[boost] output stream closed");
            }
            RestoreSinkMute();
            return;
        }

        MuteSink();
        if (_out == null && _render != null && _format != null)
        {
            try
            {
                _mixer = new BoostMixer(_format);
                var target = SelfTestOutputId != null ? _enumerator.GetDevice(SelfTestOutputId) : _render;
                _out = new WasapiOut(target, AudioClientShareMode.Shared, true, 30);
                _out.Init(new SampleToWaveProvider(_mixer));
                _out.Play();
                Log.Write($"[boost] output stream open on '{_render.FriendlyName}' ({_format.SampleRate} Hz, {_format.Channels} ch)");
            }
            catch (Exception ex)
            {
                Log.Error("boost output stream", ex);
                _out?.Dispose(); _out = null; _mixer = null;
            }
        }
        if (_mixer != null) _mixer.Sources = _boosted.Values.Select(b => b.Source).ToArray();
    }

    /// <summary>Nothing sent to the spare output may be heard there (Voicemeeter running, a real device picked as spare).</summary>
    private void MuteSink()
    {
        if (_sink == null || _store.SinkMuteOwned) return;
        try
        {
            _store.SinkWasMuted = _sink.AudioEndpointVolume.Mute;
            _store.SinkMuteOwned = true;
            _store.SinkId = _sink.ID;
            _store.Save();
            _sink.AudioEndpointVolume.Mute = true;
        }
        catch (Exception ex) { Log.Error("boost mute spare output", ex); }
    }

    private void RestoreSinkMute()
    {
        if (!_store.SinkMuteOwned) return;
        try
        {
            using var d = _enumerator.GetDevice(_store.SinkId);
            d.AudioEndpointVolume.Mute = _store.SinkWasMuted;
        }
        catch (Exception ex) { Log.Error("boost restore spare output", ex); }
        _store.SinkMuteOwned = false;
        _store.Save();
    }

    // ---- housekeeping --------------------------------------------------------------------

    private void FixSinkSessionsSoon()
    {
        foreach (var ms in new[] { 250, 900 })
        {
            var t = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) => { t.Stop(); FixSinkSessions(); };
            t.Start();
        }
    }

    /// <summary>
    /// On the spare output: boosted apps' sessions at 100 % and unmuted (the slider is applied by Otto);
    /// apps Otto moved in an earlier run but no longer boosts are sent back.
    /// </summary>
    private void FixSinkSessions()
    {
        if (_disposed) return;
        string? sinkId = _sink?.ID ?? _store.SinkId;
        if (sinkId == null) return;
        bool stale = _store.Routes.Any(r => !_boosted.Values.Any(b => string.Equals(b.Exe, r.Exe, StringComparison.OrdinalIgnoreCase)));
        if (_boosted.Count == 0 && !stale) return;

        var boostedPids = _boosted.Values.SelectMany(b => b.Captures.Keys).ToHashSet();
        try
        {
            using var dev = _enumerator.GetDevice(sinkId);
            var mgr = dev.AudioSessionManager;
            mgr.RefreshSessions();
            var sessions = mgr.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                try
                {
                    uint pid = s.GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId) continue;
                    var exe = ProcessInfo.Get(pid).ExeName;
                    var owner = _boosted.Values.FirstOrDefault(b => string.Equals(b.Exe, exe, StringComparison.OrdinalIgnoreCase));
                    bool inTree = IsInBoostedTree(pid, boostedPids);
                    if (owner != null || inTree)
                    {
                        if (s.SimpleAudioVolume.Volume < 0.999f) s.SimpleAudioVolume.Volume = 1f;
                        if (s.SimpleAudioVolume.Mute) s.SimpleAudioVolume.Mute = false;
                        if (!inTree && s.State == AudioSessionState.AudioSessionStateActive)
                        {
                            owner!.FoundOnSpare.Add(pid);
                            AddCapture(owner, pid);
                            owner.Source.Captures = owner.Captures.Values.ToArray();
                            boostedPids.Add(pid);
                            Log.Write($"[boost] + {exe} pid {pid} (new process, already on the spare output)");
                        }
                        continue;
                    }
                    var old = _store.Routes.FirstOrDefault(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase));
                    if (old == null) continue;
                    AudioPolicyConfig.Set(pid, old.Previous);
                    _store.Routes.RemoveAll(r => string.Equals(r.Exe, old.Exe, StringComparison.OrdinalIgnoreCase)); _store.Save();
                    Log.Write($"[boost] sent {exe} (pid {pid}) back from the spare output (left over from an earlier run)");
                }
                catch (Exception ex) { Log.Error("boost spare session", ex); }
                finally { s.Dispose(); }
            }
        }
        catch (Exception ex) { Log.Error("boost spare sessions", ex); }
    }

    private static bool IsInBoostedTree(uint pid, HashSet<uint> boosted)
    {
        if (boosted.Contains(pid)) return true;
        return boosted.Count > 0 && ProcessTree.HasAncestorIn(pid, boosted, ProcessTree.Parents());
    }

    private void Tick()
    {
        if (ViaFxSound != _wasViaFx) { if (!FxSoundLink.Running) _fxStarted = false; Changed?.Invoke(); } // FxSound started, quit or became/stopped being the output
        FixSinkSessions();
        DetectIgnoredRoutes();
    }

    /// <summary>
    /// An app that opened a specific device (many games) ignores the per-app output and keeps playing on
    /// the real one; Otto's copy would then double it. Such an app is put back and capped at 100 %.
    /// </summary>
    private void DetectIgnoredRoutes()
    {
        foreach (var b in _boosted.Values.ToList())
        {
            var app = _svc.Apps.FirstOrDefault(a => a.Key == b.Key);
            if (app == null) continue;
            bool stillHere = app.Sessions.Any(s => b.RoutedAt.TryGetValue(s.Pid, out var at) && DateTime.UtcNow - at > TimeSpan.FromSeconds(2.5)
                                                   && s.State == AudioSessionState.AudioSessionStateActive && s.Peak > 0.0005f);
            b.Strikes = stillHere ? b.Strikes + 1 : 0;
            if (b.Strikes < 2) continue;
            Log.Write($"[boost] {app.Name} keeps playing on '{_render?.FriendlyName}' (it does not follow per-app outputs): boost off for it");
            _blocked.Add(b.Key);
            Release(b.Key);
            UpdateRenderer();
            Changed?.Invoke();
        }
    }

    /// <summary>A previous Otto may have died while boosting: unmute the spare output and send running apps back.</summary>
    private void RecoverFromLastRun()
    {
        if (_store.Routes.Count == 0 && !_store.SinkMuteOwned) return;
        Log.Write($"[boost] cleaning up an earlier run: {_store.Routes.Count} app(s) on the spare output");
        RestoreSinkMute();
        foreach (var r in _store.Routes.ToList())
        {
            if (!ProcessInfo.IsAlive(r.Pid) || !string.Equals(ProcessInfo.Get(r.Pid).ExeName, r.Exe, StringComparison.OrdinalIgnoreCase)) continue;
            try { AudioPolicyConfig.Set(r.Pid, r.Previous); _store.Routes.RemoveAll(x => string.Equals(x.Exe, r.Exe, StringComparison.OrdinalIgnoreCase)); }
            catch (Exception ex) { Log.Error("boost recover", ex); }
        }
        _store.Save(); // routes of apps that are not running stay listed until they show up on the spare output
    }

    public void Dispose()
    {
        if (_disposed) return;
        _tick.Stop();
        ReleaseAll();
        try { _fxBoost.ReleaseNow(); } catch (Exception ex) { Log.Error("fxsound release", ex); }
        _disposed = true;
        _render?.Dispose(); _sink?.Dispose();
        try { _enumerator.Dispose(); } catch { }
    }
}

/// <summary>Parent links of every process (one Toolhelp snapshot).</summary>
internal static class ProcessTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    public static Dictionary<uint, uint> Parents()
    {
        var map = new Dictionary<uint, uint>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e)) map[e.th32ProcessID] = e.th32ParentProcessID;
        }
        finally { CloseHandle(snap); }
        return map;
    }

    public static bool HasAncestorIn(uint pid, HashSet<uint> set, Dictionary<uint, uint> parents)
    {
        for (int i = 0; i < 32 && parents.TryGetValue(pid, out var parent) && parent != 0 && parent != pid; i++)
        {
            if (set.Contains(parent)) return true;
            pid = parent;
        }
        return false;
    }
}
