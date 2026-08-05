"""Port of Services/ScreenTargeting.cs (NineGridSession).

This is the heart of Precision Jump: it maps physical mouse displacement onto
a zoomable model of the display topology.

  depth 0   -> the coarsest "screen map": displays are connected through a
               simplified eight-direction topology, so leaving one display's
               normalised [0,1] box hops to the nearest display in that octant.
  depth > 0 -> the "actual layout map": movement uses the real X screen
               geometry (positions, sizes, alignment and gaps).

Scale is 3**depth and is eased over ScaleAnimationSeconds. The scale never
moves the pointer by itself -- it only changes how much map distance a given
physical displacement buys.

Timestamps are seconds (time.perf_counter) rather than Stopwatch ticks; every
public method accepts an explicit `now` so the logic stays deterministic under
test.
"""

from __future__ import annotations

import math
import time
from dataclasses import dataclass, replace
from typing import Iterable, Optional, Sequence

from .geometry import DisplayMonitor, Point, Rectangle

SCALE_ANIMATION_SECONDS = 0.14
_EPSILON = 0.000001


@dataclass(frozen=True)
class NineGridPreview:
    display: DisplayMonitor
    actual_cursor: Point
    depth: int
    map_scale: float
    step_x: float
    step_y: float

    @property
    def is_screen_level(self) -> bool:
        return self.depth == 0

    @property
    def label(self) -> str:
        if self.is_screen_level:
            return f"SCREEN {self.display.number}"
        return f"ZOOM {self.map_scale:.1f}×"

    def with_display(self, display: DisplayMonitor, **changes) -> "NineGridPreview":
        return replace(self, display=display, **changes)


@dataclass(frozen=True)
class GridInteraction:
    preview: NineGridPreview
    jump_target: Optional[Point]


def _clamp(value, low, high):
    return max(low, min(high, value))


def _contains(bounds: Rectangle, x: float, y: float) -> bool:
    return (
        x >= bounds.left
        and x < bounds.right
        and y >= bounds.top
        and y < bounds.bottom
    )


def _octant(dx: float, dy: float) -> tuple[int, int]:
    """Quantise a direction into one of eight (row, column) steps."""
    angle = math.atan2(dy, dx)
    raw = angle / (math.pi / 4)
    # C# MidpointRounding.AwayFromZero (Python's round() is banker's rounding).
    octant = int(math.floor(raw + 0.5)) if raw >= 0 else int(math.ceil(raw - 0.5))
    octant = ((octant % 8) + 8) % 8
    return {
        0: (0, 1),
        1: (1, 1),
        2: (1, 0),
        3: (1, -1),
        4: (0, -1),
        5: (-1, -1),
        6: (-1, 0),
        7: (-1, 1),
    }[octant]


def _update_ray_interval(origin, direction, min_bound, max_bound, interval):
    """Slab clip for one axis. `interval` is a mutable [minimum, maximum]."""
    if abs(direction) < _EPSILON:
        return min_bound <= origin <= max_bound

    first = (min_bound - origin) / direction
    second = (max_bound - origin) / direction
    if first > second:
        first, second = second, first

    interval[0] = max(interval[0], first)
    interval[1] = min(interval[1], second)
    return interval[1] >= interval[0]


def _ray_rectangle_entry(
    origin_x, origin_y, direction_x, direction_y, rectangle: Rectangle
) -> Optional[float]:
    """Parametric distance at which a ray enters a rectangle, or None."""
    interval = [-math.inf, math.inf]
    if not _update_ray_interval(
        origin_x, direction_x, rectangle.left, rectangle.right, interval
    ) or not _update_ray_interval(
        origin_y, direction_y, rectangle.top, rectangle.bottom, interval
    ):
        return None

    entry = max(interval[0], 0.0)
    return entry if interval[1] >= entry else None


