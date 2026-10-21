"""Real GTK native boundaries; controlled views do not establish model quality."""

import argparse
import faulthandler
import json
import math
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
    def __init__(self, connection, *, deadline=None):
        self.connection = connection
        self.deadline = deadline
        self.connection.settimeout(2)
        self.id = 0
        self.events = []
        first = self.receive()
        assert first["event"] == "hello" and first["data"]["protocol"] == 2
        self.hello = first["data"]

    def receive(self):
        if self.deadline is not None:
            remaining = self.deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("native protocol exceeded the original session deadline")
            self.connection.settimeout(min(2, remaining))
        return receive_sync(self.connection)

    def call(self, operation, data=None):
        self.id += 1
        self.connection.sendall(encode({"id": self.id, "op": operation, "data": data or {}}))
        while True:
            frame = self.receive()
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


def gtk4_source_glossary(out, environment, deadline):
    """Use Wayland production admission inside the caller's owned Xvfb/session."""
    config = out / "gtk4-glossary-niri.kdl"
    ready = out / "gtk4-glossary-environment.json"
    ready.unlink(missing_ok=True)
    (out / "gtk4-glossary-clicks.json").unlink(missing_ok=True)
    command = [
        sys.executable,
        "-c",
        "import json,os,pathlib; pathlib.Path("
        + repr(str(ready))
        + ").write_text(json.dumps({k:os.environ[k] for k in ('NIRI_SOCKET','WAYLAND_DISPLAY')}))",
    ]
    config.write_text(
        "hotkey-overlay { skip-at-startup; }\nspawn-at-startup "
        + " ".join(json.dumps(item) for item in command)
        + "\n"
    )
    nested_env = environment | {"GDK_BACKEND": "wayland", "LIBGL_ALWAYS_SOFTWARE": "1"}
    nested_env.pop("WAYLAND_DISPLAY", None)
    nested_env.pop("NIRI_SOCKET", None)
    report = out / "gtk4-glossary-startup.json"
    evidence = {"status": "not-run", "cleanup": False, "real_model_quality": False}
    report.write_text(json.dumps(evidence, indent=2) + "\n")
    with (out / "gtk4-glossary-niri.log").open("w") as log:
        compositor = subprocess.Popen(
            ["niri", "--config", str(config)], env=nested_env, stdout=log, stderr=subprocess.STDOUT
        )
        evidence["pid"] = compositor.pid
        try:
            while time.monotonic() < deadline:
                assert compositor.poll() is None, "nested niri exited before GTK4 glossary"
                try:
                    connected = nested_env | json.loads(ready.read_text())
                except (FileNotFoundError, json.JSONDecodeError):
                    time.sleep(0.025)
                    continue
                assert connected.get("NIRI_SOCKET") and connected.get("WAYLAND_DISPLAY")
                # XTest targets the outer owned Xvfb, not any Xwayland DISPLAY
                # the nested compositor publishes to its applications.
                connected["DISPLAY"] = environment["DISPLAY"]
                evidence.update(
                    status="started",
                    wayland=connected["WAYLAND_DISPLAY"],
                    ipc=connected["NIRI_SOCKET"],
                )
                report.write_text(json.dumps(evidence, indent=2) + "\n")
                result = _gtk4_source_glossary(out, connected, deadline)
                evidence["status"] = "passed"
                return result
            raise AssertionError("GTK4 glossary: original session deadline during niri startup")
        except BaseException as error:
            evidence.update(status="failed", error=f"{type(error).__name__}: {error}")
            raise
        finally:
            compositor.terminate()
            try:
                compositor.wait(timeout=2)
            except subprocess.TimeoutExpired:
                compositor.kill()
                compositor.wait(timeout=2)
                evidence["forced_cleanup"] = True
            evidence.update(cleanup=compositor.poll() is not None, returncode=compositor.returncode)
            report.write_text(json.dumps(evidence, indent=2) + "\n")


