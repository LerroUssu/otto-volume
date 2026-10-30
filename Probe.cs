using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Otto.Audio;
using Otto.Native;
using Otto.Rules;
using static Otto.Native.NativeMethods;

namespace Otto;

/// <summary>
/// `Otto.exe --probe out.json`: headless self-test. Enumerates devices/sessions, resolves icons,
/// fonts, the window under the cursor, parses hotkeys and dry-runs the rules. Opens no window and
/// changes no volume.
/// </summary>
public static class Probe
{
    public static int Run(string outPath, bool download = false)
    {
        var report = new Dictionary<string, object?>();
        int failures = 0;
        void Check(string name, Func<object?> f)
        {
            try { report[name] = f(); }
            catch (Exception ex) { failures++; report[name] = $"ERROR {ex.GetType().Name}: {ex.Message}"; }
        }

        Check("time", () => DateTime.Now.ToString("O"));
        Check("log", () => new { dataDir = Log.Dir, portable = Log.IsPortable, path = Log.ActivePath, lastError = Log.LastError });
        Check("settingsPath", () => Settings.FilePath);
        var settings = Settings.Load();
        Check("settings", () => settings);

        AudioSessionService? svc = null;
        Check("audio", () =>
        {
            svc = new AudioSessionService(Dispatcher.CurrentDispatcher);
            svc.Start();
            return new
            {
                device = svc.Master.DeviceName,
                deviceId = svc.DeviceId,
                masterVolume = svc.Master.VolumePercent,
                masterMuted = svc.Master.Muted,
                apps = svc.Apps.Select(a => new
                {
                    a.Key, a.Name, a.ExePath, a.IsSystemSounds, a.VolumePercent, a.Muted, a.IsActive,
                    hasIcon = a.IsSystemSounds || IconExtractor.ForApp(null, a.ExePath) != null,
                    sessions = a.Sessions.Select(s => new { s.Pid, s.State, s.Volume, s.Mute, processStart = s.Process.StartTime, s.Process.Description }).ToList(),
                    startLevel = settings.EffectiveStart(a.Key),
                }).ToList(),
            };
        });

        Check("fonts", () =>
        {
            var families = Fonts.GetFontFamilies(new Uri("pack://application:,,,/assets/fonts/"));
            var uiFont = (FontFamily)System.Windows.Application.Current.FindResource("UiFont");
            var resolved = new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold }.Select(w =>
            {
                var tf = new Typeface(uiFont, FontStyles.Normal, w, FontStretches.Normal);
                return tf.TryGetGlyphTypeface(out var gt)
                    ? $"{w} -> {gt.FamilyNames.Values.FirstOrDefault()} {gt.Weight} ({System.IO.Path.GetFileName(gt.FontUri.LocalPath)})"
                    : $"{w} -> (no glyph typeface)";
            }).ToList();
            return new
            {
                uiFontString = uiFont.Source,
                resolved,
                embedded = families.Select(f => new { source = f.Source, typefaces = f.FamilyTypefaces.Select(t => $"{t.Weight} {t.Style}").ToList() }).ToList(),
            };
        });

        Check("cursorTarget", () =>
        {
            GetCursorPos(out var pt);
            var t = WindowResolver.Resolve(pt, (uint)Environment.ProcessId);
            string? taskbarButton = t.Kind == TargetKind.Taskbar ? WindowResolver.TaskbarButtonNameAt(pt) : null;
            var matched = taskbarButton != null && svc != null ? svc.FindByTaskbarButton(taskbarButton, WindowResolver.VisibleWindows())?.Name : null;
            return new { point = $"{pt.x},{pt.y}", t.Kind, t.ExeName, t.Title, t.ClassName, taskbarButton, matched, scale = GetScaleForPoint(pt) };
        });

