"""Real GTK Unknown-IME refusal in niri; GTK translation remains unsupported."""

import argparse
import faulthandler
import json
import os
import signal
import socket
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from geometry import anchor_geometry  # noqa: E402
from niri import contents, request  # noqa: E402
from paths import Paths, peer_credentials  # noqa: E402
from wire import encode, now_ms, receive_sync  # noqa: E402


class Native:
    def __init__(self, connection):
        self.connection = connection
        self.connection.settimeout(2)
        self.id = 0
        self.events = []
        first = receive_sync(connection)
        assert first["event"] == "hello" and first["data"]["protocol"] == 2
        self.hello = first["data"]

    def call(self, operation, data=None):
        self.id += 1
        self.connection.sendall(encode({"id": self.id, "op": operation, "data": data or {}}))
        while True:
            frame = receive_sync(self.connection)
            if frame.get("id") == self.id:
                self.capture_trace = frame.get("captureTrace")
                self.capture_continuity = frame.get("captureContinuity")
                return frame["data"]
            self.events.append(frame)

    def describe(self, target=None):
        result = self.call("describe", {"target": target} if target else {})
        assert result is not None, "real focused GTK field unavailable"
        return result


def key(value, *, timing=None):
    if timing is not None:
        timing["xdotoolStartedAtMs"] = now_ms()
    subprocess.run(
        ["xdotool", "key", "--clearmodifiers", "--delay", "0", value], check=True, timeout=2
    )
    if timing is not None:
        timing["xdotoolReturnedAtMs"] = now_ms()
    time.sleep(0.025)
    if timing is not None:
        timing["postKeySleepFinishedAtMs"] = now_ms()


def snapshot(native, hello, geometry):
    return {
        "target": {
            "backend": "linux-gtk3",
            "value": native["target"],
            "lifetime": hello["lifetime"],
        },
        "text": native["text"],
        "selection": native["selection"],
        "bounds": geometry["bounds"],
        "multiline": native["metadata"]["multiline"],
        "access": "Supported",
        "ime": native["ime"],
        "observedAtMs": native["observedAtMs"],
        "inputEpoch": native["authority"]["inputRevision"],
        "focusEpoch": native["authority"]["focusRevision"],
        "writeSupported": True,
        "directWriteSupported": True,
    }


