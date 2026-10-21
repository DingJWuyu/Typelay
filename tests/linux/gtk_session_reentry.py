"""Real GTK callback reentry through the public native socket, without a model."""

import hashlib
import json
import math
import os
import socket
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

from geometry import anchor_geometry
from native_e2e import Native, key, plan, snapshot
from niri import contents, request
from paths import peer_credentials, process_lineage
from wire import now_ms, receive_sync


def wait_for(predicate, description, timeout=5):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(0.01)
    raise AssertionError(description)


def validated_geometry(source, backend):
    space = {"gtk3": "gtk-toplevel", "gtk4": "gtk4-toplevel"}.get(backend)
    assert space is not None, "GTK reentry requires a known GTK backend"
    geometry = anchor_geometry(source.get("geometry"), space)
    assert geometry is not None, "GTK reentry requires a valid parent-local source anchor"
    return geometry


def parent_geometry_ready(actual, dimensions):
    values = [actual.get(name) for name in ("width", "height", "scale")]
    return (
        actual.get("fullscreen") is True
        and all(
            type(value) in (int, float) and math.isfinite(value) and value > 0 for value in values
        )
        and values[0] * values[2] == dimensions[0]
        and values[1] * values[2] == dimensions[1]
    )


def click_point(actual, point, dimensions):
    assert parent_geometry_ready(actual, dimensions), "GTK parent does not fill the nested client"
    x, y = point
    assert all(type(value) in (int, float) and math.isfinite(value) for value in point)
    assert 0 <= x < actual["width"] and 0 <= y < actual["height"], "click is outside GTK parent"
    return int(x * actual["scale"]), int(y * actual["scale"])


def clickable_control(actual, name, after, dimensions):
    item = actual.get("overlayControls", {}).get(name, {})
    geometry = [item.get(key) for key in ("x", "y", "width", "height")]
    if (
        actual.get("observedAtMs", 0) <= after
        or not parent_geometry_ready(actual, dimensions)
        or not item.get("visible")
        or not all(type(value) in (int, float) and math.isfinite(value) for value in geometry)
    ):
        return None
    x, y, width, height = geometry
    if (
        width <= 0
        or height <= 0
        or not 0 <= x + width / 2 < actual["width"]
        or not 0 <= y + height / 2 < actual["height"]
    ):
        return None
    return actual


def language_control_focused(control):
    return bool(
        control.get("visible")
        and (
            control.get("buttonFocused")
            or (control.get("popupVisible") and control.get("popupFocused"))
        )
    )


def observe_click_geometry(actual):
    # Existing fixture time is MONOTONIC; render/click deadlines use BOOTTIME.
    candidate = actual.get("candidate")
    return {
        "stateObservedAtMonoMs": actual.get("observedAtMs"),
        "parent": {name: actual.get(name) for name in ("fullscreen", "width", "height", "scale")},
        "previewBounds": (
            {name: candidate.get(name) for name in ("x", "y", "width", "height")}
            if isinstance(candidate, dict)
            else None
        ),
    }


def click_once(nested, point, evidence):
    evidence["clickStartedAtMs"] = now_ms()
    try:
        subprocess.run(
            [
                "xdotool",
                "mousemove",
                "--window",
                nested,
                str(point[0]),
                str(point[1]),
                "click",
                "1",
            ],
            check=True,
            timeout=3,
        )
        evidence["clickCommandSucceeded"] = True
    finally:
        # Command return is not the GTK event-delivery timestamp.
        evidence["clickFinishedAtMs"] = now_ms()


def run_reentry_checks(command, environment, output, send_accept, *, gtk4, nested):
    # Both cases dispatch the same real configure command inside the callback.
    # Only the generation changes, so nested IME dispatch cannot fake a pass.
    stages = ["delete", "insert", "selection", "footer", "expand"]
    if gtk4:
        stages.extend(["controller-source", "controller-editor"])
    for stage in stages:
        mutation = stage in ("delete", "insert", "selection")
        for change_generation in (False, True) if mutation else (False,):
            with tempfile.TemporaryDirectory(prefix="tl-gtk-reentry-") as directory:
                run_case(
                    command,
                    environment,
                    output,
                    send_accept,
                    Path(directory),
                    stage,
                    change_generation,
                    nested,
                )


