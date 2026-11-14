"""태그 설정·환산 확인 화면 (SPEC 9장). tags.csv 를 표로 고치고, HMI 표시값을 넣으면 배율을 제안한다.
저장하면 다시 시작해야 반영된다 (읽기 묶음이 시작 때 정해지므로)."""
import csv

from PySide6.QtCore import Qt
from PySide6.QtGui import QColor
from PySide6.QtWidgets import (QDialog, QHBoxLayout, QLabel, QMessageBox, QPushButton, QTableWidget,
                               QTableWidgetItem, QVBoxLayout)

from ..core.tags import suggest_scale, save_tags_csv

LIVE = ["PLC 원시값", "환산값(표 배율)", "HMI 값 입력"]


class TagEditor(QDialog):
    def __init__(self, parent, path, on_saved):
        super().__init__(parent)
        self.setWindowTitle("태그 설정 · 환산 확인")
        self.resize(1250, 700)
        self.path, self.on_saved = path, on_saved
        with open(path, encoding="utf-8-sig", newline="") as f:
            r = csv.DictReader(f)
            self.header, rows = r.fieldnames, list(r)
        self.col = {h: i for i, h in enumerate(self.header)}
        n = len(self.header)
        self.c_raw, self.c_val, self.c_hmi = n, n + 1, n + 2
        self.raws = {}

        t = self.table = QTableWidget(len(rows), n + len(LIVE))
        t.setHorizontalHeaderLabels(self.header + LIVE)
        for i, row in enumerate(rows):
            for h, j in self.col.items():
                t.setItem(i, j, QTableWidgetItem(row[h]))
            t.item(i, self.col["key"]).setFlags(Qt.ItemIsEnabled)  # key 는 기록 파일 열 이름이라 못 바꾼다
            for j in (self.c_raw, self.c_val):
                it = QTableWidgetItem("—")
                it.setFlags(Qt.ItemIsEnabled)
                it.setBackground(QColor("#f0f0f0"))
                t.setItem(i, j, it)
            t.setItem(i, self.c_hmi, QTableWidgetItem(""))
        t.resizeColumnsToContents()

        guide = QLabel("① 현장 HMI 화면의 값을 'HMI 값 입력' 칸에 적는다  ② [배율 제안]을 누르면 배율 칸이 (HMI−오프셋)÷원시값으로 바뀐다"
                       "  ③ 맞으면 '확인필요'를 N 으로  ④ [저장 후 다시 시작]. 원시값은 1초마다 새로 읽힌다.")
        guide.setWordWrap(True)
        b_suggest = QPushButton("배율 제안 (HMI 값 넣은 줄만)")
        b_suggest.clicked.connect(self.suggest)
        b_save = QPushButton("저장 후 다시 시작")
        b_save.clicked.connect(self.save)
        b_close = QPushButton("닫기 (저장 안 함)")
        b_close.clicked.connect(self.close)
        btns = QHBoxLayout()
        btns.addWidget(b_suggest)
        btns.addStretch(1)
        btns.addWidget(b_save)
        btns.addWidget(b_close)
        lay = QVBoxLayout(self)
        lay.addWidget(guide)
        lay.addWidget(t, 1)
        lay.addLayout(btns)

    def _num(self, i, h):
        return float(self.table.item(i, self.col[h]).text())

    def set_raws(self, raws):
        """메인 창이 새 값을 읽을 때마다 부른다."""
        self.raws = raws
        for i in range(self.table.rowCount()):
            raw = raws.get(self.table.item(i, self.col["key"]).text())
            self.table.item(i, self.c_raw).setText("—" if raw is None else str(raw))
            try:
                val = "—" if raw is None else f"{raw * self._num(i, '배율') + self._num(i, '오프셋'):.6g}"
            except ValueError:
                val = "배율/오프셋 숫자 아님"
            self.table.item(i, self.c_val).setText(val)

    def suggest(self):
        done, skipped = 0, []
        for i in range(self.table.rowCount()):
            key = self.table.item(i, self.col["key"]).text()
            hmi = self.table.item(i, self.c_hmi).text().strip()
            if not hmi:
                continue
            try:
                s = suggest_scale(self.raws[key], float(hmi), self._num(i, "오프셋"))
            except (KeyError, ValueError):
                s = None
            if s is None:
                skipped.append(key)  # 원시값 없음·0, 숫자 아님
                continue
            self.table.item(i, self.col["배율"]).setText(str(s))
            done += 1
        self.set_raws(self.raws)
        msg = f"{done}줄 배율을 바꿨습니다."
        if skipped:
            msg += "\n계산 못 한 줄(원시값 0·없음 또는 숫자 아님): " + ", ".join(skipped)
        QMessageBox.information(self, "배율 제안", msg)

    def save(self):
        rows = [{h: self.table.item(i, j).text().strip() for h, j in self.col.items()}
                for i in range(self.table.rowCount())]
        try:
            save_tags_csv(self.path, self.header, rows)
        except ValueError as e:
            QMessageBox.warning(self, "저장 안 됨", str(e))
            return
        self.close()
        self.on_saved()
