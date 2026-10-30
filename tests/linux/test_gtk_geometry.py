"""GTK fixture file/geometry observations, not native scrolling or GUI evidence."""

import copy
import json
import resource
import signal
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from desktop_e2e import (  # noqa: E402
    collapse_spring_path_bounded,
    settings_text_visible_point,
    settings_viewport_from_pane,
)
from gtk_session_reentry import (  # noqa: E402
    check_preview_colors,
    click_point,
    clickable_control,
    expand_click_point,
    expand_hover_current,
    language_menu_point,
    language_trigger_hover_current,
    language_trigger_point,
    parent_geometry_ready,
    validated_geometry,
)
from hy_package_e2e import package_scroll_value, rectangle_contains  # noqa: E402
from native_e2e import (  # noqa: E402
    glossary_gtk3_hover_current,
    glossary_hover_current,
    glossary_interaction_loss,
    glossary_menu_scroll_advanced,
    glossary_menu_scroll_direction,
    glossary_menu_scroll_observation,
    glossary_painted_geometry,
    glossary_popup_fits,
    glossary_save_outcome,
    glossary_source_click_ready,
)


class GtkCollapseSpringPathTests(unittest.TestCase):
    def test_monotonic_or_one_small_valley_can_settle_at_the_exact_target(self):
        for heights in (
            [360, 310, 250, 190, 167],
            [360, 310, 250, 190, 167, 166, 166, 167],
            [360, 310, 250, 190, 167, 165, 166, 167],
        ):
            with self.subTest(heights=heights):
                self.assertTrue(collapse_spring_path_bounded(heights, 360, 167))

    def test_excursion_beyond_the_step_derived_envelope_is_rejected(self):
        self.assertFalse(collapse_spring_path_bounded([360, 250, 190, 164, 167], 360, 167))
        self.assertFalse(collapse_spring_path_bounded([361, 250, 190, 167], 360, 167))
        # Different steps use the same formula, not a blanket 2px allowance.
        self.assertTrue(collapse_spring_path_bounded([177, 173, 169, 166, 167], 177, 167))
        self.assertFalse(collapse_spring_path_bounded([177, 173, 169, 165, 167], 177, 167))
        self.assertTrue(collapse_spring_path_bounded([480, 310, 190, 164, 167], 480, 167))
        self.assertFalse(collapse_spring_path_bounded([480, 310, 190, 163, 167], 480, 167))

    def test_extra_reversal_or_recovery_overshoot_is_rejected(self):
        for heights in (
            [360, 310, 320, 250, 167],
            [360, 250, 166, 167, 166, 167],
            [360, 250, 165, 166, 165, 166, 167],
            [360, 250, 166, 168, 167],
        ):
            with self.subTest(heights=heights):
                self.assertFalse(collapse_spring_path_bounded(heights, 360, 167))

    def test_unsettled_wrong_target_and_invalid_observations_are_rejected(self):
        for heights in ([], [360, 250, 166], [360, 250, 168], [360, 250, 167.0], [True, 167]):
            with self.subTest(heights=heights):
                self.assertFalse(collapse_spring_path_bounded(heights, 360, 167))
        self.assertFalse(collapse_spring_path_bounded([167, 250, 360], 167, 360))
        self.assertFalse(collapse_spring_path_bounded([167, 167], 167, 167))


class GtkFixtureStatePublicationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="typelay-state-publication-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.directory = Path(cls.temporary.name)
        source = cls.directory / "publish.c"
        source.write_text(
            '#include "fixture_state.h"\n'
            "int main(int argc, char **argv) {\n"
            "    if (argc != 2) return 2;\n"
            "    char *body = NULL; size_t capacity = 0;\n"
            "    while (getline(&body, &capacity, stdin) >= 0) {\n"
            "        int failure = fixture_publish_state(argv[1], body);\n"
            "        if (failure) { free(body); return 1; }\n"
            "        putchar('.'); fflush(stdout);\n"
            "    }\n"
            "    free(body); return 0;\n"
            "}\n"
        )
        cls.executable = cls.directory / "publish"
        subprocess.run(
            [
                "cc",
                "-std=c17",
                "-D_POSIX_C_SOURCE=200809L",
                "-Wall",
                "-Wextra",
                "-Werror",
                "-I" + str(ROOT / "tests/linux/support"),
                str(source),
                "-o",
                str(cls.executable),
            ],
            check=True,
            timeout=30,
        )

    def test_live_replacements_are_complete_and_keep_open_readers_on_the_old_snapshot(self):
        path = self.directory / "live.json"
        initial = json.dumps({"sequence": 0, "body": "original"}) + "\n"
        path.write_text(initial)
        bodies = [{"sequence": sequence, "body": str(sequence) * 8192} for sequence in range(1, 33)]
        observed_sequences, errors = set(), []
        stopped = threading.Event()

        def observe():
            while not stopped.is_set():
                try:
                    observed = json.loads(path.read_text())
                    expected = (
                        {"sequence": 0, "body": "original"}
                        if observed["sequence"] == 0
                        else bodies[observed["sequence"] - 1]
                    )
                    self.assertEqual(observed, expected)
                    observed_sequences.add(observed["sequence"])
                except BaseException as error:
                    errors.append(error)
                    return

        with path.open() as original:
            reader = threading.Thread(target=observe)
            reader.start()
            try:
                published = subprocess.run(
                    [str(self.executable), str(path)],
                    input="".join(json.dumps(body) + "\n" for body in bodies),
                    capture_output=True,
                    text=True,
                    timeout=5,
                )
            finally:
                stopped.set()
                reader.join(timeout=2)
            self.assertFalse(reader.is_alive())
            self.assertEqual(published.returncode, 0, published.stderr)
            self.assertEqual(published.stdout, "." * len(bodies))
            self.assertEqual(original.read(), initial)
        self.assertFalse(errors, errors)
        self.assertTrue(observed_sequences)
        self.assertEqual(json.loads(path.read_text()), bodies[-1])
        self.assertEqual(list(self.directory.glob("live.json.*")), [])

    def test_partial_write_failure_keeps_old_evidence_and_fails_without_acknowledgment(self):
        path = self.directory / "failed.json"
        original = b'{"sequence":0,"body":"previous complete observation"}\n'
        path.write_bytes(original)

        def limit_owned_writer():
            signal.signal(signal.SIGXFSZ, signal.SIG_IGN)
            resource.setrlimit(resource.RLIMIT_FSIZE, (1024, 1024))

        published = subprocess.run(
            [str(self.executable), str(path)],
            input=json.dumps({"sequence": 1, "body": "x" * 16384}) + "\n",
            capture_output=True,
            text=True,
            timeout=5,
            preexec_fn=limit_owned_writer,
        )
        self.assertEqual(published.returncode, 1)
        self.assertEqual(published.stdout, "")
        self.assertEqual(path.read_bytes(), original)
        self.assertEqual(list(self.directory.glob("failed.json.*")), [])


class GlossarySourcePreparationTests(unittest.TestCase):
    def test_preparation_waits_for_this_sources_completed_click_not_sender_return(self):
        baseline = {"instance": 17, "pointerRelease": {"sequence": 5}}
        source = {
            "instance": 17,
            "focused": True,
            "pointerRelease": {"instance": 17, "sequence": 5, "atMs": 90, "focused": True},
        }
        observed = {"observedAtMs": 110, "glossarySource": source}
        # The sender has returned and focus is already equal, but the GTK
        # source has not dispatched this click's release yet.
        self.assertIsNone(glossary_source_click_ready(observed, baseline, 100))
        source["pointerRelease"] = {"instance": 17, "sequence": 6, "atMs": 115, "focused": True}
        self.assertIsNone(glossary_source_click_ready(observed, baseline, 100))
        observed["observedAtMs"] = 116
        self.assertIs(glossary_source_click_ready(observed, baseline, 100), observed)

    def test_old_missing_other_widget_or_unfocused_release_cannot_prepare_source(self):
        baseline = {"instance": 17, "pointerRelease": {"sequence": 5}}
        for change in (
            {"instance": 18},
            {"sequence": 5},
            {"sequence": 7},
            {"atMs": 99},
            {"focused": False},
        ):
            with self.subTest(change=change):
                released = {"instance": 17, "sequence": 6, "atMs": 115, "focused": True, **change}
                observed = {
                    "observedAtMs": 116,
                    "glossarySource": {"instance": 17, "focused": True, "pointerRelease": released},
                }
                self.assertIsNone(glossary_source_click_ready(observed, baseline, 100))
        for source in (
            {},
            {"instance": 17, "focused": True},
            {
                "instance": 18,
                "focused": True,
                "pointerRelease": {"instance": 17, "sequence": 6, "atMs": 115, "focused": True},
            },
            {
                "instance": 17,
                "focused": False,
                "pointerRelease": {"instance": 17, "sequence": 6, "atMs": 115, "focused": True},
            },
        ):
            with self.subTest(source=source):
                self.assertIsNone(
                    glossary_source_click_ready(
                        {"observedAtMs": 116, "glossarySource": source}, baseline, 100
                    )
                )


