"""Exercise fail-closed report behavior; these are not Windows/IME evidence."""

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

SPEC = importlib.util.spec_from_file_location(
    "tsf_preflight", Path(__file__).with_name("preflight.py")
)
probe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe)


class PreflightTests(unittest.TestCase):
    def assert_stdlib_process_unaffected(self):
        # A real unrelated stdlib child must remain usable while only the
        # probe query is doubled. platform.system itself remains unpatched.
        value = subprocess.check_output(
            [sys.executable, "-c", "print('stdlib-unaffected')"], text=True, timeout=10
        )
        self.assertEqual("stdlib-unaffected", value.strip())

    def test_push_gate_requires_this_commit_and_actual_probe_or_workflow_change(self):
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
        self.assertTrue(
            probe.push_eligible(event, head, message, [".github/workflows/tsf-preflight.yml"], True)
        )
        for unrelated in (".github/workflows/check.yml", "README.md", "src/windows/example.cs"):
            self.assertFalse(probe.push_eligible(event, head, message, [unrelated], True))
        no_opt_in = "fix: repair diagnostic configuration"
        unmarked = event | {"head_commit": {"id": head, "message": no_opt_in}}
        self.assertFalse(
            probe.push_eligible(
                unmarked, head, no_opt_in, [".github/workflows/tsf-preflight.yml"], True
            )
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

    def test_all_three_levels_are_exclusive_and_unknown_refused(self):
        head = "a" * 40
        for requested in probe.MARKERS:
            message = "test: changed diagnostic source\n\n" + requested
            event = {
                "ref": "refs/heads/work/linux-usability",
                "before": "b" * 40,
                "after": head,
                "head_commit": {"id": head, "message": message},
            }
            paths = [".github/workflows/tsf-preflight.yml"]
            with self.assertRaises(ValueError):
                probe.push_eligible(event, head, message, paths, True, marker="unknown-level")
            for level in probe.MARKERS:
                self.assertEqual(
                    level == requested,
                    probe.push_eligible(event, head, message, paths, True, marker=level),
                )
            for other in probe.MARKERS:
                if other == requested:
                    continue
                mixed = message + "\n\n" + other
                event["head_commit"]["message"] = mixed
                for level in (requested, other):
                    with self.assertRaises(ValueError):
                        probe.push_eligible(event, head, mixed, paths, True, marker=level)

    def test_public_push_gate_keeps_execution_levels_separate(self):
        script = Path(probe.__file__).resolve()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)

            def git(*args):
                return subprocess.run(
                    ["git", *args],
                    cwd=root,
                    capture_output=True,
                    text=True,
                    check=True,
                    timeout=10,
                ).stdout.strip()

            def commit(message):
                git("add", ".")
                git(
                    "-c",
                    "user.name=Probe Test",
                    "-c",
                    "user.email=probe@example.invalid",
                    "commit",
                    "-qm",
                    message,
                )
                return git("rev-parse", "HEAD")

            git("init", "-q")
            source = root / "tests/windows/tsf_probe/input.txt"
            source.parent.mkdir(parents=True)
            source.write_text("baseline", encoding="utf-8")
            before = commit("baseline")
            for marker in (*probe.MARKERS, "TypeLay-TSF-Unknown: true", "no opt-in"):
                source.write_text(marker, encoding="utf-8")
                message = "test: actual diagnostic source change\n\n" + marker
                after = commit(message)
                event = {
                    "ref": "refs/heads/work/linux-usability",
                    "before": before,
                    "after": after,
                    "head_commit": {"id": after, "message": message},
                }
                event_path = root / "event.json"
                event_path.write_text(json.dumps(event), encoding="utf-8")
                for flag in ("--check-push", "--check-native-push"):
                    output = root / "output.txt"
                    output.write_text("", encoding="utf-8")
                    result = subprocess.run(
                        [sys.executable, str(script), flag],
                        cwd=root,
                        capture_output=True,
                        text=True,
                        timeout=20,
                        check=False,
                        env=os.environ
                        | {
                            "GITHUB_EVENT_NAME": "push",
                            "GITHUB_EVENT_PATH": str(event_path),
                            "GITHUB_OUTPUT": str(output),
                        },
                    )
                    self.assertEqual(0, result.returncode, result.stderr)
                    native = flag == "--check-native-push"
                    eligible = (
                        marker in (probe.LIFECYCLE_MARKER, probe.READ_MARKER)
                        if native
                        else marker == probe.MARKER
                    )
                    level = (
                        ("read" if marker == probe.READ_MARKER else "lifecycle")
                        if eligible
                        else "none"
                    )
                    expected = f"eligible={str(eligible).lower()}\n" + (
                        f"level={level}\n" if native else ""
                    )
                    self.assertEqual(expected, output.read_text(encoding="utf-8"))
                before = after

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
                # Replace only the probe's dependency, not the shared stdlib
                # module used internally by Windows platform initialization.
                patch.object(
                    probe,
                    "subprocess",
                    SimpleNamespace(
                        run=Mock(side_effect=subprocess.TimeoutExpired("fixed-query", 40)),
                        SubprocessError=subprocess.SubprocessError,
                    ),
                ),
                patch.object(probe, "native_observation") as native,
            ):
                self.assert_stdlib_process_unaffected()
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
                patch.object(
                    probe,
                    "subprocess",
                    SimpleNamespace(run=Mock(), SubprocessError=subprocess.SubprocessError),
                ) as query,
            ):
                self.assert_stdlib_process_unaffected()
                self.assertEqual(2, probe.main(["--output", str(path)]))
                native.assert_not_called()
                query.run.assert_not_called()
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
