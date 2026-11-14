# exe 빌드: 먼저 src/rsp_logger/__init__.py 의 __version__ 수정 -> 이 스크립트 실행 -> dist\RSP_Logger.exe
python -m unittest discover -s tests
if ($LASTEXITCODE -ne 0) { throw "테스트 실패 — 빌드 중단" }
pyinstaller --noconfirm --clean --onefile --windowed --name RSP_Logger --paths src `
  --add-data "config;config" run.py
# 이 PC는 큰 파일이 가끔 깨진다 — exe 안 파일을 전부 풀어 보고, 깨졌으면 게시하지 않는다
python verify_exe.py dist\RSP_Logger.exe
if ($LASTEXITCODE -ne 0) { throw "exe 깨짐 — 다시 빌드할 것" }
