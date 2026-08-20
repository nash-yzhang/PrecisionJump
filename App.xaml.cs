using System.Threading;
using System.Windows;
using Microsoft.Win32;
using PrecisionJump.Models;
using PrecisionJump.Services;
using PrecisionJump.Views;

namespace PrecisionJump;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private SettingsStore? _settingsStore;
    private GlobalInputEngine? _inputEngine;
    private TrayIconService? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private bool _ownsSingleInstance;

    public static AppSettings Settings { get; private set; } = null!;
    public static GlobalInputEngine InputEngine { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\PrecisionJump.SingleInstance.v3",
            createdNew: out var isFirstInstance);
        _ownsSingleInstance = isFirstInstance;

        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                "Precision Jump is already running in the notification area.",
                "Precision Jump",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Settings = SettingsStore.Load();
        _settingsStore = new SettingsStore(Settings);
        RemoveLegacyStartupRegistration();

        _inputEngine = new GlobalInputEngine(Settings, Dispatcher);
        InputEngine = _inputEngine;
        try
        {
            _inputEngine.Start();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"Precision Jump could not start its global input hooks.\n\n{exception.Message}",
                "Precision Jump",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _trayIcon = new TrayIconService(
            showSettings: ShowSettings,
            quit: () => Shutdown());
        _inputEngine.JumpStateChanged += active => _trayIcon.SetJumpActive(active);

        ShowSettings();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Settings, InputEngine);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
        _settingsWindow.Topmost = true;
        _settingsWindow.Topmost = false;
        _settingsWindow.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _inputEngine?.Dispose();
        _trayIcon?.Dispose();
        _settingsStore?.Dispose();
        if (_ownsSingleInstance)
        {
            _singleInstance?.ReleaseMutex();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void RemoveLegacyStartupRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                writable: true);
            key?.DeleteValue("PrecisionJump", throwOnMissingValue: false);
        }
        catch
        {
            // A stale entry is harmless if policy prevents its removal.
        }
    }
}
