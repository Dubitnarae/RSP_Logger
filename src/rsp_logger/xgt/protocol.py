"""LS XGT 전용 프로토콜(FEnet, TCP 2004) 프레임 — 읽기 전용.

쓰기(0x0058)·RUN/STOP 등 쓰기·제어 명령은 만들지 않는다 (CLAUDE.md 절대 규칙 1).
프레임 형식은 LS FEnet 매뉴얼 "XGT 전용 프로토콜" 기준. 현장 첫 접속 때 XG5000 모니터 값과 대조할 것.
"""
import re
import struct

COMPANY_ID = b"LSIS-XGT\x00\x00"
SRC_CLIENT = 0x33
SRC_SERVER = 0x11
CMD_READ_REQ = 0x0054
CMD_READ_RESP = 0x0055
TYPE_CONTINUOUS = 0x0014  # 바이트 단위 연속 읽기
MAX_READ_BYTES = 1400

# Company ID(10), PLC Info(2), CPU Info(1), Source(1), Invoke ID(2), Length(2), FEnet 위치(1), BCC(1)
HEADER = struct.Struct("<10sHBBHHBB")
HEADER_SIZE = HEADER.size  # 20


class XgtError(Exception):
    pass


def word_addr(text):
    """'D401' -> ('D', 401)"""
    m = re.fullmatch(r"([A-Z])(\d+)", text.strip().upper())
    if not m:
        raise ValueError(f"워드 주소 형식 오류: {text}")
    return m.group(1), int(m.group(2))


def bit_addr(text):
    """'M382' -> ('M', 38, 2). 마지막 한 자리는 16진 비트 번호, 앞은 워드 번호."""
    m = re.fullmatch(r"([A-Z])(\d*)([0-9A-F])", text.strip().upper())
    if not m:
        raise ValueError(f"비트 주소 형식 오류: {text}")
    return m.group(1), int(m.group(2) or 0), int(m.group(3), 16)


def make_header(length, invoke_id, source, cpu_info=0):
    h = HEADER.pack(COMPANY_ID, 0, cpu_info, source, invoke_id, length, 0, 0)
    return h[:19] + bytes([sum(h[:19]) & 0xFF])


def parse_header(data):
    """-> (source, invoke_id, length)"""
    cid, _plc, _cpu, source, invoke_id, length, _pos, _bcc = HEADER.unpack(data)
    if not cid.startswith(b"LSIS-XGT"):
        raise XgtError(f"XGT 헤더가 아님: {cid!r}")
    return source, invoke_id, length


def build_read_request(area, start_word, word_count, invoke_id):
    """area 영역의 start_word부터 word_count 워드를 한 번에 읽는 요청 프레임."""
    nbytes = word_count * 2
    if not 0 < nbytes <= MAX_READ_BYTES:
        raise ValueError(f"한 번에 읽을 수 있는 워드 수는 1~{MAX_READ_BYTES // 2}: {word_count}")
    name = f"%{area}B{start_word * 2}".encode("ascii")
    body = (struct.pack("<HHHHH", CMD_READ_REQ, TYPE_CONTINUOUS, 0, 1, len(name))
            + name + struct.pack("<H", nbytes))
    return make_header(len(body), invoke_id, SRC_CLIENT) + body


def parse_read_response(body):
    """읽기 응답 본문 -> 부호 없는 16비트 워드 목록."""
    if len(body) < 10:
        raise XgtError("응답이 너무 짧음")
    cmd, _dtype, _res, err, code = struct.unpack_from("<HHHHH", body)
    if cmd != CMD_READ_RESP:
        raise XgtError(f"읽기 응답이 아님: 명령 0x{cmd:04X}")
    if err != 0:
        raise XgtError(f"PLC 오류 응답: 코드 0x{code:04X}")
    (n,) = struct.unpack_from("<H", body, 10)
    data = body[12:12 + n]
    if len(data) != n or n % 2:
        raise XgtError(f"데이터 길이 이상: {n}바이트 예상, {len(data)}바이트 받음")
    return list(struct.unpack(f"<{n // 2}H", data))
