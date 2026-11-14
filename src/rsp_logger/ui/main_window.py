"""메인 화면 (SPEC 9장): 상단 상태줄·좌측 값 패널·중앙 추세 그래프·우측 Run 입력과 기록 버튼."""
import threading
import time
from collections import deque
from pathlib import Path

# PySide6 를 pyqtgraph(numpy) 보다 먼저 불러온다 — 순서가 바뀌면 이 PC에서 QtCore DLL 로드 실패
from PySide6.QtCore import QObject, Qt, Signal
from PySide6.QtWidgets import (QApplication, QCheckBox, QDialog, QDialogButtonBox, QFileDialog, QFormLayout,
                               QGridLayout, QHBoxLayout, QLabel, QLineEdit, QMainWindow, QMessageBox,
                               QPushButton, QScrollArea, QVBoxLayout, QWidget)
import pyqtgraph as pg  # noqa: E402,I001

from .. import APP_NAME, AUTHOR, CONFIG_DIR, __version__, updater
from ..core.calc import DERIVED, derived
from ..core.recorder import META_FIELDS, RESULT_FIELDS
from .tag_editor import TagEditor

HISTORY = 3600  # 그래프에 남기는 점 수
DEFAULT_PLOT = {"p_upper", "p_lower", "dp"}
COLORS = ["#1f77b4", "#d62728", "#2ca02c", "#ff7f0e", "#9467bd", "#8c564b", "#e377c2", "#17becf"]


class Bridge(QObject):
    """읽기 스레드 → 화면 스레드 전달."""
    data = Signal(float, dict, dict)
    status = Signal(bool, str)
    update = Signal(object)


