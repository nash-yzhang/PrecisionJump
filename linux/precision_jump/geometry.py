"""Minimal geometry primitives mirroring System.Drawing.Point / Rectangle.

Kept dependency-free so the ported mapping logic can be unit tested without
an X server.
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class Point:
    x: int
    y: int


@dataclass(frozen=True)
class Rectangle:
    """Left/top/width/height rectangle with .NET's half-open semantics."""

    left: int
    top: int
    width: int
    height: int

    @property
    def right(self) -> int:
        return self.left + self.width

    @property
    def bottom(self) -> int:
        return self.top + self.height

    def contains(self, point: Point) -> bool:
        return (
            self.left <= point.x < self.right
            and self.top <= point.y < self.bottom
        )

    @property
    def center(self) -> Point:
        # Integer division, matching the C# Center() helper.
        return Point(
            self.left + self.width // 2,
            self.top + self.height // 2,
        )


@dataclass(frozen=True)
class DisplayMonitor:
    device_name: str
    bounds: Rectangle
    working_area: Rectangle
    is_primary: bool
    number: int
