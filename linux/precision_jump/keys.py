"""Keysym-name <-> keycode helpers.

X11 keysym names ("grave", "F8", "Shift_L") replace Windows virtual-key codes
as the identity of a key throughout the port.
"""

from __future__ import annotations

from typing import Dict, List, Optional

from Xlib import XK

_NAME_BY_KEYSYM: Dict[int, str] = {}


def _build_reverse_map() -> None:
    if _NAME_BY_KEYSYM:
        return
    for attribute in dir(XK):
        if not attribute.startswith("XK_"):
            continue
        value = getattr(XK, attribute)
        if isinstance(value, int):
            # First definition wins, so canonical names beat aliases.
            _NAME_BY_KEYSYM.setdefault(value, attribute[3:])


def keysym_name(keysym: int) -> Optional[str]:
    _build_reverse_map()
    return _NAME_BY_KEYSYM.get(keysym)


def name_to_keysym(name: str) -> Optional[int]:
    keysym = XK.string_to_keysym(name)
    return keysym or None


def keycodes_for_name(display, name: str) -> List[int]:
    """Every keycode that produces this keysym (handles duplicated keys)."""
    keysym = name_to_keysym(name)
    if not keysym:
        return []
    codes = [
        code
        for code, _index in display.keysym_to_keycodes(keysym)
    ] if hasattr(display, "keysym_to_keycodes") else []
    if not codes:
        code = display.keysym_to_keycode(keysym)
        codes = [code] if code else []
    # Deduplicate, preserving order.
    seen = set()
    return [c for c in codes if not (c in seen or seen.add(c))]


def name_for_keycode(display, keycode: int, state: int = 0) -> Optional[str]:
    """Resolve a keycode to its unshifted keysym name."""
    for index in (0, 1):
        keysym = display.keycode_to_keysym(keycode, index)
        if keysym:
            name = keysym_name(keysym)
            if name:
                return name
    return None
