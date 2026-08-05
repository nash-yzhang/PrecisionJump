"""Port of Views/OverlayWindow.xaml(.cs), Views/ModeHudWindow and
Services/ScreenOverlayMap.cs to GTK3 + cairo.

One click-through, always-on-top, ARGB window per display. WPF's
AllowsTransparency + WS_EX_TRANSPARENT becomes an RGBA visual plus an empty
input shape, so the overlay never takes a click or the focus.

The visual model is unchanged from the WPF original:
  * depth 0  -- a rounded border around each display, dimmed except the one
                the map is currently on, labelled "SCREEN n";
  * depth >0 -- a floating grid of cells (display size / map scale) with the
                cursor's cell accented and neighbours fading with distance.
"""

from __future__ import annotations

import math

import cairo
from typing import Dict, List, Optional

import gi

gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402

from .geometry import DisplayMonitor  # noqa: E402
from .screen_targeting import NineGridPreview  # noqa: E402

VISIBLE_RING_COUNT = 4

# Colours lifted from OverlayWindow.xaml (#AARRGGBB).
ACCENT = (0x17 / 255, 0xB6 / 255, 0xA4 / 255)
SCRIM_ALPHA = 0x18 / 255          # Grid Background="#18000000"
ACCENT_FILL_ALPHA = 0x52 / 255    # _accentBrush
SOFT_ACCENT_ALPHA = 0x15 / 255    # _softAccentBrush
SCREEN_FILL_ALPHA = 0x28 / 255    # ScreenBorder Background="#2817B6A4"
UNSELECTED_OPACITY = 0.24


def _cell_opacity(normalized_distance: float, is_current: bool) -> float:
    """CellOpacity() from OverlayWindow.xaml.cs."""
    if is_current:
        return 1.0
    return min(
        0.82,
        max(0.06, 0.92 * math.exp(-0.58 * max(0.0, normalized_distance - 0.5))),
    )


def _rounded_rect(cr, x, y, width, height, radius):
    radius = max(0.0, min(radius, min(width, height) / 2))
    cr.new_sub_path()
    cr.arc(x + width - radius, y + radius, radius, -math.pi / 2, 0)
    cr.arc(x + width - radius, y + height - radius, radius, 0, math.pi / 2)
    cr.arc(x + radius, y + height - radius, radius, math.pi / 2, math.pi)
    cr.arc(x + radius, y + radius, radius, math.pi, 3 * math.pi / 2)
    cr.close_path()


