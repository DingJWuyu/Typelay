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


def diagnostics(*, read):
    return {
        "protocolVersion": 2,
        "targetPhases": 31 if read else 0,
        "policyQueried": read,
        "signatureKnown": read,
        "signatureError": 0,
        "signatureFlags": 0,
        "dynamicKnown": read,
        "dynamicError": 0,
        "dynamicFlags": 0,
        "sendAttempted": read,
        "sendReturned": read,
        "sendReturn": 1 if read else 0,
        "sendLastError": 0,
        "sendStartedTick": 10 if read else 0,
        "sendEndedTick": 11 if read else 0,
        "messageResultValid": read,
        "messageResult": 0 if read else None,
    }


def observed_report():
    return {
        "diagnostics": diagnostics(read=True),
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

    def test_diagnostic_schema_distinguishes_api_result_from_observed_read(self):
        detail = diagnostics(read=True)
        self.assertTrue(validation.diagnostics_valid(detail))  # Message result zero is valid.
        failed_send = detail | {
            "sendReturn": 0,
            "sendLastError": 1460,
            "messageResultValid": False,
            "messageResult": None,
        }
        self.assertTrue(validation.read_observed(observed_report() | {"diagnostics": failed_send}))
        not_returned = detail | {
            "sendReturned": False,
            "sendReturn": 0,
            "sendStartedTick": 0,
            "sendEndedTick": 0,
            "messageResultValid": False,
            "messageResult": None,
        }
        # Well-typed intermediate facts cannot masquerade as a final successful read.
        self.assertTrue(validation.diagnostics_valid(not_returned))
        self.assertFalse(
            validation.read_observed(observed_report() | {"diagnostics": not_returned})
        )
        # Even successful send never proves a callback/snapshot occurred.
        self.assertFalse(validation.read_observed(observed_report() | {"callbackCount": 0}))
        for phase in (0, 1, 3, 7, 15, 31):
            self.assertTrue(validation.diagnostics_valid(detail | {"targetPhases": phase}))
        for key, value in (
            ("protocolVersion", 1),
            ("protocolVersion", True),
            ("targetPhases", 2),
            ("targetPhases", 32),
            ("targetPhases", False),
            ("signatureFlags", 32),
            ("signatureFlags", 1),
            ("dynamicFlags", 16),
            ("signatureKnown", False),
            ("signatureError", -1),
            ("dynamicError", 2**32),
            ("sendReturn", 2**63),
            ("sendLastError", True),
            ("sendEndedTick", 9),
            ("sendStartedTick", -1),
            ("messageResultValid", False),
            ("messageResult", None),
            ("messageResult", True),
            ("messageResult", 2**64),
            ("sendReturned", False),
            ("extra", "private-sentinel"),
        ):
            with self.subTest(key=key, value=value):
                self.assertFalse(validation.diagnostics_valid(detail | {key: value}))
        self.assertTrue(validation.diagnostics_valid(detail | {"dynamicFlags": 1}))
        self.assertTrue(validation.diagnostics_valid(failed_send | {"sendLastError": 0}))
        self.assertTrue(
            validation.diagnostics_valid(
                diagnostics(read=False)
                | {
                    "policyQueried": True,
                    "signatureKnown": True,
                    "signatureFlags": 1,
                    "dynamicKnown": True,
                }
            )
        )  # Explicit incompatible policy is a valid failure observation, never a read.
        self.assertTrue(
            validation.diagnostics_valid(
                diagnostics(read=False)
                | {
                    "policyQueried": True,
                    "signatureError": 5,
                    "dynamicKnown": True,
                }
            )
        )  # Unknown policy is representable but cannot authorize dispatch.
        missing = detail.copy()
        missing.pop("sendReturned")
        self.assertFalse(validation.diagnostics_valid(missing))

    def test_lifecycle_consumer_requires_no_dll_diagnostics_and_normal_cleanup(self):
        report = observed_report() | {
            "reason": "lifecycle-observed",
            "requestDispatched": False,
            "readSnapshotValid": False,
            "readStatus": 0,
            "sessionHr": 0,
            "callbackCount": 0,
            "syntheticExactMatch": False,
            "textLength": 0,
            "selectionStart": 0,
            "selectionLength": 0,
            "noncryptographicFingerprint": "0000000000000000",
            "diagnostics": diagnostics(read=False),
        }
        self.assertTrue(validation.lifecycle_observed(report))
        for key, value in (
            ("normalExit", False),
            ("forcedCleanup", True),
            ("requestDispatched", True),
            ("sessionDestroyed", 1),
            ("diagnostics", diagnostics(read=True)),
        ):
            self.assertFalse(validation.lifecycle_observed(report | {key: value}))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "lifecycle.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            with redirect_stdout(io.StringIO()):
                self.assertEqual(0, validation.main(["--kind", "lifecycle", "--report", str(path)]))
            report["diagnostics"]["extra"] = "private-sentinel"
            path.write_text(json.dumps(report), encoding="utf-8")
            output = io.StringIO()
            with redirect_stdout(output):
                self.assertEqual(2, validation.main(["--kind", "lifecycle", "--report", str(path)]))
            self.assertNotIn("private-sentinel", output.getvalue())

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
