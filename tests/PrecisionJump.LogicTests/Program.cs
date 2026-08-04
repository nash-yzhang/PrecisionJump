using System.Diagnostics;
using System.Drawing;
using MouseAccelerator.Services;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static Point Center(Rectangle rectangle)
{
    return new Point(
        rectangle.Left + rectangle.Width / 2,
        rectangle.Top + rectangle.Height / 2);
}

static Point FixedGridTarget(
    Point anchor,
    int row,
    int column,
    int gridSize,
    Rectangle bounds)
{
    var (currentRow, currentColumn) = NineGridSession.FindGridCell(
        bounds,
        gridSize,
        anchor);
    var cell = NineGridSession.GridCell(
        bounds,
        gridSize,
        currentRow + row,
        currentColumn + column);
    return Center(cell);
}

var primary = new DisplayMonitor(
    "PRIMARY",
    new Rectangle(0, 0, 1920, 1080),
    new Rectangle(0, 0, 1920, 1040),
    IsPrimary: true,
    Number: 1);
var middle = new DisplayMonitor(
    "MIDDLE",
    new Rectangle(1920, 0, 1280, 1024),
    new Rectangle(1920, 0, 1280, 984),
    IsPrimary: false,
    Number: 2);
var farRight = new DisplayMonitor(
    "FAR_RIGHT",
    new Rectangle(3200, 0, 1280, 1024),
    new Rectangle(3200, 0, 1280, 984),
    IsPrimary: false,
    Number: 3);
var cooldown = TimeSpan.FromMilliseconds(120);
var tick = Stopwatch.Frequency;

var screenSession = new NineGridSession(
    new Point(960, 540),
    [primary, middle, farRight]);
Assert(
    screenSession.Depth == 0
    && screenSession.Preview.IsScreenLevel
    && screenSession.Preview.Display == primary,
    "Activation must start at the physical-screen level.");

var screenMove = screenSession.Move(
    new Point(990, 548),
    selectionDistance: 26,
    cooldown,
    nowTicks: tick);
Assert(
    screenMove.Preview.Display == middle
    && screenMove.JumpTarget == Center(middle.Bounds),
    "Continuous screen scoring must choose the nearer middle display.");
Assert(
    screenSession.Origin == new Point(960, 540),
    "The activation point must be preserved for cancellation.");

Assert(
    NineGridSession.Direction(20, 20, 26) == (1, 1),
    "A diagonal vector must select a diagonal neighbor.");
Assert(
    NineGridSession.Direction(30, 12, 26) == (0, 1),
    "A vector below the equal-angle boundary must select right.");
Assert(
    NineGridSession.Direction(30, 13, 26) == (1, 1),
    "A vector above the equal-angle boundary must select down-right.");
Assert(
    NineGridSession.Direction(10, 10, 26) == (0, 0),
    "Movement inside the radial dead zone must not select a cell.");

var zoomPoint = new Point(1000, 500);
var session = new NineGridSession(zoomPoint, [primary]);
var interaction = session.ZoomIn(
    maximumDepth: 8,
    cooldown,
    actualPosition: zoomPoint,
    nowTicks: tick);
Assert(
    session.Depth == 1
    && Math.Abs(session.StepX - 640) < 0.01
    && Math.Abs(session.StepY - 360) < 0.01,
    "Level one must use one-third-screen floating steps.");
Assert(
    interaction.JumpTarget is null
    && session.CurrentPosition == zoomPoint
    && interaction.Preview.ActualCursor == zoomPoint,
    "Zoom must preserve the exact pointer and center the floating grid on it.");

var expectedFirstTarget = FixedGridTarget(
    zoomPoint,
    row: 1,
    column: 1,
    session.GridSize,
    primary.Bounds);
interaction = session.Move(
    new Point(1030, 513),
    selectionDistance: 26,
    cooldown,
    nowTicks: tick * 3);
Assert(
    interaction.JumpTarget == expectedFirstTarget
    && interaction.Preview.ActualCursor == expectedFirstTarget,
    "Movement must target the adjacent cell in the screen-anchored grid.");

session.AcceptProgrammaticPosition(
    expectedFirstTarget,
    cooldown,
    nowTicks: tick * 4);
