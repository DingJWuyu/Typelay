"""Real GTK callback reentry through the public native socket, without a model."""

import asyncio
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
from types import SimpleNamespace
from unittest.mock import patch

from geometry import anchor_geometry
from native_e2e import (
    Native,
    glossary_hover_current,
    glossary_painted_geometry,
    key,
    plan,
    snapshot,
)
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


def run_case(
    command,
    environment,
    output,
    send_accept,
    runtime,
    stage,
    change_generation,
    nested,
    *,
    language_configuration=False,
):
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
    if len(command) == 1:
        # The direct developer fixture has no production package launcher to
        # supply its private font root. Require the same complete offline bundle
        # as Qt; never silently qualify a five-font source-tree fallback.
        root = Path(__file__).resolve().parents[2]
        sys.path.insert(0, str(root))
        from eng.fonts.prepare import verify_bundle

        font_root = root / "artifacts/ui-fonts"
        verify_bundle(font_root)
        child_environment["TYPELAY_UI_FONT_ROOT"] = str(font_root)
    child_environment.pop("TYPELAY_FIXTURE_TWO_WINDOWS", None)

    if stage == "footer":
        child_environment["G_DEBUG"] = "fatal-criticals"
    identity = str(display) + "\n" + child_environment.get("NIRI_SOCKET", "")
    session = runtime / "typelay" / hashlib.sha256(identity.encode()).hexdigest()[:16]
    session.parent.mkdir(mode=0o700)
    session.mkdir(mode=0o700)
    socket_path = session / "native.sock"
    assert len(os.fsencode(socket_path)) < 108
    if language_configuration and stage.startswith(
        ("language-retention-", "language-configuration-")
    ):
        run_owned_language_options(
            command,
            child_environment,
            socket_path,
            state_path,
            output,
            nested,
            configuration_stage=stage,
        )
        return
    if stage == "language-options":
        with tempfile.TemporaryDirectory(prefix="tl-language-dto-") as directory:
            run_case(
                command,
                environment,
                output,
                send_accept,
                Path(directory),
                "language-options-dto",
                False,
                nested,
            )
        run_owned_language_options(
            command,
            child_environment,
            socket_path,
            state_path,
            output,
            nested,
            configuration_stage=stage if language_configuration else None,
        )
        return
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
                        client,
                        source,
                        generation,
                        state,
                        initial,
                        nested,
                        dimensions,
                        stage,
                        click_evidence,
                    )
                    print(f"GTK read-only language preview: {stage} refused", flush=True)
                    return
                if stage == "language-options-dto":
                    check_language_dto(client, source, generation, state, initial, click_evidence)
                    print("GTK language DTO labels and invalid catalogs checked", flush=True)
                    return
                if stage == "preview-colors":
                    check_preview_colors(client, source, generation, state, initial, click_evidence)
                    check_preview_appearance(
                        client, source, generation, state, initial, click_evidence
                    )
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
    appearance=None,
    editor_owns_text=None,
    language_view=None,
    expires_at=None,
    token_revision=None,
    status="fixture preview",
):
    token_revision = revision if token_revision is None else token_revision
    expires = now_ms() + 800 if expires_at is None else expires_at
    if evidence is not None:
        evidence.update(
            revision=revision,
            tokenRevision=token_revision,
            expiresAtMs=expires,
            renderStartedAtMs=now_ms(),
        )
    result = client.call(
        "overlay-render",
        {
            "target": source["target"],
            "authority": source["authority"],
            "generation": generation,
            "revision": revision,
            "expiresAtMs": expires,
            "theme": theme,
            "appearance": {"uiLanguage": "zh-CN", **(appearance or {})},
            "view": {
                "state": view_state,
                "editable": can_edit,
                "text": text,
                "status": status,
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
                        "revision": token_revision,
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
    normal, muted = "rgb(20,20,19)", "rgb(116,114,108)"
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
                ("ValidTranslation", short_text, False, "rgb(244,243,238)", "dark"),
                ("Translating", short_text, False, "rgb(154,152,143)", "dark"),
                ("ValidTranslation", long_text, False, normal, "light"),
                ("ValidTranslation", short_text, False, normal, "light"),
            ]
        )
    short_height = None
    phases = evidence["previewColorPhases"] = []
    for revision, (view_state, text, owns_text, foreground, theme) in enumerate(cases, 1):
        phase = {
            "revision": revision,
            "state": view_state,
            "editorOwnsText": owns_text,
            "theme": theme,
            "lengthCase": "long"
            if text == long_text
            else "short"
            if text == short_text
            else "state",
        }
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
                    "candidateHeight": candidate.get("height") if candidate else None,
                    "baselineShortHeight": short_height,
                    "fontFamily": candidate.get("fontFamily") if candidate else None,
                    "fontPixels": candidate.get("fontPixels") if candidate else None,
                    "layoutGeometry": candidate.get("layoutGeometry") if candidate else None,
                    "naturalFrames": candidate.get("naturalFrames") if candidate else None,
                }
                if candidate and value.get("observedAtMs", 0) > rendered_at:
                    phase.setdefault("firstLiveObservation", phase["lastObservation"])
                    phase["lastLiveObservation"] = phase["lastObservation"]
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
        assert actual["candidate"]["editablePreview"] is False
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


def check_preview_appearance(client, source, generation, state, initial, evidence):
    """Persisted appearance is observed on real widgets without gaining edit authority."""
    font_lock = json.loads(
        (Path(__file__).resolve().parents[2] / "eng/fonts/fonts.lock.json").read_text()
    )
    files = {item["path"]: item for item in font_lock["files"]}
    cases = (
        ("claude", 13, "clay", "zh-CN", "TypeLay Serif 13", "rgb(217,119,87)", "应用语言方向"),
        ("sans", 19, "ocean", "en", "Inter", "rgb(47,127,224)", "Apply languages"),
        ("system", 15, "sage", "en", "sans", "rgb(93,154,110)", "Apply languages"),
        ("claude", 17, "iris", "zh-CN", "TypeLay Serif 17", "rgb(138,108,216)", "应用语言方向"),
        ("sans", 15, "graphite", "zh-CN", "Inter", "rgb(119,117,111)", "应用语言方向"),
    )
    phases = evidence["appearancePhases"] = []
    for revision, (font, size, accent, language, family, color, label) in enumerate(cases, 30):
        phase = {
            "font": font,
            "textSize": size,
            "accent": accent,
            "uiLanguage": language,
            "reduceMotion": True,
            "reduceTransparency": True,
        }
        assert render_preview(
            client,
            source,
            generation,
            revision,
            "fixture exit",
            theme="light",
            appearance=phase,
            text="Appearance stays read-only.",
        )["visible"]
        phases.append(phase)
        after = time.monotonic_ns() // 1_000_000

        def rendered():
            value = state()
            candidate = value.get("candidate") or {}
            controls = value.get("overlayControls", {})
            if candidate:
                phase["lastObservation"] = {
                    "fontPixels": candidate.get("fontPixels"),
                    "fontFamily": candidate.get("fontFamily"),
                    "foreground": controls.get("typelay-dot", {}).get("foreground"),
                    "applyText": controls.get("typelay-apply-languages", {}).get("text"),
                    "fontRuns": candidate.get("fontRuns"),
                    "hostFontRuns": candidate.get("hostFontRuns"),
                    "usesHostFontMap": candidate.get("usesHostFontMap"),
                }
            if (
                value.get("observedAtMs", 0) <= after
                or candidate.get("fontPixels") != size
                or font != "system"
                and family.lower() not in candidate.get("fontFamily", "").lower()
                or controls.get("typelay-dot", {}).get("foreground") != color
                or controls.get("typelay-apply-languages", {}).get("text") != label
            ):
                return None
            if font == "claude":
                # GTK exposes shaping through the live widget's Pango context,
                # not GtkTextView's private internal runs or a pixel capture.
                observation = candidate.get("fontRuns") or {}
                runs = observation.get("runs") or []
                expected = files[f"source-serif/TypeLaySerif-{size}-Regular.ttf"]["sha256"]
                if (
                    observation.get("observation") != "live-widget-context-shaping"
                    or not runs
                    or any(
                        run.get("missingGlyphs") != 0
                        or (run.get("font") or {}).get("checksum") != expected
                        for run in runs
                    )
                ):
                    return None
            if font == "system":
                runs = (candidate.get("fontRuns") or {}).get("runs") or []
                host_runs = (candidate.get("hostFontRuns") or {}).get("runs") or []
                if (
                    candidate.get("usesHostFontMap") is not True
                    or not runs
                    or not host_runs
                    or any(run.get("missingGlyphs") != 0 for run in runs)
                    or any(not (run.get("font") or {}).get("checksum") for run in runs + host_runs)
                    or {(run.get("font") or {}).get("checksum") for run in runs}
                    != {(run.get("font") or {}).get("checksum") for run in host_runs}
                ):
                    return None
            return value

        actual = wait_for(
            rendered, f"{font}/{size}/{accent}/{language}: actual appearance was not painted"
        )
        assert not actual["candidate"]["editable"] and not actual["candidate"]["focused"]
        assert actual["focus"] == "fixture-first"
        assert actual["first"] == initial["first"] and actual["second"] == initial["second"]
        phase.update(fontFamily=actual["candidate"]["fontFamily"], fontPixels=size)

    # Family strings are requests, not evidence that the offline face shaped
    # the text. Compare Pango's observed font identity with the locked bytes.
    for revision, (font, target, sample, file) in enumerate(
        (
            ("claude", "zh-CN", "汉字翻译", "noto-serif/NotoSerifSC-Regular.otf"),
            ("sans", "zh-CN", "汉字翻译", "noto-sans/NotoSansSC-Regular.otf"),
            ("claude", "ja", "日本語翻訳", "noto-serif/NotoSerifJP-Regular.otf"),
            ("sans", "ja", "日本語翻訳", "noto-sans/NotoSansJP-Regular.otf"),
            ("claude", "ko", "한국어번역", "noto-serif/NotoSerifKR-Regular.otf"),
            ("sans", "ko", "한국어번역", "noto-sans/NotoSansKR-Regular.otf"),
        ),
        40,
    ):
        phase = {"font": font, "targetLanguage": target, "fontFile": file}
        phases.append(phase)
        assert render_preview(
            client,
            source,
            generation,
            revision,
            "fixture exit",
            appearance={"font": font, "reduceMotion": True},
            language_view={"targetLanguage": target},
            text=sample,
        )["visible"]
        after = time.monotonic_ns() // 1_000_000

        def offline_face():
            value = state()
            candidate = value.get("candidate") or {}
            observation = candidate.get("fontRuns") or {}
            phase["lastObservation"] = observation
            runs = observation.get("runs") or []
            if (
                value.get("observedAtMs", 0) <= after
                or candidate.get("text") != sample
                or not runs
                or any(
                    run.get("missingGlyphs") != 0
                    or (run.get("font") or {}).get("checksum") != files[file]["sha256"]
                    for run in runs
                )
            ):
                return None
            return value

        actual = wait_for(offline_face, f"{font}/{target}: bundled face did not shape live preview")
        assert not actual["candidate"]["editable"] and not actual["candidate"]["focused"]
        assert actual["focus"] == "fixture-first"
        assert actual["first"] == initial["first"] and actual["second"] == initial["second"]


