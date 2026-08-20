using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PrecisionJump.Models;

public sealed class AppSettings : INotifyPropertyChanged
{
    private bool _enabled = true;
    private List<KeyToken> _screenJumpSequence = [new(KeyNames.Backtick)];
    private List<KeyToken> _savePositionSequence =
        [new(KeyNames.Control), new(0x4D)];
    private List<KeyToken> _recallPositionSequence =
        [new(KeyNames.Control), new(0x47)];
    private Dictionary<string, SavedMousePosition> _savedMousePositions =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _positionMarksEnabled = true;
    private double _sequenceTimeoutSeconds = 0.42;
    private double _selectionDistance = 26;
    private int _selectionCooldownMilliseconds = 120;
    private int _maximumZoomLevel = 8;
    private bool _showStatusHud = true;
    private bool _recordMouseEvents;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public bool PositionMarksEnabled
    {
        get => _positionMarksEnabled;
        set => SetField(ref _positionMarksEnabled, value);
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

    public List<KeyToken> SavePositionSequence
    {
        get => _savePositionSequence;
        set => SetPositionSequence(
            ref _savePositionSequence,
            value,
            [new(KeyNames.Control), new(0x4D)],
            nameof(SavePositionSequence),
            nameof(SavePositionDisplay));
    }

    public List<KeyToken> RecallPositionSequence
    {
        get => _recallPositionSequence;
        set => SetPositionSequence(
            ref _recallPositionSequence,
            value,
            [new(KeyNames.Control), new(0x47)],
            nameof(RecallPositionSequence),
            nameof(RecallPositionDisplay));
    }

    public Dictionary<string, SavedMousePosition> SavedMousePositions
    {
        get => _savedMousePositions;
        set
        {
            var next = new Dictionary<string, SavedMousePosition>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (key, position) in value ?? [])
            {
                if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
                {
                    next[char.ToUpperInvariant(key[0]).ToString()] = position;
                }
            }

            _savedMousePositions = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SavedPositionCount));
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

    [JsonIgnore]
    public string ScreenJumpDisplay =>
        string.Join("  →  ", ScreenJumpSequence.Select(token => token.DisplayName));

    [JsonIgnore]
    public string SavePositionDisplay =>
        string.Join("  →  ", SavePositionSequence.Select(token => token.DisplayName));

    [JsonIgnore]
    public string RecallPositionDisplay =>
        string.Join("  →  ", RecallPositionSequence.Select(token => token.DisplayName));

    [JsonIgnore]
    public int SavedPositionCount => _savedMousePositions.Count;

    public void SaveMousePosition(char register, int x, int y)
    {
        var key = char.ToUpperInvariant(register).ToString();
        var next = new Dictionary<string, SavedMousePosition>(
            _savedMousePositions,
            StringComparer.OrdinalIgnoreCase)
        {
            [key] = new SavedMousePosition(x, y)
        };
        SavedMousePositions = next;
    }

    public void ClearSavedMousePositions()
    {
        if (_savedMousePositions.Count > 0)
        {
            SavedMousePositions = [];
        }
    }

    public bool TryGetSavedMousePosition(
        char register,
        out SavedMousePosition position)
    {
        return _savedMousePositions.TryGetValue(
            char.ToUpperInvariant(register).ToString(),
            out position);
    }

    public void RestoreDefaults()
    {
        Enabled = true;
        PositionMarksEnabled = true;
        ScreenJumpSequence = [new(KeyNames.Backtick)];
        SavePositionSequence = [new(KeyNames.Control), new(0x4D)];
        RecallPositionSequence = [new(KeyNames.Control), new(0x47)];
        SequenceTimeoutSeconds = 0.42;
        SelectionDistance = 26;
        SelectionCooldownMilliseconds = 120;
        MaximumZoomLevel = 8;
        ShowStatusHud = true;
        RecordMouseEvents = false;
    }

    public void Normalize()
    {
        ScreenJumpSequence = ScreenJumpSequence;
        SavePositionSequence = SavePositionSequence;
        RecallPositionSequence = RecallPositionSequence;
        SavedMousePositions = SavedMousePositions;
        _sequenceTimeoutSeconds = Math.Clamp(_sequenceTimeoutSeconds, 0.12, 1.5);
        _selectionDistance = Math.Clamp(_selectionDistance, 8, 120);
        _selectionCooldownMilliseconds = Math.Clamp(
            _selectionCooldownMilliseconds,
            40,
            400);
        _maximumZoomLevel = Math.Clamp(_maximumZoomLevel, 1, 10);
    }

    private void SetPositionSequence(
        ref List<KeyToken> field,
        List<KeyToken>? value,
        List<KeyToken> fallback,
        string propertyName,
        string displayPropertyName)
    {
        var next = (value ?? []).Take(2)
            .Select(token => new KeyToken(KeyNames.Normalize(token.VirtualKey)))
            .ToList();
        if (next.Count == 0)
        {
            next = fallback;
        }

        if (field.SequenceEqual(next))
        {
            return;
        }

        field = next;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(displayPropertyName);
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

public readonly record struct SavedMousePosition(int X, int Y);
