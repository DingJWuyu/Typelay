"""Process/snapshot observation contracts, not Qt GUI or model-quality evidence."""

import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, call, patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from desktop_e2e import background_direct_settlement  # noqa: E402
from qt_cooperative.run import prepare_source_glossary_capture_basis  # noqa: E402
from qt_desktop import (  # noqa: E402
    displayed_wake_candidate,
    observe_qt_processes,
    settled_overlay_motion,
)


class QtDirectSettlementTests(unittest.TestCase):
    def test_source_change_rejection_has_one_original_plan_and_no_success_receipt(self):
        # Public trace from PR40's Qt post-snapshot source-change scene.
        plan = "42df515e-d5c0-40a2-aed0-e0fb08e07325"
        rows = [
            {"atMs": 1609592, "operation": "direct-write-start", "planId": plan},
            {
                "atMs": 1609594,
                "operation": "write-RejectedBeforeMutation-EpochChanged",
                "planId": plan,
            },
            {
                "atMs": 1609594,
                "operation": "direct-write-result",
                "planId": plan,
                "detail": {"status": "RejectedBeforeMutation", "reason": "EpochChanged"},
            },
        ]
        self.assertEqual(background_direct_settlement(rows), rows[-1])
        for index in range(3):
            changed = copy.deepcopy(rows)
            changed[index]["planId"] = "different-plan"
            self.assertIsNone(background_direct_settlement(changed))
        for extra in (
            {"operation": "write-Verified-None", "planId": plan},
            {"operation": "write-OutcomeUnknown-None", "planId": plan},
            rows[1],
        ):
            self.assertIsNone(background_direct_settlement([*rows, extra]))


class QtProcessObservationTests(unittest.TestCase):
    def start_child(self):
        # Real owned child processes, with stdin as a deterministic exit trigger.
        # These exercise the scenario's polling boundary, not a fake Qt model.
        child = subprocess.Popen(
            [sys.executable, "-c", "import sys; sys.exit(int(sys.stdin.readline()))"],
            stdin=subprocess.PIPE,
            text=True,
        )
        self.addCleanup(self.close_child, child)
        return child

    @staticmethod
    def close_child(child):
        if child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=3)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=3)
        child.stdin.close()

    @staticmethod
    def exit_child(child, code):
        child.stdin.write(f"{code}\n")
        child.stdin.flush()
        child.wait(timeout=3)

    def test_live_processes_preserve_pending_and_successful_observations(self):
        fixture, daemon = self.start_child(), self.start_child()
        for value in (None, False, {}, {"acceptanceReady": True}):
            with self.subTest(value=value):
                observe = Mock(return_value=value)
                self.assertIs(observe_qt_processes(fixture, daemon, observe), value)
                observe.assert_called_once_with()

    def test_exited_process_rejects_a_stale_success_before_reading_it(self):
        with tempfile.TemporaryDirectory() as temporary:
            snapshot = Path(temporary) / "state.json"
            snapshot.write_text(json.dumps({"acceptanceReady": True}))
            for index, name in enumerate(("Qt fixture", "private Fcitx")):
                for code in (0, 9):
                    with self.subTest(process=name, code=code):
                        fixture, daemon = self.start_child(), self.start_child()
                        self.exit_child((fixture, daemon)[index], code)
                        observe = Mock(side_effect=lambda: json.loads(snapshot.read_text()))
                        with self.assertRaisesRegex(AssertionError, f"{name} exited.*{code}"):
                            observe_qt_processes(fixture, daemon, observe)
                        observe.assert_not_called()

    def test_exit_during_observation_cannot_return_a_successful_candidate(self):
        for index, name in enumerate(("Qt fixture", "private Fcitx")):
            with self.subTest(process=name):
                fixture, daemon = self.start_child(), self.start_child()

                def observe():
                    self.exit_child((fixture, daemon)[index], 7)
                    return {"acceptanceReady": True}

                with self.assertRaisesRegex(AssertionError, f"{name} exited.*7"):
                    observe_qt_processes(fixture, daemon, observe)


