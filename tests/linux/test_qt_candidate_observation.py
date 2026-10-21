"""Snapshot observation contracts, not Qt GUI or model-quality evidence."""

import copy
import sys
import unittest
from pathlib import Path
from unittest.mock import Mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from qt_desktop import displayed_wake_candidate  # noqa: E402


class WakeCandidateObservationTests(unittest.TestCase):
    def frame(self, candidate):
        return {
            "first": {
                "text": "Send this.",
                "focused": True,
                "selectionStart": -1,
                "selectionLength": 0,
                "cursor": 10,
            },
            "overlayVisible": True,
            "editorVisible": True,
            "editorReadOnly": True,
            "editorText": candidate,
            "tlqtPendingDbus": 0,
            "tlqtPendingForward": 0,
            "tlqtOutputDepth": 0,
        }

    def test_ready_host_does_not_make_an_old_empty_frame_a_candidate(self):
        ready = Mock(return_value=True)
        frames = [self.frame(""), self.frame("发送这个。")]
        self.assertIsNone(displayed_wake_candidate(frames[0], "Send this.", ready))
        ready.assert_not_called()
        self.assertIs(displayed_wake_candidate(frames[1], "Send this.", ready), frames[1])
        ready.assert_called_once_with()

    def test_observation_does_not_select_a_language_or_wait_internally(self):
        ready = Mock(return_value=True)
        # Wrong-language nonempty output must reach the caller's original
        # language assertion, not be filtered until a preferred answer appears.
        frame = self.frame("wrong language")
        self.assertIs(displayed_wake_candidate(frame, "Send this.", ready), frame)
        for _ in range(3):
            self.assertIsNone(displayed_wake_candidate(self.frame(""), "Send this.", ready))
        ready.assert_called_once_with()

    def test_source_selection_focus_visibility_and_current_readiness_remain_required(self):
        changes = [
            ("first", "text", "changed"),
            ("first", "focused", False),
            ("first", "selectionStart", 0),
            ("first", "selectionLength", 1),
            ("first", "cursor", 9),
            (None, "editorVisible", False),
            (None, "overlayVisible", False),
            (None, "editorReadOnly", False),
            (None, "tlqtPendingForward", 1),
            (None, "tlqtOutputDepth", None),
        ]
        for section, key, value in changes:
            with self.subTest(key=key):
                frame = copy.deepcopy(self.frame("发送这个。"))
                (frame[section] if section else frame)[key] = value
                self.assertIsNone(displayed_wake_candidate(frame, "Send this.", lambda: True))
        self.assertIsNone(
            displayed_wake_candidate(self.frame("发送这个。"), "Send this.", lambda: False)
        )


if __name__ == "__main__":
    unittest.main()
