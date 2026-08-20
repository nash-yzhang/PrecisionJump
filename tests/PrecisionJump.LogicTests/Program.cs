using System.Diagnostics;
using System.Drawing;
using PrecisionJump.Services;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
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
var tick = Stopwatch.Frequency;

var screenSession = new NineGridSession(
    new Point(960, 540),
    [primary, secondary]);
Assert(
    screenSession.Depth == 0
    && screenSession.Preview.IsScreenLevel
    && screenSession.Display == primary,
    "Activation must start on the display containing the pointer.");

var screenMove = screenSession.MoveContinuous(
    physicalDeltaX: 30,
    physicalDeltaY: 0,
    mapUnitTravelDistance: 26,
    nowTicks: tick);
Assert(
    screenMove.JumpTarget is Point screenTarget
    && screenMove.Preview.Display == secondary
    && secondary.Bounds.Contains(screenTarget),
    "Screen-level movement must cross to the adjacent display.");
Assert(
    screenSession.Origin == new Point(960, 540),
    "Movement must preserve the activation origin for cancellation.");

var gappedSecondary = secondary with
{
    Bounds = new Rectangle(2020, 0, 1280, 1024),
    WorkingArea = new Rectangle(2020, 0, 1280, 984)
};
var gapSession = new NineGridSession(
    new Point(1910, 500),
    [primary, gappedSecondary]);
gapSession.ZoomIn(
    maximumDepth: 3,
    actualPosition: gapSession.CurrentPosition,
    nowTicks: tick);
gapSession.RefreshScale(nowTicks: tick * 2);
var gapMove = gapSession.MoveContinuous(
    physicalDeltaX: 1,
    physicalDeltaY: 0,
    mapUnitTravelDistance: 26,
    nowTicks: tick * 3);
Assert(
    gapMove.JumpTarget is Point gapTarget
    && gapMove.Preview.Display == primary
    && gapTarget == new Point(primary.Bounds.Right - 1, 500),
    "A display separated by a gap must not be treated as an adjacent display.");

var upperRight = new DisplayMonitor(
    "UPPER_RIGHT",
    new Rectangle(1920, 0, 1280, 540),
    new Rectangle(1920, 0, 1280, 500),
    IsPrimary: false,
    Number: 2);
var lowerRight = new DisplayMonitor(
    "LOWER_RIGHT",
    new Rectangle(1920, 540, 1280, 540),
    new Rectangle(1920, 540, 1280, 500),
    IsPrimary: false,
    Number: 3);
var sideSession = new NineGridSession(
    new Point(1910, 800),
    [primary, upperRight, lowerRight]);
var sideMove = sideSession.MoveContinuous(
    physicalDeltaX: 2,
    physicalDeltaY: 0,
    mapUnitTravelDistance: 26,
    nowTicks: tick);
Assert(
    sideMove.JumpTarget is Point sideTarget
    && sideMove.Preview.Display == lowerRight
    && lowerRight.Bounds.Contains(sideTarget),
    "A side crossing must enter the screen that shares that edge position.");

var forcedAlignmentSession = new NineGridSession(
    new Point(1910, 800),
    [primary, upperRight]);
var forcedAlignmentMove = forcedAlignmentSession.MoveContinuous(
    physicalDeltaX: 2,
    physicalDeltaY: 0,
    mapUnitTravelDistance: 26,
    nowTicks: tick);
Assert(
    forcedAlignmentMove.JumpTarget is Point alignedTarget
    && forcedAlignmentMove.Preview.Display == upperRight
    && upperRight.Bounds.Contains(alignedTarget)
    && alignedTarget.Y == 400,
    "An unaligned part of a related side must map proportionally instead of blocking movement.");

var lowerDisplay = new DisplayMonitor(
    "LOWER",
    new Rectangle(0, 1080, 1600, 900),
    new Rectangle(0, 1080, 1600, 860),
    IsPrimary: false,
    Number: 4);
var verticalSession = new NineGridSession(
    new Point(800, 1070),
    [primary, lowerDisplay]);
var verticalMove = verticalSession.MoveContinuous(
    physicalDeltaX: 0,
    physicalDeltaY: 2,
    mapUnitTravelDistance: 26,
    nowTicks: tick);
Assert(
    verticalMove.JumpTarget is Point verticalTarget
    && verticalMove.Preview.Display == lowerDisplay
    && lowerDisplay.Bounds.Contains(verticalTarget),
    "A top/bottom shared edge must create a vertical display relationship.");

var forcedVerticalSession = new NineGridSession(
    new Point(1800, 1070),
    [primary, lowerDisplay]);
var forcedVerticalMove = forcedVerticalSession.MoveContinuous(
    physicalDeltaX: 0,
    physicalDeltaY: 2,
    mapUnitTravelDistance: 26,
    nowTicks: tick);