class _OverlayWindow(Gtk.Window):
    def __init__(self, display: DisplayMonitor) -> None:
        super().__init__(type=Gtk.WindowType.POPUP)
        self.display_monitor = display
        self.preview: Optional[NineGridPreview] = None
        self.is_selected = True

        self.set_app_paintable(True)
        self.set_decorated(False)
        self.set_keep_above(True)
        self.set_accept_focus(False)
        self.set_focus_on_map(False)
        self.set_skip_taskbar_hint(True)
        self.set_skip_pager_hint(True)
        self.set_type_hint(Gdk.WindowTypeHint.NOTIFICATION)

        screen = self.get_screen()
        visual = screen.get_rgba_visual()
        if visual is not None:
            self.set_visual(visual)

        self.connect("draw", self._on_draw)
        self.connect("realize", self._on_realize)
        self.premap(display)

    def _on_realize(self, _widget) -> None:
        # Empty input region: the overlay is visually present but invisible to
        # the pointer, matching WS_EX_TRANSPARENT.
        window = self.get_window()
        if window is not None:
            window.set_pass_through(True)

    def premap(self, display: DisplayMonitor) -> None:
        self.display_monitor = display
        bounds = display.bounds
        self.move(bounds.left, bounds.top)
        self.resize(bounds.width, bounds.height)
        self.set_size_request(bounds.width, bounds.height)

    def show_preview(self, preview: NineGridPreview, is_selected: bool) -> None:
        self.preview = preview
        self.is_selected = is_selected
        if not self.get_visible():
            self.show_all()
        self.premap(self.display_monitor)
        self.queue_draw()

    # ---------------------------------------------------------------- drawing

    def _on_draw(self, _widget, cr) -> bool:
        preview = self.preview
        width = self.display_monitor.bounds.width
        height = self.display_monitor.bounds.height

        # SOURCE (not OVER) so the scrim replaces the surface outright and the
        # window stays genuinely transparent rather than accumulating alpha.
        cr.set_operator(cairo.Operator.SOURCE)
        cr.set_source_rgba(0, 0, 0, 0 if preview is None else SCRIM_ALPHA)
        cr.paint()
        cr.set_operator(cairo.Operator.OVER)

        if preview is None:
            return False

        if preview.is_screen_level:
            self._draw_screen_level(cr, preview, width, height)
        else:
            self._draw_floating_grid(cr, preview, width, height)
        return False

    def _draw_screen_level(self, cr, preview, width, height) -> None:
        alpha = 1.0 if self.is_selected else UNSELECTED_OPACITY
        _rounded_rect(cr, 3, 3, width - 6, height - 6, 12)
        cr.set_source_rgba(*ACCENT, SCREEN_FILL_ALPHA * alpha)
        cr.fill_preserve()
        cr.set_source_rgba(1, 1, 1, alpha)
        cr.set_line_width(4)
        cr.stroke()

        label = f"SCREEN {self.display_monitor.number}"
        cr.select_font_face("Cantarell")
        cr.set_font_size(18)
        extents = cr.text_extents(label)
        cr.move_to(width / 2 - extents.width / 2, height / 2 + extents.height / 2)
        cr.set_source_rgba(1, 1, 1, alpha)
        cr.show_text(label)

    def _draw_floating_grid(self, cr, preview, width, height) -> None:
        bounds = self.display_monitor.bounds
        cursor_x = preview.actual_cursor.x - bounds.left
        cursor_y = preview.actual_cursor.y - bounds.top
        map_scale = max(preview.map_scale, 1.0)
        grid_size = max(int(math.ceil(map_scale)), 1)
        cell_width = width / map_scale
        cell_height = height / map_scale
        current_column = min(max(int(cursor_x // cell_width), 0), grid_size - 1)
        current_row = min(max(int(cursor_y // cell_height), 0), grid_size - 1)

        for row in range(-VISIBLE_RING_COUNT, VISIBLE_RING_COUNT + 1):
            for column in range(-VISIBLE_RING_COUNT, VISIBLE_RING_COUNT + 1):
                global_row = current_row + row
                global_column = current_column + column
                if not (0 <= global_row < grid_size and 0 <= global_column < grid_size):
                    continue

                left = cell_width * global_column
                top = cell_height * global_row
                box_width = max(cell_width, 2)
                box_height = max(cell_height, 2)
                is_current = self.is_selected and row == 0 and column == 0
                ring = max(abs(row), abs(column))

                distance = math.sqrt(
                    ((left + box_width / 2 - cursor_x) / max(cell_width, 1)) ** 2
                    + ((top + box_height / 2 - cursor_y) / max(cell_height, 1)) ** 2
                )
                opacity = _cell_opacity(distance, is_current)
                radius = min(8, min(box_width, box_height) / 8)

                _rounded_rect(cr, left, top, box_width, box_height, radius)
                if is_current:
                    cr.set_source_rgba(*ACCENT, ACCENT_FILL_ALPHA)
                    cr.fill_preserve()
                elif self.is_selected and ring <= 1:
                    cr.set_source_rgba(*ACCENT, SOFT_ACCENT_ALPHA)
                    cr.fill_preserve()
                cr.set_source_rgba(1, 1, 1, opacity)
                cr.set_line_width(3 if is_current else (1.75 if ring <= 1 else 1))
                cr.stroke()

        if not self.is_selected:
            return

        # Cursor marker.
        cr.arc(cursor_x, cursor_y, 5.5, 0, 2 * math.pi)
        cr.set_source_rgba(*ACCENT, 0xF0 / 255)
        cr.fill_preserve()
        cr.set_source_rgba(1, 1, 1, 1)
        cr.set_line_width(2)
        cr.stroke()

        # Zoom-level chip.
        label = preview.label
        cr.select_font_face("Cantarell")
        cr.set_font_size(11)
        extents = cr.text_extents(label)
        chip_width = extents.width + 14
        chip_height = 22
        chip_x = min(max(cursor_x + 10, 4), max(4.0, width - chip_width - 4))
        chip_y = min(max(cursor_y + 10, 4), max(4.0, height - chip_height - 4))
        _rounded_rect(cr, chip_x, chip_y, chip_width, chip_height, 6)
        cr.set_source_rgba(0x16 / 255, 0x24 / 255, 0x22 / 255, 0xD9 / 255)
        cr.fill_preserve()
        cr.set_source_rgba(*ACCENT, 0x90 / 255)
        cr.set_line_width(1)
        cr.stroke()
        cr.move_to(chip_x + 7, chip_y + chip_height / 2 + extents.height / 2)
        cr.set_source_rgba(1, 1, 1, 1)
        cr.show_text(label)


class ModeHud(Gtk.Window):
    """Port of Views/ModeHudWindow: a brief centred status message."""

    def __init__(self) -> None:
        super().__init__(type=Gtk.WindowType.POPUP)
        self.message = ""
        self.active_tone = True
        self._hide_source: Optional[int] = None

        self.set_app_paintable(True)
        self.set_decorated(False)
        self.set_keep_above(True)
        self.set_accept_focus(False)
        self.set_skip_taskbar_hint(True)
        self.set_type_hint(Gdk.WindowTypeHint.NOTIFICATION)
        visual = self.get_screen().get_rgba_visual()
        if visual is not None:
            self.set_visual(visual)
        self.set_size_request(240, 46)
        self.connect("draw", self._on_draw)
        self.connect("realize", lambda *_: self.get_window().set_pass_through(True))

    def show_message(self, message: str, active_tone: bool, anchor: DisplayMonitor,
                     hide_after_ms: int = 800) -> None:
        self.message = message
        self.active_tone = active_tone
        bounds = anchor.bounds
        self.move(
            bounds.left + bounds.width // 2 - 120,
            bounds.top + bounds.height - 140,
        )
        self.show_all()
        self.queue_draw()
        if self._hide_source is not None:
            GLib.source_remove(self._hide_source)
        self._hide_source = GLib.timeout_add(hide_after_ms, self._auto_hide)

    def _auto_hide(self) -> bool:
        self.hide()
        self._hide_source = None
        return False

    def _on_draw(self, _widget, cr) -> bool:
        width, height = self.get_size()
        cr.set_operator(cairo.Operator.SOURCE)
        cr.set_source_rgba(0, 0, 0, 0)
        cr.paint()
        cr.set_operator(cairo.Operator.OVER)

        _rounded_rect(cr, 0, 0, width, height, 10)
        cr.set_source_rgba(0x16 / 255, 0x24 / 255, 0x22 / 255, 0xE0 / 255)
        cr.fill_preserve()
        if self.active_tone:
            cr.set_source_rgba(*ACCENT, 0.85)
        else:
            cr.set_source_rgba(0.6, 0.6, 0.6, 0.7)
        cr.set_line_width(1.5)
        cr.stroke()

        cr.select_font_face("Cantarell")
        cr.set_font_size(14)
        extents = cr.text_extents(self.message)
        cr.move_to(width / 2 - extents.width / 2, height / 2 + extents.height / 2)
        cr.set_source_rgba(1, 1, 1, 1)
        cr.show_text(self.message)
        return False


class ScreenOverlayMap:
    """Port of Services/ScreenOverlayMap.cs: keeps one overlay per display and
    projects the active preview onto every one of them."""

    def __init__(self, displays: List[DisplayMonitor], enabled: bool = True) -> None:
        self.enabled = enabled
        self._windows: Dict[str, _OverlayWindow] = {}
        self._displays: List[DisplayMonitor] = []
        self.refresh(displays)

    def refresh(self, displays: List[DisplayMonitor]) -> None:
        self._displays = list(displays)
        names = {d.device_name for d in displays}
        for stale in [n for n in self._windows if n not in names]:
            self._windows.pop(stale).destroy()
        for display in displays:
            window = self._windows.get(display.device_name)
            if window is None:
                self._windows[display.device_name] = _OverlayWindow(display)
            else:
                window.premap(display)

    def show_preview(self, preview: NineGridPreview) -> None:
        if not self.enabled:
            return
        for display in self._displays:
            window = self._windows.get(display.device_name)
            if window is None:
                continue
            is_selected = display.device_name == preview.display.device_name
            if preview.is_screen_level:
                projected = preview.with_display(display)
            else:
                projected = preview.with_display(
                    display,
                    step_x=display.bounds.width / max(preview.map_scale, 1),
                    step_y=display.bounds.height / max(preview.map_scale, 1),
                )
            window.show_preview(projected, is_selected)

    def hide_all(self) -> None:
        for window in self._windows.values():
            window.hide()

    def destroy(self) -> None:
        for window in self._windows.values():
            window.destroy()
        self._windows.clear()