def run_case(command, environment, output, send_accept, runtime, stage, change_generation, nested):
    name = f"gtk-reentry-{stage}-{'changed' if change_generation else 'unchanged'}"
    state_path = output / f"{name}.json"
    state_path.unlink(missing_ok=True)
    display = Path(environment["WAYLAND_DISPLAY"])
    if not display.is_absolute():
        display = Path(environment["XDG_RUNTIME_DIR"]) / display
    replacement = "Replacement body."
    child_environment = {
        **environment,
        "XDG_RUNTIME_DIR": str(runtime),
        "WAYLAND_DISPLAY": str(display),
        "TYPELAY_FIXTURE_STATE": str(state_path),
        "TYPELAY_FIXTURE_REENTRY": str(runtime),
        "TYPELAY_FIXTURE_REENTRY_STAGE": stage,
        "TYPELAY_FIXTURE_REENTRY_TEXT": replacement,
    }
    child_environment.pop("TYPELAY_FIXTURE_TWO_WINDOWS", None)

    if stage == "footer":
        child_environment["G_DEBUG"] = "fatal-criticals"
    identity = str(display) + "\n" + child_environment.get("NIRI_SOCKET", "")
    session = runtime / "typelay" / hashlib.sha256(identity.encode()).hexdigest()[:16]
    session.parent.mkdir(mode=0o700)
    session.mkdir(mode=0o700)
    socket_path = session / "native.sock"
    assert len(os.fsencode(socket_path)) < 108
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server:
        server.bind(str(socket_path))
        socket_path.chmod(0o600)
        server.listen(1)
        server.settimeout(5)
        fixture = subprocess.Popen(
            command, env=child_environment, stdout=sys.stdout, stderr=sys.stderr
        )
        key_held = False
        client = None
        acceptance_phases = {}
        click_evidence = {}
        publication_event_start = publication_event_end = 0
        try:
            connection, _ = server.accept()
            with connection:
                pid = peer_credentials(connection)[0]
                assert fixture.pid in process_lineage(pid), "unexpected native test peer"
                client = Native(connection)
                window = wait_for(
                    lambda: next(
                        (
                            item
                            for item in contents(request("Windows"), "Windows")
                            if item.get("pid") == pid
                        ),
                        None,
                    ),
                    f"{name}: fixture window missing",
                )
                request({"Action": {"FocusWindow": {"id": window["id"]}}})
                subprocess.run(
                    ["fcitx5-remote", "-s", "keyboard-us"],
                    env=child_environment,
                    check=True,
                    timeout=3,
                )

                def state():
                    assert fixture.poll() is None, f"{name}: fixture exited"
                    try:
                        return json.loads(state_path.read_text())
                    except FileNotFoundError:
                        return {}

                dimensions = None
                if stage in ("expand", "resume-editor", "language-options") or stage.startswith(
                    "language-retention-"
                ):
                    geometry = dict(
                        line.split("=", 1)
                        for line in subprocess.check_output(
                            ["xdotool", "getwindowgeometry", "--shell", nested],
                            text=True,
                            timeout=3,
                        ).splitlines()
                    )
                    dimensions = int(geometry["WIDTH"]), int(geometry["HEIGHT"])
                    click_evidence = {"clock": "CLOCK_BOOTTIME", "nestedSize": dimensions}
                initial = wait_for(
                    lambda: (value := state())
                    and value.get("focus") == "fixture-first"
                    and value.get("imeClear")
                    and (dimensions is None or parent_geometry_ready(value, dimensions))
                    and value,
                    f"{name}: actual source IME/parent geometry did not become ready",
                )
                generation = str(uuid.uuid4())
                configuration = {
                    "enabled": False,
                    "sessionEnabled": True,
                    "generation": generation,
                    "accept": "F1",
                    "exit": "F24",
                    "wake": {},
                    "direct": {},
                }
                client.call("configure-shortcuts", configuration)

                def capture():
                    described = client.call("describe")
                    if not described:
                        return None
                    return client.call(
                        "capture",
                        {"target": described["target"], "authority": described["authority"]},
                    )

                source = wait_for(capture, f"{name}: real source capture unavailable")
                assert source["text"] == initial["first"]
                assert source["sessionBindings"] == {
                    "generation": generation,
                    "accept": "F1",
                    "exit": "F24",
                    "sourceReady": True,
                    "editorReady": False,
                }
                assert source["authority"]["sessionBindings"] == source["sessionBindings"]
                if stage.startswith("language-retention-"):
                    check_language_retention_refusal(
                        client, source, generation, state, initial, nested, dimensions, stage
                    )
                    print(f"GTK read-only language preview: {stage} refused", flush=True)
                    return
                if stage == "language-options":
                    check_language_options(
                        client,
                        source,
                        generation,
                        state,
                        initial,
                        nested,
                        dimensions,
                        click_evidence,
                    )
                    print(
                        "GTK language options: real menus and Apply checked without a model",
                        flush=True,
                    )
                    return
                if stage == "preview-colors":
                    check_preview_colors(client, source, generation, state, initial, click_evidence)
                    print(
                        "GTK preview colors: current text and manual drafts retain normal contrast",
                        flush=True,
                    )
                    return
                if stage == "footer":
                    check_footer(client, source, generation, runtime, state, initial)
                    print(f"{name}: real footer notify hid the overlay safely", flush=True)
                    return

                if stage == "expand":
                    check_expand(
                        client,
                        source,
                        generation,
                        runtime,
                        state,
                        initial,
                        nested,
                        dimensions,
                        click_evidence,
                    )
                    print(f"{name}: old click did not acquire the newer view revision", flush=True)
                    return
                if stage.startswith("controller-"):
                    check_controller(client, source, generation, runtime, state, initial, stage)
                    print(f"{name}: detached actual consumer no longer acknowledged", flush=True)
                    return
                if stage == "resume-editor":
                    check_resume_editor(
                        client,
                        source,
                        generation,
                        state,
                        initial,
                        nested,
                        dimensions,
                        click_evidence,
                    )
                    print(
                        f"{name}: authorized recovery restored actual editor focus without writing",
                        flush=True,
                    )
                    return

                def publication(source, generation, replacement):
                    expected = snapshot(
                        source, client.hello, validated_geometry(source, client.hello["backend"])
                    )
                    expected["target"]["backend"] = "linux-" + client.hello["backend"]
                    write = plan(expected, replacement)
                    return write, {
                        "target": source["target"],
                        "authority": source["authority"],
                        "generation": generation,
                        "lease": {
                            "source": expected,
                            "escapeAllowed": True,
                            "commandGeneration": generation,
                            "sessionRevision": 1,
                        },
                        "permit": {"plan": write, "expiresAtMs": now_ms() + 100},
                    }

                (runtime / "arm").touch()
                write, published = publication(source, generation, replacement)
                acceptance_phases = {
                    "planId": write["id"],
                    "expiresAtMs": published["permit"]["expiresAtMs"],
                    "authorityObservedAtMs": source["authority"]["observedAtMs"],
                    "publishStartedAtMs": now_ms(),
                }
                publication_event_start = len(client.events)
                reply = client.call("publish", published)
                acceptance_phases["publishReceivedAtMs"] = now_ms()
                publication_event_end = len(client.events)
                acceptance_phases["published"] = reply == {"published": True}
                assert acceptance_phases["published"], name
                key_held = True
                acceptance_phases["keySendStartedAtMs"] = now_ms()
                send_accept(True)  # One down only; never retry or renew this permit.
                acceptance_phases["keySendFinishedAtMs"] = now_ms()
                reached_path = runtime / "reached.json"
                wait_for(reached_path.exists, f"{name}: mutation callback not reached", timeout=1)
                reached = json.loads(reached_path.read_text())
                (output / f"{name}-callback.json").write_text(json.dumps(reached))
                assert reached["stage"] == stage
                assert reached["text"] == ("" if stage == "delete" else replacement)
                at_callback = len(replacement) if stage == "selection" else 0
                assert (reached["start"], reached["end"]) == (at_callback, at_callback)
                assert reached["deletions"] == initial["firstEntry"]["deletions"] + 1
                assert reached["insertions"] == initial["firstEntry"]["insertions"] + (
                    0 if stage == "delete" else 1
                )
                next_generation = str(uuid.uuid4()) if change_generation else generation
                client.call("configure-shortcuts", {**configuration, "generation": next_generation})
                (runtime / "release").touch()
                # Wait for the original issued result. Do not ask for a new
                # observation while the callback may still be unwinding.
                receipt_deadline = time.monotonic() + 3
                while not any(event.get("event") == "accepted" for event in client.events):
                    assert time.monotonic() < receipt_deadline, f"{name}: issued receipt missing"
                    client.events.append(receive_sync(connection))
                send_accept(False)
                key_held = False
                assert not (runtime / "timeout").exists(), f"{name}: callback barrier timed out"
                accepted = [
                    event["data"] for event in client.events if event.get("event") == "accepted"
                ]
                assert len(accepted) == 1 and accepted[0]["planId"] == write["id"], accepted
                receipt = accepted[0]["receipt"]
                expected_status = "OutcomeUnknown" if change_generation else "Verified"
                assert receipt["status"] == expected_status, receipt
                expected_text = "" if change_generation and stage == "delete" else replacement
                expected_caret = (
                    0 if change_generation and stage in ("delete", "insert") else len(replacement)
                )
                final = wait_for(
                    lambda: (value := state())
                    and value["first"] == expected_text
                    and value["firstEntry"]["start"] == expected_caret
                    and value["firstEntry"]["end"] == expected_caret
                    and value,
                    f"{name}: real body/selection disagrees with the mutation boundary",
                )
                assert final["firstEntry"]["deletions"] == initial["firstEntry"]["deletions"] + 1
                assert final["firstEntry"]["insertions"] == initial["firstEntry"]["insertions"] + (
                    0 if change_generation and stage == "delete" else 1
                )
                if change_generation:
                    assert (
                        receipt["reason"] == "UnknownOutcome" and "verifiedSnapshot" not in receipt
                    )
                    assert reached["text"] == final["first"]
                    assert (reached["start"], reached["end"]) == (expected_caret, expected_caret)
                else:
                    assert receipt["verifiedSnapshot"]["text"] == final["first"]
                    assert receipt["verifiedSnapshot"]["selection"] == {
                        "start": expected_caret,
                        "end": expected_caret,
                    }
                fresh = wait_for(capture, f"{name}: safe post-write read unavailable")
                assert fresh["sessionBindings"]["generation"] == next_generation
                assert fresh["writeSupported"] is (not change_generation)
                if change_generation:
                    # A fresh, otherwise valid permit still cannot authorize a
                    # quarantined target. This does not retry the issued write.
                    _, blocked = publication(fresh, next_generation, "Must not write again.")
                    assert client.call("publish", blocked) == {"published": False}
                before_order = final["functionKeys"][7]["up"]
                key("F8")  # Downstream application observation orders final evidence.
                after = wait_for(
                    lambda: (value := state())
                    and value["functionKeys"][7]["up"] > before_order
                    and value,
                    f"{name}: downstream ordering key missing",
                )
                client.call("describe")
                assert after["first"] == final["first"]
                for field in ("start", "end", "insertions", "deletions"):
                    assert after["firstEntry"][field] == final["firstEntry"][field]
                assert (
                    len([event for event in client.events if event.get("event") == "accepted"]) == 1
                )
                print(
                    f"{name}: {expected_status}, actual body/selection and one issued receipt checked",
                    flush=True,
                )
        finally:
            (runtime / "release").touch()
            try:
                if key_held:
                    send_accept(False)
            finally:
                if acceptance_phases:
                    # Save after the attempted key, never spend its original
                    # deadline on diagnostic I/O. A refusal has no planId, so
                    # retain this request's public event interval, not later
                    # publications. Never save target or source/replacement.
                    try:
                        acceptance_phases["publicationEvents"] = [
                            {
                                "ready": event["data"].get("ready"),
                                "readyUntilHostMs": event["data"].get("readyUntilHostMs"),
                            }
                            for event in client.events[
                                publication_event_start:publication_event_end
                            ]
                            if event.get("event") == "publication"
                            and event["data"].get("target") == source["target"]
                            and event["data"].get("planId") in (None, acceptance_phases["planId"])
                        ][:8]
                        (output / f"{name}-acceptance-phases.json").write_text(
                            json.dumps(acceptance_phases, indent=2)
                        )
                    except (OSError, KeyError, TypeError, ValueError):
                        pass  # Preserve the original failure and fixture cleanup.
                if click_evidence:
                    if stage == "preview-colors":
                        click_evidence["finalObservationTiming"] = "after-check-not-render-time"
                    try:
                        click_evidence["finalGeometry"] = observe_click_geometry(state())
                    except (OSError, AssertionError, KeyError, TypeError, ValueError):
                        pass
                    try:
                        observed = click_evidence["uiEvents"] = []
                        for event in client.events if client else []:
                            data = event.get("data")
                            if event.get("event") != "ui" or not isinstance(data, dict):
                                continue
                            operation, revision = data.get("operation"), data.get("revision")
                            observed.append(
                                {
                                    "operation": operation
                                    if operation
                                    in ("beginEdit", "edit", "expand", "collapse", "return", "exit")
                                    else "other",
                                    "revision": revision if type(revision) is int else None,
                                    "viewRevisionMatches": revision
                                    == click_evidence.get("revision"),
                                    "targetMatches": data.get("target") == source["target"],
                                    "generationMatches": data.get("generation") == generation,
                                }
                            )
                            if len(observed) == 8:
                                break
                    except (KeyError, TypeError, UnboundLocalError):
                        pass
                    try:
                        (output / f"{name}-click.json").write_text(
                            json.dumps(click_evidence, indent=2)
                        )
                    except (OSError, TypeError, ValueError):
                        pass  # Evidence must not replace the original failure or cleanup.
                fixture.terminate()
                try:
                    fixture.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    fixture.kill()
                    fixture.wait(timeout=3)


