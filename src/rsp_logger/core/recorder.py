"""Run 기록 — CSV(환산값)·_raw.csv(원시값)·run_index.csv (SPEC 8장).

행마다 바로 flush 해서 프로그램이 죽어도 그때까지의 기록은 남는다.
"""
import csv
import re
from datetime import datetime
from pathlib import Path

META_FIELDS = ["시료명", "합금 조성", "투입 중량(g)", "노즐 슬릿", "노즐-휠 갭(mm)", "휠 재질", "휠 지름(mm)",
               "분위기 가스", "작업자", "메모"]
RESULT_FIELDS = ["리본 폭(mm)", "리본 두께(μm)", "외관", "비정질 여부"]


def _stamp(ts):
    return datetime.fromtimestamp(ts).strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]


def _num(v):
    return "" if v is None else f"{v:.6g}"


class Recorder:
    def __init__(self, out_dir, tags, derived):
        """tags: Tag 목록, derived: [(key, 이름, 단위, 확인필요)]"""
        self.out_dir = Path(out_dir)
        self.cols = [(t.key, t.name, t.unit) for t in tags] + [(k, n, u) for k, n, u, _ in derived]
        self.raw_keys = [t.key for t in tags]
        self.path = None

    @property
    def active(self):
        return self.path is not None

    def start(self, ts, meta):
        self.out_dir.mkdir(parents=True, exist_ok=True)
        name = re.sub(r'[\\/:*?"<>|\s]+', "_", meta.get("시료명", "").strip()) or "무제"
        base = f"{datetime.fromtimestamp(ts):%Y%m%d_%H%M}_{name}"
        n = 1
        while (self.out_dir / f"{base}{'' if n == 1 else f'_{n}'}.csv").exists():
            n += 1
        self.path = self.out_dir / f"{base}{'' if n == 1 else f'_{n}'}.csv"
        self.raw_path = self.path.with_name(self.path.stem + "_raw.csv")
        self.meta, self.t0, self.t_last, self.rows = dict(meta), ts, ts, 0
        self._f = open(self.path, "w", encoding="utf-8-sig", newline="")
        self._rf = open(self.raw_path, "w", encoding="utf-8-sig", newline="")
        self._w, self._rw = csv.writer(self._f), csv.writer(self._rf)
        for w in (self._w, self._rw):
            w.writerow(["# 시작", _stamp(ts)])
            for k in META_FIELDS:
                w.writerow([f"# {k}", meta.get(k, "")])
        self._w.writerow(["시각", "경과초"] + [f"{n}({u})" if u and u != "-" else n for _k, n, u in self.cols]
                         + ["출탕구간번호", "이벤트"])
        self._rw.writerow(["시각", "경과초"] + self.raw_keys + ["이벤트"])
        self.event(ts, "Run 시작")
        return self.path

    def add(self, ts, values, raws, event=""):
        if not self.active:
            return
        head = [_stamp(ts), f"{ts - self.t0:.3f}"]
        self._w.writerow(head + [_num(values.get(k)) for k, _n, _u in self.cols] + ["", event])
        self._rw.writerow(head + [raws.get(k, "") for k in self.raw_keys] + [event])
        self._f.flush()
        self._rf.flush()
        self.t_last = ts
        self.rows += 1

    def event(self, ts, text):
        """값 없이 이벤트만 남기는 행 (Run 시작·정지, 연결 끊김 등)."""
        self.add(ts, {}, {}, text)
        self.rows -= 1

    def stop(self, ts, results=None):
        if not self.active:
            return None
        results = results or {}
        self.event(ts, "Run 정지")
        for w in (self._w, self._rw):
            w.writerow(["# 종료", _stamp(ts)])
            for k in RESULT_FIELDS:
                w.writerow([f"# {k}", results.get(k, "")])
        self._f.close()
        self._rf.close()
        index = self.out_dir / "run_index.csv"
        new = not index.exists()
        with open(index, "a", encoding="utf-8-sig" if new else "utf-8", newline="") as f:
            w = csv.writer(f)
            if new:
                w.writerow(["파일", "시작", "종료", "기록초", "행수"] + META_FIELDS + RESULT_FIELDS)
            w.writerow([self.path.name, _stamp(self.t0), _stamp(ts), f"{ts - self.t0:.1f}", self.rows]
                       + [self.meta.get(k, "") for k in META_FIELDS] + [results.get(k, "") for k in RESULT_FIELDS])
        path, self.path = self.path, None
        return path
