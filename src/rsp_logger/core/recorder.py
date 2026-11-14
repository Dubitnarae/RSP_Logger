"""Run 기록 — CSV(환산값)·_raw.csv(원시값)·run_index.csv (SPEC 6·8장).

Run 중에는 빠르게(0.2초) 읽은 값을 pre_s초 동안 붙잡아 두었다가 저장 여부를 정한다:
출탕 구간 [시작−pre_s, 종료+post_s] 안이면 모두, 밖이면 interval_s 간격으로만 저장.
그래서 파일에는 최대 pre_s초 늦게 써진다 (정지·창 닫기 때는 남은 값을 모두 쓴다).
"""
import csv
import re
from collections import deque
from datetime import datetime
from pathlib import Path

META_FIELDS = ["시료명", "합금 조성", "투입 중량(g)", "노즐 슬릿", "노즐-휠 갭(mm)", "휠 재질", "휠 지름(mm)",
               "분위기 가스", "작업자", "메모"]
RESULT_FIELDS = ["리본 폭(mm)", "리본 두께(μm)", "외관", "비정질 여부"]
# 출탕 요약에 넣는 항목 (시작값·평균·최대·최소)
SUMMARY_KEYS = ["wheel_v", "wheel_rpm", "dp", "p_upper", "p_lower", "t_melt", "t_nozzle", "t_wheel_out",
                "vg1", "vg2"]
STAT_NAMES = ["시작", "평균", "최대", "최소"]
START_KEY = "stopper_up"  # 0→1 상승 = 출탕 자동 시작 (DECISIONS 2026-11-14)


def _stamp(ts):
    return datetime.fromtimestamp(ts).strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]


def _num(v):
    return "" if v is None else f"{v:.6g}"


