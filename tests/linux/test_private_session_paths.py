"""Private-session CLI rejects recursive copies before starting desktop tools."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".agents/skills/verify-typelay/scripts/private-session.py"


class PrivateSessionPathTests(unittest.TestCase):
    def test_output_inside_source_is_rejected_without_creating_evidence(self):
        for alias in (False, True):
            with self.subTest(package_alias=alias), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                package = root / "package"
                package.mkdir()
                supplied = root / "package-alias" if alias else package
                if alias:
                    supplied.symlink_to(package, target_is_directory=True)
                output = supplied / "evidence"
                tools = root / "tools"
                tools.mkdir()
                for name in (
                    "Xvfb",
                    "xvfb-run",
                    "xauth",
                    "dbus-run-session",
                    "niri",
                    "grim",
                    "xdotool",
                ):
                    tool = tools / name
                    tool.write_text("#!/bin/sh\nexit 17\n")
                    tool.chmod(0o700)
                result = subprocess.run(
                    [
                        sys.executable,
                        str(SCRIPT),
                        "--package",
                        str(supplied),
                        "--cooperative",
                        str(root / "cooperative"),
                        "--output",
                        str(output),
                        "--",
                        "/bin/false",
                    ],
                    env={"PATH": str(tools)},
                    capture_output=True,
                    text=True,
                    timeout=10,
                )
                # The unfixed CLI allocates its own runtime before failing the
                # recursive copy. Remove only that recorded, task-owned directory.
                receipt = output / "cleanup.json"
                if receipt.exists():
                    runtime = Path(json.loads(receipt.read_text())["runtime"])
                    self.assertEqual(Path(f"/run/user/{os.getuid()}"), runtime.parent)
                    self.assertTrue(runtime.name.startswith("typelay-verify-"))
                    shutil.rmtree(runtime)
                self.assertEqual(2, result.returncode, result.stderr[-500:])
                self.assertIn("output must be outside the source package", result.stderr)
                self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
