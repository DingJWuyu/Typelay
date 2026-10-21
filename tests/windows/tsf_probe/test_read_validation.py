"""Report-consumer contracts only, not simulated TSF or Windows observations."""

import importlib.util
import io
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "read_validation", Path(__file__).with_name("read_validation.py")
)
validation = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(validation)


def observed_report():
    return {
        "kind": "isolated-tsf-native",
        "status": "observed-not-acceptance",
        "reason": "single-read-observed",
        "targetStarted": True,
        "requestDispatched": True,
        "normalExit": True,
        "forcedCleanup": False,
        "ownedJobEmpty": True,
        "syntheticFileRemoved": True,
        "removalProvenByProcessExit": True,
        "liveUnloadProven": False,
        "imeVerified": False,
        "writebackAllowed": False,
        "readSnapshotValid": True,
        "readStatus": 1,
        "readReason": 0,
        "requestHr": 0,
        "sessionHr": 0x40300,
        "callbackCount": 1,
        "sessionDestroyed": 0,
        "textLength": 79,
        "selectionStart": 79,
        "selectionLength": 0,
        "compositionCount": 0,
        "syntheticExactMatch": True,
        "noncryptographicFingerprint": "0123456789abcdef",
    }


class ReadValidationTests(unittest.TestCase):
    def test_unknown_ime_limits_conclusion_not_environment_collection(self):
        report = {
            "schemaVersion": 1,
            "kind": "tsf-readonly-prerequisites",
            "status": "observed-prerequisites-only",
            "runner": {"GITHUB_SHA": "a" * 40},
            "productAcceptance": False,
            "registrationAttempts": 0,
            "activationAttempts": 0,
            "inputChanges": 0,
            "targetApplicationStarts": 0,
            "sourceTextReads": 0,
            "capabilities": {
                "registrationAllowed": False,
                "writebackAllowed": False,
                "activeChineseImeInTarget": "unknown",
                "notepadWindow": "unknown",
            },
        }
        self.assertTrue(validation.environment_observed(report, "a" * 40))
        self.assertFalse(validation.environment_observed(report, "b" * 40))
        for key, value in (
            ("status", "blocked"),
            ("activationAttempts", 1),
            ("productAcceptance", True),
            ("sourceTextReads", False),
        ):
            self.assertFalse(validation.environment_observed(report | {key: value}, "a" * 40))

    def test_read_requires_complete_bounded_snapshot_and_normal_removal(self):
        report = observed_report()
        self.assertTrue(validation.read_observed(report))
        for key, value in (
            ("status", "not-supported-or-incomplete"),
            ("sessionHr", 1),
            ("readStatus", 0),
            ("readSnapshotValid", False),
            ("requestDispatched", False),
            ("syntheticExactMatch", False),
            ("callbackCount", 2),
            ("compositionCount", 1),
            ("normalExit", False),
            ("ownedJobEmpty", False),
            ("forcedCleanup", True),
            ("syntheticFileRemoved", False),
            ("imeVerified", True),
            ("writebackAllowed", True),
            ("textLength", 4097),
            ("selectionStart", -1),
            ("selectionStart", 80),
            ("selectionLength", 1),
            ("textLength", True),
            ("sourceText", "must-not-be-accepted"),
            ("noncryptographicFingerprint", "plaintext"),
        ):
            with self.subTest(key=key, value=value):
                self.assertFalse(validation.read_observed(report | {key: value}))
        report.pop("normalExit")
        self.assertFalse(validation.read_observed(report))

    def test_public_validator_rejects_invalid_report_without_logging_payload(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(
                json.dumps(observed_report() | {"sourceText": "private-sentinel"}), encoding="utf-8"
            )
            output = io.StringIO()
            with redirect_stdout(output):
                self.assertEqual(2, validation.main(["--kind", "read", "--report", str(path)]))
            self.assertNotIn("private-sentinel", output.getvalue())
            path.write_text(json.dumps(observed_report()), encoding="utf-8")
            with redirect_stdout(io.StringIO()):
                self.assertEqual(0, validation.main(["--kind", "read", "--report", str(path)]))


if __name__ == "__main__":
    unittest.main()
