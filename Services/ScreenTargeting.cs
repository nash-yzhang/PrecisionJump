using System.Diagnostics;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace PrecisionJump.Services;

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
        MoveOnAdjacentDisplayMap(
            physicalDeltaX / (travel * _scale),
            physicalDeltaY / (travel * _scale));

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

    private void MoveOnAdjacentDisplayMap(
        double normalizedDeltaX,
        double normalizedDeltaY)
    {
        var bounds = Display.Bounds;
        var width = Math.Max(bounds.Width - 1, 1);
        var height = Math.Max(bounds.Height - 1, 1);
        var u = Math.Clamp((_mapX - bounds.Left) / width, 0, 1);
        var v = Math.Clamp((_mapY - bounds.Top) / height, 0, 1);
        var remainingX = normalizedDeltaX;
        var remainingY = normalizedDeltaY;

        var maximumTransitions = Math.Max(_displays.Count * 2, 4);
        for (var iteration = 0; iteration < maximumTransitions; iteration++)
        {
            if (Math.Abs(remainingX) < 0.000001
                && Math.Abs(remainingY) < 0.000001)
            {
                break;
            }

            var horizontalTime = BoundaryTime(u, remainingX);
            var verticalTime = BoundaryTime(v, remainingY);
            var travelTime = Math.Min(1, Math.Min(horizontalTime, verticalTime));
            u += remainingX * travelTime;
            v += remainingY * travelTime;
            if (travelTime >= 1)
            {
                break;
            }

            var remainder = 1 - travelTime;
            remainingX *= remainder;
            remainingY *= remainder;

            // Resolve one shared edge at a time. A diagonal movement therefore
            // still passes through the screen that owns the first crossed edge.
            if (horizontalTime <= verticalTime)
            {
                var direction = remainingX > 0 ? 1 : -1;
                var crossingY = bounds.Top
                    + Math.Clamp(v, 0, 1) * height;
                var nextDisplay = FindHorizontalNeighbor(
                    direction,
                    crossingY);
                if (nextDisplay is null)
                {
                    remainingX = 0;
                    u = Math.Clamp(u, 0, 1);
                    continue;
                }

                Display = nextDisplay;
                bounds = nextDisplay.Bounds;
                width = Math.Max(bounds.Width - 1, 1);
                height = Math.Max(bounds.Height - 1, 1);
                u = direction > 0 ? 0 : 1;
                // Once two displays share a side, treat their entire logical
                // edges as aligned. Keeping the normalized perpendicular
                // position removes dead zones caused by size or offset.
                v = Math.Clamp(v, 0, 1);
            }
            else
            {
                var direction = remainingY > 0 ? 1 : -1;
                var crossingX = bounds.Left
                    + Math.Clamp(u, 0, 1) * width;
                var nextDisplay = FindVerticalNeighbor(
                    direction,
                    crossingX);
                if (nextDisplay is null)
                {
                    remainingY = 0;
                    v = Math.Clamp(v, 0, 1);
                    continue;
                }

                Display = nextDisplay;
                bounds = nextDisplay.Bounds;
                width = Math.Max(bounds.Width - 1, 1);
                height = Math.Max(bounds.Height - 1, 1);
                u = Math.Clamp(u, 0, 1);
                v = direction > 0 ? 0 : 1;
            }
        }

        _mapX = bounds.Left
            + Math.Clamp(u, 0, 1) * Math.Max(bounds.Width - 1, 1);
        _mapY = bounds.Top
            + Math.Clamp(v, 0, 1) * Math.Max(bounds.Height - 1, 1);
    }

    private static double BoundaryTime(double position, double delta)
    {
        if (delta > 0.000001)
        {
            return Math.Max(0, (1 - position) / delta);
        }

        if (delta < -0.000001)
        {
            return Math.Max(0, -position / delta);
        }

        return double.PositiveInfinity;
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

    private DisplayMonitor? FindHorizontalNeighbor(
        int direction,
        double crossingY)
    {
        var current = Display.Bounds;
        return _displays
            .Where(candidate => candidate.DeviceName != Display.DeviceName)
            .Where(candidate =>
                direction > 0
                    ? candidate.Bounds.Left == current.Right
                    : candidate.Bounds.Right == current.Left)
            .Where(candidate =>
                Overlaps(current.Top, current.Bottom,
                    candidate.Bounds.Top, candidate.Bounds.Bottom))
            .OrderBy(candidate => DistanceToOverlap(
                crossingY,
                Math.Max(current.Top, candidate.Bounds.Top),
                Math.Min(current.Bottom, candidate.Bounds.Bottom)))
            .ThenBy(candidate => candidate.Number)
            .FirstOrDefault();
    }

    private DisplayMonitor? FindVerticalNeighbor(
        int direction,
        double crossingX)
    {
        var current = Display.Bounds;
        return _displays
            .Where(candidate => candidate.DeviceName != Display.DeviceName)
            .Where(candidate =>
                direction > 0
                    ? candidate.Bounds.Top == current.Bottom
                    : candidate.Bounds.Bottom == current.Top)
            .Where(candidate =>
                Overlaps(current.Left, current.Right,
                    candidate.Bounds.Left, candidate.Bounds.Right))
            .OrderBy(candidate => DistanceToOverlap(
                crossingX,
                Math.Max(current.Left, candidate.Bounds.Left),
                Math.Min(current.Right, candidate.Bounds.Right)))
            .ThenBy(candidate => candidate.Number)
            .FirstOrDefault();
    }

    private static bool Overlaps(
        int firstStart,
        int firstEnd,
        int secondStart,
        int secondEnd) =>
        Math.Max(firstStart, secondStart) < Math.Min(firstEnd, secondEnd);

    private static double DistanceToOverlap(
        double position,
        int overlapStart,
        int overlapEnd)
    {
        var lastPixel = overlapEnd - 1;
        if (position < overlapStart)
        {
            return overlapStart - position;
        }

        return position > lastPixel ? position - lastPixel : 0;
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
