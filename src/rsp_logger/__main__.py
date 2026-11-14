"""실행: python -m rsp_logger          (현장 PLC, config/settings.toml)
      python -m rsp_logger --dev    (개발 모드: 값이 움직이는 가상 PLC를 함께 띄움)"""
import argparse
import sys
import threading
import tomllib

from PySide6.QtWidgets import QApplication

from . import CONFIG_DIR
from .core.calc import DERIVED
from .core.poller import Poller
from .core.recorder import Recorder
from .core.tags import load_tags
from .ui.main_window import MainWindow
from .xgt.client import XgtClient


def main():
    ap = argparse.ArgumentParser(description="RSP Logger (PLC 읽기 전용)")
    ap.add_argument("--dev", action="store_true", help="개발 모드: 가상 PLC 사용")
    a = ap.parse_args()
    cfg = tomllib.loads((CONFIG_DIR / "settings.toml").read_text(encoding="utf-8"))
    host, port = cfg["plc"]["host"], cfg["plc"]["port"]
    if a.dev:
        from .sim.fake_plc import FakePlc
        plc = FakePlc(port=0)
        threading.Thread(target=plc.serve_forever, daemon=True).start()
        plc.start_animation()
        host, port = plc.server_address

    tags = load_tags(CONFIG_DIR / "tags.csv")
    app = QApplication(sys.argv)
    out_dir = cfg["record"]["dir"] or CONFIG_DIR.parent / ("data_dev" if a.dev else "data")
    win = MainWindow(tags, f"{host}:{port}", cfg["process"]["wheel_diameter_mm"],
                     Recorder(out_dir, tags, DERIVED), dev=a.dev)
    poller = Poller(XgtClient(host, port, cfg["plc"]["timeout_s"]), tags, cfg["poll"]["interval_s"],
                    win.bridge.data.emit, win.bridge.status.emit)
    poller.start()
    win.show()
    code = app.exec()
    poller.stop()
    return code


if __name__ == "__main__":
    raise SystemExit(main())
