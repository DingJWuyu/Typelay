"""Outer scope-result lifecycle with a controlled compositor process port.

No desktop, model, translation or tutorial completion is exercised here.
"""

import json
import os
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from native_e2e import outer


class DesktopScopeResultTests(unittest.TestCase):
    def exercise(self, scope, *, launch_error=False, cleanup_error=False, inside_error=False):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            args = SimpleNamespace(
                output=output,
                native=output / "native",
                package=output / "package",
                desktop=True,
                desktop_scope=scope,
                fcitx_cooperative=None,
                qt_cooperative=None,
                editor_bundle=None,
            )
            report = output / "scope-result.json"
            pending = []

            def launch(*_args, **_kwargs):
                pending.append(json.loads(report.read_text()))
                configuration = (output / "niri.kdl").read_text()
                self.assertEqual(
                    "animations { off; }" in configuration.splitlines(), scope == "ordinary-gimp"
                )
                if launch_error:
                    raise OSError("synthetic private input must not enter the report")
                # The inside success receipt is a controlled process-port reply,
                # not evidence of real GUI acceptance.
                if not inside_error:
                    (output / "desktop-result.json").write_text('{"passed": []}')

                def wait(**_kwargs):
                    self.assertEqual(json.loads(report.read_text())["status"], "not-run")
                    if cleanup_error:
                        raise RuntimeError("controlled cleanup failure")
                    return 0

                return SimpleNamespace(poll=lambda: 0, wait=wait)

            with (
                patch.dict(os.environ, {"DISPLAY": ":controlled"}),
                patch("native_e2e.subprocess.Popen", side_effect=launch),
            ):
                if launch_error or cleanup_error or inside_error:
                    with self.assertRaises(OSError if launch_error else RuntimeError):
                        outer(args)
                else:
                    outer(args)
            self.assertEqual(pending, [{"schema": 1, "scope": scope, "status": "not-run"}])
            result = json.loads(report.read_text())
            self.assertEqual(result["schema"], 1)
            self.assertEqual(result["scope"], scope)
            self.assertEqual(
                result["status"],
                "failed" if launch_error or cleanup_error or inside_error else "passed",
            )
            self.assertEqual(set(result), {"schema", "scope", "status"})

    def test_each_setup_scope_records_failure_before_inside_start(self):
        for scope in ("setup", "setup-settings", "setup-tutorial", "ordinary-gimp", "settings-ui"):
            with self.subTest(scope=scope):
                self.exercise(scope, launch_error=True)

    def test_inside_without_completion_receipt_reports_failure(self):
        self.exercise("setup-settings", inside_error=True)

    def test_inside_receipt_does_not_override_failed_cleanup(self):
        for scope in ("setup-tutorial", "ordinary-gimp", "settings-ui"):
            with self.subTest(scope=scope):
                self.exercise(scope, cleanup_error=True)

    def test_report_write_failure_preserves_original_launch_exception(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            args = SimpleNamespace(
                output=output,
                native=output / "native",
                package=output / "package",
                desktop=True,
                desktop_scope="setup-tutorial",
                fcitx_cooperative=None,
                qt_cooperative=None,
                editor_bundle=None,
            )
            original = OSError("original launch failure")

            def launch(*_args, **_kwargs):
                (output / "scope-result.json.tmp").mkdir()
                raise original

            with (
                patch.dict(os.environ, {"DISPLAY": ":controlled"}),
                patch("native_e2e.subprocess.Popen", side_effect=launch),
            ):
                with self.assertRaises(OSError) as raised:
                    outer(args)
            self.assertIs(raised.exception, original)
            self.assertEqual(
                json.loads((output / "scope-result.json").read_text())["status"], "not-run"
            )

    def test_success_is_reported_after_outer_cleanup(self):
        self.exercise("setup-settings")
        self.exercise("settings-ui")


if __name__ == "__main__":
    unittest.main()
