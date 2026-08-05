"""Port of Services/GlobalInputEngine.cs to X11.

Windows mechanism                     -> X11 mechanism
------------------------------------------------------------------------
SetWindowsHookEx(WH_KEYBOARD_LL)      -> passive XGrabKey in GrabModeSync on
                                         each key of the activation sequence.
                                         AllowEvents(AsyncKeyboard) swallows a
                                         key, ReplayKeyboard passes it through
                                         -- the direct analogue of the hook
                                         returning 1 vs CallNextHookEx.
SetWindowsHookEx(WH_MOUSE_LL)         -> XGrabPointer while the gesture is
                                         active, so motion, scroll and clicks
                                         reach us only and never the app below.
SendInput(MOUSEEVENTF_ABSOLUTE)       -> XWarpPointer.
"injected" hook flag                  -> not needed: the delta is measured
                                         against the session's own position,
                                         so a warp echo yields a zero delta and
                                         is ignored, exactly as upstream does.

The engine owns its own X connection and runs its event loop on a dedicated
thread; previews are handed to the UI through a callback.
"""

from __future__ import annotations

import os
import select
import threading
import time
from collections import deque
from typing import Callable, List, Optional

from Xlib import X, display as xdisplay
from Xlib.error import XError

from .displays import get_displays
from .geometry import Point
from .keys import keycodes_for_name, name_for_keycode
from .recorder import MouseEventRecorder
from .screen_targeting import NineGridPreview, NineGridSession
from .sequence import (
    MatchProgress,
    SequenceMatcher,
    normalize,
    should_cancel_active_gesture,
    should_suppress_activation_key,
)
from .settings import AppSettings

SCROLL_UP = 4
SCROLL_DOWN = 5
RIGHT_BUTTON = 3
ANIMATION_TICK_SECONDS = 0.016

_BUTTON_EVENT_NAMES = {
    1: ("left_down", "left_up"),
    2: ("middle_down", "middle_up"),
    3: ("right_down", "right_up"),
    4: ("wheel_up", "wheel_up_release"),
    5: ("wheel_down", "wheel_down_release"),
    6: ("wheel_left", "wheel_left_release"),
    7: ("wheel_right", "wheel_right_release"),
    8: ("x1_down", "x1_up"),
    9: ("x2_down", "x2_up"),
}


def _log(message: str) -> None:
    path = os.environ.get("PRECISION_JUMP_DIAGNOSTIC_LOG")
    if not path:
        return
    try:
        with open(path, "a") as handle:
            handle.write(f"{time.time():.6f} {message}\n")
    except OSError:
        pass  # Diagnostics must never interfere with input.


