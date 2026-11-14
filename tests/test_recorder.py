import csv
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from rsp_logger import CONFIG_DIR  # noqa: E402
from rsp_logger.core.calc import DERIVED  # noqa: E402
from rsp_logger.core.recorder import Recorder  # noqa: E402
from rsp_logger.core.tags import load_tags  # noqa: E402

T = 1_800_000_000.0


def rows(p):
    with open(p, encoding="utf-8-sig", newline="") as f:
        return list(csv.reader(f))


def series(p):
    data = rows(p)
    hdr = next(x for x in data if x[0] == "시각")
    return [dict(zip(hdr, x)) for x in data[data.index(hdr) + 1:] if not x[0].startswith("#")]


class RecorderTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.r = Recorder(self.tmp.name, load_tags(CONFIG_DIR / "tags.csv"), DERIVED)

    def tearDown(self):
        if self.r.active:
            self.r.stop(T + 999)
        self.tmp.cleanup()

    def run_scenario(self):
        r = self.r
        p = r.start(T, {"시료명": "Fe/Si B:1"})
        for i in range(1, 301):  # 0.2초 간격 60초
            t = T + i * 0.2
            ej = 20 <= i * 0.2
            v = {"p_upper": 0.5 if ej else 0.2, "stopper_up": 1 if ej else 0,
                 "t_wheel_out": 30.0 if 20 <= i * 0.2 <= 25 else 22.0, "dp": 0.4}
            r.add(t, v, {"p_upper": int(v["p_upper"] * 100)})
            if i == 125:
                r.eject_end(t)                 # 25초 수동 종료
            if i == 150:
                r.event(t, "연결 끊김")
            if i == 285:
                self.assertEqual(r.eject_start(t), 2)  # 57초 수동 시작
            if i == 290:
                r.eject_end(t)
        return p, r.stop(T + 60, {"외관": "양호"})

    def test_ejection_recording(self):
        p, p2 = self.run_scenario()
        self.assertEqual(p, p2)
        self.assertTrue(p.name.endswith("_Fe_Si_B_1.csv"), p.name)
        s = series(p)
        data = [x for x in s if not x["이벤트"]]
        ts = [float(x["경과초"]) for x in data]
        self.assertEqual(ts, sorted(ts))
        # 출탕 창 [10, 55] 안은 0.2초 전부, 밖은 약 1초 간격
        inside = [t for t in ts if 10 <= t <= 55]
        self.assertEqual(len(inside), 226)
        outside = [t for t in ts if t < 10]
        self.assertTrue(all(0.9 < b - a < 1.1 for a, b in zip(outside, outside[1:])), outside)
        no1 = [float(x["경과초"]) for x in data if x["출탕구간번호"] == "1"]
        self.assertEqual((no1[0], no1[-1]), (20.0, 25.0))
        self.assertTrue(any(x["출탕구간번호"] == "2" for x in data))
        evs = [x["이벤트"] for x in s if x["이벤트"]]
        self.assertEqual(evs, ["Run 시작", "출탕 1 시작(자동(M151))", "출탕 1 종료", "연결 끊김",
                               "출탕 2 시작(수동)", "출탕 2 종료", "Run 정지"])

        all_rows = rows(p)
        self.assertIn(["# 외관", "양호"], all_rows)
        hdr = next(x for x in all_rows if x[0] == "# 출탕 요약")
        first = dict(zip(hdr[1:], all_rows[all_rows.index(hdr) + 1][1:]))
        self.assertEqual(first["출탕 시간(s)"], "5.0")
        self.assertEqual(first["상부 챔버 압력(PG1) 시작"], "0.5")
        self.assertEqual(first["휠 출수온도 상승폭"], "8")

        idx = rows(Path(self.tmp.name) / "run_index.csv")
        self.assertEqual(len(idx), 3)  # 머리줄 + 출탕 2줄
        self.assertEqual(idx[2][idx[0].index("출탕번호")], "2")

        raw = series(p.with_name(p.stem + "_raw.csv"))
        self.assertEqual(raw[1]["p_upper"], "20")

    def test_no_ejection_and_dup_name(self):
        self.r.start(T, {"시료명": "A"})
        self.r.add(T + 1, {"stopper_up": 1}, {})  # 처음부터 켜져 있으면 상승이 아님
        self.assertFalse(self.r.ejections)
        p1 = self.r.stop(T + 2)
        self.r.start(T, {"시료명": "A"})
        p2 = self.r.stop(T + 1)
        self.assertTrue(p2.name.endswith("_A_2.csv"))
        self.assertNotEqual(p1, p2)
        idx = rows(Path(self.tmp.name) / "run_index.csv")
        self.assertEqual(len(idx), 3)

    def test_index_header_change_keeps_old(self):
        old = Path(self.tmp.name) / "run_index.csv"
        old.write_text("파일,옛열\nx,y\n", encoding="utf-8-sig")
        self.r.start(T, {"시료명": "A"})
        self.r.stop(T + 1)
        kept = list(Path(self.tmp.name).glob("run_index_이전_*.csv"))
        self.assertEqual(len(kept), 1)
        self.assertEqual(rows(old)[0][0], "파일")
        self.assertEqual(len(rows(old)), 2)


if __name__ == "__main__":
    unittest.main()
