"""Validate consumer exit prerequisites through their CLI, not process-name guesses."""

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
        for name, nonce, mode in (
            ("continuous", "b" * 32, "--verify-continuous"),
            ("browser", "c" * 32, "--verify-browser-cooperation"),
            ("notepad", "e" * 32, "--verify-ordinary-notepad"),
        ):
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
            if name == "browser":
                self.before_notepad_command = self.command + ["--scope", "before-notepad"]

    def run_validator(self, expected, command=None):
        result = subprocess.run(command or self.command, capture_output=True, text=True, timeout=10)
        self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
        self.assertEqual("no-active-owned-consumers\n" if expected == 0 else "", result.stdout)

    def test_all_three_natural_exits_are_required(self):
        self.run_validator(0)
        self.run_validator(0, self.command + ["--scope", "all"])

    def test_before_notepad_accepts_failed_but_exited_prior_consumers(self):
        self.proofs[2][0].unlink()
        for path, proof in self.proofs[:2]:
            proof["rootExitCode"] = 1
            path.write_text(json.dumps(proof), encoding="utf-8")
        self.run_validator(0, self.before_notepad_command)
        self.run_validator(1)
        for path, _ in self.proofs[:2]:
            self.assertEqual(1, json.loads(path.read_text())["rootExitCode"])

    def test_scene_failure_and_confirmed_forced_cleanup_do_not_erase_failure(self):
        path, proof = self.proofs[0]
        proof.update(rootExitCode=23, cleanupMode="terminated")
        path.write_text(json.dumps(proof), encoding="utf-8")
        self.run_validator(0)
        self.assertEqual(23, json.loads(path.read_text())["rootExitCode"])

    def test_notepad_scene_failure_with_confirmed_exit_allows_only_next_consumer(self):
        path, proof = self.proofs[2]
        proof["rootExitCode"] = 1
        path.write_text(json.dumps(proof), encoding="utf-8")
        self.run_validator(0)
        self.assertEqual(1, json.loads(path.read_text())["rootExitCode"])
        proof.update(rootKernelSignaled=False, activeProcesses=1, noActiveOwnedConsumers=False)
        path.write_text(json.dumps(proof), encoding="utf-8")
        self.run_validator(1)

    def test_skipped_notepad_without_proof_cannot_authorize(self):
        self.proofs[2][0].unlink()
        self.run_validator(1)

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
        for proofs, command in (
            (self.proofs, self.command),
            (self.proofs[:2], self.before_notepad_command),
        ):
            for path, original in proofs:
                for key, values in changes.items():
                    for value in values:
                        with self.subTest(path=path.name, key=key, value=value, command=command):
                            proof = {**original, key: value}
                            path.write_text(json.dumps(proof), encoding="utf-8")
                            self.run_validator(1, command)
                for key in original:
                    with self.subTest(path=path.name, missing=key, command=command):
                        proof = copy.copy(original)
                        del proof[key]
                        path.write_text(json.dumps(proof), encoding="utf-8")
                        self.run_validator(1, command)
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
                self.run_validator(1, self.before_notepad_command)
        path.unlink()
        self.run_validator(1)
        self.run_validator(1, self.before_notepad_command)
        path.mkdir()
        self.run_validator(1)
        self.run_validator(1, self.before_notepad_command)

    def test_before_notepad_requires_both_prior_proof_files(self):
        for path, proof in self.proofs[:2]:
            with self.subTest(missing=path.name):
                path.unlink()
                self.run_validator(1, self.before_notepad_command)
                path.write_text(json.dumps(proof), encoding="utf-8")

    def test_cancelled_or_unknown_invocation_cannot_authorize(self):
        for invocation in (self.command, self.before_notepad_command):
            for value in ("true", "unknown", ""):
                command = invocation.copy()
                command[command.index("--workflow-cancelled") + 1] = value
                self.run_validator(1, command)
            for flag in (
                "--run-id",
                "--run-attempt",
                "--head",
                "--continuous-nonce",
                "--browser-nonce",
                "--notepad-nonce",
            ):
                if flag not in invocation:
                    continue
                command = invocation.copy()
                command[command.index(flag) + 1] = ""
                self.run_validator(1, command)

    def test_swapped_reports_and_invalid_utf8_fail_closed(self):
        first, second = (entry[0] for entry in self.proofs[:2])
        first_bytes, second_bytes = first.read_bytes(), second.read_bytes()
        first.write_bytes(second_bytes)
        second.write_bytes(first_bytes)
        self.run_validator(1)
        self.run_validator(1, self.before_notepad_command)
        first.write_bytes(first_bytes)
        second.write_bytes(b"\xff")
        self.run_validator(1)
        self.run_validator(1, self.before_notepad_command)

    def test_missing_required_context_is_not_inferred_from_reports(self):
        for invocation in (self.command, self.before_notepad_command):
            for flag in (
                "--workflow-cancelled",
                "--run-id",
                "--run-attempt",
                "--head",
                "--continuous-nonce",
                "--browser-nonce",
                "--notepad-nonce",
                "--continuous-proof",
                "--browser-proof",
                "--notepad-proof",
            ):
                if flag not in invocation:
                    continue
                with self.subTest(flag=flag, invocation=invocation):
                    command = invocation.copy()
                    index = command.index(flag)
                    del command[index : index + 2]
                    result = subprocess.run(command, capture_output=True, text=True, timeout=10)
                    self.assertNotEqual(0, result.returncode)
                    self.assertEqual("", result.stdout)

    def test_same_nonce_cannot_stand_for_two_invocations(self):
        path, proof = self.proofs[1]
        proof["nonce"] = "b" * 32
        path.write_text(json.dumps(proof), encoding="utf-8")
        for invocation in (self.command, self.before_notepad_command):
            command = invocation.copy()
            command[command.index("--browser-nonce") + 1] = "b" * 32
            self.run_validator(1, command)

    def test_unknown_scope_cannot_authorize(self):
        result = subprocess.run(
            self.command + ["--scope", "browser-only"],
            capture_output=True,
            text=True,
            timeout=10,
        )
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("", result.stdout)


if __name__ == "__main__":
    unittest.main()
