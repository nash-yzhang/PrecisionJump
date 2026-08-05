"""Mouse event recorder.

Implements the CSV contract the README documents:

  * one UTC-timestamped file per session, opened exclusively so an existing
    recording is never appended to or overwritten,
  * columns  t_us,x,y,evt,jumping,raw_dx,raw_dy,
  * t_us is high-resolution elapsed microseconds since session start,
  * writes go through a bounded background queue so disk I/O never blocks the
    input path, flushing at least every 250 ms or 256 rows.
"""

from __future__ import annotations

import queue
import threading
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

from .geometry import Point
from .settings import DATA_DIR

FLUSH_INTERVAL_SECONDS = 0.25
FLUSH_ROW_COUNT = 256
QUEUE_CAPACITY = 8192
HEADER = "t_us,x,y,evt,jumping,raw_dx,raw_dy\n"


class MouseEventRecorder:
    def __init__(self, directory: Path = DATA_DIR) -> None:
        self._directory = directory
        self._queue: "queue.Queue[Optional[str]]" = queue.Queue(QUEUE_CAPACITY)
        self._thread: Optional[threading.Thread] = None
        self._started_at = time.perf_counter()
        self._handle = None
        self.path: Optional[Path] = None
        self.dropped = 0

    def start(self) -> Path:
        if self._thread is not None:
            return self.path  # type: ignore[return-value]

        self._directory.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        path = self._directory / f"mouse-events-{stamp}.csv"
        suffix = 1
        while True:
            try:
                # "x" is CreateNew: never clobber an existing recording.
                self._handle = path.open("x", buffering=1 << 16)
                break
            except FileExistsError:
                path = self._directory / f"mouse-events-{stamp}-{suffix}.csv"
                suffix += 1

        self._handle.write(HEADER)
        self.path = path
        self._started_at = time.perf_counter()
        self._thread = threading.Thread(
            target=self._drain, name="precision-jump-recorder", daemon=True
        )
        self._thread.start()
        return path

    def record(
        self,
        position: Point,
        event_name: str,
        jumping: bool,
        raw_dx: float = 0,
        raw_dy: float = 0,
    ) -> None:
        if self._thread is None:
            return

        elapsed_us = int((time.perf_counter() - self._started_at) * 1_000_000)
        row = (
            f"{elapsed_us},{position.x},{position.y},{event_name},"
            f"{'true' if jumping else 'false'},{raw_dx:g},{raw_dy:g}\n"
        )
        try:
            self._queue.put_nowait(row)
        except queue.Full:
            # Bounded queue: drop rather than stall the input path.
            self.dropped += 1

    def _drain(self) -> None:
        pending = 0
        last_flush = time.perf_counter()
        while True:
            timeout = max(0.0, FLUSH_INTERVAL_SECONDS - (time.perf_counter() - last_flush))
            try:
                row = self._queue.get(timeout=timeout or FLUSH_INTERVAL_SECONDS)
            except queue.Empty:
                row = ""

            if row is None:  # shutdown sentinel
                break

            if row and self._handle is not None:
                self._handle.write(row)
                pending += 1

            now = time.perf_counter()
            if pending and (
                pending >= FLUSH_ROW_COUNT or now - last_flush >= FLUSH_INTERVAL_SECONDS
            ):
                self._handle.flush()
                pending = 0
                last_flush = now

        if self._handle is not None:
            self._handle.flush()

    def stop(self) -> None:
        if self._thread is None:
            return
        try:
            self._queue.put_nowait(None)
        except queue.Full:
            pass
        self._thread.join(timeout=1.5)
        self._thread = None
        if self._handle is not None:
            self._handle.flush()
            self._handle.close()
            self._handle = None