def render_preview(
    client,
    source,
    generation,
    revision,
    label,
    *,
    view_state="ValidTranslation",
    can_edit=False,
    evidence=None,
    text="Read-only fixture preview.",
    theme="system",
    editor_owns_text=None,
    language_view=None,
    expires_at=None,
):
    expires = now_ms() + 800 if expires_at is None else expires_at
    if evidence is not None:
        evidence.update(revision=revision, expiresAtMs=expires, renderStartedAtMs=now_ms())
    result = client.call(
        "overlay-render",
        {
            "target": source["target"],
            "authority": source["authority"],
            "generation": generation,
            "revision": revision,
            "expiresAtMs": expires,
            "theme": theme,
            "view": {
                "state": view_state,
                "editable": can_edit,
                "text": text,
                "status": "fixture preview",
                "sourceLanguage": "en",
                "targetLanguage": "zh",
                **(language_view or {}),
                "presentation": {
                    "visible": True,
                    "editorOwnsText": editor_owns_text
                    if editor_owns_text is not None
                    else view_state in ("EditingTranslation", "ReturningToSource"),
                    "tabHint": "fixture read-only preview",
                    "dot": "Yellow",
                },
                "sessionShortcuts": {
                    "acceptHint": "fixture accept",
                    "passThroughHint": "fixture application key",
                    "exitHint": label,
                },
                "viewContext": {
                    "token": {
                        "target": {
                            "backend": "linux-" + client.hello["backend"],
                            "value": source["target"],
                            "lifetime": client.hello["lifetime"],
                        },
                        "commandGeneration": generation,
                        "revision": revision,
                    },
                    "bindings": {"accept": "F1", "exit": "F24"},
                    "canEdit": can_edit,
                },
            },
        },
    )

    if evidence is not None:
        evidence["responseVisible"] = result.get("visible")
        evidence["renderReturnedAtMs"] = now_ms()
        evidence["renderReturnedAtMonoMs"] = time.monotonic_ns() // 1_000_000
    return result