interaction = session.ZoomIn(
    maximumDepth: 8,
    cooldown,
    actualPosition: expectedFirstTarget,
    nowTicks: tick * 5);
Assert(
    session.Depth == 2
    && Math.Abs(session.StepX - 1920d / 9) < 0.01
    && Math.Abs(session.StepY - 1080d / 9) < 0.01
    && session.GridSize == 9
    && interaction.JumpTarget is null
    && session.CurrentPosition == expectedFirstTarget,
    "Further refinement must create a finer fixed grid without parent regions.");

var fixedReferenceCell = NineGridSession.GridCell(
    primary.Bounds,
    session.GridSize,
    row: 4,
    column: 4);
Assert(
    fixedReferenceCell
        == NineGridSession.GridCell(primary.Bounds, 9, 4, 4),
    "Grid geometry must remain anchored to the display.");

var expectedSecondTarget = FixedGridTarget(
    expectedFirstTarget,
    row: 0,
    column: -1,
    session.GridSize,
    primary.Bounds);
interaction = session.Move(
    new Point(expectedFirstTarget.X - 30, expectedFirstTarget.Y),
    selectionDistance: 26,
    cooldown,
    nowTicks: tick * 7);
Assert(
    interaction.JumpTarget == expectedSecondTarget
    && interaction.Preview.ActualCursor == expectedSecondTarget,
    "Moving the pointer must not translate the fixed grid.");

var positionBeforeZoomOut = session.CurrentPosition;
interaction = session.ZoomOut(
    cooldown,
    actualPosition: positionBeforeZoomOut,
    nowTicks: tick * 8);
Assert(
    session.Depth == 1
    && interaction.JumpTarget is null
    && session.CurrentPosition == positionBeforeZoomOut,
    "Wheel-down must preserve the exact pointer position.");

interaction = session.ZoomOut(
    cooldown,
    actualPosition: positionBeforeZoomOut,
    nowTicks: tick * 9);
Assert(
    session.Depth == 0
    && interaction.Preview.IsScreenLevel
    && session.CurrentPosition == positionBeforeZoomOut,
    "Wheel-down from level one must return to the screen layer in place.");

for (var index = 0; index < 20; index++)
{
    session.ZoomIn(
        maximumDepth: 3,
        cooldown,
        nowTicks: tick * (10 + index));
}
Assert(
    session.Depth == 3,
    "Refinement must respect the configured maximum depth.");

var matcher = new SequenceMatcher();
var expectedCombinedSequence = new[] { 0x11, 0x4B };
Assert(
    matcher.Consume(
        0x11,
        expectedCombinedSequence,
        TimeSpan.FromSeconds(0.5),
        tick) == MatchProgress.Prefix,
    "The first key of a combined sequence must be retained.");
Assert(
    matcher.Consume(
        0x4B,
        expectedCombinedSequence,
        TimeSpan.FromSeconds(0.5),
        tick + tick / 10) == MatchProgress.Complete,
    "The final key must complete while the prefix remains held.");

Assert(
    !ShortcutPolicy.ShouldSuppressActivationKey(0x12),
    "Alt must never be swallowed as an activation key.");
Assert(
    ShortcutPolicy.ShouldCancelActiveGesture(
        finalKey: 0x12,
        pressedKey: 0x09,
        firstKeyDown: true),
    "Tab must end an Alt-activated grid so Alt+Tab reaches Windows.");

Console.WriteLine("CONTINUOUS_SCREEN_SELECTION=PASSED");
Console.WriteLine("EQUAL_ANGLE_DIRECTION_SELECTION=PASSED");
Console.WriteLine("SCREEN_ANCHORED_FIXED_GRID=PASSED");
Console.WriteLine("DISTANCE_ALPHA_PREVIEW_MODEL=PASSED");
Console.WriteLine("WHEEL_ZOOM_PRESERVES_POINTER=PASSED");
Console.WriteLine("MAXIMUM_REFINEMENT_DEPTH=PASSED");
Console.WriteLine("COMBINED_KEY_SEQUENCE=PASSED");
Console.WriteLine("PRECISION_JUMP_LOGIC_TESTS_PASSED");
