using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Otto.Audio.FxSound;

/// <summary>
/// Drives an installed FxSound (fxsound.com, a separate app with its own audio driver) through its documented
/// command line: `FxSound.exe --power=1`, `--preset="Music"`, `--master_gain=6`, … are forwarded to the running
/// instance, and `--status` makes it write its state to %APPDATA%\FxSound\status.json. Otto ships none of
/// FxSound's code. Commands run one at a time on a background thread.
/// </summary>
public sealed class FxSoundLink
{
    public const string DriverName = "FxSound Audio Enhancer";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? ExePath { get; private set; }
    public bool Installed => ExePath != null && File.Exists(ExePath);
    public static bool Running => Process.GetProcessesByName("FxSound").Length > 0;
    public FxStatus? Status { get; private set; }

    /// <summary>New status read (any thread).</summary>
    public event Action? StatusChanged;

    public FxSoundLink() { Locate(); }

    public void Locate()
    {
        string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FxSound LLC", "FxSound", "FxSound.exe");
        if (File.Exists(pf)) { ExePath = pf; return; }
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(name);
                    if (app?.GetValue("DisplayName") is not string dn || !dn.Contains("FxSound", StringComparison.OrdinalIgnoreCase)) continue;
                    if (app.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, "FxSound.exe"))) { ExePath = Path.Combine(dir, "FxSound.exe"); return; }
                }
            }
            catch (Exception ex) { Log.Error("fxsound locate", ex); }
        }
        ExePath = null;
    }

    /// <summary>Quote a value the way FxSound's tokenizer wants it: plain double quotes, no escaping.</summary>
    public static string Arg(string option, string value) => $"--{option}=\"{value.Replace("\"", "")}\"";
    public static string Arg(string option, double value) => FormattableString.Invariant($"--{option}={value:0.##}");

    /// <summary>Send options to the running FxSound. Never starts it (a cold start would become the instance and not return).</summary>
    public async Task<bool> SendAsync(params string[] args)
    {
        if (!Installed || !Running || args.Length == 0) return false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => RunExe(string.Join(" ", args), 5000)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Start FxSound (or bring its window up when it already runs).</summary>
    public void Open(bool minimized = false)
    {
        if (!Installed) return;
        try { Process.Start(new ProcessStartInfo(ExePath!, minimized ? "--run_minimized" : "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath!)! }); }
        catch (Exception ex) { Log.Error("fxsound open", ex); }
    }

    private bool RunExe(string args, int timeoutMs)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            // Arguments go to the command line verbatim, which is what FxSound's own parser expects.
            using var p = Process.Start(new ProcessStartInfo(ExePath!, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(ExePath!)! })!;
            bool done = p.WaitForExit(timeoutMs);
            Log.Write($"[fxsound] {args} ({(done ? $"{sw.ElapsedMilliseconds} ms" : "no exit")})");
            return done;
        }
        catch (Exception ex) { Log.Error($"fxsound {args}", ex); return false; }
    }

    /// <summary>Self-test only: where FxSound's status file really is when this process sees another AppData.</summary>
    internal static string? StatusPathOverride { get; set; }
    public static string StatusPath => StatusPathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FxSound", "status.json");

    /// <summary>Ask FxSound for its state and read it back (null when it is not running or did not answer).</summary>
    public async Task<FxStatus?> RefreshAsync()
    {
        if (!Installed || !Running) { Status = null; StatusChanged?.Invoke(); return null; }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var before = File.Exists(StatusPath) ? File.GetLastWriteTimeUtc(StatusPath) : DateTime.MinValue;
            await Task.Run(() => RunExe("--status", 5000)).ConfigureAwait(false);
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    if (File.Exists(StatusPath) && File.GetLastWriteTimeUtc(StatusPath) > before)
                    {
                        var s = JsonSerializer.Deserialize<FxStatus>(await File.ReadAllTextAsync(StatusPath).ConfigureAwait(false));
                        if (s != null) { Status = s; StatusChanged?.Invoke(); return s; }
                    }
                }
                catch (IOException) { } // still being written
                catch (JsonException) { }
                await Task.Delay(100).ConfigureAwait(false);
            }
            Log.Write("[fxsound] status.json not updated");
            return Status;
        }
        finally { _gate.Release(); }
    }
}

public sealed class FxStatus
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("power")] public bool Power { get; set; }
    [JsonPropertyName("presets")] public FxPresets Presets { get; set; } = new();
    [JsonPropertyName("selected_preset")] public string SelectedPreset { get; set; } = "";
    [JsonPropertyName("output_devices")] public List<string> OutputDevices { get; set; } = new();
    [JsonPropertyName("selected_output")] public string SelectedOutput { get; set; } = "";
    [JsonPropertyName("equalizer")] public FxEqualizer Equalizer { get; set; } = new();
    [JsonPropertyName("effects")] public FxEffects Effects { get; set; } = new();
}

public sealed class FxPresets
{
    [JsonPropertyName("built_in")] public List<FxPreset> BuiltIn { get; set; } = new();
    [JsonPropertyName("user_defined")] public List<FxPreset> UserDefined { get; set; } = new();
}

public sealed class FxPreset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("modified")] public bool Modified { get; set; }
}

public sealed class FxEqualizer
{
    [JsonPropertyName("num_bands")] public int NumBands { get; set; }
    [JsonPropertyName("master_gain")] public double MasterGain { get; set; }
    [JsonPropertyName("volume_leveling")] public double VolumeLeveling { get; set; }
    [JsonPropertyName("bands")] public List<FxBand> Bands { get; set; } = new();
}

public sealed class FxBand
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("frequency")] public double Frequency { get; set; }
    [JsonPropertyName("gain")] public double Gain { get; set; }
}

public sealed class FxEffects
{
    [JsonPropertyName("clarity")] public double Clarity { get; set; }
    [JsonPropertyName("ambience")] public double Ambience { get; set; }
    [JsonPropertyName("surround")] public double Surround { get; set; }
    [JsonPropertyName("dynamicboost")] public double DynamicBoost { get; set; }
    [JsonPropertyName("bass")] public double Bass { get; set; }
}
