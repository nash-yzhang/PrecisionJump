using System.Diagnostics;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace MouseAccelerator.Services;

public sealed record DisplayMonitor(
    string DeviceName,
    Rectangle Bounds,
    Rectangle WorkingArea,
    bool IsPrimary,
    int Number);

public sealed record NineGridPreview(
    DisplayMonitor Display,
    Point ActualCursor,
    int Depth,
    double MapScale,
    double StepX,
    double StepY)
{
    public bool IsScreenLevel => Depth == 0;

    public string Label => IsScreenLevel
        ? $"SCREEN {Display.Number}"
        : $"ZOOM {MapScale:F1}×";
}

public sealed record GridInteraction(
    NineGridPreview Preview,
    Point? JumpTarget);

public sealed class NineGridSession
{
    private const double ScaleAnimationSeconds = 0.14;

    private readonly IReadOnlyList<DisplayMonitor> _displays;
    private double _mapX;
    private double _mapY;
    private int _depth;
    private double _scale = 1;
    private double _scaleStart = 1;
    private double _scaleTarget = 1;
    private long _scaleStartedAt;

    public NineGridSession(
        Point origin,
        IReadOnlyList<DisplayMonitor> displays)
    {
        if (displays.Count == 0)
        {
            throw new InvalidOperationException("No displays are available.");
        }

        _displays = displays;
        Origin = origin;
        _mapX = origin.X;
        _mapY = origin.Y;
        Display = FindDisplay(origin);
        Preview = CreatePreview();
    }

    public Point Origin { get; }
    public Point CurrentPosition => RoundedPosition();
    public DisplayMonitor Display { get; private set; }
    public int Depth => _depth;
    public double MapScale => _scale;
    public double StepX => Display.Bounds.Width / Math.Max(_scale, 1);
    public double StepY => Display.Bounds.Height / Math.Max(_scale, 1);
    public NineGridPreview Preview { get; private set; }

    public bool IsScaleAnimating
    {
        get
        {
            var elapsed = (Stopwatch.GetTimestamp() - _scaleStartedAt)
                / (double)Stopwatch.Frequency;
            return Math.Abs(_scale - _scaleTarget) > 0.001
                && elapsed < ScaleAnimationSeconds;
        }
    }

    public GridInteraction MoveContinuous(
        double physicalDeltaX,
        double physicalDeltaY,
        double mapUnitTravelDistance,
        long? nowTicks = null)
    {
        UpdateScale(nowTicks);
        if (physicalDeltaX == 0 && physicalDeltaY == 0)
        {
            Preview = CreatePreview();
            return new GridInteraction(Preview, null);
        }

        var travel = Math.Max(mapUnitTravelDistance, 1);
        if (Depth == 0)
        {
            MoveOnDiscreteScreenMap(
                physicalDeltaX / (travel * _scale),
                physicalDeltaY / (travel * _scale));
        }
        else
        {
            MoveOnActualDisplayMap(
                physicalDeltaX * Display.Bounds.Width / (travel * _scale),
                physicalDeltaY * Display.Bounds.Height / (travel * _scale));
        }

        Preview = CreatePreview();
        return new GridInteraction(Preview, CurrentPosition);
    }

    public GridInteraction ZoomIn(
        int maximumDepth,
        Point? actualPosition = null,
        long? nowTicks = null)
    {
        if (Depth >= maximumDepth)
        {
            return new GridInteraction(Preview, null);
        }

        if (actualPosition is Point position)
        {
            SyncPosition(position);
        }
        StartScaleTransition(Depth + 1, nowTicks);
        Preview = CreatePreview();
        return new GridInteraction(Preview, null);
    }

    public GridInteraction ZoomOut(
        Point? actualPosition = null,
        long? nowTicks = null)
    {
        if (Depth == 0)
        {
            return new GridInteraction(Preview, null);
        }

        if (actualPosition is Point position)
        {
            SyncPosition(position);
        }
        StartScaleTransition(Depth - 1, nowTicks);
        Preview = CreatePreview();
        return new GridInteraction(Preview, null);
    }

    public NineGridPreview RefreshScale(long? nowTicks = null)
    {
        UpdateScale(nowTicks);
        Preview = CreatePreview();
        return Preview;
    }

    public void AcceptProgrammaticPosition(Point actualPosition)
    {
        SyncPosition(actualPosition);
        Preview = CreatePreview();
    }

