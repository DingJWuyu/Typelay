"""Status age and correlated direct-result oracles; not GUI or model evidence."""

import hashlib
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from desktop_e2e import (  # noqa: E402
    acceptance_observation_current,
    background_direct_rejection_current,
    gtk_candidate_observation_current,
)


class AcceptanceObservationTests(unittest.TestCase):
    def test_only_positive_public_hint_time_is_current(self):
        data = {
            "acceptanceReady": True,
            "acceptanceRemainingMs": 27,
            "acceptanceHintRemainingMs": 2,
        }
        self.assertTrue(acceptance_observation_current(data, 1))
        self.assertFalse(acceptance_observation_current(data, 2))
        self.assertFalse(acceptance_observation_current(data, 3))
        self.assertEqual(data["acceptanceRemainingMs"], 27)
        self.assertEqual(data["acceptanceHintRemainingMs"], 2)

    def test_original_permission_alone_cannot_stand_in_for_a_public_hint(self):
        self.assertFalse(
            acceptance_observation_current(
                {"acceptanceReady": True, "acceptanceRemainingMs": 100}, 0
            )
        )
        self.assertFalse(
            acceptance_observation_current(
                {
                    "acceptanceReady": True,
                    "acceptanceRemainingMs": 25,
                    "acceptanceHintRemainingMs": 0,
                },
                0,
            )
        )

    def test_unknown_and_invalid_time_never_grant_readiness(self):
        for remaining in (None, True, "2", float("nan"), float("inf"), -1, 0):
            with self.subTest(remaining=remaining):
                self.assertFalse(
                    acceptance_observation_current(
                        {
                            "acceptanceReady": True,
                            "acceptanceRemainingMs": 100,
                            "acceptanceHintRemainingMs": remaining,
                        },
                        0,
                    )
                )
        for elapsed in (None, True, "1", float("nan"), float("inf"), -1):
            with self.subTest(elapsed=elapsed):
                self.assertFalse(
                    acceptance_observation_current(
                        {
                            "acceptanceReady": True,
                            "acceptanceRemainingMs": 27,
                            "acceptanceHintRemainingMs": 2,
                        },
                        elapsed,
                    )
                )
        self.assertFalse(acceptance_observation_current({}, 0))

    def test_hint_cannot_outlive_unknown_or_shorter_original_permission(self):
        for permission in (None, True, "100", float("nan"), float("inf"), -1, 0, 1):
            with self.subTest(permission=permission):
                self.assertFalse(
                    acceptance_observation_current(
                        {
                            "acceptanceReady": True,
                            "acceptanceRemainingMs": permission,
                            "acceptanceHintRemainingMs": 2,
                        },
                        0,
                    )
                )

    def test_false_or_non_boolean_ready_is_not_a_cue(self):
        for ready in (False, None, 1, "true"):
            with self.subTest(ready=ready):
                self.assertFalse(
                    acceptance_observation_current(
                        {
                            "acceptanceReady": ready,
                            "acceptanceRemainingMs": 100,
                            "acceptanceHintRemainingMs": 75,
                        },
                        0,
                    )
                )


