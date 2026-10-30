using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Otto.Audio;
using Otto.Audio.Boost;
using Otto.Rules;

namespace Otto;

/// <summary>
/// `Otto.exe --boost-selftest out.json [spare-output-name]`: silent end-to-end test of boost. A child Otto
/// plays an inaudible tone (-80 dBFS) on a muted session and follows default-device changes the way
/// Chrome does; the real engine boosts it and plays the result into the muted spare output (never the
/// speakers). Checks the move, the gain, master boost, the way back and the clean-up.
/// </summary>
public static class BoostSelfTest
{
    private const float ToneGain = 0.0001f;
    private static readonly Regex Spare = new(@"CABLE.*Input|VoiceMeeter.*Input|VB-Audio|NVIDIA Broadcast", RegexOptions.IgnoreCase);

    public static int Run(string outPath, string? sinkName)
    {
        var report = new Dictionary<string, object?>();
        bool ok = true;
        void Check(string name, bool pass, object? detail) { report[name] = new { pass, detail = detail?.ToString() }; ok &= pass; }
        Process? child = null;
        AudioSessionService? svc = null;
        string tonePath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "OttoTone.exe");
        try
        {
            using var en = new MMDeviceEnumerator();
            var defaultId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            var sink = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.ID != defaultId && (sinkName == null ? Spare.IsMatch(d.FriendlyName) : d.FriendlyName.Contains(sinkName, StringComparison.OrdinalIgnoreCase)))
                ?? throw new Exception("no spare output to test with");
            report["spare"] = sink.FriendlyName;
            bool sinkMuteBefore = sink.AudioEndpointVolume.Mute;

            var settings = new Settings { BoostEnabled = true, BoostMax = 200, BoostDevice = sink.ID };
            svc = new AudioSessionService(Dispatcher.CurrentDispatcher, () => settings);
            svc.Boost!.SelfTestOutputId = sink.ID;
            svc.Boost.SelfTestIgnoresMute = true;
            uint childPid = 0;
            svc.Boost.SelfTestOnly = a => a.Sessions.Any(s => s.Pid == childPid); // never move the user's real apps
            svc.Start();
            Check("available", svc.Boost.Available && svc.Master.MaxPercent == 200, $"{svc.Boost.SinkName} max {svc.Master.MaxPercent}");

            // Windows routes per program: the tone must not be Otto.exe, or moving it would move this process too
            File.Copy(Environment.ProcessPath!, tonePath, overwrite: true);
            child = Process.Start(new ProcessStartInfo(tonePath, "--tone") { UseShellExecute = false, CreateNoWindow = true })!;
            uint pid = childPid = (uint)child.Id;
            var app = WaitFor(() => svc.Apps.FirstOrDefault(a => a.Sessions.Any(s => s.Pid == pid)), 10000)
                      ?? throw new Exception("the test tone never showed up in the mixer");
            Pump(500);
            Check("toneSilentOnSpeakers", app.Sessions.All(s => s.Mute), string.Join(",", app.Sessions.Select(s => $"{s.Pid}:{s.Mute}")));
            Check("appMax", app.MaxPercent == 200, app.MaxPercent);

            app.VolumePercent = 200;
            Pump(2500);
            string? routed = AudioPolicyConfig.Get(pid);
            Check("routedToSpare", routed == sink.ID, routed);
            var onSpare = SessionOn(en, sink.ID, pid);
            Check("playsOnSpare", onSpare is { state: AudioSessionState.AudioSessionStateActive, vol: > 0.99f, mute: false }, onSpare);
            Check("leftSpeakers", SessionOn(en, defaultId, pid)?.state != AudioSessionState.AudioSessionStateActive, SessionOn(en, defaultId, pid));
            Check("spareMuted", sink.AudioEndpointVolume.Mute, sink.AudioEndpointVolume.Mute);

            float boosted = 0f, ottoOut = 0f;
            for (int i = 0; i < 20; i++)
            {
                Pump(100);
                boosted = Math.Max(boosted, svc.Boost.TakePeak(app.Key));
                ottoOut = Math.Max(ottoOut, SessionOn(en, sink.ID, (uint)Environment.ProcessId)?.peak ?? 0f);
            }
            Check("gain200", boosted > 0.00017f && boosted < 0.00023f, $"{boosted:0.000000} (expected {2 * ToneGain:0.000000})");
            Check("ottoPlays", ottoOut > 0.00015f, $"{ottoOut:0.000000}");
            Check("rowKeeps200", svc.Apps.Contains(app) && app.VolumePercent == 200, app.VolumePercent);

            app.VolumePercent = 80;
            Pump(2000);
            Check("routedBack", AudioPolicyConfig.Get(pid) == null, AudioPolicyConfig.Get(pid));
            Check("backOnSpeakers", SessionOn(en, defaultId, pid)?.state == AudioSessionState.AudioSessionStateActive, SessionOn(en, defaultId, pid));
            Check("spareUnmuted", sink.AudioEndpointVolume.Mute == sinkMuteBefore, sink.AudioEndpointVolume.Mute);
            Check("streamClosed", !svc.Boost.IsActive, svc.Boost.IsActive);

