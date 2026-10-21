"""Status age and correlated direct-result oracles; not GUI or model evidence."""

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from desktop_e2e import (  # noqa: E402
    acceptance_observation_current,
    background_direct_rejection_current,
)


class AcceptanceObservationTests(unittest.TestCase):
    def test_only_positive_original_remaining_time_is_current(self):
        data = {"acceptanceReady": True, "acceptanceRemainingMs": 2}
        self.assertTrue(acceptance_observation_current(data, 1))
        self.assertFalse(acceptance_observation_current(data, 2))
        self.assertFalse(acceptance_observation_current(data, 3))
        self.assertEqual(data["acceptanceRemainingMs"], 2)

    def test_unknown_and_invalid_time_never_grant_readiness(self):
        for remaining in (None, True, "2", float("nan"), float("inf"), -1, 0):
            with self.subTest(remaining=remaining):
                self.assertFalse(
                    acceptance_observation_current(
                        {"acceptanceReady": True, "acceptanceRemainingMs": remaining}, 0
                    )
                )
        for elapsed in (None, True, "1", float("nan"), float("inf"), -1):
            with self.subTest(elapsed=elapsed):
                self.assertFalse(
                    acceptance_observation_current(
                        {"acceptanceReady": True, "acceptanceRemainingMs": 2}, elapsed
                    )
                )
        self.assertFalse(acceptance_observation_current({}, 0))

    def test_false_or_non_boolean_ready_is_not_a_cue(self):
        for ready in (False, None, 1, "true"):
            with self.subTest(ready=ready):
                self.assertFalse(
                    acceptance_observation_current(
                        {"acceptanceReady": ready, "acceptanceRemainingMs": 100}, 0
                    )
                )


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


if __name__ == "__main__":
    unittest.main()
