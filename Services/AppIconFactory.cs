using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MouseAccelerator.Services;

public static class AppIconFactory
{
    public static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var background = new LinearGradientBrush(
            new Rectangle(0, 0, 64, 64),
            Color.FromArgb(17, 36, 39),
            Color.FromArgb(23, 157, 141),
            45f);
        graphics.FillRoundedRectangle(background, new RectangleF(2, 2, 60, 60), 15);

        var cursor = new[]
        {
            new PointF(20, 12),
            new PointF(46, 35),
            new PointF(34, 38),
            new PointF(40, 51),
            new PointF(32, 55),
            new PointF(26, 41),
            new PointF(18, 50)
        };
        using var cursorBrush = new SolidBrush(Color.White);
        graphics.FillPolygon(cursorBrush, cursor);

        using var movementPen = new Pen(Color.FromArgb(235, 255, 255, 255), 3)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(movementPen, 7, 18, 14, 18);
        graphics.DrawLine(movementPen, 6, 27, 14, 27);
        graphics.DrawLine(movementPen, 8, 36, 15, 36);

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public static System.Windows.Media.ImageSource CreateImageSource()
    {
        using var icon = CreateIcon();
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(64, 64));
        source.Freeze();
        return source;
    }

    private static void FillRoundedRectangle(
        this Graphics graphics,
        Brush brush,
        RectangleF bounds,
        float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(
            bounds.Right - diameter,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
