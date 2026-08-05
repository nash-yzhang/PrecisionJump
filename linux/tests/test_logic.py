"""Logic tests for the Linux port.

Covers the continuous-mapping design: screen-level topology hops, zoom
preserving the pointer, depth limits, scale easing, and shortcut matching.

Run with:  python3 -m tests.test_logic     (from the linux/ directory)
"""

from __future__ import annotations

import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from precision_jump.geometry import DisplayMonitor, Point, Rectangle
from precision_jump.screen_targeting import (
    SCALE_ANIMATION_SECONDS,
    NineGridSession,
    _octant,
    order_displays,
)
from precision_jump.sequence import (
    MatchProgress,
    SequenceMatcher,
    should_cancel_active_gesture,
    should_suppress_activation_key,
)

FAILURES: list[str] = []


def check(condition: bool, message: str) -> None:
    if not condition:
        FAILURES.append(message)


def monitor(name, left, top, width, height, primary=False, number=0):
    bounds = Rectangle(left, top, width, height)
    return DisplayMonitor(name, bounds, bounds, primary, number)


PRIMARY = monitor("PRIMARY", 0, 0, 1920, 1080, primary=True, number=1)
MIDDLE = monitor("MIDDLE", 1920, 0, 1280, 1024, number=2)
FAR_RIGHT = monitor("FAR_RIGHT", 3200, 0, 1280, 1024, number=3)
ALL = [PRIMARY, MIDDLE, FAR_RIGHT]

TRAVEL = 26.0  # AppSettings.selection_distance default


# --------------------------------------------------------------- screen level

session = NineGridSession(Point(960, 540), ALL)
check(
    session.depth == 0
    and session.preview.is_screen_level
    and session.preview.display is PRIMARY,
    "Activation must start at the physical-screen level on the display under the cursor.",
)
check(session.origin == Point(960, 540), "The activation point must be preserved for cancellation.")

# At depth 0 a full display width is crossed in `travel` pixels of physical
# movement, so a small nudge right must not yet leave the primary display.
result = session.move_continuous(10, 0, TRAVEL, now=0.0)
check(
    result.preview.display is PRIMARY,
    "A sub-screen nudge must stay on the current display at the screen level.",
)

# At depth 0, `travel` pixels of physical movement equals one full normalised
# screen width, so a push of exactly that must hop once, to the neighbour in
# the +x octant.
session = NineGridSession(Point(960, 540), ALL)
session.move_continuous(TRAVEL, 0, TRAVEL, now=0.0)
check(
    session.display is MIDDLE,
    "Pushing past the right edge must hop to the nearest display in the right octant.",
)

# ... and continuing right must reach the far display, not skip back.
session.move_continuous(TRAVEL, 0, TRAVEL, now=0.0)
check(
    session.display is FAR_RIGHT,
    "Continued travel must chain to the next display in the same octant.",
)

# A single large push chains multiple hops in one event, rather than stopping
# at the first neighbour.
chained = NineGridSession(Point(960, 540), ALL)
chained.move_continuous(TRAVEL * 2, 0, TRAVEL, now=0.0)
check(
    chained.display is FAR_RIGHT,
    "One large delta must chain through the topology in a single step.",
)

# The rightmost display has no right neighbour, so the map must clamp.
session.move_continuous(400, 0, TRAVEL, now=0.0)
check(
    session.display is FAR_RIGHT
    and session.current_position.x == FAR_RIGHT.bounds.right - 1,
    "With no display in that direction the map must clamp to the edge.",
)


# ------------------------------------------------------------------- octants

check(_octant(20, 20) == (1, 1), "A diagonal vector must select a diagonal neighbour.")
check(_octant(30, 12) == (0, 1), "A vector below the equal-angle boundary must select right.")
check(_octant(30, 13) == (1, 1), "A vector above the equal-angle boundary must select down-right.")
check(_octant(-30, 0) == (0, -1), "A leftward vector must select the left neighbour.")
check(_octant(0, -30) == (-1, 0), "An upward vector must select the upper neighbour.")


# ---------------------------------------------------------------------- zoom

zoom_point = Point(1000, 500)
session = NineGridSession(zoom_point, [PRIMARY])
interaction = session.zoom_in(maximum_depth=8, actual_position=zoom_point, now=0.0)
check(
    interaction.jump_target is None
    and session.current_position == zoom_point
    and interaction.preview.actual_cursor == zoom_point,
    "Zoom must preserve the exact pointer position.",
)

# Scale eases from 1 to 3 across SCALE_ANIMATION_SECONDS.
check(session.is_scale_animating(now=0.0), "Zoom must start a scale animation.")
session.refresh_scale(now=SCALE_ANIMATION_SECONDS / 2)
check(
    1.0 < session.map_scale < 3.0,
    "Mid-animation scale must lie between the start and target scale.",
)
session.refresh_scale(now=SCALE_ANIMATION_SECONDS + 0.01)
check(abs(session.map_scale - 3.0) < 1e-9, "The scale must settle exactly on 3**depth.")
check(
    abs(session.step_x - 1920 / 3) < 0.01 and abs(session.step_y - 1080 / 3) < 0.01,
    "Level one must use one-third-screen steps.",
)
check(
    not session.is_scale_animating(now=SCALE_ANIMATION_SECONDS + 0.01),
    "The animation must stop once the target scale is reached.",
)

