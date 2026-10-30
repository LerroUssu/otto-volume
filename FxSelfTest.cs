using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Otto.Audio;
using Otto.Audio.FxSound;
using Otto.Rules;
using static Otto.BoostSelfTest;

namespace Otto;

/// <summary>
/// `Otto.exe --fx-selftest out.json [status.json path]`: boost through FxSound, end to end. Refuses to run
/// while anything is playing. A muted -80 dBFS tone app goes to 200 %: FxSound's gain must rise 6 dB and every
/// other app's real level halve (so they sound the same), then everything must come back exactly.
/// </summary>
public static class FxSelfTest
{
    public static int Run(string outPath, string? statusPath)
    {
        var report = new Dictionary<string, object?>();
        bool ok = true;
        void Check(string name, bool pass, object? detail) { report[name] = new { pass, detail = detail?.ToString() }; ok &= pass; }
        if (statusPath != null) FxSoundLink.StatusPathOverride = statusPath;
        Process? child = null;
        AudioSessionService? svc = null;
        string tonePath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "OttoTone.exe");
        try
        {
            var fx = new FxSoundLink();
            Check("installed", fx.Installed && FxSoundLink.Running, fx.ExePath);
            using var en = new MMDeviceEnumerator();
            using var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Check("fxsoundIsOutput", def.FriendlyName.Contains(FxSoundLink.DriverName), def.FriendlyName);

            // silence: nothing may be playing, on FxSound's device or on any real output
            float loudest = 0f;
            var outs = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            for (int i = 0; i < 15; i++) { foreach (var d in outs) loudest = Math.Max(loudest, d.AudioMeterInformation.MasterPeakValue); Thread.Sleep(100); }
            if (loudest > 0.001f) { Check("silence", false, $"something is playing (peak {loudest:0.0000}), not testing"); throw new OperationCanceledException(); }
            Check("silence", true, loudest);

            var sw = Stopwatch.StartNew();
            var st0 = fx.RefreshAsync().GetAwaiter().GetResult();
            Check("status", st0 != null, $"{sw.ElapsedMilliseconds} ms, gain {st0?.Equalizer.MasterGain}, power {st0?.Power}, preset {st0?.SelectedPreset}");
            double gain0 = st0?.Equalizer.MasterGain ?? double.NaN;

            var settings = new Settings { BoostEnabled = true, BoostMax = 200, BoostVia = BoostRoute.FxSound };
            svc = new AudioSessionService(Dispatcher.CurrentDispatcher, () => settings);
            FxSoundLink.StatusPathOverride = statusPath;
            svc.Start();
            svc.Boost!.FxBoost.IgnoreMute = true;
            Check("viaFxSound", svc.Boost!.ViaFxSound && svc.Master.MaxPercent == 200, $"via {svc.Boost.ViaFxSound}, max {svc.Master.MaxPercent}");

            File.Copy(Environment.ProcessPath!, tonePath, overwrite: true);
            child = Process.Start(new ProcessStartInfo(tonePath, "--tone") { UseShellExecute = false, CreateNoWindow = true })!;
            uint pid = (uint)child.Id;
            var app = WaitFor(() => svc.Apps.FirstOrDefault(a => a.Sessions.Any(s => s.Pid == pid)), 10000) ?? throw new Exception("the test tone never showed up");
            Pump(800);
            Check("toneSilent", app.Sessions.All(s => s.Mute), "muted");
            svc.Boost.Evaluate();
            WaitUntil(() => svc.Boost.FxBoost.Settled, 8000);

            var others = svc.Apps.Where(a => a != app).ToList();
            var before = others.SelectMany(a => a.Sessions).ToDictionary(s => s, s => s.Volume);
            var slidersBefore = others.ToDictionary(a => a, a => a.VolumePercent);
            report["others"] = string.Join(", ", others.Select(a => $"{a.Name} {a.VolumePercent}%"));

            sw.Restart();
            app.VolumePercent = 200;
            WaitUntil(() => svc.Boost.FxBoost.Settled, 8000);
            long settle = sw.ElapsedMilliseconds;
            var st1 = fx.RefreshAsync().GetAwaiter().GetResult();
            Check("gainUp6", st1 != null && Math.Abs(st1.Equalizer.MasterGain - (gain0 + 6)) < 0.6, $"{st1?.Equalizer.MasterGain} dB after {settle} ms (own {gain0})");
            Check("toneAt100", app.Sessions.All(s => s.Volume > 0.999f) && app.VolumePercent == 200, $"{app.Sessions.Max(s => s.Volume):0.000} slider {app.VolumePercent}");
            Check("othersHalved", before.All(kv => Math.Abs(kv.Key.Volume - kv.Value * 0.5012f) < 0.012f), string.Join(", ", before.Select(kv => $"{kv.Value:0.00}->{kv.Key.Volume:0.00}")));
            Check("slidersKept", slidersBefore.All(kv => Math.Abs(kv.Key.VolumePercent - kv.Value) < 1.01), string.Join(", ", slidersBefore.Select(kv => $"{kv.Value}->{kv.Key.VolumePercent}")));

            app.VolumePercent = 80;
            WaitUntil(() => svc.Boost.FxBoost.Settled, 8000);
            Pump(300);
            var st2 = fx.RefreshAsync().GetAwaiter().GetResult();
            Check("gainBack", st2 != null && Math.Abs(st2.Equalizer.MasterGain - gain0) < 0.6, st2?.Equalizer.MasterGain);
            Check("othersBack", before.All(kv => Math.Abs(kv.Key.Volume - kv.Value) < 0.011f), string.Join(", ", before.Select(kv => $"{kv.Value:0.00}->{kv.Key.Volume:0.00}")));
            Check("toneAt80", app.Sessions.All(s => Math.Abs(s.Volume - 0.8f) < 0.005f), app.Sessions.Max(s => s.Volume));

            double masterBefore = svc.Master.VolumePercent;
            svc.Master.VolumePercent = 150;
            WaitUntil(() => svc.Boost.FxBoost.Settled, 8000);
            var st3 = fx.RefreshAsync().GetAwaiter().GetResult();
            // 150 % = +3.5 dB, FxSound rounds to +4 dB; sessions × 1.5/1.585
            Check("masterGain", st3 != null && Math.Abs(st3.Equalizer.MasterGain - (gain0 + 4)) < 0.6, st3?.Equalizer.MasterGain);
            Check("masterScale", before.All(kv => Math.Abs(kv.Key.Volume - Math.Min(1f, kv.Value * 0.9464f)) < 0.012f), string.Join(", ", before.Select(kv => $"{kv.Value:0.00}->{kv.Key.Volume:0.00}")));
            svc.Master.VolumePercent = masterBefore;
            WaitUntil(() => svc.Boost.FxBoost.Settled, 8000);
            Pump(300);

            app.VolumePercent = 180;
            WaitUntil(() => svc.Boost.FxBoost.Settled, 8000);
            svc.Dispose(); svc = null;
            var st4 = fx.RefreshAsync().GetAwaiter().GetResult();
            Check("quitRestores", st4 != null && Math.Abs(st4.Equalizer.MasterGain - gain0) < 0.6 && !File.Exists(Path.Combine(Log.Dir, "fxsound-boost.json")), st4?.Equalizer.MasterGain);
            Check("quitOthersBack", before.All(kv => Math.Abs(ReadVolume(kv.Key) - kv.Value) < 0.011f), string.Join(", ", before.Select(kv => $"{kv.Value:0.00}->{ReadVolume(kv.Key):0.00}")));
        }
        catch (OperationCanceledException) { ok = false; }
        catch (Exception ex) { ok = false; report["error"] = ex.ToString(); }
        finally
        {
            try { svc?.Dispose(); } catch { }
            try { child?.Kill(); child?.WaitForExit(3000); } catch { }
            try { File.Delete(tonePath); } catch { }
        }
        report["pass"] = ok;
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return ok ? 0 : 1;
    }

    private static float ReadVolume(SessionEntry s) { try { return s.Control.SimpleAudioVolume.Volume; } catch { return s.Volume; } }

    private static void WaitUntil(Func<bool> f, int ms)
    {
        var sw = Stopwatch.StartNew();
        Pump(150);
        while (!f() && sw.ElapsedMilliseconds < ms) Pump(100);
    }
}
