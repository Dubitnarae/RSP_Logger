"""백그라운드 읽기 반복. 끊기면 1·2·5·10초 간격으로 재접속."""
import threading
import time

from ..xgt.protocol import XgtError
from .tags import decode, plan_blocks

BACKOFF = [1, 2, 5, 10]
MIN_INTERVAL = 0.1  # CLAUDE.md 규칙 4


class Poller(threading.Thread):
    def __init__(self, client, tags, interval, on_data, on_status):
        super().__init__(daemon=True)
        self.client, self.tags = client, tags
        self.base_interval = self.interval = max(MIN_INTERVAL, interval)
        self.on_data, self.on_status = on_data, on_status
        self.blocks = plan_blocks(tags)
        self._stop = threading.Event()

    def set_interval(self, s):
        self.interval = max(MIN_INTERVAL, s)

    def stop(self):
        self._stop.set()

    def run(self):
        fails = 0
        while not self._stop.is_set():
            t0 = time.monotonic()
            try:
                results = [self.client.read_words(*b) for b in self.blocks]
            except (OSError, XgtError) as e:
                self.client.close()
                wait = BACKOFF[min(fails, len(BACKOFF) - 1)]
                fails += 1
                self.on_status(False, f"끊김 ({e}) — {wait}초 뒤 재접속")
                self._stop.wait(wait)
                continue
            fails = 0
            self.on_status(True, "연결됨")
            self.on_data(time.time(), *decode(self.tags, self.blocks, results))
            self._stop.wait(max(0.0, self.interval - (time.monotonic() - t0)))
        self.client.close()
