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
            if name == "continuous":
                self.continuous_command = self.command + ["--scope", "continuous-only"]
        self.command += ["--scope", "ordinary"]
        self.scopes = (
            (self.proofs, self.command),
            (self.proofs[:1], self.continuous_command),
        )

    def run_validator(self, expected, command=None):
        result = subprocess.run(command or self.command, capture_output=True, text=True, timeout=10)
        self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
        self.assertEqual("no-active-owned-consumers\n" if expected == 0 else "", result.stdout)

    def test_ordinary_job_requires_its_continuous_and_notepad_exits(self):
        self.run_validator(0)
        for path, proof in self.proofs:
            with self.subTest(missing=path.name):
                path.unlink()
                self.run_validator(1)
                path.write_text(json.dumps(proof), encoding="utf-8")

    def test_next_consumer_requires_its_exact_continuous_exit(self):
        self.proofs[1][0].unlink()
        self.run_validator(0, self.continuous_command)
        path, proof = self.proofs[0]
        proof["rootKernelSignaled"] = False
        path.write_text(json.dumps(proof), encoding="utf-8")
        self.run_validator(1, self.continuous_command)
        path.unlink()
        self.run_validator(1, self.continuous_command)

    def test_failed_but_exited_consumers_keep_their_failure(self):
        for path, proof in self.proofs:
            proof["rootExitCode"] = 1
            path.write_text(json.dumps(proof), encoding="utf-8")
        self.run_validator(0, self.continuous_command)
        self.run_validator(0)
        for path, _ in self.proofs:
            self.assertEqual(1, json.loads(path.read_text())["rootExitCode"])

    def test_scene_failure_and_confirmed_forced_cleanup_do_not_erase_failure(self):
        path, proof = self.proofs[0]
        proof.update(rootExitCode=23, cleanupMode="terminated")
        path.write_text(json.dumps(proof), encoding="utf-8")
        self.run_validator(0)
        self.run_validator(0, self.continuous_command)
        self.assertEqual(23, json.loads(path.read_text())["rootExitCode"])

    def test_notepad_scene_failure_still_requires_confirmed_exit(self):
        path, proof = self.proofs[1]
        proof["rootExitCode"] = 1
        path.write_text(json.dumps(proof), encoding="utf-8")
        self.run_validator(0)
        self.assertEqual(1, json.loads(path.read_text())["rootExitCode"])
        proof.update(rootKernelSignaled=False, activeProcesses=1, noActiveOwnedConsumers=False)
        path.write_text(json.dumps(proof), encoding="utf-8")
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
        for proofs, command in self.scopes:
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
        for proofs, command in self.scopes:
            for path, proof in proofs:
                for content in (
                    b"{",
                    b"null",
                    b"[]",
                    b"x" * 65537,
                    b"\xff",
                    (json.dumps(proof)[:-1] + ', "activeProcesses":0}').encode(),
                    json.dumps({**proof, "unknown": True}).encode(),
                ):
                    with self.subTest(path=path.name, content=content[:40], command=command):
                        path.write_bytes(content)
                        self.run_validator(1, command)
                path.unlink()
                self.run_validator(1, command)
                path.mkdir()
                self.run_validator(1, command)
                path.rmdir()
                path.write_text(json.dumps(proof), encoding="utf-8")

    def test_cancelled_or_unknown_invocation_cannot_authorize(self):
        for _, invocation in self.scopes:
            for value in ("true", "unknown", ""):
                command = invocation.copy()
                command[command.index("--workflow-cancelled") + 1] = value
                self.run_validator(1, command)
            for flag in (
                "--run-id",
                "--run-attempt",
                "--head",
                "--continuous-nonce",
                "--notepad-nonce",
            ):
                if flag not in invocation:
                    continue
                command = invocation.copy()
                command[command.index(flag) + 1] = ""
                self.run_validator(1, command)

    def test_swapped_reports_fail_closed(self):
        first, second = (entry[0] for entry in self.proofs)
        first_bytes, second_bytes = first.read_bytes(), second.read_bytes()
        first.write_bytes(second_bytes)
        second.write_bytes(first_bytes)
        self.run_validator(1)
        self.run_validator(1, self.continuous_command)

    def test_missing_required_context_is_not_inferred_from_reports(self):
        for _, invocation in self.scopes:
            for flag in (
                "--scope",
                "--workflow-cancelled",
                "--run-id",
                "--run-attempt",
                "--head",
                "--continuous-nonce",
                "--notepad-nonce",
                "--continuous-proof",
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
        path, original = self.proofs[1]
        path.write_text(json.dumps({**original, "nonce": "b" * 32}), encoding="utf-8")
        command = self.command.copy()
        command[command.index("--notepad-nonce") + 1] = "b" * 32
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