        Check("foreground", () =>
        {
            var fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out var pid);
            var info = ProcessInfo.Get(pid);
            return new { title = GetWindowTitle(fg), cls = GetClassNameOf(fg), info.ExeName, info.Description, app = svc?.FindByExe(info.ExeName)?.Name };
        });

        Check("hotkeys", () => new[] { "Ctrl+Alt+V", "Shift+F9", "Win+Space", "Ctrl+1", "bogus+x", "" }
            .Select(h => new { h, ok = HotkeyManager.TryParse(h, out var m, out var vk), m, vk }).ToList());

        Check("mouseHook", () => { using var hook = new MouseWheelHook(); return hook.IsInstalled; });

        Check("icons", () => new
        {
            explorer = IconExtractor.ForApp(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) != null,
            systemSounds = IconExtractor.ForApp(@"@%SystemRoot%\System32\AudioSrv.Dll,-202", null) != null,
            systemSoundsName = LoadIndirectString(@"@%SystemRoot%\System32\AudioSrv.Dll,-202"),
        });

        Check("boost", () =>
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var def = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia).ID;
            var spares = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
                .Where(d => d.ID != def).Select(d => d.FriendlyName).ToList();
            return new
            {
                perAppOutputs = Otto.Audio.Boost.AudioPolicyConfig.IsAvailable,
                ownRoute = Otto.Audio.Boost.AudioPolicyConfig.Get((uint)Environment.ProcessId),
                spares,
                settings.BoostEnabled, settings.BoostMax, settings.BoostDevice,
                leftovers = File.Exists(Path.Combine(Log.Dir, "boost-routes.json")),
            };
        });

        Check("fxsound", () =>
        {
            var fx = new Otto.Audio.FxSound.FxSoundLink();
            var st = fx.Installed && Otto.Audio.FxSound.FxSoundLink.Running ? Task.Run(fx.RefreshAsync).GetAwaiter().GetResult() : null;
            string? signer = null;
            if (fx.Installed) signer = Otto.Audio.FxSound.FxSoundInstaller.SignedByFxSound(fx.ExePath!, out var why) ? "FxSound, LLC" : why;
            return new
            {
                fx.ExePath, fx.Installed, running = Otto.Audio.FxSound.FxSoundLink.Running, signer,
                power = st?.Power, preset = st?.SelectedPreset, gain = st?.Equalizer.MasterGain, output = st?.SelectedOutput,
                presets = st?.Presets.BuiltIn.Count, effects = st?.Effects,
            };
        });

        Check("autostart", () => AutoStart.IsEnabled());
        Check("trayInspect", () => Otto.Tray.TrayIcon.CanInspect);

        Check("taskbar", () =>
        {
            var tracker = new TaskbarTracker(Dispatcher.CurrentDispatcher, () => svc?.Master.VolumePercent ?? -1);
            // the async speaker lookup needs a moment; poll briefly
            for (int i = 0; i < 40 && !tracker.HasSpeaker; i++) Thread.Sleep(50);
            var r = tracker.SpeakerRect;
            var result = new
            {
                taskbars = tracker.Taskbars.Select(t => $"{t.left},{t.top}-{t.right},{t.bottom}").ToList(),
                speakerFound = tracker.HasSpeaker,
                speakerRect = tracker.HasSpeaker ? $"{r.left},{r.top}-{r.right},{r.bottom}" : null,
                thumbnailVisible = TaskbarTracker.ThumbnailRect() != null,
            };
            tracker.Dispose();
            return result;
        });

        Check("updateTags", () =>
        {
            var cases = new[] { "v0.3.0", "0.3.1", "v1.2", "v2.0.0-beta", "nope", "" };
            return cases.Select(c => Updater.TryParseTag(c, out var v) ? $"{c} -> {v.ToString(3)}" : $"{c} -> invalid").ToList();
        });
        Check("updateSwap", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "fader-swap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string exe = Path.Combine(dir, "Otto.exe"), staged = Path.Combine(dir, "Otto.update.exe");
                File.WriteAllText(exe, "old"); File.WriteAllText(staged, "new");
                Updater.Swap(exe, staged);
                bool ok = File.ReadAllText(exe) == "new" && File.ReadAllText(exe + ".old") == "old" && !File.Exists(staged);
                if (!ok) throw new Exception("swap produced the wrong files");
                return "ok";
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });
        Check("update", () =>
        {
            // newerThan 0.0.0: always describe the latest release, whatever this build's version is.
            var latest = Task.Run(() => Updater.CheckAsync(default, new Version(0, 0, 0))).GetAwaiter().GetResult();
            object? downloaded = null;
            if (download && latest?.DownloadUrl != null)
            {
                string dest = Path.Combine(Path.GetTempPath(), "fader-dl-" + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    Task.Run(() => Updater.DownloadAsync(latest, null, default, dest)).GetAwaiter().GetResult();
                    downloaded = new { bytes = new FileInfo(dest).Length, verifiedSha256 = latest.Sha256 != null };
                }
                finally { try { File.Delete(dest); } catch { } }
            }
            return new
            {
                current = Updater.Current.ToString(3), selfContained = Updater.IsSelfContained, assetSuffix = Updater.AssetSuffix,
                latest = latest?.Tag, asset = latest?.AssetName, size = latest?.Size, sha256 = latest?.Sha256,
                newer = latest != null && latest.Version > Updater.Current, downloaded,
            };
        });

        try { svc?.Dispose(); } catch (Exception ex) { report["disposeError"] = ex.Message; }
        report["failures"] = failures;

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        File.WriteAllText(outPath, json);
        Log.Write($"[probe] wrote {outPath} ({failures} failures)");
        return failures == 0 ? 0 : 1;
    }
}
