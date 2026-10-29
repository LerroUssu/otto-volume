using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Otto.Audio;

/// <summary>All audio sessions of one application (grouped by executable), as shown in one mixer row.</summary>
public sealed class AppAudio : ObservableObject
{
    private ImageSource? _icon;
    private double _volumePercent;
    private bool _muted;
    private double _peak;
    private bool _isActive;
    private bool _syncing;
    private int _maxPercent = 100;

    public string Key { get; }
    public string Name { get; }
    public string? ExePath { get; }
    public bool IsSystemSounds { get; }
    public List<SessionEntry> Sessions { get; } = new();

    /// <summary>Raised after the user (slider, gesture, hotkey) changed the volume — not for external changes.</summary>
    public event Action<AppAudio>? AdjustedByUser;

    public AppAudio(string key, string name, string? exePath, bool isSystemSounds)
    {
        Key = key; Name = name; ExePath = exePath; IsSystemSounds = isSystemSounds;
    }

    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public double Peak { get => _peak; set => Set(ref _peak, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public string Pids => string.Join(", ", Sessions.Select(s => s.Pid));

    /// <summary>Top of the slider: 100, or the boost limit when the app can be boosted.</summary>
    public int MaxPercent
    {
        get => _maxPercent;
        internal set
        {
            if (!Set(ref _maxPercent, value)) return;
            if (_volumePercent > value) { _volumePercent = value; Raise(nameof(VolumePercent)); Raise(nameof(IsBoosted)); } // sessions are at 100 % already
        }
    }

    public bool IsBoosted => _volumePercent > 100;

    /// <summary>0–<see cref="MaxPercent"/>. Setting it (outside a sync) pushes the level to every session of the app; above 100 % the boost engine adds the rest.</summary>
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var v = Math.Round(Math.Clamp(value, 0, _maxPercent));
            if (!Set(ref _volumePercent, v)) return;
            Raise(nameof(IsBoosted));
            if (_syncing) return;
            foreach (var s in Sessions) s.SetVolume((float)(v / 100.0));
            AdjustedByUser?.Invoke(this);
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            if (_syncing) return;
            foreach (var s in Sessions) s.SetMute(value);
            AdjustedByUser?.Invoke(this);
        }
    }

    /// <summary>Wheel steps stop at 100 % once on the way up; the next step goes into boost.</summary>
    public void Nudge(int percentDelta)
        => VolumePercent = _volumePercent < 100 && _volumePercent + percentDelta > 100 ? 100 : _volumePercent + percentDelta;

    /// <summary>Pull volume / mute / state from the sessions (UI thread, after COM events).</summary>
    public void SyncFromSessions()
    {
        if (Sessions.Count == 0) return;
        _syncing = true;
        try
        {
            double v = Sessions.Max(s => s.UserVolume) * 100.0;
            if (v >= 99.95 && _volumePercent > 100) v = _volumePercent; // boosted: the sessions sit at 100 %
            VolumePercent = v;
            Muted = Sessions.All(s => s.Mute);
            IsActive = Sessions.Any(s => s.State == AudioSessionState.AudioSessionStateActive);
        }
        finally { _syncing = false; }
    }
}