def _gtk4_source_glossary(out, environment, deadline):
    """Real GTK4/Fcitx consumer, controlled public views, never model-quality evidence.

    The caller owns the daemon, HOME and Xvfb and supplies its original absolute
    session deadline. All cases share this budget, including socket I/O.
    """
    import hashlib

    def remaining():
        value = deadline - time.monotonic()
        assert value > 0, "GTK4 source glossary exhausted the existing session budget"
        return value

    def execute(*args):
        return subprocess.check_output(
            [str(arg) for arg in args],
            env=environment,
            text=True,
            timeout=min(2, remaining()),
        ).strip()

    def wait_for(predicate, label):
        while time.monotonic() < deadline:
            value = predicate()
            if value:
                return value
            time.sleep(min(0.025, remaining()))
        raise AssertionError(f"GTK4 source glossary session deadline: {label}")

    identity = environment.get("WAYLAND_DISPLAY", "") + "\n" + environment.get("NIRI_SOCKET", "")
    directory = (
        Path(environment["XDG_RUNTIME_DIR"])
        / "typelay"
        / hashlib.sha256(identity.encode()).hexdigest()[:16]
    )
    directory.mkdir(parents=True, mode=0o700)
    path = directory / "native.sock"
    state_path = out / "gtk4-glossary-state.json"
    command_path = out / "gtk4-glossary-command"
    for owned in (path, state_path, command_path):
        owned.unlink(missing_ok=True)
    environment = environment | {
        "GTK_PATH": str(out / "lib/gtk-4.0"),
        "TYPELAY_FIXTURE_STATE": str(state_path),
        "TYPELAY_FIXTURE_GLOSSARY_COMMAND": str(command_path),
        "TYPELAY_FIXTURE_COOPERATIVE": "1",
        "TYPELAY_FIXTURE_FULLSCREEN": "1",
    }
    environment.pop("TYPELAY_DESKTOP", None)
    results = []
    current_case = None
    capture_admission = None
    (out / "gtk4-glossary-result.json").unlink(missing_ok=True)
    with (
        socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server,
        (out / "gtk4-glossary.log").open("w") as log,
    ):
        server.bind(str(path))
        path.chmod(0o600)
        server.listen(1)
        server.settimeout(min(5, remaining()))
        process = subprocess.Popen(
            [str(out / "gtk4-fixture")], env=environment, stdout=log, stderr=subprocess.STDOUT
        )
        try:
            connection, _ = server.accept()
            with connection:
                assert peer_credentials(connection)[0] == process.pid
                native = Native(connection, deadline=deadline)
                mappings = Path(f"/proc/{process.pid}/maps").read_text()
                for library in (
                    out / "lib/libtypelay-gtk4.so",
                    out / "lib/gtk-4.0/4.0.0/immodules/libim-fcitx5.so",
                ):
                    assert str(library.resolve()) in mappings, f"actual GTK4 must load {library}"

                def call(operation, data=None):
                    connection.settimeout(min(2, remaining()))
                    return native.call(operation, data)

                def state():
                    try:
                        return json.loads(state_path.read_text())
                    except (FileNotFoundError, json.JSONDecodeError):
                        return {}

                def visible_windows():
                    found = subprocess.run(
                        ["xdotool", "search", "--onlyvisible", "--name", "^niri$"],
                        env=environment,
                        text=True,
                        capture_output=True,
                        timeout=min(2, remaining()),
                    )
                    assert found.returncode in (0, 1), found.stderr
                    return found.stdout.split()

                windows = wait_for(visible_windows, "owned nested niri window")
                assert len(windows) == 1, "owned Xvfb must contain exactly one nested niri"
                window = windows[0]
                execute("xdotool", "windowfocus", "--sync", window)
                wait_for(
                    lambda: state().get("fullscreen"), "fullscreen Wayland fixture coordinates"
                )
                execute("fcitx5-remote", "-s", "keyboard-us")
                generation = str(uuid.uuid4())
                configuration = {
                    "enabled": True,
                    "sessionEnabled": True,
                    "generation": generation,
                    "accept": "Tab",
                    "exit": "Esc",
                    "wake": {},
                    "direct": {},
                }
                call("configure-shortcuts", configuration)
                serial = 0

                def host(action):
                    nonlocal serial
                    serial += 1
                    temporary = command_path.with_suffix(".tmp")
                    temporary.write_text(f"{serial} {action}\n")
                    temporary.replace(command_path)
                    wait_for(lambda: state().get("glossaryCommand") == serial, action)

                clicks = []
                click_path = out / "gtk4-glossary-clicks.json"

                def point(name, *, overlay_control=False):
                    started = time.monotonic_ns() // 1000000
                    geometry = dict(
                        line.split("=", 1)
                        for line in execute(
                            "xdotool", "getwindowgeometry", "--shell", window
                        ).splitlines()
                    )
                    dimensions = int(geometry["WIDTH"]), int(geometry["HEIGHT"])
                    display = tuple(
                        int(value) for value in execute("xdotool", "getdisplaygeometry").split()
                    )
                    assert len(display) == 2 and all(value > 0 for value in display), display

                    def ready():
                        actual = state()
                        parent = [actual.get(key) for key in ("width", "height", "scale")]
                        if (
                            actual.get("observedAtMs", 0) <= started
                            or actual.get("fullscreen") is not True
                            or not all(
                                type(value) in (int, float) and math.isfinite(value) and value > 0
                                for value in parent
                            )
                            or parent[0] * parent[2] != dimensions[0]
                            or parent[1] * parent[2] != dimensions[1]
                        ):
                            return None
                        control = (
                            actual.get("overlayControls", {}).get(name)
                            if overlay_control
                            else actual.get(name)
                        )
                        if not control or overlay_control and not control.get("visible"):
                            return None
                        surface = control.get("surfaceGeometry") if overlay_control else control
                        if not isinstance(surface, dict):
                            return None
                        if overlay_control and (
                            type(surface.get("scale")) is not int or surface["scale"] != parent[2]
                        ):
                            return None
                        rectangle = [surface.get(key) for key in ("x", "y")]
                        rectangle += [control.get(key, 4) for key in ("width", "height")]
                        if not all(
                            type(value) in (int, float) and math.isfinite(value)
                            for value in rectangle
                        ):
                            return None
                        x, y, width, height = rectangle
                        if width <= 0 or height <= 0:
                            return None
                        x, y = x + width / 2, y + height / 2
                        if not (0 <= x < parent[0] and 0 <= y < parent[1]):
                            return None
                        if overlay_control:
                            assert control["enabled"], name
                        return actual, control, (int(x * parent[2]), int(y * parent[2]))

                    actual, control, pixel = wait_for(ready, f"{name}: matching parent geometry")
                    evidence = {
                        "case": current_case,
                        "control": name,
                        "nestedSize": dimensions,
                        "displaySize": display,
                        "nestedOrigin": [int(geometry[key]) for key in ("X", "Y")],
                        "beforeObservedAtMonoMs": actual.get("observedAtMs"),
                        "parent": {
                            key: actual.get(key)
                            for key in ("fullscreen", "width", "height", "scale")
                        },
                        "bounds": {key: control.get(key) for key in ("x", "y", "width", "height")},
                        "surfaceGeometry": control.get("surfaceGeometry"),
                        "beforeFocus": actual.get("focus"),
                        "pixelPoint": pixel,
                        "moveStartedAtMonoMs": time.monotonic_ns() // 1000000,
                    }
                    clicks.append(evidence)
                    click_path.write_text(json.dumps(clicks, indent=2) + "\n")
                    # A window may extend past the X screen: XTest would silently
                    # clamp an unreachable point, even when window bounds match GTK.
                    assert all(
                        0 <= origin + coordinate < limit
                        for origin, coordinate, limit in zip(
                            evidence["nestedOrigin"], pixel, display, strict=True
                        )
                    ), f"{name}: point outside actual X display: {evidence}"
                    # One move, then only a real crossing of this same control
                    # authorizes the one click. Never search offsets or move again.
                    execute("xdotool", "mousemove", "--window", window, *pixel)
                    moved_at = time.monotonic_ns() // 1000000
                    evidence["moveFinishedAtMonoMs"] = moved_at
                    try:
                        location = dict(
                            line.split("=", 1)
                            for line in execute(
                                "xdotool", "getmouselocation", "--shell"
                            ).splitlines()
                        )
                        evidence["xPointer"] = {
                            "status": "captured",
                            "ownedNiri": int(window),
                            "observedAtMonoMs": time.monotonic_ns() // 1000000,
                            **{key: int(location[key]) for key in ("X", "Y", "WINDOW")},
                        }
                    except (
                        OSError,
                        ValueError,
                        KeyError,
                        subprocess.SubprocessError,
                        AssertionError,
                    ) as error:
                        evidence["xPointer"] = {
                            "status": "unavailable",
                            "error": type(error).__name__,
                        }
                    click_path.write_text(json.dumps(clicks, indent=2) + "\n")
                    if overlay_control:
                        instance = control["instance"]

                        def hovered():
                            renew()
                            current = ready()
                            if current is None:
                                return None
                            observed, item, current_pixel = current
                            evidence["hoverObservation"] = {
                                "observedAtMonoMs": observed.get("observedAtMs"),
                                "control": item,
                                "pixelPoint": current_pixel,
                            }
                            click_path.write_text(json.dumps(clicks, indent=2) + "\n")
                            return (
                                observed
                                if current_pixel == pixel
                                and observed.get("observedAtMs", 0) >= moved_at
                                and item.get("instance") == instance
                                and item.get("pointerInside") is True
                                and type(item.get("pointerEnteredAtMonoMs")) is int
                                and item["pointerEnteredAtMonoMs"] > 0
                                else None
                            )

                        wait_for(hovered, f"{name}: real pointer hover on same surface point")
                    evidence["clickStartedAtMonoMs"] = time.monotonic_ns() // 1000000
                    click_path.write_text(json.dumps(clicks, indent=2) + "\n")
                    execute("xdotool", "click", "1")
                    following = state()
                    evidence.update(
                        clickReturnedAtMonoMs=time.monotonic_ns() // 1000000,
                        afterObservedAtMonoMs=following.get("observedAtMs"),
                        afterFocus=following.get("focus"),
                    )
                    click_path.write_text(json.dumps(clicks, indent=2) + "\n")

                for view_state in ("ValidTranslation", "EditedTranslationPending"):
                    for finish in (
                        "save",
                        "cancel",
                        "changed-source",
                        "replaced-buffer",
                        "reset",
                        "external-focus",
                        "no-click-replay",
                    ):
                        current_case = {"state": view_state, "finish": finish}
                        capture_admission = None
                        call("overlay-hide")
                        call("configure-shortcuts", configuration)
                        wait_for(lambda: state().get("firstEntry"), "GTK4 source observation")
                        point("firstEntry")
                        wait_for(lambda: state().get("imeClear"), "source real IM clear")
                        initial_source = state()
                        assert initial_source["firstEntry"]["focused"] is True
                        expected_context = initial_source["firstEntry"]["ime"]["context"]
                        assert type(expected_context) is int and expected_context > 0
                        target = None
                        capture_admission = {"rejectedCaptures": 0, "unavailableDescriptions": 0}

                        def source_ready():
                            nonlocal target
                            attempt = {"describeStartedAtMs": now_ms()}
                            capture_admission["lastAttempt"] = attempt
                            description = call("describe")
                            attempt["describeReturnedAtMs"] = now_ms()
                            if description is None:
                                capture_admission["unavailableDescriptions"] += 1
                                return None
                            assert description["sourceGlossarySupported"] is True
                            assert description["focused"] is True
                            if target is None:
                                target = description["target"]
                            assert description["target"] == target
                            basis = description["authority"]
                            assert basis["contextStamp"]["context"] == expected_context
                            attempt["describedAuthority"] = basis
                            attempt["captureStartedAtMs"] = now_ms()
                            value = call("capture", {"target": target, "authority": basis})
                            attempt["captureReturnedAtMs"] = now_ms()
                            attempt["captureReturnedNull"] = value is None
                            if value is None:
                                capture_admission["rejectedCaptures"] += 1
                                return None
                            assert value["ime"] == "Clear" and value["target"] == target
                            assert value["authority"]["contextStamp"]["context"] == expected_context
                            assert value["text"] == initial_source["first"]
                            capture_admission["capturedAuthority"] = value["authority"]
                            return value

                        # Fixture Clear is an independent earlier observation,
                        # not a promise that a later native capture cannot reject.
                        # Match the existing read-only source admission boundary:
                        # every attempt describes afresh, before any form action.
                        captured = wait_for(
                            source_ready, "same source/context native capture ready"
                        )
                        authority = captured["authority"]
                        token = {
                            "target": {
                                "backend": "linux-gtk4",
                                "value": target,
                                "lifetime": native.hello["lifetime"],
                            },
                            "commandGeneration": generation,
                            "revision": 1,
                        }
                        actions = {
                            "settingsRevision": 12,
                            "styleReleased": False,
                            "alternativeReleased": False,
                            "glossaryReleased": True,
                            "canOpenGlossary": True,
                            "canChangeStyle": False,
                            "canInheritStyle": False,
                            "canRequestAlternative": False,
                            "applicationStyle": None,
                            "styleUnavailableReason": None,
                            "inheritUnavailableReason": None,
                            "alternativeUnavailableReason": None,
                            "feedback": None,
                            "glossaryUnavailableReason": None,
                        }
                        view = {
                            "state": view_state,
                            "text": "Kept candidate",
                            "editable": True,
                            "viewContext": {
                                "token": token,
                                "bindings": {"accept": "Tab", "exit": "Esc"},
                                "canEdit": True,
                                "translationActions": actions,
                            },
                            "presentation": {"visible": True, "editorOwnsText": False},
                        }
                        if finish in ("save", "cancel"):
                            view["canAccept"] = True
                            view["presentation"]["tabHint"] = "Tab 接受译文"
                        base = {
                            "target": target,
                            "authority": authority,
                            "generation": generation,
                            "revision": 1,
                        }
                        assert call(
                            "overlay-render", {**base, "expiresAtMs": now_ms() + 800, "view": view}
                        )["visible"]
                        wait_for(
                            lambda: (state().get("candidate") or {}).get("text")
                            == "Kept candidate",
                            "rendered candidate",
                        )
                        original = state()
                        before = len(native.events)

                        def renew():
                            call("overlay-renew", {**base, "expiresAtMs": now_ms() + 800})

                        def observe(predicate, label):
                            def check():
                                renew()
                                return predicate(state())

                            return wait_for(check, f"{view_state}/{finish}: {label}")

                        def click(name):
                            control = observe(
                                lambda value: value.get("overlayControls", {}).get(name)
                                if value.get("overlayControls", {}).get(name, {}).get("visible")
                                else None,
                                name,
                            )
                            assert control["enabled"]
                            point(name, overlay_control=True)

                        def events(operation):
                            renew()
                            return [
                                item["data"]
                                for item in native.events[before:]
                                if item.get("event") == "ui"
                                and item["data"].get("operation") == operation
                            ]

                        def unchanged():
                            actual = state()
                            assert actual["first"] == original["first"]
                            for name in ("start", "end", "insertions", "deletions"):
                                assert actual["firstEntry"][name] == original["firstEntry"][name], (
                                    view_state,
                                    finish,
                                    name,
                                )
                            candidate = actual["candidate"]
                            assert (
                                candidate["text"] == "Kept candidate"
                                and not candidate["editable"]
                                and not candidate["focused"]
                            )
                            assert not events("beginEdit")
                            assert not any(
                                item.get("event") == "accepted" for item in native.events[before:]
                            )

                        entry = {
                            "formId": str(uuid.uuid4()),
                            "token": token,
                            "settingsRevision": 12,
                            "directions": [],
                            "sourceOrigin": True,
                        }
                        request_data = {**base, "entry": entry}
                        if finish == "no-click-replay":
                            assert not call("overlay-glossary-open", request_data)["opened"]
                            closed_view = {
                                **view,
                                "viewContext": {
                                    **view["viewContext"],
                                    "translationActions": {
                                        **actions,
                                        "glossaryReleased": False,
                                        "canOpenGlossary": False,
                                    },
                                },
                            }
                            assert call(
                                "overlay-render",
                                {**base, "expiresAtMs": now_ms() + 800, "view": closed_view},
                            )["visible"]
                            assert not call("overlay-glossary-open", request_data)["opened"]
                            assert call(
                                "overlay-render",
                                {**base, "expiresAtMs": now_ms() + 800, "view": view},
                            )["visible"]
                        click("typelay-menu-trigger")
                        click("typelay-glossary-add")
                        observe(lambda _: events("sessionGlossaryBegin"), "explicit glossary click")
                        if finish in ("save", "cancel"):
                            # Runtime Begin revokes acceptance and renders before
                            # it returns the entry. This pending view is the
                            # native form's sourceView, not the initial preview.
                            view = {
                                **view,
                                "canAccept": False,
                                "presentation": {**view["presentation"], "tabHint": "Tab 交还应用"},
                                "viewContext": {
                                    **view["viewContext"],
                                    "translationActions": {
                                        **actions,
                                        "canOpenGlossary": False,
                                        "glossaryUnavailableReason": "请先完成当前术语表单",
                                    },
                                },
                            }
                            assert call(
                                "overlay-render",
                                {
                                    **base,
                                    "expiresAtMs": now_ms() + 800,
                                    "view": view,
                                },
                            )["visible"]
                        assert call("overlay-glossary-open", request_data)["opened"]
                        observe(
                            lambda value: value["overlayControls"]
                            .get("typelay-glossary-source", {})
                            .get("focused"),
                            "source term focused",
                        )
                        unchanged()
                        if finish in ("save", "cancel"):
                            form_instance = state()["overlayControls"]["typelay-glossary-form"][
                                "instance"
                            ]
                            # The auxiliary ACK only withdraws edit affordances;
                            # it must retain this exact form and pending source.
                            auxiliary_view = {
                                **view,
                                "editable": False,
                                "viewContext": {
                                    **view["viewContext"],
                                    "canEdit": False,
                                    "translationActions": {
                                        **view["viewContext"]["translationActions"],
                                        "glossaryUnavailableReason": "内容或设置已变化，请重新打开表单",
                                    },
                                },
                            }
                            assert call(
                                "overlay-render",
                                {
                                    **base,
                                    "expiresAtMs": now_ms() + 800,
                                    "view": auxiliary_view,
                                },
                            )["visible"]
                            observe(
                                lambda value: value["overlayControls"]
                                .get("typelay-glossary-form", {})
                                .get("instance")
                                == form_instance
                                and value["overlayControls"]["typelay-glossary-form"]["visible"]
                                and value["overlayControls"]["typelay-glossary-source"]["focused"],
                                "auxiliary ACK retains the same focused form",
                            )
                            unchanged()
                        if finish in ("cancel", "no-click-replay"):
                            click("typelay-glossary-cancel")
                            observe(
                                lambda value: value["firstEntry"]["focused"],
                                "cancel returned source",
                            )
                            assert not events("sessionGlossaryAdd")
                            unchanged()
                            if finish == "no-click-replay":
                                assert not call("overlay-glossary-open", request_data)["opened"]
                                # A new physical opt-in opens a new form. Revoking
                                # settings/model projection after opening must not
                                # be mistaken for the host's benign ACK render.
                                previous_begins = len(events("sessionGlossaryBegin"))
                                click("typelay-menu-trigger")
                                click("typelay-glossary-add")
                                observe(
                                    lambda _: len(events("sessionGlossaryBegin")) > previous_begins,
                                    "new explicit glossary activation",
                                )
                                entry = {**entry, "formId": str(uuid.uuid4())}
                                request_data = {**base, "entry": entry}
                                assert call("overlay-glossary-open", request_data)["opened"]
                                observe(
                                    lambda value: value["overlayControls"]
                                    .get("typelay-glossary-source", {})
                                    .get("focused"),
                                    "replacement form focused",
                                )
                                changed_actions = {
                                    **actions,
                                    **(
                                        {"settingsRevision": 13}
                                        if view_state == "ValidTranslation"
                                        else {"glossaryReleased": False, "canOpenGlossary": False}
                                    ),
                                }
                                changed_view = {
                                    **view,
                                    "viewContext": {
                                        **view["viewContext"],
                                        "translationActions": changed_actions,
                                    },
                                }
                                call(
                                    "overlay-render",
                                    {**base, "expiresAtMs": now_ms() + 800, "view": changed_view},
                                )
                                wait_for(
                                    lambda: not state()
                                    .get("overlayControls", {})
                                    .get("typelay-glossary-form", {})
                                    .get("visible"),
                                    "post-open settings/model change closes old form",
                                )
                                assert (
                                    call(
                                        "overlay-glossary-return",
                                        {**base, "formId": entry["formId"], "token": token},
                                    )
                                    is None
                                )
                                assert not call("overlay-glossary-open", request_data)["opened"]
                                assert not events("sessionGlossaryAdd") and not events("beginEdit")
                                actual = state()
                                assert actual["first"] == original["first"]
                                for name in ("start", "end", "insertions", "deletions"):
                                    assert (
                                        actual["firstEntry"][name] == original["firstEntry"][name]
                                    )
                                assert not any(
                                    item.get("event") == "accepted"
                                    for item in native.events[before:]
                                )
                        elif finish in ("changed-source", "replaced-buffer", "external-focus"):
                            invalidation_start = len(native.events)
                            if finish in ("changed-source", "replaced-buffer"):
                                host(
                                    "change-source"
                                    if finish == "changed-source"
                                    else "replace-buffer"
                                )
                            else:
                                point("secondEntry")

                            def invalidated_source():
                                call("describe", {"target": target})
                                return any(
                                    item.get("event") == "input"
                                    and item.get("data", {}).get("target") == target
                                    for item in native.events[invalidation_start:]
                                )

                            wait_for(invalidated_source, "real source/provider invalidation")
                            # The production host closes its view after this input
                            # event. Here the native contract must independently
                            # refuse the old form before any controlled-host hide.
                            assert not call("overlay-glossary-open", request_data)["opened"]
                            invalidated = state()
                            assert not events("sessionGlossaryAdd")
                            assert (
                                call(
                                    "overlay-glossary-return",
                                    {**base, "formId": entry["formId"], "token": token},
                                )
                                is None
                            )
                            after_refusal = state()
                            assert after_refusal["first"] == invalidated["first"]
                            for name in ("start", "end", "insertions", "deletions"):
                                assert (
                                    after_refusal["firstEntry"][name]
                                    == invalidated["firstEntry"][name]
                                )
                            assert not events("beginEdit")
                            assert not any(
                                item.get("event") == "accepted" for item in native.events[before:]
                            )
                        else:
                            execute("xdotool", "type", "--delay", "0", "PR")
                            observe(
                                lambda value: value["overlayControls"][
                                    "typelay-glossary-source"
                                ].get("text")
                                == "PR"
                                and value.get("focusedImeClear"),
                                "first term clear",
                            )
                            execute("xdotool", "key", "Tab")
                            observe(
                                lambda value: value["overlayControls"]["typelay-glossary-target"][
                                    "focused"
                                ],
                                "target term focus",
                            )
                            execute("xdotool", "type", "--delay", "0", "pull request")
                            observe(
                                lambda value: value["overlayControls"][
                                    "typelay-glossary-target"
                                ].get("text")
                                == "pull request"
                                and value.get("focusedImeClear"),
                                "target term clear",
                            )
                            if finish == "reset":
                                host("reset-inactive-term")
                            click("typelay-glossary-save")
                            if finish == "reset":
                                observe(
                                    lambda value: value["overlayControls"][
                                        "fixture-glossary-feedback"
                                    ]["text"]
                                    == "请先完成两个词条的输入法组合，再保存",
                                    "real reset refuses explicit Save",
                                )
                                assert not events("sessionGlossaryAdd")
                            else:
                                saved = observe(
                                    lambda _: events("sessionGlossaryAdd"), "explicit save"
                                )[-1]["value"]
                                assert (
                                    saved["formId"] == entry["formId"] and saved["token"] == token
                                )
                                assert saved["rule"] == {
                                    "sourceTerm": "PR",
                                    "targetTerm": "pull request",
                                    "sourceLanguage": None,
                                    "targetLanguage": None,
                                }
                                receipt = {"formId": entry["formId"], "token": token}
                                assert call("overlay-glossary-return", {**base, **receipt}) == {
                                    **receipt,
                                    "returned": True,
                                }
                                observe(
                                    lambda value: value["firstEntry"]["focused"]
                                    and value.get("imeClear"),
                                    "exact source return",
                                )
                                description = call("describe", {"target": target})
                                assert description["glossarySource"] == receipt
                                assert (
                                    not description["sessionBindings"]["sourceReady"]
                                    and not description["sessionBindings"]["editorReady"]
                                )
                                again = call(
                                    "capture",
                                    {"target": target, "authority": description["authority"]},
                                )
                                assert (
                                    again
                                    and again["glossarySource"] == receipt
                                    and again["ime"] == "Clear"
                                )
                                assert (
                                    not again["writeSupported"]
                                    and again["text"] == captured["text"]
                                    and again["selection"] == captured["selection"]
                                )
                                assert len(events("sessionGlossaryAdd")) == 1
                            unchanged()
                        results.append(
                            {
                                "state": view_state,
                                "finish": finish,
                                "passed": True,
                                "captureAdmission": capture_admission,
                            }
                        )
                call("overlay-hide")
                point("multiline")
                wait_for(lambda: state().get("focus") == "fixture-multiline", "TextView focus")
                assert call("describe")["sourceGlossarySupported"] is False
        except BaseException as error:
            (out / "gtk4-glossary-result.json").write_text(
                json.dumps(
                    {
                        "passed": False,
                        "cases": results,
                        "failed_case": current_case,
                        "captureAdmission": capture_admission,
                        "error": f"{type(error).__name__}: {error}",
                        "real_model_quality": False,
                    },
                    indent=2,
                )
                + "\n"
            )
            raise
        finally:
            process.terminate()
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=2)
            path.unlink(missing_ok=True)
    (out / "gtk4-glossary-result.json").write_text(
        json.dumps({"passed": True, "cases": results, "real_model_quality": False}, indent=2) + "\n"
    )
    return results


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
