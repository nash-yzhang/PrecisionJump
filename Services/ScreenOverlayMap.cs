using Microsoft.Win32;
using System.Windows.Threading;
using PrecisionJump.Views;

namespace PrecisionJump.Services;

public sealed class ScreenOverlayMap : IDisposable
{
    private static readonly TimeSpan RefreshDebounce =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RefreshVerificationInterval =
        TimeSpan.FromMilliseconds(100);
    private const int MaximumRefreshSamples = 10;

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, (DisplayMonitor Display, OverlayWindow Window)> _windows = [];
    private readonly DispatcherTimer _refreshTimer;
    private IReadOnlyList<DisplayMonitor>? _candidateDisplays;
    private int _refreshSampleCount;
    private bool _disposed;

    public event Action? DisplaysInvalidated;
    public event Action<IReadOnlyList<DisplayMonitor>>? DisplaysChanged;

    public ScreenOverlayMap(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _refreshTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            dispatcher);
        _refreshTimer.Tick += RefreshTimerOnTick;
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
        foreach (var (deviceName, item) in _windows)
        {
            var isSelectedDisplay =
                deviceName == preview.Display.DeviceName;
            if (preview.IsScreenLevel)
            {
                var screenPreview = preview with
                {
                    Display = item.Display
                };
                item.Window.ShowPreview(
                    screenPreview,
                    isSelectedDisplay);
            }
            else
            {
                var displayPreview = preview with
                {
                    Display = item.Display,
                    StepX = item.Display.Bounds.Width
                        / Math.Max(preview.MapScale, 1),
                    StepY = item.Display.Bounds.Height
                        / Math.Max(preview.MapScale, 1)
                };
                item.Window.ShowPreview(
                    displayPreview,
                    isSelectedDisplay);
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

        ApplyDisplays(NineGridSession.GetDisplays());
    }

    private void ApplyDisplays(IReadOnlyList<DisplayMonitor> displays)
    {
        if (Displays.SequenceEqual(displays))
        {
            return;
        }

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
                if (existing.Display != display)
                {
                    existing.Window.PreMap(display);
                }
                _windows[display.DeviceName] = (display, existing.Window);
            }
            else
            {
                var window = new OverlayWindow();
                window.PreMap(display);
                _windows[display.DeviceName] = (display, window);
            }
        }

        DisplaysChanged?.Invoke(Displays);
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(
            ScheduleRefresh,
            DispatcherPriority.Send);
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        if (!_refreshTimer.IsEnabled)
        {
            DisplaysInvalidated?.Invoke();
        }

        _candidateDisplays = null;
        _refreshSampleCount = 0;
        _refreshTimer.Stop();
        _refreshTimer.Interval = RefreshDebounce;
        _refreshTimer.Start();
    }

    private void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        var displays = NineGridSession.GetDisplays();
        _refreshSampleCount++;

        if (_candidateDisplays is not null
            && _candidateDisplays.SequenceEqual(displays))
        {
            FinishRefresh(displays);
            return;
        }

        _candidateDisplays = displays;
        if (_refreshSampleCount >= MaximumRefreshSamples)
        {
            FinishRefresh(displays);
            return;
        }

        _refreshTimer.Interval = RefreshVerificationInterval;
    }

    private void FinishRefresh(IReadOnlyList<DisplayMonitor> displays)
    {
        _refreshTimer.Stop();
        _candidateDisplays = null;
        _refreshSampleCount = 0;
        ApplyDisplays(displays);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimerOnTick;
        DisplaysInvalidated = null;
        DisplaysChanged = null;
        foreach (var item in _windows.Values)
        {
            item.Window.Close();
        }
        _windows.Clear();
    }
}