def check_preview_colors(client, source, generation, state, initial, evidence):
    # DTO-to-widget rendering only. Actual editing/return remains in desktop_e2e.
    normal, muted = "rgb(34,35,39)", "rgb(102,103,113)"
    cases = [
        ("ValidTranslation", "Existing translation.", False, normal, "light"),
        ("Translating", "Existing translation.", False, muted, "light"),
        ("ValidTranslation", "Updated translation.", False, normal, "light"),
        ("Translating", "", False, normal, "light"),
        ("Translating", "Manual draft.", True, normal, "light"),
        ("EditingTranslation", "Manual draft.", True, normal, "light"),
        ("ReturningToSource", "Manual draft.", True, normal, "light"),
        ("EditedTranslationPending", "Manual draft.", False, normal, "light"),
    ]
    short_text = "Renewed preview."
    long_text = "\n".join(["A longer translated line."] * 8)
    if client.hello["backend"] == "gtk4":
        # Exercise actual mapped widgets across renewal, theme changes and
        # grow/shrink requests. No layout measurement or native trace probe.
        cases.extend(
            [
                ("ValidTranslation", short_text, False, normal, "light"),
                ("ValidTranslation", short_text, False, normal, "light"),
                ("ValidTranslation", short_text, False, "rgb(244,244,246)", "dark"),
                ("Translating", short_text, False, "rgb(182,183,192)", "dark"),
                ("ValidTranslation", long_text, False, normal, "light"),
                ("ValidTranslation", short_text, False, normal, "light"),
            ]
        )
    short_height = None
    phases = evidence["previewColorPhases"] = []
    for revision, (view_state, text, owns_text, foreground, theme) in enumerate(cases, 1):
        phase = None
        if len(phases) < 8:
            phase = {"state": view_state, "editorOwnsText": owns_text, "theme": theme}
            phases.append(phase)
        assert render_preview(
            client,
            source,
            generation,
            revision,
            "fixture exit",
            view_state=view_state,
            text=text,
            theme=theme,
            editor_owns_text=owns_text,
            evidence=phase,
        )["visible"]
        rendered_at = time.monotonic_ns() // 1_000_000

        def rendered_state():
            value = state()
            candidate = value.get("candidate")
            if phase is not None:
                phase["lastObservation"] = {
                    "observedAtMs": value.get("observedAtMs"),
                    "sourceFocused": value.get("focus") == "fixture-first",
                    "imeClear": value.get("imeClear"),
                    "candidatePresent": bool(candidate),
                    "textMatches": bool(candidate and candidate.get("text") == text),
                    "foregroundMatches": bool(
                        candidate and candidate.get("foreground") == foreground
                    ),
                }
            if (
                value.get("observedAtMs", 0) <= rendered_at
                or not candidate
                or candidate["text"] != text
                or candidate["foreground"] != foreground
            ):
                return None
            if text == long_text and (short_height is None or candidate["height"] <= short_height):
                return None
            if text == short_text and theme == "light" and short_height is not None:
                if candidate["height"] != short_height:
                    return None
            return value

        actual = wait_for(
            rendered_state,
            f"{view_state}/{theme}: rendered candidate style/size was not observed",
        )
        assert actual["candidate"]["foreground"] == foreground, (view_state, actual["candidate"])
        if text == short_text and theme == "light":
            if short_height is None:
                short_height = actual["candidate"]["height"]
            else:
                assert actual["candidate"]["height"] == short_height, actual["candidate"]
        if text == long_text:
            assert short_height is not None and actual["candidate"]["height"] > short_height, (
                actual["candidate"]
            )
        assert actual["focus"] == "fixture-first"
        assert actual["first"] == initial["first"] and actual["second"] == initial["second"]


def check_language_retention_refusal(
    client, source, generation, state, initial, nested, dimensions, stage
):
    """Real detached focus refusals; expired-render replays the retired deadline.

    Expiry retirement may restore source Clear. Its replay checks the original
    request deadline, not a detached renewal after the owned UI has disappeared.
    A separate fresh source capture must still allow a new read-only preview.
    """
    assert client.hello["backend"] in ("gtk3", "gtk4")
    trigger, menu, control = (
        "typelay-menu-trigger",
        "typelay-language-menu",
        "typelay-source-language",
    )
    languages = {
        "sourceLanguage": "en",
        "targetLanguage": "ja",
        "languageChoices": {
            "source": [{"code": "en", "label": "English"}, {"code": "ja", "label": "日本語"}],
            "target": [{"code": "en", "label": "English"}, {"code": "ja", "label": "日本語"}],
        },
    }

    def click(name, field=False, actual=None):
        actual = state() if actual is None else actual
        item = actual[name] if field else actual["overlayControls"][name]
        assert item["width"] > 0 and item["height"] > 0
        point = click_point(
            actual, (item["x"] + item["width"] / 2, item["y"] + item["height"] / 2), dimensions
        )
        click_once(nested, point, {})

    def controls():
        return state().get("overlayControls", {})

    def detached_menu():
        actual = state()
        selected = actual.get("overlayControls", {}).get(control, {})
        return (
            language_control_focused(selected)
            and not actual["firstEntry"]["focused"]
            and (actual["firstEntry"].get("ime") or {}).get("flags") == 17  # Known | Detached.
            and not actual["imeClear"]
            and actual
        )

    initial_render = {}
    assert render_preview(
        client,
        source,
        generation,
        1,
        "fixture exit",
        language_view=languages,
        evidence=initial_render,
    )["visible"]
    # Mapping precedes native allocation. Use the existing waits to require a
    # fresh, in-bounds click target, then click that exact observed snapshot once.
    rendered = initial_render["renderReturnedAtMonoMs"]
    shown = wait_for(
        lambda: clickable_control(state(), trigger, rendered, dimensions),
        "language trigger geometry missing",
    )
    assert now_ms() <= initial_render["expiresAtMs"], "initial language lease expired before click"
    click(trigger, actual=shown)
    opened = time.monotonic_ns() // 1_000_000
    shown = wait_for(
        lambda: (value := clickable_control(state(), control, opened, dimensions))
        and value["overlayControls"].get(menu, {}).get("visible")
        and value,
        "language selector geometry missing",
    )
    assert now_ms() <= initial_render["expiresAtMs"], "initial language lease expired before click"
    click(control, actual=shown)
    wait_for(detached_menu, "source did not detach under exact language control focus")
    # The old implementation fails here. This must not alter source IME authority.
    retained_render = {}
    assert render_preview(
        client,
        source,
        generation,
        2,
        "fixture exit",
        language_view=languages,
        evidence=retained_render,
    )["visible"]
    wait_for(detached_menu, "read-only renewal changed source/menu focus")
    trigger_case = stage.startswith("language-retention-trigger-")
    if trigger_case:
        key("Escape")
        wait_for(
            lambda: controls().get(control, {}).get("focused")
            and not controls().get(control, {}).get("popupVisible"),
            "Escape did not return from child popup",
        )
        key("Escape")
        wait_for(
            lambda: controls().get(trigger, {}).get("focused")
            and not controls().get(menu, {}).get("visible"),
            "menu Escape did not hand focus to its trigger",
        )
        assert render_preview(
            client,
            source,
            generation,
            3,
            "fixture exit",
            language_view=languages,
            evidence=retained_render,
        )["visible"]
        assert not state()["imeClear"], "collapsed menu fabricated source Clear"
        key("F1")  # The configured source acceptance must not be minted by menu focus.
        client.call("describe")
        assert not any(event.get("event") == "accepted" for event in client.events)
        assert state()["first"] == initial["first"]
        stage = stage.replace("language-retention-trigger-", "language-retention-", 1)
    changed_source = json.loads(json.dumps(source))
    kwargs = {}
    expected_first = initial["first"]
    if stage == "language-retention-body":
        kwargs["text"] = "A different candidate cannot borrow the detached preview."
    elif stage == "language-retention-authority":
        changed_source["authority"]["inputRevision"] += 1
    elif stage == "language-retention-permissions":
        kwargs["can_edit"] = True
    elif stage == "language-retention-external-focus":
        click("secondEntry", field=True)
        wait_for(lambda: state().get("focus") == "fixture-second", "external field not focused")
    elif stage == "language-retention-expired-render":
        wait_for(lambda: state().get("candidate") is None, "original menu lease did not expire")
        assert now_ms() > retained_render["expiresAtMs"], "preview retired before its expiry"
        kwargs["expires_at"] = retained_render["expiresAtMs"]
    elif stage == "language-retention-source-input":
        click("firstEntry", field=True)
        wait_for(
            lambda: state().get("focus") == "fixture-first" and state().get("imeClear"),
            "source did not regain actual IME Clear",
        )
        key("End")
        key("x")
        expected_first += "x"
        after_input = wait_for(
            lambda: (actual := state()).get("first") == expected_first and actual,
            "real source input not delivered",
        )
        assert after_input.get("candidate") and after_input.get("overlayControls", {}).get(
            control, {}
        ).get("visible"), (
            "source input retired the preview before detached-retention refusal could be exercised"
        )
        click(control)
        wait_for(detached_menu, "language control did not regain focus after source input")
    else:
        raise AssertionError(f"unknown language retention stage: {stage}")
    result = render_preview(
        client,
        changed_source,
        generation,
        4 if trigger_case else 3,
        "fixture exit",
        language_view=languages,
        **kwargs,
    )
    assert result == {"visible": False, "editing": False}, (stage, result)
    final = wait_for(
        lambda: (actual := state()) and actual.get("candidate") is None and actual,
        "refused language renewal left a preview visible",
    )
    assert final["first"] == expected_first and final["second"] == initial["second"]
    assert not any(event.get("event") == "accepted" for event in client.events)
    if stage == "language-retention-expired-render":
        # This is a new explicit source interaction/capture, not resurrection of
        # the expired detached menu. Preserve normal fresh read-only admission.
        click("firstEntry", field=True)
        wait_for(
            lambda: state().get("focus") == "fixture-first" and state().get("imeClear"),
            "source did not establish fresh actual IME Clear",
        )
        described = client.call("describe")
        fresh = client.call(
            "capture", {"target": described["target"], "authority": described["authority"]}
        )
        assert fresh and fresh["text"] == initial["first"]
        assert render_preview(
            client, fresh, generation, 5, "fixture exit", language_view=languages
        ) == {
            "visible": True,
            "editing": False,
        }
        shown = wait_for(
            lambda: (actual := state()).get("candidate") and actual, "fresh source preview missing"
        )
        assert not shown["candidate"].get("editablePreview", False)
        assert shown["first"] == initial["first"] and shown["second"] == initial["second"]
        client.call("describe")
        assert not any(event.get("event") == "accepted" for event in client.events)


