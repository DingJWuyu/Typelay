"""Real Qt6 generic plugin and standard Widgets in nested niri; not physical IME certification."""

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from native_e2e import (
    Native,
    Paths,
    contents,
    key,
    now_ms,
    outer,
    peer_credentials,
    plan,
    request,
    snapshot,
)


def inside(arguments):
    sys.stdout = sys.stderr = (arguments.output / "inside.log").open("w", buffering=1)
    paths = Paths(Path(__file__).resolve().parents[2])
    paths.native.unlink(missing_ok=True)
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(str(paths.native))
    paths.native.chmod(0o600)
    server.listen(1)
    server.settimeout(10)
    environment = {
        **os.environ,
        "QT_QPA_PLATFORM": "wayland",
        "QT_PLUGIN_PATH": str(arguments.native / "qt6")
        + ":"
        + os.environ.get("QT_PLUGIN_PATH", ""),
        "QT_QPA_GENERIC_PLUGINS": "typelay",
    }
    fixture = subprocess.Popen([str(arguments.native / "qt-fixture")], env=environment)
    checks = []
    try:
        connection, _ = server.accept()
        assert peer_credentials(connection)[0] == fixture.pid
        client = Native(connection)
        assert client.hello["backend"] == "qt6"
        deadline = time.monotonic() + 8
        window = None
        while time.monotonic() < deadline and window is None:
            window = next(
                (
                    item
                    for item in contents(request("Windows"), "Windows")
                    if item.get("pid") == fixture.pid
                ),
                None,
            )
            if window is None:
                time.sleep(0.05)
        assert window is not None and not window["is_floating"]
        os.environ["DISPLAY"] = os.environ["TYPELAY_TEST_DISPLAY"]
        nested = subprocess.check_output(
            ["xdotool", "search", "--onlyvisible", "--name", "^niri$"], text=True, timeout=2
        ).splitlines()
        assert len(nested) == 1
        subprocess.run(["xdotool", "windowfocus", "--sync", nested[0]], check=True, timeout=2)
        time.sleep(0.2)
        current = client.capture()
        assert current["metadata"]["automationId"] == "fixture-first"
        assert current["text"] == "alpha 😀 omega" and current["ime"] == "Clear"
        assert current["geometry"]["space"] == "qt-toplevel"
        original = current["target"]
        checks.append("additive generic loader, full text and parent-local tiled Qt anchor")

        def source(native):
            result = snapshot(native, client.hello, {"bounds": native["geometry"]["bounds"]})
            result["target"]["backend"] = "linux-qt6"
            return result

        write = plan(source(current), "你好", {"start": 9, "end": 14})
        key("Tab")
        assert client.describe()["target"] != original
        approved = client.describe(original)["authority"]
        result = client.call(
            "direct",
            {
                "target": original,
                "plan": write,
                "authority": current["authority"],
                "approved": approved,
                "expiresAtMs": now_ms() + 250,
            },
        )
        assert result["status"] == "Verified", result
        assert client.capture(original)["text"] == "alpha 😀 你好"
        assert client.describe()["target"] != original
        checks.append(
            "UTF-16 selection direct write preserves background source and foreground focus"
        )
        key("shift+Tab")
        current = client.capture()
        write = plan(source(current), "accepted candidate")
        client.call(
            "publish",
            {
                "target": original,
                "authority": current["authority"],
                "lease": {"source": write["expected"], "escapeAllowed": True},
                "permit": {"plan": write, "expiresAtMs": current["observedAtMs"] + 100},
            },
        )
        key("Tab")
        assert client.capture(original)["text"] == "accepted candidate"
        assert client.describe()["target"] == original
        assert any(event.get("event") == "accepted" for event in client.events)
        checks.append("compositor Tab commits synchronously without focus navigation")

        before_ime = client.capture()
        key("F8")
        composing = client.describe()
        assert (
            client.call("capture", {"target": original, "authority": composing["authority"]})
            is None
        )
        result = client.call(
            "direct",
            {
                "target": original,
                "plan": plan(source(before_ime), "forbidden"),
                "authority": composing["authority"],
                "approved": composing["authority"],
                "expiresAtMs": now_ms() + 250,
            },
        )
        assert result["status"] == "RejectedBeforeMutation"
        key("F9")
        after_ime = client.describe()
        assert (
            client.call("capture", {"target": original, "authority": after_ime["authority"]})
            is None
        )
        checks.append("IME-exposed target remains Unknown even after an empty preedit event")
        client.call("revoke")
        key("Tab")
        key("Tab")
        password = client.describe()
        assert password["metadata"]["password"]
        assert (
            client.call(
                "capture", {"target": password["target"], "authority": password["authority"]}
            )
            is None
        )
        checks.append("unowned Tab passthrough and password refusal before text read")
        key("Tab")
        plain = client.capture()
        assert (
            plain["metadata"]["automationId"] == "fixture-plain"
            and plain["text"] == "first\u00a0line\nsecond line"
        )
        result = client.call(
            "direct",
            {
                "target": plain["target"],
                "plan": plan(source(plain), "changed", {"start": 11, "end": 17}),
                "authority": plain["authority"],
                "approved": plain["authority"],
                "expiresAtMs": now_ms() + 250,
            },
        )
        assert result["status"] == "Verified", result
        assert client.capture()["text"] == "first\u00a0line\nchanged line"
        key("Tab")
        text = client.capture()
        assert text["metadata"]["automationId"] == "fixture-text"
        assert text["text"] == "plain text editor"
        checks.append(
            "QPlainTextEdit range write preserves NBSP and newline; plain QTextEdit capture"
        )
        (arguments.output / "native-result.json").write_text(
            json.dumps({"passed": checks}, indent=2)
        )
    finally:
        fixture.terminate()
        try:
            fixture.wait(timeout=2)
        except subprocess.TimeoutExpired:
            fixture.kill()
            fixture.wait()
        server.close()
        paths.native.unlink(missing_ok=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inside", action="store_true")
    parser.add_argument("--native", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.native = args.native.resolve()
    args.output = args.output.resolve()
    args.desktop = False
    if args.inside:
        try:
            inside(args)
        except BaseException:
            import traceback

            traceback.print_exc()
            sys.stderr.flush()
            (args.output / "inside.failed").touch()
            raise SystemExit(1) from None
    else:
        outer(args, __file__)
