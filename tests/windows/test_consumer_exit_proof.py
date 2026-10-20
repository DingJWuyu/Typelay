"""Validate the future Hy prerequisite through its CLI, not process-name guesses."""

import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

VALIDATOR = Path(__file__).resolve().parents[2] / "eng/windows/verify-consumer-exit.py"


class ConsumerExitProofTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.proofs = []
        self.command = [
            sys.executable,
            str(VALIDATOR),
            "--run-id",
            "123",
            "--run-attempt",
            "2",
            "--head",
            "a" * 40,
            "--workflow-cancelled",
            "false",
        ]
        for name, nonce in (("continuous", "b" * 32), ("browser", "c" * 32)):
            mode = "--verify-continuous" if name == "continuous" else "--verify-browser-cooperation"
            path = self.root / (name + ".json")
            proof = dict(
                schemaVersion=1,
                mode=mode,
                runId="123",
                runAttempt="2",
                head="a" * 40,
                nonce=nonce,
                rootProcessId=42,
                rootExitCode=0,
                rootKernelSignaled=True,
                activeProcesses=0,
                assignedBeforeResume=True,
                interrupted=False,
                cleanupMode="natural",
                failure=None,
                noActiveOwnedConsumers=True,
            )
            self.proofs.append((path, proof))
            path.write_text(json.dumps(proof), encoding="utf-8")
            self.command += ["--" + name + "-proof", str(path), "--" + name + "-nonce", nonce]

    def run_validator(self, expected, command=None):
        result = subprocess.run(command or self.command, capture_output=True, text=True, timeout=10)
        self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
        self.assertEqual("no-active-owned-consumers\n" if expected == 0 else "", result.stdout)

    def test_both_natural_exits_are_required(self):
        self.run_validator(0)

    def test_scene_failure_and_confirmed_forced_cleanup_do_not_erase_failure(self):
        path, proof = self.proofs[0]
        proof.update(rootExitCode=23, cleanupMode="terminated")
        path.write_text(json.dumps(proof), encoding="utf-8")
        self.run_validator(0)
        self.assertEqual(23, json.loads(path.read_text())["rootExitCode"])

    def test_each_report_rejects_stale_unknown_or_inconsistent_evidence(self):
        changes = dict(
            schemaVersion=[2, True],
            mode=["--verify-hy"],
            runId=["122", None],
            runAttempt=["1", 2],
            head=["d" * 40],
            nonce=["d" * 32],
            rootProcessId=[None, 0, -1, True, 2**32],
            rootExitCode=[None, True, 2**31],
            rootKernelSignaled=[False, 1],
            activeProcesses=[None, 1, False],
            assignedBeforeResume=[False, 1],
            interrupted=[True, 0],
            cleanupMode=["unconfirmed", None],
            failure=["Win32Exception", ""],
            noActiveOwnedConsumers=[False, 1],
        )
        for path, original in self.proofs:
            for key, values in changes.items():
                for value in values:
                    with self.subTest(path=path.name, key=key, value=value):
                        proof = {**original, key: value}
                        path.write_text(json.dumps(proof), encoding="utf-8")
                        self.run_validator(1)
            for key in original:
                with self.subTest(missing=key):
                    proof = copy.copy(original)
                    del proof[key]
                    path.write_text(json.dumps(proof), encoding="utf-8")
                    self.run_validator(1)
            path.write_text(json.dumps(original), encoding="utf-8")

    def test_missing_partial_oversized_duplicate_and_unknown_documents_fail_closed(self):
        path, proof = self.proofs[0]
        for content in (
            "{",
            "null",
            "[]",
            "x" * 65537,
            "\ufffd",
            json.dumps(proof)[:-1] + ', "activeProcesses":0}',
            json.dumps({**proof, "unknown": True}),
        ):
            with self.subTest(content=content[:40]):
                path.write_text(content, encoding="utf-8")
                self.run_validator(1)
        path.unlink()
        self.run_validator(1)
        path.mkdir()
        self.run_validator(1)

    def test_cancelled_or_unknown_invocation_cannot_authorize(self):
        for value in ("true", "unknown", ""):
            command = self.command.copy()
            command[command.index("--workflow-cancelled") + 1] = value
            self.run_validator(1, command)
        for flag in (
            "--run-id",
            "--run-attempt",
            "--head",
            "--continuous-nonce",
            "--browser-nonce",
        ):
            command = self.command.copy()
            command[command.index(flag) + 1] = ""
            self.run_validator(1, command)

    def test_swapped_reports_and_invalid_utf8_fail_closed(self):
        first, second = (entry[0] for entry in self.proofs)
        first_bytes, second_bytes = first.read_bytes(), second.read_bytes()
        first.write_bytes(second_bytes)
        second.write_bytes(first_bytes)
        self.run_validator(1)
        first.write_bytes(first_bytes)
        second.write_bytes(b"\xff")
        self.run_validator(1)

    def test_missing_required_context_is_not_inferred_from_reports(self):
        for flag in (
            "--workflow-cancelled",
            "--run-id",
            "--run-attempt",
            "--head",
            "--continuous-nonce",
            "--browser-nonce",
            "--continuous-proof",
            "--browser-proof",
        ):
            with self.subTest(flag=flag):
                command = self.command.copy()
                index = command.index(flag)
                del command[index : index + 2]
                result = subprocess.run(command, capture_output=True, text=True, timeout=10)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("", result.stdout)

    def test_same_nonce_cannot_stand_for_two_invocations(self):
        path, proof = self.proofs[1]
        proof["nonce"] = "b" * 32
        path.write_text(json.dumps(proof), encoding="utf-8")
        command = self.command.copy()
        command[command.index("--browser-nonce") + 1] = "b" * 32
        self.run_validator(1, command)


if __name__ == "__main__":
    unittest.main()