def language_trigger_point(actual, phase, dimensions, now):
    assert now <= phase["expiresAtMs"], "language trigger lease expired before click"
    name = "typelay-menu-trigger"
    if not clickable_control(actual, name, phase["renderReturnedAtMonoMs"], dimensions):
        return None
    control = actual["overlayControls"][name]
    if (
        not control.get("enabled")
        or control.get("labelLayoutCurrent") is not True
        or not control.get("labelRevision")
        or control.get("paintedLabelRevision") != control["labelRevision"]
    ):
        return None
    return click_point(
        actual,
        (control["x"] + control["width"] / 2, control["y"] + control["height"] / 2),
        dimensions,
    )


def check_language_options(
    client, source, generation, state, initial, nested, dimensions, evidence
):
    """Real menu widgets and UI events; synthetic view DTO, no installed model claim."""
    trigger = "typelay-menu-trigger"
    menu = "typelay-language-menu"
    source_control, target_control = "typelay-source-language", "typelay-target-language"
    apply_control = "typelay-apply-languages"
    choices = {
        "source": [
            {"code": "auto", "label": "自动检测"},
            {"code": "en", "label": "English"},
            {"code": "ja", "label": "日本語"},
            {"code": "ko", "label": "한국어"},
        ],
        "target": [
            {"code": "en", "label": "English"},
            {"code": "ja", "label": "日本語"},
            {"code": "ko", "label": "한국어"},
        ],
    }
    view = {"sourceLanguage": "en", "targetLanguage": "ja", "languageChoices": choices}
    revision = 0
    phases = evidence["languagePhases"] = []

    def observe(value):
        # Retain bounded existing render/input observations, never source or
        # candidate text, labels, extra observation requests, or sleeps.
        return {
            "observedAtMonoMs": value.get("observedAtMs"),
            "focus": value.get("focus"),
            "controls": {
                name: {
                    key: control[key]
                    for key in (
                        "instance",
                        "modelInstance",
                        "visible",
                        "enabled",
                        "focused",
                        "buttonFocused",
                        "labelRevision",
                        "paintedLabelRevision",
                        "labelLayoutCurrent",
                        "selected",
                        "popupVisible",
                        "popupActive",
                        "popupShown",
                        "popupFocused",
                        "x",
                        "y",
                        "width",
                        "height",
                    )
                    if key in control
                }
                for name, control in value.get("overlayControls", {}).items()
                if name in (trigger, menu, source_control, target_control, apply_control)
            },
        }

    def render(stage=None):
        nonlocal revision
        revision += 1
        phase = {
            "stage": stage
            or (
                (
                    "initial",
                    "menu-renew",
                    "choice-renew",
                    "draft-renew",
                    "saved-update",
                    "second-choice",
                )[revision - 1]
                if revision <= 6
                else "language-renew"
            )
        }
        phases.append(phase)
        # Keep initial allocation evidence plus the six most recent phases.
        # Later failures must not lose their actual render/click boundary.
        if len(phases) > 8:
            del phases[2]
        result = render_preview(
            client,
            source,
            generation,
            revision,
            "fixture exit",
            language_view=view,
            evidence=phase,
        )
        if phase is not None:
            phase["renderVisible"] = result["visible"]
        assert result["visible"]
        rendered = time.monotonic_ns() // 1_000_000

        def shown():
            value = state()
            phase["lastObservation"] = observe(value)
            fresh = value.get("observedAtMs", 0) > rendered
            if fresh and phase is not None and "firstFreshObservation" not in phase:
                phase["firstFreshObservation"] = observe(value)
            if fresh and value.get("overlayControls", {}).get(trigger, {}).get("visible"):
                if phase is not None:
                    phase["shownObservation"] = observe(value)
                return value["overlayControls"]
            return None

        return wait_for(shown, "language controls did not render")

    def controls():
        actual = state()
        if phases:
            phases[-1]["lastObservation"] = observe(actual)
        return actual.get("overlayControls", {})

    def click(name):
        if name == trigger and client.hello["backend"] == "gtk3":

            def laid_out():
                actual = state()
                phases[-1]["lastObservation"] = observe(actual)
                point = language_trigger_point(actual, phases[-1], dimensions, now_ms())
                return (actual, point) if point else None

            actual, point = wait_for(laid_out, "language trigger label layout did not finish")
        else:
            actual = state()
            control = actual["overlayControls"][name]
            assert control["visible"] and control["width"] > 0 and control["height"] > 0
            point = click_point(
                actual,
                (control["x"] + control["width"] / 2, control["y"] + control["height"] / 2),
                dimensions,
            )
        phase = phases[-1]
        if phase:
            phase.update(control=name, point=point, beforeClick=observe(actual))
        if phase:
            phase["clickStartedAtMonoMs"] = time.monotonic_ns() // 1_000_000
        if name == trigger and client.hello["backend"] == "gtk3":
            assert now_ms() <= phase["expiresAtMs"], "language trigger lease expired before click"
        click_once(nested, point, phase)
        if phase:
            phase["clickFinishedAtMonoMs"] = time.monotonic_ns() // 1_000_000

    def toggle(opened, renew=True):
        if renew:
            render()
        phases[-1]["requestedMenuVisible"] = opened
        click(trigger)
        wait_for(
            lambda: controls().get(menu, {}).get("visible") is opened,
            "language menu did not open/close",
        )

    def select(name, index):
        render()
        click(name)
        if revision in (3, 6):
            # Read the existing report once, without waiting for a fresh frame.
            # Its timestamp distinguishes a stale sample from post-click focus.
            try:
                phases[-1]["afterClickBeforeKeys"] = observe(state())
            except (OSError, ValueError, AssertionError):
                phases[-1]["afterClickObservationUnavailable"] = True
            phases[-1]["keysStartedAtMonoMs"] = time.monotonic_ns() // 1_000_000
        if client.hello["backend"] == "gtk4":
            wait_for(
                lambda: controls().get(name, {}).get("popupFocused"),
                "opened language popup did not acquire real list focus",
            )
        key("Home")
        for _ in range(index):
            key("Down")
        key("Return")
        phases[-1]["keysReturnedAtMs"] = now_ms()

        def selected():
            actual = state()
            phases[-1]["lastObservation"] = observe(actual)
            value = actual.get("overlayControls", {})
            if "afterKeys" not in phases[-1]:
                phases[-1]["afterKeys"] = observe(actual)
            control = value.get(name, {})
            if control.get("selected") != index:
                return None
            # Return selects a row before GTK finishes closing its child popup.
            # Do not start another click on a still-closing surface. This is the
            # same action's existing bounded wait, not another key or a retry.
            if client.hello["backend"] in ("gtk3", "gtk4") and (
                control.get("popupVisible") is not False or control.get("buttonFocused") is not True
            ):
                return None
            if client.hello["backend"] == "gtk3" and control.get("popupShown") is not False:
                return None
            return value

        return wait_for(
            selected,
            "actual dropdown selection did not change",
        )

    def events():
        client.call("describe")
        return [
            event["data"]
            for event in client.events
            if event.get("event") == "ui" and event.get("data", {}).get("operation") == "languages"
        ]

    actual = render()
    assert actual[trigger]["text"] == "English → 日本語"
    toggle(True)
    actual = controls()
    assert actual[source_control]["items"] == ["自动检测", "English", "日本語", "한국어"]
    assert actual[target_control]["items"] == ["English", "日本語", "한국어"]
    assert actual[source_control]["selected"] == 1 and actual[target_control]["selected"] == 1
    selected = select(source_control, 3)
    assert selected[source_control]["focused"], "dropdown did not acquire real focus"
    before_events = events()
    renewed = render()
    for name in (source_control, target_control):
        for field in ("instance", "modelInstance", "selected", "focused"):
            assert renewed[name][field] == selected[name][field], (name, field)
    assert events() == before_events, "unapplied selection was sent as a save"
    view["sourceLanguage"] = "ja"
    actual = render()
    assert actual[source_control]["selected"] == 2, "confirmed saved code did not replace draft"
    select(source_control, 3)
    toggle(False)
    if client.hello["backend"] in ("gtk3", "gtk4"):
        wait_for(lambda: controls()[trigger]["focused"], "closing menu lost real language focus")
        assert render()[trigger]["focused"], "collapsed renewal lost owned trigger focus"
    toggle(True, renew=False)
    if client.hello["backend"] in ("gtk3", "gtk4"):
        wait_for(
            lambda: controls()[source_control]["focused"],
            "reopening did not restore language navigation",
        )
    assert controls()[source_control]["selected"] == 2, "reopening retained an unapplied choice"
    select(source_control, 3)
    select(target_control, 0)
    render()
    click(apply_control)
    wait_for(lambda: not controls().get(menu, {}).get("visible"), "Apply did not close menu")
    if client.hello["backend"] in ("gtk3", "gtk4"):
        wait_for(lambda: controls()[trigger]["focused"], "Apply lost owned language focus")
    emitted = events()
    assert len(emitted) == len(before_events) + 1
    assert emitted[-1]["value"] == {"source": "ko", "target": "en"}
    assert emitted[-1]["generation"] == generation and emitted[-1]["revision"] == revision
    # The controlled host has not confirmed the requested save. Reopening must
    # show its last confirmed state, including after a real host save failure.
    toggle(True, renew=False)
    assert controls()[source_control]["selected"] == 2
    assert controls()[target_control]["selected"] == 1
    view.update(sourceLanguage="ko", targetLanguage="en")
    actual = render()
    assert actual[source_control]["selected"] == 3 and actual[target_control]["selected"] == 0

    view.update(
        sourceLanguage="ja",
        targetLanguage="ko",
        languageChoices=choices,
        sourceLanguageLabel="Saved Japanese",
        targetLanguageLabel="Saved Korean",
    )
    assert render()[trigger]["text"] == "Saved Japanese → Saved Korean"
    view["languageChoices"] = {}
    unavailable = render()
    assert unavailable[trigger]["text"] == "Saved Japanese → Saved Korean"
    if client.hello["backend"] in ("gtk3", "gtk4"):
        assert unavailable[trigger]["focused"], "disabling choices lost owned language focus"
        assert not unavailable[menu]["visible"]
        assert not unavailable[source_control]["enabled"]
        assert not unavailable[target_control]["enabled"]
    view["languageChoices"] = choices
    view.pop("sourceLanguageLabel")
    view.pop("targetLanguageLabel")
    restored = render()
    assert restored[trigger]["text"] == "日本語 → 한국어"
    if client.hello["backend"] in ("gtk3", "gtk4"):
        assert restored[trigger]["focused"], "restored choices lost owned trigger focus"
        toggle(True, renew=False)
        assert controls()[source_control]["focused"]
    view["sourceLanguage"] = "zz"
    actual = render()
    assert actual[trigger]["text"] == "zz（当前不可用） → 한국어"
    assert actual[source_control]["items"][actual[source_control]["selected"]] == "zz（当前不可用）"
    assert not actual[apply_control]["enabled"]
    assert select(source_control, 2)[apply_control]["enabled"]
    toggle(False)
    toggle(True, renew=False)
    assert not controls()[apply_control]["enabled"], "unavailable confirmed choice was lost"

    if client.hello["backend"] in ("gtk3", "gtk4"):
        before_popup_events = events()
        render()
        click(source_control)
        wait_for(lambda: controls()[source_control]["popupVisible"], "language popup did not open")
        key("Home")
        key("Escape")
        wait_for(
            lambda: (value := controls())[source_control]["focused"]
            and not value[source_control]["popupVisible"],
            "Escape did not close the language popup and return focus",
        )
        assert events() == before_popup_events, "cancelled popup saved a language"
        toggle(False)
        toggle(True, renew=False)
        assert not controls()[apply_control]["enabled"], "cancel/reopen lost the saved unknown code"
        render()
        click(source_control)
        wait_for(
            lambda: controls()[source_control]["popupVisible"], "language popup did not reopen"
        )
        click(trigger)
        wait_for(
            lambda: not controls()[source_control]["popupVisible"]
            and not controls()[menu]["visible"],
            "outside menu click left the language popup open",
        )
        toggle(True)
        # A full 32-choice list must remain scrollable and keyboard reachable.
        view["languageChoices"] = {
            **choices,
            "source": choices["source"]
            + [{"code": f"lang-{index}", "label": f"Language {index}"} for index in range(28)],
        }
        view["sourceLanguage"] = "ja"
        render()
        click(source_control)
        key("End")
        key("Return")
        wait_for(
            lambda: (value := controls())[source_control]["selected"] == 31
            and value[source_control]["focused"]
            and not value[source_control]["popupVisible"],
            "last language in a scrolling popup was not keyboard reachable",
        )
        assert events() == before_popup_events, "unapplied scrolling choice was saved"

    before_events = events()
    view.update(sourceLanguage="ja", targetLanguage="ko")
    for invalid in (None, {**choices, "target": choices["target"] * 2}):
        view["languageChoices"] = invalid
        actual = render()
        for name in (source_control, target_control, apply_control):
            assert not actual[name]["enabled"], name
    view.pop("languageChoices")
    actual = render()
    assert actual[trigger]["text"] == "ja（当前不可用） → ko（当前不可用）"
    # Invalidating the focused choices safely closes the menu onto its trigger.
    # Reopen it through the public button before testing the disabled action.
    assert not actual[menu]["visible"] and actual[trigger]["focused"]
    toggle(True, renew=False)
    actual = controls()
    for name in (source_control, target_control, apply_control):
        assert actual[name]["visible"] and not actual[name]["enabled"], name
    click(apply_control)
    assert events() == before_events, "invalid choices emitted a language change"
    if client.hello["backend"] in ("gtk3", "gtk4"):
        # With both selectors disabled, the trigger still owns focus. Close
        # before renewing; an open-menu trigger is not a retained focus proof.
        toggle(False, renew=False)
        view["languageChoices"] = choices
        render("restore-valid-choices")
        toggle(True, renew=False)
        click(source_control)
        wait_for(
            lambda: controls()[source_control]["popupVisible"], "language popup did not reopen"
        )
        bounds = source["geometry"]["bounds"]
        point = click_point(
            state(),
            (bounds["x"] + bounds["width"] / 4, bounds["y"] + bounds["height"] / 2),
            dimensions,
        )
        click_once(nested, point, {})
        wait_for(
            lambda: state().get("focus") == "fixture-first"
            and not controls().get(source_control, {}).get("popupVisible", False),
            "outside source click did not close the popup without stealing focus",
        )
        assert events() == before_events, "outside click saved a language"
    assert state()["first"] == initial["first"] and state()["second"] == initial["second"]


