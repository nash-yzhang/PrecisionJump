using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PrecisionJump.Services;
using DrawingPoint = System.Drawing.Point;
using MediaColor = System.Windows.Media.Color;

namespace PrecisionJump.Views;

public enum HudTone
{
    Neutral,
    Active,
    Inactive
}

public partial class ModeHudWindow : Window
{
    private readonly DispatcherTimer _hideTimer;
    private nint _handle;

    public ModeHudWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1_200)
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
            style |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, new nint(style));
        };
    }

    public void ShowMessage(
        string message,
        HudTone tone = HudTone.Neutral,
        int? hideAfterMilliseconds = 1_200)
    {
        _hideTimer.Stop();
        StatusLabel.Text = message;
        ApplyTone(tone);

        if (!IsVisible)
        {
            Show();
        }

        if (_handle == 0)
        {
            _handle = new WindowInteropHelper(this).Handle;
        }

        var cursor = NativeMethods.GetCursorPos(out var point)
            ? new DrawingPoint(point.X, point.Y)
            : DrawingPoint.Empty;
        var displays = NineGridSession.GetDisplays();
        var display = displays.FirstOrDefault(item => item.Bounds.Contains(cursor))
            ?? displays.First();
        const int width = 224;
        const int height = 44;
        var x = display.WorkingArea.Left + (display.WorkingArea.Width - width) / 2;
        var y = display.WorkingArea.Top + 18;

        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            x,
            y,
            width,
            height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);

        if (hideAfterMilliseconds is not null)
        {
            _hideTimer.Interval = TimeSpan.FromMilliseconds(hideAfterMilliseconds.Value);
            _hideTimer.Start();
        }
    }

    private void ApplyTone(HudTone tone)
    {
        HudBorder.BorderBrush = tone switch
        {
            HudTone.Active => new SolidColorBrush(MediaColor.FromRgb(63, 210, 195)),
            HudTone.Inactive => new SolidColorBrush(MediaColor.FromRgb(147, 158, 164)),
            _ => new SolidColorBrush(MediaColor.FromRgb(111, 179, 214))
        };
        HudBorder.Background = tone switch
        {
            HudTone.Active => new SolidColorBrush(MediaColor.FromArgb(235, 19, 55, 52)),
            HudTone.Inactive => new SolidColorBrush(MediaColor.FromArgb(235, 47, 52, 55)),
            _ => new SolidColorBrush(MediaColor.FromArgb(235, 23, 32, 36))
        };
    }
}
