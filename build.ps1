# exe 빌드: 먼저 src/rsp_logger/__init__.py 의 __version__ 수정 -> 이 스크립트 실행 -> dist\RSP_Logger.exe
python -m unittest discover -s tests
if ($LASTEXITCODE -ne 0) { throw "테스트 실패 — 빌드 중단" }
pyinstaller --noconfirm --onefile --windowed --name RSP_Logger --paths src `
  --add-data "config;config" run.py