def check_footer(client, source, generation, runtime, state, initial):
    assert render_preview(client, source, generation, 1, "fixture-initial-exit") == {
        "visible": True,
        "editing": False,
    }
    wait_for(
        lambda: (runtime / "footer-ready").exists() and state().get("candidate"),
        "fixture did not observe the real footer before its next update",
    )
    (runtime / "arm").touch()
    assert render_preview(client, source, generation, 2, "fixture-updated-exit") == {
        "visible": False,
        "editing": False,
    }
    assert (runtime / "footer-fired").exists()
    final = wait_for(
        lambda: (value := state())
        and value.get("focus") == "fixture-second"
        and value.get("candidate") is None
        and value,
        "footer notify did not hide the actual overlay and focus the second field",
    )
    assert final["first"] == initial["first"] and final["second"] == initial["second"]
    for field in ("firstEntry", "secondEntry"):
        for counter in ("insertions", "deletions"):
            assert final[field][counter] == initial[field][counter]
    described = client.describe()
    assert described["metadata"]["automationId"] == "fixture-second"
    assert not any(event.get("event") == "accepted" for event in client.events)


def expand_click_point(actual, evidence, dimensions, now):
    assert now <= evidence["expiresAtMs"], "expand lease expired before click"
    name = "fixture-expand"
    if not clickable_control(actual, name, evidence["renderReturnedAtMonoMs"], dimensions):
        return None
    control = actual["overlayControls"][name]
    if not control.get("enabled") or control.get("scale") != actual["scale"]:
        return None
    return click_point(
        actual,
        (control["x"] + control["width"] / 2, control["y"] + control["height"] / 2),
        dimensions,
    )


