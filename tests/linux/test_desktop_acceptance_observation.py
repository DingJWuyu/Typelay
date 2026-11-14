"""Status age and correlated direct-result oracles; not GUI or model evidence."""

import hashlib
import os
import random
import sys
import tempfile
import unittest
from itertools import permutations
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from desktop_e2e import (  # noqa: E402
    acceptance_observation_current,
    background_direct_rejection_current,
    background_direct_settlement,
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
            {"text": "preview without an editing cue"},
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


class BackgroundDirectSettlementTests(unittest.TestCase):
    def rows(self):
        return [
            {"operation": "direct-write-start", "planId": "direct-plan"},
            {"operation": "write-Verified-None", "planId": "direct-plan"},
            {
                "operation": "direct-write-result",
                "planId": "direct-plan",
                "detail": {"status": "Verified", "reason": "None"},
            },
        ]

    def test_direct_result_matches_its_single_common_settlement(self):
        rows = self.rows()
        self.assertEqual(background_direct_settlement(rows), rows[-1])

    def test_partial_duplicate_other_plan_and_tab_submission_do_not_complete(self):
        rows = self.rows()
        for invalid in (
            [],
            rows[:2],
            [rows[0], rows[-1]],
            rows + [rows[1]],
            list(reversed(rows)),
            [rows[0], {**rows[1], "planId": "other-plan"}, rows[-1]],
            [rows[0], {**rows[1], "operation": "write-OutcomeUnknown-None"}, rows[-1]],
            [{"operation": "write-start", "planId": "tab-plan"}, *rows],
        ):
            with self.subTest(rows=invalid):
                self.assertIsNone(background_direct_settlement(invalid))

    def generated_settlements(self):
        # Literal receipt pairs are independent examples of the public ledger.
        outcomes = (
            ("write-Verified-None", {"status": "Verified", "reason": "None"}),
            (
                "write-RejectedBeforeMutation-ImeUnclear",
                {"status": "RejectedBeforeMutation", "reason": "ImeUnclear"},
            ),
            (
                "write-RejectedBeforeMutation-SourceChanged",
                {"status": "RejectedBeforeMutation", "reason": "SourceChanged"},
            ),
            (
                "write-OutcomeUnknown-None",
                {"status": "OutcomeUnknown", "reason": "None"},
            ),
        )
        identities = random.Random(1410)
        for sample in range(64):
            plan = f"plan-{identities.getrandbits(128):032x}"
            other = f"other-{identities.getrandbits(128):032x}"
            for operation, detail in outcomes:
                yield (
                    sample,
                    other,
                    [
                        {"operation": "direct-write-start", "planId": plan},
                        {"operation": operation, "planId": plan},
                        {"operation": "direct-write-result", "planId": plan, "detail": detail},
                    ],
                )

    def test_consistent_identity_renaming_preserves_the_receipt(self):
        for sample, renamed_plan, rows in self.generated_settlements():
            with self.subTest(sample=sample, terminal=rows[1]["operation"]):
                original = background_direct_settlement(rows)
                renamed = [{**row, "planId": renamed_plan} for row in rows]
                self.assertEqual(original, rows[-1])
                self.assertEqual(
                    background_direct_settlement(renamed),
                    {**original, "planId": renamed_plan},
                )

    def test_receipts_from_another_plan_cannot_complete_a_settlement(self):
        for sample, other, rows in self.generated_settlements():
            for changed in range(3):
                with self.subTest(sample=sample, terminal=rows[1]["operation"], changed=changed):
                    mixed = [
                        {**row, "planId": other} if index == changed else row
                        for index, row in enumerate(rows)
                    ]
                    self.assertIsNone(background_direct_settlement(mixed))

    def test_duplicate_terminal_receipts_never_complete_the_plan(self):
        for sample, _, rows in self.generated_settlements():
            for terminal in rows[1:]:
                for copies in range(1, 4):
                    for position in range(4):
                        with self.subTest(
                            sample=sample,
                            terminal=terminal["operation"],
                            copies=copies,
                            position=position,
                        ):
                            duplicated = rows[:position] + [terminal] * copies + rows[position:]
                            self.assertIsNone(background_direct_settlement(duplicated))

    def test_lifecycle_order_survives_unrelated_observations_only(self):
        for sample, _, rows in self.generated_settlements():
            for order in permutations(range(3)):
                for position in range(4):
                    with self.subTest(
                        sample=sample,
                        terminal=rows[1]["operation"],
                        order=order,
                        position=position,
                    ):
                        sequence = [rows[index] for index in order]
                        sequence.insert(position, {"operation": "model-ready"})
                        observed = background_direct_settlement(sequence)
                        if order == (0, 1, 2):
                            self.assertEqual(observed, rows[-1])
                        else:
                            self.assertIsNone(observed)


class BackgroundDirectRefusalTests(unittest.TestCase):
    def refusal(self, reason="ImeUnclear"):
        outcome = {
            "operation": "direct-write-result",
            "planId": "current-plan",
            "detail": {"status": "RejectedBeforeMutation", "reason": reason},
        }
        report = {
            "serviceStatus": (
                "输入法状态尚未确认，TypeLay 未替换文字。请完成或取消当前输入后再操作。"
                if reason == "ImeUnclear"
                else "输入内容、选区或字段已变化，TypeLay 未替换文字。请确认目标输入框后再操作。"
            ),
            "operationFeedback": {"code": "write-rejected", "revision": 9},
        }
        return report, outcome

    def test_settled_refusal_requires_new_corresponding_feedback(self):
        for reason in (
            "ImeUnclear",
            "SourceChanged",
            "SelectionChanged",
            "TargetChanged",
            "EpochChanged",
        ):
            with self.subTest(reason=reason):
                report, outcome = self.refusal(reason)
                self.assertTrue(background_direct_rejection_current(report, outcome, 8))
                self.assertFalse(background_direct_rejection_current(report, outcome, 9))
                report["serviceStatus"] = "已直接替换原输入框文字。"
                self.assertFalse(background_direct_rejection_current(report, outcome, 8))

    def test_other_reason_success_or_wrong_feedback_does_not_complete(self):
        report, outcome = self.refusal("Unsupported")
        self.assertFalse(background_direct_rejection_current(report, outcome, 8))
        report, outcome = self.refusal()
        outcome["detail"]["status"] = "Verified"
        self.assertFalse(background_direct_rejection_current(report, outcome, 8))
        report, outcome = self.refusal()
        report["serviceStatus"] = self.refusal("SourceChanged")[0]["serviceStatus"]
        self.assertFalse(background_direct_rejection_current(report, outcome, 8))
        report, outcome = self.refusal()
        report["operationFeedback"]["code"] = "write-unconfirmed"
        self.assertFalse(background_direct_rejection_current(report, outcome, 8))


class AcceptanceInjectionTests(unittest.TestCase):
    def test_xdotool_timing_separates_one_send_from_the_existing_post_key_sleep(self):
        from native_e2e import key

        timing = {}
        with (
            patch("native_e2e.subprocess.run") as send,
            patch("native_e2e.time.sleep") as sleep,
            patch("native_e2e.now_ms", side_effect=[100, 107, 132]),
        ):
            key("Tab", timing=timing)
        send.assert_called_once_with(
            ["xdotool", "key", "--clearmodifiers", "--delay", "0", "Tab"], check=True, timeout=2
        )
        sleep.assert_called_once_with(0.025)
        self.assertEqual(
            timing,
            {
                "xdotoolStartedAtMs": 100,
                "xdotoolReturnedAtMs": 107,
                "postKeySleepFinishedAtMs": 132,
            },
        )

    def test_failed_xdotool_send_keeps_only_start_time_without_replay(self):
        from native_e2e import key

        timing = {}
        with (
            patch("native_e2e.subprocess.run", side_effect=OSError("send failed")) as send,
            patch("native_e2e.time.sleep") as sleep,
            patch("native_e2e.now_ms", return_value=100),
            self.assertRaisesRegex(OSError, "send failed"),
        ):
            key("Tab", timing=timing)
        send.assert_called_once()
        sleep.assert_not_called()
        self.assertEqual(timing, {"xdotoolStartedAtMs": 100})

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
