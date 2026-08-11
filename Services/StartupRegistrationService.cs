using System.ComponentModel;
using Microsoft.Win32;
using MouseAccelerator.Models;

namespace MouseAccelerator.Services;

public sealed class StartupRegistrationService : IDisposable
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PrecisionJump";

    private readonly AppSettings _settings;
    private bool _disposed;

    public StartupRegistrationService(AppSettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += SettingsOnPropertyChanged;
        Apply(_settings.StartWithWindows);
    }

    private void SettingsOnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.StartWithWindows))
        {
            Apply(_settings.StartWithWindows);
        }
    }

    private static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(executable))
                {
                    key.SetValue(
                        ValueName,
                        $"\"{executable}\" --startup",
                        RegistryValueKind.String);
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Startup registration is optional and must not prevent launch.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.PropertyChanged -= SettingsOnPropertyChanged;
    }
}