def expand_hover_current(actual, evidence, dimensions, now, instance, point, moved_at):
    current_point = expand_click_point(actual, evidence, dimensions, now)
    if current_point != point or actual.get("observedAtMs", 0) < moved_at:
        return False
    control = actual["overlayControls"]["fixture-expand"]
    return (
        control.get("instance") == instance
        and control.get("pointerInside") is True
        and control.get("pointerEnteredAtMs", 0) > 0
    )


def check_expand(client, source, generation, runtime, state, initial, nested, dimensions, evidence):
    assert render_preview(client, source, generation, 1, "fixture exit", evidence=evidence)[
        "visible"
    ]
    ready = runtime / "expand-ready"

    def allocated_button():
        actual = state()
        point = expand_click_point(actual, evidence, dimensions, now_ms())
        return (actual, point) if ready.exists() and actual.get("candidate") and point else None

    actual, point = wait_for(allocated_button, "real expand button/current geometry missing")
    evidence["beforeClick"] = observe_click_geometry(actual)
    evidence["buttonBoundsCurrent"] = {
        key: actual["overlayControls"]["fixture-expand"][key]
        for key in ("instance", "x", "y", "width", "height", "scale")
    }
    if "eventWindow" in actual["overlayControls"]["fixture-expand"]:
        evidence["buttonBoundsCurrent"]["eventWindow"] = actual["overlayControls"][
            "fixture-expand"
        ]["eventWindow"]
    evidence["pixelPoint"] = point
    assert now_ms() <= evidence["expiresAtMs"], "expand lease expired before click"
    if client.hello["backend"] in ("gtk3", "gtk4"):
        instance = actual["overlayControls"]["fixture-expand"]["instance"]
        moved_at = time.monotonic_ns() // 1_000_000
        evidence["moveStartedAtMonoMs"] = moved_at
        subprocess.run(
            ["xdotool", "mousemove", "--window", nested, str(point[0]), str(point[1])],
            check=True,
            timeout=3,
        )

        moved_at = time.monotonic_ns() // 1_000_000
        evidence["moveFinishedAtMonoMs"] = moved_at

        def hovered_button():
            current = state()
            evidence["hoverObservation"] = current.get("overlayControls", {}).get("fixture-expand")
            return (
                current
                if expand_hover_current(
                    current, evidence, dimensions, now_ms(), instance, point, moved_at
                )
                else None
            )

        actual = wait_for(hovered_button, "real expand button did not receive pointer hover")
        evidence["beforeClick"] = observe_click_geometry(actual)
        assert now_ms() <= evidence["expiresAtMs"], "expand lease expired before click"
        (runtime / "arm").touch()
        evidence["clickStartedAtMs"] = now_ms()
        try:
            subprocess.run(["xdotool", "click", "1"], check=True, timeout=3)
            evidence["clickCommandSucceeded"] = True
        finally:
            evidence["clickFinishedAtMs"] = now_ms()
    else:
        (runtime / "arm").touch()
        click_once(nested, point, evidence)
    try:
        evidence["afterClick"] = observe_click_geometry(state())
    except (OSError, AssertionError, KeyError, TypeError, ValueError):
        pass  # A diagnostic read cannot replace the original interaction result.
    wait_for((runtime / "reached.json").exists, "expand notify was not reached", timeout=1)
    # Retained editor ownership legitimately skips a new IME RPC while the
    # source observation is unchanged. Same generation, newer transport view.
    assert render_preview(
        client, source, generation, 2, "fixture exit", view_state="ReturningToSource"
    )["visible"]
    (runtime / "release").touch()
    wait_for((runtime / "expand-returned").exists, "expand notify did not return")
    assert not (runtime / "timeout").exists()
    client.describe()
    assert not any(
        event.get("event") == "ui" and event["data"].get("operation") == "expand"
        for event in client.events
    ), "old click was relabelled with the replacement view's transport revision"
    assert state()["first"] == initial["first"]
    assert state()["second"] == initial["second"]