class Gtk3GlossaryPaintGeometryTests(unittest.TestCase):
    def test_mapped_menu_allocation_without_this_maps_paint_cannot_supply_move(self):
        # CI491 reported this same pre-layout allocation twice, then the one
        # move missed when GTK placed the menu at (740, 640). No click followed.
        control = dict(instance=506, visible=True, enabled=True, x=467, y=352, width=496, height=29)
        for observed_at in (521897, 521924):
            self.assertFalse(glossary_painted_geometry(control, observed_at, toolkit="gtk3"))

    def control(self):
        geometry = dict(instance=506, x=740, y=640, width=496, height=29, scale=1)
        return {
            **geometry,
            "visible": True,
            "enabled": True,
            "paintObservation": dict(
                ready=True,
                mapGeneration=2,
                paintedMapGeneration=2,
                clockInstance=17,
                drawClockInstance=17,
                paintedClockInstance=17,
                mappedAtMonoMs=521880,
                paintedAtMonoMs=521946,
                frame=75,
                drawFrame=75,
                paintedFrame=75,
                geometry=dict(geometry),
            ),
        }

    def test_draw_on_another_window_clock_cannot_complete_the_widget_toplevel_frame(self):
        control = self.control()
        # GTK3 Wayland popovers draw on a subsurface clock, independently of
        # the GtkWidget toplevel animation clock used by the old observer.
        control["paintObservation"]["drawClockInstance"] = 23
        self.assertFalse(glossary_painted_geometry(control, 521948, toolkit="gtk3"))

    def test_completed_subsurface_draw_does_not_need_the_widget_toplevel_clock(self):
        control = self.control()
        control["paintObservation"].update(
            clockInstance=23,
            drawClockInstance=23,
            paintedClockInstance=23,
            widgetClockInstance=17,
            frame=2,
            drawFrame=2,
            paintedFrame=2,
        )
        self.assertTrue(glossary_painted_geometry(control, 521948, toolkit="gtk3"))

    def test_only_this_maps_completed_draw_supplies_the_new_menu_position(self):
        control = self.control()
        control["paintObservation"]["ready"] = False
        self.assertFalse(glossary_painted_geometry(control, 521948, toolkit="gtk3"))
        control["paintObservation"]["ready"] = True
        self.assertTrue(glossary_painted_geometry(control, 521948, toolkit="gtk3"))
        # A later unrelated frame does not require another draw of this control.
        control["paintObservation"]["frame"] += 1
        self.assertTrue(glossary_painted_geometry(control, 521970, toolkit="gtk3"))

    def test_unmapped_replaced_clock_or_unpainted_relayout_cannot_authorize_move(self):
        mutations = (
            lambda c: c.update(visible=False),
            lambda c: c.update(instance=507),
            lambda c: c.update(x=467, y=352),
            lambda c: c.update(width=500),
            lambda c: c.update(scale=2),
            lambda c: c["paintObservation"].update(mapGeneration=3),
            lambda c: c["paintObservation"].update(clockInstance=18),
            lambda c: c["paintObservation"].update(drawClockInstance=0),
            lambda c: c["paintObservation"].pop("drawClockInstance"),
            lambda c: c["paintObservation"].update(drawFrame=76),
            lambda c: c["paintObservation"].update(frame=74),
            lambda c: c["paintObservation"].update(mappedAtMonoMs=521947),
            lambda c: c["paintObservation"].update(paintedAtMonoMs=521949),
            lambda c: c["paintObservation"].pop("geometry"),
        )
        for mutate in mutations:
            control = self.control()
            mutate(control)
            with self.subTest(control=control):
                self.assertFalse(glossary_painted_geometry(control, 521948, toolkit="gtk3"))


class Gtk3GlossaryFixedPointTests(unittest.TestCase):
    def setUp(self):
        # CI542: this same painted Add button moved up 8px after one move.
        geometry = dict(instance=44, scale=1, x=828, y=379, width=400, height=30)
        self.before = dict(
            **geometry,
            visible=True,
            enabled=True,
            pointerInside=False,
            paintObservation=dict(
                ready=True,
                mapGeneration=1,
                paintedMapGeneration=1,
                clockInstance=29,
                drawClockInstance=29,
                paintedClockInstance=29,
                mappedAtMonoMs=528890,
                paintedAtMonoMs=528936,
                frame=9,
                drawFrame=6,
                paintedFrame=6,
                geometry=dict(geometry),
            ),
        )
        self.current = copy.deepcopy(self.before)
        self.current.update(y=371, pointerInside=True)
        self.current["paintObservation"].update(
            paintedAtMonoMs=529019,
            frame=11,
            drawFrame=11,
            paintedFrame=11,
            geometry={**geometry, "y": 371},
        )
        self.pointer = dict(
            status="captured",
            ownedNiri=6291458,
            WINDOW=6291458,
            observedAtMonoMs=529027,
            X=1028,
            Y=394,
        )

    def admitted(self, *, observed_at=529020, point=(1028, 394), origin=(0, 0)):
        return glossary_gtk3_hover_current(
            self.current, observed_at, self.before, point, 529012, self.pointer, origin
        )

    def repaint(self):
        self.current["paintObservation"]["geometry"] = {
            key: self.current[key] for key in ("instance", "scale", "x", "y", "width", "height")
        }

    def test_one_fixed_point_still_hits_the_same_repainted_button_after_layout(self):
        self.assertTrue(self.admitted())
        self.pointer.update(X=1128, Y=444)
        self.assertTrue(self.admitted(origin=(100, 50)))

    def test_fixed_point_outside_current_button_is_not_relocated_to_new_center(self):
        self.current["y"] = 401
        self.repaint()
        self.assertFalse(self.admitted())

    def test_replaced_instance_map_clock_or_scale_cannot_reuse_original_point(self):
        for change in ("instance", "scale", "map", "clock"):
            with self.subTest(change=change):
                self.setUp()
                if change in ("instance", "scale"):
                    self.current[change] += 1
                    self.repaint()
                elif change == "map":
                    self.current["paintObservation"].update(mapGeneration=2, paintedMapGeneration=2)
                else:
                    self.current["paintObservation"].update(
                        clockInstance=30, drawClockInstance=30, paintedClockInstance=30
                    )
                self.assertFalse(self.admitted())

    def test_unpainted_geometry_stale_time_or_absent_hover_cannot_authorize_click(self):
        for mutate in (
            lambda c: c.update(pointerInside=False),
            lambda c: c.update(visible=False),
            lambda c: c.update(enabled=False),
            lambda c: c["paintObservation"]["geometry"].update(y=379),
            lambda c: c["paintObservation"].update(ready=False),
            lambda c: c["paintObservation"].update(paintedAtMonoMs=529021),
            lambda c: c["paintObservation"].update(paintedAtMonoMs=528889),
        ):
            with self.subTest(mutate=mutate):
                self.setUp()
                mutate(self.current)
                self.assertFalse(self.admitted())
        self.setUp()
        self.assertFalse(self.admitted(observed_at=529011))

    def test_real_x_pointer_must_match_the_original_point_and_owned_niri(self):
        for change in (
            dict(status="unavailable"),
            dict(X=1029),
            dict(Y=386),
            dict(WINDOW=6291459),
            dict(ownedNiri=0),
            dict(observedAtMonoMs=529011),
        ):
            with self.subTest(change=change):
                self.setUp()
                self.pointer.update(change)
                self.assertFalse(self.admitted())
        self.pointer = {}
        self.assertFalse(self.admitted())