Assert(
    forcedVerticalMove.JumpTarget is Point alignedVerticalTarget
    && forcedVerticalMove.Preview.Display == lowerDisplay
    && lowerDisplay.Bounds.Contains(alignedVerticalTarget)
    && alignedVerticalTarget.X == 1500,
    "An unaligned top/bottom edge must preserve the normalized horizontal position.");

var zoomOrigin = new Point(1000, 500);
var zoomSession = new NineGridSession(zoomOrigin, [primary]);
var interaction = zoomSession.ZoomIn(
    maximumDepth: 3,
    actualPosition: zoomOrigin,
    nowTicks: tick);
Assert(
    interaction.JumpTarget is null
    && zoomSession.Depth == 1
    && zoomSession.CurrentPosition == zoomOrigin,
    "Zooming must preserve the exact pointer position.");

zoomSession.RefreshScale(nowTicks: tick * 2);
Assert(
    Math.Abs(zoomSession.MapScale - 3) < 0.001,
    "The first completed zoom level must use a 3x map scale.");

interaction = zoomSession.MoveContinuous(
    physicalDeltaX: 26,
    physicalDeltaY: 0,
    mapUnitTravelDistance: 26,
    nowTicks: tick * 3);
var firstTarget = interaction.JumpTarget
    ?? throw new InvalidOperationException("Continuous movement did not produce a target.");
Assert(
    firstTarget.X == 1640
    && firstTarget.Y == zoomOrigin.Y,
    "At 3x scale, one configured travel unit must move one third of the display width.");

zoomSession.ZoomIn(
    maximumDepth: 3,
    actualPosition: firstTarget,
    nowTicks: tick * 4);
zoomSession.RefreshScale(nowTicks: tick * 5);
Assert(
    zoomSession.Depth == 2
    && Math.Abs(zoomSession.MapScale - 9) < 0.001
    && zoomSession.CurrentPosition == firstTarget,
    "Further refinement must preserve the pointer and reach the next 3x scale.");

var positionBeforeZoomOut = zoomSession.CurrentPosition;
zoomSession.ZoomOut(
    actualPosition: positionBeforeZoomOut,
    nowTicks: tick * 6);
Assert(
    zoomSession.Depth == 1
    && zoomSession.CurrentPosition == positionBeforeZoomOut,
    "Zooming out must preserve the exact pointer position.");

for (var index = 0; index < 20; index++)
{
    zoomSession.ZoomIn(
        maximumDepth: 3,
        nowTicks: tick * (7 + index));
}
Assert(
    zoomSession.Depth == 3,
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
    "Tab must end an Alt-activated map so Alt+Tab reaches Windows.");

using (var injector = new MouseMoveInjector())
{
    injector.ObservePosition(new Point(100, 100));
    var firstDelta = injector.PhysicalDeltaFrom(new Point(103, 101));
    var repeatedDelta = injector.PhysicalDeltaFrom(new Point(103, 101));
    Assert(
        firstDelta == new Point(3, 1)
        && repeatedDelta == firstDelta,
        "Repeated hooks before injection completes must not accumulate the same displacement twice.");

    injector.ObservePosition(new Point(-500, 250));
    Assert(
        injector.PhysicalDeltaFrom(new Point(-498, 247))
            == new Point(2, -3),
        "The applied injection position must atomically become the next physical-delta baseline.");
}

Assert(
    MousePositionRegisters.TryGetRegister(0x41, out var letterRegister)
    && letterRegister == 'A'
    && MousePositionRegisters.TryGetRegister(0x37, out var numberRegister)
    && numberRegister == '7'
    && MousePositionRegisters.TryGetRegister(0x63, out var numpadRegister)
    && numpadRegister == '3'
    && !MousePositionRegisters.TryGetRegister(0x20, out _),
    "Only A-Z and 0-9 may select a saved pointer position.");

var disconnectedPosition = new Point(4000, 2000);
Assert(
    MousePositionRegisters.ResolveDestination(
        disconnectedPosition,
        [primary, secondary]) == new Point(3199, 1023),
    "A position on a disconnected display must clamp to the nearest available display.");
Assert(
    MousePositionRegisters.ResolveDestination(
        new Point(2000, 500),
        [primary, secondary]) == new Point(2000, 500),
    "A position on a connected display must remain exact.");

Console.WriteLine("CONTINUOUS_SCREEN_MAP=PASSED");
Console.WriteLine("EDGE_ADJACENT_DISPLAY_MAP=PASSED");
Console.WriteLine("ANIMATED_ZOOM_SCALE=PASSED");
Console.WriteLine("POINTER_PRESERVING_ZOOM=PASSED");
Console.WriteLine("MAXIMUM_REFINEMENT_DEPTH=PASSED");
Console.WriteLine("COMBINED_KEY_SEQUENCE=PASSED");
Console.WriteLine("ASYNC_INJECTION_BASELINE=PASSED");
Console.WriteLine("POSITION_REGISTER_KEYS=PASSED");
Console.WriteLine("POSITION_RECALL_DISPLAY_CLAMP=PASSED");
Console.WriteLine("PRECISION_JUMP_LOGIC_TESTS_PASSED");
