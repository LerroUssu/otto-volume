using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace Otto.Audio;

/// <summary>Master volume of the default render device.</summary>
public sealed class MasterAudio : ObservableObject
{
    private readonly Dispatcher _ui;
    private AudioEndpointVolume? _endpoint;
    private AudioMeterInformation? _meter;
    private AudioEndpointVolumeNotificationDelegate? _handler;
    private double _volumePercent;
    private bool _muted;
    private double _peak;
    private string _deviceName = "No output device";
    private bool _syncing;
    private int _maxPercent = 100;

    public MasterAudio(Dispatcher ui) { _ui = ui; }

    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }
    public double Peak { get => _peak; set => Set(ref _peak, value); }
    public bool IsBound => _endpoint != null;

    /// <summary>Top of the slider: 100, or the boost limit when a spare output is there (boosts every app).</summary>
    public int MaxPercent
    {
        get => _maxPercent;
        internal set
        {
            if (!Set(ref _maxPercent, value)) return;
            if (_volumePercent > value) { _volumePercent = value; Raise(nameof(VolumePercent)); Raise(nameof(IsBoosted)); }
        }
    }

    public bool IsBoosted => _volumePercent > 100;

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var v = Math.Round(Math.Clamp(value, 0, _maxPercent));
            if (!Set(ref _volumePercent, v)) return;
            Raise(nameof(IsBoosted));
            if (_syncing || _endpoint == null) return;
            try { _endpoint.MasterVolumeLevelScalar = (float)(Math.Min(v, 100) / 100.0); } catch (Exception ex) { Log.Error("master volume", ex); }
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value) || _syncing || _endpoint == null) return;
            try { _endpoint.Mute = value; } catch (Exception ex) { Log.Error("master mute", ex); }
        }
    }

    /// <summary>Wheel steps stop at 100 % once on the way up; the next step goes into boost.</summary>
    public void Nudge(int percentDelta)
        => VolumePercent = _volumePercent < 100 && _volumePercent + percentDelta > 100 ? 100 : _volumePercent + percentDelta;

    internal void Bind(MMDevice device)
    {
        Unbind();
        try
        {
            _endpoint = device.AudioEndpointVolume;
            _meter = device.AudioMeterInformation;
            DeviceName = device.FriendlyName;
            _handler = data => _ui.BeginInvoke(() => Sync(data.MasterVolume, data.Muted));
            _endpoint.OnVolumeNotification += _handler;
            Sync(_endpoint.MasterVolumeLevelScalar, _endpoint.Mute);
        }
        catch (Exception ex) { Log.Error("bind master", ex); _endpoint = null; }
    }

    internal void Unbind()
    {
        if (_endpoint != null && _handler != null) { try { _endpoint.OnVolumeNotification -= _handler; } catch { } }
        _endpoint = null; _meter = null; _handler = null;
        DeviceName = "No output device";
    }

    internal float ReadPeak()
    {
        try { return _meter?.MasterPeakValue ?? 0f; } catch { return 0f; }
    }

    private void Sync(float volume, bool muted)
    {
        _syncing = true;
        try
        {
            double v = volume * 100.0;
            if (v >= 99.95 && _volumePercent > 100) v = _volumePercent; // boosted: the endpoint sits at 100 %
            VolumePercent = v; Muted = muted;
        }
        finally { _syncing = false; }
    }
}
