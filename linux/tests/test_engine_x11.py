"""End-to-end engine test driven by synthetic XTest input.

Requires a running X server. It briefly grabs the pointer and moves the real
cursor, then restores it. Every grab is released on exit -- and the X server
drops all grabs automatically if this process dies, so a crash cannot leave the
desktop stuck.

Run with:  python3 tests/test_engine_x11.py
"""

from __future__ import annotations

import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from Xlib import X, display as xdisplay
from Xlib.ext import xtest

from precision_jump.engine import GestureEngine
from precision_jump.keys import keycodes_for_name
from precision_jump.settings import AppSettings

FAILURES: list[str] = []
EVENTS: list[str] = []


def check(condition: bool, message: str) -> None:
    if not condition:
        FAILURES.append(message)


driver = xdisplay.Display()
root = driver.screen().root
restore = root.query_pointer()
ORIGIN_X, ORIGIN_Y = restore.root_x, restore.root_y


def fake_key(name: str, press: bool) -> None:
    keycode = keycodes_for_name(driver, name)[0]
    xtest.fake_input(driver, X.KeyPress if press else X.KeyRelease, keycode)
    driver.sync()


def fake_move(dx: int, dy: int) -> None:
    xtest.fake_input(driver, X.MotionNotify, detail=1, x=dx, y=dy)
    driver.sync()


def fake_button(button: int) -> None:
    xtest.fake_input(driver, X.ButtonPress, button)
    xtest.fake_input(driver, X.ButtonRelease, button)
    driver.sync()


def settle(seconds: float = 0.25) -> None:
    time.sleep(seconds)


settings = AppSettings()
settings.normalize_values()

previews: list = []
states: list[bool] = []
completions: list[str] = []

engine = GestureEngine(
    settings,
    on_preview=lambda p: previews.append(p),
    on_state_changed=lambda active: states.append(active),
    on_completion=lambda message, tone: completions.append(message),
)

print(f"displays: {[(d.number, d.device_name) for d in engine.displays]}")
check(len(engine.displays) >= 1, "The engine must enumerate at least one display.")

engine.start()
settle(0.3)

try:
    # Park the pointer somewhere predictable on display 1.
    first = engine.displays[0]
    start_x = first.bounds.left + first.bounds.width // 2
    start_y = first.bounds.top + first.bounds.height // 2
    root.warp_pointer(start_x, start_y)
    driver.sync()
    settle()

    # ---------------------------------------------------------- activation
    fake_key("grave", True)
    settle()
    check(engine.jump_active, "Holding the activation key must begin a gesture.")
    check(states and states[0] is True, "Beginning a gesture must report an active state.")
    check(
        previews and previews[-1] is not None and previews[-1].depth == 0,
        "A new gesture must start at the screen level.",
    )

    # ------------------------------------------------------------ movement
    # At depth 0 this is more than a full screen width of map travel.
    before = engine._session.current_position
    fake_move(30, 0)
    settle()
    after = engine._session.current_position
    check(after != before, "Physical motion must move the mapped pointer.")
    moved_display = engine._session.display.number
    print(f"screen-level move: {before} -> {after} (display {moved_display})")

    # ---------------------------------------------------------------- zoom
    fake_button(4)  # scroll up = zoom in
    settle()
    check(engine._session.depth == 1, "Scroll up must zoom in one level.")
    position_at_zoom = engine._session.current_position

    zoom_display_width = engine._session.display.bounds.width
    fake_move(10, 0)
    settle()
    fine = engine._session.current_position
    travelled = abs(fine.x - position_at_zoom.x)
    # At depth 1 the scale has eased to 3, so displacement is
    # delta * width / (travel * scale) -- a third of the depth-0 distance.
    expected = round(10 * zoom_display_width / (settings.selection_distance * 3))
    check(
        travelled == expected,
        f"Zoomed displacement must be delta*width/(travel*scale) "
        f"(expected {expected}, got {travelled}).",
    )
    check(
        travelled < zoom_display_width,
        "Zoomed movement must stay within one display rather than hopping.",
    )
    print(f"zoomed move: {position_at_zoom} -> {fine} ({travelled}px, expected {expected})")

    fake_button(5)  # scroll down = zoom out
    settle()
    check(engine._session.depth == 0, "Scroll down must zoom back out.")

    # -------------------------------------------------------- keep on release
    destination = engine._session.current_position
    fake_key("grave", False)
    settle()
    check(not engine.jump_active, "Releasing the activation key must end the gesture.")
    check(
        completions and completions[-1].startswith("KEEP"),
        "Releasing the key must report a KEEP completion.",
    )
    settle()
    final = root.query_pointer()
    check(
        (final.root_x, final.root_y) == (destination.x, destination.y),
        f"The pointer must stay where the gesture left it "
        f"(expected {destination}, got {final.root_x},{final.root_y}).",
    )
    print(f"kept pointer at {final.root_x},{final.root_y}")

    # ------------------------------------------------ right-click cancellation
    root.warp_pointer(start_x, start_y)
    driver.sync()
    settle()
    fake_key("grave", True)
    settle()
    check(engine.jump_active, "A second gesture must begin normally.")
    cancel_origin = engine._session.origin
    fake_move(30, 0)
    settle()
    fake_button(3)  # right click cancels
    settle()
    check(not engine.jump_active, "Right-click must cancel the gesture.")
    check(
        completions and completions[-1] == "CANCELLED",
        "Cancelling must report a CANCELLED completion.",
    )
    restored = root.query_pointer()
    check(
        (restored.root_x, restored.root_y) == (cancel_origin.x, cancel_origin.y),
        f"Cancelling must restore the activation position "
        f"(expected {cancel_origin}, got {restored.root_x},{restored.root_y}).",
    )
    print(f"cancel restored pointer to {restored.root_x},{restored.root_y}")
    fake_key("grave", False)
    settle()

    # --------------------------------------------- key passes through when idle
    check(not engine.jump_active, "No gesture may remain active after cancellation.")

finally:
    engine.stop()
    try:
        root.warp_pointer(ORIGIN_X, ORIGIN_Y)
        driver.sync()
    except Exception:
        pass

if FAILURES:
    for failure in FAILURES:
        print(f"FAILED: {failure}")
    sys.exit(1)

print("ENGINE_ACTIVATION=PASSED")
print("ENGINE_SCREEN_LEVEL_MOVE=PASSED")
print("ENGINE_WHEEL_ZOOM=PASSED")
print("ENGINE_KEEP_ON_RELEASE=PASSED")
print("ENGINE_RIGHT_CLICK_CANCEL=PASSED")
print("PRECISION_JUMP_ENGINE_TESTS_PASSED")