class NineGridSession:
    def __init__(
        self,
        origin: Point,
        displays: Sequence[DisplayMonitor],
    ) -> None:
        if not displays:
            raise RuntimeError("No displays are available.")

        self._displays = list(displays)
        self.origin = origin
        self._map_x = float(origin.x)
        self._map_y = float(origin.y)
        self._depth = 0
        self._scale = 1.0
        self._scale_start = 1.0
        self._scale_target = 1.0
        self._scale_started_at = 0.0
        self.display = self._find_display(origin)
        self.preview = self._create_preview()

    # ---------------------------------------------------------------- state

    @property
    def current_position(self) -> Point:
        return self._rounded_position()

    @property
    def depth(self) -> int:
        return self._depth

    @property
    def map_scale(self) -> float:
        return self._scale

    @property
    def step_x(self) -> float:
        return self.display.bounds.width / max(self._scale, 1.0)

    @property
    def step_y(self) -> float:
        return self.display.bounds.height / max(self._scale, 1.0)

    def is_scale_animating(self, now: Optional[float] = None) -> bool:
        now = time.perf_counter() if now is None else now
        elapsed = now - self._scale_started_at
        return (
            abs(self._scale - self._scale_target) > 0.001
            and elapsed < SCALE_ANIMATION_SECONDS
        )

    # ------------------------------------------------------------- movement

    def move_continuous(
        self,
        physical_delta_x: float,
        physical_delta_y: float,
        map_unit_travel_distance: float,
        now: Optional[float] = None,
    ) -> GridInteraction:
        self._update_scale(now)
        if physical_delta_x == 0 and physical_delta_y == 0:
            self.preview = self._create_preview()
            return GridInteraction(self.preview, None)

        travel = max(map_unit_travel_distance, 1.0)
        if self._depth == 0:
            self._move_on_discrete_screen_map(
                physical_delta_x / (travel * self._scale),
                physical_delta_y / (travel * self._scale),
            )
        else:
            self._move_on_actual_display_map(
                physical_delta_x * self.display.bounds.width / (travel * self._scale),
                physical_delta_y * self.display.bounds.height / (travel * self._scale),
            )

        self.preview = self._create_preview()
        return GridInteraction(self.preview, self.current_position)

    def zoom_in(
        self,
        maximum_depth: int,
        actual_position: Optional[Point] = None,
        now: Optional[float] = None,
    ) -> GridInteraction:
        if self._depth >= maximum_depth:
            return GridInteraction(self.preview, None)

        if actual_position is not None:
            self._sync_position(actual_position)
        self._start_scale_transition(self._depth + 1, now)
        self.preview = self._create_preview()
        return GridInteraction(self.preview, None)

    def zoom_out(
        self,
        actual_position: Optional[Point] = None,
        now: Optional[float] = None,
    ) -> GridInteraction:
        if self._depth == 0:
            return GridInteraction(self.preview, None)

        if actual_position is not None:
            self._sync_position(actual_position)
        self._start_scale_transition(self._depth - 1, now)
        self.preview = self._create_preview()
        return GridInteraction(self.preview, None)

    def refresh_scale(self, now: Optional[float] = None) -> NineGridPreview:
        self._update_scale(now)
        self.preview = self._create_preview()
        return self.preview

    def accept_programmatic_position(self, actual_position: Point) -> None:
        self._sync_position(actual_position)
        self.preview = self._create_preview()

    # ------------------------------------------------------- internal moves

    def _move_on_discrete_screen_map(self, normalized_dx, normalized_dy) -> None:
        bounds = self.display.bounds
        width = max(bounds.width - 1, 1)
        height = max(bounds.height - 1, 1)
        u = (self._map_x - bounds.left) / width + normalized_dx
        v = (self._map_y - bounds.top) / height + normalized_dy

        for _ in range(8):
            column_direction = -1 if u < 0 else (1 if u > 1 else 0)
            row_direction = -1 if v < 0 else (1 if v > 1 else 0)
            if row_direction == 0 and column_direction == 0:
                break

            next_display = self._find_discrete_display(row_direction, column_direction)
            if next_display is None:
                u = _clamp(u, 0, 1)
                v = _clamp(v, 0, 1)
                break

            if column_direction > 0:
                u -= 1
            elif column_direction < 0:
                u += 1
            if row_direction > 0:
                v -= 1
            elif row_direction < 0:
                v += 1

            self.display = next_display

        bounds = self.display.bounds
        self._map_x = bounds.left + _clamp(u, 0, 1) * max(bounds.width - 1, 1)
        self._map_y = bounds.top + _clamp(v, 0, 1) * max(bounds.height - 1, 1)

    def _move_on_actual_display_map(self, map_delta_x, map_delta_y) -> None:
        proposed_x = self._map_x + map_delta_x
        proposed_y = self._map_y + map_delta_y
        if _contains(self.display.bounds, proposed_x, proposed_y):
            self._map_x = proposed_x
            self._map_y = proposed_y
            return

        containing = next(
            (d for d in self._displays if _contains(d.bounds, proposed_x, proposed_y)),
            None,
        )
        if containing is not None:
            self.display = containing
            self._map_x = proposed_x
            self._map_y = proposed_y
            return

        crossing = self._find_actual_layout_crossing(
            self._map_x, self._map_y, map_delta_x, map_delta_y
        )
        if crossing is not None:
            target_display, entry_x, entry_y = crossing
            self.display = target_display
            self._map_x = entry_x
            self._map_y = entry_y
            return

        # Keep integrating through real virtual-desktop gaps. The visible
        # pointer stays projected to the current display edge until the
        # continuous map coordinate enters another display rectangle.
        self._map_x = proposed_x
        self._map_y = proposed_y

    # ------------------------------------------------------------ zoom math

    def _start_scale_transition(self, depth: int, now: Optional[float]) -> None:
        now = time.perf_counter() if now is None else now
        self._update_scale(now)
        self._depth = depth
        self._scale_start = self._scale
        self._scale_target = math.pow(3, depth)
        self._scale_started_at = now

    def _update_scale(self, now: Optional[float]) -> None:
        if abs(self._scale - self._scale_target) <= 0.001:
            self._scale = self._scale_target
            return

        now = time.perf_counter() if now is None else now
        elapsed = now - self._scale_started_at
        progress = _clamp(elapsed / SCALE_ANIMATION_SECONDS, 0.0, 1.0)
        eased = progress * progress * (3 - 2 * progress)  # smoothstep
        self._scale = self._scale_start + (self._scale_target - self._scale_start) * eased
        if progress >= 1:
            self._scale = self._scale_target

    # -------------------------------------------------------------- lookups

    def _sync_position(self, position: Point) -> None:
        self._map_x = float(position.x)
        self._map_y = float(position.y)
        self.display = self._find_display(position)

    def _create_preview(self) -> NineGridPreview:
        return NineGridPreview(
            self.display,
            self.current_position,
            self._depth,
            self._scale,
            self.step_x,
            self.step_y,
        )

    def _find_discrete_display(
        self, row_direction: int, column_direction: int
    ) -> Optional[DisplayMonitor]:
        current_center = self.display.bounds.center
        candidates = []
        for candidate in self._displays:
            if candidate.device_name == self.display.device_name:
                continue
            candidate_center = candidate.bounds.center
            dx = candidate_center.x - current_center.x
            dy = candidate_center.y - current_center.y
            if _octant(dx, dy) != (row_direction, column_direction):
                continue
            candidates.append((dx * dx + dy * dy, candidate))

        if not candidates:
            return None
        # min() on the distance only -- DisplayMonitor is not orderable and
        # ties must keep enumeration order, like C#'s stable OrderBy.
        return min(candidates, key=lambda item: item[0])[1]

    def _find_actual_layout_crossing(
        self, origin_x, origin_y, direction_x, direction_y
    ):
        length = math.sqrt(direction_x * direction_x + direction_y * direction_y)
        if length <= 0:
            return None

        best = None
        for candidate in self._displays:
            if candidate.device_name == self.display.device_name:
                continue
            entry = _ray_rectangle_entry(
                origin_x, origin_y, direction_x, direction_y, candidate.bounds
            )
            if entry is None or not (0 <= entry <= 1):
                continue
            if best is None or entry < best[0]:
                best = (entry, candidate)

        if best is None:
            return None

        entry, target = best
        unit_x = direction_x / length
        unit_y = direction_y / length
        bounds = target.bounds
        return (
            target,
            _clamp(origin_x + direction_x * entry + unit_x, bounds.left, bounds.right - 1),
            _clamp(origin_y + direction_y * entry + unit_y, bounds.top, bounds.bottom - 1),
        )

    def _find_display(self, point: Point) -> DisplayMonitor:
        for display in self._displays:
            if display.bounds.contains(point):
                return display

        def squared_gap(display: DisplayMonitor) -> int:
            x = _clamp(point.x, display.bounds.left, display.bounds.right - 1)
            y = _clamp(point.y, display.bounds.top, display.bounds.bottom - 1)
            dx = point.x - x
            dy = point.y - y
            return dx * dx + dy * dy

        return min(self._displays, key=squared_gap)

    def _rounded_position(self) -> Point:
        bounds = self.display.bounds
        return Point(
            int(_clamp(_round_half_away(self._map_x), bounds.left, bounds.right - 1)),
            int(_clamp(_round_half_away(self._map_y), bounds.top, bounds.bottom - 1)),
        )