# Zoomed movement is fine-grained: the same physical delta now moves far less.
before = session.current_position
session.move_continuous(10, 0, TRAVEL, now=1.0)
travelled = session.current_position.x - before.x
check(
    0 < travelled < 1920,
    "Zoomed movement must stay within the display for a small delta.",
)
check(
    travelled == round(10 * 1920 / (TRAVEL * 3)),
    "Zoomed displacement must equal delta * width / (travel * scale).",
)

session.zoom_in(maximum_depth=8, actual_position=session.current_position, now=1.0)
session.refresh_scale(now=1.0 + SCALE_ANIMATION_SECONDS + 0.01)
check(
    session.depth == 2 and abs(session.map_scale - 9.0) < 1e-9,
    "Each zoom step must multiply the scale by three.",
)

position_before_zoom_out = session.current_position
session.zoom_out(actual_position=position_before_zoom_out, now=2.0)
check(
    session.depth == 1 and session.current_position == position_before_zoom_out,
    "Wheel-down must preserve the exact pointer position.",
)
session.zoom_out(actual_position=position_before_zoom_out, now=3.0)
session.refresh_scale(now=3.0 + SCALE_ANIMATION_SECONDS + 0.01)
check(
    session.depth == 0
    and session.preview.is_screen_level
    and session.current_position == position_before_zoom_out,
    "Wheel-down from level one must return to the screen layer in place.",
)
check(
    session.zoom_out(now=4.0).jump_target is None and session.depth == 0,
    "Zooming out from the screen layer must be a no-op.",
)

for index in range(20):
    session.zoom_in(maximum_depth=3, now=5.0 + index)
check(session.depth == 3, "Refinement must respect the configured maximum depth.")


# ------------------------------------------- crossing real layout boundaries

# Two displays with a vertical gap between them; a zoomed move aimed into the
# gap must land on the far display's entry edge rather than teleporting.
LEFT = monitor("LEFT", 0, 0, 1000, 1000, number=1)
RIGHT = monitor("RIGHT", 1500, 0, 1000, 1000, number=2)
session = NineGridSession(Point(900, 500), [LEFT, RIGHT])
session.zoom_in(maximum_depth=8, actual_position=Point(900, 500), now=0.0)
session.refresh_scale(now=SCALE_ANIMATION_SECONDS + 0.01)
session.move_continuous(TRAVEL * 3 * 0.8, 0, TRAVEL, now=1.0)
check(
    session.display is RIGHT and session.current_position.x >= RIGHT.bounds.left,
    "A zoomed move across a layout gap must enter the far display.",
)


# ------------------------------------------------------------- display order

unordered = [
    monitor("C", 3200, 0, 1280, 1024),
    monitor("A", 0, 0, 1920, 1080),
    monitor("B", 1920, 0, 1280, 1024),
]
ordered = order_displays(unordered)
check(
    [d.device_name for d in ordered] == ["A", "B", "C"]
    and [d.number for d in ordered] == [1, 2, 3],
    "Displays must be ordered by left edge and numbered from one.",
)


# ------------------------------------------------------------------ shortcuts

matcher = SequenceMatcher()
combined = ["Control_L", "k"]
check(
    matcher.consume("Control_L", combined, 0.5, now=1.0) == MatchProgress.PREFIX,
    "The first key of a combined sequence must be retained.",
)
check(
    matcher.consume("k", combined, 0.5, now=1.1) == MatchProgress.COMPLETE,
    "The final key must complete while the prefix remains held.",
)

matcher = SequenceMatcher()
matcher.consume("Control_L", combined, 0.5, now=1.0)
check(
    matcher.consume("k", combined, 0.5, now=2.0) == MatchProgress.NONE,
    "A sequence must not complete after the timeout expires.",
)

matcher = SequenceMatcher()
check(
    matcher.consume("z", combined, 0.5, now=1.0) == MatchProgress.NONE,
    "An unrelated key must not advance the sequence.",
)

matcher = SequenceMatcher()
check(
    matcher.consume("grave", ["grave"], 0.5, now=1.0) == MatchProgress.COMPLETE,
    "A single-key sequence must complete immediately.",
)

check(
    not should_suppress_activation_key("Alt_L"),
    "Alt must never be swallowed as an activation key.",
)
check(
    should_suppress_activation_key("grave"),
    "An ordinary activation key must be swallowed.",
)
check(
    should_cancel_active_gesture("Alt_L", "Tab", True),
    "Tab must end an Alt-activated gesture so Alt+Tab reaches the window manager.",
)
check(
    not should_cancel_active_gesture("grave", "grave", True),
    "Repeating the activation key must not cancel the gesture.",
)


# ---------------------------------------------------------------------- exit

NAMES = [
    "CONTINUOUS_SCREEN_TOPOLOGY",
    "EQUAL_ANGLE_OCTANT_SELECTION",
    "ZOOM_PRESERVES_POINTER",
    "EASED_SCALE_ANIMATION",
    "MAXIMUM_REFINEMENT_DEPTH",
    "ACTUAL_LAYOUT_CROSSING",
    "DISPLAY_ORDERING",
    "COMBINED_KEY_SEQUENCE",
]

if FAILURES:
    for failure in FAILURES:
        print(f"FAILED: {failure}")
    sys.exit(1)

for name in NAMES:
    print(f"{name}=PASSED")
print("PRECISION_JUMP_LOGIC_TESTS_PASSED")
