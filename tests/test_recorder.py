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


def rows(p):
    with open(p, encoding="utf-8-sig", newline="") as f:
        return list(csv.reader(f))


class RecorderTest(unittest.TestCase):
    def test_run(self):
        tags = load_tags(CONFIG_DIR / "tags.csv")
        with tempfile.TemporaryDirectory() as d:
            r = Recorder(d, tags, DERIVED)
            ts = 1_800_000_000.0
            p = r.start(ts, {"시료명": "Fe/Si B:1", "작업자": "홍"})
            self.assertTrue(p.name.endswith("_1700_Fe_Si_B_1.csv"), p.name)
            r.add(ts + 1, {"p_upper": 0.25, "dp": 0.4}, {"p_upper": 25})
            r.event(ts + 1.5, "연결 끊김")
            r.add(ts + 2, {"p_upper": 0.5}, {"p_upper": 50})
            self.assertEqual(r.stop(ts + 3, {"외관": "양호"}), p)
            self.assertFalse(r.active)

            self.assertTrue(p.read_bytes().startswith(b"\xef\xbb\xbf"))
            data = rows(p)
            self.assertIn(["# 시료명", "Fe/Si B:1"], data)
            self.assertIn(["# 외관", "양호"], data)
            hdr = next(x for x in data if x[0] == "시각")
            series = [dict(zip(hdr, x)) for x in data[data.index(hdr) + 1:] if not x[0].startswith("#")]
            self.assertEqual([x["이벤트"] for x in series], ["Run 시작", "", "연결 끊김", "", "Run 정지"])
            self.assertEqual(series[1]["상부 챔버 압력(PG1)(bar)"], "0.25")
            self.assertEqual(series[1]["분사 차압 ΔP(bar)"], "0.4")
            self.assertEqual(series[1]["경과초"], "1.000")

            raw = rows(p.with_name(p.stem + "_raw.csv"))
            rh = next(x for x in raw if x[0] == "시각")
            self.assertEqual(dict(zip(rh, raw[raw.index(rh) + 2]))["p_upper"], "25")

            # 같은 분·같은 시료명이면 _2, 인덱스는 두 줄
            r.start(ts, {"시료명": "Fe/Si B:1"})
            p2 = r.stop(ts + 1)
            self.assertTrue(p2.name.endswith("_2.csv"))
            idx = rows(Path(d) / "run_index.csv")
            self.assertEqual(len(idx), 3)
            self.assertEqual(idx[1][4], "2")  # 행수(이벤트 제외)


if __name__ == "__main__":
    unittest.main()
