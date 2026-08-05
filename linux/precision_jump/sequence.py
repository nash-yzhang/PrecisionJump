"""Port of Services/SequenceMatcher.cs, Services/ShortcutPolicy.cs and
Models/KeyToken.cs.

Windows virtual-key codes are replaced by X keysym *names* ("grave", "F8",
"Shift_L", ...), which are the natural identity for a key on X11. Left/right
modifier pairs are normalised to a single token exactly as KeyNames.Normalize
collapses VK_LSHIFT/VK_RSHIFT to VK_SHIFT.
"""

from __future__ import annotations

import enum
import time
from typing import Optional, Sequence

BACKTICK = "grave"

_MODIFIER_NORMALISATION = {
    "Shift_R": "Shift_L",
    "Control_R": "Control_L",
    "Alt_R": "Alt_L",
    "ISO_Level3_Shift": "Alt_L",
    "Meta_R": "Alt_L",
    "Super_R": "Super_L",
}

# Modifiers must keep reaching other applications even when they activate the
# gesture, mirroring ShortcutPolicy's Shift/Control/Alt/LeftWindows exclusion.
_TRANSPARENT_MODIFIERS = frozenset({"Shift_L", "Control_L", "Alt_L", "Super_L"})

_DISPLAY_NAMES = {
    "grave": "`",
    "Shift_L": "Shift",
    "Control_L": "Ctrl",
    "Alt_L": "Alt",
    "Super_L": "Super",
    "Escape": "Esc",
    "space": "Space",
    "Tab": "Tab",
    "Return": "Enter",
    "BackSpace": "Backspace",
    "Left": "←",
    "Up": "↑",
    "Right": "→",
    "Down": "↓",
    "Caps_Lock": "CapsLock",
}


def normalize(keysym_name: str) -> str:
    return _MODIFIER_NORMALISATION.get(keysym_name, keysym_name)


def display_name(keysym_name: str) -> str:
    keysym_name = normalize(keysym_name)
    if keysym_name in _DISPLAY_NAMES:
        return _DISPLAY_NAMES[keysym_name]
    if len(keysym_name) == 1:
        return keysym_name.upper()
    return keysym_name


def sequence_display(sequence: Sequence[str]) -> str:
    return "  →  ".join(display_name(token) for token in sequence)


class MatchProgress(enum.Enum):
    NONE = 0
    PREFIX = 1
    COMPLETE = 2


class SequenceMatcher:
    """Faithful port: advances through an expected key list, restarting when a
    keystroke matches the first element and resetting on timeout or mismatch."""

    def __init__(self) -> None:
        self._position = 0
        self._last_key_at = 0.0

    def consume(
        self,
        key: str,
        expected: Sequence[str],
        timeout_seconds: float,
        now: Optional[float] = None,
    ) -> MatchProgress:
        if not expected:
            return MatchProgress.NONE

        if self._position >= len(expected):
            self.reset()

        now = time.perf_counter() if now is None else now
        elapsed = now - self._last_key_at
        if self._position > 0 and elapsed > timeout_seconds:
            self._position = 0

        if key == expected[self._position]:
            self._position += 1
            self._last_key_at = now
        elif key == expected[0]:
            self._position = 1
            self._last_key_at = now
        else:
            self.reset()
            return MatchProgress.NONE

        if self._position != len(expected):
            return MatchProgress.PREFIX

        self.reset()
        return MatchProgress.COMPLETE

    def reset(self) -> None:
        self._position = 0
        self._last_key_at = 0.0


def should_suppress_activation_key(key: str) -> bool:
    """Modifiers stay visible to other apps; ordinary keys are swallowed."""
    return normalize(key) not in _TRANSPARENT_MODIFIERS


def should_cancel_active_gesture(
    final_key: Optional[str], pressed_key: str, first_key_down: bool
) -> bool:
    return first_key_down and pressed_key != final_key
