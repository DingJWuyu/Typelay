"""Real GTK + native module in a nested niri session. Not physical desktop certification."""

import argparse
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
from niri import contents, place_geometry, request  # noqa: E402
from paths import Paths, peer_credentials  # noqa: E402
from wire import encode, now_ms, receive_sync  # noqa: E402


class Native:
    def __init__(self, connection):
        self.connection = connection
        self.connection.settimeout(2)
        self.id = 0
        self.events = []
        first = receive_sync(connection)
        assert first["event"] == "hello" and first["data"]["protocol"] == 1
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

    def capture(self, target=None):
        fields = self.describe(target)
        result = self.call("capture", {"target": fields["target"], "authority": fields["authority"]})
        assert result is not None, "authorized GTK read unavailable"
        return result


def key(value):
    subprocess.run(["xdotool", "key", "--clearmodifiers", "--delay", "0", value], check=True, timeout=2)
    time.sleep(0.025)


def snapshot(native, hello, geometry):
    return {
        "target": {"backend": "linux-gtk3", "value": native["target"], "lifetime": hello["lifetime"]},
        "text": native["text"], "selection": native["selection"], "bounds": geometry["bounds"],
        "multiline": native["metadata"]["multiline"], "access": "Supported", "ime": native["ime"],
        "observedAtMs": native["observedAtMs"], "inputEpoch": native["authority"]["inputRevision"],
        "focusEpoch": native["authority"]["focusRevision"], "writeSupported": True, "directWriteSupported": True,
    }


