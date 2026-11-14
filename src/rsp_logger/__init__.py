"""RSP Logger 버전 정보 — 버전은 이 파일에서만 수정한다."""
import shutil
import sys
from pathlib import Path

APP_NAME = "RSP Logger"
__version__ = "0.1.0"
AUTHOR = "KIM YEONGDO"
GITHUB_REPO = "Dubitnarae/RSP_Logger"  # 자동 업데이트가 릴리스를 확인하는 저장소(공개여야 함)

if getattr(sys, "frozen", False):
    # exe: exe 옆 config 폴더를 쓴다 (현장에서 tags.csv·settings.toml 수정 가능). 없으면 기본값을 꺼내 둔다.
    CONFIG_DIR = Path(sys.executable).resolve().parent / "config"
    if not CONFIG_DIR.exists():
        shutil.copytree(Path(sys._MEIPASS) / "config", CONFIG_DIR)
else:
    CONFIG_DIR = Path(__file__).resolve().parents[2] / "config"
