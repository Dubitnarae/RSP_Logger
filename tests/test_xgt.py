import socket
import struct
import sys
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from rsp_logger.sim.fake_plc import FakePlc  # noqa: E402
from rsp_logger.xgt import protocol as p  # noqa: E402
from rsp_logger.xgt.client import XgtClient  # noqa: E402


class AddressTest(unittest.TestCase):
    def test_parse(self):
        self.assertEqual(p.word_addr("D401"), ("D", 401))
        self.assertEqual(p.bit_addr("M382"), ("M", 38, 2))
        self.assertEqual(p.bit_addr("M118"), ("M", 11, 8))
        self.assertEqual(p.bit_addr("P42"), ("P", 4, 2))
        self.assertEqual(p.bit_addr("M38F"), ("M", 38, 15))
        self.assertRaises(ValueError, p.word_addr, "%DW1")


class FrameTest(unittest.TestCase):
    def test_read_request_bytes(self):
        f = p.build_read_request("D", 400, 2, invoke_id=7)
        name = b"%DB800"
        body = struct.pack("<HHHHH", 0x54, 0x14, 0, 1, len(name)) + name + struct.pack("<H", 4)
        self.assertEqual(f[:10], b"LSIS-XGT\x00\x00")
        self.assertEqual(f[13], 0x33)
        self.assertEqual(struct.unpack_from("<HH", f, 14), (7, len(body)))
        self.assertEqual(f[19], sum(f[:19]) & 0xFF)
        self.assertEqual(f[20:], body)

    def test_error_response(self):
        body = struct.pack("<HHHHH", 0x55, 0x14, 0, 0xFFFF, 0x0012)
        with self.assertRaisesRegex(p.XgtError, "0x0012"):
            p.parse_read_response(body)

    def test_size_limit(self):
        self.assertRaises(ValueError, p.build_read_request, "D", 0, 701, 1)

    def test_no_write_command(self):
        # 절대 규칙 1: 쓰기 함수·쓰기 명령 상수가 없어야 한다
        names = dir(p) + dir(XgtClient)
        self.assertFalse([n for n in names if "write" in n.lower()])
        self.assertNotIn(0x58, [v for v in vars(p).values() if isinstance(v, int)])


class SimRoundTripTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.plc = FakePlc(port=0)
        threading.Thread(target=cls.plc.serve_forever, daemon=True).start()
        cls.port = cls.plc.server_address[1]

    @classmethod
    def tearDownClass(cls):
        cls.plc.shutdown()
        cls.plc.server_close()

    def test_read(self):
        self.plc.set_word("D401", 25)
        self.plc.set_word("D402", (-15) & 0xFFFF)
        self.plc.set_bit("M382", True)
        c = XgtClient("127.0.0.1", self.port)
        try:
            self.assertEqual(c.read_words("D", 401, 2), [25, 0xFFF1])
            self.assertEqual(c.read_bit("M382"), 1)
            self.assertEqual(c.read_bit("M383"), 0)
            self.assertEqual(c.read_word("D5190"), 0)
        finally:
            c.close()

    def test_sim_rejects_write(self):
        # 시뮬레이터가 읽기 외 명령(예: 0x0058)을 오류로 돌려주는지
        name = b"%DB0"
        body = struct.pack("<HHHHH", 0x58, 0x14, 0, 1, len(name)) + name + struct.pack("<HH", 2, 0)
        with socket.create_connection(("127.0.0.1", self.port), 2) as s:
            s.sendall(p.make_header(len(body), 1, p.SRC_CLIENT) + body)
            _src, _inv, n = p.parse_header(s.recv(20))
            resp = s.recv(n)
        self.assertEqual(struct.unpack_from("<H", resp, 6)[0], 0xFFFF)
        self.assertEqual(self.plc.mem["D"][0:2], b"\x00\x00")


if __name__ == "__main__":
    unittest.main()
