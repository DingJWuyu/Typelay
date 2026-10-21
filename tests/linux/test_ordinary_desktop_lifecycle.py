"""Real process-lifetime checks for the ordinary-app test's cleanup evidence.

No GIMP UI, model, input-method or product acceptance is exercised here.
"""

import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))

from desktop_session import process_identity  # noqa: E402
from ordinary_gimp import (  # noqa: E402
    acceptable_candidate,
    comment_undo_evidence,
    completed_new_image,
    direct_write_receipt,
    full_output_origin_proven,
    fullscreen_content_origin,
    launched_application,
    properties_click_point,
    returned_draft_ready,
    session_exit_confirmed,
    unique_owned_window,
    validate_nested_output,
)


class CommentUndoEvidenceTests(unittest.TestCase):
    def test_audited_nonundoable_comment_is_not_reported_as_undo_success(self):
        result = comment_undo_evidence(
            "gimp 3.2.6-2", "source", "translation", ["translation"] * 2, ["translation"] * 2
        )
        self.assertEqual(
            result,
            {
                "nativeUndoSupported": False,
                "undoRestoration": "not-supported",
                "compatibilityTrajectoryMatches": True,
            },
        )

    def test_real_restoring_baseline_is_preserved_including_future_host(self):
        for native in (["source", "earlier"], ["translation", "source"]):
            result = comment_undo_evidence("gimp future", "source", "translation", native, native)
            self.assertTrue(result["nativeUndoSupported"])
            self.assertEqual(result["undoRestoration"], "observed")

    def test_missing_host_history_never_excuses_a_different_typelay_trajectory(self):
        with self.assertRaises(AssertionError):
            comment_undo_evidence(
                "gimp 3.2.6-2", "source", "translation", ["changed", "changed"], ["translation"] * 2
            )
        with self.assertRaises(AssertionError):
            comment_undo_evidence(
                "gimp future", "source", "translation", ["translation"] * 2, ["source", "earlier"]
            )

    def test_unknown_nonrestoring_behavior_and_wrong_action_counts_fail(self):
        for version, direct, native in (
            ("gimp future", ["translation"] * 2, ["translation"] * 2),
            ("gimp 3.2.6-2", ["other"] * 2, ["other"] * 2),
            ("gimp 3.2.6-2", [], []),
            ("gimp 3.2.6-2", ["source"] * 3, ["source"] * 3),
        ):
            with self.subTest(version=version, native=native), self.assertRaises(AssertionError):
                comment_undo_evidence(version, "source", "translation", direct, native)


class DirectReceiptTests(unittest.TestCase):
    def test_only_the_actual_submitted_direct_plan_receipt_can_finish(self):
        start = {"operation": "direct-write-start", "planId": "owned-plan"}
        result = {
            "operation": "direct-write-result",
            "planId": "owned-plan",
            "detail": {"status": "Verified", "reason": "None"},
        }
        self.assertIsNone(direct_write_receipt([{"operation": "write-Verified-None"}]))
        self.assertIsNone(direct_write_receipt([start]))
        self.assertEqual(direct_write_receipt([start, result]), result)
        rejected = {
            **result,
            "detail": {"status": "RejectedBeforeMutation", "reason": "EpochChanged"},
        }
        self.assertEqual(direct_write_receipt([start, rejected]), rejected)
        for rows in (
            [result],
            [start, {**result, "planId": "other"}],
            [start, start, result],
            [start, result, result],
        ):
            with self.subTest(rows=rows), self.assertRaises(AssertionError):
                direct_write_receipt(rows)


class OrdinaryFirstRunTests(unittest.TestCase):
    def test_welcome_requires_unique_same_process_exact_title(self):
        welcome = {"id": 2, "pid": 10, "title": "Welcome to GIMP 3.2.6"}
        self.assertEqual(unique_owned_window([welcome], 10, welcome["title"]), welcome)
        self.assertIsNone(unique_owned_window([welcome], 11, welcome["title"]))
        self.assertIsNone(unique_owned_window([welcome], 10, "Welcome to GIMP 3.2.7"))
        with self.assertRaises(AssertionError):
            unique_owned_window([welcome, {**welcome, "id": 3}], 10, welcome["title"])

    def test_new_image_requires_old_dialog_gone_and_same_focused_main_with_new_title(self):
        previous = {"id": 1, "pid": 10, "title": "[Untitled]-1.0", "is_focused": True}
        current = {**previous, "title": "[Untitled]-2.0"}
        self.assertEqual(completed_new_image([current], previous, 2), current)
        self.assertIsNone(completed_new_image([current, {"id": 2}], previous, 2))
        for change in (
            {"title": previous["title"]},
            {"pid": 11},
            {"id": 3},
            {"is_focused": False},
            {"title": "Welcome to GIMP 3.2.6"},
        ):
            with self.subTest(change=change):
                self.assertIsNone(completed_new_image([{**current, **change}], previous, 2))