class OverlayMotionTraceTests(unittest.TestCase):
    # Native public output from the completed motion immediately before the
    # CI575 theme failure. This tests trace correlation, not Qt GUI behaviour.
    completed = (
        "tlqt-motion phase=attempt time=701 running=0 revision=93 incoming=-1 from=-1 continuation=-1\n"
        "tlqt-motion phase=started time=0 running=1 revision=93 incoming=-1 from=-1 continuation=-1\n"
        "tlqt-motion phase=render time=38 running=1 revision=93 incoming=94 from=93 continuation=1\n"
        "tlqt-motion-identity sameView=1 sameTheme=1 sameAppearance=1 sameGeneration=1 nextRevision=1 sameDocument=1 sameDocumentRevision=1 sameGeometry=1\n"
        "tlqt-motion phase=settled time=752 running=1 revision=94 incoming=-1 from=-1 continuation=-1\n"
    )
    unfinished_collapse = (
        "tlqt-motion phase=attempt time=862 running=0 revision=94 incoming=-1 from=-1 continuation=-1\n"
        "tlqt-motion phase=started time=0 running=1 revision=94 incoming=-1 from=-1 continuation=-1\n"
        "tlqt-motion phase=render time=4 running=1 revision=94 incoming=95 from=94 continuation=1\n"
    )

    def test_completed_native_motion_reports_its_actual_continuation(self):
        self.assertEqual(settled_overlay_motion(self.completed), {"revision": 94, "elapsedMs": 752})

    def test_start_logging_can_follow_clock_restart_by_one_millisecond(self):
        delayed_start = self.completed.replace("phase=started time=0", "phase=started time=1")
        self.assertEqual(settled_overlay_motion(delayed_start), {"revision": 94, "elapsedMs": 752})

    def test_original_reverse_click_stops_old_motion_before_its_own_settlement(self):
        # CI run 43563208796 reached the compact frame with this original
        # single-click trace. The stopped expansion is not the new collapse.
        reversed_motion = (
            "tlqt-motion phase=attempt time=924 running=1 revision=81 incoming=-1 from=-1 continuation=-1\n"
            "tlqt-motion phase=layout-stop time=924 running=1 revision=81 incoming=-1 from=-1 continuation=-1\n"
            "tlqt-motion phase=started time=0 running=1 revision=81 incoming=-1 from=-1 continuation=-1\n"
            "tlqt-motion phase=render time=4 running=1 revision=81 incoming=82 from=81 continuation=1\n"
            "tlqt-motion phase=settled time=528 running=1 revision=82 incoming=-1 from=-1 continuation=-1\n"
        )
        self.assertEqual(
            settled_overlay_motion(reversed_motion), {"revision": 82, "elapsedMs": 528}
        )
        for changed in (
            reversed_motion.replace("phase=layout-stop", "phase=hide-stop"),
            reversed_motion.replace("layout-stop time=924", "layout-stop time=923"),
            reversed_motion.replace(
                "layout-stop time=924 running=1 revision=81",
                "layout-stop time=924 running=1 revision=80",
            ),
            reversed_motion.replace("phase=started time=0", "phase=started time=925"),
            reversed_motion.replace("phase=settled", "phase=layout-stop"),
        ):
            with self.subTest(trace=changed):
                self.assertIsNone(settled_overlay_motion(changed))

    def test_ci_collapse_without_settlement_is_not_a_compact_baseline(self):
        self.assertIsNone(settled_overlay_motion(self.unfinished_collapse))
        self.assertIsNone(settled_overlay_motion(self.completed + self.unfinished_collapse))

    def test_complete_looking_unterminated_settlement_is_not_a_complete_record(self):
        self.assertIsNone(settled_overlay_motion(self.completed.removesuffix("\n")))
        for phase in ("attempt", "layout-stop"):
            self.assertIsNone(settled_overlay_motion(self.completed + f"tlqt-motion phase={phase}"))
        self.assertEqual(settled_overlay_motion(self.completed), {"revision": 94, "elapsedMs": 752})

    def test_successive_continuations_must_each_link_to_the_current_revision(self):
        terminal = "tlqt-motion phase=settled time=752 running=1 revision=94 incoming=-1 from=-1 continuation=-1\n"
        continuation = "tlqt-motion phase=render time=68 running=1 revision=94 incoming=95 from=94 continuation=1\n"
        chain = self.completed.replace(
            terminal, continuation + terminal.replace("revision=94", "revision=95")
        )
        self.assertEqual(settled_overlay_motion(chain), {"revision": 95, "elapsedMs": 752})
        for changed in (
            chain.replace("incoming=95 from=94", "incoming=95 from=93"),
            chain.replace("incoming=95 from=94", "incoming=96 from=94"),
            chain.replace("running=1 revision=94 incoming=95", "running=1 revision=93 incoming=95"),
            chain.replace(
                "settled time=752 running=1 revision=95", "settled time=752 running=1 revision=94"
            ),
            self.completed + continuation,
        ):
            with self.subTest(trace=changed):
                self.assertIsNone(settled_overlay_motion(changed))

    def test_same_revision_display_continues_only_with_native_confirmation(self):
        terminal = "tlqt-motion phase=settled time=752 running=1 revision=94 incoming=-1 from=-1 continuation=-1\n"
        display = "tlqt-motion phase=render time=63 running=1 revision=94 incoming=94 from=-1 continuation=1\n"
        continued = self.completed.replace(terminal, display + terminal)
        self.assertEqual(settled_overlay_motion(continued), {"revision": 94, "elapsedMs": 752})
        for changed in (
            continued.replace(display, display.replace("continuation=1", "continuation=0")),
            continued.replace(display, display.replace("incoming=94", "incoming=93")),
            continued.replace(display, display.replace("from=-1", "from=94")),
            continued.replace(terminal, terminal.replace("phase=settled", "phase=layout-stop")),
        ):
            with self.subTest(trace=changed):
                self.assertIsNone(settled_overlay_motion(changed))

    def test_missing_or_reordered_motion_events_cannot_prove_settlement(self):
        lines = self.completed.splitlines(keepends=True)
        for index in (0, 1, 2, 4):
            with self.subTest(missing=index):
                self.assertIsNone(
                    settled_overlay_motion("".join(lines[:index] + lines[index + 1 :]))
                )
        self.assertIsNone(settled_overlay_motion("".join([lines[1], lines[0], *lines[2:]])))

    def test_wrong_continuation_identity_or_stale_settlement_is_rejected(self):
        for before, after in (
            ("incoming=94 from=93", "incoming=97 from=93"),
            ("incoming=94 from=93", "incoming=94 from=92"),
            ("continuation=1", "continuation=0"),
            ("settled time=752 running=1 revision=94", "settled time=752 running=1 revision=93"),
            ("started time=0 running=1 revision=93", "started time=0 running=1 revision=92"),
            ("settled time=752", "settled time=20"),
        ):
            with self.subTest(after=after):
                self.assertIsNone(settled_overlay_motion(self.completed.replace(before, after)))

    def test_stop_retirement_and_malformed_or_truncated_records_are_not_settlement(self):
        for phase in ("layout-stop", "frame-guard-stop", "hide-stop", "size-or-style-skip"):
            with self.subTest(phase=phase):
                self.assertIsNone(settled_overlay_motion(self.completed.replace("settled", phase)))
        self.assertIsNone(settled_overlay_motion(self.completed + "tlqt-motion incomplete\n"))
        self.assertIsNone(settled_overlay_motion(self.completed[:-3]))


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