class MainWindow(QMainWindow):
    def __init__(self, tags, host, wheel_d_mm, recorder, dev=False):
        super().__init__()
        self.setWindowTitle(f"{APP_NAME} v{__version__}{'  [개발 모드: 가상 PLC]' if dev else ''}")
        self.resize(1200, 720)
        self.host, self.wheel_d_mm, self.recorder, self.dev = host, wheel_d_mm, recorder, dev
        self.restart_dev = None  # 다시 시작할 때 True(개발)/False(현장)
        self.tag_editor = None
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
        self.regions = {}  # 출탕번호 -> 그래프 음영
        self.poller = None  # __main__ 에서 연결 (Run 중 고속 읽기 전환용)
        self.fast_interval = 0.2

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
        self.btn_ej_start = QPushButton("▲ 출탕 시작 (수동)")
        self.btn_ej_end = QPushButton("▼ 출탕 종료")
        self.btn_ej_start.clicked.connect(lambda: self.recorder.eject_start(self.last_ts or time.time()))
        self.btn_ej_end.clicked.connect(lambda: self.recorder.eject_end(self.last_ts or time.time()))
        for b in (self.btn_start, self.btn_stop, self.btn_ej_start, self.btn_ej_end):
            b.setMinimumHeight(36)
            form.addRow(b)
        self.btn_ej_end.setStyleSheet("font-weight:bold;")
        form.addRow(QLabel("출탕 시작: M151 자동 감지 / 종료: 버튼"))
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
        menu = self.menuBar().addMenu("파일")
        menu.addAction("지난 기록 열기…", self.open_history)
        menu.addAction("태그 설정 · 환산 확인…", self.open_tag_editor)
        menu.addSeparator()
        menu.addAction("현장 모드로 다시 시작" if dev else "개발 모드(가상 PLC)로 다시 시작", self.switch_mode)
        help_menu = self.menuBar().addMenu("도움말")
        help_menu.addAction("프로그램 정보", lambda: QMessageBox.about(
            self, APP_NAME, f"{APP_NAME} v{__version__}\n개발자 {AUTHOR}\n\nPLC 공정값 읽기 전용 기록기"))
        self.history_windows = []
        self.bridge.update.connect(self.on_update)

        self.sync_buttons()
        self.update_status()

    # ---------- 지난 기록 / 업데이트 ----------
    def open_history(self):
        path, _ = QFileDialog.getOpenFileName(self, "지난 기록 열기", str(self.recorder.out_dir), "CSV (*.csv)")
        if not path:
            return
        from .history import HistoryWindow
        try:
            w = HistoryWindow(Path(path))
        except (OSError, ValueError) as e:
            QMessageBox.warning(self, "열 수 없음", str(e))
            return
        self.history_windows.append(w)
        w.show()

    def check_update(self):
        """exe 로 실행할 때만, 시작 시 한 번 백그라운드로 확인."""
        updater.cleanup()
        def run():
            try:
                self.bridge.update.emit(updater.check())
            except Exception:
                pass  # 인터넷이 없는 현장 PC 등 — 조용히 넘어간다
        threading.Thread(target=run, daemon=True).start()

    def on_update(self, found):
        if not found or self.recorder.active:  # 기록 중에는 묻지 않는다
            return
        ver, exe_url, sha_url, notes = found
        msg = f"새 버전 v{ver} 이 있습니다. 지금 설치할까요?\n\n{notes[:800]}"
        if QMessageBox.question(self, "업데이트", msg) != QMessageBox.Yes:
            return
        QApplication.setOverrideCursor(Qt.WaitCursor)
        try:
            updater.install(exe_url, sha_url)
        except Exception as e:
            QApplication.restoreOverrideCursor()
            QMessageBox.warning(self, "업데이트 실패", str(e))
            return
        QApplication.quit()

    def sync_buttons(self):
        r = self.recorder
        for e in self.meta.values():
            e.setEnabled(not r.active)
        self.btn_start.setEnabled(not r.active)
        self.btn_stop.setEnabled(r.active)
        self.btn_ej_start.setEnabled(r.active and not r.ejecting)
        self.btn_ej_end.setEnabled(r.ejecting)

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
        if not self.meta["시료명"].text().strip():
            QMessageBox.information(self, "시료명 필요", "시료명을 입력해야 기록을 시작할 수 있습니다.")
            self.meta["시료명"].setFocus()
            return
        try:
            path = self.recorder.start(time.time(), {k: e.text() for k, e in self.meta.items()})
        except OSError as e:
            QMessageBox.warning(self, "기록 시작 실패", str(e))
            return
        for reg in self.regions.values():
            self.plot.removeItem(reg)
        self.regions.clear()
        if self.poller:
            self.poller.set_interval(self.fast_interval)
        self.sync_buttons()
        self.statusBar().showMessage(f"기록 중: {path.name}")
        self.update_status()

    def stop_run(self, ask=True):
        results = ResultDialog.ask(self) if ask else {}
        path = self.recorder.stop(time.time(), results)
        if self.poller:
            self.poller.set_interval(self.poller.base_interval)
        self.sync_buttons()
        if path:
            self.statusBar().showMessage(f"저장됨: {path}")
        self.update_status()

    def switch_mode(self):
        target = "현장 모드" if self.dev else "개발 모드(가상 PLC)"
        self.restart(not self.dev, "모드 전환", f"{target}로 다시 시작합니다.")

    def restart(self, dev, title, msg):
        """실제 재시작은 창이 닫힌 뒤 __main__ 이 한다."""
        if self.recorder.active:
            msg += "\n지금 기록 중인 Run은 정지·저장됩니다."
        if QMessageBox.question(self, title, msg) != QMessageBox.Yes:
            return
        self.restart_dev = dev
        self.close()

    def open_tag_editor(self):
        if self.tag_editor is None:
            self.tag_editor = TagEditor(self, CONFIG_DIR / "tags.csv", lambda: self.restart(
                self.dev, "태그 저장됨", "tags.csv 를 저장했습니다. 다시 시작해야 반영됩니다. 지금 다시 시작할까요?"))
        self.tag_editor.show()
        self.tag_editor.raise_()

    def closeEvent(self, e):
        if self.recorder.active:
            self.stop_run(ask=False)  # 닫을 때도 파일을 닫아 기록을 보존
        super().closeEvent(e)

    def update_status(self):
        dot = "🟢" if self.connected else "🔴"
        r = self.recorder
        rec = f"🔴 기록 중 {self.last_ts - r.t0:.0f}초" if r.active else "대기(모니터링만)"
        if r.ejecting:
            e = r.ejections[-1]
            rec += f"   |   🟧 출탕 {e['no']} 진행 중 {self.last_ts - e['start']:.1f}초"
        elif r.active:
            rec += f"   |   출탕 {len(r.ejections)}회"
        def bit(k):
            return "—" if k not in self.last else ("ON" if self.last[k] else "OFF")
        self.status_lbl.setText(
            f"{dot} {self.conn_msg}   |   PLC {self.host}   |   RSP 리모콘(M382): {bit('rsp_remote')}"
            f"   |   스토퍼 상승(M151): {bit('stopper_up')}   |   기록: {rec}")

    def on_data(self, ts, values, raws):
        values = {**values, **derived(values, self.wheel_d())}
        self.last, self.last_ts = values, ts
        self.recorder.add(ts, values, raws)
        if self.tag_editor and self.tag_editor.isVisible():
            self.tag_editor.set_raws(raws)
        if self.t_start is None:
            self.t_start = ts
        self.t_hist.append(ts - self.t_start)
        for k, lbl in self.val_lbl.items():
            v = values.get(k)
            self.hist[k].append(float("nan") if v is None else v)
            lbl.setText("—" if v is None else (f"{v:.2e}" if k in ("vg1", "vg2") else f"{v:,.2f}"))
            if k in raws:
                lbl.setToolTip(f"PLC 원시값 {raws[k]}  (HMI 값과 비교해 배율 확인용)")
        self.sync_regions()
        self.sync_buttons()
        self.update_status()
        self.redraw()

    def sync_regions(self):
        """출탕 구간을 그래프에 주황 음영으로 표시."""
        for e in self.recorder.ejections:
            x0 = e["start"] - self.t_start
            x1 = (e["end"] or self.last_ts) - self.t_start
            reg = self.regions.get(e["no"])
            if reg is None:
                reg = pg.LinearRegionItem((x0, x1), movable=False, brush=pg.mkBrush(255, 165, 0, 50))
                self.plot.addItem(reg)
                self.regions[e["no"]] = reg
            reg.setRegion((x0, x1))

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
