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
    Rectangle CurrentRegion,
    Rectangle SelectedRegion,
    Point ActualCursor,
    int Depth,
    int GridSize,
    int GlobalRow,
    int GlobalColumn,
    int SelectedRow,
    int SelectedColumn)
{
    public bool IsScreenLevel => Depth == 0;

    public string Label => IsScreenLevel
        ? $"SCREEN {Display.Number}"
        : $"LEVEL {Depth}  ·  CELL {GlobalRow + 1},{GlobalColumn + 1}";
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
    private int _globalRow;
    private int _globalColumn;

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
    public int GlobalRow => _globalRow;
    public int GlobalColumn => _globalColumn;
    public int SelectedRow => Depth == 0 ? 0 : _globalRow % 3;
    public int SelectedColumn => Depth == 0 ? 0 : _globalColumn % 3;
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
        var (rowDelta, columnDelta) = Direction(
            dx,
            dy,
            selectionDistance);
        if (rowDelta == 0 && columnDelta == 0)
        {
            Preview = CreatePreview();
            return new GridInteraction(Preview, null);
        }

        if (Depth == 0)
        {
            var targetDisplay = FindDirectionalDisplay(
                rowDelta,
                columnDelta);
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

        var row = Math.Clamp(_globalRow + rowDelta, 0, GridSize - 1);
        var column = Math.Clamp(
            _globalColumn + columnDelta,
            0,
            GridSize - 1);
        if (row == _globalRow && column == _globalColumn)
        {
            _movementAnchor = actualPosition;
            Preview = CreatePreview();
            return new GridInteraction(Preview, null);
        }

        _globalRow = row;
        _globalColumn = column;
        var target = Center(SelectedCell());
        CurrentPosition = target;
        _movementAnchor = target;
        _blockedUntil = now + ToStopwatchTicks(cooldown);
        Preview = CreatePreview();
        return new GridInteraction(Preview, target);
    }

    public GridInteraction ZoomIn(
        int maximumDepth,
        TimeSpan cooldown,
        long? nowTicks = null)
    {
        if (Depth >= maximumDepth)
        {
            return new GridInteraction(Preview, null);
        }

        var nextGridSize = checked(GridSize * 3);
        if (
            Display.Bounds.Width < nextGridSize
            || Display.Bounds.Height < nextGridSize
        )
        {
            return new GridInteraction(Preview, null);
        }

        _globalRow = Depth == 0 ? 1 : _globalRow * 3 + 1;
        _globalColumn = Depth == 0 ? 1 : _globalColumn * 3 + 1;
        _depth++;
        var target = Center(SelectedCell());
        Reanchor(target, cooldown, nowTicks);
        Preview = CreatePreview();
        return new GridInteraction(Preview, target);
    }

    public GridInteraction ZoomOut(
        TimeSpan cooldown,
        long? nowTicks = null)
    {
        if (Depth == 0)
        {
            return new GridInteraction(Preview, null);
        }

        _depth--;
        _globalRow = Depth == 0 ? 0 : _globalRow / 3;
        _globalColumn = Depth == 0 ? 0 : _globalColumn / 3;
        var target = Depth == 0
            ? CurrentPosition
            : Center(SelectedCell());
        Reanchor(target, cooldown, nowTicks);
        Preview = CreatePreview();
        return new GridInteraction(Preview, target);
    }

    public void AcceptProgrammaticPosition(
        Point actualPosition,
        TimeSpan cooldown,
        long? nowTicks = null)
    {
        Reanchor(actualPosition, cooldown, nowTicks);
        Preview = CreatePreview();
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
        var gridSize = GridSize;
        var currentRegion = Depth switch
        {
            0 => Display.Bounds,
            1 => Display.Bounds,
            _ => GridCell(
                Display.Bounds,
                gridSize / 3,
                _globalRow / 3,
                _globalColumn / 3)
        };
        var selectedRegion = Depth == 0
            ? Display.Bounds
            : SelectedCell();

        return new NineGridPreview(
            Display,
            currentRegion,
            selectedRegion,
            CurrentPosition,
            Depth,
            gridSize,
            _globalRow,
            _globalColumn,
            SelectedRow,
            SelectedColumn);
    }

    private Rectangle SelectedCell()
    {
        return Depth == 0
            ? Display.Bounds
            : GridCell(
                Display.Bounds,
                GridSize,
                _globalRow,
                _globalColumn);
    }

    public static Rectangle Cell(Rectangle region, int row, int column)
    {
        return GridCell(region, 3, row, column);
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

    public static (int RowDelta, int ColumnDelta) Direction(
        double dx,
        double dy,
        double selectionDistance)
    {
        if (
            dx * dx + dy * dy
            < selectionDistance * selectionDistance
        )
        {
            return (0, 0);
        }

        var absX = Math.Abs(dx);
        var absY = Math.Abs(dy);

        // Cardinal directions deliberately have a narrower cone than
        // diagonals. This makes the four corner cells easy to acquire while
        // still allowing a clearly horizontal or vertical gesture.
        const double cardinalCone = 0.32;
        if (absY <= absX * cardinalCone)
        {
            return (0, Math.Sign(dx));
        }
        if (absX <= absY * cardinalCone)
        {
            return (Math.Sign(dy), 0);
        }

        return (Math.Sign(dy), Math.Sign(dx));
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
                    return dx * dx + dy * dy;
                })
                .First();
    }

    private DisplayMonitor? FindDirectionalDisplay(
        int rowDelta,
        int columnDelta)
    {
        var currentCenter = Center(Display.Bounds);
        return _displays
            .Where(candidate => candidate.DeviceName != Display.DeviceName)
            .Select(candidate =>
            {
                var candidateCenter = Center(candidate.Bounds);
                var dx = candidateCenter.X - currentCenter.X;
                var dy = candidateCenter.Y - currentCenter.Y;
                var direction = Direction(dx, dy, selectionDistance: 0);
                var distanceSquared =
                    (long)dx * dx + (long)dy * dy;
                return new
                {
                    Display = candidate,
                    Direction = direction,
                    DistanceSquared = distanceSquared
                };
            })
            .Where(candidate =>
                candidate.Direction
                    == (RowDelta: rowDelta, ColumnDelta: columnDelta))
            .OrderBy(candidate => candidate.DistanceSquared)
            .Select(candidate => candidate.Display)
            .FirstOrDefault();
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