    private void MoveOnDiscreteScreenMap(
        double normalizedDeltaX,
        double normalizedDeltaY)
    {
        var bounds = Display.Bounds;
        var width = Math.Max(bounds.Width - 1, 1);
        var height = Math.Max(bounds.Height - 1, 1);
        var u = (_mapX - bounds.Left) / width + normalizedDeltaX;
        var v = (_mapY - bounds.Top) / height + normalizedDeltaY;

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var columnDirection = u < 0 ? -1 : u > 1 ? 1 : 0;
            var rowDirection = v < 0 ? -1 : v > 1 ? 1 : 0;
            if (rowDirection == 0 && columnDirection == 0)
            {
                break;
            }

            var nextDisplay = FindDiscreteDisplay(
                rowDirection,
                columnDirection);
            if (nextDisplay is null)
            {
                u = Math.Clamp(u, 0, 1);
                v = Math.Clamp(v, 0, 1);
                break;
            }

            if (columnDirection > 0)
            {
                u -= 1;
            }
            else if (columnDirection < 0)
            {
                u += 1;
            }
            if (rowDirection > 0)
            {
                v -= 1;
            }
            else if (rowDirection < 0)
            {
                v += 1;
            }

            Display = nextDisplay;
        }

        bounds = Display.Bounds;
        _mapX = bounds.Left
            + Math.Clamp(u, 0, 1) * Math.Max(bounds.Width - 1, 1);
        _mapY = bounds.Top
            + Math.Clamp(v, 0, 1) * Math.Max(bounds.Height - 1, 1);
    }

    private void MoveOnActualDisplayMap(
        double mapDeltaX,
        double mapDeltaY)
    {
        var proposedX = _mapX + mapDeltaX;
        var proposedY = _mapY + mapDeltaY;
        if (Contains(Display.Bounds, proposedX, proposedY))
        {
            _mapX = proposedX;
            _mapY = proposedY;
            return;
        }

        var containingDisplay = _displays.FirstOrDefault(
            display => Contains(display.Bounds, proposedX, proposedY));
        if (containingDisplay is not null)
        {
            Display = containingDisplay;
            _mapX = proposedX;
            _mapY = proposedY;
            return;
        }

        var crossing = FindActualLayoutCrossing(
            _mapX,
            _mapY,
            mapDeltaX,
            mapDeltaY);
        if (crossing is not null)
        {
            var (targetDisplay, entryX, entryY) = crossing.Value;
            Display = targetDisplay;
            _mapX = entryX;
            _mapY = entryY;
            return;
        }

        // Keep integrating through real virtual-desktop gaps. The visible
        // pointer remains projected to the current display edge until the
        // continuous map coordinate enters another display rectangle.
        _mapX = proposedX;
        _mapY = proposedY;
    }

    private void StartScaleTransition(int depth, long? nowTicks)
    {
        var now = nowTicks ?? Stopwatch.GetTimestamp();
        UpdateScale(now);
        _depth = depth;
        _scaleStart = _scale;
        _scaleTarget = Math.Pow(3, depth);
        _scaleStartedAt = now;
    }

    private void UpdateScale(long? nowTicks)
    {
        if (Math.Abs(_scale - _scaleTarget) <= 0.001)
        {
            _scale = _scaleTarget;
            return;
        }

        var now = nowTicks ?? Stopwatch.GetTimestamp();
        var elapsed = (now - _scaleStartedAt)
            / (double)Stopwatch.Frequency;
        var progress = Math.Clamp(
            elapsed / ScaleAnimationSeconds,
            0,
            1);
        var eased = progress * progress * (3 - 2 * progress);
        _scale = _scaleStart
            + (_scaleTarget - _scaleStart) * eased;
        if (progress >= 1)
        {
            _scale = _scaleTarget;
        }
    }

    private void SyncPosition(Point position)
    {
        _mapX = position.X;
        _mapY = position.Y;
        Display = FindDisplay(position);
    }

    private NineGridPreview CreatePreview()
    {
        return new NineGridPreview(
            Display,
            CurrentPosition,
            Depth,
            _scale,
            StepX,
            StepY);
    }

    private DisplayMonitor? FindDiscreteDisplay(
        int rowDirection,
        int columnDirection)
    {
        var currentCenter = Center(Display.Bounds);
        return _displays
            .Where(candidate => candidate.DeviceName != Display.DeviceName)
            .Select(candidate =>
            {
                var candidateCenter = Center(candidate.Bounds);
                var dx = candidateCenter.X - currentCenter.X;
                var dy = candidateCenter.Y - currentCenter.Y;
                return new
                {
                    Display = candidate,
                    Direction = Octant(dx, dy),
                    DistanceSquared =
                        (long)dx * dx + (long)dy * dy
                };
            })
            .Where(candidate =>
                candidate.Direction
                    == (Row: rowDirection, Column: columnDirection))
            .OrderBy(candidate => candidate.DistanceSquared)
            .Select(candidate => candidate.Display)
            .FirstOrDefault();
    }

    private (DisplayMonitor Display, double X, double Y)?
        FindActualLayoutCrossing(
            double originX,
            double originY,
            double directionX,
            double directionY)
    {
        var length = Math.Sqrt(
            directionX * directionX + directionY * directionY);
        if (length <= 0)
        {
            return null;
        }

        var crossing = _displays
            .Where(candidate => candidate.DeviceName != Display.DeviceName)
            .Select(candidate => new
            {
                Display = candidate,
                Entry = RayRectangleEntry(
                    originX,
                    originY,
                    directionX,
                    directionY,
                    candidate.Bounds)
            })
            .Where(candidate =>
                candidate.Entry is >= 0 and <= 1)
            .OrderBy(candidate => candidate.Entry)
            .FirstOrDefault();
        if (crossing?.Entry is not double entry)
        {
            return null;
        }

        var unitX = directionX / length;
        var unitY = directionY / length;
        var bounds = crossing.Display.Bounds;
        return (
            crossing.Display,
            Math.Clamp(
                originX + directionX * entry + unitX,
                bounds.Left,
                bounds.Right - 1),
            Math.Clamp(
                originY + directionY * entry + unitY,
                bounds.Top,
                bounds.Bottom - 1));
    }

    private static double? RayRectangleEntry(
        double originX,
        double originY,
        double directionX,
        double directionY,
        Rectangle rectangle)
    {
        var minimum = double.NegativeInfinity;
        var maximum = double.PositiveInfinity;
        if (!UpdateRayInterval(
            originX,
            directionX,
            rectangle.Left,
            rectangle.Right,
            ref minimum,
            ref maximum)
            || !UpdateRayInterval(
                originY,
                directionY,
                rectangle.Top,
                rectangle.Bottom,
                ref minimum,
                ref maximum))
        {
            return null;
        }

        var entry = Math.Max(minimum, 0);
        return maximum >= entry ? entry : null;
    }

    private static bool UpdateRayInterval(
        double origin,
        double direction,
        double minimumBound,
        double maximumBound,
        ref double minimum,
        ref double maximum)
    {
        const double epsilon = 0.000001;
        if (Math.Abs(direction) < epsilon)
        {
            return origin >= minimumBound && origin <= maximumBound;
        }

        var first = (minimumBound - origin) / direction;
        var second = (maximumBound - origin) / direction;
        if (first > second)
        {
            (first, second) = (second, first);
        }

        minimum = Math.Max(minimum, first);
        maximum = Math.Min(maximum, second);
        return maximum >= minimum;
    }

    private static (int Row, int Column) Octant(double dx, double dy)
    {
        var angle = Math.Atan2(dy, dx);
        var octant = (int)Math.Round(
            angle / (Math.PI / 4),
            MidpointRounding.AwayFromZero);
        octant = ((octant % 8) + 8) % 8;
        return octant switch
        {
            0 => (0, 1),
            1 => (1, 1),
            2 => (1, 0),
            3 => (1, -1),
            4 => (0, -1),
            5 => (-1, -1),
            6 => (-1, 0),
            _ => (-1, 1)
        };
    }

    private DisplayMonitor FindDisplay(Point point)
    {
        return _displays.FirstOrDefault(
                display => display.Bounds.Contains(point))
            ?? _displays
                .OrderBy(display =>
                {
                    var x = Math.Clamp(
                        point.X,
                        display.Bounds.Left,
                        display.Bounds.Right - 1);
                    var y = Math.Clamp(
                        point.Y,
                        display.Bounds.Top,
                        display.Bounds.Bottom - 1);
                    var dx = point.X - x;
                    var dy = point.Y - y;
                    return (long)dx * dx + (long)dy * dy;
                })
                .First();
    }

    private Point RoundedPosition()
    {
        var bounds = Display.Bounds;
        return new Point(
            Math.Clamp(
                (int)Math.Round(_mapX),
                bounds.Left,
                bounds.Right - 1),
            Math.Clamp(
                (int)Math.Round(_mapY),
                bounds.Top,
                bounds.Bottom - 1));
    }

    private static bool Contains(
        Rectangle bounds,
        double x,
        double y)
    {
        return x >= bounds.Left
            && x < bounds.Right
            && y >= bounds.Top
            && y < bounds.Bottom;
    }

    private static Point Center(Rectangle region)
    {
        return new Point(
            region.Left + region.Width / 2,
            region.Top + region.Height / 2);
    }

    public static IReadOnlyList<DisplayMonitor> GetDisplays()
    {
        return Forms.Screen.AllScreens
            .OrderBy(screen => screen.Bounds.Left)
            .ThenBy(screen => screen.Bounds.Top)
            .Select((screen, index) => new DisplayMonitor(
                screen.DeviceName,
                screen.Bounds,
                screen.WorkingArea,
                screen.Primary,
                index + 1))
            .ToList();
    }
}
