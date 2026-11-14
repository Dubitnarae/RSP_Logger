"""메인 화면 (SPEC 9장): 상단 상태줄·좌측 값 패널·중앙 추세 그래프·우측 Run 입력과 기록 버튼."""
from collections import deque

import pyqtgraph as pg
from PySide6.QtCore import QObject, Qt, Signal
import time

from PySide6.QtWidgets import (QCheckBox, QDialog, QDialogButtonBox, QFormLayout, QGridLayout, QHBoxLayout,
                               QLabel, QLineEdit, QMainWindow, QMessageBox, QPushButton, QScrollArea,
                               QVBoxLayout, QWidget)

from .. import APP_NAME, AUTHOR, __version__
from ..core.calc import DERIVED, derived
from ..core.recorder import META_FIELDS, RESULT_FIELDS

HISTORY = 3600  # 그래프에 남기는 점 수
DEFAULT_PLOT = {"p_upper", "p_lower", "dp"}
COLORS = ["#1f77b4", "#d62728", "#2ca02c", "#ff7f0e", "#9467bd", "#8c564b", "#e377c2", "#17becf"]


class Bridge(QObject):
    """읽기 스레드 → 화면 스레드 전달."""
    data = Signal(float, dict, dict)
    status = Signal(bool, str)


class MainWindow(QMainWindow):
    def __init__(self, tags, host, wheel_d_mm, recorder, dev=False):
        super().__init__()
        self.setWindowTitle(f"{APP_NAME} v{__version__}{'  [개발 모드: 가상 PLC]' if dev else ''}")
        self.resize(1200, 720)
        self.host, self.wheel_d_mm, self.recorder, self.dev = host, wheel_d_mm, recorder, dev
        self.bridge = Bridge()
        self.bridge.data.connect(self.on_data)
        self.bridge.status.connect(self.on_status)
        self.t_start = None
        self.connected, self.conn_msg, self.last, self.last_ts = False, "접속 중…", {}, 0.0

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

        form = QFormLayout()
        self.meta = {}
        for k in META_FIELDS:
            self.meta[k] = QLineEdit()
            form.addRow(k, self.meta[k])
        if wheel_d_mm:
            self.meta["휠 지름(mm)"].setText(f"{wheel_d_mm:g}")
        self.btn_start = QPushButton("● 기록 시작")
        self.btn_stop = QPushButton("■ 기록 정지")
        self.btn_start.clicked.connect(self.start_run)
        self.btn_stop.clicked.connect(self.stop_run)
        self.btn_stop.setEnabled(False)
        for b in (self.btn_start, self.btn_stop):
            b.setMinimumHeight(36)
        form.addRow(self.btn_start)
        form.addRow(self.btn_stop)
        folder = QLabel(f"저장 폴더:\n{recorder.out_dir}")
        folder.setWordWrap(True)
        form.addRow(folder)
        right = QWidget()
        right.setLayout(form)
        right.setFixedWidth(300)

        body = QHBoxLayout()
        body.addWidget(scroll)
        body.addWidget(self.plot, 1)
        body.addWidget(right)
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
        if self.connected and not ok and self.recorder.active:
            self.recorder.event(time.time(), f"연결 끊김: {msg}")
        self.connected, self.conn_msg = ok, msg
        self.update_status()

    def wheel_d(self):
        """Run 입력의 휠 지름이 숫자면 그것, 아니면 설정값."""
        try:
            return float(self.meta["휠 지름(mm)"].text())
        except ValueError:
            return self.wheel_d_mm

    def start_run(self):
        try:
            path = self.recorder.start(time.time(), {k: e.text() for k, e in self.meta.items()})
        except OSError as e:
            QMessageBox.warning(self, "기록 시작 실패", str(e))
            return
        for e in self.meta.values():
            e.setEnabled(False)
        self.btn_start.setEnabled(False)
        self.btn_stop.setEnabled(True)
        self.statusBar().showMessage(f"기록 중: {path.name}")
        self.update_status()

    def stop_run(self, ask=True):
        results = ResultDialog.ask(self) if ask else {}
        path = self.recorder.stop(time.time(), results)
        for e in self.meta.values():
            e.setEnabled(True)
        self.btn_start.setEnabled(True)
        self.btn_stop.setEnabled(False)
        if path:
            self.statusBar().showMessage(f"저장됨: {path}")
        self.update_status()

    def closeEvent(self, e):
        if self.recorder.active:
            self.stop_run(ask=False)  # 닫을 때도 파일을 닫아 기록을 보존
        super().closeEvent(e)

    def update_status(self):
        dot = "🟢" if self.connected else "🔴"
        r = self.recorder
        rec = f"🔴 기록 중 {self.last_ts - r.t0:.0f}초 ({r.rows}행)" if r.active else "대기(모니터링만)"
        def bit(k):
            return "—" if k not in self.last else ("ON" if self.last[k] else "OFF")
        self.status_lbl.setText(
            f"{dot} {self.conn_msg}   |   PLC {self.host}   |   RSP 리모콘(M382): {bit('rsp_remote')}"
            f"   |   가스 가압(M118): {bit('gas_press_bit')}   |   기록: {rec}")

    def on_data(self, ts, values, raws):
        values = {**values, **derived(values, self.wheel_d())}
        self.last, self.last_ts = values, ts
        self.recorder.add(ts, values, raws)
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


class ResultDialog(QDialog):
    """Run 정지 후 결과 입력. 모두 선택 입력이라 비워 두고 저장해도 된다."""
    def __init__(self, parent):
        super().__init__(parent)
        self.setWindowTitle("Run 결과 입력 (비워 둬도 됨)")
        form = QFormLayout(self)
        self.edits = {k: QLineEdit() for k in RESULT_FIELDS}
        for k, e in self.edits.items():
            form.addRow(k, e)
        bb = QDialogButtonBox(QDialogButtonBox.Save)
        bb.accepted.connect(self.accept)
        form.addRow(bb)

    @classmethod
    def ask(cls, parent):
        d = cls(parent)
        d.exec()  # 창을 그냥 닫아도 입력한 만큼 저장 (기록은 이미 정지됨)
        return {k: e.text() for k, e in d.edits.items()}