class Recorder:
    def __init__(self, out_dir, tags, derived, interval_s=1.0, pre_s=10.0, post_s=30.0):
        """tags: Tag 목록, derived: [(key, 이름, 단위, 확인필요)]"""
        self.out_dir = Path(out_dir)
        self.cols = [(t.key, t.name, t.unit) for t in tags] + [(k, n, u) for k, n, u, _ in derived]
        self.names = {k: n for k, n, _u in self.cols}
        self.raw_keys = [t.key for t in tags]
        self.interval_s, self.pre_s, self.post_s = interval_s, pre_s, post_s
        self.path = None
        self.ejections = []

    @property
    def active(self):
        return self.path is not None

    @property
    def ejecting(self):
        return bool(self.ejections) and self.ejections[-1]["end"] is None

    # ---------- Run ----------
    def start(self, ts, meta):
        self.out_dir.mkdir(parents=True, exist_ok=True)
        name = re.sub(r'[\\/:*?"<>|\s]+', "_", meta.get("시료명", "").strip()) or "무제"
        base = f"{datetime.fromtimestamp(ts):%Y%m%d_%H%M}_{name}"
        n = 1
        while (self.out_dir / f"{base}{'' if n == 1 else f'_{n}'}.csv").exists():
            n += 1
        self.path = self.out_dir / f"{base}{'' if n == 1 else f'_{n}'}.csv"
        self.raw_path = self.path.with_name(self.path.stem + "_raw.csv")
        self.meta, self.t0, self.rows = dict(meta), ts, 0
        self.pending = deque()  # (ts, values, raws, event) — 저장 여부 결정 대기
        self.last_written = None
        self.prev_start_bit = None
        self.prev_sample = None
        self.ejections = []
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

    def add(self, ts, values, raws):
        if not self.active:
            return
        bit = values.get(START_KEY)
        if self.prev_start_bit == 0 and bit == 1:
            self.eject_start(ts, "자동(M151)")
        if bit is not None:
            self.prev_start_bit = bit
        if self.ejecting:
            self.ejections[-1]["samples"].append(values)
        else:
            self.prev_sample = values
        self.pending.append((ts, values, raws, ""))
        self._drain(ts - self.pre_s)

    def event(self, ts, text):
        """값 없이 이벤트만 남기는 행 (Run 시작·정지, 연결 끊김, 출탕 시작·종료)."""
        if self.active:
            self.pending.append((ts, {}, {}, text))

    # ---------- 출탕 ----------
    def eject_start(self, ts, how="수동"):
        if not self.active or self.ejecting:
            return None
        no = len(self.ejections) + 1
        self.ejections.append({"no": no, "start": ts, "end": None, "how": how, "samples": [],
                               "before": self.prev_sample})
        self.event(ts, f"출탕 {no} 시작({how})")
        return no

    def eject_end(self, ts, why=""):
        if not self.ejecting:
            return None
        e = self.ejections[-1]
        e["end"] = ts
        self.event(ts, f"출탕 {e['no']} 종료{why}")
        return e["no"]

    def _eject_no(self, t):
        for e in self.ejections:
            if e["start"] <= t and (e["end"] is None or t <= e["end"]):
                return e["no"]
        return None

    def _in_window(self, t):
        return any(e["start"] - self.pre_s <= t and (e["end"] is None or t <= e["end"] + self.post_s)
                   for e in self.ejections)

    def _drain(self, upto):
        """upto 이전 값들의 저장 여부를 정해 파일에 쓴다."""
        while self.pending and self.pending[0][0] <= upto:
            ts, values, raws, ev = self.pending.popleft()
            if ev or self._in_window(ts) or self.last_written is None \
                    or ts - self.last_written >= self.interval_s * 0.95:
                self._write(ts, values, raws, ev)
        self._f.flush()
        self._rf.flush()

    def _write(self, ts, values, raws, ev):
        head = [_stamp(ts), f"{ts - self.t0:.3f}"]
        no = "" if ev else (self._eject_no(ts) or "")
        self._w.writerow(head + [_num(values.get(k)) for k, _n, _u in self.cols] + [no, ev])
        self._rw.writerow(head + [raws.get(k, "") for k in self.raw_keys] + [ev])
        if not ev:
            self.last_written = ts
            self.rows += 1

    # ---------- 요약 ----------
    def summary(self, e):
        """출탕 한 번의 요약 -> 순서 있는 dict"""
        s = e["samples"]
        out = {"출탕번호": e["no"], "출탕 시작": _stamp(e["start"]), "출탕 종료": _stamp(e["end"]),
               "출탕 시간(s)": f"{e['end'] - e['start']:.1f}", "판정": e["how"]}
        for k in SUMMARY_KEYS:
            vals = [x[k] for x in s if x.get(k) is not None]
            stats = [vals[0], sum(vals) / len(vals), max(vals), min(vals)] if vals else [None] * 4
            for name, v in zip(STAT_NAMES, stats):
                out[f"{self.names.get(k, k)} {name}"] = _num(v)
        temps = [x["t_wheel_out"] for x in s if x.get("t_wheel_out") is not None]
        before = (e["before"] or {}).get("t_wheel_out")
        out["휠 출수온도 상승폭"] = _num(max(temps) - before) if temps and before is not None else ""
        return out

    def stop(self, ts, results=None):
        if not self.active:
            return None
        results = results or {}
        self.eject_end(ts, "(Run 정지)")
        self.event(ts, "Run 정지")
        self._drain(float("inf"))
        sums = [self.summary(e) for e in self.ejections]
        for w in (self._w, self._rw):
            w.writerow(["# 종료", _stamp(ts)])
            for k in RESULT_FIELDS:
                w.writerow([f"# {k}", results.get(k, "")])
        if sums:
            self._w.writerow(["# 출탕 요약"] + list(sums[0]))
            for row in sums:
                self._w.writerow(["#"] + list(row.values()))
        self._f.close()
        self._rf.close()
        self._index(ts, results, sums)
        path, self.path = self.path, None
        return path

    def _index(self, ts, results, sums):
        """run_index.csv — 출탕마다 한 줄 (출탕이 없으면 Run 한 줄)."""
        sum_cols = list(self.summary_columns())
        header = ["파일", "Run 시작", "Run 종료", "기록초", "행수"] + META_FIELDS + RESULT_FIELDS + sum_cols
        index = self.out_dir / "run_index.csv"
        if index.exists():
            with open(index, encoding="utf-8-sig", newline="") as f:
                old = next(csv.reader(f), None)
            if old != header:  # 열 구성이 바뀌면 옛 파일은 이름을 바꿔 보존
                index.rename(index.with_name(f"run_index_이전_{datetime.now():%Y%m%d_%H%M%S}.csv"))
        new = not index.exists()
        base = [self.path.name, _stamp(self.t0), _stamp(ts), f"{ts - self.t0:.1f}", self.rows] \
            + [self.meta.get(k, "") for k in META_FIELDS] + [results.get(k, "") for k in RESULT_FIELDS]
        with open(index, "a", encoding="utf-8-sig" if new else "utf-8", newline="") as f:
            w = csv.writer(f)
            if new:
                w.writerow(header)
            for s in sums or [{}]:
                w.writerow(base + [s.get(c, "") for c in sum_cols])

    def summary_columns(self):
        yield from ["출탕번호", "출탕 시작", "출탕 종료", "출탕 시간(s)", "판정"]
        for k in SUMMARY_KEYS:
            for name in STAT_NAMES:
                yield f"{self.names.get(k, k)} {name}"
        yield "휠 출수온도 상승폭"
