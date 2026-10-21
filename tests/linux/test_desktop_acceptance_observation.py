"""Status-response age checks; not GUI delivery or model-quality evidence."""

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from desktop_e2e import acceptance_observation_current  # noqa: E402


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


if __name__ == "__main__":
    unittest.main()
