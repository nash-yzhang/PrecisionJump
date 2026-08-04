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
    int GridSize,
    double StepX,
    double StepY)
{
    public bool IsScreenLevel => Depth == 0;

    public string Label => IsScreenLevel
        ? $"SCREEN {Display.Number}"
        : $"LEVEL {Depth}";
}

public sealed record GridInteraction(
    NineGridPreview Preview,
    Point? JumpTarget);

public sealed class NineGridSession
{
    private readonly IReadOnlyList<DisplayMonitor> _displays;
    private Point _movementAnchor;
    private long _blockedUntil;
    private int _depth;

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
        CurrentPosition = origin;
        _movementAnchor = origin;
        Display = FindDisplay(origin);
        Preview = CreatePreview();
    }

    public Point Origin { get; }
    public Point CurrentPosition { get; private set; }
    public DisplayMonitor Display { get; private set; }
    public int Depth => _depth;
    public int GridSize => GridSizeAtDepth(Depth);
    public double StepX => Display.Bounds.Width / (double)GridSize;
    public double StepY => Display.Bounds.Height / (double)GridSize;
    public NineGridPreview Preview { get; private set; }

    public GridInteraction Move(
        Point actualPosition,
        double selectionDistance,
        TimeSpan cooldown,
        long? nowTicks = null)
    {
        var now = nowTicks ?? Stopwatch.GetTimestamp();
        CurrentPosition = actualPosition;

        if (now < _blockedUntil)
        {
            Preview = CreatePreview();
            return new GridInteraction(Preview, null);
        }

        var dx = actualPosition.X - _movementAnchor.X;
        var dy = actualPosition.Y - _movementAnchor.Y;
        if (!ClearsThreshold(dx, dy, selectionDistance))
        {
            Preview = CreatePreview();
            return new GridInteraction(Preview, null);
        }

        if (Depth == 0)
        {
            var targetDisplay = FindDirectionalDisplay(dx, dy);
            if (targetDisplay is null)
            {
                _movementAnchor = actualPosition;
                Preview = CreatePreview();
                return new GridInteraction(Preview, null);
            }

            Display = targetDisplay;
            var screenTarget = Center(Display.Bounds);
            CurrentPosition = screenTarget;
            _movementAnchor = screenTarget;
            _blockedUntil = now + ToStopwatchTicks(cooldown);
            Preview = CreatePreview();
            return new GridInteraction(Preview, screenTarget);
        }

        var (rowDelta, columnDelta) = Direction(
            dx,
            dy,
            selectionDistance);
        var target = FixedGridTarget(
            _movementAnchor,
            rowDelta,
            columnDelta);
        if (target == _movementAnchor)
        {
            _movementAnchor = actualPosition;
            Preview = CreatePreview();
            return new GridInteraction(Preview, null);
        }

        CurrentPosition = target;
        _movementAnchor = target;
        _blockedUntil = now + ToStopwatchTicks(cooldown);
        Preview = CreatePreview();
        return new GridInteraction(Preview, target);
    }

    public GridInteraction ZoomIn(
        int maximumDepth,
        TimeSpan cooldown,
        Point? actualPosition = null,
        long? nowTicks = null)
    {
        if (Depth >= maximumDepth)
        {
            return new GridInteraction(Preview, null);
        }

        var nextGridSize = Math.Pow(3, Depth + 1);
        if (
            Display.Bounds.Width < nextGridSize
            || Display.Bounds.Height < nextGridSize
        )
        {
            return new GridInteraction(Preview, null);
        }

        _depth++;
        var position = actualPosition ?? CurrentPosition;
        Reanchor(position, cooldown, nowTicks);
        Preview = CreatePreview();
        return new GridInteraction(Preview, null);
    }

    public GridInteraction ZoomOut(
        TimeSpan cooldown,
        Point? actualPosition = null,
        long? nowTicks = null)
    {
        if (Depth == 0)
        {
            return new GridInteraction(Preview, null);
        }

        _depth--;
        var position = actualPosition ?? CurrentPosition;
        Reanchor(position, cooldown, nowTicks);
        Preview = CreatePreview();
        return new GridInteraction(Preview, null);
    }

    public void AcceptProgrammaticPosition(
        Point actualPosition,
        TimeSpan cooldown,
        long? nowTicks = null)
    {
        Reanchor(actualPosition, cooldown, nowTicks);
        Preview = CreatePreview();
    }

    public static (int RowDelta, int ColumnDelta) Direction(
        double dx,
        double dy,
        double selectionDistance)
    {
        if (!ClearsThreshold(dx, dy, selectionDistance))
        {
            return (0, 0);
        }

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

    private static bool ClearsThreshold(
        double dx,
        double dy,
        double selectionDistance)
    {
        return dx * dx + dy * dy
            >= selectionDistance * selectionDistance;
    }

    private Point FixedGridTarget(
        Point anchor,
        int rowDelta,
        int columnDelta)
    {
        var (currentRow, currentColumn) = FindGridCell(
            Display.Bounds,
            GridSize,
            anchor);
        var targetRow = Math.Clamp(
            currentRow + rowDelta,
            0,
            GridSize - 1);
        var targetColumn = Math.Clamp(
            currentColumn + columnDelta,
            0,
            GridSize - 1);
        if (
            targetRow == currentRow
            && targetColumn == currentColumn
        )
        {
            return anchor;
        }

        return Center(GridCell(
            Display.Bounds,
            GridSize,
            targetRow,
            targetColumn));
    }

    private void Reanchor(
        Point position,
        TimeSpan cooldown,
        long? nowTicks)
    {
        CurrentPosition = position;
        _movementAnchor = position;
        var now = nowTicks ?? Stopwatch.GetTimestamp();
        _blockedUntil = now + ToStopwatchTicks(cooldown);
    }

    private NineGridPreview CreatePreview()
    {
        return new NineGridPreview(
            Display,
            CurrentPosition,
            Depth,
            GridSize,
            StepX,
            StepY);
    }

    public static Rectangle GridCell(
        Rectangle region,
        int gridSize,
        int row,
        int column)
    {
        gridSize = Math.Max(gridSize, 1);
        row = Math.Clamp(row, 0, gridSize - 1);
        column = Math.Clamp(column, 0, gridSize - 1);
        var left = region.Left + region.Width * column / gridSize;
        var right = region.Left + region.Width * (column + 1) / gridSize;
        var top = region.Top + region.Height * row / gridSize;
        var bottom = region.Top + region.Height * (row + 1) / gridSize;
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    public static (int Row, int Column) FindGridCell(
        Rectangle region,
        int gridSize,
        Point position)
    {
        gridSize = Math.Max(gridSize, 1);
        var relativeX =
            (position.X - region.Left) / (double)Math.Max(region.Width, 1);
        var relativeY =
            (position.Y - region.Top) / (double)Math.Max(region.Height, 1);
        return (
            Math.Clamp((int)Math.Floor(relativeY * gridSize), 0, gridSize - 1),
            Math.Clamp((int)Math.Floor(relativeX * gridSize), 0, gridSize - 1));
    }

    private static int GridSizeAtDepth(int depth)
    {
        var size = 1;
        for (var index = 0; index < depth; index++)
        {
            size *= 3;
        }
        return size;
    }

    private DisplayMonitor? FindDirectionalDisplay(double dx, double dy)
    {
        var gestureLength = Math.Sqrt(dx * dx + dy * dy);
        if (gestureLength <= 0)
        {
            return null;
        }

        var currentCenter = Center(Display.Bounds);
        var candidates = _displays
            .Where(candidate => candidate.DeviceName != Display.DeviceName)
            .Select(candidate =>
            {
                var candidateCenter = Center(candidate.Bounds);
                var candidateX = candidateCenter.X - currentCenter.X;
                var candidateY = candidateCenter.Y - currentCenter.Y;
                var candidateLength = Math.Sqrt(
                    candidateX * candidateX + candidateY * candidateY);
                var cosine = candidateLength <= 0
                    ? -1
                    : (dx * candidateX + dy * candidateY)
                        / (gestureLength * candidateLength);
                return new
                {
                    Display = candidate,
                    Cosine = cosine,
                    DistanceSquared =
                        (long)candidateX * candidateX
                        + (long)candidateY * candidateY
                };
            })
            .Where(candidate => candidate.Cosine > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        // Treat monitors within a small angular band as being in the same
        // intended direction, then choose the nearest. This prevents a far
        // monitor with a marginally better center angle from hiding a middle
        // monitor in an uneven physical layout.
        var bestCosine = candidates.Max(candidate => candidate.Cosine);
        const double angularScoreTolerance = 0.12;
        return candidates
            .Where(candidate =>
                candidate.Cosine >= bestCosine - angularScoreTolerance)
            .OrderBy(candidate => candidate.DistanceSquared)
            .Select(candidate => candidate.Display)
            .FirstOrDefault();
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

    private static Point Center(Rectangle region)
    {
        return new Point(
            region.Left + region.Width / 2,
            region.Top + region.Height / 2);
    }

    private static long ToStopwatchTicks(TimeSpan duration)
    {
        return (long)Math.Round(duration.TotalSeconds * Stopwatch.Frequency);
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
