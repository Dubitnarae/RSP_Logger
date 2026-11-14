"""빌드한 exe 안의 파일을 전부 압축 해제해 본다. 이 PC는 큰 파일이 가끔 1비트 깨지므로(SSD 의심) 게시 전에 꼭 확인."""
import sys

from PyInstaller.archive.readers import CArchiveReader

arc = CArchiveReader(sys.argv[1] if len(sys.argv) > 1 else "dist/RSP_Logger.exe")
bad = []
for name in arc.toc:
    try:
        arc.extract(name)
    except Exception as e:
        bad.append(f"{name}: {e}")
print(f"{len(arc.toc)}개 중 깨진 것 {len(bad)}개")
for b in bad:
    print("  ", b)
sys.exit(1 if bad else 0)