class GestureEngine:
    def __init__(
        self,
        settings: AppSettings,
        on_preview: Optional[Callable[[Optional[NineGridPreview]], None]] = None,
        on_state_changed: Optional[Callable[[bool], None]] = None,
        on_completion: Optional[Callable[[str, bool], None]] = None,
    ) -> None:
        self._settings = settings
        self._on_preview = on_preview
        self._on_state_changed = on_state_changed
        self._on_completion = on_completion

        self._display = xdisplay.Display()
        self._root = self._display.screen().root
        self.displays = get_displays(self._display, self._root)

        self._matcher = SequenceMatcher()
        self._session: Optional[NineGridSession] = None
        self._release_key: Optional[str] = None
        self._grabbed_keycodes: List[int] = []
        self._pointer_grabbed = False
        self._held_keys: set[str] = set()
        self._pushed_back: deque = deque()

        self._recorder = MouseEventRecorder()
        self._thread: Optional[threading.Thread] = None
        self._running = False
        self._wake_r, self._wake_w = os.pipe()
        # Must be non-blocking: the loop drains it unconditionally after every
        # select(), and a blocking read would stall the whole event loop.
        os.set_blocking(self._wake_r, False)

        # Keycode -> configured token. Resolving the activation keys this way
        # keeps the binding correct on layouts where the key carries a
        # different level-0 keysym (e.g. keycode 49 is "masculine", not
        # "grave", on a Spanish layout).
        self._token_by_keycode: dict[int, str] = {}

    # ------------------------------------------------------------ lifecycle

    @property
    def jump_active(self) -> bool:
        return self._session is not None

    def start(self) -> None:
        if self._thread is not None:
            return
        if self._settings.record_mouse_events:
            path = self._recorder.start()
            _log(f"RECORDING to {path}")
        self._install_key_grabs()
        self._running = True
        self._thread = threading.Thread(
            target=self._run, name="precision-jump-input", daemon=True
        )
        self._thread.start()
        _log("ENGINE STARTED")

    def stop(self) -> None:
        if not self._running:
            return
        self._running = False
        try:
            os.write(self._wake_w, b"x")
        except OSError:
            pass
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
        self._recorder.stop()
        _log("ENGINE STOPPED")

    def refresh_displays(self) -> None:
        self.displays = get_displays(self._display, self._root)

    # ----------------------------------------------------------- key grabs

    def _install_key_grabs(self) -> None:
        self._remove_key_grabs()
        self._token_by_keycode = {}
        for token in self._settings.screen_jump_sequence:
            for keycode in keycodes_for_name(self._display, token):
                self._token_by_keycode[keycode] = token
                try:
                    self._root.grab_key(
                        keycode,
                        X.AnyModifier,
                        True,               # owner_events
                        X.GrabModeAsync,    # pointer
                        X.GrabModeSync,     # keyboard: lets us replay or eat
                    )
                    self._grabbed_keycodes.append(keycode)
                except XError as error:
                    _log(f"GRAB FAILED keycode={keycode} {error}")
        self._display.sync()
        _log(f"GRABBED keycodes={self._grabbed_keycodes}")

    def _remove_key_grabs(self) -> None:
        for keycode in self._grabbed_keycodes:
            try:
                self._root.ungrab_key(keycode, X.AnyModifier)
            except XError:
                pass
        self._grabbed_keycodes = []
        self._display.sync()

    def rebind(self) -> None:
        """Re-install grabs after the activation sequence changed."""
        self._cancel_gesture(restore_origin=True, show_feedback=False)
        self._matcher.reset()
        self._install_key_grabs()

    # ----------------------------------------------------------- event loop

    def _run(self) -> None:
        fd = self._display.fileno()
        while self._running:
            animating = (
                self._session is not None
                and self._session.is_scale_animating()
            )
            timeout = ANIMATION_TICK_SECONDS if animating else 0.25
            try:
                ready, _, _ = select.select([fd, self._wake_r], [], [], timeout)
            except (OSError, ValueError):
                break

            if self._wake_r in ready:
                try:
                    os.read(self._wake_r, 64)
                except (BlockingIOError, OSError):
                    pass

            try:
                self._pump_events()
            except Exception as error:  # input must always fail open
                _log(f"LOOP ERROR {error!r}")

            if self._session is not None:
                preview = self._session.refresh_scale()
                if self._session.is_scale_animating():
                    self._emit_preview(preview)

        self._teardown()

    def _next_event(self):
        """Events pushed back by the auto-repeat peek are served first."""
        if self._pushed_back:
            return self._pushed_back.popleft()
        if self._display.pending_events():
            return self._display.next_event()
        return None

    def _pump_events(self) -> None:
        while True:
            event = self._next_event()
            if event is None:
                return
            kind = event.type
            if kind == X.KeyPress:
                self._on_key_press(event)
            elif kind == X.KeyRelease:
                self._on_key_release(event)
            elif kind == X.MotionNotify:
                self._on_motion(event)
            elif kind == X.ButtonPress:
                self._on_button_press(event)
            elif kind == X.ButtonRelease:
                self._on_button_release(event)

    def _teardown(self) -> None:
        self._cancel_gesture(restore_origin=True, show_feedback=False)
        self._remove_key_grabs()
        try:
            self._display.close()
        except Exception:
            pass

    # -------------------------------------------------------------- keyboard

    def _allow(self, replay: bool) -> None:
        """ReplayKeyboard passes the key on to the focused client;
        AsyncKeyboard keeps it for us alone (i.e. swallows it)."""
        self._display.allow_events(
            X.ReplayKeyboard if replay else X.AsyncKeyboard, X.CurrentTime
        )
        self._display.flush()

    def _resolve_key(self, keycode: int) -> Optional[str]:
        """A grabbed keycode always reports the token it was bound to; other
        keys fall back to their keysym name."""
        token = self._token_by_keycode.get(keycode)
        if token is not None:
            return token
        name = name_for_keycode(self._display, keycode)
        return normalize(name) if name else None

    def _on_key_press(self, event) -> None:
        name = self._resolve_key(event.detail)
        if name is None:
            self._allow(replay=True)
            return
        first_key_down = name not in self._held_keys
        self._held_keys.add(name)
        _log(f"KEY DOWN {name} first={first_key_down}")

        # An already-active gesture: any other key ends it (so Alt+Tab still
        # reaches the window manager when Alt is the activation key).
        if self._session is not None:
            if should_cancel_active_gesture(self._release_key, name, first_key_down):
                _log(f"END FOR KEY {name}")
                self._cancel_gesture(restore_origin=False, show_feedback=False)
                self._matcher.reset()
                self._allow(replay=True)
                return
            self._allow(replay=False)
            return

        if not first_key_down or not self._settings.enabled:
            self._allow(replay=True)
            return

        progress = self._matcher.consume(
            name,
            self._settings.screen_jump_sequence,
            self._settings.sequence_timeout_seconds,
        )
        if progress is MatchProgress.NONE:
            self._allow(replay=True)
            return
        if progress is MatchProgress.PREFIX:
            # Hold the prefix key back only if it is not a modifier.
            self._allow(replay=not should_suppress_activation_key(name))
            return

        _log(f"SEQUENCE MATCH final={name}")
        self._begin_gesture(name)
        self._allow(replay=not should_suppress_activation_key(name))

    def _on_key_release(self, event) -> None:
        name = self._resolve_key(event.detail)
        if name is None:
            return

        if self._is_autorepeat(event):
            _log(f"AUTOREPEAT {name} ignored")
            return

        self._held_keys.discard(name)
        _log(f"KEY UP {name}")
        if self._session is not None and name == self._release_key:
            self._confirm_gesture()

    def _is_autorepeat(self, release_event) -> bool:
        """X sends KeyRelease+KeyPress pairs with identical timestamps while a
        key auto-repeats; without this a held activation key would confirm the
        gesture immediately."""
        peeked = self._next_event()
        if peeked is None:
            return False
        if (
            peeked.type == X.KeyPress
            and peeked.detail == release_event.detail
            and peeked.time == release_event.time
        ):
            # The swallowed repeat still came through the sync grab; releasing
            # it keeps the keyboard from staying frozen.
            self._allow(replay=False)
            return True
        # Not a repeat: hand it back to the pump rather than recursing.
        self._pushed_back.appendleft(peeked)
        return False

    # --------------------------------------------------------------- pointer

    def _on_motion(self, event) -> None:
        if self._session is None:
            return

        current = self._session.current_position
        raw_dx = event.root_x - current.x
        raw_dy = event.root_y - current.y
        if raw_dx == 0 and raw_dy == 0:
            return  # our own warp echoing back

        interaction = self._session.move_continuous(
            raw_dx, raw_dy, self._settings.selection_distance
        )
        target = interaction.jump_target
        if target is not None:
            self._warp(target)
            self._record(target, "move", True, raw_dx, raw_dy)
        self._emit_preview(interaction.preview)

    def _on_button_press(self, event) -> None:
        if self._session is None:
            return

        button = event.detail
        self._record(
            Point(event.root_x, event.root_y),
            _BUTTON_EVENT_NAMES.get(button, (f"button{button}_down", ""))[0],
            True,
        )

        if button == SCROLL_UP:
            self._adjust_depth(1, Point(event.root_x, event.root_y))
        elif button == SCROLL_DOWN:
            self._adjust_depth(-1, Point(event.root_x, event.root_y))
        elif button == RIGHT_BUTTON:
            _log("RIGHT CLICK CANCEL")
            self._cancel_gesture(restore_origin=True, show_feedback=True)
        # Every other button is swallowed while the gesture is active.

    def _on_button_release(self, event) -> None:
        if self._session is None:
            return
        self._record(
            Point(event.root_x, event.root_y),
            _BUTTON_EVENT_NAMES.get(event.detail, ("", f"button{event.detail}_up"))[1],
            True,
        )

    def _adjust_depth(self, direction: int, actual_position: Point) -> None:
        if self._session is None or direction == 0:
            return
        if direction > 0:
            interaction = self._session.zoom_in(
                self._settings.maximum_zoom_level, actual_position
            )
        else:
            interaction = self._session.zoom_out(actual_position)
        preview = interaction.preview
        _log(
            f"WHEEL {direction} depth={preview.depth} "
            f"scale={preview.map_scale:.2f} at {preview.actual_cursor}"
        )
        self._emit_preview(preview)

    def _warp(self, target: Point) -> None:
        self._root.warp_pointer(target.x, target.y)
        self._display.flush()

    def _pointer_position(self) -> Point:
        pointer = self._root.query_pointer()
        return Point(pointer.root_x, pointer.root_y)

    # ---------------------------------------------------------------- gesture

    def _begin_gesture(self, final_key: str) -> None:
        self.refresh_displays()
        if not self.displays:
            return

        origin = self._pointer_position()
        self._session = NineGridSession(origin, self.displays)
        self._release_key = final_key

        result = self._root.grab_pointer(
            False,  # owner_events: send everything to us, nothing to apps
            X.PointerMotionMask | X.ButtonPressMask | X.ButtonReleaseMask,
            X.GrabModeAsync,
            X.GrabModeAsync,
            X.NONE,
            X.NONE,
            X.CurrentTime,
        )
        self._pointer_grabbed = result == X.GrabSuccess
        if not self._pointer_grabbed:
            _log(f"POINTER GRAB FAILED result={result}")

        _log(
            f"BEGIN origin={origin} release={final_key} "
            f"displays={len(self.displays)} grab={self._pointer_grabbed}"
        )
        self._emit_preview(self._session.preview)
        self._notify_state(True)

    def _confirm_gesture(self) -> None:
        if self._session is None:
            return
        preview = self._session.preview
        destination = self._session.current_position
        _log(
            f"KEEP screen={preview.display.number} depth={preview.depth} "
            f"destination={destination}"
        )
        self._end_gesture()
        self._show_completion(f"KEEP  ·  LEVEL {preview.depth}", True)

    def _cancel_gesture(self, restore_origin: bool, show_feedback: bool) -> None:
        if self._session is None:
            return
        origin = self._session.origin
        _log(f"CANCEL restore={restore_origin} origin={origin}")
        self._end_gesture()
        if restore_origin:
            self._warp(origin)
        if show_feedback:
            self._show_completion("CANCELLED", False)

    def _end_gesture(self) -> None:
        self._session = None
        self._release_key = None
        if self._pointer_grabbed:
            try:
                self._display.ungrab_pointer(X.CurrentTime)
                self._display.flush()
            except XError:
                pass
            self._pointer_grabbed = False
        self._emit_preview(None)
        self._notify_state(False)

    # ------------------------------------------------------------ callbacks

    def _emit_preview(self, preview: Optional[NineGridPreview]) -> None:
        if self._on_preview is not None:
            self._on_preview(preview)

    def _notify_state(self, active: bool) -> None:
        if self._on_state_changed is not None:
            self._on_state_changed(active)

    def _show_completion(self, message: str, active_tone: bool) -> None:
        if self._settings.show_status_hud and self._on_completion is not None:
            self._on_completion(message, active_tone)

    def _record(
        self, position: Point, event_name: str, jumping: bool, raw_dx=0, raw_dy=0
    ) -> None:
        if self._settings.record_mouse_events and event_name:
            self._recorder.record(position, event_name, jumping, raw_dx, raw_dy)
