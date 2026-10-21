"""Exercise fail-closed report behavior; these are not Windows/IME evidence."""

import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "tsf_preflight", Path(__file__).with_name("preflight.py")
)
probe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe)


class PreflightTests(unittest.TestCase):
    def test_push_gate_requires_this_commit_and_actual_probe_change(self):
        head = "a" * 40
        message = "test: actual capability batch\n\n" + probe.MARKER
        event = {
            "ref": "refs/heads/work/linux-usability",
            "before": "b" * 40,
            "after": head,
            "head_commit": {"id": head, "message": message},
        }
        paths = ["tests/windows/tsf_probe/preflight.py"]
        self.assertTrue(probe.push_eligible(event, head, message, paths, True))
        self.assertFalse(
            probe.push_eligible(event, head, message, [".github/workflows/tsf-preflight.yml"], True)
        )
        self.assertFalse(
            probe.push_eligible(event | {"ref": "refs/heads/trunk"}, head, message, paths, True)
        )
        self.assertFalse(probe.push_eligible(event | {"deleted": True}, head, message, paths, True))
        for altered in (
            event | {"before": "0" * 40},
            event | {"forced": True},
            event | {"after": "c" * 40},
            event | {"head_commit": {"id": head, "message": message + " changed"}},
        ):
            with self.assertRaises(ValueError):
                probe.push_eligible(altered, head, message, paths, True)
        with self.assertRaises(ValueError):
            probe.push_eligible(event, head, message, paths, False)

    def test_push_gate_rejects_quoted_duplicate_or_nonterminal_opt_in(self):
        head = "a" * 40
        for message in (
            probe.MARKER + "\n\nmore",
            "quoted ```\n\n" + probe.MARKER,
            probe.MARKER + "\n\n" + probe.MARKER,
            "title\n\n " + probe.MARKER,
        ):
            event = {
                "ref": "refs/heads/work/linux-usability",
                "before": "b" * 40,
                "after": head,
                "head_commit": {"id": head, "message": message},
            }
            with self.assertRaises(ValueError):
                probe.push_eligible(
                    event, head, message, ["tests/windows/tsf_probe/preflight.py"], True
                )

    def test_admission_requires_all_labels_and_windows(self):
        environment = {
            "GITHUB_ACTIONS": "true",
            "RUNNER_ENVIRONMENT": "github-hosted",
            "RUNNER_OS": "Windows",
        }
        self.assertEqual([], probe.admission(environment, "Windows"))
        for missing in environment:
            self.assertTrue(
                probe.admission(
                    {key: value for key, value in environment.items() if key != missing}, "Windows"
                )
            )
        self.assertTrue(probe.admission(environment, "Linux"))

    def test_configured_profile_never_proves_ime_or_write_permission(self):
        result = probe.assess(
            {
                "languages": [{"languageTag": "zh-CN", "inputMethodTips": ["0804:example"]}],
                "notepadFile": {"version": "example"},
            },
            {"notepadWindows": [{"class": "Edit"}]},
        )
        self.assertEqual("present", result["chineseProfileConfiguration"])
        self.assertEqual("present", result["candidateWindowMetadata"])
        self.assertEqual("unknown", result["notepadWindow"])
        self.assertFalse(result["targetIdentityVerified"])
        for key in (
            "activeChineseImeInTarget",
            "completeCommittedTextAndSelection",
            "compositionObservation",
            "editNotifications",
            "coexistence",
        ):
            self.assertEqual("unknown", result[key])
        self.assertFalse(result["registrationAllowed"])
        self.assertFalse(result["writebackAllowed"])

    def test_absent_application_and_language_are_unknown(self):
        result = probe.assess({}, {})
        self.assertEqual("unknown", result["notepadWindow"])
        self.assertEqual("unknown", result["chineseProfileConfiguration"])

    def test_query_timeout_preserves_blocked_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "report.json"
            with (
                patch.object(probe, "admission", return_value=[]),
                patch.dict(os.environ, {"WINDIR": "C:/Windows"}),
                patch.object(
                    probe.subprocess,
                    "run",
                    side_effect=probe.subprocess.TimeoutExpired("fixed-query", 40),
                ),
                patch.object(probe, "native_observation") as native,
            ):
                self.assertEqual(2, probe.main(["--output", str(path)]))
                native.assert_not_called()
            result = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual("blocked", result["status"])
            self.assertEqual(["TimeoutExpired"], result["blockers"])
            self.assertFalse(result["productAcceptance"])

    def test_blocked_run_writes_evidence_without_observing_system(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "report.json"
            with (
                patch.dict(os.environ, {}, clear=True),
                patch.object(probe, "native_observation") as native,
                patch.object(probe.subprocess, "run") as shell,
            ):
                self.assertEqual(2, probe.main(["--output", str(path)]))
                native.assert_not_called()
                shell.assert_not_called()
            result = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual("blocked", result["status"])
            self.assertFalse(result["productAcceptance"])
            self.assertEqual(0, result["registrationAttempts"])
            self.assertEqual(0, result["sourceTextReads"])
            before = path.read_bytes()
            with self.assertRaises(FileExistsError):
                probe.main(["--output", str(path)])
            self.assertEqual(before, path.read_bytes())


if __name__ == "__main__":
    unittest.main()