class ReturnedDraftReadinessTests(unittest.TestCase):
    def test_returned_draft_requests_manual_not_machine_translation_authority(self):
        calls = []
        permit = {"viewState": "EditedTranslationPending", "acceptanceReady": True}

        def ready(state="ValidTranslation"):
            calls.append(state)
            return permit if state == permit["viewState"] else None

        self.assertIs(returned_draft_ready(lambda: True, ready), permit)
        self.assertEqual(calls, ["EditedTranslationPending"])

    def test_machine_candidate_cannot_satisfy_manual_wait(self):
        machine = {"viewState": "ValidTranslation", "acceptanceReady": True}
        self.assertIsNone(
            returned_draft_ready(
                lambda: True, lambda state: machine if state == machine["viewState"] else None
            )
        )

    def test_diagnostics_distinguish_focus_from_manual_authority(self):
        observation = {}
        returned_draft_ready(lambda: False, lambda state: None, observation)
        self.assertFalse(observation["sourceFocused"])
        self.assertFalse(observation["manualAcceptanceReady"])
        returned_draft_ready(lambda: True, lambda state: None, observation)
        self.assertTrue(observation["sourceFocused"])
        self.assertFalse(observation["manualAcceptanceReady"])

    def test_lost_source_focus_or_missing_manual_authority_does_not_accept(self):
        calls = []
        self.assertIsNone(returned_draft_ready(lambda: False, lambda state: calls.append(state)))
        self.assertEqual(calls, [])
        self.assertIsNone(returned_draft_ready(lambda: True, lambda state: None))