class GlossaryPaintGeometryTests(unittest.TestCase):
    def control(self):
        geometry = dict(
            x=744.0,
            y=421.0,
            width=488,
            height=22,
            instance=224,
            surfaceInstance=216,
            scale=1,
            nativeX=20.0,
            nativeY=323.0,
            transformX=0.0,
            transformY=0.0,
            popup=True,
            popupX=724,
            popupY=98,
        )
        return dict(
            instance=224,
            visible=True,
            enabled=True,
            width=488,
            height=22,
            surfaceGeometry=geometry,
            pointerInside=True,
            pointerEnteredAtMonoMs=110,
            paintObservation=dict(
                ready=True,
                mapGeneration=2,
                paintedMapGeneration=2,
                requestedFrame=7,
                mappedAtMonoMs=100,
                paintedAtMonoMs=105,
                frame=8,
                paintedFrame=8,
                layoutFrame=8,
                paintFrame=8,
                geometry=dict(geometry),
            ),
        )

    def test_unpainted_reopen_is_not_clickable_even_with_fresh_mapped_geometry(self):
        control = self.control()
        self.assertTrue(glossary_painted_geometry(control, 120))
        control["paintObservation"]["frame"] = 9
        self.assertTrue(glossary_painted_geometry(control, 130))
        control["paintObservation"]["ready"] = False
        self.assertFalse(glossary_painted_geometry(control, 120))

    def test_old_map_frame_surface_or_changed_allocation_cannot_authorize_move(self):
        mutations = [
            lambda c: c["paintObservation"].update(mappedAtMonoMs=106),
            lambda c: c["paintObservation"].update(frame=7),
            lambda c: c["paintObservation"].update(requestedFrame=8),
            lambda c: c["paintObservation"].update(paintedMapGeneration=1),
            lambda c: c["paintObservation"].update(layoutFrame=7),
            lambda c: c["paintObservation"].update(paintFrame=7),
            lambda c: c["paintObservation"].update(paintedAtMonoMs=121),
            lambda c: c["paintObservation"].update(mapGeneration=0),
            lambda c: c["surfaceGeometry"].update(surfaceInstance=217),
            lambda c: c["surfaceGeometry"].update(y=274.0),
            lambda c: c.update(instance=225),
            lambda c: c.update(width=500),
            lambda c: c.update(visible=False),
        ]
        for mutate in mutations:
            c = self.control()
            mutate(c)
            with self.subTest(control=c):
                self.assertFalse(glossary_painted_geometry(c, 120))

    def test_fixed_point_must_hit_fresh_bounds_not_equal_new_center(self):
        c = self.control()
        before = self.control()
        self.assertTrue(glossary_hover_current(c, 120, before, (976, 432), 108))
        # Actual 8b824cf missed point: after relayout it is above the button.
        self.assertFalse(glossary_hover_current(c, 120, before, (976, 285), 108))
        for change in (dict(pointerInside=False), dict(pointerEnteredAtMonoMs=0)):
            c = self.control()
            c.update(change)
            self.assertFalse(glossary_hover_current(c, 120, before, (976, 432), 108))
        c = self.control()
        c["paintObservation"].update(mapGeneration=3, paintedMapGeneration=3)
        self.assertTrue(glossary_painted_geometry(c, 120))
        self.assertFalse(glossary_hover_current(c, 120, before, (976, 432), 108))
        for key, value in (("instance", 225), ("surfaceInstance", 217), ("scale", 2)):
            c = self.control()
            c["surfaceGeometry"][key] = value
            c["paintObservation"]["geometry"][key] = value
            if key == "instance":
                c["instance"] = value
            self.assertTrue(glossary_painted_geometry(c, 120))
            self.assertFalse(glossary_hover_current(c, 120, before, (976, 432), 108))
        self.assertFalse(glossary_hover_current(self.control(), 107, before, (976, 432), 108))


class GlossaryMotionGeometryTests(unittest.TestCase):
    """Captured controller/device evidence, not a real GTK or XTest execution."""

    def setUp(self):
        self.before = GlossaryPaintGeometryTests().control()
        self.point, self.origin = (976, 432), (10, 20)
        self.pointer = dict(
            status="captured", observedAtMonoMs=111, ownedNiri=99, WINDOW=99, X=986, Y=452
        )
        for key, phase, instance in (("pointerDispatchObservation", 2, 301), ("capture", 1, 302)):
            dispatch = dict(
                controllerInstance=instance,
                widgetInstance=224,
                signalCount=1,
                phase=phase,
                limit=1,
                nativeInstance=200,
            )
            if key == "capture":
                self.before["pointerCaptureObservation"] = dict(
                    attachedAtMonoMs=99, leftAtMonoMs=0, dispatch=dispatch
                )
            else:
                self.before[key] = dispatch
        self.before.update(pointerObserverAttachedAtMonoMs=99, pointerLeftAtMonoMs=0)
        self.control = copy.deepcopy(self.before)
        self.control.update(
            pointerInside=False,
            pointerEnteredAtMonoMs=0,
            pointerObservation=dict(
                status="captured",
                surface="control-native",
                surfaceInstance=216,
                nativeInstance=200,
                x=252.0,
                y=334.0,
                pickedAncestors=[
                    dict(instance=224, isObservedControl=True, visible=True, mapped=True)
                ],
            ),
        )
        for dispatch in (
            self.control["pointerDispatchObservation"],
            self.control["pointerCaptureObservation"]["dispatch"],
        ):
            dispatch.update(
                signalCount=2,
                lastSignal="motion",
                eventType=1,
                observedAtMonoMs=112,
                eventControllerInstance=dispatch["controllerInstance"],
                eventWidgetInstance=224,
                eventSurfaceInstance=216,
                eventNativeInstance=200,
                pointerDevice=True,
                hasPosition=True,
                x=232.0,
                y=11.0,
            )

    def admitted(self):
        return glossary_hover_current(
            self.control, 120, self.before, self.point, 108, self.pointer, self.origin, 107
        )

    def test_post_move_actual_motion_can_prove_current_point_without_enter_history(self):
        self.assertTrue(self.admitted())
        self.assertFalse(self.control["pointerInside"])
        self.assertEqual(self.control["pointerEnteredAtMonoMs"], 0)

    def test_graphene_single_precision_neighbor_is_not_a_different_pointer_pixel(self):
        # Both phases traverse GTK's float32 coordinate conversion. CI557
        # recorded exactly one ULP of error in each local component.
        self.point = (943, 437)
        self.pointer.update(X=953, Y=457)
        self.control["pointerObservation"].update(x=219.0, y=339.0)
        for dispatch in (
            self.control["pointerDispatchObservation"],
            self.control["pointerCaptureObservation"]["dispatch"],
        ):
            dispatch.update(x=198.99998474121094, y=16.000001907348633)
        self.assertTrue(self.admitted())

    def test_graphene_more_than_one_ulp_and_nonfinite_remain_rejected(self):
        original = copy.deepcopy(self.control)
        for phase in ("pointerDispatchObservation", "pointerCaptureObservation"):
            for change in (
                dict(x=232.0 - 2 * 2**-16),
                dict(x=232.0 + 2 * 2**-16),
                dict(y=11.0 - 2 * 2**-20),
                dict(y=11.0 + 2 * 2**-20),
                dict(x=float("inf")),
                dict(y=float("nan")),
            ):
                with self.subTest(phase=phase, change=change):
                    self.control = copy.deepcopy(original)
                    dispatch = self.control[phase]
                    if phase == "pointerCaptureObservation":
                        dispatch = dispatch["dispatch"]
                    dispatch.update(change)
                    self.assertFalse(self.admitted())

    def test_device_coordinates_do_not_inherit_graphene_tolerance(self):
        self.control["pointerObservation"]["x"] += 2**-16
        self.assertFalse(self.admitted())

    def test_old_foreign_or_unpositioned_dispatch_cannot_replace_crossing(self):
        original = copy.deepcopy(self.control)
        for phase in ("pointerDispatchObservation", "pointerCaptureObservation"):
            for change in (
                dict(observedAtMonoMs=107),
                dict(observedAtMonoMs=121),
                dict(signalCount=1),
                dict(lastSignal="enter"),
                dict(lastSignal="leave"),
                dict(eventType=2),
                dict(controllerInstance=999),
                dict(eventControllerInstance=999),
                dict(widgetInstance=999),
                dict(eventWidgetInstance=999),
                dict(eventSurfaceInstance=999),
                dict(eventNativeInstance=999),
                dict(nativeInstance=999),
                dict(pointerDevice=False),
                dict(hasPosition=False),
                dict(x=233.0),
                dict(y=12.0),
                dict(x=float("nan")),
                dict(limit=0),
                dict(phase=0),
            ):
                with self.subTest(phase=phase, change=change):
                    self.control = copy.deepcopy(original)
                    dispatch = self.control[phase]
                    if phase == "pointerCaptureObservation":
                        dispatch = dispatch["dispatch"]
                    dispatch.update(change)
                    self.assertFalse(self.admitted())

    def test_controller_installed_late_or_later_leave_refuses(self):
        original = copy.deepcopy(self.control)
        for capture in (False, True):
            for key, value in (("attachedAtMonoMs", 108), ("leftAtMonoMs", 113)):
                with self.subTest(capture=capture, key=key):
                    self.control = copy.deepcopy(original)
                    if capture:
                        self.control["pointerCaptureObservation"][key] = value
                    else:
                        self.control[
                            "pointerObserverAttachedAtMonoMs"
                            if key == "attachedAtMonoMs"
                            else "pointerLeftAtMonoMs"
                        ] = value
                    self.assertFalse(self.admitted())

    def test_actual_device_point_pick_surface_and_x_window_are_all_required(self):
        original = copy.deepcopy(self.control)
        for change in (
            dict(status="unavailable"),
            dict(surface="fixture-window"),
            dict(surfaceInstance=999),
            dict(nativeInstance=999),
            dict(x=251.0),
            dict(y=335.0),
            dict(pickedAncestors=[]),
            dict(
                pickedAncestors=[
                    dict(instance=999, isObservedControl=True, visible=True, mapped=True)
                ]
            ),
            dict(
                pickedAncestors=[
                    dict(instance=224, isObservedControl=True, visible=True, mapped=False)
                ]
            ),
        ):
            with self.subTest(device=change):
                self.control = copy.deepcopy(original)
                self.control["pointerObservation"].update(change)
                self.assertFalse(self.admitted())
        self.control = original
        original_pointer = dict(self.pointer)
        for change in (
            dict(status="unavailable"),
            dict(observedAtMonoMs=107),
            dict(observedAtMonoMs=121),
            dict(WINDOW=999),
            dict(X=987),
            dict(Y=453),
        ):
            with self.subTest(pointer=change):
                self.pointer = {**original_pointer, **change}
                self.assertFalse(self.admitted())

    def test_unmap_replacement_old_paint_or_fixed_point_outside_still_refuses(self):
        original = copy.deepcopy(self.control)
        mutations = (
            lambda c: c.update(instance=999),
            lambda c: c.update(enabled=False),
            lambda c: c.update(visible=False),
            lambda c: c["paintObservation"].update(mapGeneration=3, paintedMapGeneration=3),
            lambda c: c["paintObservation"].update(paintedAtMonoMs=121),
            lambda c: c["paintObservation"].update(ready=False),
        )
        for mutate in mutations:
            self.control = copy.deepcopy(original)
            mutate(self.control)
            self.assertFalse(self.admitted())
        self.control = original
        self.point = (976, 400)
        self.assertFalse(self.admitted())