            // master above 100 % boosts every app: 80 % × 150 % = 120 %
            double masterBefore = svc.Master.VolumePercent;
            svc.Master.VolumePercent = 150;
            Pump(2500);
            Check("masterRoutes", AudioPolicyConfig.Get(pid) == sink.ID && svc.Boost.IsBoosted(app.Key), AudioPolicyConfig.Get(pid));
            float masterBoosted = 0f;
            for (int i = 0; i < 15; i++) { Pump(100); masterBoosted = Math.Max(masterBoosted, svc.Boost.TakePeak(app.Key)); }
            Check("masterGain", masterBoosted > 0.00010f && masterBoosted < 0.00014f, $"{masterBoosted:0.000000} (expected {1.2 * ToneGain:0.000000})");
            svc.Master.VolumePercent = masterBefore;
            Pump(1500);
            Check("masterBack", AudioPolicyConfig.Get(pid) == null && !svc.Boost.IsActive, AudioPolicyConfig.Get(pid));

            // quitting while boosted puts everything back
            app.VolumePercent = 180;
            Pump(1500);
            Check("boostedAgain", AudioPolicyConfig.Get(pid) == sink.ID, AudioPolicyConfig.Get(pid));
            svc.Dispose(); svc = null;
            Check("quitRestores", AudioPolicyConfig.Get(pid) == null && sink.AudioEndpointVolume.Mute == sinkMuteBefore
                                  && !File.Exists(Path.Combine(Log.Dir, "boost-routes.json")), AudioPolicyConfig.Get(pid));
        }
        catch (Exception ex) { ok = false; report["error"] = ex.ToString(); }
        finally
        {
            try { svc?.Dispose(); } catch { }
            try { if (child != null) { AudioPolicyConfig.Set((uint)child.Id, null); child.Kill(); child.WaitForExit(3000); } } catch { }
            try { File.Delete(tonePath); } catch { }
        }
        report["pass"] = ok;
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return ok ? 0 : 1;
    }

    private record SessionState(AudioSessionState state, float vol, bool mute, float peak);

    private static SessionState? SessionOn(MMDeviceEnumerator en, string deviceId, uint pid)
    {
        using var d = en.GetDevice(deviceId);
        var m = d.AudioSessionManager;
        m.RefreshSessions();
        var ss = m.Sessions;
        SessionState? found = null;
        for (int i = 0; i < ss.Count; i++)
        {
            using var s = ss[i];
            if (s.GetProcessID != pid) continue;
            var st = new SessionState(s.State, s.SimpleAudioVolume.Volume, s.SimpleAudioVolume.Mute, s.AudioMeterInformation.MasterPeakValue);
            if (found == null || st.state == AudioSessionState.AudioSessionStateActive) found = st;
        }
        return found;
    }

    internal static T? WaitFor<T>(Func<T?> f, int ms) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { var v = f(); if (v != null) return v; Pump(100); }
        return null;
    }

    internal static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        t.Start();
        Dispatcher.PushFrame(frame);
        t.Stop();
    }

    // ---- the child: an app that follows the default output, like Chrome ------------------

    /// <summary>`Otto.exe --tone`: -80 dBFS tone, its session muted before the first sample; reopens on default-device changes.</summary>
    public static int Tone()
    {
        using var en = new MMDeviceEnumerator();
        var session = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioSessionManager.SimpleAudioVolume;
        session.Mute = true;
        if (!session.Mute) return 2;
        var gen = new SignalGenerator(48000, 2) { Frequency = 440, Gain = ToneGain, Type = SignalGeneratorType.Sin };
        var changed = new AutoResetEvent(false);
        en.RegisterEndpointNotificationCallback(new Follow(changed));
        var sw = Stopwatch.StartNew();
        var life = TimeSpan.FromSeconds(45);
        while (sw.Elapsed < life)
        {
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var wo = new WasapiOut(dev, AudioClientShareMode.Shared, true, 50);
            wo.Init(gen);
            wo.Play();
            var left = life - sw.Elapsed;
            if (left > TimeSpan.Zero) changed.WaitOne(left);
            Thread.Sleep(50);
        }
        return 0;
    }

    private sealed class Follow : IMMNotificationClient
    {
        private readonly AutoResetEvent _e;
        public Follow(AutoResetEvent e) { _e = e; }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) { if (flow == DataFlow.Render && role == Role.Multimedia) _e.Set(); }
        public void OnDeviceStateChanged(string d, DeviceState s) { }
        public void OnDeviceAdded(string d) { }
        public void OnDeviceRemoved(string d) { }
        public void OnPropertyValueChanged(string d, PropertyKey k) { }
    }
}
