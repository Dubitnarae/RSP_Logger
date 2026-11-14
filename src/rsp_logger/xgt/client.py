"""XGT 클라이언트 — 소켓 1개, 읽기만."""
import socket

from . import protocol as p


class XgtClient:
    def __init__(self, host, port=2004, timeout=2.0):
        self.host, self.port, self.timeout = host, port, timeout
        self.sock = None
        self._invoke = 0

    def connect(self):
        self.close()
        self.sock = socket.create_connection((self.host, self.port), self.timeout)
        self.sock.settimeout(self.timeout)

    def close(self):
        if self.sock:
            try:
                self.sock.close()
            finally:
                self.sock = None

    def _recv_exact(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.sock.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("PLC가 연결을 끊음")
            buf += chunk
        return buf

    def read_words(self, area, start, count):
        """끊기면 소켓을 닫고 예외를 올린다. 재접속은 호출하는 쪽(폴링 루프)이 한다."""
        if not self.sock:
            self.connect()
        self._invoke = (self._invoke + 1) & 0xFFFF
        try:
            self.sock.sendall(p.build_read_request(area, start, count, self._invoke))
            source, invoke, length = p.parse_header(self._recv_exact(p.HEADER_SIZE))
            body = self._recv_exact(length)
        except OSError:
            self.close()
            raise
        if source != p.SRC_SERVER or invoke != self._invoke:
            self.close()  # 응답이 어긋나면 다음 요청과 섞이지 않게 새로 연결
            raise p.XgtError(f"응답 불일치: source 0x{source:02X}, invoke {invoke}≠{self._invoke}")
        words = p.parse_read_response(body)
        if len(words) != count:
            raise p.XgtError(f"{count}워드 요청, {len(words)}워드 받음")
        return words

    def read_word(self, addr):
        area, w = p.word_addr(addr)
        return self.read_words(area, w, 1)[0]

    def read_bit(self, addr):
        area, w, b = p.bit_addr(addr)
        return (self.read_words(area, w, 1)[0] >> b) & 1
