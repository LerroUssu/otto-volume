using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Otto.Audio.FxSound;

/// <summary>
/// Volume above 100 % through FxSound: FxSound's master gain lifts everything that plays on its device by
/// G, and every app's own session is scaled by 1/G, so only the apps set above 100 % come out louder.
/// One app at 200 % → FxSound +6 dB, that app's session at 100 %, every other app at half its level.
/// The master slider above 100 % multiplies every app. Nothing is moved or recorded.
/// Order of operations keeps it from ever jumping loud: sessions go down before the gain goes up,
/// and the gain goes down before sessions come back up.
/// </summary>
public sealed class FxSoundBoost
{
    private const double MaxGainDb = 20; // FxSound's --master_gain range is -20..+20 dB
    private readonly Dispatcher _ui;
    private readonly AudioSessionService _svc;
    private readonly FxSoundLink _fx;
    private readonly string _statePath = Path.Combine(Log.Dir, "fxsound-boost.json");
    private double _appliedDb, _targetDb, _baseDb;
    private bool _baseKnown, _pushing, _released = true;

    public FxSoundBoost(Dispatcher ui, AudioSessionService svc, FxSoundLink fx)
    {
        _ui = ui; _svc = svc; _fx = fx;
    }

    /// <summary>Gain FxSound currently adds on top of the user's own setting (for the meters).</summary>
    public double Gain => Math.Pow(10, Math.Max(_appliedDb, _targetDb) / 20);
    public bool IsActive => _appliedDb > 0 || _targetDb > 0;
    /// <summary>Self-test: the user's own gain is known and nothing is on its way to FxSound.</summary>
    internal bool Settled => _baseKnown && !_pushing && Math.Abs(_targetDb - _appliedDb) < 0.01;
    internal double BaseDb => _baseDb;
    /// <summary>Self-test only: the test tone is muted so it can never be heard; count it anyway.</summary>
    internal bool IgnoreMute { get; set; }

    private sealed record State(double BaseDb, double AppliedDb);

    /// <summary>Learn the user's own FxSound gain; undo what a crashed Otto left in it.</summary>
    public async Task StartAsync()
    {
        var st = await _fx.RefreshAsync();
        if (st == null) return;
        double current = st.Equalizer.MasterGain;
        State? saved = null;
        try { if (File.Exists(_statePath)) saved = JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath)); } catch { }
        _ui.Invoke(() =>
        {
            // still at what Otto set last time: the user's own gain is the saved one
            _baseDb = saved != null && Math.Abs(current - (saved.BaseDb + saved.AppliedDb)) < 0.6 ? saved.BaseDb : current;
            _appliedDb = saved != null && Math.Abs(current - (saved.BaseDb + saved.AppliedDb)) < 0.6 ? saved.AppliedDb : 0;
            _baseKnown = true;
            Log.Write($"[fxsound] gain {current:0.#} dB, own {_baseDb:0.#} dB, Otto's {_appliedDb:0.#} dB");
            Push();
        });
    }

    /// <summary>Bring FxSound's gain and the session scale in line with the sliders. UI thread.</summary>
    public void Evaluate(int maxPercent)
    {
        _released = false;
        double master = _svc.Master.Muted ? 1 : Math.Max(1, _svc.Master.VolumePercent / 100.0);
        double g = master;
        foreach (var app in _svc.Apps)
            if (!app.IsSystemSounds && (!app.Muted || IgnoreMute)) g = Math.Max(g, app.VolumePercent / 100.0 * master);
        g = Math.Min(g, Math.Max(1, maxPercent / 100.0));
        // FxSound rounds its gain to whole dB
        _targetDb = Math.Clamp(Math.Round(20 * Math.Log10(g)), 0, Math.Max(0, MaxGainDb - _baseDb));
        ApplyScale();
        Push();
    }

    /// <summary>Boost off: gain back to the user's own, sessions back to their plain levels.</summary>
    public void Release()
    {
        if (_released) return;
        _released = true;
        _targetDb = 0;
        // FxSound quit: its gain no longer applies; the saved state lets the next start undo it in FxSound
        if (!FxSoundLink.Running) { _appliedDb = 0; _baseKnown = false; }
        ApplyScale();
        Push();
    }

    /// <summary>Quitting: blocking, gain first, then sessions.</summary>
    public void ReleaseNow()
    {
        _targetDb = 0;
        if (_appliedDb > 0 && _baseKnown)
        {
            if (_fx.SendAsync(FxSoundLink.Arg("master_gain", _baseDb)).Wait(6000)) { _appliedDb = 0; SaveState(); }
        }
        _svc.SetSessionScale(1);
    }

    private void ApplyScale()
    {
        double master = _svc.Master.Muted ? 1 : Math.Max(1, _svc.Master.VolumePercent / 100.0);
        // while FxSound has not caught up, scale for the larger of the two gains: quieter, never louder
        _svc.SetSessionScale(master / Math.Pow(10, Math.Max(_appliedDb, _targetDb) / 20));
    }

    private void Push()
    {
        if (_pushing || !_baseKnown || Math.Abs(_targetDb - _appliedDb) < 0.01) return;
        _pushing = true;
        double want = _targetDb;
        bool needPower = want > 0 && _fx.Status is { Power: false };
        var args = needPower ? new[] { "--power=1", FxSoundLink.Arg("master_gain", _baseDb + want) } : new[] { FxSoundLink.Arg("master_gain", _baseDb + want) };
        _fx.SendAsync(args).ContinueWith(t => _ui.BeginInvoke(() =>
        {
            _pushing = false;
            if (t.Status == TaskStatus.RanToCompletion && t.Result)
            {
                _appliedDb = want;
                if (needPower && _fx.Status != null) _fx.Status.Power = true;
                SaveState();
            }
            ApplyScale();
            if (t.Status == TaskStatus.RanToCompletion && t.Result) Push(); // the sliders moved meanwhile
        }));
    }

    private void SaveState()
    {
        try
        {
            if (_appliedDb <= 0) { File.Delete(_statePath); return; }
            File.WriteAllText(_statePath, JsonSerializer.Serialize(new State(_baseDb, _appliedDb)));
        }
        catch (Exception ex) { Log.Error("fxsound state", ex); }
    }
}
