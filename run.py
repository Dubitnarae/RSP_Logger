"""exe 진입점 (PyInstaller 용). 개발 중에는 src 에서 python -m rsp_logger --dev"""
from rsp_logger.__main__ import main

raise SystemExit(main())