class GlossarySaveOutcomeTests(unittest.TestCase):
    def test_actual_ime_refusal_ends_one_click_without_waiting_for_deadline(self):
        with self.assertRaisesRegex(AssertionError, "explicit Save refused"):
            glossary_save_outcome(
                [], {"fixture-glossary-feedback": {"text": "请先完成两个词条的输入法组合，再保存"}}
            )

    def test_pending_does_not_invent_success_or_refusal(self):
        self.assertIsNone(glossary_save_outcome([], {}))
        self.assertIsNone(
            glossary_save_outcome(
                [],
                {"fixture-glossary-feedback": {"text": "仅保存明确填写的两个词，不从译文自动学习"}},
            )
        )
        saved = {"formId": "owned-form", "rule": {"sourceTerm": "PR", "targetTerm": "pull request"}}
        self.assertEqual(glossary_save_outcome([{"value": saved}], {}), saved)


class GlossaryPopupSizeTests(unittest.TestCase):
    def test_scroll_decision_waits_for_the_mapped_menu_and_action_to_be_painted(self):
        # CI479 mapped the menu at frame 2537, before its first layout/paint.
        # A zero allocation is pending evidence, not an impossible viewport.
        action = GlossaryPaintGeometryTests().control()
        menu = GlossaryPaintGeometryTests().control()
        menu["scrollViewport"] = dict(y=100, height=200)
        snapshot = {
            "observedAtMs": 120,
            "overlayControls": {
                "typelay-glossary-add": action,
                "typelay-language-menu": menu,
            },
        }
        action["height"] = action["surfaceGeometry"]["height"] = 0
        action["paintObservation"]["ready"] = False
        menu["height"] = menu["surfaceGeometry"]["height"] = 0
        menu["scrollViewport"]["height"] = 0
        menu["paintObservation"]["ready"] = False
        self.assertIsNone(glossary_menu_scroll_observation(snapshot, "typelay-glossary-add", 224))
        painted_menu = GlossaryPaintGeometryTests().control()
        painted_menu["scrollViewport"] = dict(y=100, height=200)
        snapshot["overlayControls"]["typelay-language-menu"] = painted_menu
        self.assertIsNone(glossary_menu_scroll_observation(snapshot, "typelay-glossary-add", 224))
        snapshot["overlayControls"]["typelay-language-menu"] = menu
        snapshot["overlayControls"]["typelay-glossary-add"] = GlossaryPaintGeometryTests().control()
        self.assertIsNone(glossary_menu_scroll_observation(snapshot, "typelay-glossary-add", 224))
        menu = painted_menu
        snapshot["overlayControls"]["typelay-language-menu"] = menu
        self.assertEqual(
            glossary_menu_scroll_observation(snapshot, "typelay-glossary-add", 224), (menu, 1)
        )
        # Completed frames retain the strict whole-control assertion.
        menu["scrollViewport"]["height"] = 21
        with self.assertRaisesRegex(AssertionError, "whole control"):
            glossary_menu_scroll_observation(snapshot, "typelay-glossary-add", 224)
        with self.assertRaisesRegex(AssertionError, "replaced before scroll"):
            glossary_menu_scroll_observation(snapshot, "typelay-glossary-add", 225)
        menu["visible"] = False
        with self.assertRaisesRegex(AssertionError, "menu control lost"):
            glossary_menu_scroll_observation(snapshot, "typelay-glossary-add", 224)

    def test_fresh_scroll_snapshot_waits_for_adjustment_without_another_wheel(self):
        def snapshot(at, value, instance=7):
            return {
                "observedAtMs": at,
                "overlayControls": {
                    "typelay-language-menu": {
                        "instance": instance,
                        "visible": True,
                        "scrollValue": value,
                    }
                },
            }

        self.assertFalse(glossary_menu_scroll_advanced(snapshot(99, 12), 7, 0, 1, 100))
        self.assertFalse(glossary_menu_scroll_advanced(snapshot(101, 0), 7, 0, 1, 100))
        self.assertTrue(glossary_menu_scroll_advanced(snapshot(102, 12), 7, 0, 1, 100))
        self.assertTrue(glossary_menu_scroll_advanced(snapshot(103, 4), 7, 12, -1, 102))
        with self.assertRaisesRegex(AssertionError, "menu lost"):
            glossary_menu_scroll_advanced(snapshot(104, 15, 8), 7, 0, 1, 100)

    def test_mapped_menu_action_needs_scroll_when_outside_viewport(self):
        viewport = dict(y=100, height=200)
        for y, expected in ((99, -1), (100, 0), (278, 0), (279, 1), (421, 1)):
            with self.subTest(y=y):
                control = dict(visible=True, surfaceGeometry=dict(y=y, height=22))
                self.assertEqual(glossary_menu_scroll_direction(control, viewport), expected)
        with self.assertRaisesRegex(AssertionError, "whole control"):
            glossary_menu_scroll_direction(dict(surfaceGeometry=dict(y=100, height=201)), viewport)

    def test_mapped_surface_must_fit_measured_minimum_in_both_axes(self):
        popup = dict(
            visible=True,
            mapped=True,
            surfaceMapped=True,
            minimumWidth=520,
            minimumHeight=150,
            surfaceWidth=520,
            surfaceHeight=434,
        )
        self.assertTrue(glossary_popup_fits(popup))
        for change in (
            dict(minimumHeight=450),
            dict(minimumWidth=521),
            dict(surfaceHeight=0),
            dict(surfaceWidth=True),
            dict(mapped=False),
            dict(visible=False),
            dict(surfaceMapped=False),
        ):
            with self.subTest(change=change):
                self.assertFalse(glossary_popup_fits(popup | change))
        self.assertFalse(glossary_popup_fits(None))
        self.assertFalse(glossary_popup_fits({}))


