"""Port of Models/AppSettings.cs and Services/SettingsStore.cs.

Windows paths become XDG paths:
  %LOCALAPPDATA%\\MouseAccelerator\\settings.json -> ~/.config/precision-jump/settings.json
  CSV recordings                                  -> ~/.local/share/precision-jump/
"Start with Windows" (registry Run key)           -> ~/.config/autostart/precision-jump.desktop
"""

from __future__ import annotations

import json
import os
import sys
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import List

from .sequence import BACKTICK, normalize, sequence_display

CONFIG_DIR = Path(
    os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")
) / "precision-jump"
DATA_DIR = Path(
    os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share")
) / "precision-jump"
SETTINGS_PATH = CONFIG_DIR / "settings.json"
AUTOSTART_PATH = Path(
    os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")
) / "autostart" / "precision-jump.desktop"


def _clamp(value, low, high):
    return max(low, min(high, value))


@dataclass
class AppSettings:
    enabled: bool = True
    screen_jump_sequence: List[str] = field(default_factory=lambda: [BACKTICK])
    sequence_timeout_seconds: float = 0.42
    selection_distance: float = 26.0
    selection_cooldown_milliseconds: int = 120
    maximum_zoom_level: int = 8
    show_status_hud: bool = True
    show_overlay: bool = True
    record_mouse_events: bool = False
    start_with_session: bool = False

    def normalize_values(self) -> None:
        sequence = [normalize(token) for token in (self.screen_jump_sequence or [])][:3]
        self.screen_jump_sequence = sequence or [BACKTICK]
        self.sequence_timeout_seconds = _clamp(float(self.sequence_timeout_seconds), 0.12, 1.5)
        self.selection_distance = _clamp(float(self.selection_distance), 8, 120)
        self.selection_cooldown_milliseconds = int(
            _clamp(int(self.selection_cooldown_milliseconds), 40, 400)
        )
        self.maximum_zoom_level = int(_clamp(int(self.maximum_zoom_level), 1, 10))

    def restore_defaults(self) -> None:
        defaults = AppSettings()
        for key, value in asdict(defaults).items():
            setattr(self, key, value)

    @property
    def screen_jump_display(self) -> str:
        return sequence_display(self.screen_jump_sequence)


def load(path: Path = SETTINGS_PATH) -> AppSettings:
    settings = AppSettings()
    try:
        raw = json.loads(path.read_text())
    except (OSError, ValueError):
        settings.normalize_values()
        return settings

    known = {f for f in AppSettings().__dict__}
    for key, value in raw.items():
        if key in known:
            setattr(settings, key, value)
    settings.normalize_values()
    return settings


def save(settings: AppSettings, path: Path = SETTINGS_PATH) -> None:
    settings.normalize_values()
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(asdict(settings), indent=2))
    tmp.replace(path)


def apply_autostart(enabled: bool) -> None:
    """Replacement for the Windows Run registry key."""
    if not enabled:
        AUTOSTART_PATH.unlink(missing_ok=True)
        return

    launcher = Path(__file__).resolve().parent.parent / "run.sh"
    AUTOSTART_PATH.parent.mkdir(parents=True, exist_ok=True)
    AUTOSTART_PATH.write_text(
        "[Desktop Entry]\n"
        "Type=Application\n"
        "Name=Precision Jump\n"
        f"Exec={launcher}\n"
        "X-GNOME-Autostart-enabled=true\n"
        "NoDisplay=true\n"
    )