class GtkCandidateObservationTests(unittest.TestCase):
    def snapshot(self):
        return {
            "observedAtMs": 120,
            "first": "Please send me the latest report.",
            "focus": "fixture-first",
            "focusedImeClear": True,
            "focusedImeComposing": False,
            "candidate": {"text": "请给我最新的报告。", "editablePreview": True},
        }

    def test_current_candidate_keeps_source_and_counts_in_one_snapshot(self):
        snapshot = self.snapshot()
        snapshot["firstEntry"] = {"insertions": 68, "deletions": 4}
        self.assertIs(gtk_candidate_observation_current(snapshot, snapshot["first"], 120), snapshot)

    def test_empty_or_uneditable_preview_is_not_the_expected_candidate(self):
        for candidate in (
            None,
            {},
            {"text": "", "editablePreview": True},
            {"text": "old preview", "editablePreview": False},
        ):
            with self.subTest(candidate=candidate):
                snapshot = self.snapshot()
                snapshot["candidate"] = candidate
                self.assertIsNone(
                    gtk_candidate_observation_current(snapshot, snapshot["first"], 120)
                )

    def test_old_or_unrelated_source_and_unclear_context_are_rejected(self):
        for change in (
            {"observedAtMs": 119},
            {"first": "another source"},
            {"focus": "fixture-second"},
            {"focusedImeClear": False},
            {"focusedImeComposing": True},
        ):
            with self.subTest(change=change):
                snapshot = self.snapshot()
                source = snapshot["first"]
                snapshot.update(change)
                self.assertIsNone(gtk_candidate_observation_current(snapshot, source, 120))
        self.assertIsNone(gtk_candidate_observation_current({}, "source", 120))


class BackgroundDirectRefusalTests(unittest.TestCase):
    def refusal(self, reason="ImeUnclear"):
        rows = [
            {"operation": "direct-write-start", "planId": "current-plan"},
            {
                "operation": "direct-write-result",
                "planId": "current-plan",
                "detail": {"status": "RejectedBeforeMutation", "reason": reason},
            },
        ]
        report = {
            "serviceStatus": (
                "输入法状态尚未确认，TypeLay 未替换文字。请完成或取消当前输入后再操作。"
                if reason == "ImeUnclear"
                else "输入内容、选区或字段已变化，TypeLay 未替换文字。请确认目标输入框后再操作。"
            ),
            "operationFeedback": {"code": "write-rejected", "revision": 9},
        }
        return report, rows

    def test_only_matching_plan_refusal_and_new_corresponding_feedback_complete(self):
        for reason in (
            "ImeUnclear",
            "SourceChanged",
            "SelectionChanged",
            "TargetChanged",
            "EpochChanged",
        ):
            with self.subTest(reason=reason):
                report, rows = self.refusal(reason)
                self.assertTrue(background_direct_rejection_current(report, rows, 8))
                self.assertFalse(background_direct_rejection_current(report, rows, 9))
                report["serviceStatus"] = "已直接替换原输入框文字。"
                self.assertFalse(background_direct_rejection_current(report, rows, 8))

    def test_missing_duplicate_old_or_mismatched_plan_does_not_complete(self):
        report, rows = self.refusal()
        for incomplete in ([], rows[:1], rows[1:], rows * 2, list(reversed(rows))):
            self.assertFalse(background_direct_rejection_current(report, incomplete, 8))
        rows[1]["planId"] = "old-plan"
        self.assertFalse(background_direct_rejection_current(report, rows, 8))

    def test_other_reason_success_or_wrong_feedback_does_not_complete(self):
        report, rows = self.refusal("Unsupported")
        self.assertFalse(background_direct_rejection_current(report, rows, 8))
        report, rows = self.refusal()
        rows[1]["detail"]["status"] = "Verified"
        self.assertFalse(background_direct_rejection_current(report, rows, 8))
        report, rows = self.refusal()
        report["serviceStatus"] = self.refusal("SourceChanged")[0]["serviceStatus"]
        self.assertFalse(background_direct_rejection_current(report, rows, 8))
        report, rows = self.refusal()
        report["operationFeedback"]["code"] = "write-unconfirmed"
        self.assertFalse(background_direct_rejection_current(report, rows, 8))


class AcceptanceInjectionTests(unittest.TestCase):
    def test_only_default_gtk4_tab_uses_preconnected_single_injection(self):
        from desktop_e2e import send_gtk_acceptance

        for gtk4, shortcut, expected in (
            (True, "Tab", "preconnected"),
            (False, "Tab", "configured"),
            (True, "Ctrl+F7", "configured"),
        ):
            calls = []
            timing = {}

            def tab(*, timing):
                calls.append(("preconnected", "Tab", timing))

            def configured(value, *, timing):
                calls.append(("configured", value, timing))

            send_gtk_acceptance(gtk4, shortcut, tab, configured, timing)
            self.assertEqual(calls, [(expected, shortcut, timing)])


