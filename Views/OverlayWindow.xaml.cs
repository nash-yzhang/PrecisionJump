using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MouseAccelerator.Services;

namespace MouseAccelerator.Views;

public partial class OverlayWindow : Window
{
    private const int VisibleRingCount = 4;

    private readonly List<(int Row, int Column, Border Cell)> _floatingCells = [];
    private readonly System.Windows.Media.Brush _accentBrush =
        new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x52, 0x17, 0xB6, 0xA4));
    private readonly System.Windows.Media.Brush _softAccentBrush =
        new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x15, 0x17, 0xB6, 0xA4));
    private readonly System.Windows.Media.Brush _transparentBrush =
        System.Windows.Media.Brushes.Transparent;
    private nint _handle;
    private DisplayMonitor? _mappedDisplay;
    private NineGridPreview? _preview;
    private bool _isSelectedScreen = true;

    public OverlayWindow()
    {
        InitializeComponent();
        CreateFloatingCells();
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

        if (_preview.IsScreenLevel)
        {
            RenderScreenLevel();
            return;
        }

        RenderFloatingGrid();
    }

    private void RenderScreenLevel()
    {
        FloatingGridCanvas.Visibility = Visibility.Collapsed;
        CursorMarker.Visibility = Visibility.Collapsed;
        LevelLabelBorder.Visibility = Visibility.Collapsed;
        ScreenBorder.Visibility = Visibility.Visible;
        ScreenBorder.Opacity = _isSelectedScreen ? 1 : 0.24;
        Canvas.SetLeft(ScreenBorder, 3);
        Canvas.SetTop(ScreenBorder, 3);
        ScreenBorder.Width = Math.Max(OverlayCanvas.ActualWidth - 6, 1);
        ScreenBorder.Height = Math.Max(OverlayCanvas.ActualHeight - 6, 1);
        ScreenLabel.Text = _preview!.Label;
    }

    private void RenderFloatingGrid()
    {
        var preview = _preview!;
        var display = _mappedDisplay!;
        var scaleX = OverlayCanvas.ActualWidth / display.Bounds.Width;
        var scaleY = OverlayCanvas.ActualHeight / display.Bounds.Height;
        var cursorX =
            (preview.ActualCursor.X - display.Bounds.Left) * scaleX;
        var cursorY =
            (preview.ActualCursor.Y - display.Bounds.Top) * scaleY;
        var mapScale = Math.Max(preview.MapScale, 1);
        var gridSize = Math.Max((int)Math.Ceiling(mapScale), 1);
        var nominalCellWidth = OverlayCanvas.ActualWidth / mapScale;
        var nominalCellHeight = OverlayCanvas.ActualHeight / mapScale;
        var currentColumn = Math.Clamp(
            (int)Math.Floor(cursorX / nominalCellWidth),
            0,
            gridSize - 1);
        var currentRow = Math.Clamp(
            (int)Math.Floor(cursorY / nominalCellHeight),
            0,
            gridSize - 1);

        ScreenBorder.Visibility = Visibility.Collapsed;
        FloatingGridCanvas.Visibility = Visibility.Visible;
        FloatingGridCanvas.Width = OverlayCanvas.ActualWidth;
        FloatingGridCanvas.Height = OverlayCanvas.ActualHeight;

        foreach (var (row, column, cell) in _floatingCells)
        {
            var globalRow = currentRow + row;
            var globalColumn = currentColumn + column;
            if (
                globalRow < 0
                || globalRow >= gridSize
                || globalColumn < 0
                || globalColumn >= gridSize
            )
            {
                cell.Visibility = Visibility.Collapsed;
                continue;
            }

            cell.Visibility = Visibility.Visible;
            var left = nominalCellWidth * globalColumn;
            var right = nominalCellWidth * (globalColumn + 1);
            var top = nominalCellHeight * globalRow;
            var bottom = nominalCellHeight * (globalRow + 1);
            var cellWidth = Math.Max(right - left, 2);
            var cellHeight = Math.Max(bottom - top, 2);
            var isCurrent =
                _isSelectedScreen && row == 0 && column == 0;

            Canvas.SetLeft(cell, left);
            Canvas.SetTop(cell, top);
            cell.Width = cellWidth;
            cell.Height = cellHeight;
            cell.CornerRadius = new CornerRadius(
                Math.Min(8, Math.Min(cellWidth, cellHeight) / 8));
            cell.Background = isCurrent
                ? _accentBrush
                : _isSelectedScreen
                    && Math.Max(Math.Abs(row), Math.Abs(column)) <= 1
                    ? _softAccentBrush
                    : _transparentBrush;
            cell.BorderThickness = new Thickness(isCurrent ? 3 : 1);
            var normalizedDistance = Math.Sqrt(
                Math.Pow(
                    ((left + right) / 2 - cursorX)
                    / Math.Max(nominalCellWidth, 1),
                    2)
                + Math.Pow(
                    ((top + bottom) / 2 - cursorY)
                    / Math.Max(nominalCellHeight, 1),
                    2));
            cell.Opacity = CellOpacity(normalizedDistance, isCurrent);
        }

        CursorMarker.Visibility =
            _isSelectedScreen ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(CursorMarker, cursorX - CursorMarker.Width / 2);
        Canvas.SetTop(CursorMarker, cursorY - CursorMarker.Height / 2);

        LevelLabelBorder.Visibility =
            _isSelectedScreen ? Visibility.Visible : Visibility.Collapsed;
        LevelLabel.Text = preview.Label;
        Canvas.SetLeft(
            LevelLabelBorder,
            Math.Clamp(cursorX + 10, 4, Math.Max(4, OverlayCanvas.ActualWidth - 80)));
        Canvas.SetTop(
            LevelLabelBorder,
            Math.Clamp(cursorY + 10, 4, Math.Max(4, OverlayCanvas.ActualHeight - 28)));
    }

    private void CreateFloatingCells()
    {
        for (var row = -VisibleRingCount; row <= VisibleRingCount; row++)
        {
            for (var column = -VisibleRingCount; column <= VisibleRingCount; column++)
            {
                var ring = Math.Max(Math.Abs(row), Math.Abs(column));
                var isCenter = row == 0 && column == 0;
                var cell = new Border
                {
                    Background = isCenter
                        ? _accentBrush
                        : ring <= 1
                            ? _softAccentBrush
                            : _transparentBrush,
                    BorderBrush = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(
                        isCenter ? 3 : ring <= 1 ? 1.75 : 1),
                    IsHitTestVisible = false
                };
                FloatingGridCanvas.Children.Add(cell);
                _floatingCells.Add((row, column, cell));
            }
        }
    }

    private static double CellOpacity(
        double normalizedDistance,
        bool isCurrent)
    {
        if (isCurrent)
        {
            return 1;
        }

        return Math.Clamp(
            0.92 * Math.Exp(-0.58 * Math.Max(0, normalizedDistance - 0.5)),
            0.06,
            0.82);
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
