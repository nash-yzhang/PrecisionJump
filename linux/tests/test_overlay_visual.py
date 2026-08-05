"""Launches the real app, drives it with synthetic input and screenshots the
overlay at each zoom level.

Usage:  python3 tests/test_overlay_visual.py <output-directory>
"""

from __future__ import annotations

import os
import signal
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GdkPixbuf, Gtk  # noqa: E402

Gtk.init([])
from Xlib import X, display as xdisplay  # noqa: E402
from Xlib.ext import xtest  # noqa: E402

from precision_jump.keys import keycodes_for_name  # noqa: E402

OUT = Path(sys.argv[1] if len(sys.argv) > 1 else ".")
OUT.mkdir(parents=True, exist_ok=True)
ROOT_DIR = Path(__file__).resolve().parent.parent

driver = xdisplay.Display()
root = driver.screen().root
geometry = root.get_geometry()
restore = root.query_pointer()


def shot(name: str) -> Path:
    window = Gdk.get_default_root_window()
    pixbuf = Gdk.pixbuf_get_from_window(window, 0, 0, geometry.width, geometry.height)
    # Halve it so the screenshot is cheap to inspect.
    scaled = pixbuf.scale_simple(
        geometry.width // 2, geometry.height // 2, GdkPixbuf.InterpType.BILINEAR
    )
    path = OUT / name
    scaled.savev(str(path), "png", [], [])
    print(f"  wrote {path}")
    return path


def key(name: str, press: bool) -> None:
    keycode = keycodes_for_name(driver, name)[0]
    xtest.fake_input(driver, X.KeyPress if press else X.KeyRelease, keycode)
    driver.sync()


def button(number: int) -> None:
    xtest.fake_input(driver, X.ButtonPress, number)
    xtest.fake_input(driver, X.ButtonRelease, number)
    driver.sync()


def move(dx: int, dy: int) -> None:
    xtest.fake_input(driver, X.MotionNotify, detail=1, x=dx, y=dy)
    driver.sync()


app = subprocess.Popen(
    ["/usr/bin/python3", "-m", "precision_jump.app", "--no-tray"],
    cwd=str(ROOT_DIR),
    stdout=subprocess.PIPE,
    stderr=subprocess.STDOUT,
    text=True,
    preexec_fn=os.setsid,
)
time.sleep(2.5)

try:
    if app.poll() is not None:
        print("app exited early:\n" + app.stdout.read())
        sys.exit(1)

    root.warp_pointer(900, 500)
    driver.sync()
    time.sleep(0.4)

    print("holding activation key (screen level)...")
    key("grave", True)
    time.sleep(0.9)
    shot("overlay-1-screen-level.png")

    print("zooming in one level...")
    button(4)
    time.sleep(0.7)
    shot("overlay-2-zoom-3x.png")

    print("zooming in a second level...")
    button(4)
    time.sleep(0.7)
    move(6, 2)
    time.sleep(0.5)
    shot("overlay-3-zoom-9x.png")

    print("releasing...")
    key("grave", False)
    time.sleep(0.8)
    shot("overlay-4-after-release.png")

finally:
    try:
        key("grave", False)
    except Exception:
        pass
    time.sleep(0.3)
    try:
        os.killpg(os.getpgid(app.pid), signal.SIGTERM)
        app.wait(timeout=5)
    except Exception:
        try:
            os.killpg(os.getpgid(app.pid), signal.SIGKILL)
        except Exception:
            pass
    try:
        root.warp_pointer(restore.root_x, restore.root_y)
        driver.sync()
    except Exception:
        pass

output = app.stdout.read() if app.stdout else ""
print("--- app output ---")
print(output.strip()[:2000])
