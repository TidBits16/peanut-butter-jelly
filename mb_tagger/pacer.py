"""Thread-safe minimum interval between outbound HTTP calls."""

from __future__ import annotations

import threading
import time


class Pacer:
    def __init__(self, interval: float) -> None:
        self.interval = interval
        self._lock = threading.Lock()
        self._last = 0.0

    def wait(self) -> None:
        with self._lock:
            delay = self.interval - (time.monotonic() - self._last)
            if delay > 0:
                time.sleep(delay)
            self._last = time.monotonic()