class GlossaryInteractionLossTests(unittest.TestCase):
    def test_initial_layout_and_stale_absence_are_not_loss(self):
        self.assertIsNone(glossary_interaction_loss(None, None, {"visible": False}, 120, 100))
        self.assertIsNone(
            glossary_interaction_loss({"instance": 1}, None, {"visible": True}, 99, 100)
        )
        self.assertIsNone(
            glossary_interaction_loss(
                {"instance": 1}, {"instance": 1, "visible": True}, {"visible": True}, 120, 100
            )
        )

    def test_refused_renewal_stops_even_with_old_visible_snapshot(self):
        control = {"instance": 1, "visible": True}
        self.assertEqual(
            glossary_interaction_loss(control, control, {"visible": False}, 99, 100),
            "renewal refused",
        )

    def test_fresh_disappearance_or_replacement_stops_old_interaction(self):
        before = {
            "instance": 1,
            "surfaceGeometry": {"surfaceInstance": 2},
            "paintObservation": {"mapGeneration": 3},
        }
        for current, reason in (
            (None, "observed control disappeared"),
            ({"instance": 4, "visible": True}, "control instance replaced"),
            (
                {"instance": 1, "visible": True, "surfaceGeometry": {"surfaceInstance": 5}},
                "surfaceInstance replaced",
            ),
            (
                {**before, "visible": True, "paintObservation": {"mapGeneration": 4}},
                "mapGeneration replaced",
            ),
        ):
            with self.subTest(reason=reason):
                self.assertEqual(
                    glossary_interaction_loss(before, current, {"visible": True}, 120, 100), reason
                )


class GtkPreviewObservationTests(unittest.TestCase):
    def test_refused_render_is_recorded_once_without_reading_or_retrying(self):
        class Client:
            hello = {"backend": "gtk3", "lifetime": "l"}
            calls = []

            def call(self, operation, request):
                self.calls.append((operation, request))
                return {"visible": False}

        client, evidence = Client(), {}
        with self.assertRaises(AssertionError):
            check_preview_colors(
                client,
                {"target": "t", "authority": {}},
                "g",
                lambda: self.fail("refused render must not poll"),
                {},
                evidence,
            )
        self.assertEqual(len(client.calls), 1)
        phase = evidence["previewColorPhases"][0]
        self.assertEqual(phase["revision"], 1)
        self.assertIs(phase["responseVisible"], False)
        self.assertEqual(phase["state"], "ValidTranslation")
        self.assertLessEqual(phase["expiresAtMs"] - phase["renderStartedAtMs"], 800)
        self.assertNotIn("lastObservation", phase)

    def test_eight_color_requests_keep_original_checks_and_bounded_observations(self):
        class Client:
            hello = {"backend": "gtk3", "lifetime": "l"}
            requests = []

            def call(self, operation, request):
                self.requests.append(request)
                return {"visible": True}

        client, evidence = Client(), {}
        initial = {"first": "source", "second": "other"}

        def state():
            view = client.requests[-1]["view"]
            muted = (
                view["state"] == "Translating"
                and view["text"]
                and not view["presentation"]["editorOwnsText"]
            )
            return {
                **initial,
                "observedAtMs": 10**15,
                "focus": "fixture-first",
                "imeClear": True,
                "candidate": {
                    "text": view["text"],
                    "foreground": "rgb(116,114,108)" if muted else "rgb(20,20,19)",
                    "editablePreview": False,
                },
            }

        check_preview_colors(
            client, {"target": "t", "authority": {}}, "g", state, initial, evidence
        )
        self.assertEqual(len(client.requests), 8)
        self.assertEqual(len(evidence["previewColorPhases"]), 8)
        self.assertTrue(
            all(p["lastObservation"]["textMatches"] for p in evidence["previewColorPhases"])
        )


class PackageControlScrollTests(unittest.TestCase):
    def test_clipped_package_card_uses_only_the_observed_bottom_overflow(self):
        self.assertEqual(
            package_scroll_value(0, 0, 625, (238, 672, 368, 165), (202, 130, 440, 661)),
            46,
        )

    def test_upper_clipping_scrolls_back_without_moving_an_already_visible_card(self):
        viewport = (202, 130, 440, 661)
        self.assertEqual(package_scroll_value(120, 0, 625, (238, 84, 368, 165), viewport), 74)
        self.assertEqual(package_scroll_value(120, 0, 625, (238, 200, 368, 165), viewport), 120)

    def test_one_scroll_is_clamped_but_never_claims_the_button_is_visible(self):
        card, viewport = (238, 672, 368, 165), (202, 130, 440, 661)
        self.assertEqual(package_scroll_value(600, 0, 625, card, viewport), 625)
        self.assertFalse(rectangle_contains(viewport, card))
        self.assertFalse(rectangle_contains(viewport, (238, 778, 122, 34)))
        self.assertFalse(rectangle_contains(viewport, (-(2**31), -(2**31), 122, 34)))
        self.assertTrue(rectangle_contains(viewport, (238, 757, 122, 34)))

    def test_unknown_ranges_or_bounds_cannot_authorize_a_scroll(self):
        card, viewport = (238, 672, 368, 165), (202, 130, 440, 661)
        for values in (
            (float("nan"), 0, 625),
            (0, 0, float("inf")),
            (0, float("-inf"), 625),
            (0, 1, 625),
            (626, 0, 625),
            (False, 0, 625),
        ):
            with self.subTest(values=values), self.assertRaises(AssertionError):
                package_scroll_value(*values, card, viewport)
        for unknown in (None, (0, 0, 1), (-(2**31), 130, 440, 661), (202, 130, 0, 661)):
            with self.subTest(bounds=unknown), self.assertRaises(AssertionError):
                package_scroll_value(0, 0, 625, unknown, viewport)
            with self.subTest(clip=unknown), self.assertRaises(AssertionError):
                package_scroll_value(0, 0, 625, card, unknown)

    def test_vertical_scroll_cannot_rescue_horizontal_or_oversized_clipping(self):
        viewport = (202, 130, 440, 661)
        for card in ((201, 672, 368, 165), (275, 672, 368, 165), (238, 130, 368, 662)):
            with self.subTest(card=card), self.assertRaises(AssertionError):
                package_scroll_value(0, 0, 625, card, viewport)