class AcceptanceAttemptTests(unittest.TestCase):
    def test_trace_io_finishes_before_readiness_and_one_original_pair(self):
        from desktop_e2e import observe_and_send_gtk_acceptance

        clock = [0]
        sent = []
        phase = {}

        def checkpoint():
            # Synthetic slow trace I/O, not a decomposition of CI461's delay.
            clock[0] += 120
            return 42

        def ready():
            return {"expiresAtMs": clock[0] + 95, "candidate": "exact manual draft"}

        def send(*, timing):
            self.assertLess(clock[0], phase["readiness"]["expiresAtMs"])
            sent.extend(("down", "up"))
            timing["downStartedAtMs"] = clock[0]

        with patch("desktop_e2e.now_ms", side_effect=lambda: clock[0]):
            before = observe_and_send_gtk_acceptance(checkpoint, ready, send, phase)
        self.assertEqual(before, 42)
        self.assertEqual(sent, ["down", "up"])
        self.assertEqual(phase["checkpointFinishedAtMs"], 120)
        self.assertEqual(phase["readinessStartedAtMs"], 120)
        self.assertEqual(
            phase["readiness"], {"expiresAtMs": 215, "candidate": "exact manual draft"}
        )
        self.assertEqual(phase["keySendStartedAtMs"], 120)
        self.assertEqual(phase["injection"], {"downStartedAtMs": 120})

    def test_failed_send_keeps_attempt_timing_without_replay(self):
        from desktop_e2e import observe_and_send_gtk_acceptance

        phase = {}
        sent = []

        def send(*, timing):
            sent.append("down")
            timing["downStartedAtMs"] = 10
            raise OSError("injection failed")

        with patch("desktop_e2e.now_ms", return_value=10):
            with self.assertRaisesRegex(OSError, "injection failed"):
                observe_and_send_gtk_acceptance(lambda: 42, lambda: {"ready": True}, send, phase)
        self.assertEqual(sent, ["down"])
        self.assertEqual(phase["keySendFinishedAtMs"], 10)
        self.assertEqual(phase["injection"], {"downStartedAtMs": 10})

    def test_failed_readiness_sends_nothing(self):
        from desktop_e2e import observe_and_send_gtk_acceptance

        sent = []
        phase = {}

        def ready():
            raise AssertionError("manual candidate not ready")

        with self.assertRaisesRegex(AssertionError, "manual candidate not ready"):
            observe_and_send_gtk_acceptance(
                lambda: 42, ready, lambda **_: sent.append("down"), phase
            )
        self.assertEqual(sent, [])
        self.assertNotIn("keySendStartedAtMs", phase)


class FixtureIdentityTests(unittest.TestCase):
    def test_records_only_expected_test_process_files(self):
        from desktop_e2e import fixture_module_identity

        executable = Path(sys.executable).resolve()
        record = fixture_module_identity(os.getpid(), executable, executable)
        self.assertEqual(set(record), {"pid", "startTimeTicks", "files"})
        self.assertEqual(record["pid"], os.getpid())
        self.assertGreater(record["startTimeTicks"], 0)
        expected = {
            "path": str(executable),
            "sha256": hashlib.sha256(executable.read_bytes()).hexdigest(),
        }
        self.assertEqual(record["files"], [expected, expected])

    def test_unmapped_file_cannot_be_reported_as_loaded(self):
        from desktop_e2e import fixture_module_identity

        with tempfile.NamedTemporaryFile() as unmapped:
            with self.assertRaisesRegex(AssertionError, "did not load expected module"):
                fixture_module_identity(os.getpid(), Path(sys.executable), Path(unmapped.name))


if __name__ == "__main__":
    unittest.main()
