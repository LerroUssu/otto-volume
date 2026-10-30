using System.Windows.Threading;
using Otto.Audio.FxSound;

namespace Otto.UI;

/// <summary>The FxSound card in settings: power, presets, effects and output of the installed FxSound, or a way to get it.</summary>
public sealed class FxSoundViewModel : ObservableObject, IDisposable
{
    private readonly FxSoundLink _fx;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _poll;
    private readonly Action _boostChanged;
    private DateTime _editedAt = DateTime.MinValue;
    private string _installText = "";
    private bool _installing;

    public sealed class Choice : ObservableObject
    {
        private bool _isChecked;
        private readonly Action<string> _pick;
        public string Name { get; }
        public Choice(string name, bool isChecked, Action<string> pick) { Name = name; _isChecked = isChecked; _pick = pick; }
        public bool IsChecked { get => _isChecked; set { if (Set(ref _isChecked, value) && value) _pick(Name); } }
        internal void SetQuiet(bool v) => Set(ref _isChecked, v, nameof(IsChecked));
    }

    public sealed class Effect : ObservableObject
    {
        private readonly FxSoundViewModel _vm;
        private double _value;
        private DispatcherTimer? _debounce;
        public string Label { get; }
        public string Option { get; }
        public Effect(FxSoundViewModel vm, string label, string option) { _vm = vm; Label = label; Option = option; }
        public double Value
        {
            get => _value;
            set
            {
                if (!Set(ref _value, Math.Round(Math.Clamp(value, 0, 10), 1))) return;
                _vm._editedAt = DateTime.UtcNow;
                _debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
                {
                    _debounce!.Stop();
                    _ = _vm._fx.SendAsync(FormattableString.Invariant($"--set_effect={Option}:{_value:0.#}"));
                }, _vm._ui);
                _debounce.Stop(); _debounce.Start();
            }
        }
        internal void SetQuiet(double v) => Set(ref _value, Math.Round(v, 1), nameof(Value));
    }

    public FxSoundViewModel(FxSoundLink fx, Dispatcher ui, Action boostChanged)
    {
        _fx = fx; _ui = ui; _boostChanged = boostChanged;
        Effects = new List<Effect>
        {
            new(this, "Clarity", "clarity"), new(this, "Ambience", "ambience"), new(this, "Surround", "surround"),
            new(this, "Dynamic boost", "dynamicboost"), new(this, "Bass", "bass"),
        };
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) => _ = RefreshAsync(), ui);
        _poll.Start();
        _ = RefreshAsync();
    }

    public void Dispose() => _poll.Stop();

    public bool Installed => _fx.Installed;
    public bool NotInstalled => !_fx.Installed;
    public bool Running => _fx.Installed && FxSoundLink.Running;
    public bool NotRunning => _fx.Installed && !FxSoundLink.Running;
    public bool HasStatus => Running && _fx.Status != null;
    public string Version => _fx.Status?.Version is { Length: > 0 } v ? "v" + v : "";
    public string InstallText { get => _installText; private set => Set(ref _installText, value); }
    public bool CanInstall => !_installing;

    public bool Power
    {
        get => _fx.Status?.Power ?? false;
        set
        {
            if (_fx.Status == null || _fx.Status.Power == value) return;
            _fx.Status.Power = value;
            _editedAt = DateTime.UtcNow;
            Raise(); Raise(nameof(PowerOff));
            // FxSound moves Windows to its own driver when on and back when off; boost follows
            _ = _fx.SetPowerAsync(value).ContinueWith(_ => _ui.BeginInvoke(_boostChanged));
        }
    }
    public bool PowerOff => !Power;

    public List<Choice> Presets { get; private set; } = new();
    public List<Choice> Outputs { get; private set; } = new();
    public List<Effect> Effects { get; }

    public async Task RefreshAsync()
    {
        bool wasInstalled = _fx.Installed;
        if (!wasInstalled) _fx.Locate();
        if (DateTime.UtcNow - _editedAt < TimeSpan.FromSeconds(2)) return; // don't fight a slider being dragged
        await _fx.RefreshAsync();
        _ = _ui.BeginInvoke(new Action(Apply));
    }

    private void Apply()
    {
        var st = _fx.Status;
        if (st != null)
        {
            var names = st.Presets.BuiltIn.Concat(st.Presets.UserDefined).Select(p => p.Name).ToList();
            if (!names.SequenceEqual(Presets.Select(p => p.Name)))
            {
                Presets = names.Select(n => new Choice(n, n == st.SelectedPreset, PickPreset)).ToList();
                Raise(nameof(Presets));
            }
            else foreach (var p in Presets) p.SetQuiet(p.Name == st.SelectedPreset);

            if (!st.OutputDevices.SequenceEqual(Outputs.Select(o => o.Name)))
            {
                Outputs = st.OutputDevices.Select(n => new Choice(n, n == st.SelectedOutput, PickOutput)).ToList();
                Raise(nameof(Outputs));
            }
            else foreach (var o in Outputs) o.SetQuiet(o.Name == st.SelectedOutput);

            Effects[0].SetQuiet(st.Effects.Clarity); Effects[1].SetQuiet(st.Effects.Ambience); Effects[2].SetQuiet(st.Effects.Surround);
            Effects[3].SetQuiet(st.Effects.DynamicBoost); Effects[4].SetQuiet(st.Effects.Bass);
        }
        Raise(nameof(Installed)); Raise(nameof(NotInstalled)); Raise(nameof(Running)); Raise(nameof(NotRunning));
        Raise(nameof(HasStatus)); Raise(nameof(Version)); Raise(nameof(Power)); Raise(nameof(PowerOff));
    }

    private void PickPreset(string name)
    {
        foreach (var p in Presets) if (p.Name != name) p.SetQuiet(false);
        _editedAt = DateTime.UtcNow;
        _ = _fx.SendAsync(FxSoundLink.Arg("preset", name)).ContinueWith(_ => { Thread.Sleep(300); _editedAt = DateTime.MinValue; return RefreshAsync(); });
    }

    private void PickOutput(string name)
    {
        foreach (var o in Outputs) if (o.Name != name) o.SetQuiet(false);
        _editedAt = DateTime.UtcNow;
        _ = _fx.SendAsync(FxSoundLink.Arg("output", name));
    }

    public void Open() { _fx.Open(); _ui.BeginInvoke(async () => { await Task.Delay(2500); _ = RefreshAsync(); _boostChanged(); }); }

    public async Task InstallAsync()
    {
        if (_installing) return;
        _installing = true; Raise(nameof(CanInstall));
        try
        {
            bool done = await FxSoundInstaller.InstallAsync(new Progress<string>(s => InstallText = s));
            _fx.Locate();
            if (done && _fx.Installed) { InstallText = ""; await Task.Delay(3000); }
            else if (done) InstallText = "FxSound wasn't installed.";
            await RefreshAsync();
            _boostChanged();
        }
        finally { _installing = false; Raise(nameof(CanInstall)); }
    }
}
