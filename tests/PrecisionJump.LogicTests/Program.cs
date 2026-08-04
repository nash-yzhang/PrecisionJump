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

var primary = new DisplayMonitor(
    "PRIMARY",
    new Rectangle(0, 0, 1920, 1080),
    new Rectangle(0, 0, 1920, 1040),
    IsPrimary: true,
    Number: 1);
var secondary = new DisplayMonitor(
    "SECONDARY",
    new Rectangle(1920, 0, 1280, 1024),
    new Rectangle(1920, 0, 1280, 984),
    IsPrimary: false,
    Number: 2);
var cooldown = TimeSpan.FromMilliseconds(120);
var tick = Stopwatch.Frequency;

var screenSession = new NineGridSession(
    new Point(960, 540),
    [primary, secondary]);

Assert(
    screenSession.Depth == 0
    && screenSession.Preview.IsScreenLevel
    && screenSession.Preview.SelectedRegion == primary.Bounds,
    "Activation must start at the physical-screen level.");
var screenMove = screenSession.Move(
    new Point(990, 540),
    selectionDistance: 26,
    cooldown,
    nowTicks: tick);
Assert(
    screenMove.Preview.Display == secondary
    && screenMove.JumpTarget == Center(secondary.Bounds),
    "A small screen-level gesture must select and jump to the adjacent display.");
Assert(
    screenSession.Origin == new Point(960, 540),
    "The activation point must be preserved for right-click cancellation.");

var session = new NineGridSession(
    new Point(960, 540),
    [primary]);
var interaction = session.ZoomIn(
    maximumDepth: 8,
    cooldown,
    nowTicks: tick);
var centerCell = NineGridSession.Cell(primary.Bounds, 1, 1);
Assert(
    session.Depth == 1
    && session.GridSize == 3
    && interaction.Preview.CurrentRegion == primary.Bounds,
    "Wheel-up from the screen level must open the first full-screen 3x3 grid.");
Assert(
    interaction.Preview.SelectedRegion == centerCell
    && interaction.JumpTarget == Center(centerCell),
    "The first grid must begin at its center cell.");

Assert(
    NineGridSession.Direction(20, 20, 26) == (1, 1),
    "A short diagonal gesture whose vector clears the threshold must select a corner.");
Assert(
    NineGridSession.Direction(30, 12, 26) == (1, 1),
    "Near-diagonal input must favor a corner instead of a cardinal neighbor.");
Assert(
    NineGridSession.Direction(30, 4, 26) == (0, 1),
    "A clearly horizontal gesture must still select the right neighbor.");

session.AcceptProgrammaticPosition(
    interaction.JumpTarget!.Value,
    cooldown,
    nowTicks: tick * 2);
var anchor = interaction.JumpTarget.Value;
interaction = session.Move(
    new Point(anchor.X - 20, anchor.Y - 20),
    selectionDistance: 26,
    cooldown,
    nowTicks: tick * 3);
var upperLeftCell = NineGridSession.Cell(primary.Bounds, 0, 0);
Assert(
    interaction.Preview.GlobalRow == 0
    && interaction.Preview.GlobalColumn == 0
    && interaction.JumpTarget == Center(upperLeftCell),
    "A corner gesture must reliably select the diagonal cell.");

var globalSession = new NineGridSession(
    new Point(960, 540),
    [primary]);
interaction = globalSession.ZoomIn(
    maximumDepth: 8,
    cooldown,
    nowTicks: tick);
globalSession.AcceptProgrammaticPosition(
    interaction.JumpTarget!.Value,
    cooldown,
    nowTicks: tick * 2);
interaction = globalSession.ZoomIn(
    maximumDepth: 8,
    cooldown,
    nowTicks: tick * 3);
Assert(
    globalSession.Depth == 2
    && globalSession.GridSize == 9
    && globalSession.GlobalRow == 4
    && globalSession.GlobalColumn == 4,
    "Refinement must create one global 9x9 grid, centered on the prior cell.");

globalSession.AcceptProgrammaticPosition(
    interaction.JumpTarget!.Value,
    cooldown,
    nowTicks: tick * 4);
anchor = interaction.JumpTarget.Value;
interaction = globalSession.Move(
    new Point(anchor.X + 30, anchor.Y),
    selectionDistance: 26,
    cooldown,
    nowTicks: tick * 5);
globalSession.AcceptProgrammaticPosition(
    interaction.JumpTarget!.Value,
    cooldown,
    nowTicks: tick * 6);
anchor = interaction.JumpTarget.Value;
interaction = globalSession.Move(
    new Point(anchor.X + 30, anchor.Y),
    selectionDistance: 26,
    cooldown,
    nowTicks: tick * 7);

var rightParent = NineGridSession.Cell(primary.Bounds, 1, 2);
var globalFineCell = NineGridSession.GridCell(primary.Bounds, 9, 4, 6);
Assert(
    globalSession.GlobalColumn == 6
    && interaction.Preview.CurrentRegion == rightParent
    && interaction.Preview.SelectedRegion == globalFineCell,
    "Fine movement must cross into a sibling parent without dropping depth.");

interaction = globalSession.ZoomOut(
    cooldown,
    nowTicks: tick * 8);
Assert(
    globalSession.Depth == 1
    && interaction.Preview.SelectedRegion == rightParent,
    "Wheel-down must select the coarse parent containing the fine cell.");
interaction = globalSession.ZoomOut(
    cooldown,
    nowTicks: tick * 9);
Assert(
    globalSession.Depth == 0
    && interaction.Preview.IsScreenLevel,
    "Wheel-down from the first grid must return to the screen level.");

for (var index = 0; index < 20; index++)
{
    globalSession.ZoomIn(
        maximumDepth: 3,
        cooldown,
        nowTicks: tick * (10 + index));
}
Assert(
    globalSession.Depth == 3,
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
    "The final key must complete even when the prefix key is still held.");

Assert(
    !ShortcutPolicy.ShouldSuppressActivationKey(0x12),
    "Alt must never be swallowed as an activation key.");
Assert(
    ShortcutPolicy.ShouldCancelActiveGesture(
        finalKey: 0x12,
        pressedKey: 0x09,
        firstKeyDown: true),
    "Tab must end an Alt-activated grid so Alt+Tab reaches Windows.");

Console.WriteLine("SCREEN_ROOT_LEVEL=PASSED");
Console.WriteLine("COMBINED_KEY_SEQUENCE=PASSED");
Console.WriteLine("CORNER_DIRECTION_BIAS=PASSED");
Console.WriteLine("GLOBAL_FINE_GRID=PASSED");
Console.WriteLine("DYNAMIC_PARENT_PREVIEW=PASSED");
Console.WriteLine("MAXIMUM_REFINEMENT_DEPTH=PASSED");
Console.WriteLine("PRECISION_JUMP_LOGIC_TESTS_PASSED");
