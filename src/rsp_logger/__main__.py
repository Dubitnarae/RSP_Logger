"""실행: python -m rsp_logger          (현장 PLC, config/settings.toml)
      python -m rsp_logger --dev    (개발 모드: 값이 움직이는 가상 PLC를 함께 띄움)"""
import argparse
import os
import subprocess
import sys
import threading
import tomllib
from pathlib import Path

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
    ej = cfg["ejection"]
    out_dir = Path(cfg["record"]["dir"]) if cfg["record"]["dir"] else CONFIG_DIR.parent / ("data_dev" if a.dev else "data")
    win = MainWindow(tags, f"{host}:{port}", cfg["process"]["wheel_diameter_mm"],
                     Recorder(out_dir, tags, DERIVED, cfg["poll"]["interval_s"], ej["pre_s"], ej["post_s"]),
                     dev=a.dev)
    win.fast_interval = ej["fast_interval_s"]
    poller = Poller(XgtClient(host, port, cfg["plc"]["timeout_s"]), tags, cfg["poll"]["interval_s"],
                    win.bridge.data.emit, win.bridge.status.emit)
    win.poller = poller
    poller.start()
    win.show()
    if getattr(sys, "frozen", False):
        win.check_update()
    code = app.exec()
    poller.stop()
    if win.restart_dev is not None:
        restart(win.restart_dev)
    return code


def restart(dev):
    args = ["--dev"] if dev else []
    if getattr(sys, "frozen", False):
        # 새 exe 가 지금 exe 의 임시 폴더(_MEI…)를 물려받지 않게 (updater.py 와 같은 이유)
        env = dict(os.environ, PYINSTALLER_RESET_ENVIRONMENT="1")
        subprocess.Popen([sys.executable, *args], cwd=str(Path(sys.executable).parent), close_fds=True, env=env)
    else:
        subprocess.Popen([sys.executable, "-m", "rsp_logger", *args], cwd=str(Path(__file__).parents[1]), close_fds=True)


if __name__ == "__main__":
    raise SystemExit(main())
