using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace MouseAccelerator.Models;

public sealed class AppSettings : INotifyPropertyChanged
{
    private bool _enabled = true;
    private List<KeyToken> _screenJumpSequence = [new(KeyNames.Backtick)];
    private double _sequenceTimeoutSeconds = 0.42;
    private double _selectionDistance = 26;
    private int _selectionCooldownMilliseconds = 120;
    private int _maximumZoomLevel = 8;
    private bool _showStatusHud = true;
    private bool _recordMouseEvents;
    private bool _startWithWindows;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public List<KeyToken> ScreenJumpSequence
    {
        get => _screenJumpSequence;
        set
        {
            var next = (value ?? []).Take(3)
                .Select(token => new KeyToken(KeyNames.Normalize(token.VirtualKey)))
                .ToList();
            if (next.Count == 0)
            {
                next = [new(KeyNames.Backtick)];
            }

            if (_screenJumpSequence.SequenceEqual(next))
            {
                return;
            }

            _screenJumpSequence = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScreenJumpDisplay));
        }
    }

    public double SequenceTimeoutSeconds
    {
        get => _sequenceTimeoutSeconds;
        set => SetField(ref _sequenceTimeoutSeconds, Math.Clamp(value, 0.12, 1.5));
    }

    public double SelectionDistance
    {
        get => _selectionDistance;
        set => SetField(ref _selectionDistance, Math.Clamp(value, 8, 120));
    }

    public int SelectionCooldownMilliseconds
    {
        get => _selectionCooldownMilliseconds;
        set => SetField(
            ref _selectionCooldownMilliseconds,
            Math.Clamp(value, 40, 400));
    }

    public int MaximumZoomLevel
    {
        get => _maximumZoomLevel;
        set => SetField(ref _maximumZoomLevel, Math.Clamp(value, 1, 10));
    }

    public bool ShowStatusHud
    {
        get => _showStatusHud;
        set => SetField(ref _showStatusHud, value);
    }

    public bool RecordMouseEvents
    {
        get => _recordMouseEvents;
        set => SetField(ref _recordMouseEvents, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetField(ref _startWithWindows, value);
    }

    [JsonIgnore]
    public string ScreenJumpDisplay =>
        string.Join("  →  ", ScreenJumpSequence.Select(token => token.DisplayName));

    public void RestoreDefaults()
    {
        Enabled = true;
        ScreenJumpSequence = [new(KeyNames.Backtick)];
        SequenceTimeoutSeconds = 0.42;
        SelectionDistance = 26;
        SelectionCooldownMilliseconds = 120;
        MaximumZoomLevel = 8;
        ShowStatusHud = true;
        RecordMouseEvents = false;
        StartWithWindows = false;
    }

    public void Normalize()
    {
        ScreenJumpSequence = ScreenJumpSequence;
        _sequenceTimeoutSeconds = Math.Clamp(_sequenceTimeoutSeconds, 0.12, 1.5);
        _selectionDistance = Math.Clamp(_selectionDistance, 8, 120);
        _selectionCooldownMilliseconds = Math.Clamp(
            _selectionCooldownMilliseconds,
            40,
            400);
        _maximumZoomLevel = Math.Clamp(_maximumZoomLevel, 1, 10);
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