def _round_half_away(value: float) -> int:
    """Math.Round(value) in C# rounds .5 away from zero; Python rounds to even."""
    return int(math.floor(value + 0.5)) if value >= 0 else int(math.ceil(value - 0.5))


def find_grid_cell(bounds: Rectangle, grid_size: int, point: Point) -> tuple[int, int]:
    """Helper retained for parity with the C# test-suite entry points."""
    cell_width = bounds.width / grid_size
    cell_height = bounds.height / grid_size
    column = int(_clamp(math.floor((point.x - bounds.left) / cell_width), 0, grid_size - 1))
    row = int(_clamp(math.floor((point.y - bounds.top) / cell_height), 0, grid_size - 1))
    return row, column


def grid_cell(bounds: Rectangle, grid_size: int, row: int, column: int) -> Rectangle:
    cell_width = bounds.width / grid_size
    cell_height = bounds.height / grid_size
    left = int(round(bounds.left + column * cell_width))
    top = int(round(bounds.top + row * cell_height))
    return Rectangle(left, top, int(round(cell_width)), int(round(cell_height)))


def order_displays(displays: Iterable[DisplayMonitor]) -> list[DisplayMonitor]:
    """Match GetDisplays(): order by left, then top, numbering from 1."""
    ordered = sorted(displays, key=lambda d: (d.bounds.left, d.bounds.top))
    return [
        DisplayMonitor(
            d.device_name, d.bounds, d.working_area, d.is_primary, index + 1
        )
        for index, d in enumerate(ordered)
    ]