class GtkReentryGeometryTests(unittest.TestCase):
    def test_gtk4_reopened_menu_does_not_click_childs_previous_allocation(self):
        # CI479 rev27: mapped menu still had zero allocation. The selector
        # retained its collapsed y=153 and the old runner clicked (988, 167).
        actual = dict(
            fullscreen=True,
            width=1280,
            height=800,
            scale=1,
            observedAtMs=976617,
            overlayControls={
                "typelay-language-menu": dict(
                    instance=3, visible=True, x=728.0, y=113.0, width=0, height=0
                ),
                "typelay-source-language": dict(
                    instance=4, visible=True, x=740.0, y=153.0, width=496, height=28
                ),
            },
        )
        self.assertIsNone(
            language_menu_point(
                actual,
                "typelay-source-language",
                dict(renderReturnedAtMonoMs=976472, expiresAtMs=977269),
                (1280, 800),
                976659,
                {"typelay-language-menu": 3, "typelay-source-language": 4},
            )
        )

    def language_menu(self, name="typelay-source-language"):
        # Completed-frame companion to the real pre-layout sample above. This
        # is controlled observation evidence, not a fabricated desktop result.
        control, menu = (GlossaryPaintGeometryTests().control() for _ in range(2))
        for item, identity, x, y, width, height in (
            (control, 4, 740, 288, 496, 28),
            (menu, 3, 728, 248, 520, 278),
        ):
            item.update(instance=identity, x=x, y=y, width=width, height=height)
            item["surfaceGeometry"].update(instance=identity, x=x, y=y, width=width, height=height)
            item["paintObservation"]["geometry"] = dict(item["surfaceGeometry"])
        menu["scrollViewport"] = {
            **menu["surfaceGeometry"],
            "instance": 9,
            "x": 740,
            "y": 260,
            "width": 496,
            "height": 254,
        }
        return dict(
            fullscreen=True,
            width=1280,
            height=800,
            scale=1,
            observedAtMs=120,
            overlayControls={name: control, "typelay-language-menu": menu},
        )

    def menu_point(self, actual, name="typelay-source-language", now=120):
        return language_menu_point(
            actual,
            name,
            dict(renderReturnedAtMonoMs=100, expiresAtMs=800),
            (1280, 800),
            now,
            {name: 4, "typelay-language-menu": 3},
        )

    def test_language_menu_uses_completed_surface_not_old_widget_tree_coordinates(self):
        actual = self.language_menu()
        actual["overlayControls"]["typelay-source-language"]["y"] = 153
        self.assertEqual(self.menu_point(actual), (988, 302))
        # Testing disabled Apply must remain possible; click readiness does
        # not imply an enabled action or exempt the eventual no-save assertion.
        actual = self.language_menu("typelay-apply-languages")
        actual["overlayControls"]["typelay-apply-languages"]["enabled"] = False
        self.assertEqual(self.menu_point(actual, "typelay-apply-languages"), (988, 302))

    def test_language_menu_waits_for_both_current_frames_within_original_lease(self):
        original = self.language_menu()
        for name in ("typelay-source-language", "typelay-language-menu"):
            for change in (
                {"ready": False},
                {"paintedMapGeneration": 1},
                {"paintedFrame": 7},
                {"paintedAtMonoMs": 121},
                {"geometry": None},
            ):
                with self.subTest(name=name, change=change):
                    actual = copy.deepcopy(original)
                    actual["overlayControls"][name]["paintObservation"].update(change)
                    self.assertIsNone(self.menu_point(actual))
        self.assertIsNone(self.menu_point({**original, "observedAtMs": 100}))
        self.assertEqual(self.menu_point(original, now=800), (988, 302))
        with self.assertRaisesRegex(AssertionError, "lease expired"):
            self.menu_point(original, now=801)

    def test_language_menu_replacement_disappearance_and_clipping_still_fail(self):
        original = self.language_menu()
        for name in ("typelay-source-language", "typelay-language-menu"):
            for change in ({"instance": 99}, {"visible": False}):
                with self.subTest(name=name, change=change):
                    actual = copy.deepcopy(original)
                    actual["overlayControls"][name].update(change)
                    with self.assertRaises(AssertionError):
                        self.menu_point(actual)
            actual = copy.deepcopy(original)
            del actual["overlayControls"][name]
            with self.assertRaisesRegex(AssertionError, "replaced"):
                self.menu_point(actual)
        for change in (
            {"surfaceInstance": 999},
            {"scale": 2},
            {"y": 289},
            {"x": 741},
            {"height": 55},
            {"width": 495},
            {"height": 0},
        ):
            actual = copy.deepcopy(original)
            actual["overlayControls"]["typelay-language-menu"]["scrollViewport"].update(change)
            with (
                self.subTest(change=change),
                self.assertRaisesRegex(AssertionError, "not fully visible"),
            ):
                self.menu_point(actual)
        actual = copy.deepcopy(original)
        del actual["overlayControls"]["typelay-language-menu"]["scrollViewport"]
        with self.assertRaisesRegex(AssertionError, "viewport geometry missing"):
            self.menu_point(actual)

    def source(self, space):
        return {
            "geometry": {
                "space": space,
                "scale": 2,
                "frameWidth": 200,
                "frameHeight": 100,
                "bounds": {"x": 12, "y": 8, "width": 80, "height": 24},
            }
        }

    def test_each_backend_accepts_only_its_own_parent_space(self):
        for backend, space in (("gtk3", "gtk-toplevel"), ("gtk4", "gtk4-toplevel")):
            with self.subTest(backend=backend):
                self.assertEqual(
                    validated_geometry(self.source(space), backend),
                    {"bounds": {"x": 24, "y": 16, "width": 160, "height": 48}},
                )
                other = "gtk4-toplevel" if backend == "gtk3" else "gtk-toplevel"
                with self.assertRaises(AssertionError):
                    validated_geometry(self.source(other), backend)
                with self.assertRaises(AssertionError):
                    validated_geometry(self.source("screen"), backend)

    def test_unknown_backend_and_missing_geometry_remain_rejected(self):
        with self.assertRaises(AssertionError):
            validated_geometry(self.source("gtk-toplevel"), "unknown")
        for source in ({}, {"geometry": None}):
            with self.subTest(source=source), self.assertRaises(AssertionError):
                validated_geometry(source, "gtk4")

    def test_control_readiness_requires_fresh_allocated_in_bounds_snapshot(self):
        control = {"visible": True, "x": 740, "y": 122, "width": 97, "height": 28}
        actual = {
            "fullscreen": True,
            "width": 1280,
            "height": 800,
            "scale": 1,
            "observedAtMs": 101,
            "overlayControls": {"trigger": control},
        }
        self.assertIs(clickable_control(actual, "trigger", 100, (1280, 800)), actual)
        self.assertIsNone(clickable_control(actual, "trigger", 101, (1280, 800)))
        for changed in (
            {"x": 41, "y": 67, "width": 0, "height": 0},
            {"x": -1, "y": -1, "width": 1, "height": 1},
            {"visible": False},
            {"x": float("nan")},
            {"height": float("inf")},
            {"width": True},
            {"x": 1280},
        ):
            with self.subTest(changed=changed):
                pending = {**actual, "overlayControls": {"trigger": {**control, **changed}}}
                self.assertIsNone(clickable_control(pending, "trigger", 100, (1280, 800)))
        self.assertIsNone(clickable_control(actual, "missing", 100, (1280, 800)))
        self.assertIsNone(clickable_control(actual, "trigger", 100, (640, 400)))

    def test_expand_uses_current_allocation_and_never_an_expired_or_foreign_parent(self):
        evidence = {"renderReturnedAtMonoMs": 100, "expiresAtMs": 800}
        button = {
            "visible": True,
            "enabled": True,
            "instance": 7,
            "scale": 1,
            "x": -1,
            "y": -1,
            "width": 1,
            "height": 1,
        }
        actual = {
            "fullscreen": True,
            "width": 1280,
            "height": 800,
            "scale": 1,
            "observedAtMs": 101,
            "overlayControls": {"fixture-expand": button},
        }
        self.assertIsNone(expand_click_point(actual, evidence, (1280, 800), 200))
        allocated = {
            **actual,
            "observedAtMs": 102,
            "overlayControls": {
                "fixture-expand": {**button, "x": 1100, "y": 120, "width": 80, "height": 28}
            },
        }
        self.assertEqual(expand_click_point(allocated, evidence, (1280, 800), 201), (1140, 134))
        for changed in ({"fullscreen": False}, {"width": 640}, {"scale": 2}, {"observedAtMs": 100}):
            with self.subTest(changed=changed):
                self.assertIsNone(
                    expand_click_point({**allocated, **changed}, evidence, (1280, 800), 202)
                )
        wrong_scale = {
            **allocated,
            "overlayControls": {
                "fixture-expand": {**allocated["overlayControls"]["fixture-expand"], "scale": 2}
            },
        }
        self.assertIsNone(expand_click_point(wrong_scale, evidence, (1280, 800), 202))
        for snapshot in (actual, allocated):
            with (
                self.subTest(snapshot=snapshot),
                self.assertRaisesRegex(AssertionError, "lease expired"),
            ):
                expand_click_point(snapshot, evidence, (1280, 800), 801)

    def test_expand_hover_requires_same_live_button_and_original_point_before_deadline(self):
        evidence = {"renderReturnedAtMonoMs": 100, "expiresAtMs": 800}
        button = {
            "visible": True,
            "enabled": True,
            "instance": 7,
            "scale": 1,
            "x": 1100,
            "y": 120,
            "width": 80,
            "height": 28,
            "pointerInside": True,
            "pointerEnteredAtMs": 150,
        }
        actual = {
            "fullscreen": True,
            "width": 1280,
            "height": 800,
            "scale": 1,
            "observedAtMs": 210,
            "overlayControls": {"fixture-expand": button},
        }

        def current(snapshot, now=220):
            return expand_hover_current(snapshot, evidence, (1280, 800), now, 7, (1140, 134), 200)

        # A real prior enter is still current after the one move, until leave/unmap.
        self.assertTrue(current(actual))
        for changed in (
            {"pointerInside": False},
            {"pointerEnteredAtMs": 0},
            {"instance": 8},
            {"visible": False},
            {"enabled": False},
            {"x": 1110},
            {"scale": 2},
        ):
            with self.subTest(changed=changed):
                self.assertFalse(
                    current(
                        {**actual, "overlayControls": {"fixture-expand": {**button, **changed}}}
                    )
                )
        for changed in ({"observedAtMs": 199}, {"width": 640}, {"scale": 2}, {"fullscreen": False}):
            with self.subTest(changed=changed):
                self.assertFalse(current({**actual, **changed}))
        with self.assertRaisesRegex(AssertionError, "lease expired"):
            current(actual, 801)

    def test_language_trigger_requires_current_label_completed_frame_without_extending_lease(self):
        phase = {"renderReturnedAtMonoMs": 100, "expiresAtMs": 800}
        button = {
            "visible": True,
            "enabled": True,
            "instance": 1,
            "scale": 1,
            "x": 740,
            "y": 122,
            "width": 97,
            "height": 28,
            "labelRevision": 3,
            "paintedLabelRevision": 3,
            "labelLayoutCurrent": True,
        }
        actual = {
            "fullscreen": True,
            "width": 1280,
            "height": 800,
            "scale": 1,
            "observedAtMs": 110,
            "overlayControls": {"typelay-menu-trigger": button},
        }
        self.assertEqual(language_trigger_point(actual, phase, (1280, 800), 120), (788, 136))
        # Mid-frame label change, clock invalidation, unmap, and absent receipt
        # must not promote a merely mapped/text-updated button to clickable.
        for change in (
            {"labelRevision": 4},
            {"paintedLabelRevision": 0, "labelLayoutCurrent": False},
            {"visible": False},
            {"labelRevision": 0},
            {"labelLayoutCurrent": False},
            {"enabled": False},
        ):
            with self.subTest(change=change):
                changed = {
                    **actual,
                    "overlayControls": {"typelay-menu-trigger": {**button, **change}},
                }
                self.assertIsNone(language_trigger_point(changed, phase, (1280, 800), 120))
        for change in ({"width": 640}, {"scale": 2}, {"observedAtMs": 100}):
            self.assertIsNone(language_trigger_point({**actual, **change}, phase, (1280, 800), 120))
        with self.assertRaisesRegex(AssertionError, "lease expired"):
            language_trigger_point(actual, phase, (1280, 800), 801)

    def test_gtk4_language_trigger_waits_for_current_label_before_its_single_move(self):
        # CI restore-valid-choices changed the same mapped trigger from width
        # 210 to 88. The first post-render report still exposed the old bounds.
        phase = {"renderReturnedAtMonoMs": 991044, "expiresAtMs": 991840}
        button = GlossaryPaintGeometryTests().control()
        button.update(x=744.0, y=124.0, width=210, instance=2)
        surface = button["surfaceGeometry"]
        surface.update(x=744.0, y=124.0, width=210, instance=2, surfaceInstance=1)
        paint = button["paintObservation"]
        paint.update(
            geometry=dict(surface),
            labelRevision=2,
            paintedLabelRevision=1,
            ready=False,
            paintedAtMonoMs=991000,
        )
        actual = dict(
            fullscreen=True,
            width=1280,
            height=800,
            scale=1,
            observedAtMs=991045,
            overlayControls={"typelay-menu-trigger": button},
        )
        self.assertIsNone(language_trigger_point(actual, phase, (1280, 800), 991045, "gtk4"))
        # Geometry alone cannot certify a new label, even before its width changes.
        paint["ready"] = True
        self.assertIsNone(language_trigger_point(actual, phase, (1280, 800), 991045, "gtk4"))
        self.assertEqual(
            language_trigger_point(actual, phase, (1280, 800), 991045, "gtk4", layout=False),
            (849, 135),
        )
        button["width"] = surface["width"] = 88
        paint.update(geometry=dict(surface), paintedLabelRevision=2, paintedAtMonoMs=991080)
        actual["observedAtMs"] = 991090
        self.assertEqual(
            language_trigger_point(actual, phase, (1280, 800), 991090, "gtk4"), (788, 135)
        )
        for change in (
            {"ready": False},
            {"labelRevision": 0, "paintedLabelRevision": 0},
            {"labelRevision": 1, "paintedLabelRevision": True},
            {"labelRevision": 3},
            {"paintedLabelRevision": 0},
            {"geometry": {**surface, "width": 210}},
            {"paintedMapGeneration": 1},
        ):
            with self.subTest(change=change):
                changed = {
                    **actual,
                    "overlayControls": {
                        "typelay-menu-trigger": {
                            **button,
                            "paintObservation": {**paint, **change},
                        },
                    },
                }
                self.assertIsNone(
                    language_trigger_point(changed, phase, (1280, 800), 991090, "gtk4")
                )
        with self.assertRaisesRegex(AssertionError, "lease expired"):
            language_trigger_point(actual, phase, (1280, 800), 991841, "gtk4")

    def test_language_trigger_hover_binds_fresh_instance_point_and_surface(self):
        phase = {"renderReturnedAtMonoMs": 100, "expiresAtMs": 800}
        button = {
            "visible": True,
            "enabled": True,
            "instance": 7,
            "scale": 1,
            "x": 740,
            "y": 122,
            "width": 100,
            "height": 28,
            "pointerInside": True,
            "pointerEnteredAtMs": 190,
            "pointerEnteredAtMonoMs": 190,
            "surfaceGeometry": {"x": 720, "y": 112, "scale": 1, "surfaceInstance": 8},
        }
        painted = GlossaryPaintGeometryTests().control()
        button["surfaceGeometry"] = {
            **painted["surfaceGeometry"],
            **button["surfaceGeometry"],
            "instance": 7,
            "width": 100,
            "height": 28,
        }
        button["paintObservation"] = {
            **painted["paintObservation"],
            "geometry": dict(button["surfaceGeometry"]),
            "labelRevision": 1,
            "paintedLabelRevision": 1,
        }
        actual = {
            "fullscreen": True,
            "width": 1280,
            "height": 800,
            "scale": 1,
            "observedAtMs": 210,
            "overlayControls": {"typelay-menu-trigger": button},
        }
        for backend, point, layout in (
            ("gtk3", (790, 136), False),
            ("gtk4", (770, 126), False),
            ("gtk4", (770, 126), True),
        ):

            def current(snapshot, now=220):
                return language_trigger_hover_current(
                    snapshot, phase, (1280, 800), now, backend, 7, 8, point, 200, layout=layout
                )

            with self.subTest(backend=backend, layout=layout):
                self.assertTrue(current(actual))
                self.assertFalse(current({**actual, "observedAtMs": 199}))
                for scale in (1, 2):
                    moved = {
                        **button,
                        "scale": scale,
                        "x": 744,
                        "y": 130,
                        "width": 104,
                        "surfaceGeometry": {
                            **button["surfaceGeometry"],
                            "x": 724,
                            "y": 120,
                            "scale": scale,
                            "width": 104,
                        },
                    }
                    moved["paintObservation"] = {
                        **button["paintObservation"],
                        "geometry": dict(moved["surfaceGeometry"]),
                    }
                    snapshot = {
                        **actual,
                        "scale": scale,
                        "overlayControls": {"typelay-menu-trigger": moved},
                    }
                    self.assertTrue(
                        language_trigger_hover_current(
                            snapshot,
                            phase,
                            (1280 * scale, 800 * scale),
                            220,
                            backend,
                            7,
                            8,
                            tuple(value * scale for value in point),
                            200,
                            layout=layout,
                        )
                    )
                for change in (
                    {"instance": 9},
                    {"pointerInside": False},
                    {"width": 50},
                    {"width": 49},
                    {"height": 14},
                    {"height": 13},
                    {"visible": False},
                    {"enabled": False},
                    {"pointerEnteredAtMs": 0, "pointerEnteredAtMonoMs": 0},
                ):
                    changed = {
                        **actual,
                        "overlayControls": {"typelay-menu-trigger": {**button, **change}},
                    }
                    changed_control = changed["overlayControls"]["typelay-menu-trigger"]
                    changed_control["surfaceGeometry"] = {
                        **button["surfaceGeometry"],
                        **{key: changed_control[key] for key in ("instance", "width", "height")},
                    }
                    changed_control["paintObservation"] = {
                        **button["paintObservation"],
                        "geometry": dict(changed_control["surfaceGeometry"]),
                    }
                    self.assertFalse(current(changed))
                with self.assertRaisesRegex(AssertionError, "lease expired"):
                    current(actual, 801)
        for surface in (
            None,
            {"x": 720, "y": 112, "scale": 2, "surfaceInstance": 8},
            {"x": float("nan"), "y": 112, "scale": 1, "surfaceInstance": 8},
            {"x": 720, "y": 112, "scale": 1, "surfaceInstance": 9},
        ):
            changed = {
                **actual,
                "overlayControls": {"typelay-menu-trigger": {**button, "surfaceGeometry": surface}},
            }
            for layout in (False, True):
                self.assertFalse(
                    language_trigger_hover_current(
                        changed,
                        phase,
                        (1280, 800),
                        220,
                        "gtk4",
                        7,
                        8,
                        (770, 126),
                        200,
                        layout=layout,
                    )
                )

    def test_click_maps_parent_local_point_at_native_scale(self):
        actual = {"fullscreen": True, "width": 640, "height": 400, "scale": 2}
        self.assertTrue(parent_geometry_ready(actual, (1280, 800)))
        self.assertEqual(
            click_point(actual, (70, 50), (1280, 800)),
            (140, 100),
        )

    def test_click_rejects_unmatched_parent_or_invalid_point(self):
        actual = {"fullscreen": True, "width": 640, "height": 400, "scale": 2}
        point = (70, 50)
        for changed in ({"fullscreen": False}, {"width": 639}, {"scale": 0}, {"scale": True}):
            with self.subTest(changed=changed):
                self.assertFalse(parent_geometry_ready({**actual, **changed}, (1280, 800)))
                with self.assertRaises(AssertionError):
                    click_point({**actual, **changed}, point, (1280, 800))
        for point in ((-1, 50), (640, 50), (70, 400), (70, float("nan"))):
            with self.subTest(point=point), self.assertRaises(AssertionError):
                click_point(actual, point, (1280, 800))