class OrdinaryDesktopLifecycleTests(unittest.TestCase):
    def test_running_child_waits_for_final_exec_without_accepting_launcher(self):
        executable = shutil.which("sleep")
        self.assertIsNotNone(executable)
        child = subprocess.Popen(
            [
                sys.executable,
                "-c",
                "import os,sys; print('ready',flush=True); "
                "sys.stdin.readline(); os.execv(sys.argv[1], [sys.argv[1], '30'])",
                executable,
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            text=True,
        )
        try:
            self.assertEqual(child.stdout.readline(), "ready\n")
            identity = process_identity(child.pid)
            self.assertIsNone(launched_application(identity, executable))
            child.stdin.write("exec\n")
            child.stdin.flush()
            # Observe the actual image under a fixed deadline; no assumed exec delay.
            deadline = time.monotonic() + 5
            while launched_application(identity, executable) is None:
                self.assertLess(time.monotonic(), deadline)
                time.sleep(0.01)
            self.assertEqual(launched_application(identity, executable), identity)
            with self.assertRaises(AssertionError):
                launched_application(
                    dict(identity, startTime=identity["startTime"] + 1), executable
                )
        finally:
            child.terminate()
            child.wait(timeout=5)
            child.stdin.close()
            child.stdout.close()

    def test_removed_receipt_does_not_prove_guardian_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
            try:
                identity = process_identity(child.pid)
                self.assertIsNotNone(identity)
                # The guardian can unlink its receipt before fsync/lock cleanup.
                self.assertFalse(session_exit_confirmed(Path(directory), [identity]))
                child.terminate()
                child.wait(timeout=5)
                self.assertTrue(session_exit_confirmed(Path(directory), [identity]))
            finally:
                if child.poll() is None:
                    child.kill()
                    child.wait(timeout=5)

    def test_denied_process_observation_is_not_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch("ordinary_gimp.Path.read_text", side_effect=PermissionError("denied")):
                with self.assertRaises(PermissionError):
                    session_exit_confirmed(Path(directory), [{"pid": 12345, "startTime": 678}])

    def test_malformed_process_observation_is_not_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch("ordinary_gimp.Path.read_text", return_value="invalid"):
                with self.assertRaises(ValueError):
                    session_exit_confirmed(Path(directory), [{"pid": 12345, "startTime": 678}])

    def test_malformed_receipt_does_not_claim_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            data = Path(directory)
            root = data / "components/desktop-text/sessions"
            root.mkdir(parents=True, mode=0o700)
            (root / ("a" * 32 + ".json")).write_text("{}")
            with self.assertRaises(OSError):
                session_exit_confirmed(data, [])


class OrdinaryCandidateObservationTests(unittest.TestCase):
    def test_expired_cue_and_empty_translating_text_do_not_authorize_accept(self):
        events = []

        def ready():
            events.append("ready")
            return {"acceptanceReady": True}

        self.assertIsNone(acceptable_candidate(lambda: "", ready, "source"))
        self.assertIsNone(acceptable_candidate(lambda: "source", ready, "source"))
        self.assertEqual(events, [])
        self.assertIsNone(acceptable_candidate(lambda: "translation", lambda: None, "source"))

    def test_current_candidate_is_read_before_final_acceptance_cue(self):
        events = []

        def read():
            events.append("text")
            return "translation"

        def ready():
            events.append("ready")
            return {"acceptanceReady": True}

        self.assertEqual(
            acceptable_candidate(read, ready, "source"), ("translation", {"acceptanceReady": True})
        )
        self.assertEqual(events, ["text", "ready"])


class OrdinaryDesktopGeometryTests(unittest.TestCase):
    def output(self, logical):
        return {"winit": {"name": "winit", "make": "Smithay", "model": "Winit", "logical": logical}}

    def test_render_flip_is_accepted_only_for_exact_nested_winit_backend(self):
        # Actual 245339 IPC values, not an invented rotation workaround.
        logical = dict(x=0, y=0, width=1280, height=800, scale=1.0, transform="Flipped180")
        validate_nested_output(self.output(logical), [1280, 800])
        for field, value in (("name", "HDMI-A-1"), ("make", "other"), ("model", "other")):
            output = self.output(logical)
            output["winit"][field] = value
            with self.subTest(field=field), self.assertRaises(AssertionError):
                validate_nested_output(output, [1280, 800])
        window = dict(id=12, pid=345, is_focused=True, layout=self.layout())
        measured = ((10, 20, 100, 40), (0, 0, 400, 540))
        # The logical point remains y=170, not render-buffer mirrored y=630.
        self.assertEqual(
            properties_click_point(window, window, measured, measured, [1280, 800]), (500, 170)
        )

    def layout(self):
        return dict(
            tile_size=[1280, 800],
            tile_pos_in_workspace_view=[0, 0],
            window_size=[400, 540],
            window_offset_in_tile=[440, 130],
        )

    def test_click_requires_actual_single_unit_scale_output(self):
        logical = dict(x=0, y=0, width=1280, height=800, scale=1, transform="Flipped180")
        validate_nested_output(self.output(logical), [1280, 800])
        for field, value in (("x", 10), ("width", 1200), ("scale", 2), ("transform", "90")):
            with self.subTest(field=field), self.assertRaises(AssertionError):
                validate_nested_output(self.output(dict(logical, **{field: value})), [1280, 800])
        with self.assertRaises(AssertionError):
            validate_nested_output({}, [1280, 800])
        with self.assertRaises(AssertionError):
            validate_nested_output(
                {"a": {"logical": logical}, "b": {"logical": logical}}, [1280, 800]
            )

    def test_output_failure_identifies_observed_scale_or_transform(self):
        logical = dict(x=0, y=0, width=1280, height=800, scale=1, transform="Flipped180")
        for field, value, message in (
            ("scale", 1.25, "nested output scale mismatch: 1.25"),
            ("transform", "Normal", "nested output transform mismatch: 'Normal'"),
        ):
            with self.subTest(field=field), self.assertRaises(AssertionError) as failure:
                validate_nested_output(self.output(dict(logical, **{field: value})), [1280, 800])
            self.assertEqual(str(failure.exception), message)

    def test_click_point_uses_content_offset_and_rejects_changed_identity_or_frame(self):
        window = dict(id=12, pid=345, is_focused=True, layout=self.layout())
        measured = ((10, 20, 100, 40), (0, 0, 400, 540))
        self.assertEqual(
            properties_click_point(window, window, measured, measured, [1280, 800]), (500, 170)
        )
        for field, value in (("id", 13), ("pid", 346), ("is_focused", False)):
            with self.subTest(field=field), self.assertRaises(AssertionError):
                properties_click_point(
                    window, dict(window, **{field: value}), measured, measured, [1280, 800]
                )
        with self.assertRaises(AssertionError):
            properties_click_point(
                window, window, measured, ((11, 20, 100, 40), measured[1]), [1280, 800]
            )
        bad_frame = (measured[0], (0, 0, 1280, 800))
        with self.assertRaises(AssertionError):
            properties_click_point(window, window, bad_frame, bad_frame, [1280, 800])

    def test_fixed_size_fullscreen_uses_observed_content_offset(self):
        self.assertEqual(fullscreen_content_origin(self.layout(), [1280, 800]), (440, 130))
        full = self.layout()
        full.update(window_size=[1280, 800], window_offset_in_tile=[0, 0])
        self.assertEqual(fullscreen_content_origin(full, [1280, 800]), (0, 0))

    def test_nonfullscreen_or_unlocated_tile_never_authorizes_click(self):
        for field, value in (
            ("tile_size", [400, 540]),
            ("tile_pos_in_workspace_view", None),
            ("tile_pos_in_workspace_view", [600, 0]),
        ):
            layout = self.layout()
            layout[field] = value
            self.assertIsNone(fullscreen_content_origin(layout, [1280, 800]))

    def test_unknown_or_offscreen_content_never_authorizes_click(self):
        for field, value in (
            ("window_size", None),
            ("window_size", [0, 540]),
            ("window_offset_in_tile", [-1, 130]),
            ("window_offset_in_tile", [1000, 130]),
            ("window_offset_in_tile", [True, 130]),
            ("window_offset_in_tile", [0.5, 130]),
            ("window_offset_in_tile", [float("nan"), 130]),
        ):
            layout = self.layout()
            layout[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(AssertionError):
                fullscreen_content_origin(layout, [1280, 800])


class FullscreenOriginProofTests(unittest.TestCase):
    def setUp(self):
        # a27 actual current window allocation, not a fabricated tile origin.
        self.window = dict(
            id=12,
            pid=34683,
            workspace_id=1,
            is_focused=True,
            is_floating=False,
            layout=dict(
                tile_size=[1280.0, 800.0],
                window_size=[1280, 800],
                window_offset_in_tile=[0.0, 0.0],
                tile_pos_in_workspace_view=None,
            ),
        )
        self.workspaces = [dict(id=1, output="winit", is_active=True, is_focused=True)]

    def proof(
        self, window=None, workspaces=None, overview=None, requested=12, version="26.04 (8ed0da4)"
    ):
        return full_output_origin_proven(
            self.window if window is None else window,
            [1280, 800],
            self.workspaces if workspaces is None else workspaces,
            {"is_open": False} if overview is None else overview,
            requested,
            version,
            animations_disabled=True,
        )

    def test_current_full_allocation_proves_origin_only_with_complete_scope(self):
        self.assertIsNone(fullscreen_content_origin(self.window["layout"], [1280, 800]))
        self.assertTrue(self.proof())
        self.assertFalse(
            full_output_origin_proven(
                self.window, [1280, 800], self.workspaces, {"is_open": False}, 12, "26.04 (8ed0da4)"
            )
        )
        measured = ((754, 480, 510, 50), (0, 0, 1280, 800))
        self.assertEqual(
            properties_click_point(
                self.window,
                self.window,
                measured,
                measured,
                [1280, 800],
                full_output_proven=self.proof(),
            ),
            (1009, 505),
        )

    def test_request_unknown_version_overview_and_other_workspace_cannot_prove_origin(self):
        self.assertFalse(self.proof(requested=13))
        self.assertFalse(self.proof(version="unknown"))
        self.assertFalse(self.proof(overview={"is_open": True}))
        self.assertFalse(self.proof(overview={}))
        self.assertFalse(self.proof(workspaces=[]))
        self.assertFalse(self.proof(workspaces=self.workspaces * 2))
        for field, value in (
            ("output", "other"),
            ("id", 2),
            ("is_active", False),
            ("is_focused", False),
        ):
            with self.subTest(field=field):
                self.assertFalse(
                    self.proof(workspaces=[dict(self.workspaces[0], **{field: value})])
                )

    def test_small_shifted_floating_and_unfocused_windows_never_infer_null_origin(self):
        for field, value in (("id", 13), ("is_focused", False), ("is_floating", True)):
            with self.subTest(field=field):
                self.assertFalse(self.proof(window=dict(self.window, **{field: value})))
        for field, value in (
            ("window_size", [400, 540]),
            ("tile_size", [400, 540]),
            ("window_offset_in_tile", [1, 0]),
            ("tile_pos_in_workspace_view", [20, 0]),
        ):
            with self.subTest(field=field):
                window = dict(self.window, layout=dict(self.window["layout"], **{field: value}))
                self.assertFalse(self.proof(window=window))
                self.assertIsNone(
                    fullscreen_content_origin(
                        window["layout"], [1280, 800], full_output_proven=True
                    )
                )


if __name__ == "__main__":
    unittest.main()
