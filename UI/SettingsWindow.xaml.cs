using System.Windows;
using System.Windows.Input;
using Otto.Audio;
using Otto.Rules;

namespace Otto.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(Settings settings, AudioSessionService service, UpdateService updates, Action changed)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(settings, service, updates, changed);
        DataContext = _vm;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 20);
        MaxWidth = Math.Max(480, SystemParameters.WorkArea.Width - 20);
        VersionText.Text = "v" + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1");
        System.Collections.Specialized.NotifyCollectionChangedEventHandler appsChanged = (_, _) => UpdateNoApps();
        service.Apps.CollectionChanged += appsChanged;
        Closed += (_, _) => { service.Apps.CollectionChanged -= appsChanged; _vm.Dispose(); };
        UpdateNoApps();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !HotkeyBox.IsKeyboardFocused) Close(); };
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => _ = _vm.Updates.CheckAsync(manual: true);
    private void InstallUpdate_Click(object sender, RoutedEventArgs e) => _ = _vm.Updates.InstallAsync();
    private void ReleasePage_Click(object sender, RoutedEventArgs e) => _vm.Updates.OpenReleasePage();

    private void UpdateNoApps() => NoAppsText.Visibility = _vm.KnownApps.All(a => a.IsSystemSounds) ? Visibility.Visible : Visibility.Collapsed;


    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void FxOpen_Click(object sender, RoutedEventArgs e) => _vm.Fx?.Open();
    private void FxInstall_Click(object sender, RoutedEventArgs e) => _ = _vm.Fx?.InstallAsync();

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete) { _vm.Hotkey = string.Empty; return; }
        if (key is Key.Escape) { Keyboard.ClearFocus(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (parts.Count == 0 && !isFunctionKey) return; // plain letters would steal typing everywhere

        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        parts.Add(name);
        _vm.Hotkey = string.Join("+", parts);
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppRule rule) _vm.RemoveRule(rule);
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppAudio app) _vm.AddRule(app);
    }
}
