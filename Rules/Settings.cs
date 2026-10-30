using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Otto.Native.NativeMethods;

namespace Otto.Rules;

public enum ScrollModifier { Alt, CtrlAlt, ShiftAlt, CtrlShift }
public enum OsdPosition { Top, Center, Bottom }

/// <summary>Per-app override. A rule replaces the default entirely: null means "leave this app alone".</summary>
public sealed class AppRule : ObservableObject
{
    private string _app = string.Empty;
    private string? _name;
    private int? _startVolume;

    /// <summary>Executable name, lower-case ("chrome.exe"), or "system".</summary>
    public string App { get => _app; set => Set(ref _app, value); }
    public string? Name { get => _name; set => Set(ref _name, value); }
    public int? StartVolume { get => _startVolume; set => Set(ref _startVolume, value is int v ? Math.Clamp(v, 0, 100) : null); }
}

public sealed class Settings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly string Dir = Log.Dir;
    public static readonly string FilePath = Path.Combine(Dir, "settings.json");

    // New-app rules (defaults; a per-app rule overrides them)
    public int? NewAppStartVolume { get; set; } = 50;
    public int FreshProcessSeconds { get; set; } = 60;
    public int CooldownMinutes { get; set; } = 10;
    public List<AppRule> Rules { get; set; } = new();

    // Gesture
    public ScrollModifier ScrollModifier { get; set; } = ScrollModifier.Alt;
    public int ScrollStep { get; set; } = 2;
    public bool ScrollGestureEnabled { get; set; } = true;
    public bool ScrollOverTaskbar { get; set; } = true;
    public bool ShowOsd { get; set; } = true;
    /// <summary>Where the level pill always appears on the monitor the cursor is on.</summary>
    public OsdPosition OsdPosition { get; set; } = OsdPosition.Bottom;

    // Taskbar, no modifier
    public bool TaskbarScroll { get; set; } = true;
    public bool TaskbarHoverPill { get; set; } = true;
    public bool SpeakerIconOpensFader { get; set; } = true;

    // Flyout
    public string Hotkey { get; set; } = "Ctrl+Alt+V";
    public string Accent { get; set; } = DefaultAccent;
    public const string DefaultAccent = "#46A873"; // Otto's green

    // Boost above 100 % (needs a spare output such as VB-CABLE or VoiceMeeter Input)
    public bool BoostEnabled { get; set; } = true;
    /// <summary>FxSound (installed separately) or a spare output Otto records and plays back.</summary>
    public BoostRoute BoostVia { get; set; } = BoostRoute.FxSound;
    public int BoostMax { get; set; } = 200;
    /// <summary>Output used to carry boosted apps; null = the first virtual cable found.</summary>
    public string? BoostDevice { get; set; }

    public bool FirstRunDone { get; set; } = false;
    public bool AutoCheckUpdates { get; set; } = true;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
                if (string.Equals(s.Accent, "#8B5CF6", StringComparison.OrdinalIgnoreCase)) s.Accent = DefaultAccent; // Fader's old violet default
                return s;
            }
        }
        catch (Exception ex) { Log.Error("settings load", ex); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    public AppRule? RuleFor(string key) => Rules.FirstOrDefault(r => string.Equals(r.App, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Start level for an app: its rule if it has one, else the default.</summary>
    public int? EffectiveStart(string key)
    {
        var r = RuleFor(key);
        return r != null ? r.StartVolume : NewAppStartVolume;
    }

    /// <summary>True when exactly the configured modifier keys are held (Win never counts).</summary>
    public bool ModifierHeld()
    {
        bool ctrl = IsKeyDown(VK_CONTROL), alt = IsKeyDown(VK_MENU), shift = IsKeyDown(VK_SHIFT);
        if (IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)) return false;
        return ScrollModifier switch
        {
            ScrollModifier.Alt => alt && !ctrl && !shift,
            ScrollModifier.CtrlAlt => ctrl && alt && !shift,
            ScrollModifier.ShiftAlt => shift && alt && !ctrl,
            ScrollModifier.CtrlShift => ctrl && shift && !alt,
            _ => false,
        };
    }

    [JsonIgnore] public bool ModifierUsesAlt => ScrollModifier != ScrollModifier.CtrlShift;

    [JsonIgnore]
    public string ModifierLabel => ScrollModifier switch
    {
        ScrollModifier.Alt => "Alt",
        ScrollModifier.CtrlAlt => "Ctrl + Alt",
        ScrollModifier.ShiftAlt => "Shift + Alt",
        ScrollModifier.CtrlShift => "Ctrl + Shift",
        _ => "Alt",
    };
}

public enum BoostRoute { FxSound, Spare }