def plan(source, replacement, scope=None):
    text = source["text"].encode("utf-16-le")
    first, last = (scope["start"], scope["end"]) if scope else (0, len(text) // 2)
    result = (text[: first * 2] + replacement.encode("utf-16-le") + text[last * 2 :]).decode(
        "utf-16-le"
    )
    return {
        "id": str(uuid.uuid4()),
        "sessionId": str(uuid.uuid4()),
        "revision": 1,
        "expected": source,
        "scope": scope,
        "replacement": replacement,
        "resultText": result,
        "caret": first + len(replacement.encode("utf-16-le")) // 2,
    }


def inside(arguments, notifications=None):
    # niri may detach spawned programs from its own diagnostic stream.
    log = "inside.log" if notifications is None else f"inside-notifications-{notifications}.log"
    sys.stdout = sys.stderr = (arguments.output / log).open("w", buffering=1)
    faulthandler.dump_traceback_later(15, file=sys.stderr, repeat=False)
    paths = Paths(ROOT)
    paths.native.unlink(missing_ok=True)
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(str(paths.native))
    paths.native.chmod(0o600)
    server.listen(4)
    server.settimeout(10)
    environment = {
        **os.environ,
        "GTK3_MODULES": str(arguments.native / "libtypelay-gtk3.so"),
        "GDK_BACKEND": "wayland",
        "TYPELAY_TRACE": "1",
    }
    environment.pop("TYPELAY_DESKTOP", None)
    if notifications is not None:
        environment["TYPELAY_FIXTURE_INPUT_NOTIFICATIONS"] = notifications
    control_path = arguments.output / "direct-control.json"
    if notifications == "direct":
        control_path.unlink(missing_ok=True)
        environment["TYPELAY_FIXTURE_DIRECT_CONTROL"] = str(control_path)
    state_path = arguments.output / "fixture.json"
    state_path.unlink(missing_ok=True)
    environment["TYPELAY_FIXTURE_STATE"] = str(state_path)
    fixture = subprocess.Popen([str(arguments.native / "gtk-fixture")], env=environment)
    checks = []
    connection = None
    try:
        connection, _ = server.accept()
        assert peer_credentials(connection)[0] == fixture.pid
        print("native peer connected", flush=True)
        client = Native(connection)
        windows = []
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            windows = contents(request("Windows"), "Windows")
            window = next((item for item in windows if item.get("pid") == fixture.pid), None)
            if window:
                request({"Action": {"FocusWindow": {"id": window["id"]}}})
                break
            time.sleep(0.05)
        assert window is not None
        # XTest enters the nested compositor, which sends normal Wayland key events.
        # This is a dedicated Xvfb containing exactly one nested compositor,
        # not production target identification. niri does not publish an X11
        # WM_CLASS or _NET_WM_PID on all winit versions, but sets this title.
        os.environ["DISPLAY"] = os.environ["TYPELAY_TEST_DISPLAY"]
        nested = subprocess.check_output(
            ["xdotool", "search", "--onlyvisible", "--name", "^niri$"],
            text=True,
            timeout=2,
        ).splitlines()
        assert len(nested) == 1, "nested compositor window is not uniquely identified"
        subprocess.run(["xdotool", "windowfocus", "--sync", nested[0]], check=True, timeout=2)
        time.sleep(0.2)
        print("focused fixture", flush=True)

        def state():
            return json.loads(state_path.read_text()) if state_path.exists() else {}

        def wait_state(predicate):
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                value = state()
                if predicate(value):
                    return value
                time.sleep(0.025)
            raise AssertionError("fixture state did not reach the expected input/focus")

        field = "fixture-first" if notifications is None else "fixture-multiline"
        initial = wait_state(lambda value: value.get("focus") == field)
        original = initial["first"] if notifications is None else initial["multiline"]["text"]
        description = client.describe()
        assert description["metadata"]["automationId"] == field
        if notifications == "direct":
            checks = direct_epoch_contract(client, description, state, wait_state, control_path)
            (arguments.output / "direct-epoch-result.json").write_text(json.dumps(checks, indent=2))
            return
        if notifications is not None:
            notification_contract(client, description, notifications, original, state)
            (arguments.output / f"notification-{notifications}-result.json").write_text(
                json.dumps(
                    {
                        "scope": "controlled-notification-contract",
                        "mode": notifications,
                        "passed": True,
                    }
                )
            )
            return
        assert description["metadata"]["hasTextEdit"] is False
        assert "text" not in description and "selection" not in description
        assert original not in json.dumps(description, ensure_ascii=False)
        target = description["target"]
        assert description.get("sessionBindings") is None, (
            "unconfigured GTK claimed session key ownership"
        )
        geometry = anchor_geometry(description["geometry"])
        assert geometry is not None, "refusal must exercise a visible ordinary GTK field"
        assert (
            client.call(
                "capture",
                {"target": target, "authority": description["authority"], "ownedSource": True},
            )
            is None
        ), "Unknown IME exposed source text"
        assert client.capture_trace == {"reason": "ime-unavailable"}, (
            "first-guard refusal must not report unobserved identity or revision fields"
        )
        assert client.capture_continuity is None, "Unknown IME cannot retain an owned source intent"
        checks.append("visible GTK metadata excludes body; Unknown IME capture returns no snapshot")

        paused = client.call(
            "overlay-render",
            {
                "target": target,
                "authority": description["authority"],
                "revision": 1,
                "expiresAtMs": now_ms() + 800,
                "view": {
                    "state": "Paused",
                    "pauseRevision": 1,
                    "canAccept": False,
                    "presentation": {
                        "kind": "Paused",
                        "visible": True,
                        "status": "已暂停",
                        "tabHint": "点击恢复",
                        "editorOwnsText": False,
                        "showEscape": False,
                    },
                },
            },
        )
        assert paused == {"visible": False, "editing": False}, paused
        assert not any(
            event.get("event") == "ui" and event["data"].get("operation") == "resume"
            for event in client.events
        ), "Unknown IME exposed a resume action"
        checks.append("metadata-only paused view keeps the real Unknown-IME refusal")

        # This intentionally dishonest request uses fixture-owned text, never a
        # successful native capture. Even a caller claiming Clear cannot grant IME authority.
        caret = len(original.encode("utf-16-le")) // 2
        forged = {
            **description,
            "text": original,
            "selection": {"start": caret, "end": caret},
            "ime": "Clear",
            "observedAtMs": now_ms(),
        }
        write = plan(snapshot(forged, client.hello, geometry), "must not replace")
        rejected = client.call(
            "direct",
            {
                "target": target,
                "plan": write,
                "authority": description["authority"],
                "approved": description["authority"],
                "expiresAtMs": now_ms() + 250,
            },
        )
        assert rejected == {"status": "RejectedBeforeMutation", "reason": "EpochChanged"}, rejected
        # Trace is enabled in this fixture, but the public default schema stays
        # exact. Only a typed opt-in may add read-only diagnostics.
        diagnostic_request = {
            "target": target,
            "plan": write,
            "authority": description["authority"],
            "approved": description["authority"],
            "expiresAtMs": now_ms() + 250,
        }
        for invalid in (False, "true", 1, None, {}, []):
            result = client.call("direct", {**diagnostic_request, "diagnostic": invalid})
            assert result == {"status": "RejectedBeforeMutation", "reason": "EpochChanged"}, result
        traced = client.call("direct", {**diagnostic_request, "diagnostic": True})
        assert (
            traced["status"] == "RejectedBeforeMutation" and traced["reason"] == "EpochChanged"
        ), traced
        assert traced["diagnostic"]["phase"] == "generation-before-ime", traced
        assert set(traced) == {"status", "reason", "diagnostic"}, traced
        generation = str(uuid.uuid4())
        client.call(
            "configure-shortcuts",
            {
                "enabled": True,
                "generation": generation,
                "sessionEnabled": True,
                "accept": "F1",
                "exit": "F24",
                "wake": {},
                "direct": {},
            },
        )
        applied = client.describe(target)
        assert applied["sessionBindings"] == {
            "generation": generation,
            "accept": "F1",
            "exit": "F24",
            "sourceReady": True,
            "editorReady": False,
        }, "real GTK target did not confirm the installed canonical pair"
        assert applied["authority"]["sessionBindings"] == applied["sessionBindings"]
        rejected = client.call(
            "direct",
            {
                "target": target,
                "generation": generation,
                "plan": write,
                "authority": applied["authority"],
                "approved": applied["authority"],
                "expiresAtMs": now_ms() + 250,
            },
        )
        assert rejected == {"status": "RejectedBeforeMutation", "reason": "ImeUnclear"}, rejected
        for kind in ("wake", "direct"):
            current = client.describe(target)
            lease = {"target": target, "authority": current["authority"], "generation": generation}
            client.call("publish-shortcut", lease)
            before_events = len(client.events)
            assert client.call(
                "trigger-shortcut",
                {
                    **lease,
                    "kind": kind,
                    "triggeredAtMs": now_ms(),
                },
            ) == {"triggered": False}, "Unknown IME allowed a shortcut read"
            if kind == "wake":
                feedback = [
                    event
                    for event in client.events[before_events:]
                    if event.get("event") in ("shortcut-started", "shortcut-rejected")
                ]
                assert [event["event"] for event in feedback] == [
                    "shortcut-started",
                    "shortcut-rejected",
                ], "native CLI refusal did not use the ordered feedback protocol"
                started, refused = (event["data"] for event in feedback)
                assert started["commandId"] > 0
                assert refused["commandId"] == started["commandId"]
                assert refused["kind"] == kind and refused["generation"] == generation
                assert refused["reason"] == "ImeUnclear"
                assert "native" not in refused and "source" not in refused
                assert original not in json.dumps(feedback, ensure_ascii=False)
        checks.append("caller-supplied Clear cannot authorize direct write or wake/direct reads")
        checks.append("native CLI Unknown-IME refusal reports one ordered source-free result")

        current = client.describe(target)
        write = plan(snapshot({**forged, **current}, client.hello, geometry), "must not replace")
        client.call(
            "publish",
            {
                "target": target,
                "authority": current["authority"],
                "generation": generation,
                "lease": {
                    "source": write["expected"],
                    "escapeAllowed": True,
                    "commandGeneration": generation,
                    "sessionRevision": 1,
                },
                "permit": {"plan": write, "expiresAtMs": now_ms() + 100},
            },
        )
        for name, index in (("F1", 0), ("F24", 23)):
            before = state()["functionKeys"][index]
            key(name)
            observed = wait_state(lambda value: value["functionKeys"][index]["up"] > before["up"])
            assert observed["functionKeys"][index]["down"] > before["down"]
            assert observed["first"] == original
        key("Tab")
        after_tab = wait_state(lambda value: value.get("focus") == "fixture-second")
        assert after_tab["first"] == original and after_tab["second"] == initial["second"]
        client.describe()  # Drain events through a subsequent real protocol response.
        assert not any(event.get("event") in ("accepted", "shortcut") for event in client.events)
        key("shift+Tab")
        wait_state(lambda value: value.get("focus") == "fixture-first")
        control_generation = str(uuid.uuid4())
        client.call(
            "configure-shortcuts",
            {
                "enabled": False,
                "sessionEnabled": True,
                "generation": control_generation,
                "accept": "Ctrl+F7",
                "exit": "Ctrl+F8",
                "wake": {},
                "direct": {},
            },
        )
        unsupported = client.describe(target)["sessionBindings"]
        assert unsupported["sourceReady"] is False and unsupported["editorReady"] is False
        for name, index in (("F7", 6), ("F8", 7)):
            before = state()["functionKeys"][index]
            key("ctrl+" + name)
            observed = wait_state(lambda value: value["functionKeys"][index]["up"] > before["up"])
            assert observed["functionKeys"][index]["down"] > before["down"]
            assert observed["first"] == original
        client.describe(target)
        assert not any(event.get("event") == "accepted" for event in client.events)
        assert not any(
            event.get("event") == "ui" and event["data"].get("operation") in ("return", "escape")
            for event in client.events
        )
        checks.append(
            "Unknown IME keeps Ctrl source/editor unavailable and both original key pairs pass"
        )
        key("End")
        subprocess.run(["xdotool", "type", "--clearmodifiers", "x"], check=True, timeout=2)
        wait_state(lambda value: value.get("first") == original + "x")
        current = client.describe(target)
        assert current["metadata"]["hasTextEdit"] is False
        assert client.call("capture", {"target": target, "authority": current["authority"]}) is None
        checks.append(
            "Tab belongs to GTK, text remains unchanged by TypeLay, ordinary typing stays Unknown"
        )
        replacement_generation = str(uuid.uuid4())
        client.call(
            "configure-shortcuts",
            {
                "enabled": False,
                "sessionEnabled": True,
                "generation": replacement_generation,
                "accept": "Tab",
                "exit": "Esc",
                "wake": {},
                "direct": {},
            },
        )
        reapplied = client.describe(target)
        assert reapplied["sessionBindings"]["generation"] == replacement_generation
        assert reapplied["sessionBindings"]["accept"] == "Tab"
        assert reapplied["sessionBindings"]["sourceReady"] is True, (
            "automatic mode incorrectly disabled session key ownership"
        )
        assert client.call(
            "return",
            {"target": target, "authority": current["authority"], "generation": generation},
        ) == {"returned": False}
        stale = client.call(
            "direct",
            {
                "target": target,
                "generation": generation,
                "plan": write,
                "authority": reapplied["authority"],
                "approved": reapplied["authority"],
                "expiresAtMs": now_ms() + 250,
            },
        )
        assert stale == {"status": "RejectedBeforeMutation", "reason": "EpochChanged"}
        client.call(
            "configure-shortcuts",
            {
                "enabled": False,
                "sessionEnabled": True,
                "generation": str(uuid.uuid4()),
                "accept": "F7",
                "exit": "F7",
                "wake": {},
                "direct": {},
            },
        )
        assert client.describe(target).get("sessionBindings") is None, (
            "invalid session pair silently fell back or advertised readiness"
        )
        checks.append(
            "target-specific canonical ACK, automatic-mode session bindings, stale generation refusal and invalid-pair refusal"
        )

        (arguments.output / "native-unknown-result.json").write_text(
            json.dumps(
                {
                    "scope": "gtk-unknown-ime-refusal-only",
                    "passed": checks,
                    "unsupported": [
                        "GTK source capture, translation, overlay editing and acceptance"
                    ],
                },
                indent=2,
            )
        )
    finally:
        fixture.terminate()
        try:
            fixture.wait(timeout=2)
        except subprocess.TimeoutExpired:
            fixture.kill()
            fixture.wait()
        if connection is not None:
            connection.close()
        faulthandler.cancel_dump_traceback_later()
        server.close()
        paths.native.unlink(missing_ok=True)


def direct_epoch_contract(client, description, state, wait_state, control_path):
    """Production requests against a controlled IM port, not real IME or native Undo evidence."""
    generation = str(uuid.uuid4())
    client.call(
        "configure-shortcuts",
        {
            "enabled": True,
            "generation": generation,
            "sessionEnabled": False,
            "wake": {"keyval": 0xFFC6, "modifiers": 0},
            "direct": {},
        },
    )
    target = description["target"]
    sequence = 0

    def send(action, *, rejected=False, repeated=False):
        nonlocal sequence
        before = state().get("directControl", {}).get("rejected", 0)
        number = sequence if repeated else sequence + 1
        pending = control_path.with_suffix(".tmp")
        pending.write_text(json.dumps({"sequence": number, "action": action}))
        pending.replace(control_path)
        if rejected:
            value = wait_state(lambda v: v.get("directControl", {}).get("rejected") == before + 1)
            assert value["directControl"]["sequence"] == sequence
        else:
            sequence = number
            value = wait_state(lambda v: v.get("directControl", {}).get("sequence") == sequence)
        return value

    cases = []
    for mode in (
        "epoch",
        "input",
        "context",
        "serial",
        "composition",
        "focus",
        "body",
        "selection",
        "rollback",
        "prepare-input",
        "stale-approved",
        "missing-basis",
        "missing-approved",
        "approved-input",
        "approved-content",
        "approved-focus",
        "approved-epoch",
        "future-approved",
        "malformed-basis",
    ):
        send("restore")
        current = client.describe(target)
        geometry = anchor_geometry(current["geometry"])
        assert geometry is not None
        native = client.call("capture", {"target": target, "authority": current["authority"]})
        assert native is not None and native["ime"] == "Clear", (mode, client.capture_trace)
        assert native["text"] == "Controlled original source"
        write = plan(snapshot(native, client.hello, geometry), "Controlled replacement")
        before = send(
            mode
            if mode
            not in (
                "stale-approved",
                "missing-basis",
                "missing-approved",
                "approved-input",
                "approved-content",
                "approved-focus",
                "approved-epoch",
                "future-approved",
                "malformed-basis",
            )
            else "epoch"
        )
        # Read the real current approval only after the controlled provider event has completed.
        # No forged old epoch in the positive case and no focus restoration in the refusal case.
        fresh = client.describe(target)
        approved = fresh["authority"]
        if mode == "epoch":
            old_stamp = native["authority"]["contextStamp"]
            new_stamp = approved["contextStamp"]
            assert fresh["focused"] and new_stamp["epoch"] > old_stamp["epoch"]
            assert {k: v for k, v in old_stamp.items() if k != "epoch"} == {
                k: v for k, v in new_stamp.items() if k != "epoch"
            }
            for field in ("inputRevision", "contentRevision", "focusRevision"):
                assert native["authority"][field] == approved[field], field
        if mode == "stale-approved":
            approved["observedAtMs"] = -(2**63)
        if mode == "missing-basis":
            del native["authority"]["contextStamp"]["input"]
        if mode == "malformed-basis":
            native["authority"]["contextStamp"]["input"] = str(
                native["authority"]["contextStamp"]["input"]
            )
        if mode == "missing-approved":
            del approved["contextStamp"]["applied"]
        if mode in ("approved-input", "approved-content", "approved-focus"):
            approved[mode.removeprefix("approved-") + "Revision"] += 1
        if mode == "approved-epoch":
            approved["contextStamp"]["epoch"] += 1
        if mode == "future-approved":
            approved["observedAtMs"] = now_ms() + 1000
        result = client.call(
            "direct",
            {
                "target": target,
                "plan": write,
                "authority": native["authority"],
                "approved": approved,
                "generation": generation,
                "expiresAtMs": now_ms() + 250,
                "diagnostic": True,
            },
        )
        if mode == "epoch":
            assert result["status"] == "Verified", (mode, result)
            verified = result["verifiedSnapshot"]
            assert verified["text"] == write["resultText"]
            assert verified["selection"] == {"start": write["caret"], "end": write["caret"]}
            after = wait_state(lambda v: v.get("multiline", {}).get("text") == write["resultText"])
            assert (after["multiline"]["start"], after["multiline"]["end"]) == (
                write["caret"],
                write["caret"],
            )
            assert after["userActionsStarted"] == before["userActionsStarted"] + 1
            assert after["userActionsFinished"] == before["userActionsFinished"] + 1
        else:
            assert result["status"] == "RejectedBeforeMutation", (mode, result)
            if mode == "composition":
                assert result["reason"] == "ImeUnclear", (mode, result)
            elif mode == "prepare-input":
                assert result["reason"] == "SourceChanged", (mode, result)
            else:
                assert result["reason"] == "EpochChanged", (mode, result)
                assert result.get("diagnostic", {}).get("phase") == "authority", (mode, result)
            # A fresh report barrier observes any actual native buffer callback, not a stale file.
            after = send("prepare-input")
            assert after["multiline"] == before["multiline"], (mode, result)
            assert after["userActionsStarted"] == after["userActionsFinished"]
        cases.append(
            {
                "case": mode,
                "status": result["status"],
                "reason": result["reason"],
                "diagnostic": result.get("diagnostic"),
            }
        )
    before = send("restore")
    before_authority = client.describe(target)["authority"]
    for action, repeated in (("unknown", False), ("epoch", True)):
        after = send(action, rejected=True, repeated=repeated)
        assert after["multiline"] == before["multiline"]
        after_authority = client.describe(target)["authority"]
        for field in ("contextStamp", "inputRevision", "contentRevision", "focusRevision"):
            assert after_authority[field] == before_authority[field], (action, field)
    return {
        "scope": "controlled-textview-direct-epoch",
        "cases": cases,
        "controlRejectsUnknownAndRepeated": True,
        "nativeUndoTested": False,
    }


def notification_contract(client, description, mode, original, state):
    generation = str(uuid.uuid4())
    client.call(
        "configure-shortcuts",
        {
            "enabled": True,
            "generation": generation,
            "sessionEnabled": False,
            "wake": {"keyval": 0xFFC6, "modifiers": 0},
            "direct": {},
        },
    )
    current = client.describe(description["target"])
    assert current["authority"]["contextStamp"]["flags"] == 3  # Controlled Known|Focused.
    client.call(
        "publish-shortcut",
        {
            "target": current["target"],
            "authority": current["authority"],
            "generation": generation,
        },
    )
    # One ordered key sequence, no intervening host probe to refresh the cache.
    subprocess.run(
        ["xdotool", "key", "--clearmodifiers", "--delay", "0", "F6", "F9"], check=True, timeout=2
    )
    while True:
        frame = receive_sync(client.connection)
        if frame.get("event") in ("shortcut", "shortcut-rejected"):
            break
    expected = "shortcut" if mode in ("notified", "nested-complete") else "shortcut-rejected"
    assert frame["event"] == expected, (mode, frame)
    if expected == "shortcut":
        assert frame["data"]["native"]["text"] == original
    else:
        assert frame["data"]["reason"] == "EpochChanged", frame
        assert frame["data"]["diagnostic"]["phase"] == "ime", frame
        assert frame["data"]["diagnostic"]["gate"] == "input-revision", frame
    assert state()["multiline"]["text"] == original, "controlled notification mutated source"


def quality_exit(arguments, compositor, work_deadline, result, failure):
    from support.translation_quality_capture import (
        CLEANUP_SECONDS,
        collections,
        inspect_report,
        process_identity,
        process_status,
    )

    expected_collections = collections(
        getattr(arguments, "hy_decode_comparison", False),
        getattr(arguments, "fixed_model_diagnostics", False),
    )
    cancelled = False
    signalled = False
    owner = None
    cleanup_deadline = work_deadline + CLEANUP_SECONDS - 3

    def interrupt(signum, frame):
        nonlocal cancelled, cleanup_deadline
        cancelled = True
        cleanup_deadline = min(cleanup_deadline, time.monotonic() + CLEANUP_SECONDS - 3)

    handlers = {signum: signal.getsignal(signum) for signum in (signal.SIGINT, signal.SIGTERM)}
    for signum in handlers:
        signal.signal(signum, interrupt)
    try:
        while time.monotonic() < cleanup_deadline:
            if owner is None:
                try:
                    owner = json.loads((arguments.output / "quality-inside-owner.json").read_text())
                    assert type(owner["pid"]) is int and owner["pid"] > 1
                    assert isinstance(owner["startTime"], str) and owner["startTime"].isdigit()
                except (OSError, ValueError, KeyError, TypeError, AssertionError):
                    owner = None  # Missing/partial/invalid owner is never permission to signal.
            terminated = False
            try:
                status = process_status(owner["pid"]) if owner is not None else None
                identity = status[0] if status is not None else None
                # An exact unreaped inside process has exited, but its workers
                # still require every independent exit proof below.
                terminated = owner is not None and status == (owner["startTime"], "Z")
            except (OSError, ValueError, IndexError):
                identity = "unknown"  # Read failure cannot prove the recorded process exited.
            barrier = result.exists() or failure.exists()
            if (
                owner is not None
                and barrier
                and (terminated or identity not in ("unknown", owner["startTime"]))
            ):
                report = json.loads((arguments.output / "hy-package-result.json").read_text())
                quality = report["translationQuality"]
                assert report["hostExitConfirmed"] is True, "host exit unconfirmed; preserve data"
                assert report["workerExitConfirmed"] is True, (
                    "serving exit unconfirmed; preserve data"
                )
                assert quality["workerExitConfirmed"] is True, (
                    "quality exit unconfirmed; preserve data"
                )
                for item in quality["collections"]:
                    original, checked = inspect_report(
                        item["runner"], arguments.output / f"{item['runner']}-quality.json"
                    )
                    assert original["workerExitConfirmed"] is True, (
                        "runner exit unconfirmed; preserve data"
                    )
                    expected_count = expected_collections[item["runner"]]
                    assert (
                        original["status"] == "completed"
                        and original["workerStarts"] == 1
                        and original["workerExitCode"] == 0
                        and checked["actualTranslationRequests"] == expected_count
                        and len(original["requests"]) == expected_count + 1
                    ), "individual runner response or exit evidence incomplete"
                assert not cancelled and not failure.exists() and report["status"] == "passed", (
                    "quality lifecycle failed; preserve its reports and isolated data"
                )
                assert [item["runner"] for item in quality["collections"]] == list(
                    expected_collections
                )
                assert (
                    quality["actualTranslationRequests"] == sum(expected_collections.values())
                    and quality["requestCountComplete"]
                )
                return
            if not cancelled and (
                time.monotonic() >= work_deadline or compositor.poll() is not None
            ):
                # Compositor exit is not a model-process exit proof. Address only
                # this run's recorded inside process, even if niri already exited.
                cancelled = True
                cleanup_deadline = min(cleanup_deadline, time.monotonic() + CLEANUP_SECONDS - 3)
            if (
                cancelled
                and not signalled
                and not terminated
                and owner is not None
                and identity == owner["startTime"]
            ):
                # Recheck immediately before signalling; never chase a reused PID.
                try:
                    if process_identity(owner["pid"]) == owner["startTime"]:
                        os.kill(owner["pid"], signal.SIGTERM)
                        signalled = True
                except (OSError, ValueError, IndexError):
                    pass
            time.sleep(0.05)
        raise RuntimeError("quality inside exit unconfirmed; preserve isolated data and reports")
    finally:
        for signum, handler in handlers.items():
            signal.signal(signum, handler)


def outer(arguments, script=None):
    if not arguments.desktop or arguments.desktop_scope not in (
        "setup",
        "setup-settings",
        "setup-tutorial",
        "ordinary-gimp",
    ):
        return run_outer(arguments, script)
    arguments.output.mkdir(parents=True, exist_ok=True)
    report = arguments.output / "scope-result.json"
    result = {"schema": 1, "scope": arguments.desktop_scope, "status": "not-run"}

    def record(status):
        result["status"] = status
        pending = report.with_suffix(".json.tmp")
        pending.write_text(json.dumps(result, indent=2))
        pending.replace(report)

    record("not-run")
    try:
        run_outer(arguments, script)
    except BaseException:
        try:
            record("failed")
        except OSError:
            pass  # Preserve the original preparation, execution or cleanup failure.
        raise
    # The inside success receipt and the existing compositor cleanup have both
    # completed. This separate report is never an inside completion signal.
    record("passed")


def run_outer(arguments, script=None):
    arguments.output.mkdir(parents=True, exist_ok=True)
    quality = getattr(arguments, "collect_translation_quality", False)
    if quality and any(arguments.output.iterdir()):
        raise RuntimeError(
            "quality capture requires a fresh output directory; preserve prior results"
        )
    result_name = "desktop-result.json" if arguments.desktop else "native-result.json"
    (arguments.output / result_name).unlink(missing_ok=True)
    failure = arguments.output / "inside.failed"
    failure.unlink(missing_ok=True)
    runtime = arguments.output / "runtime"
    runtime.mkdir(mode=0o700, exist_ok=True)
    config = arguments.output / "niri.kdl"
    command = [
        "/usr/bin/python3",
        "-I",
        # Every scope reads the same immutable bundle. Earlier setup imports
        # must not leave bytecode for a later strict quality identity check.
        "-B",
        str(Path(script or __file__).resolve()),
        "--inside",
        "--native",
        str(arguments.native),
        "--output",
        str(arguments.output),
    ]
    quality_deadline = None
    if quality:
        from support.translation_quality_capture import WORK_SECONDS

        quality_deadline = time.monotonic() + WORK_SECONDS
        command.extend(
            ["--collect-translation-quality", "--quality-work-deadline", str(quality_deadline)]
        )
    if getattr(arguments, "fixed_model_diagnostics", False):
        command.append("--fixed-model-diagnostics")
    if getattr(arguments, "hy_decode_comparison", False):
        command.append("--hy-decode-comparison")
    if getattr(arguments, "gtk_refusal", False):
        command.append("--gtk-refusal")
    if getattr(arguments, "gtk_session_bindings", False):
        command.append("--gtk-session-bindings")
    if arguments.desktop:
        command.extend(
            [
                "--desktop",
                "--package",
                str(arguments.package),
                "--desktop-scope",
                arguments.desktop_scope,
            ]
        )
        if arguments.fcitx_cooperative:
            command.extend(["--fcitx-cooperative", str(arguments.fcitx_cooperative)])
        if arguments.qt_cooperative:
            command.extend(["--qt-cooperative", str(arguments.qt_cooperative)])
        if arguments.editor_bundle:
            command.extend(["--editor-bundle", str(arguments.editor_bundle)])
    # The compositor's first-run help owns keyboard focus independently of its
    # focused-window IPC state. This disposable test session has no help UI.
    config.write_text(
        "hotkey-overlay { skip-at-startup; }\n"
        + (
            "animations { off; }\n"
            if getattr(arguments, "desktop_scope", None) == "ordinary-gimp"
            else ""
        )
        + "spawn-at-startup "
        + " ".join(json.dumps(item, ensure_ascii=False) for item in command)
        + "\n"
    )
    environment = {
        **os.environ,
        "XDG_RUNTIME_DIR": str(runtime),
        "GDK_BACKEND": "wayland",
        "LIBGL_ALWAYS_SOFTWARE": "1",
        "TYPELAY_TEST_DISPLAY": os.environ["DISPLAY"],
    }
    environment.pop("WAYLAND_DISPLAY", None)
    environment.pop("NIRI_SOCKET", None)
    environment.pop("TYPELAY_DESKTOP", None)
    with (arguments.output / "nested-niri.log").open("w") as log:
        compositor = subprocess.Popen(
            ["dbus-run-session", "--", "niri", "--config", str(config)],
            env=environment,
            stdout=log,
            stderr=log,
            start_new_session=True,
        )
        try:
            # Settings owns the independent direction-installation regression
            # (120s); tutorial keeps the ordinary 240s desktop budget. Split full
            # acceptance budgets 360s + 240s, not one shared deadline.
            timeout = (
                360
                if arguments.desktop and arguments.desktop_scope in ("setup", "setup-settings")
                else 240
            )
            if arguments.desktop and arguments.desktop_scope == "model-packages":
                timeout = 260  # 240 seconds of work, then owned-process cleanup.
            deadline = time.monotonic() + (timeout if arguments.desktop else 45)
            result = arguments.output / result_name
            if quality:
                quality_exit(arguments, compositor, quality_deadline, result, failure)
            while (
                not quality
                and time.monotonic() < deadline
                and compositor.poll() is None
                and not result.exists()
                and not failure.exists()
            ):
                time.sleep(0.1)
            if not result.exists():
                for inside_log in sorted(arguments.output.glob("inside*.log")):
                    print(inside_log.read_text(), file=sys.stderr)
                raise RuntimeError("nested niri/GTK contract failed; inspect nested-niri.log")
            print(result.read_text())
        finally:
            if compositor.poll() is None:
                os.killpg(compositor.pid, signal.SIGTERM)
            try:
                timeout = 3
                if quality:
                    from support.translation_quality_capture import CLEANUP_SECONDS

                    timeout = max(
                        0, min(timeout, quality_deadline + CLEANUP_SECONDS - time.monotonic())
                    )
                compositor.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                os.killpg(compositor.pid, signal.SIGKILL)
                if quality:
                    from support.translation_quality_capture import CLEANUP_SECONDS

                    remaining = quality_deadline + CLEANUP_SECONDS - time.monotonic()
                    if remaining <= 0:
                        raise RuntimeError("compositor exit unconfirmed; preserve quality data")
                    compositor.wait(timeout=remaining)
                else:
                    compositor.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inside", action="store_true")
    parser.add_argument("--desktop", action="store_true")
    parser.add_argument("--collect-translation-quality", action="store_true")
    parser.add_argument("--hy-decode-comparison", action="store_true")
    parser.add_argument("--fixed-model-diagnostics", action="store_true")
    parser.add_argument("--quality-work-deadline", type=float, help=argparse.SUPPRESS)
    parser.add_argument(
        "--desktop-scope",
        choices=(
            "all",
            "browser",
            "gtk",
            "gtk4",
            "qt",
            "setup",
            "setup-settings",
            "setup-tutorial",
            "ordinary-gimp",
            "model-packages",
        ),
        default="all",
        help="setup combines settings and tutorial; split full acceptance requires both setup-settings and setup-tutorial",
    )
    parser.add_argument(
        "--gtk-refusal",
        action="store_true",
        help="verify GTK Unknown-IME refusal only, not translation support",
    )
    parser.add_argument(
        "--gtk-session-bindings",
        action="store_true",
        help="exercise configured F1/F24, held editor return and rebinding on real GTK controls",
    )
    parser.add_argument("--package", type=Path)
    parser.add_argument("--editor-bundle", type=Path)
    parser.add_argument("--fcitx-cooperative", type=Path)
    parser.add_argument("--qt-cooperative", type=Path)
    parser.add_argument("--native", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.fixed_model_diagnostics and (
        not args.collect_translation_quality or args.hy_decode_comparison
    ):
        parser.error("--fixed-model-diagnostics requires collection and excludes comparison")
    if args.hy_decode_comparison and not args.collect_translation_quality:
        parser.error("--hy-decode-comparison requires --collect-translation-quality")
    if args.collect_translation_quality and (
        not args.desktop or args.desktop_scope != "model-packages"
    ):
        parser.error(
            "--collect-translation-quality requires --desktop --desktop-scope model-packages"
        )
    if args.collect_translation_quality and args.inside and args.quality_work_deadline is None:
        parser.error("inside quality capture requires the outer work deadline")
    if args.quality_work_deadline is not None and (
        not args.inside or not args.collect_translation_quality
    ):
        parser.error("--quality-work-deadline is private to the quality inside process")
    args.native = args.native.resolve()
    args.output = args.output.resolve()
    if not args.desktop and not args.gtk_refusal:
        parser.error(
            "GTK translation is unsupported; use --gtk-refusal for safety refusal or --desktop for the browser path"
        )
    if args.gtk_session_bindings and (
        not args.desktop or args.desktop_scope not in ("gtk", "gtk4")
    ):
        parser.error("--gtk-session-bindings requires --desktop --desktop-scope gtk or gtk4")
    if args.desktop and args.gtk_refusal:
        parser.error("--gtk-refusal and --desktop verify different capability scopes")
    if args.desktop and not args.package:
        parser.error("--desktop requires --package")
    if args.desktop_scope in ("gtk", "gtk4") and not args.fcitx_cooperative:
        parser.error("GTK desktop scopes require --fcitx-cooperative")
    if args.desktop_scope == "qt" and (not args.fcitx_cooperative or not args.qt_cooperative):
        parser.error("Qt research requires --fcitx-cooperative and --qt-cooperative")
    if args.qt_cooperative:
        args.qt_cooperative = args.qt_cooperative.resolve(strict=True)
    if args.package:
        args.package = args.package.resolve()
    if args.fcitx_cooperative:
        args.fcitx_cooperative = args.fcitx_cooperative.resolve(strict=True)
    if args.editor_bundle:
        args.editor_bundle = args.editor_bundle.resolve(strict=True)
    if args.inside:
        try:
            if args.desktop:
                sys.path.insert(0, str(Path(__file__).parent))
                if args.desktop_scope == "model-packages":
                    from hy_package_e2e import run
                else:
                    from desktop_e2e import run

                run(args)
            else:
                inside(args)
                for mode in (
                    "notified",
                    "nested-complete",
                    "during",
                    "during-zero",
                    "missing",
                    "context",
                    "rollback",
                    "unknown",
                ):
                    inside(args, notifications=mode)
                inside(args, notifications="direct")
                result = json.loads((args.output / "native-unknown-result.json").read_text())
                result["controlledNotifications"] = [
                    json.loads(path.read_text())
                    for path in sorted(args.output.glob("notification-*-result.json"))
                ]
                assert len(result["controlledNotifications"]) == 8
                result["controlledDirectEpoch"] = json.loads(
                    (args.output / "direct-epoch-result.json").read_text()
                )
                (args.output / "native-result.json").write_text(json.dumps(result, indent=2))
        except BaseException:
            import traceback

            traceback.print_exc()
            sys.stderr.flush()
            (args.output / "inside.failed").touch()
            raise SystemExit(1) from None
    else:
        outer(args)
