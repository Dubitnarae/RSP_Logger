import hashlib
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from rsp_logger.updater import EXE_NAME, is_newer, verified  # noqa: E402


class UpdaterTest(unittest.TestCase):
    def test_version_compare(self):
        self.assertTrue(is_newer("0.3.0", "0.2.0") and is_newer("v0.2.1", "0.2.0") and is_newer("1.0", "0.9.9"))
        self.assertFalse(is_newer("0.2.0", "0.2.0") or is_newer("0.1.9", "0.2.0"))

    def test_sha(self):
        h = hashlib.sha256(b"abc").hexdigest()
        self.assertTrue(verified(b"abc", f"{h}  {EXE_NAME}\n"))
        self.assertFalse(verified(b"abd", h) or verified(b"abc", ""))


if __name__ == "__main__":
    unittest.main()