class SettingsScrollCalibrationTests(unittest.TestCase):
    def setUp(self):
        self.initial = {
            "pid": 100,
            "ancestry": tuple(
                (object(), 100, role) for role in ("viewport", "scroll-pane", "panel", "frame")
            ),
            "viewport": (199, 155, 426, 515),
            "pane": (199, 155, 426, 515),
            "frame": (0, 0, 668, 820),
            "showing": (True, True),
            "alive": True,
        }
        self.current = {**self.initial, "viewport": (-(2**31), -(2**31), 426, 515)}

    def test_only_calibrated_sentinel_recovers_the_live_pane_rectangle(self):
        self.assertEqual(
            settings_viewport_from_pane(self.initial, self.current), (199, 155, 426, 515)
        )
        self.assertIsNone(settings_viewport_from_pane(None, self.current))
        self.assertIsNone(settings_viewport_from_pane(self.initial, self.initial))

    def test_moving_the_same_frame_preserves_the_observed_local_clip(self):
        moved = {
            **self.current,
            "pane": (219, 185, 426, 515),
            "frame": (20, 30, 668, 820),
        }
        self.assertEqual(settings_viewport_from_pane(self.initial, moved), moved["pane"])

    def test_draft_footer_shrink_uses_the_smaller_live_clip(self):
        initial = {
            **self.initial,
            "viewport": (202, 130, 614, 453),
            "pane": (202, 130, 614, 453),
            "frame": (0, 0, 842, 612),
        }
        current = {
            **initial,
            "viewport": (-(2**31), -(2**31), 614, 363),
            "pane": (202, 130, 614, 363),
        }
        clip = settings_viewport_from_pane(initial, current)
        self.assertEqual(clip, (202, 130, 614, 363))
        self.assertIsNotNone(settings_text_visible_point((226, 409, 0, 19), [clip]))
        self.assertIsNone(settings_text_visible_point((226, 510, 0, 19), [clip]))
        for change in (
            {"viewport": (-(2**31), -(2**31), 614, 454), "pane": (202, 130, 614, 454)},
            {"viewport": (-(2**31), -(2**31), 613, 363), "pane": (202, 130, 613, 363)},
            {"viewport": (-(2**31), -(2**31), 614, 363), "pane": (202, 131, 614, 363)},
            {"viewport": (-(2**31), -(2**31), 614, 453)},
        ):
            with self.subTest(change=change):
                self.assertIsNone(settings_viewport_from_pane(initial, {**current, **change}))

    def test_changed_layout_requires_a_new_unscrolled_basis_before_scrolling(self):
        # A page switch or native window allocation can change the same pair.
        # An old basis cannot authorize that new geometry, even when identities
        # match; a separately observed unclipped first screen can.
        layout = {
            **self.initial,
            "viewport": (202, 95, 440, 696),
            "pane": (202, 95, 440, 696),
            "frame": (0, 0, 668, 800),
        }
        scrolled = {**layout, "viewport": (-(2**31), -(2**31), 440, 696)}
        self.assertIsNone(settings_viewport_from_pane(self.initial, scrolled))
        self.assertEqual(settings_viewport_from_pane(layout, scrolled), layout["pane"])
        # A failed/scrolled observation is never itself a replacement basis.
        self.assertIsNone(settings_viewport_from_pane(scrolled, scrolled))

    def test_new_instance_requires_its_own_valid_first_screen_measurement(self):
        reopened = {
            **self.initial,
            "pid": 101,
            "ancestry": tuple((object(), 101, role) for _, _, role in self.initial["ancestry"]),
        }
        current = {**reopened, "viewport": self.current["viewport"]}
        self.assertIsNone(settings_viewport_from_pane(self.initial, current))
        self.assertEqual(settings_viewport_from_pane(reopened, current), (199, 155, 426, 515))

    def test_replaced_window_parent_or_process_cannot_reuse_calibration(self):
        for index in range(len(self.current["ancestry"])):
            with self.subTest(replaced_ancestor=index):
                path = list(self.current["ancestry"])
                _, pid, role = path[index]
                path[index] = (object(), pid, role)
                self.assertIsNone(
                    settings_viewport_from_pane(
                        self.initial, {**self.current, "ancestry": tuple(path)}
                    )
                )
        self.assertIsNone(settings_viewport_from_pane(self.initial, {**self.current, "pid": 101}))
        path = list(self.initial["ancestry"])
        node, _, role = path[1]
        path[1] = (node, 101, role)
        # Even matching observations cannot establish a cross-process pair.
        self.assertIsNone(
            settings_viewport_from_pane(
                {**self.initial, "ancestry": tuple(path)},
                {**self.current, "ancestry": tuple(path)},
            )
        )

    def test_only_a_direct_unclipped_viewport_pane_frame_path_can_calibrate(self):
        original = self.initial["ancestry"]
        for path in (
            original[:1] + ((object(), 100, "panel"),) + original[1:],
            original[:2] + ((object(), 100, "viewport"),) + original[2:],
            original[:-1],
        ):
            with self.subTest(path=path):
                self.assertIsNone(
                    settings_viewport_from_pane(
                        {**self.initial, "ancestry": path}, {**self.current, "ancestry": path}
                    )
                )
        for viewport in ((200, 155, 426, 515), (199, 155, 425, 515)):
            with self.subTest(viewport=viewport):
                self.assertIsNone(
                    settings_viewport_from_pane(
                        {**self.initial, "viewport": viewport}, self.current
                    )
                )

    def test_changed_layout_sizes_or_unknown_bounds_remain_rejected(self):
        for changed in (
            {"viewport": (-(2**31), -(2**31), 426, 514)},
            {"viewport": (-(2**31), 155, 426, 515)},
            {"viewport": (199, -(2**31), 426, 515)},
            {"viewport": (-(2**31) + 1, -(2**31) + 1, 426, 515)},
            {"pane": (199, 155, 425, 515)},
            {"pane": (200, 155, 426, 515)},
            {"pane": (199, 156, 426, 515)},
            {"pane": (-(2**31), -(2**31), 426, 515)},
            {"pane": (199, 155, 0, 515)},
            {"frame": (0, 0, 667, 820)},
            {"frame": (0, 1, 668, 820)},
            {"frame": (-(2**31), -(2**31), 668, 820)},
            {"frame": (0, 0, 668, 0)},
            {"alive": False},
            {"showing": (False, True)},
            {"showing": (True, False)},
        ):
            with self.subTest(changed=changed):
                self.assertIsNone(
                    settings_viewport_from_pane(self.initial, {**self.current, **changed})
                )

    def test_hidden_or_invalid_initial_measurement_cannot_authorize_recovery(self):
        for changed in (
            {"viewport": (-(2**31), -(2**31), 426, 515)},
            {"pane": (199, 155, 426, 0)},
            {"frame": (0, 0, 668, -1)},
            {"alive": False},
            {"showing": (True, False)},
        ):
            with self.subTest(changed=changed):
                self.assertIsNone(
                    settings_viewport_from_pane({**self.initial, **changed}, self.current)
                )


