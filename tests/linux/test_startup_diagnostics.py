"""Public doctor command diagnoses missing GUI dependencies without a desktop."""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class StartupDiagnosticsTests(unittest.TestCase):
    def test_missing_gui_dependencies_have_actionable_diagnostics_without_display(self):
        with tempfile.TemporaryDirectory(prefix="typelay-doctor-") as directory:
            home = Path(directory)
            (home / "package/app").mkdir(parents=True)
            (home / "package/app/TypeLay.Linux").touch()
            environment = {
                **os.environ,
                "HOME": directory,
                "XDG_CONFIG_HOME": str(home / "config"),
                "XDG_DATA_HOME": str(home / "data"),
                "XDG_CACHE_HOME": str(home / "cache"),
                "XDG_RUNTIME_DIR": directory,
                "TYPELAY_HOME": str(home / "package"),
            }
            for name in ("DISPLAY", "WAYLAND_DISPLAY", "NIRI_SOCKET", "PYTHONPATH"):
                environment.pop(name, None)
            # Disable site packages, making GI genuinely unavailable, not a mocked result.
            result = subprocess.run(
                [
                    sys.executable,
                    "-I",
                    "-S",
                    "-B",
                    "-c",
                    "import runpy,sys; sys.path.insert(0,sys.argv.pop(1)); "
                    "runpy.run_module('main',run_name='__main__')",
                    str(ROOT / "src/linux/desktop"),
                    "--doctor",
                ],
                env=environment,
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            state = json.loads(result.stdout)
            self.assertTrue(state["hostInstalled"])
            self.assertFalse(state["wayland"])
            self.assertIsNone(state["niriVersion"])
            self.assertEqual(
                state["desktopDependencies"], {"gtk3Typelib": False, "glibEventLoop": False}
            )
            self.assertIn("python-gobject", state["desktopDependencyHelp"])
            self.assertIn("gtk3", state["desktopDependencyHelp"])
            self.assertNotIn("Traceback", result.stderr)
            self.assertFalse((home / "config/typelay/settings.json").exists())
            self.assertFalse((home / "data/typelay/components").exists())


if __name__ == "__main__":
    unittest.main()