def plan(source, replacement, scope=None):
    text = source["text"].encode("utf-16-le")
    first, last = (scope["start"], scope["end"]) if scope else (0, len(text) // 2)
    result = (text[: first * 2] + replacement.encode("utf-16-le") + text[last * 2 :]).decode("utf-16-le")
    return {
        "id": str(uuid.uuid4()), "sessionId": str(uuid.uuid4()), "revision": 1,
        "expected": source, "scope": scope, "replacement": replacement,
        "resultText": result, "caret": first + len(replacement.encode("utf-16-le")) // 2,
    }


def inside(arguments):
    paths = Paths(ROOT)
    paths.native.unlink(missing_ok=True)
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(str(paths.native))
    paths.native.chmod(0o600)
    server.listen(4)
    server.settimeout(10)
    environment = {**os.environ, "GTK3_MODULES": str(arguments.native / "libtypelay-gtk3.so"), "GDK_BACKEND": "wayland"}
    environment.pop("TYPELAY_DESKTOP", None)
    fixture = subprocess.Popen([str(arguments.native / "gtk-fixture")], env=environment)
    checks = []
    try:
        connection, _ = server.accept()
        assert peer_credentials(connection)[0] == fixture.pid
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
        nested = subprocess.check_output(["xdotool", "search", "--class", "niri"], text=True).splitlines()
        assert nested
        subprocess.run(["xdotool", "windowfocus", "--sync", nested[-1]], check=True, timeout=2)
        time.sleep(0.2)
        native = client.capture()
        assert native["metadata"]["automationId"] == "fixture-first"
        assert native["ime"] == "Clear" and native["writeSupported"]
        outputs = contents(request("Outputs"), "Outputs")
        workspaces = contents(request("Workspaces"), "Workspaces")
        window = next(item for item in contents(request("Windows"), "Windows") if item["pid"] == fixture.pid)
        geometry = place_geometry(window, outputs, workspaces, native["geometry"])
        assert geometry is not None, "GTK frame geometry does not agree with niri"
        checks.append("real GTK metadata, authorized read and niri geometry")

        original_id = native["target"]
        write = plan(snapshot(native, client.hello, geometry), "你好", {"start": 9, "end": 14})
        # Direct replacement remains bound to the first entry after focus moves away.
        key("Tab")
        assert client.describe()["target"] != original_id
        fresh = client.describe(original_id)
        receipt = client.call("direct", {
            "target": original_id, "plan": write, "authority": native["authority"],
            "approved": fresh["authority"], "expiresAtMs": now_ms() + 250,
        })
        assert receipt["status"] == "Verified", receipt["reason"]
        assert client.describe()["target"] != original_id, "direct replacement stole focus"
        assert client.capture(original_id)["text"] == "alpha 😀 你好"
        checks.append("background direct replacement, UTF-16 scope and focus preservation")

        key("shift+Tab")
        current = client.capture()
        assert current["target"] == original_id
        write = plan(snapshot(current, client.hello, geometry), "accepted candidate")
        client.call("publish", {
            "target": original_id, "authority": current["authority"],
            "lease": {"source": write["expected"], "escapeAllowed": True},
            "permit": {"plan": write, "expiresAtMs": current["observedAtMs"] + 100},
        })
        key("Tab")
        accepted = client.capture(original_id)
        assert accepted["text"] == "accepted candidate"
        assert client.describe()["target"] == original_id, "successful Tab leaked to GTK focus navigation"
        assert any(event.get("event") == "accepted" for event in client.events)
        checks.append("real compositor Tab, synchronous commit and no duplicate focus navigation")

        # Without a permit, Tab must retain GTK's ordinary focus behavior.
        client.call("revoke")
        key("Tab")
        assert client.describe()["target"] != original_id
        key("Tab")
        password = client.describe()
        assert password["metadata"]["password"]
        assert client.call("capture", {"target": password["target"], "authority": password["authority"]}) is None
        checks.append("unowned Tab passthrough and password-before-text rejection")

        key("shift+Tab")
        key("shift+Tab")
        old = client.capture()
        stale = plan(snapshot(old, client.hello, geometry), "must not replace")
        subprocess.run(["xdotool", "type", "--clearmodifiers", "--delay", "0", "new input"], check=True, timeout=2)
        time.sleep(0.05)
        fresh = client.describe(original_id)
        refused = client.call("direct", {
            "target": original_id, "plan": stale, "authority": old["authority"],
            "approved": fresh["authority"], "expiresAtMs": now_ms() + 250,
        })
        assert refused["status"] == "RejectedBeforeMutation"
        assert client.capture(original_id)["text"] != "must not replace"
        checks.append("concurrent source edits rejected before mutation")
        (arguments.output / "native-result.json").write_text(json.dumps({"passed": checks}, indent=2))
    finally:
        fixture.terminate()
        try:
            fixture.wait(timeout=2)
        except subprocess.TimeoutExpired:
            fixture.kill()
            fixture.wait()
        server.close()
        paths.native.unlink(missing_ok=True)


def outer(arguments):
    arguments.output.mkdir(parents=True, exist_ok=True)
    runtime = arguments.output / "runtime"
    runtime.mkdir(mode=0o700, exist_ok=True)
    config = arguments.output / "niri.kdl"
    command = ["/usr/bin/python3", "-I", str(Path(__file__).resolve()), "--inside", "--native", str(arguments.native), "--output", str(arguments.output)]
    config.write_text("spawn-at-startup " + " ".join(json.dumps(item) for item in command) + "\n")
    environment = {**os.environ, "XDG_RUNTIME_DIR": str(runtime), "GDK_BACKEND": "wayland", "LIBGL_ALWAYS_SOFTWARE": "1"}
    environment.pop("WAYLAND_DISPLAY", None)
    environment.pop("NIRI_SOCKET", None)
    environment.pop("TYPELAY_DESKTOP", None)
    with (arguments.output / "nested-niri.log").open("w") as log:
        compositor = subprocess.Popen(
            ["dbus-run-session", "--", "niri", "--config", str(config)],
            env=environment, stdout=log, stderr=log, start_new_session=True,
        )
        try:
            deadline = time.monotonic() + 45
            result = arguments.output / "native-result.json"
            while time.monotonic() < deadline and compositor.poll() is None and not result.exists():
                time.sleep(0.1)
            if not result.exists():
                raise RuntimeError("nested niri/GTK contract failed; inspect nested-niri.log")
            print(result.read_text())
        finally:
            os.killpg(compositor.pid, signal.SIGTERM)
            try:
                compositor.wait(timeout=3)
            except subprocess.TimeoutExpired:
                os.killpg(compositor.pid, signal.SIGKILL)
                compositor.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inside", action="store_true")
    parser.add_argument("--native", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.native = args.native.resolve()
    args.output = args.output.resolve()
    (inside if args.inside else outer)(args)
