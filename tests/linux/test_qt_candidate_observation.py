"""Snapshot observation contracts, not Qt GUI or model-quality evidence."""

import copy
import sys
import unittest
from pathlib import Path
from unittest.mock import Mock, call, patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from qt_cooperative.run import prepare_source_glossary_capture_basis  # noqa: E402
from qt_desktop import displayed_wake_candidate  # noqa: E402


class SourceGlossaryCaptureBasisTests(unittest.TestCase):
    def description(self, *, flags=3, applied=235):
        return {
            "target": "original-source-target",
            "focused": True,
            "metadata": {"automationId": "plain"},
            "authority": {
                "target": "original-source-target",
                "contentRevision": 21,
                "inputRevision": 126,
                "focusRevision": 69,
                "observedAtMs": 681134,
                "contextStamp": {
                    "context": 2,
                    "field": 31,
                    "epoch": 1335,
                    "input": 0,
                    "server": 235,
                    "applied": applied,
                    "flags": flags,
                },
            },
        }

    def test_empty_local_queues_do_not_make_pending_describe_a_capture_basis(self):
        pending = self.description(flags=7, applied=234)
        clear = self.description()
        clear["authority"]["contextStamp"]["epoch"] = 1336
        clear["authority"]["observedAtMs"] = 681150
        observations = iter((pending, clear))
        with patch("qt_cooperative.run.time.sleep"):
            actual = prepare_source_glossary_capture_basis(
                lambda target=None: next(observations), lambda: True, "plain"
            )
        self.assertIs(actual, clear)

    def test_preparation_keeps_the_source_but_freezes_only_the_final_authority(self):
        pending = self.description(flags=7, applied=234)
        clear = self.description()
        clear["authority"].update(inputRevision=127, focusRevision=70)
        clear["authority"]["contextStamp"].update(context=3, field=32, input=1)
        original = copy.deepcopy([pending, clear])
        describe = Mock(side_effect=[pending, clear])
        with patch("qt_cooperative.run.time.sleep"):
            actual = prepare_source_glossary_capture_basis(describe, lambda: True, "plain")
        self.assertIs(actual, clear)
        self.assertEqual(describe.call_args_list, [call(None), call("original-source-target")])
        self.assertEqual([pending, clear], original)

    def test_unknown_unfocused_pending_composing_and_unsynced_sources_are_not_ready(self):
        for section, key, value in (
            ("authority", "contextStamp", {}),
            (None, "focused", False),
            ("stamp", "flags", 1),
            ("stamp", "flags", 7),
            ("stamp", "flags", 11),
            ("stamp", "flags", 17),
            ("stamp", "context", 0),
            ("stamp", "field", 0),
            ("stamp", "applied", 234),
            ("stamp", "server", None),
        ):
            with self.subTest(key=key, value=value):
                unclear = self.description()
                container = (
                    unclear["authority"]["contextStamp"]
                    if section == "stamp"
                    else unclear[section]
                    if section
                    else unclear
                )
                container[key] = value
                clear = self.description()
                describe = Mock(side_effect=[unclear, clear])
                with patch("qt_cooperative.run.time.sleep"):
                    actual = prepare_source_glossary_capture_basis(describe, lambda: True, "plain")
                self.assertIs(actual, clear)

    def test_preparation_cannot_switch_to_another_source(self):
        for section, key, value in (
            (None, "target", "another-target"),
            ("authority", "target", "another-target"),
            ("metadata", "automationId", "second"),
        ):
            with self.subTest(section=section):
                pending = self.description(flags=7, applied=234)
                other = self.description()
                (other[section] if section else other)[key] = value
                describe = Mock(side_effect=[pending, other])
                with patch("qt_cooperative.run.time.sleep"), self.assertRaises(AssertionError):
                    prepare_source_glossary_capture_basis(describe, lambda: True, "plain")
                self.assertEqual(
                    describe.call_args_list, [call(None), call("original-source-target")]
                )

    def test_queue_and_provider_preparation_share_the_original_fifteen_second_window(self):
        settled = Mock(side_effect=[False, True])
        describe = Mock(return_value=self.description(flags=7, applied=234))
        with (
            patch("qt_cooperative.run.time.monotonic", side_effect=[100, 100, 114, 115]),
            patch("qt_cooperative.run.time.sleep"),
            self.assertRaisesRegex(AssertionError, "Timed out"),
        ):
            prepare_source_glossary_capture_basis(describe, settled, "plain")
        describe.assert_called_once_with(None)


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
