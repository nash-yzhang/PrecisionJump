using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MouseAccelerator.Models;
using MouseAccelerator.Services;

namespace MouseAccelerator.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly GlobalInputEngine _inputEngine;
    private readonly List<KeyToken> _recordedSequence = [];
    private bool _recording;

    public SettingsWindow(AppSettings settings, GlobalInputEngine inputEngine)
    {
        _settings = settings;
        _inputEngine = inputEngine;
        DataContext = settings;
        InitializeComponent();
        Icon = AppIconFactory.CreateImageSource();
        HeaderIcon.Source = Icon;

        _inputEngine.JumpStateChanged += InputEngineOnJumpStateChanged;
        _settings.PropertyChanged += SettingsOnPropertyChanged;
        UpdateStatus();
    }

    private void RecordScreenJump_Click(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            FinishRecording(apply: true);
            return;
        }

        _inputEngine.PrepareForShortcutCapture();
        _inputEngine.IsCapturingKey = true;
        _recordedSequence.Clear();
        _recording = true;
        RecordScreenJumpButton.Content = "Done";
        ScreenJumpDisplay.Text = "Press 1–3 keys…";
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        e.Handled = true;
        if (e.IsRepeat)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.None or Key.DeadCharProcessed or Key.ImeProcessed)
        {
            return;
        }

        var virtualKey = KeyNames.Normalize(KeyInterop.VirtualKeyFromKey(key));
        if (virtualKey == 0)
        {
            return;
        }

        _recordedSequence.Add(new KeyToken(virtualKey));
        ScreenJumpDisplay.Text = string.Join(
            "  →  ",
            _recordedSequence.Select(token => token.DisplayName));

        if (_recordedSequence.Count >= 3)
        {
            FinishRecording(apply: true);
        }
    }

    private void FinishRecording(bool apply)
    {
        if (!_recording)
        {
            return;
        }

        if (apply && _recordedSequence.Count > 0)
        {
            _settings.ScreenJumpSequence = _recordedSequence.ToList();
        }

        _recording = false;
        _inputEngine.IsCapturingKey = false;
        RecordScreenJumpButton.Content = "Record";
        ScreenJumpDisplay.SetBinding(
            System.Windows.Controls.TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(AppSettings.ScreenJumpDisplay)));
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        FinishRecording(apply: false);
        _settings.RestoreDefaults();
    }

    private void InputEngineOnJumpStateChanged(bool _)
    {
        Dispatcher.Invoke(UpdateStatus);
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Enabled))
        {
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        StatusText.Text = !_settings.Enabled
            ? "Paused"
            : _inputEngine.JumpActive
                ? "Continuous map active · wheel to change scale"
                : "Ready in the background";
        StatusText.Foreground = _inputEngine.JumpActive
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _recording = false;
        _inputEngine.IsCapturingKey = false;
        _inputEngine.JumpStateChanged -= InputEngineOnJumpStateChanged;
        _settings.PropertyChanged -= SettingsOnPropertyChanged;
    }
}