class SettingsTextPointTests(unittest.TestCase):
    def test_character_and_empty_caret_use_actual_reported_position(self):
        clips = [(226, 409, 566, 84), (202, 95, 614, 398), (0, 0, 842, 612)]
        self.assertEqual(settings_text_visible_point((226, 409, 8, 18), clips), (230, 418))
        self.assertEqual(settings_text_visible_point((226, 409, 0, 18), clips), (226, 418))

    def test_outer_clipping_unknown_geometry_and_empty_clip_fail_closed(self):
        character = (230, 410, 8, 18)
        for clips in (
            [],
            [None],
            [(0, 0, 0, 100)],
            [(-(2**31), 0, 900, 900)],
            [(226, 409, 566, 84), (202, 95, 614, 300)],
            [(235, 409, 566, 84)],
            [(0, 0, 234, 900)],
        ):
            with self.subTest(clips=clips):
                self.assertIsNone(settings_text_visible_point(character, clips))

    def test_invalid_character_rectangle_is_not_replaced_by_parent_center(self):
        for character in (
            (0, 0, -1, 18),
            (0, 0, 1, 0),
            (-(2**31), 0, 0, 18),
            (False, 0, 1, 18),
            (0, 0, 1.0, 18),
        ):
            with self.subTest(character=character):
                self.assertIsNone(settings_text_visible_point(character, [(0, 0, 900, 900)]))


if __name__ == "__main__":
    unittest.main()
