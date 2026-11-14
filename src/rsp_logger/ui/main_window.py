"""모니터링 화면 (SPEC 9장 중 상단 상태줄·좌측 값 패널·중앙 추세 그래프)."""
from collections import deque

import pyqtgraph as pg
from PySide6.QtCore import QObject, Qt, Signal
from PySide6.QtWidgets import (QCheckBox, QGridLayout, QHBoxLayout, QLabel, QMainWindow,
                               QScrollArea, QVBoxLayout, QWidget)

from .. import APP_NAME, AUTHOR, __version__
from ..core.calc import DERIVED, derived

HISTORY = 3600  # 그래프에 남기는 점 수
DEFAULT_PLOT = {"p_upper", "p_lower", "dp"}
COLORS = ["#1f77b4", "#d62728", "#2ca02c", "#ff7f0e", "#9467bd", "#8c564b", "#e377c2", "#17becf"]


class Bridge(QObject):
    """읽기 스레드 → 화면 스레드 전달."""
    data = Signal(float, dict, dict)
    status = Signal(bool, str)


class MainWindow(QMainWindow):
    def __init__(self, tags, host, wheel_d_mm, dev=False):
        super().__init__()
        self.setWindowTitle(f"{APP_NAME} v{__version__}{'  [개발 모드: 가상 PLC]' if dev else ''}")
        self.resize(1200, 720)
        self.host, self.wheel_d_mm, self.dev = host, wheel_d_mm, dev
        self.bridge = Bridge()
        self.bridge.data.connect(self.on_data)
        self.bridge.status.connect(self.on_status)
        self.t_start = None
        self.connected, self.conn_msg, self.last = False, "접속 중…", {}

        # (key, 이름, 단위, 확인필요) — 비트 태그는 상태줄에만
        rows = [(t.key, t.name, t.unit, t.unconfirmed) for t in tags if t.type != "BIT"]
        rows[0:0] = DERIVED

        self.status_lbl = QLabel()
        self.status_lbl.setStyleSheet("font-size:14px; padding:4px;")

        grid = QGridLayout()
        self.val_lbl, self.chk = {}, {}
        for i, (key, name, unit, unconf) in enumerate(rows):
            cb = QCheckBox(name + (" *" if unconf else ""))
            cb.setChecked(key in DEFAULT_PLOT)
            if unconf:
                cb.setToolTip("환산 배율·식이 현장 확인 전 임시값")
            cb.toggled.connect(self.redraw)
            v = QLabel("—")
            v.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
            v.setStyleSheet("font: bold 15px Consolas;")
            grid.addWidget(cb, i, 0)
            grid.addWidget(v, i, 1)
            grid.addWidget(QLabel(unit), i, 2)
            self.chk[key], self.val_lbl[key] = cb, v
        grid.addWidget(QLabel("* 현장 확인 전 임시 환산\n체크한 항목만 그래프에 표시"), len(rows), 0, 1, 3)
        panel = QWidget()
        panel.setLayout(grid)
        scroll = QScrollArea()
        scroll.setWidget(panel)
        scroll.setWidgetResizable(True)
        scroll.setFixedWidth(380)

        self.plot = pg.PlotWidget(background="w")
        self.plot.showGrid(x=True, y=True, alpha=0.3)
        self.plot.setLabel("bottom", "경과 시간 (s)")
        self.plot.addLegend()
        self.t_hist = deque(maxlen=HISTORY)
        self.hist = {k: deque(maxlen=HISTORY) for k in self.chk}
        self.curves = {}

        body = QHBoxLayout()
        body.addWidget(scroll)
        body.addWidget(self.plot, 1)
        root = QVBoxLayout()
        root.addWidget(self.status_lbl)
        root.addLayout(body, 1)
        footer = QLabel(f"개발자 {AUTHOR}")
        footer.setStyleSheet("color:gray;")
        root.addWidget(footer, 0, Qt.AlignRight)
        w = QWidget()
        w.setLayout(root)
        self.setCentralWidget(w)
        self.update_status()

    def on_status(self, ok, msg):
        self.connected, self.conn_msg = ok, msg
        self.update_status()

    def update_status(self):
        dot = "🟢" if self.connected else "🔴"
        def bit(k):
            return "—" if k not in self.last else ("ON" if self.last[k] else "OFF")
        self.status_lbl.setText(
            f"{dot} {self.conn_msg}   |   PLC {self.host}   |   RSP 리모콘(M382): {bit('rsp_remote')}"
            f"   |   가스 가압(M118): {bit('gas_press_bit')}   |   기록: 대기(모니터링만)")

    def on_data(self, ts, values, _raws):
        values = {**values, **derived(values, self.wheel_d_mm)}
        self.last = values
        if self.t_start is None:
            self.t_start = ts
        self.t_hist.append(ts - self.t_start)
        for k, lbl in self.val_lbl.items():
            v = values.get(k)
            self.hist[k].append(float("nan") if v is None else v)
            lbl.setText("—" if v is None else (f"{v:.2e}" if k in ("vg1", "vg2") else f"{v:,.2f}"))
        self.update_status()
        self.redraw()

    def redraw(self):
        for k, cb in self.chk.items():
            if cb.isChecked():
                if k not in self.curves:
                    self.curves[k] = self.plot.plot(name=cb.text(), pen=pg.mkPen(COLORS[len(self.curves) % len(COLORS)], width=2),
                                                    connect="finite")
                self.curves[k].setData(list(self.t_hist), list(self.hist[k]))
            elif k in self.curves:
                self.plot.removeItem(self.curves.pop(k))
