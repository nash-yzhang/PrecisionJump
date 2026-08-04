using System.ComponentModel;
using System.IO;
using System.Text.Json;
using MouseAccelerator.Models;

namespace MouseAccelerator.Services;

public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly AppSettings _settings;
    private readonly object _saveLock = new();
    private System.Threading.Timer? _saveTimer;
    private bool _disposed;

    public SettingsStore(AppSettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += SettingsOnPropertyChanged;
    }

    public static string SettingsPath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(
                "MOUSE_ACCELERATOR_SETTINGS_PATH");
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MouseAccelerator",
                    "settings.json")
                : Path.GetFullPath(overridePath);
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        lock (_saveLock)
        {
            _saveTimer?.Dispose();
            _saveTimer = new System.Threading.Timer(_ => Save(), null, 300, Timeout.Infinite);
        }
    }

    private void Save()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = SettingsPath + ".tmp";
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Settings persistence must never take down the input utility.
        }
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= SettingsOnPropertyChanged;
        lock (_saveLock)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
        }

        Save();
        _disposed = true;
    }
}