def check_resume_editor(client, source, generation, state, initial, nested, dimensions, evidence):
    assert render_preview(client, source, generation, 1, "fixture exit")["visible"]
    described = client.describe()
    assert described["target"] == source["target"] and described["sessionBindings"]["editorReady"]
    source = client.call(
        "capture", {"target": described["target"], "authority": described["authority"]}
    )
    assert source["sessionBindings"] == described["sessionBindings"]
    assert render_preview(
        client, source, generation, 2, "fixture exit", can_edit=True, evidence=evidence
    )["visible"]
    actual = wait_for(
        lambda: (value := state()).get("candidate")
        and value["observedAtMs"] > evidence["renderReturnedAtMonoMs"]
        and value,
        "real current preview missing",
    )
    candidate = actual["candidate"]
    evidence["beforeClick"] = observe_click_geometry(actual)
    assert candidate["width"] > 0 and candidate["height"] > 0
    point = click_point(
        actual,
        (
            candidate["x"] + candidate["width"] / 2,
            candidate["y"] + candidate["height"] / 2,
        ),
        dimensions,
    )
    evidence["pixelPoint"] = point
    click_once(nested, point, evidence)
    try:
        evidence["afterClick"] = observe_click_geometry(state())
    except (OSError, AssertionError, KeyError, TypeError, ValueError):
        pass  # A diagnostic read cannot replace the original interaction result.

    def edit_requested():
        client.describe(source["target"])
        return any(
            event.get("event") == "ui"
            and event["data"].get("operation") == "beginEdit"
            and event["data"].get("generation") == generation
            and event["data"].get("revision") == 2
            for event in client.events
        )

    wait_for(edit_requested, "actual preview click did not request editor authorization")
    assert render_preview(
        client,
        source,
        generation,
        3,
        "fixture exit",
        view_state="EditingTranslation",
        can_edit=True,
    )["editing"]
    command = {"target": source["target"], "generation": generation, "revision": 3}
    assert client.call("overlay-edit", {**command, "allowed": True})["editing"]
    wait_for(
        lambda: state().get("focus") == "typelay-candidate" and state().get("focusedImeClear"),
        "authorized editor did not acquire actual focus",
    )
    assert render_preview(
        client, source, generation, 4, "fixture exit", view_state="ReturningToSource", can_edit=True
    )["visible"]
    assert client.call(
        "return",
        {"target": source["target"], "authority": source["authority"], "generation": generation},
    ) == {"returned": True}
    wait_for(
        lambda: state().get("focus") == "fixture-first" and state().get("imeClear"),
        "native return did not actually focus the source",
    )
    # The host can cancel after focus succeeds but its final confirmation fails.
    # Exercise its resume-before-render order through the real native protocol;
    # do not fake provider facts or let a later render repair the focus first.
    assert not client.call("overlay-resume", command)["editing"], (
        "old view revision resumed editing"
    )
    assert not client.call(
        "overlay-resume", {**command, "revision": 4, "generation": str(uuid.uuid4())}
    )["editing"], "foreign generation resumed editing"
    assert client.call("overlay-resume", {**command, "revision": 4})["editing"]
    final = wait_for(
        lambda: (value := state()).get("focus") == "typelay-candidate" and value,
        "recovery did not restore actual editor focus before the next render",
    )
    assert final["candidate"]["text"] == "Read-only fixture preview."
    assert final["first"] == initial["first"] and final["second"] == initial["second"]
    for field in ("firstEntry", "secondEntry"):
        for counter in ("insertions", "deletions"):
            assert final[field][counter] == initial[field][counter]
    client.describe(source["target"])
    assert not any(event.get("event") == "accepted" for event in client.events)


def check_controller(client, source, generation, runtime, state, initial, stage):
    editor = stage == "controller-editor"
    if editor:
        assert render_preview(client, source, generation, 1, "fixture exit")["visible"]
        wait_for(lambda: state().get("candidate"), "actual GTK4 editor did not appear")
    before = client.describe()["sessionBindings"]
    assert before["sourceReady"] and before["editorReady"] is editor
    (runtime / "arm").touch()
    removed = runtime / "controller-removed"
    wait_for(removed.exists, "fixture did not remove the real GTK4 controller")
    assert removed.read_text() == "1"
    described = client.describe()
    assert described["sessionBindings"] == {
        **before,
        "sourceReady": editor,
        "editorReady": False,
    }, "detached controller was acknowledged as an installed session consumer"
    captured = client.call(
        "capture", {"target": described["target"], "authority": described["authority"]}
    )
    assert captured and captured["writeSupported"] is True
    assert captured["text"] == initial["first"]
    assert captured["sessionBindings"] == described["sessionBindings"]
    assert state()["second"] == initial["second"]
