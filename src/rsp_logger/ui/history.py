"""지난 기록 보기 — 저장한 Run CSV를 열어 그래프로 다시 본다 (출탕 구간 음영·이벤트 선 포함)."""
import csv

from PySide6.QtCore import Qt  # pyqtgraph 보다 먼저 (main_window.py 참고)
from PySide6.QtWidgets import QHBoxLayout, QLabel, QListWidget, QListWidgetItem, QVBoxLayout, QWidget
import pyqtgraph as pg  # noqa: E402,I001

from .main_window import COLORS

SKIP = {"시각", "경과초", "출탕구간번호", "이벤트"}
DEFAULT = ("상부 챔버 압력", "하부 챔버 압력", "분사 차압")


def load_run(path):
    """-> (메타 [(키, 값)], 열 이름 목록, 행 dict 목록)"""
    with open(path, encoding="utf-8-sig", newline="") as f:
        rows = list(csv.reader(f))
    hi = next((i for i, r in enumerate(rows) if r and r[0] == "시각"), None)
    if hi is None:
        raise ValueError("RSP Logger 기록 파일이 아닙니다 ('시각' 머리줄 없음).")
    meta = [(r[0][1:].strip(), r[1]) for r in rows if r and r[0].startswith("#") and len(r) == 2 and r[1]]
    hdr = rows[hi]
    data = [dict(zip(hdr, r)) for r in rows[hi + 1:] if r and not r[0].startswith("#")]
    return meta, hdr, data


def _f(s):
    try:
        return float(s)
    except (TypeError, ValueError):
        return float("nan")


class HistoryWindow(QWidget):
    def __init__(self, path):
        super().__init__()
        meta, hdr, data = load_run(path)
        self.setWindowTitle(f"지난 기록 — {path.name}")
        self.resize(1100, 650)
        samples = [r for r in data if not r.get("이벤트")]
        self.x = [_f(r["경과초"]) for r in samples]
        self.cols = {c: [_f(r.get(c)) for r in samples] for c in hdr if c not in SKIP}

        self.plot = pg.PlotWidget(background="w")
        self.plot.showGrid(x=True, y=True, alpha=0.3)
        self.plot.setLabel("bottom", "경과 시간 (s)")
        self.plot.addLegend()
        # 출탕 구간 음영
        spans = {}
        for r in samples:
            if r.get("출탕구간번호"):
                spans.setdefault(r["출탕구간번호"], []).append(_f(r["경과초"]))
        for no, xs in spans.items():
            self.plot.addItem(pg.LinearRegionItem((min(xs), max(xs)), movable=False,
                                                  brush=pg.mkBrush(255, 165, 0, 50)))
        for r in data:
            if r.get("이벤트"):
                self.plot.addItem(pg.InfiniteLine(_f(r["경과초"]), pen=pg.mkPen("#888", style=Qt.DashLine),
                                                  label=r["이벤트"], labelOpts={"position": 0.95, "color": "#555"}))

        self.list = QListWidget()
        self.list.setFixedWidth(300)
        for c, vals in self.cols.items():
            if all(v != v for v in vals):  # 값이 하나도 없는 열은 뺀다
                continue
            it = QListWidgetItem(c)
            it.setFlags(it.flags() | Qt.ItemIsUserCheckable)
            it.setCheckState(Qt.Checked if c.startswith(DEFAULT) else Qt.Unchecked)
            self.list.addItem(it)
        self.list.itemChanged.connect(self.redraw)
        self.curves = {}

        info = QLabel("   ".join(f"{k}: {v}" for k, v in meta) or "(메타데이터 없음)")
        info.setWordWrap(True)
        body = QHBoxLayout()
        body.addWidget(self.list)
        body.addWidget(self.plot, 1)
        root = QVBoxLayout(self)
        root.addWidget(info)
        root.addLayout(body, 1)
        self.redraw()

    def redraw(self):
        for i in range(self.list.count()):
            it = self.list.item(i)
            c = it.text()
            if it.checkState() == Qt.Checked and c not in self.curves:
                self.curves[c] = self.plot.plot(self.x, self.cols[c], name=c, connect="finite",
                                                pen=pg.mkPen(COLORS[len(self.curves) % len(COLORS)], width=2))
            elif it.checkState() != Qt.Checked and c in self.curves:
                self.plot.removeItem(self.curves.pop(c))
