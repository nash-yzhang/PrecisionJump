using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MouseAccelerator.Services;

namespace MouseAccelerator.Views;

public partial class OverlayWindow : Window
{
    private nint _handle;
    private DisplayMonitor? _mappedDisplay;
    private NineGridPreview? _preview;
    private bool _isSelectedScreen = true;
    private int _renderedParentGridSize = -1;
    private double _renderedCanvasWidth;
    private double _renderedCanvasHeight;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ConfigureNativeWindow();
    }

    public void PreMap(DisplayMonitor display)
    {
        _mappedDisplay = display;
        EnsureNativeWindow();
        PositionWindow(display);
    }

    public void ShowPreview(
        NineGridPreview preview,
        bool isSelectedScreen = true)
    {
        _mappedDisplay = preview.Display;
        _preview = preview;
        _isSelectedScreen = isSelectedScreen;

        if (!IsVisible)
        {
            Show();
        }

        EnsureNativeWindow();
        PositionWindow(preview.Display, show: true);
        RenderPreview();
    }

    private void PositionWindow(DisplayMonitor display, bool show = false)
    {
        var bounds = display.Bounds;
        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate
                | (show ? NativeMethods.SwpShowWindow : 0));
    }

    private void OverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderPreview();
    }

    private void RenderPreview()
    {
        if (
            _mappedDisplay is null
            || _preview is null
            || OverlayCanvas.ActualWidth <= 0
            || OverlayCanvas.ActualHeight <= 0
        )
        {
            return;
        }

        var display = _mappedDisplay;
        var scaleX = OverlayCanvas.ActualWidth / display.Bounds.Width;
        var scaleY = OverlayCanvas.ActualHeight / display.Bounds.Height;
        var current = ToCanvasRect(
            _preview.CurrentRegion,
            display,
            scaleX,
            scaleY);
        var selected = ToCanvasRect(
            _preview.SelectedRegion,
            display,
            scaleX,
            scaleY);

        var isScreenLevel = _preview.IsScreenLevel;
        CurrentRegionBorder.Visibility =
            isScreenLevel ? Visibility.Collapsed : Visibility.Visible;
        CurrentRegionBorder.Opacity = 0.48;
        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Opacity =
            isScreenLevel && !_isSelectedScreen ? 0.28 : 1;

        PositionBorder(CurrentRegionBorder, current, inset: 2);
        PositionBorder(SelectionBorder, selected, inset: 3);
        SelectionBorder.CornerRadius = new CornerRadius(
            Math.Min(10, Math.Min(selected.Width, selected.Height) / 10));

        RenderParentGrid(_preview);

        var oneThirdX = current.Left + current.Width / 3;
        var twoThirdsX = current.Left + current.Width * 2 / 3;
        var oneThirdY = current.Top + current.Height / 3;
        var twoThirdsY = current.Top + current.Height * 2 / 3;
        SetVerticalLine(VerticalLine1, oneThirdX, current.Top, current.Bottom);
        SetVerticalLine(VerticalLine2, twoThirdsX, current.Top, current.Bottom);
        SetHorizontalLine(HorizontalLine1, oneThirdY, current.Left, current.Right);
        SetHorizontalLine(HorizontalLine2, twoThirdsY, current.Left, current.Right);
        var childGridVisibility =
            isScreenLevel ? Visibility.Collapsed : Visibility.Visible;
        VerticalLine1.Visibility = childGridVisibility;
        VerticalLine2.Visibility = childGridVisibility;
        HorizontalLine1.Visibility = childGridVisibility;
        HorizontalLine2.Visibility = childGridVisibility;

        TargetLabel.Text = _preview.Label;
        TargetLabel.Visibility =
            isScreenLevel
            || selected.Width >= 135 && selected.Height >= 60
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void RenderParentGrid(NineGridPreview preview)
    {
        var parentGridSize = preview.Depth <= 1
            ? 0
            : preview.GridSize / 3;
        if (parentGridSize <= 1)
        {
            ParentGridCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        ParentGridCanvas.Visibility = Visibility.Visible;
        var width = OverlayCanvas.ActualWidth;
        var height = OverlayCanvas.ActualHeight;
        if (
            parentGridSize == _renderedParentGridSize
            && Math.Abs(width - _renderedCanvasWidth) < 0.5
            && Math.Abs(height - _renderedCanvasHeight) < 0.5
        )
        {
            return;
        }

        _renderedParentGridSize = parentGridSize;
        _renderedCanvasWidth = width;
        _renderedCanvasHeight = height;
        ParentGridCanvas.Children.Clear();
        ParentGridCanvas.Width = width;
        ParentGridCanvas.Height = height;

        for (var index = 1; index < parentGridSize; index++)
        {
            var x = width * index / parentGridSize;
            ParentGridCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = height,
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 1
            });

            var y = height * index / parentGridSize;
            ParentGridCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 1
            });
        }
    }

    private static Rect ToCanvasRect(
        System.Drawing.Rectangle region,
        DisplayMonitor display,
        double scaleX,
        double scaleY)
    {
        return new Rect(
            (region.Left - display.Bounds.Left) * scaleX,
            (region.Top - display.Bounds.Top) * scaleY,
            Math.Max(region.Width * scaleX, 3),
            Math.Max(region.Height * scaleY, 3));
    }

    private static void PositionBorder(Border border, Rect rectangle, double inset)
    {
        Canvas.SetLeft(border, rectangle.Left + inset);
        Canvas.SetTop(border, rectangle.Top + inset);
        border.Width = Math.Max(rectangle.Width - inset * 2, 1);
        border.Height = Math.Max(rectangle.Height - inset * 2, 1);
    }

    private static void SetVerticalLine(
        System.Windows.Shapes.Line line,
        double x,
        double top,
        double bottom)
    {
        line.X1 = x;
        line.X2 = x;
        line.Y1 = top;
        line.Y2 = bottom;
    }

    private static void SetHorizontalLine(
        System.Windows.Shapes.Line line,
        double y,
        double left,
        double right)
    {
        line.X1 = left;
        line.X2 = right;
        line.Y1 = y;
        line.Y2 = y;
    }

    private void EnsureNativeWindow()
    {
        if (_handle != 0)
        {
            return;
        }

        _handle = new WindowInteropHelper(this).EnsureHandle();
        ConfigureNativeWindow();
    }

    private void ConfigureNativeWindow()
    {
        if (_handle == 0)
        {
            _handle = new WindowInteropHelper(this).Handle;
        }
        if (_handle == 0)
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtr(
            _handle,
            NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExTransparent
            | NativeMethods.WsExToolWindow
            | NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlExStyle,
            new nint(style));
    }
}
