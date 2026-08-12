using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MouseAccelerator.Models;
using MouseAccelerator.Services;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MouseAccelerator.Views;

public partial class SettingsWindow : Window
{
    private enum ShortcutTarget
    {
        ScreenJump,
        SavePosition,
        RecallPosition
    }

    private readonly AppSettings _settings;
    private readonly GlobalInputEngine _inputEngine;
    private readonly List<KeyToken> _recordedSequence = [];
    private ShortcutTarget? _recordingTarget;
    private Button? _recordingButton;
    private TextBlock? _recordingDisplay;
    private int _recordingLimit;

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
        ToggleRecording(
            ShortcutTarget.ScreenJump,
            RecordScreenJumpButton,
            ScreenJumpDisplay,
            limit: 3);
    }

    private void RecordSavePosition_Click(object sender, RoutedEventArgs e)
    {
        ToggleRecording(
            ShortcutTarget.SavePosition,
            RecordSavePositionButton,
            SavePositionDisplay,
            limit: 2);
    }

    private void RecordRecallPosition_Click(object sender, RoutedEventArgs e)
    {
        ToggleRecording(
            ShortcutTarget.RecallPosition,
            RecordRecallPositionButton,
            RecallPositionDisplay,
            limit: 2);
    }

    private void ToggleRecording(
        ShortcutTarget target,
        Button button,
        TextBlock display,
        int limit)
    {
        if (_recordingTarget == target)
        {
            FinishRecording(apply: true);
            return;
        }

        FinishRecording(apply: false);
        _inputEngine.PrepareForShortcutCapture();
        _inputEngine.IsCapturingKey = true;
        _recordedSequence.Clear();
        _recordingTarget = target;
        _recordingButton = button;
        _recordingDisplay = display;
        _recordingLimit = limit;
        button.Content = "Done";
        display.Text = limit == 2 ? "Press up to 2 keys…" : "Press 1–3 keys…";
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_recordingTarget is null)
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
        _recordingDisplay!.Text = string.Join(
            "  →  ",
            _recordedSequence.Select(token => token.DisplayName));

        if (_recordedSequence.Count >= _recordingLimit)
        {
            FinishRecording(apply: true);
        }
    }

    private void FinishRecording(bool apply)
    {
        if (_recordingTarget is not ShortcutTarget target)
        {
            return;
        }

        if (apply && _recordedSequence.Count > 0)
        {
            switch (target)
            {
                case ShortcutTarget.ScreenJump:
                    _settings.ScreenJumpSequence = _recordedSequence.ToList();
                    break;
                case ShortcutTarget.SavePosition:
                    _settings.SavePositionSequence = _recordedSequence.ToList();
                    break;
                case ShortcutTarget.RecallPosition:
                    _settings.RecallPositionSequence = _recordedSequence.ToList();
                    break;
            }
        }

        var button = _recordingButton!;
        var display = _recordingDisplay!;
        _recordingTarget = null;
        _recordingButton = null;
        _recordingDisplay = null;
        _recordingLimit = 0;
        _inputEngine.IsCapturingKey = false;
        button.Content = "Record";
        var propertyName = target switch
        {
            ShortcutTarget.ScreenJump => nameof(AppSettings.ScreenJumpDisplay),
            ShortcutTarget.SavePosition => nameof(AppSettings.SavePositionDisplay),
            ShortcutTarget.RecallPosition => nameof(AppSettings.RecallPositionDisplay),
            _ => throw new ArgumentOutOfRangeException()
        };
        display.SetBinding(
            TextBlock.TextProperty,
            new Binding(propertyName));
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        FinishRecording(apply: false);
        _settings.RestoreDefaults();
    }

    private void ClearSavedPositions_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.SavedPositionCount == 0)
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "Clear all saved pointer positions? This cannot be undone.",
            "Precision Jump",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result == MessageBoxResult.Yes)
        {
            _settings.ClearSavedMousePositions();
        }
    }

    private void InputEngineOnJumpStateChanged(bool _)
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateStatus();
        }
        else
        {
            Dispatcher.BeginInvoke(UpdateStatus);
        }
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
        FinishRecording(apply: false);
        _inputEngine.IsCapturingKey = false;
        _inputEngine.JumpStateChanged -= InputEngineOnJumpStateChanged;
        _settings.PropertyChanged -= SettingsOnPropertyChanged;
    }
}
