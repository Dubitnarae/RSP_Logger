"""현장 첫 접속용 연결 테스트 — 몇 개 주소만 읽어 원시값을 보여준다.

예) python -m rsp_logger.conntest D401 D402 M382
    python -m rsp_logger.conntest --host 127.0.0.1 D401   (가상 PLC)
"""
import argparse
import tomllib
from . import CONFIG_DIR
from .xgt.client import XgtClient

SETTINGS = CONFIG_DIR / "settings.toml"
DEFAULT_ADDRS = ["D401", "D402", "D324", "D221", "M382"]


def main():
    cfg = tomllib.loads(SETTINGS.read_text(encoding="utf-8"))["plc"]
    ap = argparse.ArgumentParser(description="RSP PLC 연결 테스트 (읽기 전용)")
    ap.add_argument("--host", default=cfg["host"])
    ap.add_argument("--port", type=int, default=cfg["port"])
    ap.add_argument("--bits", default="M,P,K,F,L", help="비트로 읽을 영역 문자")
    ap.add_argument("addrs", nargs="*", default=DEFAULT_ADDRS)
    a = ap.parse_args()

    c = XgtClient(a.host, a.port, cfg["timeout_s"])
    print(f"접속 시도: {a.host}:{a.port}")
    try:
        c.connect()
    except OSError as e:
        print(f"접속 실패: {e}\n→ 랜선·PC IP 대역(192.168.1.x)·PLC 전원을 확인하고 결과를 알려 주세요.")
        return 1
    try:
        for addr in a.addrs:
            try:
                if addr[0].upper() in a.bits.split(","):
                    print(f"{addr:>8} = {c.read_bit(addr)}")
                else:
                    v = c.read_word(addr)
                    print(f"{addr:>8} = {v} (부호 있음 {v - 0x10000 if v & 0x8000 else v}, 0x{v:04X})")
            except Exception as e:
                print(f"{addr:>8} 읽기 실패: {e}")
    finally:
        c.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
