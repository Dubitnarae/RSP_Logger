import sys
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from rsp_logger import CONFIG_DIR  # noqa: E402
from rsp_logger.core.calc import derived  # noqa: E402
from rsp_logger.core.poller import Poller  # noqa: E402
from rsp_logger.core.tags import decode, load_tags, plan_blocks, save_tags_csv, suggest_scale  # noqa: E402
from rsp_logger.sim.fake_plc import FakePlc  # noqa: E402
from rsp_logger.xgt.client import XgtClient  # noqa: E402


class TagsTest(unittest.TestCase):
    def setUp(self):
        self.tags = load_tags(CONFIG_DIR / "tags.csv")

    def test_suggest_scale(self):
        self.assertEqual(suggest_scale(25, 0.25), 0.01)
        self.assertEqual(suggest_scale(-15, -0.15), 0.01)
        self.assertEqual(suggest_scale(500, 30, offset=-20), 0.1)
        self.assertIsNone(suggest_scale(0, 1.0))

    def test_save_tags_csv(self):
        import csv
        import tempfile
        src = CONFIG_DIR / "tags.csv"
        with open(src, encoding="utf-8-sig", newline="") as f:
            r = csv.DictReader(f)
            header, rows = r.fieldnames, list(r)
        path = Path(tempfile.mkdtemp()) / "tags.csv"
        rows[0]["배율"] = "0.1"
        save_tags_csv(path, header, rows)
        self.assertEqual(load_tags(path)[0].scale, 0.1)
        before = path.read_bytes()
        rows[0]["배율"] = "abc"
        with self.assertRaises(ValueError):
            save_tags_csv(path, header, rows)
        self.assertEqual(path.read_bytes(), before)  # 실패하면 원래 파일 유지
        self.assertFalse(path.with_suffix(".tmp").exists())

    def test_blocks_cover_all(self):
        blocks = plan_blocks(self.tags)
        covered = {(a, s + i) for a, s, n in blocks for i in range(n)}
        for t in self.tags:
            self.assertIn((t.area, t.word), covered, t.key)
            if t.type == "INT32":
                self.assertIn((t.area, t.word + 1), covered, t.key)
        self.assertTrue(all(n <= 700 for _a, _s, n in blocks))
        self.assertIn(("D", 3185, 6), blocks)  # 진공도는 따로 묶임

    def test_decode(self):
        tags = [t for t in self.tags if t.key in ("p_lower", "pos_fb", "rsp_remote")]
        blocks = [("D", 380, 23), ("M", 38, 1)]
        d = [0] * 23
        d[0], d[1] = 0xFFFE, 0xFFFF  # D380~381 = -2 (INT32)
        d[22] = (-15) & 0xFFFF       # D402
        v, raw = decode(tags, blocks, [d, [0b100]])
        self.assertAlmostEqual(v["p_lower"], -0.15)
        self.assertEqual(raw["p_lower"], -15)
        self.assertEqual(v["pos_fb"], -2)
        self.assertEqual(v["rsp_remote"], 1)

    def test_derived(self):
        v = {"wheel_rpm": 1500, "p_upper": 0.5, "p_lower": 0.1,
             "vg1_mant": 3.5, "vg1_exp": -3, "vg2_mant": 1.2, "vg2_exp": -2}
        d = derived(v, 250)
        self.assertAlmostEqual(d["wheel_v"], 19.635, places=3)
        self.assertAlmostEqual(d["dp"], 0.4)
        self.assertAlmostEqual(d["vg1"], 3.5e-3)
        self.assertIsNone(derived(v, 0)["wheel_v"])


class PollerTest(unittest.TestCase):
    def test_poll(self):
        tags = load_tags(CONFIG_DIR / "tags.csv")
        got, status = [], []
        plc = FakePlc(port=0)
        port = plc.server_address[1]
        plc.set_word("D401", 25)
        poller = Poller(XgtClient("127.0.0.1", port, 0.5), tags, 0.1,
                        lambda ts, v, r: got.append(v), lambda ok, m: status.append(ok))
        threading.Thread(target=plc.serve_forever, daemon=True).start()
        poller.start()
        for _ in range(50):
            if len(got) >= 3:
                break
            threading.Event().wait(0.1)
        poller.stop()
        plc.shutdown()
        plc.server_close()
        self.assertGreaterEqual(len(got), 3)
        self.assertAlmostEqual(got[0]["p_upper"], 0.25)
        self.assertTrue(status[-1])

    def test_backoff_when_down(self):
        status = []
        poller = Poller(XgtClient("127.0.0.1", 1, 0.2), load_tags(CONFIG_DIR / "tags.csv"), 1,
                        lambda *a: None, lambda ok, m: status.append((ok, m)))
        poller.start()
        threading.Event().wait(0.5)
        poller.stop()
        self.assertFalse(status[0][0])
        self.assertIn("1초 뒤 재접속", status[0][1])


if __name__ == "__main__":
    unittest.main()
