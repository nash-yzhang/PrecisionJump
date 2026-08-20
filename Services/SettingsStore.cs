using System.ComponentModel;
using System.IO;
using System.Text.Json;
using PrecisionJump.Models;

namespace PrecisionJump.Services;

public sealed class SettingsStore : IDisposable
{
    private const string SettingsOverrideVariable =
        "PRECISION_JUMP_SETTINGS_PATH";
    private const string ProductDirectoryName = "PrecisionJump";
    private const string LegacyProductDirectoryName = "MouseAccelerator";

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
                SettingsOverrideVariable);
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ProductDirectoryName,
                    "settings.json")
                : Path.GetFullPath(overridePath);
        }
    }

    public static AppSettings Load()
    {
        try
        {
            var loadPath = ResolveLoadPath();
            if (!File.Exists(loadPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(loadPath);
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

    private static string ResolveLoadPath()
    {
        var settingsPath = SettingsPath;
        if (File.Exists(settingsPath)
            || !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(SettingsOverrideVariable)))
        {
            return settingsPath;
        }

        var legacyPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            LegacyProductDirectoryName,
            "settings.json");
        if (!File.Exists(legacyPath))
        {
            return settingsPath;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.Copy(legacyPath, settingsPath, overwrite: false);
            return settingsPath;
        }
        catch
        {
            // A read-only legacy file can still seed this session. Any later
            // setting change is saved under the new PrecisionJump directory.
            return legacyPath;
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