def check_language_retention_refusal(
    client, source, generation, state, initial, nested, dimensions, stage, evidence
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
        if name == trigger and not field:
            phase = evidence["languageTrigger"] = dict(initial_render)
            click_language_trigger(
                state, nested, dimensions, phase, client.hello["backend"], layout=False
            )
            return
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

    # Host token identity belongs to the session, not the transport repaint.
    token_revision, revision, status = 9, 2, "fixture preview"
    initial_render = evidence["retentionInitial"] = {}
    assert render_preview(
        client,
        source,
        generation,
        1,
        "fixture exit",
        language_view=languages,
        evidence=initial_render,
        token_revision=token_revision,
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
    # Repaint transport 2 retains host session 9, without changing source IME authority.
    retained_render = evidence["retentionRenewal"] = {}
    assert render_preview(
        client,
        source,
        generation,
        2,
        "fixture exit",
        language_view=languages,
        evidence=retained_render,
        token_revision=token_revision,
    )["visible"]
    wait_for(detached_menu, "read-only renewal changed source/menu focus")
    if stage == "language-retention-body":
        # A known host save failure changes only its read-only feedback label.
        # The same actual detached menu/source must survive, without capture or input.
        before = controls()[control]["instance"]
        status = "语言设置未保存，请在设置中检查语言方向或保存权限。"
        revision += 1
        assert render_preview(
            client,
            source,
            generation,
            revision,
            "fixture exit",
            language_view=languages,
            token_revision=token_revision,
            status=status,
            evidence=evidence.setdefault("retentionStatus", {}),
        )["visible"]
        observed = wait_for(detached_menu, "save feedback changed source/menu focus")
        assert observed["overlayControls"][control]["instance"] == before
        assert observed["first"] == initial["first"] and observed["second"] == initial["second"]
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
        revision += 1
        assert render_preview(
            client,
            source,
            generation,
            revision,
            "fixture exit",
            language_view=languages,
            evidence=retained_render,
            token_revision=token_revision,
        )["visible"]
        assert not state()["imeClear"], "collapsed menu fabricated source Clear"
        key("F1")  # The configured source acceptance must not be minted by menu focus.
        client.call("describe")
        assert not any(event.get("event") == "accepted" for event in client.events)
        assert state()["first"] == initial["first"]
        stage = stage.replace("language-retention-trigger-", "language-retention-", 1)
    changed_source = json.loads(json.dumps(source))
    kwargs = {"token_revision": token_revision}
    expected_first = initial["first"]
    if stage == "language-retention-body":
        kwargs["text"] = "A different candidate cannot borrow the detached preview."
    elif stage == "language-retention-host-token":
        kwargs["token_revision"] = 10
    elif stage == "language-retention-state":
        kwargs["view_state"] = "Translating"
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
    if stage != "language-retention-expired-render":
        lease = evidence.get("retentionStatus", retained_render)
        assert now_ms() <= lease["expiresAtMs"], "retention refusal was masked by expiry"
    result = render_preview(
        client,
        changed_source,
        generation,
        revision + 1,
        "fixture exit",
        language_view=languages,
        status=status,
        evidence=evidence.setdefault("retentionRefusal", {}),
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


def language_trigger_point(actual, phase, dimensions, now, backend="gtk3", *, layout=True):
    assert now <= phase["expiresAtMs"], "language trigger lease expired before click"
    return language_trigger_geometry(
        actual, phase["renderReturnedAtMonoMs"], dimensions, backend, layout=layout
    )


def language_trigger_geometry(actual, rendered, dimensions, backend, *, layout=True):
    name = "typelay-menu-trigger"
    if not clickable_control(actual, name, rendered, dimensions):
        return None
    control = actual["overlayControls"][name]
    if not control.get("enabled"):
        return None
    if backend == "gtk3":
        if control.get("scale") != actual["scale"]:
            return None
        if layout and (
            control.get("labelLayoutCurrent") is not True
            or not control.get("labelRevision")
            or control.get("paintedLabelRevision") != control["labelRevision"]
        ):
            return None
        bounds = control
    elif backend == "gtk4":
        if layout:
            paint = control.get("paintObservation", {})
            if (
                not glossary_painted_geometry(control, actual.get("observedAtMs"))
                or type(paint.get("labelRevision")) is not int
                or paint["labelRevision"] <= 0
                or type(paint.get("paintedLabelRevision")) is not int
                or paint.get("paintedLabelRevision") != paint["labelRevision"]
            ):
                return None
        bounds = control.get("surfaceGeometry")
        if (
            not isinstance(bounds, dict)
            or type(bounds.get("scale")) is not int
            or bounds["scale"] != actual["scale"]
        ):
            return None
        if type(bounds.get("surfaceInstance")) is not int or bounds["surfaceInstance"] <= 0:
            return None
        if not all(
            type(bounds.get(key)) in (int, float) and math.isfinite(bounds[key])
            for key in ("x", "y")
        ):
            return None
    else:
        raise AssertionError("unknown GTK language trigger backend")
    return click_point(
        actual,
        (bounds["x"] + control["width"] / 2, bounds["y"] + control["height"] / 2),
        dimensions,
    )


def language_trigger_hover_current(
    actual, phase, dimensions, now, backend, instance, surface, point, moved_at, *, layout=True
):
    assert now <= phase["expiresAtMs"], "language trigger lease expired before click"
    return language_trigger_hover_geometry(
        actual,
        phase["renderReturnedAtMonoMs"],
        dimensions,
        backend,
        instance,
        surface,
        point,
        moved_at,
        layout=layout,
    )


def language_trigger_hover_geometry(
    actual, rendered, dimensions, backend, instance, surface, point, moved_at, *, layout=True
):
    current_point = language_trigger_geometry(actual, rendered, dimensions, backend, layout=layout)
    if current_point is None or actual.get("observedAtMs", 0) < moved_at:
        return False
    control = actual["overlayControls"]["typelay-menu-trigger"]
    bounds = control["surfaceGeometry"] if backend == "gtk4" else control
    # Keep the original physical pointer fixed. A layout change may move the
    # center without moving that pointer outside this same control/surface.
    x, y = (coordinate / actual["scale"] for coordinate in point)
    if not (
        bounds["x"] < x < bounds["x"] + control["width"]
        and bounds["y"] < y < bounds["y"] + control["height"]
    ):
        return False
    entered = control.get("pointerEnteredAtMonoMs" if backend == "gtk4" else "pointerEnteredAtMs")
    return (
        control.get("instance") == instance
        and (backend != "gtk4" or control["surfaceGeometry"]["surfaceInstance"] == surface)
        and control.get("pointerInside") is True
        and type(entered) is int
        and entered > 0
    )


def click_language_trigger(state, nested, dimensions, phase, backend, *, layout=True):
    # No renewal: these language tests deliberately bind some actions to an
    # original expiry. Hover consumes that same budget and never retries input.
    def observation(actual):
        control = actual.get("overlayControls", {}).get("typelay-menu-trigger", {})
        return {
            "observedAtMonoMs": actual.get("observedAtMs"),
            "parent": {key: actual.get(key) for key in ("fullscreen", "width", "height", "scale")},
            "control": {
                key: control.get(key)
                for key in (
                    "instance",
                    "visible",
                    "enabled",
                    "x",
                    "y",
                    "width",
                    "height",
                    "surfaceGeometry",
                    "text",
                    "paintObservation",
                    "pointerInside",
                    "pointerEnteredAtMs",
                    "pointerEnteredAtMonoMs",
                    "pointerObservation",
                    "eventWindow",
                )
            },
        }

    def ready():
        actual = state()
        phase["triggerGeometry"] = observation(actual)
        point = language_trigger_point(actual, phase, dimensions, now_ms(), backend, layout=layout)
        return (actual, point) if point else None

    actual, point = wait_for(ready, "language trigger geometry unavailable before single move")
    control = actual["overlayControls"]["typelay-menu-trigger"]
    instance = control["instance"]
    surface = control.get("surfaceGeometry", {}).get("surfaceInstance")
    phase.update(control="typelay-menu-trigger", point=point)
    subprocess.run(
        ["xdotool", "mousemove", "--window", nested, *map(str, point)], check=True, timeout=3
    )
    moved_at = time.monotonic_ns() // 1_000_000
    phase["moveFinishedAtMonoMs"] = moved_at
    try:
        location = dict(
            line.split("=", 1)
            for line in subprocess.check_output(
                ["xdotool", "getmouselocation", "--shell"], text=True, timeout=3
            ).splitlines()
        )
        phase["xPointer"] = {
            "ownedNiri": int(nested),
            **{key: int(location[key]) for key in ("X", "Y", "WINDOW")},
        }
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        phase["xPointer"] = {"unavailable": type(error).__name__}

    def hovered():
        actual = state()
        value = observation(actual)
        phase.setdefault("firstHoverObservation", value)
        phase["lastHoverObservation"] = value
        return (
            actual
            if language_trigger_hover_current(
                actual,
                phase,
                dimensions,
                now_ms(),
                backend,
                instance,
                surface,
                point,
                moved_at,
                layout=layout,
            )
            else None
        )

    actual = wait_for(hovered, "language trigger did not receive real same-point hover")
    phase["beforeClick"] = observation(actual)
    assert now_ms() <= phase["expiresAtMs"], "language trigger lease expired before click"
    phase["clickStartedAtMonoMs"] = time.monotonic_ns() // 1_000_000
    phase["clickStartedAtMs"] = now_ms()
    try:
        subprocess.run(["xdotool", "click", "1"], check=True, timeout=3)
        phase["clickCommandSucceeded"] = True
    finally:
        phase["clickFinishedAtMs"] = now_ms()
        phase["clickFinishedAtMonoMs"] = time.monotonic_ns() // 1_000_000


def language_menu_point(actual, name, phase, dimensions, now, instances):
    """GTK4 menu mapping can expose a child's previous, collapsed allocation."""
    assert now <= phase["expiresAtMs"], "language menu lease expired before click"
    return language_menu_geometry(
        actual, name, phase["renderReturnedAtMonoMs"], dimensions, instances
    )


def language_menu_geometry(actual, name, rendered, dimensions, instances):
    controls = actual.get("overlayControls", {})
    for control_name, instance in instances.items():
        item = controls.get(control_name, {})
        assert item.get("instance") == instance, "language menu control replaced before click"
        assert item.get("visible") is True, "language menu control disappeared before click"
    control, menu = controls[name], controls["typelay-language-menu"]
    if (
        actual.get("observedAtMs", 0) <= rendered
        or not parent_geometry_ready(actual, dimensions)
        or not all(
            glossary_painted_geometry(item, actual.get("observedAtMs")) for item in (control, menu)
        )
    ):
        return None
    bounds, menu_bounds = control["surfaceGeometry"], menu["surfaceGeometry"]
    viewport = menu.get("scrollViewport")
    assert isinstance(viewport, dict) and all(
        type(viewport.get(key)) in (int, float) and math.isfinite(viewport[key])
        for key in ("x", "y", "width", "height")
    ), "language menu viewport geometry missing"
    assert (
        bounds["surfaceInstance"]
        == menu_bounds["surfaceInstance"]
        == viewport.get("surfaceInstance")
        and bounds["scale"] == menu_bounds["scale"] == viewport.get("scale") == actual["scale"]
        and viewport["width"] > 0
        and viewport["height"] > 0
        and viewport["x"] <= bounds["x"]
        and bounds["x"] + bounds["width"] <= viewport["x"] + viewport["width"]
        and viewport["y"] <= bounds["y"]
        and bounds["y"] + bounds["height"] <= viewport["y"] + viewport["height"]
    ), "language menu control is not fully visible on the current surface"
    return click_point(
        actual,
        (bounds["x"] + bounds["width"] / 2, bounds["y"] + bounds["height"] / 2),
        dimensions,
    )


async def language_wait_for(predicate, description, timeout=5):
    """The existing observation budget, yielding to the real desktop owner."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        await asyncio.sleep(0.01)
    raise AssertionError(description)


async def language_command(*arguments, timeout, environment=None):
    child = await asyncio.create_subprocess_exec(
        *arguments, env=environment, stdout=asyncio.subprocess.PIPE
    )
    try:
        output, _ = await asyncio.wait_for(child.communicate(), timeout)
        if child.returncode:
            raise subprocess.CalledProcessError(child.returncode, arguments, output)
        return output.decode()
    finally:
        if child.returncode is None:
            child.terminate()
            try:
                await asyncio.wait_for(child.wait(), 3)
            except TimeoutError:
                child.kill()
                await asyncio.wait_for(child.wait(), 3)


async def language_key(value):
    await language_command("xdotool", "key", "--clearmodifiers", "--delay", "0", value, timeout=2)
    await asyncio.sleep(0.025)  # Preserve key()'s original pacing, not a new retry/wait.


def language_configuration_preview(source, generation):
    return {
        "state": "ValidTranslation",
        "source": source,
        "editable": False,
        "canAccept": False,
        "text": "Read-only fixture preview.",
        "status": "fixture preview",
        "sourceLanguage": "en",
        "targetLanguage": "ja",
        "presentation": {
            "visible": True,
            "editorOwnsText": False,
            "tabHint": "fixture read-only preview",
            "dot": "Yellow",
        },
        "sessionShortcuts": {
            "acceptHint": "fixture accept",
            "passThroughHint": "fixture application key",
            "exitHint": "fixture exit",
        },
        "viewContext": {
            "token": {"target": source["target"], "commandGeneration": generation, "revision": 1},
            "bindings": {"accept": "F1", "exit": "F24"},
            "canEdit": False,
        },
    }


async def check_language_configuration(
    overlay,
    peer,
    preview,
    source,
    generation,
    state,
    initial,
    nested,
    dimensions,
    evidence,
    native_events,
    calls,
    begin_release,
    stage,
    environment,
    output,
    jobs,
):
    """GTK4 public UI over real Input/Overlay; controlled configuration Host only.

    Host StopSession/persistence and stale connection/cancel replies have separate
    real protocol contracts. This case does not manufacture a native focus ACK.
    Each retention case is independently initialized, never a failed-case retry.
    """
    popup_name, search_name = "typelay-language-configuration", "typelay-language-search"
    trigger = "typelay-menu-trigger"
    started = time.monotonic_ns() // 1_000_000
    actions = evidence["configurationActions"] = []
    configuration = None
    peer_lifetime, peer_connection = peer.lifetime, peer.connection_id
    source_instances = {name: initial[name]["instance"] for name in ("firstEntry", "secondEntry")}
    top_instance = initial["firstEntry"]["topInstance"]
    focus_instances = {}

    def owned(actual):
        assert peer in overlay.input.peers and peer.link.channel.alive
        assert peer.lifetime == peer_lifetime and peer.connection_id == peer_connection
        assert overlay.input.focused_peer() is peer
        assert parent_geometry_ready(actual, dimensions)
        for name, instance in source_instances.items():
            item = actual[name]
            assert item["instance"] == instance and item["topInstance"] == top_instance
            assert item["topActive"] and item["mapped"] and item["enabled"]
        return actual

    def observe():
        actual = state()
        assert actual["first"] == initial["first"] and actual["second"] == initial["second"]
        return actual

    def config_current():
        assert overlay.language_configuration is configuration
        assert configuration["phase"] == "Open"
        assert overlay.language_configuration_current(configuration)
        assert overlay.value is None and overlay.peer is None
        assert not overlay.visible and not overlay.editing and not overlay.acceptance_ready
        actual = owned(observe())
        assert actual.get("candidate") is None
        return actual

    def input_target(name, *, composing=None, closed_source=False):
        actual = owned(observe()) if closed_source else config_current()
        if closed_source:
            assert overlay.language_configuration is None and overlay.value is None
            assert not overlay.visible and actual.get("candidate") is None
            assert actual[name]["focused"]
        else:
            item = actual.get("overlayControls", {}).get(name, {})
            assert item.get("instance") == focus_instances[name] and item.get("focused")
            assert item.get("visible") is True and item.get("enabled") is True
        if composing is not None:
            assert actual.get("focusedImeComposing" if composing else "focusedImeClear")

    async def config_key(key_name, target=search_name, *, composing=False):
        input_target(target, composing=composing)
        await language_key(key_name)

    async def source_key(key_name):
        input_target("firstEntry", closed_source=True)
        await language_key(key_name)

    async def search_type(text):
        input_target(search_name, composing=False)
        await language_command(
            "xdotool", "type", "--clearmodifiers", "--delay", "0", text, timeout=3
        )

    def geometry(actual, name):
        control = actual.get("overlayControls", {}).get(name, {})
        if not parent_geometry_ready(actual, dimensions) or control.get("enabled") is not True:
            return None
        if not glossary_painted_geometry(control, actual.get("observedAtMs")):
            return None
        bounds = control["surfaceGeometry"]
        if bounds["scale"] != actual["scale"] or bounds["width"] <= 1 or bounds["height"] <= 1:
            return None
        if not (
            0 <= bounds["x"]
            and bounds["x"] + bounds["width"] <= actual["width"]
            and 0 <= bounds["y"]
            and bounds["y"] + bounds["height"] <= actual["height"]
        ):
            return None
        return click_point(
            actual,
            (bounds["x"] + bounds["width"] / 2, bounds["y"] + bounds["height"] / 2),
            dimensions,
        )

    async def click(name, *, opening=False):
        # Freeze one painted object and one point. The production owner's normal
        # pre-begin display renewal is not a configuration/source capability.
        def ready():
            if not opening:
                config_current()
            actual = observe()
            point = geometry(actual, name)
            return (actual, point) if point else None

        actual, point = await language_wait_for(ready, f"{name}: current painted geometry missing")
        before = actual["overlayControls"][name]
        if opening:
            assert overlay.visible and overlay.current_record() is not None
            assert now_ms() <= evidence["configurationRender"]["expiresAtMs"]
        else:
            assert (
                before["surfaceGeometry"]["surfaceInstance"]
                == actual["overlayControls"][popup_name]["surfaceGeometry"]["surfaceInstance"]
            )
        moved_start = time.monotonic_ns() // 1_000_000
        await language_command(
            "xdotool", "mousemove", "--window", nested, *map(str, point), timeout=3
        )
        moved = time.monotonic_ns() // 1_000_000
        location = dict(
            line.split("=", 1)
            for line in (
                await language_command("xdotool", "getmouselocation", "--shell", timeout=3)
            ).splitlines()
        )
        pointer = {
            "status": "captured",
            "observedAtMonoMs": time.monotonic_ns() // 1_000_000,
            "ownedNiri": int(nested),
            **{key: int(location[key]) for key in ("X", "Y", "WINDOW")},
        }

        def hovered():
            if not opening:
                config_current()
            actual = observe()
            control = actual.get("overlayControls", {}).get(name, {})
            return (
                actual
                if glossary_hover_current(
                    control,
                    actual.get("observedAtMs", 0),
                    before,
                    point,
                    moved,
                    pointer,
                    evidence["nestedOrigin"],
                    moved_start,
                )
                else None
            )

        actual = await language_wait_for(hovered, f"{name}: fixed-point current hover missing")
        if opening:
            assert overlay.visible and overlay.current_record() is not None
            assert now_ms() <= evidence["configurationRender"]["expiresAtMs"]
        owned(actual)
        if opening:
            assert actual["firstEntry"]["focused"]
        else:
            input_target(search_name)
        phase = {
            "name": name,
            "instance": before["instance"],
            "point": point,
            "mapGeneration": before["paintObservation"]["mapGeneration"],
            "clickStartedAtMs": now_ms(),
        }
        actions.append(phase)
        assert len(actions) <= 4
        await language_command("xdotool", "click", "1", timeout=3)
        phase["clickFinishedAtMs"] = now_ms()

    async def source_click(name):
        actual = owned(observe())
        bounds = actual[name]
        assert bounds["width"] > 1 and bounds["height"] > 1
        point = click_point(
            actual,
            (bounds["x"] + bounds["width"] / 2, bounds["y"] + bounds["height"] / 2),
            dimensions,
        )
        frozen_bounds = tuple(bounds[key] for key in ("x", "y", "width", "height"))
        await language_command(
            "xdotool", "mousemove", "--window", nested, *map(str, point), timeout=3
        )
        moved = time.monotonic_ns() // 1_000_000

        def source_hit():
            current = owned(observe())
            field = current[name]
            assert tuple(field[key] for key in ("x", "y", "width", "height")) == frozen_bounds
            hit = field.get("sourceHit")
            return (
                current["observedAtMs"] >= moved
                and hit
                and hit["isSource"]
                and hit["x"] * current["scale"] == point[0]
                and hit["y"] * current["scale"] == point[1]
            )

        await language_wait_for(source_hit, "fixed source point not currently owned by its source")
        # One original click only, after the real device/pick and exact source
        # identity check. No focus call, second point or conditional retry.
        await language_command("xdotool", "click", "1", timeout=3)
        await language_wait_for(
            lambda: state().get("focus")
            == ("fixture-first" if name == "firstEntry" else "fixture-second"),
            "one real outside click did not focus its source field",
        )

    async def closed():
        actual = await language_wait_for(
            lambda: (value := state())
            and overlay.language_configuration is None
            and not value.get("overlayControls", {}).get(popup_name, {}).get("visible")
            and value,
            "configuration opening did not retire",
        )
        assert actual.get("candidate") is None
        assert overlay.value is None and not overlay.visible and not overlay.acceptance_ready
        return actual

    async def image(label):
        config_current()
        path = output / f"gtk4-language-configuration-{label}.png"
        await language_command("grim", str(path), timeout=3, environment=environment)
        config_current()
        evidence.setdefault("screenshots", []).append(path.name)

    automatic_source = stage == "language-configuration-swap-auto"
    overlay.render_guidance(
        {
            "languageChoices": {
                "source": [
                    *([{"code": "auto", "label": "自动"}] if automatic_source else []),
                    {"code": "en", "label": "English"},
                    {"code": "ja", "label": "日本語"},
                ],
                "target": [
                    {"code": "en", "label": "English"},
                    {"code": "ja", "label": "日本語"},
                    {"code": "ko", "label": "한국어"},
                ],
            }
        }
    )
    overlay.render(preview)
    await language_wait_for(
        lambda: observe() and not overlay.pending and overlay.visible,
        "initial configuration trigger preview not rendered",
    )
    await click(trigger, opening=True)
    await language_wait_for(lambda: calls["begin"], "real trigger did not request configuration")
    old_render = json.loads(json.dumps(evidence["configurationRender"]))
    configuration = overlay.language_configuration
    assert configuration is not None and len(calls["begin"]) == 1
    evidence["beginElapsedMs"] = now_ms() - actions[0]["clickStartedAtMs"]
    evidence["handshakeExpiresAtMs"] = configuration["expiresAtMs"]
    pending_case = stage in (
        "language-retention-trigger-external-focus",
        "language-retention-trigger-expired-render",
    )
    if pending_case:
        assert configuration["phase"] == "Pending"
        assert observe().get("candidate") is None and overlay.value is None
        if stage.endswith("external-focus"):
            await source_click("secondEntry")
            assert now_ms() <= configuration["expiresAtMs"], (
                "pending focus refusal masked by timeout"
            )
        await closed()
        if stage.endswith("expired-render"):
            assert now_ms() >= actions[0]["clickStartedAtMs"] + 600, (
                "configuration retired before its native handshake deadline"
            )
        begin_release.set()  # Deliberately deliver the held old reply, not a new begin/ACK.
        await language_wait_for(
            lambda: all(
                job.done()
                for job in jobs
                if job.get_coro().cr_code == overlay.begin_language_configuration.__func__.__code__
            ),
            "late controlled Host reply not drained",
        )
        await peer.link.ask("describe")
        final = await closed()
        if stage.endswith("external-focus"):
            assert final.get("focus") == "fixture-second", "late ACK stole the new field's focus"
        else:
            await language_wait_for(
                lambda: now_ms() > old_render["expiresAtMs"], "old source deadline not reached"
            )
            result = await peer.link.ask("overlay-render", old_render)
            assert result == {"visible": False, "editing": False}
            # As in the former expiry case, only an explicit new real source
            # interaction/capture admits a new preview, never the expired request.
            await source_click("firstEntry")
            await language_wait_for(lambda: observe().get("imeClear"), "fresh source IME not Clear")
            fresh = await overlay.input.request("capture", {})
            assert fresh and fresh.get("snapshot") and fresh["snapshot"]["text"] == initial["first"]
            assert fresh["snapshot"]["observedAtMs"] > source["observedAtMs"]
            following = language_configuration_preview(fresh["snapshot"], generation)
            following["viewContext"]["token"]["revision"] = 2
            overlay.render(following)
            await language_wait_for(
                lambda: not overlay.pending and overlay.visible and observe().get("candidate"),
                "fresh source preview not admitted after retirement",
            )
            assert not observe()["candidate"].get("editablePreview", False)
            evidence["freshSourcePreview"] = True
        assert not calls["save"] and calls["cancel"]
        evidence["pendingRetired"] = True
        return

    actual = await language_wait_for(
        lambda: (value := observe())
        and configuration["phase"] == "Open"
        and value.get("overlayControls", {}).get(search_name, {}).get("focused")
        and value,
        "configuration search did not acquire its own acknowledged focus",
    )
    assert now_ms() <= configuration["expiresAtMs"], "open observation crossed handshake deadline"
    config_current()
    controls = actual["overlayControls"]
    opening_instance = controls[popup_name]["instance"]
    focus_instances.update({name: item["instance"] for name, item in controls.items()})
    assert controls[popup_name]["width"] == 264, controls[popup_name]
    assert controls[popup_name]["autohide"] is True and controls[popup_name]["hasArrow"] is False
    assert "typelay-source-language" not in controls and "typelay-apply-languages" not in controls
    assert not actual["firstEntry"]["focused"] and not actual["imeClear"]
    assert await overlay.input.request("capture", {}) is None, (
        "configuration search became a source"
    )
    assert not calls["save"]

    if stage in ("language-configuration-swap", "language-configuration-swap-auto"):
        swap = controls["typelay-language-swap"]
        assert swap["visible"] is True and swap["enabled"] is (not automatic_source)
        if automatic_source:
            # Actual GTK sensitivity is the negative here, not a manufactured
            # activation of a disabled peer. Escape uses the same focused search.
            assert not calls["save"]
            await config_key("Escape")
            await closed()
            assert not calls["save"] and calls["cancel"]
        else:
            await click("typelay-language-swap")
            await language_wait_for(lambda: len(calls["save"]) == 1, "real swap was not sent")
            await closed()
            assert [(item["source"], item["target"]) for item in calls["save"]] == [("ja", "en")]
    elif stage in ("language-configuration-enter", "language-configuration-empty-recovery"):
        if stage.endswith("empty-recovery"):
            # These queries/keys are pre-registered, not a retry after a failed
            # Enter. Neither an unavailable row nor an empty list may save.
            for query, visible in (("fr", {"fr"}), ("not-a-language", set())):
                await search_type(query)
                filtered = await language_wait_for(
                    lambda: (value := config_current())
                    and value["overlayControls"][search_name].get("text") == query
                    and all(
                        value["overlayControls"][f"typelay-language-choice-{code}"]["visible"]
                        == (code in visible)
                        for code in ("en", "ja", "ko", "fr")
                    )
                    and value,
                    "search did not show its exact unavailable/empty result",
                )
                assert filtered["overlayControls"]["typelay-language-choice-fr"]["enabled"] is False
                if not visible:
                    assert filtered["overlayControls"]["fixture-language-feedback"].get("text")
                await config_key("Return")
                await peer.link.ask("describe")
                assert (
                    config_current()["overlayControls"][popup_name]["instance"] == opening_instance
                )
                assert not calls["save"] and not calls["cancel"]
                await config_key("ctrl+a")
                await config_key("BackSpace")
                restored = await language_wait_for(
                    lambda: (value := config_current())
                    and value["overlayControls"][search_name].get("text") == ""
                    and all(
                        value["overlayControls"][f"typelay-language-choice-{code}"]["visible"]
                        for code in ("en", "ja", "ko", "fr")
                    )
                    and value,
                    "clearing the same search did not restore its real catalog",
                )
                assert restored["overlayControls"][popup_name]["instance"] == opening_instance
                assert not restored["overlayControls"]["fixture-language-feedback"].get("text")
                assert not calls["save"] and not calls["cancel"]
        await search_type("ko")
        await language_wait_for(
            lambda: (value := config_current())
            and value["overlayControls"][search_name].get("text") == "ko"
            and value["overlayControls"]["typelay-language-choice-ko"]["visible"]
            and all(
                not value["overlayControls"][f"typelay-language-choice-{code}"]["visible"]
                for code in ("en", "ja", "fr")
            ),
            "search did not isolate the actual selectable target before Enter",
        )
        await config_key("Return")  # Real Entry key path, never direct activate/click.
        await language_wait_for(lambda: len(calls["save"]) == 1, "real search Enter did not save")
        await closed()
        assert [(item["source"], item["target"]) for item in calls["save"]] == [("en", "ko")]
    elif stage == "language-options":
        actual = await language_wait_for(
            lambda: (value := observe())
            and geometry(value, "typelay-language-choice-en")
            and value,
            "configuration list not painted",
        )
        controls = actual["overlayControls"]
        assert controls["typelay-language-choice-en"]["enabled"], (
            "same-language direction was filtered out"
        )
        assert controls["typelay-language-choice-fr"]["enabled"] is False
        # GTK default focus traversal remains real after replacing only its
        # unconditional Escape handler; keyboard focus must visibly follow Tab.
        await config_key("Tab")
        await language_wait_for(
            lambda: (value := observe())
            .get("overlayControls", {})
            .get("typelay-language-choice-en", {})
            .get("focused")
            and value["overlayControls"]["typelay-language-choice-en"].get("focusVisible"),
            "Tab did not show the first actual target row's focus ring",
        )
        await config_key("shift+Tab", "typelay-language-choice-en", composing=None)
        await language_wait_for(
            lambda: observe()["overlayControls"][search_name]["focused"],
            "Shift+Tab did not return to the same search",
        )
        await image("open")
        input_target(search_name)
        await language_command("fcitx5-remote", "-s", "pinyin", timeout=3, environment=environment)
        await language_wait_for(
            lambda: observe().get("focusedImeClear"), "search pinyin context not Clear"
        )
        await search_type("nihao")
        await language_wait_for(
            lambda: observe().get("focusedImeComposing"), "real search preedit missing"
        )
        await click("typelay-language-choice-en")
        await peer.link.ask("describe")
        composed = observe()
        assert (
            composed["focusedImeComposing"] and composed["overlayControls"][search_name]["focused"]
        )
        assert composed["overlayControls"][search_name]["text"] == "" and not calls["save"]
        await config_key(
            "Escape", composing=True
        )  # Delivered to the real IM, never a configuration cancel.
        await language_wait_for(
            lambda: observe().get("focusedImeClear"), "original preedit Escape was not delivered"
        )
        config_current()
        assert observe()["overlayControls"][popup_name]["instance"] == opening_instance
        assert not calls["save"] and not calls["cancel"]
        input_target(search_name)
        await language_command(
            "fcitx5-remote", "-s", "keyboard-us", timeout=3, environment=environment
        )
        await language_wait_for(
            lambda: observe().get("focusedImeClear"), "search keyboard context not Clear"
        )
        await search_type("ko")
        filtered = await language_wait_for(
            lambda: (value := observe())
            and value["overlayControls"][search_name].get("text") == "ko"
            and value["overlayControls"]["typelay-language-choice-ko"]["visible"]
            and not value["overlayControls"]["typelay-language-choice-en"]["visible"]
            and not value["overlayControls"]["typelay-language-choice-ja"]["visible"]
            and value,
            "search did not filter by its real code",
        )
        assert filtered["overlayControls"][popup_name]["instance"] == opening_instance
        assert await overlay.input.request("capture", {}) is None
        await image("search")
        await click("typelay-language-choice-ko")
        await language_wait_for(
            lambda: len(calls["save"]) == 1
            and configuration["phase"] == "Open"
            and observe()["overlayControls"].get("fixture-language-feedback", {}).get("text"),
            "explicit save failure did not return this opening to Open",
        )
        assert observe()["overlayControls"][popup_name]["instance"] == opening_instance
        await image("save-failure")
        await click("typelay-language-choice-ko")  # An explicit new user retry, never automatic.
        await language_wait_for(lambda: len(calls["save"]) == 2, "second real choice was not sent")
        await closed()
        assert [(item["source"], item["target"]) for item in calls["save"]] == [
            ("en", "ko"),
            ("en", "ko"),
        ]
    elif stage in ("language-retention-external-focus", "language-retention-source-input"):
        await source_click("secondEntry" if stage.endswith("external-focus") else "firstEntry")
        await closed()
        if stage.endswith("source-input"):
            await source_key("End")
            await source_key("x")
            await language_wait_for(
                lambda: state()["first"] == initial["first"] + "x", "new source input missing"
            )
            assert state()["second"] == initial["second"] and state().get("candidate") is None
            assert not calls["save"]
            return
    elif stage in (
        "language-retention-trigger-authority",
        "language-retention-trigger-permissions",
    ):
        await config_key("Escape")
        await closed()
        # There is no collapsed live source-preview/trigger after cancellation.
        # An ordinary source key cannot obtain the old editing/acceptance ticket.
        await source_key("F1")
        await peer.link.ask("describe")
        assert observe().get("candidate") is None and not overlay.acceptance_ready
        assert not any(event.get("operation") in ("accept", "beginEdit") for event in native_events)
        rejected = json.loads(json.dumps(old_render))
        rejected["revision"] += 1
        if stage.endswith("authority"):
            rejected["authority"]["inputRevision"] += 1
        else:
            rejected["view"]["editable"] = True
            rejected["view"]["viewContext"]["canEdit"] = True
        assert now_ms() <= old_render["expiresAtMs"], "closed-opening refusal masked by expiry"
        assert await peer.link.ask("overlay-render", rejected) == {
            "visible": False,
            "editing": False,
        }
        assert observe().get("candidate") is None
    else:
        # The old detached-preview cases now receive an actual subsequent Host
        # render. Every such view retires the configuration; it cannot retain the
        # retired source's candidate behind the search or inherit editing rights.
        changed = json.loads(json.dumps(old_render))
        changed["revision"] += 1
        if stage == "language-retention-body":
            changed["view"]["text"] = "A different controlled candidate."
        elif stage == "language-retention-host-token":
            changed["view"]["viewContext"]["token"]["revision"] += 1
        elif stage == "language-retention-state":
            changed["view"]["state"] = "Translating"
        elif stage == "language-retention-authority":
            changed["authority"]["inputRevision"] += 1
        elif stage == "language-retention-permissions":
            changed["view"]["editable"] = True
            changed["view"]["viewContext"]["canEdit"] = True
        else:
            raise AssertionError(f"unmapped GTK4 language lifecycle case: {stage}")
        assert now_ms() <= old_render["expiresAtMs"], (
            "new-view refusal masked by original display expiry"
        )
        result = await peer.link.ask("overlay-render", changed)
        assert result == {"visible": False, "editing": False}
        await closed()
    assert observe().get("candidate") is None
    assert not any(event.get("operation") in ("accept", "beginEdit") for event in native_events)
    expected_saves = (
        2
        if stage == "language-options"
        else 1
        if stage
        in (
            "language-configuration-swap",
            "language-configuration-enter",
            "language-configuration-empty-recovery",
        )
        else 0
    )
    assert len(calls["save"]) == expected_saves
    evidence["elapsedMs"] = time.monotonic_ns() // 1_000_000 - started


def run_owned_language_options(
    command, environment, socket_path, state_path, output, nested, *, configuration_stage=None
):
    """Real production renewal/IPC/foreground owner; only the host is controlled."""
    import gi

    gi.require_version("Gdk", "4.0")
    from gi.events import GLibEventLoop
    from gi.repository import Gdk, GLib
    from input import Input
    from overlay import Overlay

    async def exercise():
        jobs, native_events, saves = [], [], []
        automatic_source = configuration_stage == "language-configuration-swap-auto"
        begin_release = asyncio.Event()
        pending_case = configuration_stage in (
            "language-retention-trigger-external-focus",
            "language-retention-trigger-expired-render",
        )
        if not pending_case:
            begin_release.set()
        configuration_calls = {"begin": [], "cancel": [], "save": saves}
        evidence = {
            "clock": "CLOCK_BOOTTIME",
            "owner": "production Input/Overlay",
            "displaySends": [],
            "renewalSendCount": 0,
        }
        if configuration_stage:
            evidence["configurationStage"] = configuration_stage
            evidence["controlledHost"] = (
                "configuration ACK/result only; real Host contracts are separate"
            )
        observing = True

        def configuration_note(stage, opening_id=None, **fields):
            if not configuration_stage or not observing:
                return
            try:
                rows = evidence.setdefault("configurationTimeline", [])
                if len(rows) >= 32:
                    evidence["configurationTimelineOmitted"] = (
                        evidence.get("configurationTimelineOmitted", 0) + 1
                    )
                    return
                current = overlay.language_configuration
                phase = current.get("phase") if current else None
                rows.append(
                    {
                        "stage": stage,
                        "atMs": now_ms(),
                        "openingMatches": bool(current and current["openingId"] == opening_id),
                        "phase": phase
                        if phase in ("Pending", "Open", "Saving", "Retired")
                        else None,
                        "peerAlive": peer.link.channel.alive,
                        "peerFocused": input_service.focused_peer() is peer,
                        "compositorFresh": input_service.fresh(),
                        **fields,
                    }
                )
            except MemoryError:
                raise
            except Exception:
                pass  # Diagnostics cannot replace the original send, result or UI event.

        class Host:
            def send(self, operation, data=None):
                if configuration_stage and operation == "cancelLanguageConfiguration":
                    configuration_note("host-cancel", data.get("openingId"))
                    configuration_calls["cancel"].append(dict(data))
                    assert len(configuration_calls["cancel"]) <= 4
                    return
                if operation != "state":
                    evidence["unexpectedOperation"] = f"host notification: {operation}"
                    raise AssertionError(evidence["unexpectedOperation"])

            async def ask(self, operation, data=None):
                if operation == "allowField":
                    assert data.get("aria") == "GtkEntry"
                    assert data.get("enabled") is True and data.get("password") is False
                    assert data.get("readOnly") is False and data.get("offscreen") is False
                    return True  # The one owned, focused fixture source, no model or settings I/O.
                if configuration_stage and operation == "beginLanguageConfiguration":
                    assert data["connectionId"] == peer.connection_id
                    assert data["token"] == preview["viewContext"]["token"]
                    configuration_calls["begin"].append(dict(data))
                    assert len(configuration_calls["begin"]) == 1
                    # The real Host stop/persistence contracts run separately.
                    # Here its ordinary hide notification passes through the real bridge.
                    overlay.hide()
                    await begin_release.wait()
                    return {
                        "opened": True,
                        "connectionId": data["connectionId"],
                        "openingId": data["openingId"],
                        "settingsRevision": 9,
                        "source": "auto" if automatic_source else "en",
                        "target": "ja",
                        "sourceLabel": "自动" if automatic_source else "English",
                        "targetLabel": "日本語",
                        "swapAllowed": not automatic_source,
                        "choices": [
                            {
                                "code": code,
                                "label": label,
                                "nativeLabel": native,
                                "selectable": selectable,
                            }
                            for code, label, native, selectable in (
                                ("en", "英语", "English", True),
                                ("ja", "日语", "日本語", True),
                                ("ko", "韩语", "한국어", True),
                                ("fr", "法语", "Français", False),
                            )
                        ],
                    }
                if configuration_stage and operation == "saveLanguageConfiguration":
                    owner = configuration_calls["begin"][0]
                    assert data["connectionId"] == owner["connectionId"]
                    assert data["openingId"] == owner["openingId"]
                    assert data["expectedRevision"] == 9
                    saves.append(dict(data))
                    assert len(saves) <= 2
                    return {
                        "saved": len(saves) == 2
                        or configuration_stage
                        in (
                            "language-configuration-swap",
                            "language-configuration-enter",
                            "language-configuration-empty-recovery",
                        ),
                        "reason": "save-unconfirmed",
                    }
                if operation != "traySettings":
                    evidence["unexpectedOperation"] = f"host request: {operation}"
                    raise AssertionError(evidence["unexpectedOperation"])
                saves.append(data)
                return False  # Exercise the production save-failure response without persistence.

        def task(coroutine):
            jobs.append(asyncio.create_task(coroutine))

        host = Host()
        input_service = Input(GLib, Gdk, SimpleNamespace(native=socket_path), host, task)
        overlay = Overlay(host, input_service, task, lambda: host.send("settings"))
        fixture = None
        try:
            generation = str(uuid.uuid4())
            input_service.notification("settings", {"enabled": True})
            input_service.notification(
                "shortcuts",
                {
                    "enabled": False,
                    "sessionEnabled": True,
                    "generation": generation,
                    "accept": "F1",
                    "exit": "F24",
                    "wake": "F9",
                    "direct": "F10",
                },
            )
            overlay.configure(
                {
                    "source": "auto" if automatic_source else "en",
                    "target": "ja",
                    "uiLanguage": "zh-CN",
                }
            )
            fixture = await asyncio.create_subprocess_exec(*command, env=environment)

            def state():
                assert fixture.returncode is None, "language fixture exited"
                if not configuration_stage:
                    assert "retired" not in evidence, evidence.get("retired")
                assert "unexpectedOperation" not in evidence, evidence.get("unexpectedOperation")
                for job in jobs:
                    if job.done():
                        job.result()
                try:
                    return json.loads(state_path.read_text())
                except FileNotFoundError:
                    return {}

            peer = await language_wait_for(
                lambda: next((peer for peer in input_service.peers if peer.lifetime), None),
                "language fixture handshake missing",
            )
            assert fixture.pid in process_lineage(peer.pid), "unexpected native test peer"
            assert peer.backend in ("gtk3", "gtk4")
            if configuration_stage:
                assert peer.backend == "gtk4", "configuration migration is GTK4-only"
            window = await language_wait_for(
                lambda: next(
                    (item for item in input_service.niri.windows if item.get("pid") == peer.pid),
                    None,
                ),
                "language fixture window missing",
            )
            focused = asyncio.get_running_loop().create_future()
            input_service.niri.focus(
                window["id"], lambda ok: None if focused.done() else focused.set_result(ok)
            )
            assert await asyncio.wait_for(focused, 5), "language fixture focus refused"
            await language_command(
                "fcitx5-remote", "-s", "keyboard-us", timeout=3, environment=environment
            )
            geometry = dict(
                line.split("=", 1)
                for line in (
                    await language_command(
                        "xdotool", "getwindowgeometry", "--shell", nested, timeout=3
                    )
                ).splitlines()
            )
            dimensions = int(geometry["WIDTH"]), int(geometry["HEIGHT"])
            evidence["nestedSize"] = dimensions
            evidence["nestedOrigin"] = [int(geometry["X"]), int(geometry["Y"])]
            initial = await language_wait_for(
                lambda: (value := state())
                and value.get("focus") == "fixture-first"
                and value.get("imeClear")
                and parent_geometry_ready(value, dimensions)
                and input_service.focused_peer() is peer
                and value,
                "language source IME/parent/compositor did not become ready",
            )
            captured = await input_service.request("capture", {})
            assert captured and captured.get("snapshot"), input_service.capture_status
            source = captured["snapshot"]
            assert source["text"] == initial["first"]
            acknowledgement = captured["sessionShortcuts"]
            assert acknowledgement and acknowledgement["generation"] == generation
            assert acknowledgement["bindings"] == {"accept": "F1", "exit": "F24"}
            assert acknowledgement["sourceReady"] is True
            assert acknowledgement["editorReady"] is False

            preview = (
                language_configuration_preview(source, generation) if configuration_stage else None
            )
            if automatic_source:
                preview["sourceLanguage"] = "auto"
            # Observe the single production sender without decoding, handling
            # replies, or changing frames. A send is not a native acknowledgement.
            send = peer.link.channel.send
            ask = peer.link.ask

            async def observed_ask(operation, data=None, *arguments, **options):
                if not configuration_stage or operation != "overlay-language-configuration-open":
                    return await ask(operation, data, *arguments, **options)
                opening_id = data.get("openingId")
                configuration_note("native-open-dispatch", opening_id)
                try:
                    reply = await ask(operation, data, *arguments, **options)
                except (OSError, EOFError, TimeoutError):
                    configuration_note("native-open-transport-failed", opening_id)
                    raise
                configuration_note(
                    "native-open-return",
                    opening_id,
                    opened=isinstance(reply, dict) and reply.get("opened") is True,
                    replyOpeningMatches=isinstance(reply, dict)
                    and reply.get("openingId") == opening_id,
                )
                return reply

            def observed_send(frame):
                operation = frame.get("op")
                if operation not in {
                    "describe",
                    "overlay-render",
                    "overlay-renew",
                    "overlay-hide",
                    "revoke",
                    *(
                        {
                            "overlay-language-configuration-open",
                            "overlay-language-configuration-close",
                            "overlay-language-configuration-result",
                            "capture",
                        }
                        if configuration_stage
                        else set()
                    ),
                }:
                    evidence["unexpectedOperation"] = f"native operation: {operation}"
                    raise AssertionError(evidence["unexpectedOperation"])
                if observing and operation in ("overlay-render", "overlay-renew"):
                    data = frame["data"]
                    if configuration_stage and operation == "overlay-render":
                        evidence["configurationRender"] = json.loads(json.dumps(data))
                    sent = {
                        "operation": operation,
                        "sentAtMs": now_ms(),
                        "expiresAtMs": data["expiresAtMs"],
                        "revision": data.get("revision"),
                        "renderPending": overlay.pending,
                    }
                    evidence["displaySends"].append(sent)
                    if len(evidence["displaySends"]) > 16:
                        del evidence["displaySends"][2]
                    if operation == "overlay-renew":
                        evidence["renewalSendCount"] += 1
                    elif evidence.get("languagePhases"):
                        evidence["languagePhases"][-1].update(
                            expiresAtMs=data["expiresAtMs"], renderSentAtMs=sent["sentAtMs"]
                        )
                elif observing and operation == "overlay-hide":
                    evidence.setdefault("retired", {"reason": "owner-hide", "atMs": now_ms()})
                if operation == "overlay-language-configuration-close":
                    configuration_note(
                        "native-close-dispatch", frame.get("data", {}).get("openingId")
                    )
                return send(frame)

            def ui_event(owner, event):
                if (
                    observing
                    and event.get("operation") == "visible"
                    and event.get("value") is False
                ):
                    evidence.setdefault("retired", {"reason": "native-hidden", "atMs": now_ms()})
                if (
                    event.get("operation") == "languages"
                    or configuration_stage
                    and event.get("operation")
                    in (
                        "languageConfigurationBegin",
                        "languageConfigurationSave",
                        "languageConfigurationCancel",
                        "accept",
                        "beginEdit",
                    )
                ):
                    native_events.append(dict(event))
                    assert len(native_events) <= 12
                    if event.get("operation") in (
                        "languageConfigurationBegin",
                        "languageConfigurationSave",
                        "languageConfigurationCancel",
                    ):
                        configuration_note(
                            event["operation"], event.get("value", {}).get("openingId")
                        )
                overlay.event(owner, event)

            peer.link.ask = observed_ask
            peer.link.channel.send = observed_send
            input_service.ui_event = ui_event
            if configuration_stage:
                await check_language_configuration(
                    overlay,
                    peer,
                    preview,
                    source,
                    generation,
                    state,
                    initial,
                    nested,
                    dimensions,
                    evidence,
                    native_events,
                    configuration_calls,
                    begin_release,
                    configuration_stage,
                    environment,
                    output,
                    jobs,
                )
                print(
                    f"GTK4 language configuration: {configuration_stage} public flow checked",
                    flush=True,
                )
            else:
                await check_language_options(
                    overlay,
                    source,
                    generation,
                    state,
                    initial,
                    nested,
                    dimensions,
                    evidence,
                    native_events,
                    saves,
                )
                assert saves == [{"source": "ko", "target": "en"}]
                assert evidence["renewalSendCount"] > 0, (
                    "production owner did not renew the live preview"
                )
                state()  # A later render cannot hide an intermediate display retirement.
                print(
                    "GTK language options: real production owner, menus and Apply checked without a model",
                    flush=True,
                )
        finally:
            try:
                if fixture is not None:
                    evidence["finalGeometry"] = observe_click_geometry(state())
            except Exception:
                pass  # A failed job's diagnostic must not mask the failure or skip cleanup.
            observing = False
            if configuration_stage:
                overlay.cancel_language_configuration()
                evidence.pop("configurationRender", None)
                evidence["configurationCallCounts"] = {
                    key: len(value) for key, value in configuration_calls.items()
                }
            overlay.hide()
            GLib.source_remove(overlay.timer)
            for job in jobs:
                job.cancel()
            await asyncio.gather(*jobs, return_exceptions=True)
            input_service.close()
            if fixture is not None and fixture.returncode is None:
                fixture.terminate()
                try:
                    await asyncio.wait_for(fixture.wait(), 3)
                except TimeoutError:
                    fixture.kill()
                    await asyncio.wait_for(fixture.wait(), 3)
            try:
                (output / f"{state_path.stem}-click.json").write_text(
                    json.dumps(evidence, indent=2)
                )
            except (OSError, TypeError, ValueError):
                pass  # Evidence must not replace the original failure or cleanup.

    with patch.dict(os.environ, {"NIRI_SOCKET": environment["NIRI_SOCKET"]}):
        loop = GLibEventLoop(GLib.MainContext.default())
        try:
            with loop:
                loop.run_until_complete(exercise())
        finally:
            loop.close()


def check_language_dto(client, source, generation, state, initial, evidence):
    """Short native DTO rendering cases, not an idle interactive desktop owner."""
    choices = {
        "source": [{"code": "ja", "label": "日本語"}],
        "target": [{"code": "ko", "label": "한국어"}],
    }
    view = {
        "sourceLanguage": "ja",
        "targetLanguage": "ko",
        "languageChoices": choices,
        "sourceLanguageLabel": "Saved Japanese",
        "targetLanguageLabel": "Saved Korean",
    }
    cases = [dict(view), {**view, "languageChoices": {}}]
    view = {"sourceLanguage": "ja", "targetLanguage": "ko"}
    cases.extend(
        [
            {**view, "languageChoices": None},
            {**view, "languageChoices": {**choices, "target": choices["target"] * 2}},
            view,
        ]
    )
    for revision, value in enumerate(cases, 1):
        assert render_preview(
            client,
            source,
            generation,
            revision,
            "fixture exit",
            language_view=value,
            evidence=evidence,
        )["visible"]
        expected = (
            "Saved Japanese → Saved Korean"
            if revision <= 2
            else "ja（当前不可用） → ko（当前不可用）"
            if revision == 5
            else None
        )
        controls = wait_for(
            lambda: (actual := state()).get("observedAtMs", 0) > evidence["renderReturnedAtMonoMs"]
            and actual.get("overlayControls", {}).get("typelay-menu-trigger", {}).get("visible")
            and (
                expected is None
                or actual["overlayControls"]["typelay-menu-trigger"]["description"] == expected
            )
            and actual["overlayControls"],
            "language DTO title did not render",
        )
        if revision > 1:
            for name in (
                "typelay-source-language",
                "typelay-target-language",
                "typelay-apply-languages",
            ):
                assert not controls[name]["enabled"], name
        assert state()["first"] == initial["first"] and state()["second"] == initial["second"]


async def check_language_options(
    overlay, source, generation, state, initial, nested, dimensions, evidence, native_events, saves
):
    """Real menu interactions under the production owner; no installed model claim."""
    peer = overlay.input.bound_observation["peer"]
    backend = peer.backend
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
                        "surfaceGeometry",
                        "paintObservation",
                        "scrollViewport",
                        "pointerInside",
                        "pointerEnteredAtMs",
                        "pointerEnteredAtMonoMs",
                    )
                    if key in control
                }
                for name, control in value.get("overlayControls", {}).items()
                if name in (trigger, menu, source_control, target_control, apply_control)
            },
        }

    def current():
        assert overlay.current_record() is not None, "language owner lost current source binding"
        assert overlay.visible, "language preview retired during interaction"

    async def render(stage="language-renew"):
        phase = {"stage": stage, "renderStartedAtMs": now_ms()}
        phases.append(phase)
        if len(phases) > 8:
            del phases[2]
        overlay.render_guidance({"languageChoices": view.get("languageChoices")})
        overlay.render(
            {
                "state": "ValidTranslation",
                "source": source,
                "editable": False,
                "canAccept": False,
                "text": "Read-only fixture preview.",
                "status": "fixture preview",
                "sourceLanguage": view["sourceLanguage"],
                "targetLanguage": view["targetLanguage"],
                "presentation": {
                    "visible": True,
                    "editorOwnsText": False,
                    "tabHint": "fixture read-only preview",
                    "dot": "Yellow",
                },
                "sessionShortcuts": {
                    "acceptHint": "fixture accept",
                    "passThroughHint": "fixture application key",
                    "exitHint": "fixture exit",
                },
                "viewContext": {
                    "token": {
                        "target": source["target"],
                        "commandGeneration": generation,
                        "revision": 1,
                    },
                    "bindings": {"accept": "F1", "exit": "F24"},
                    "canEdit": False,
                },
            }
        )
        await language_wait_for(
            lambda: state() and not overlay.pending, "language render incomplete"
        )
        phase.update(
            revision=overlay.revision,
            renderVisible=overlay.visible,
            renderReturnedAtMs=now_ms(),
            renderReturnedAtMonoMs=time.monotonic_ns() // 1_000_000,
        )
        current()

        def shown():
            value = state()
            phase["lastObservation"] = observe(value)
            fresh = value.get("observedAtMs", 0) > phase["renderReturnedAtMonoMs"]
            if fresh:
                phase.setdefault("firstFreshObservation", observe(value))
            if fresh and value.get("overlayControls", {}).get(trigger, {}).get("visible"):
                phase["shownObservation"] = observe(value)
                return value["overlayControls"]
            return None

        return await language_wait_for(shown, "language controls did not render")

    def controls():
        actual = state()
        if phases:
            phases[-1]["lastObservation"] = observe(actual)
        return actual.get("overlayControls", {})

    async def click(name):
        current()
        phase = phases[-1]
        if name == trigger:

            def ready():
                current()
                actual = state()
                phase["triggerGeometry"] = observe(actual)
                point = language_trigger_geometry(
                    actual, phase["renderReturnedAtMonoMs"], dimensions, backend
                )
                return (actual, point) if point else None

            actual, point = await language_wait_for(
                ready, "language trigger geometry unavailable before single move"
            )
            control = actual["overlayControls"][trigger]
            instance, surface = (
                control["instance"],
                control.get("surfaceGeometry", {}).get("surfaceInstance"),
            )
            await language_command(
                "xdotool", "mousemove", "--window", nested, *map(str, point), timeout=3
            )
            moved_at = time.monotonic_ns() // 1_000_000
            phase["moveFinishedAtMonoMs"] = moved_at

            def hovered():
                current()
                actual = state()
                phase.setdefault("firstHoverObservation", observe(actual))
                phase["lastHoverObservation"] = observe(actual)
                return (
                    actual
                    if language_trigger_hover_geometry(
                        actual,
                        phase["renderReturnedAtMonoMs"],
                        dimensions,
                        backend,
                        instance,
                        surface,
                        point,
                        moved_at,
                    )
                    else None
                )

            actual = await language_wait_for(
                hovered, "language trigger did not receive real same-point hover"
            )
            arguments = ["xdotool", "click", "1"]
        else:
            actual = state()
            if backend == "gtk4":
                instances = {
                    item: actual["overlayControls"][item]["instance"] for item in (name, menu)
                }

                def ready():
                    current()
                    actual = state()
                    phase["menuClickObservation"] = observe(actual)
                    point = language_menu_geometry(
                        actual, name, phase["renderReturnedAtMonoMs"], dimensions, instances
                    )
                    return (actual, point) if point else None

                actual, point = await language_wait_for(
                    ready, "language menu geometry unavailable before single click"
                )
            else:
                control = actual["overlayControls"][name]
                assert control["visible"] and control["width"] > 0 and control["height"] > 0
                point = click_point(
                    actual,
                    (control["x"] + control["width"] / 2, control["y"] + control["height"] / 2),
                    dimensions,
                )
            arguments = ["xdotool", "mousemove", "--window", nested, *map(str, point), "click", "1"]
        current()
        phase.update(
            control=name,
            point=point,
            beforeClick=observe(actual),
            clickStartedAtMs=now_ms(),
            clickStartedAtMonoMs=time.monotonic_ns() // 1_000_000,
        )
        try:
            await language_command(*arguments, timeout=3)
            phase["clickCommandSucceeded"] = True
        finally:
            phase["clickFinishedAtMs"] = now_ms()
            phase["clickFinishedAtMonoMs"] = time.monotonic_ns() // 1_000_000

    async def toggle(opened, renew=True):
        if renew:
            await render()
        phases[-1]["requestedMenuVisible"] = opened
        await click(trigger)
        await language_wait_for(
            lambda: controls().get(menu, {}).get("visible") is opened,
            "language menu did not open/close",
        )

    async def select(name, index):
        await render()
        await click(name)
        if len(phases) in (3, 6):
            # Read the existing report once, without waiting for a fresh frame.
            # Its timestamp distinguishes a stale sample from post-click focus.
            try:
                phases[-1]["afterClickBeforeKeys"] = observe(state())
            except (OSError, ValueError, AssertionError):
                phases[-1]["afterClickObservationUnavailable"] = True
            phases[-1]["keysStartedAtMonoMs"] = time.monotonic_ns() // 1_000_000
        if backend == "gtk4":
            await language_wait_for(
                lambda: controls().get(name, {}).get("popupFocused"),
                "opened language popup did not acquire real list focus",
            )
        await language_key("Home")
        for _ in range(index):
            await language_key("Down")
        await language_key("Return")
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
            if backend in ("gtk3", "gtk4") and (
                control.get("popupVisible") is not False or control.get("buttonFocused") is not True
            ):
                return None
            if backend == "gtk3" and control.get("popupShown") is not False:
                return None
            return value

        return await language_wait_for(
            selected,
            "actual dropdown selection did not change",
        )

    async def events():
        await peer.link.ask("describe")
        await language_wait_for(
            lambda: state() and not overlay.pending, "language feedback render incomplete"
        )
        return list(native_events)

    actual = await render()
    assert actual[trigger]["description"] == "English → 日本語"
    await toggle(True)
    actual = controls()
    assert actual[source_control]["items"] == ["自动检测", "English", "日本語", "한국어"]
    assert actual[target_control]["items"] == ["English", "日本語", "한국어"]
    assert actual[source_control]["selected"] == 1 and actual[target_control]["selected"] == 1
    selected = await select(source_control, 3)
    assert selected[source_control]["focused"], "dropdown did not acquire real focus"
    before_events = await events()
    renewed = await render()
    for name in (source_control, target_control):
        for field in ("instance", "modelInstance", "selected", "focused"):
            assert renewed[name][field] == selected[name][field], (name, field)
    assert await events() == before_events, "unapplied selection was sent as a save"
    view["sourceLanguage"] = "ja"
    actual = await render()
    assert actual[source_control]["selected"] == 2, "confirmed saved code did not replace draft"
    await select(source_control, 3)
    await toggle(False)
    if backend in ("gtk3", "gtk4"):
        await language_wait_for(
            lambda: controls()[trigger]["focused"], "closing menu lost real language focus"
        )
        assert (await render())[trigger]["focused"], "collapsed renewal lost owned trigger focus"
    await toggle(True, renew=False)
    if backend in ("gtk3", "gtk4"):
        await language_wait_for(
            lambda: controls()[source_control]["focused"],
            "reopening did not restore language navigation",
        )
    assert controls()[source_control]["selected"] == 2, "reopening retained an unapplied choice"
    await select(source_control, 3)
    await select(target_control, 0)
    await render()
    apply_revision = overlay.revision
    await click(apply_control)
    await language_wait_for(
        lambda: not controls().get(menu, {}).get("visible"), "Apply did not close menu"
    )
    if backend in ("gtk3", "gtk4"):
        await language_wait_for(
            lambda: controls()[trigger]["focused"], "Apply lost owned language focus"
        )
    emitted = await events()
    assert len(emitted) == len(before_events) + 1
    assert emitted[-1]["value"] == {"source": "ko", "target": "en"}
    assert emitted[-1]["generation"] == generation and emitted[-1]["revision"] == apply_revision
    assert saves == [{"source": "ko", "target": "en"}]
    assert "未保存" in overlay.value["status"]
    # The controlled host has not confirmed the requested save. Reopening must
    # show its last confirmed state, including after a real host save failure.
    await toggle(True, renew=False)
    assert controls()[source_control]["selected"] == 2
    assert controls()[target_control]["selected"] == 1
    view.update(sourceLanguage="ko", targetLanguage="en")
    actual = await render()
    assert actual[source_control]["selected"] == 3 and actual[target_control]["selected"] == 0

    view.update(sourceLanguage="ja", targetLanguage="ko", languageChoices=choices)
    assert (await render())[trigger]["description"] == "日本語 → 한국어"
    view["languageChoices"] = {}
    unavailable = await render()
    assert unavailable[trigger]["description"] == "ja（当前不可用） → ko（当前不可用）"
    assert unavailable[trigger]["focused"], "disabling choices lost owned language focus"
    assert not unavailable[menu]["visible"]
    assert not unavailable[source_control]["enabled"]
    assert not unavailable[target_control]["enabled"]
    view["languageChoices"] = choices
    restored = await render()
    assert restored[trigger]["description"] == "日本語 → 한국어"
    assert restored[trigger]["focused"], "restored choices lost owned trigger focus"
    await toggle(True, renew=False)
    assert controls()[source_control]["focused"]
    view["sourceLanguage"] = "zz"
    actual = await render()
    assert actual[trigger]["description"] == "zz（当前不可用） → 한국어"
    assert actual[source_control]["items"][actual[source_control]["selected"]] == "zz（当前不可用）"
    assert not actual[apply_control]["enabled"]
    assert (await select(source_control, 2))[apply_control]["enabled"]
    await toggle(False)
    await toggle(True, renew=False)
    assert not controls()[apply_control]["enabled"], "unavailable confirmed choice was lost"

    if backend in ("gtk3", "gtk4"):
        before_popup_events = await events()
        await render()
        await click(source_control)
        await language_wait_for(
            lambda: controls()[source_control]["popupVisible"], "language popup did not open"
        )
        await language_key("Home")
        await language_key("Escape")
        await language_wait_for(
            lambda: (value := controls())[source_control]["focused"]
            and not value[source_control]["popupVisible"],
            "Escape did not close the language popup and return focus",
        )
        assert await events() == before_popup_events, "cancelled popup saved a language"
        await toggle(False)
        await toggle(True, renew=False)
        assert not controls()[apply_control]["enabled"], "cancel/reopen lost the saved unknown code"
        await render()
        await click(source_control)
        await language_wait_for(
            lambda: controls()[source_control]["popupVisible"], "language popup did not reopen"
        )
        await click(trigger)
        await language_wait_for(
            lambda: not controls()[source_control]["popupVisible"]
            and not controls()[menu]["visible"],
            "outside menu click left the language popup open",
        )
        await toggle(True)
        # A full 32-choice list must remain scrollable and keyboard reachable.
        view["languageChoices"] = {
            **choices,
            "source": choices["source"]
            + [{"code": f"lang-{index}", "label": f"Language {index}"} for index in range(28)],
        }
        view["sourceLanguage"] = "ja"
        await render()
        await click(source_control)
        await language_key("End")
        await language_key("Return")
        await language_wait_for(
            lambda: (value := controls())[source_control]["selected"] == 31
            and value[source_control]["focused"]
            and not value[source_control]["popupVisible"],
            "last language in a scrolling popup was not keyboard reachable",
        )
        assert await events() == before_popup_events, "unapplied scrolling choice was saved"

    before_events = await events()
    view.update(sourceLanguage="ja", targetLanguage="ko")
    for invalid in (None, {**choices, "target": choices["target"] * 2}):
        view["languageChoices"] = invalid
        actual = await render()
        for name in (source_control, target_control, apply_control):
            assert not actual[name]["enabled"], name
    view.pop("languageChoices")
    actual = await render()
    assert actual[trigger]["description"] == "ja（当前不可用） → ko（当前不可用）"
    # Invalidating the focused choices safely closes the menu onto its trigger.
    # Reopen it through the public button before testing the disabled action.
    assert not actual[menu]["visible"] and actual[trigger]["focused"]
    await toggle(True, renew=False)
    actual = controls()
    for name in (source_control, target_control, apply_control):
        assert actual[name]["visible"] and not actual[name]["enabled"], name
    await click(apply_control)
    assert await events() == before_events, "invalid choices emitted a language change"
    if backend in ("gtk3", "gtk4"):
        # With both selectors disabled, the trigger still owns focus. Close
        # before renewing; an open-menu trigger is not a retained focus proof.
        await toggle(False, renew=False)
        view["languageChoices"] = choices
        await render("restore-valid-choices")
        await toggle(True, renew=False)
        await click(source_control)
        await language_wait_for(
            lambda: controls()[source_control]["popupVisible"], "language popup did not reopen"
        )
        bounds = source["bounds"]
        point = click_point(
            state(),
            (bounds["x"] + bounds["width"] / 4, bounds["y"] + bounds["height"] / 2),
            dimensions,
        )
        current()
        await language_command(
            "xdotool", "mousemove", "--window", nested, *map(str, point), "click", "1", timeout=3
        )
        await language_wait_for(
            lambda: state().get("focus") == "fixture-first"
            and not controls().get(source_control, {}).get("popupVisible", False),
            "outside source click did not close the popup without stealing focus",
        )
        assert await events() == before_events, "outside click saved a language"
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
    if (
        not control.get("enabled")
        or control.get("scale") != actual["scale"]
        or control["width"] <= 1
        or control["height"] <= 1
    ):
        # A newly mapped GTK widget can still have its initial 1x1 allocation
        # at an in-bounds position. Do not freeze that placeholder as a hit point.
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
    # Pre-register two presentation states of this captured source. A short
    # preview must not expose Expand; the reentry action needs a genuinely long
    # preview. Neither stage retries, captures again, or renews an expired view.
    short = evidence["shortPreview"] = {}
    assert render_preview(client, source, generation, 1, "fixture exit", evidence=short)["visible"]

    def short_hidden():
        assert now_ms() <= short["expiresAtMs"], "short preview expired before hidden observation"
        actual = state()
        assert now_ms() <= short["expiresAtMs"], "short preview expired during hidden observation"
        assert actual["observedAtMs"] <= short["expiresAtMs"]
        control = actual.get("overlayControls", {}).get("fixture-expand")
        candidate = actual.get("candidate")
        if (
            actual.get("observedAtMs", 0) <= short["renderReturnedAtMonoMs"]
            or not candidate
            or candidate.get("text") != "Read-only fixture preview."
            or not control
        ):
            return None
        assert control["visible"] is False, "short preview exposed Expand"
        assert actual["focus"] == "fixture-first"
        assert actual["first"] == initial["first"] and actual["second"] == initial["second"]
        return actual

    hidden = wait_for(short_hidden, "current short preview/hidden Expand not observed")
    short["observedAtMonoMs"] = hidden["observedAtMs"]
    short["expandVisible"] = hidden["overlayControls"]["fixture-expand"]["visible"]
    long_text = "\n".join(["A longer translated line for the expand callback."] * 8)
    assert render_preview(
        client, source, generation, 2, "fixture exit", evidence=evidence, text=long_text
    )["visible"]
    ready = runtime / "expand-ready"

    def allocated_button():
        actual = state()
        point = expand_click_point(actual, evidence, dimensions, now_ms())
        return (
            (actual, point)
            if ready.exists() and (actual.get("candidate") or {}).get("text") == long_text and point
            else None
        )

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
        client, source, generation, 3, "fixture exit", view_state="ReturningToSource"
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
