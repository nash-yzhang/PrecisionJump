"""Replacement for NineGridSession.GetDisplays() / System.Windows.Forms.Screen.

Enumerates the connected outputs through XRandR and returns them in the same
order the Windows build uses: sorted by left edge, then top edge, numbered
from 1.
"""

from __future__ import annotations

from typing import List

from Xlib import X
from Xlib.ext import randr

from .geometry import DisplayMonitor, Rectangle
from .screen_targeting import order_displays


def _from_randr_monitors(display, root) -> List[DisplayMonitor]:
    """RandR >= 1.5 exposes logical monitors directly, which is what we want:
    it already accounts for mirroring and per-monitor panning."""
    monitors = randr.get_monitors(root, True).monitors
    result = []
    for monitor in monitors:
        name = display.get_atom_name(monitor.name)
        bounds = Rectangle(monitor.x, monitor.y, monitor.width_in_pixels,
                           monitor.height_in_pixels)
        result.append(
            DisplayMonitor(name, bounds, bounds, bool(monitor.primary), 0)
        )
    return result


def _from_randr_crtcs(display, root) -> List[DisplayMonitor]:
    """Fallback for servers without RandR 1.5 monitor objects."""
    resources = randr.get_screen_resources_current(root)
    try:
        primary_output = randr.get_output_primary(root).output
    except Exception:
        primary_output = 0

    result = []
    for output in resources.outputs:
        info = randr.get_output_info(display, output, resources.config_timestamp)
        if info.crtc == 0 or info.connection != 0:
            continue
        crtc = randr.get_crtc_info(display, info.crtc, resources.config_timestamp)
        bounds = Rectangle(crtc.x, crtc.y, crtc.width, crtc.height)
        name = info.name if isinstance(info.name, str) else bytes(info.name).decode()
        result.append(
            DisplayMonitor(name, bounds, bounds, output == primary_output, 0)
        )
    return result


def get_displays(display, root=None) -> List[DisplayMonitor]:
    root = root or display.screen().root
    monitors: List[DisplayMonitor] = []
    try:
        monitors = _from_randr_monitors(display, root)
    except Exception:
        monitors = []

    if not monitors:
        try:
            monitors = _from_randr_crtcs(display, root)
        except Exception:
            monitors = []

    if not monitors:
        # Last resort: treat the whole X screen as a single display.
        screen = display.screen()
        bounds = Rectangle(0, 0, screen.width_in_pixels, screen.height_in_pixels)
        monitors = [DisplayMonitor("SCREEN", bounds, bounds, True, 0)]

    return order_displays(monitors)


def virtual_bounds(displays) -> Rectangle:
    left = min(d.bounds.left for d in displays)
    top = min(d.bounds.top for d in displays)
    right = max(d.bounds.right for d in displays)
    bottom = max(d.bounds.bottom for d in displays)
    return Rectangle(left, top, right - left, bottom - top)
