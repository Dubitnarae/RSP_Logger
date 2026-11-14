"""가상 PLC — XGT 연속 읽기(0x0054/0x0014)에만 응답하는 TCP 서버. 다른 명령은 오류로 돌려준다.

실행: python -m rsp_logger.sim.fake_plc  (127.0.0.1:2004)
"""
import math
import random
import re
import socketserver
import struct
import threading
import time
from collections import defaultdict

from ..xgt import protocol as p

ERR_UNSUPPORTED = 0x0003  # 시뮬레이터용 임의 코드


class _Handler(socketserver.BaseRequestHandler):
    def _recv_exact(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.request.recv(n - len(buf))
            if not chunk:
                return None
            buf += chunk
        return buf

    def handle(self):
        while True:
            head = self._recv_exact(p.HEADER_SIZE)
            if head is None:
                return
            _src, invoke, length = p.parse_header(head)
            body = self._recv_exact(length)
            if body is None:
                return
            out = self.server.respond(body)
            self.request.sendall(p.make_header(len(out), invoke, p.SRC_SERVER, cpu_info=0xB0) + out)


class FakePlc(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, host="127.0.0.1", port=2004):
        super().__init__((host, port), _Handler)
        self.mem = defaultdict(lambda: bytearray(0x20000))  # 영역별 바이트 메모리

    def set_word(self, addr, value):
        area, w = p.word_addr(addr)
        struct.pack_into("<H", self.mem[area], w * 2, value & 0xFFFF)

    def set_bit(self, addr, on):
        area, w, b = p.bit_addr(addr)
        (v,) = struct.unpack_from("<H", self.mem[area], w * 2)
        v = v | (1 << b) if on else v & ~(1 << b)
        struct.pack_into("<H", self.mem[area], w * 2, v)

    def respond(self, body):
        cmd, dtype, _res, blocks, nlen = struct.unpack_from("<HHHHH", body)
        name = body[10:10 + nlen].decode("ascii", "replace")
        m = re.fullmatch(r"%([A-Z])B(\d+)", name)
        if cmd != p.CMD_READ_REQ or dtype != p.TYPE_CONTINUOUS or blocks != 1 or not m:
            return struct.pack("<HHHHH", cmd + 1, dtype, 0, 0xFFFF, ERR_UNSUPPORTED)
        (n,) = struct.unpack_from("<H", body, 10 + nlen)
        start = int(m.group(2))
        data = bytes(self.mem[m.group(1)][start:start + n])
        return struct.pack("<HHHHHH", p.CMD_READ_RESP, dtype, 0, 0, 1, n) + data


    def start_animation(self, cycle=30.0, eject_s=5.0):
        """개발 모드용: 공정처럼 움직이는 원시값. cycle초마다 eject_s초 동안 출탕(M118 ON, 상부 압력 상승)."""
        def loop():
            t0 = time.monotonic()
            while True:
                t = time.monotonic() - t0
                ej = (t % cycle) > cycle - eject_s
                n = lambda a: random.uniform(-a, a)  # noqa: E731
                self.set_word("D401", int((50 if ej else 20) + n(1)))
                self.set_word("D402", int(n(1)))
                self.set_word("D3185", int(350 + n(5)))
                self.set_word("D3190", -3)
                self.set_word("D5185", int(120 + n(3)))
                self.set_word("D5190", -2)
                self.set_word("D201", int(250 + n(2)))
                self.set_word("D202", int(240 + n(2)))
                self.set_word("D203", int(8000 + n(20)))
                self.set_word("D204", int((300 if ej else 220) + n(3)))
                self.set_word("D221", int(1400 + 20 * math.sin(t / 10) + n(2)))
                self.set_word("D385", 14000)
                self.set_word("D324", int(1500 + n(5)))
                self.set_word("D888", int(400 + n(3)))
                self.set_word("D892", int(15 + n(1)))
                self.set_bit("M118", ej)
                self.set_bit("M382", True)
                time.sleep(0.2)
        threading.Thread(target=loop, daemon=True).start()


if __name__ == "__main__":
    plc = FakePlc()
    plc.start_animation()
    print("가상 PLC 실행 중: 127.0.0.1:2004 (Ctrl+C로 종료)")
    try:
        plc.serve_forever()
    except KeyboardInterrupt:
        pass
