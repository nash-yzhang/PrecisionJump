"""Application host: port of App.xaml.cs + Services/TrayIconService.cs.

The engine runs on its own thread with its own X connection; every UI update is
marshalled onto the GTK main thread. Previews are coalesced into a single
pending slot, mirroring GlobalInputEngine.QueuePreview so a fast mouse cannot
flood the renderer.
"""

from __future__ import annotations

import argparse
import signal
import sys
import threading
from typing import Optional

import gi

gi.require_version("Gtk", "3.0")
from gi.repository import GLib, Gtk  # noqa: E402

from . import settings as settings_module  # noqa: E402
from .engine import GestureEngine  # noqa: E402
from .overlay import ModeHud, ScreenOverlayMap  # noqa: E402
from .screen_targeting import NineGridPreview  # noqa: E402

try:
    gi.require_version("AppIndicator3", "0.1")
    from gi.repository import AppIndicator3
except (ValueError, ImportError):  # pragma: no cover - depends on the desktop
    AppIndicator3 = None


class PrecisionJumpApp:
    def __init__(self, settings, show_tray: bool = True) -> None:
        self.settings = settings
        self.engine = GestureEngine(
            settings,
            on_preview=self._queue_preview,
            on_state_changed=self._on_state_changed,
            on_completion=self._queue_completion,
        )
        self.overlay = ScreenOverlayMap(self.engine.displays, settings.show_overlay)
        self.hud = ModeHud()

        self._pending_preview: Optional[NineGridPreview] = None
        self._preview_queued = False
        self._lock = threading.Lock()
        self._indicator = None
        if show_tray:
            self._build_tray()

    # ---------------------------------------------------------------- preview

    def _queue_preview(self, preview: Optional[NineGridPreview]) -> None:
        """Called from the engine thread."""
        with self._lock:
            self._pending_preview = preview
            if self._preview_queued:
                return
            self._preview_queued = True
        GLib.idle_add(self._render_preview, priority=GLib.PRIORITY_HIGH_IDLE)

    def _render_preview(self) -> bool:
        with self._lock:
            preview = self._pending_preview
            self._preview_queued = False
        if preview is None:
            self.overlay.hide_all()
        else:
            self.overlay.show_preview(preview)
        return False

    def _queue_completion(self, message: str, active_tone: bool) -> None:
        anchor = self.engine.displays[0]
        for display in self.engine.displays:
            if display.is_primary:
                anchor = display
                break
        GLib.idle_add(self.hud.show_message, message, active_tone, anchor)

    def _on_state_changed(self, active: bool) -> None:
        if not active:
            GLib.idle_add(self._refresh_overlay_geometry)

    def _refresh_overlay_geometry(self) -> bool:
        self.overlay.refresh(self.engine.displays)
        return False

    # ------------------------------------------------------------------- tray

    def _build_tray(self) -> None:
        if AppIndicator3 is None:
            print(
                "Tray indicator unavailable (AppIndicator3 missing); "
                "running without a tray icon.",
                file=sys.stderr,
            )
            return

        self._indicator = AppIndicator3.Indicator.new(
            "precision-jump",
            "input-mouse",
            AppIndicator3.IndicatorCategory.APPLICATION_STATUS,
        )
        self._indicator.set_status(AppIndicator3.IndicatorStatus.ACTIVE)
        self._indicator.set_title("Precision Jump")
        self._indicator.set_menu(self._build_menu())

    def _build_menu(self) -> Gtk.Menu:
        menu = Gtk.Menu()

        header = Gtk.MenuItem(label=f"Hold  {self.settings.screen_jump_display}")
        header.set_sensitive(False)
        menu.append(header)
        menu.append(Gtk.SeparatorMenuItem())

        enabled = Gtk.CheckMenuItem(label="Enabled")
        enabled.set_active(self.settings.enabled)
        enabled.connect("toggled", self._on_toggle_enabled)
        menu.append(enabled)

        overlay_item = Gtk.CheckMenuItem(label="Show overlay")
        overlay_item.set_active(self.settings.show_overlay)
        overlay_item.connect("toggled", self._on_toggle_overlay)
        menu.append(overlay_item)

        hud_item = Gtk.CheckMenuItem(label="Show status HUD")
        hud_item.set_active(self.settings.show_status_hud)
        hud_item.connect("toggled", self._on_toggle_hud)
        menu.append(hud_item)

        autostart = Gtk.CheckMenuItem(label="Start with session")
        autostart.set_active(self.settings.start_with_session)
        autostart.connect("toggled", self._on_toggle_autostart)
        menu.append(autostart)

        menu.append(Gtk.SeparatorMenuItem())

        config = Gtk.MenuItem(label="Settings file…")
        config.connect("activate", self._on_open_settings)
        menu.append(config)

        quit_item = Gtk.MenuItem(label="Quit")
        quit_item.connect("activate", lambda *_: self.quit())
        menu.append(quit_item)

        menu.show_all()
        return menu

    def _persist(self) -> None:
        settings_module.save(self.settings)

    def _on_toggle_enabled(self, item) -> None:
        self.settings.enabled = item.get_active()
        self._persist()

    def _on_toggle_overlay(self, item) -> None:
        self.settings.show_overlay = item.get_active()
        self.overlay.enabled = item.get_active()
        if not item.get_active():
            self.overlay.hide_all()
        self._persist()

    def _on_toggle_hud(self, item) -> None:
        self.settings.show_status_hud = item.get_active()
        if not item.get_active():
            self.hud.hide()
        self._persist()

    def _on_toggle_autostart(self, item) -> None:
        self.settings.start_with_session = item.get_active()
        settings_module.apply_autostart(item.get_active())
        self._persist()

    def _on_open_settings(self, _item) -> None:
        settings_module.save(self.settings)
        Gtk.show_uri_on_window(None, f"file://{settings_module.SETTINGS_PATH}", 0)

    # -------------------------------------------------------------- lifecycle

    def run(self) -> None:
        self.engine.start()
        print(
            f"Precision Jump running. Hold  {self.settings.screen_jump_display}  "
            f"and move the mouse; scroll to zoom, right-click to cancel.\n"
            f"Displays: "
            + ", ".join(
                f"#{d.number} {d.device_name} {d.bounds.width}x{d.bounds.height}"
                for d in self.engine.displays
            )
        )
        Gtk.main()

    def quit(self) -> None:
        self.engine.stop()
        self.overlay.destroy()
        Gtk.main_quit()


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        prog="precision-jump",
        description="Zoomable, multi-monitor pointer navigation for X11.",
    )
    parser.add_argument("--no-tray", action="store_true", help="run without a tray icon")
    parser.add_argument("--no-overlay", action="store_true", help="disable the grid overlay")
    parser.add_argument("--record", action="store_true", help="record mouse events to CSV")
    parser.add_argument(
        "--key",
        help="activation sequence, comma separated X keysym names (e.g. 'grave' or 'Control_L,k')",
    )
    args = parser.parse_args(argv)

    settings = settings_module.load()
    if args.no_overlay:
        settings.show_overlay = False
    if args.record:
        settings.record_mouse_events = True
    if args.key:
        settings.screen_jump_sequence = [
            token.strip() for token in args.key.split(",") if token.strip()
        ]
    settings.normalize_values()

    app = PrecisionJumpApp(settings, show_tray=not args.no_tray)

    # Ctrl-C must tear the grabs down cleanly.
    def _on_signal(*_args):
        app.quit()
        return False

    GLib.unix_signal_add(GLib.PRIORITY_HIGH, signal.SIGINT, _on_signal)
    GLib.unix_signal_add(GLib.PRIORITY_HIGH, signal.SIGTERM, _on_signal)

    try:
        app.run()
    finally:
        app.engine.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
