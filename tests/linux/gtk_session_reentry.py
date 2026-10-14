"""Real GTK callback reentry through the public native socket, without a model."""

import hashlib
import json
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


def run_reentry_checks(command, environment, output, send_accept, *, gtk4, nested):
    # Both cases dispatch the same real configure command inside the callback.
    # Only the generation changes, so nested IME dispatch cannot fake a pass.
    stages = ["delete", "insert", "selection", "footer", "expand"]
    if gtk4:
        stages.extend(["controller-source", "controller-editor", "resume-editor"])
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

                initial = wait_for(
                    lambda: (value := state())
                    and value.get("focus") == "fixture-first"
                    and value.get("imeClear")
                    and value,
                    f"{name}: actual source IME did not become Clear",
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
                if stage == "footer":
                    check_footer(client, source, generation, runtime, state, initial)
                    print(f"{name}: real footer notify hid the overlay safely", flush=True)
                    return

                if stage == "expand":
                    check_expand(client, source, generation, runtime, state, initial, nested)
                    print(f"{name}: old click did not acquire the newer view revision", flush=True)
                    return
                if stage.startswith("controller-"):
                    check_controller(client, source, generation, runtime, state, initial, stage)
                    print(f"{name}: detached actual consumer no longer acknowledged", flush=True)
                    return
                if stage == "resume-editor":
                    check_resume_editor(client, source, generation, state, initial, nested)
                    print(
                        f"{name}: authorized recovery restored actual editor focus without writing",
                        flush=True,
                    )
                    return

                def publication(source, generation, replacement):
                    expected = snapshot(source, client.hello, anchor_geometry(source["geometry"]))
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
                assert client.call("publish", published) == {"published": True}, name
                key_held = True
                send_accept(True)  # One down only; never retry or renew this permit.
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
                fixture.terminate()
                try:
                    fixture.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    fixture.kill()
                    fixture.wait(timeout=3)


def render_preview(
    client, source, generation, revision, label, *, view_state="ValidTranslation", can_edit=False
):
    return client.call(
        "overlay-render",
        {
            "target": source["target"],
            "authority": source["authority"],
            "generation": generation,
            "revision": revision,
            "expiresAtMs": now_ms() + 800,
            "theme": "system",
            "view": {
                "state": view_state,
                "editable": can_edit,
                "text": "Read-only fixture preview.",
                "status": "fixture preview",
                "sourceLanguage": "en",
                "targetLanguage": "zh",
                "presentation": {
                    "visible": True,
                    "editorOwnsText": view_state in ("EditingTranslation", "ReturningToSource"),
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


def check_expand(client, source, generation, runtime, state, initial, nested):
    assert render_preview(client, source, generation, 1, "fixture exit")["visible"]
    ready = runtime / "expand-ready"
    wait_for(lambda: ready.exists() and state().get("candidate"), "real expand button missing")
    x, y, scale = map(float, ready.read_text().split())
    (runtime / "arm").touch()
    subprocess.run(
        [
            "xdotool",
            "mousemove",
            "--window",
            nested,
            str(int(x * scale)),
            str(int(y * scale)),
            "click",
            "1",
        ],
        check=True,
        timeout=3,
    )
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


def check_resume_editor(client, source, generation, state, initial, nested):
    assert render_preview(client, source, generation, 1, "fixture exit")["visible"]
    described = client.describe()
    assert described["target"] == source["target"] and described["sessionBindings"]["editorReady"]
    source = client.call(
        "capture", {"target": described["target"], "authority": described["authority"]}
    )
    assert source["sessionBindings"] == described["sessionBindings"]
    assert render_preview(client, source, generation, 2, "fixture exit", can_edit=True)["visible"]
    actual = wait_for(lambda: (value := state()).get("candidate") and value, "real preview missing")
    candidate, scale = actual["candidate"], actual["scale"]
    subprocess.run(
        [
            "xdotool",
            "mousemove",
            "--window",
            nested,
            str(int((candidate["x"] + candidate["width"] / 2) * scale)),
            str(int((candidate["y"] + candidate["height"] / 2) * scale)),
            "click",
            "1",
        ],
        check=True,
        timeout=3,
    )

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
