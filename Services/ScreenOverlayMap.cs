using Microsoft.Win32;
using System.Windows.Threading;
using MouseAccelerator.Views;

namespace MouseAccelerator.Services;

public sealed class ScreenOverlayMap : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, (DisplayMonitor Display, OverlayWindow Window)> _windows = [];
    private bool _disposed;

    public ScreenOverlayMap(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Refresh();
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    public IReadOnlyList<DisplayMonitor> Displays =>
        _windows.Values
            .Select(item => item.Display)
            .OrderBy(display => display.Number)
            .ToList();

    public void ShowPreview(NineGridPreview preview)
    {
        EnsureCurrentMap();
        foreach (var (deviceName, item) in _windows)
        {
            if (preview.IsScreenLevel)
            {
                var screenPreview = preview with
                {
                    Display = item.Display,
                    CurrentRegion = item.Display.Bounds,
                    SelectedRegion = item.Display.Bounds
                };
                item.Window.ShowPreview(
                    screenPreview,
                    deviceName == preview.Display.DeviceName);
            }
            else if (deviceName == preview.Display.DeviceName)
            {
                item.Window.ShowPreview(preview);
            }
            else
            {
                item.Window.Hide();
            }
        }
    }

    public void HideAll()
    {
        foreach (var item in _windows.Values)
        {
            item.Window.Hide();
        }
    }

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var displays = NineGridSession.GetDisplays();
        var currentNames = displays.Select(display => display.DeviceName).ToHashSet();

        foreach (var removed in _windows.Keys.Where(key => !currentNames.Contains(key)).ToList())
        {
            _windows[removed].Window.Close();
            _windows.Remove(removed);
        }

        foreach (var display in displays)
        {
            if (_windows.TryGetValue(display.DeviceName, out var existing))
            {
                existing.Window.PreMap(display);
                _windows[display.DeviceName] = (display, existing.Window);
            }
            else
            {
                var window = new OverlayWindow();
                window.PreMap(display);
                _windows[display.DeviceName] = (display, window);
            }
        }
    }

    private void EnsureCurrentMap()
    {
        var current = NineGridSession.GetDisplays();
        if (
            current.Count != _windows.Count
            || current.Any(display =>
                !_windows.TryGetValue(display.DeviceName, out var mapped)
                || mapped.Display.Bounds != display.Bounds)
        )
        {
            Refresh();
        }
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(Refresh);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        foreach (var item in _windows.Values)
        {
            item.Window.Close();
        }
        _windows.Clear();
    }
}
