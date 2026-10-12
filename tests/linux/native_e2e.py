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
sys.path.insert(0, str(ROOT / "src/TypeLay.Linux/desktop"))
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
                return frame["data"]
            self.events.append(frame)

    def describe(self, target=None):
        result = self.call("describe", {"target": target} if target else {})
        assert result is not None, "real focused GTK field unavailable"
        return result


def key(value):
    subprocess.run(
        ["xdotool", "key", "--clearmodifiers", "--delay", "0", value], check=True, timeout=2
    )
    time.sleep(0.025)


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


def inside(arguments):
    # niri may detach spawned programs from its own diagnostic stream.
    sys.stdout = sys.stderr = (arguments.output / "inside.log").open("w", buffering=1)
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
    }
    environment.pop("TYPELAY_DESKTOP", None)
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

        initial = wait_state(lambda value: value.get("focus") == "fixture-first")
        original = initial["first"]
        description = client.describe()
        assert description["metadata"]["automationId"] == "fixture-first"
        assert description["metadata"]["hasTextEdit"] is False
        assert "text" not in description and "selection" not in description
        assert original not in json.dumps(description, ensure_ascii=False)
        target = description["target"]
        geometry = anchor_geometry(description["geometry"])
        assert geometry is not None, "refusal must exercise a visible ordinary GTK field"
        assert (
            client.call("capture", {"target": target, "authority": description["authority"]})
            is None
        ), "Unknown IME exposed source text"
        checks.append("visible GTK metadata excludes body; Unknown IME capture returns no snapshot")

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
        assert rejected == {"status": "RejectedBeforeMutation", "reason": "ImeUnclear"}, rejected
        generation = str(uuid.uuid4())
        client.call(
            "configure-shortcuts",
            {
                "enabled": True,
                "generation": generation,
                "wake": {},
                "direct": {},
            },
        )
        for kind in ("wake", "direct"):
            current = client.describe(target)
            lease = {"target": target, "authority": current["authority"], "generation": generation}
            client.call("publish-shortcut", lease)
            assert client.call(
                "trigger-shortcut",
                {
                    **lease,
                    "kind": kind,
                    "triggeredAtMs": now_ms(),
                },
            ) == {"triggered": False}, "Unknown IME allowed a shortcut read"
        checks.append("caller-supplied Clear cannot authorize direct write or wake/direct reads")

        current = client.describe(target)
        write = plan(snapshot({**forged, **current}, client.hello, geometry), "must not replace")
        client.call(
            "publish",
            {
                "target": target,
                "authority": current["authority"],
                "lease": {"source": write["expected"], "escapeAllowed": True},
                "permit": {"plan": write, "expiresAtMs": now_ms() + 100},
            },
        )
        key("Tab")
        after_tab = wait_state(lambda value: value.get("focus") == "fixture-second")
        assert after_tab["first"] == original and after_tab["second"] == initial["second"]
        client.describe()  # Drain events through a subsequent real protocol response.
        assert not any(event.get("event") in ("accepted", "shortcut") for event in client.events)
        key("shift+Tab")
        wait_state(lambda value: value.get("focus") == "fixture-first")
        key("End")
        subprocess.run(["xdotool", "type", "--clearmodifiers", "x"], check=True, timeout=2)
        wait_state(lambda value: value.get("first") == original + "x")
        current = client.describe(target)
        assert current["metadata"]["hasTextEdit"] is False
        assert client.call("capture", {"target": target, "authority": current["authority"]}) is None
        checks.append(
            "Tab belongs to GTK, text remains unchanged by TypeLay, ordinary typing stays Unknown"
        )
        (arguments.output / "native-result.json").write_text(
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


def outer(arguments, script=None):
    arguments.output.mkdir(parents=True, exist_ok=True)
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
        str(Path(script or __file__).resolve()),
        "--inside",
        "--native",
        str(arguments.native),
        "--output",
        str(arguments.output),
    ]
    if getattr(arguments, "gtk_refusal", False):
        command.append("--gtk-refusal")
    if arguments.desktop:
        command.extend(["--desktop", "--package", str(arguments.package)])
        if arguments.editor_bundle:
            command.extend(["--editor-bundle", str(arguments.editor_bundle)])
    # The compositor's first-run help owns keyboard focus independently of its
    # focused-window IPC state. This disposable test session has no help UI.
    config.write_text(
        "hotkey-overlay { skip-at-startup; }\n"
        + "spawn-at-startup "
        + " ".join(json.dumps(item) for item in command)
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
            deadline = time.monotonic() + (240 if arguments.desktop else 45)
            result = arguments.output / result_name
            while (
                time.monotonic() < deadline
                and compositor.poll() is None
                and not result.exists()
                and not failure.exists()
            ):
                time.sleep(0.1)
            if not result.exists():
                inside_log = arguments.output / "inside.log"
                if inside_log.exists():
                    print(inside_log.read_text(), file=sys.stderr)
                raise RuntimeError("nested niri/GTK contract failed; inspect nested-niri.log")
            print(result.read_text())
        finally:
            if compositor.poll() is None:
                os.killpg(compositor.pid, signal.SIGTERM)
            try:
                compositor.wait(timeout=3)
            except subprocess.TimeoutExpired:
                os.killpg(compositor.pid, signal.SIGKILL)
                compositor.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inside", action="store_true")
    parser.add_argument("--desktop", action="store_true")
    parser.add_argument(
        "--gtk-refusal",
        action="store_true",
        help="verify GTK Unknown-IME refusal only, not translation support",
    )
    parser.add_argument("--package", type=Path)
    parser.add_argument("--editor-bundle", type=Path)
    parser.add_argument("--native", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.native = args.native.resolve()
    args.output = args.output.resolve()
    if not args.desktop and not args.gtk_refusal:
        parser.error(
            "GTK translation is unsupported; use --gtk-refusal for safety refusal or --desktop for the browser path"
        )
    if args.desktop and args.gtk_refusal:
        parser.error("--gtk-refusal and --desktop verify different capability scopes")
    if args.desktop and not args.package:
        parser.error("--desktop requires --package")
    if args.package:
        args.package = args.package.resolve()
    if args.editor_bundle:
        args.editor_bundle = args.editor_bundle.resolve(strict=True)
    if args.inside:
        try:
            if args.desktop:
                sys.path.insert(0, str(Path(__file__).parent))
                from desktop_e2e import run

                run(args)
            else:
                inside(args)
        except BaseException:
            import traceback

            traceback.print_exc()
            sys.stderr.flush()
            (args.output / "inside.failed").touch()
            raise SystemExit(1) from None
    else:
        outer(args)
