"""Real installed desktop, real CPU model, real GTK fields and an unmodified YAD app."""

import json
import os
import re
import subprocess
import sys
import time

from main import control
from native_e2e import key
from niri import contents, request
from paths import Paths


def wait_for(predicate, description, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(0.05)
    raise AssertionError(description)


def run(arguments):
    sys.stdout = sys.stderr = (arguments.output / "inside.log").open("w", buffering=1)
    os.environ["DISPLAY"] = os.environ["TYPELAY_TEST_DISPLAY"]
    # niri deliberately clears DISPLAY for applications; XTest addresses the
    # separate Xvfb while applications still use the real Wayland socket.
    nested = subprocess.check_output(
        ["xdotool", "search", "--onlyvisible", "--name", "^niri$"], text=True, timeout=2
    ).splitlines()
    assert len(nested) == 1
    subprocess.run(["xdotool", "windowfocus", "--sync", nested[0]], check=True, timeout=2)
    package = arguments.package
    paths = Paths(package)
    state_path = arguments.output / "fixture.json"
    environment = {**os.environ, "TYPELAY_FIXTURE_STATE": str(state_path)}
    environment["TYPELAY_TRACE"] = str(arguments.output / "controller.jsonl")
    process = fixture = application = None
    evidence = []
    original = "Please send me the final version."
    settings = {
        "Schema": 1,
        "Enabled": True,
        "Source": "en",
        "Target": "zh",
        "OnboardingComplete": True,
        "ExperimentalWriteback": True,
        "Mode": 0,
    }

    def status():
        try:
            return control(paths, "status") or {}
        except (OSError, EOFError, ValueError):
            return {}

    def view(state):
        data = status()
        return data if data.get("viewState") == state and data.get("overlayVisible") else None

    def fixture_state():
        if state_path.exists():
            return json.loads(state_path.read_text())
        return {}

    def typed(text):
        subprocess.run(
            ["xdotool", "type", "--clearmodifiers", "--delay", "2", text], check=True, timeout=5
        )

    def start():
        nonlocal process
        paths.settings.write_text(json.dumps(settings))
        process = subprocess.Popen(
            [str(package / "typelay"), "--background"],
            env=environment,
            stdout=sys.stdout,
            stderr=sys.stderr,
        )

        def ready():
            assert process.poll() is None, "production desktop exited during startup"
            return status().get("modelReady")

        wait_for(ready, "real model did not become ready", 125)
        assert process.poll() is None

    def stop():
        nonlocal process
        assert control(paths, "quit") is True
        assert process.wait(timeout=7) == 0
        process = None
        assert not paths.control.exists() and not paths.native.exists()

    def focus(pid):
        def select():
            windows = contents(request("Windows"), "Windows")
            return next((window for window in windows if window.get("pid") == pid), None)

        window = wait_for(select, "application window did not map")
        request({"Action": {"FocusWindow": {"id": window["id"]}}})
        time.sleep(0.2)

    def translated(value):
        return bool(value and value != original and re.search(r"[\u3400-\u9fff]", value))

    def changed_fixture():
        value = fixture_state()
        return value if translated(value.get("first")) else None

    try:
        start()
        fixture = subprocess.Popen(
            [str(package / "typelay"), "launch", str(arguments.native / "gtk-fixture")],
            env=environment,
        )
        focus(fixture.pid)
        wait_for(lambda: fixture_state().get("focus") == "fixture-first", "fixture focus")
        key("ctrl+a")
        key("BackSpace")
        wait_for(lambda: view("IdleCapsule"), "empty field did not show a capsule")
        subprocess.run(["grim", str(arguments.output / "capsule.png")], check=True, timeout=3)
        typed(original)
        wait_for(lambda: view("ValidTranslation"), "actual translation preview did not appear")
        subprocess.run(["grim", str(arguments.output / "translation.png")], check=True, timeout=3)
        assert fixture_state()["first"] == original, "preview changed the source before acceptance"
        key("Tab")
        accepted = wait_for(
            changed_fixture,
            "physical compositor Tab did not accept the real candidate",
        )
        assert accepted["focus"] == "fixture-first", "Tab also navigated to another field"
        evidence.append("automatic mode: real model preview, physical Wayland Tab and source focus")
        # The same public background command must not reopen the settings window.
        subprocess.run([str(package / "typelay"), "--background"], env=environment, check=True)
        assert fixture_state()["focus"] == "fixture-first"
        stop()
        settings["Mode"] = 1
        fixture.terminate()
        fixture.wait(timeout=3)
        fixture = None
        state_path.unlink(missing_ok=True)
        start()
        fixture = subprocess.Popen(
            [str(package / "typelay"), "launch", str(arguments.native / "gtk-fixture")],
            env=environment,
        )
        focus(fixture.pid)
        key("ctrl+a")
        typed(original)
        time.sleep(1)
        assert not status().get("overlayVisible"), "on-demand mode translated before activation"
        key("ctrl+shift+F9")
        wait_for(lambda: view("ValidTranslation"), "wake shortcut did not activate translation")
        key("Escape")
        wait_for(lambda: not status().get("overlayVisible"), "Escape did not hide translation")
        key("ctrl+shift+F10")
        key("Tab")
        changed = wait_for(
            changed_fixture,
            "direct translation did not replace its original field",
        )
        assert changed["focus"] == "fixture-second", "direct translation stole keyboard focus"
        assert changed["second"] == "second field", "direct translation wrote into the new field"
        assert not status().get("overlayVisible"), "direct translation opened the preview"
        evidence.append("on-demand wake, Escape and direct translation bound to the original field")
        stop()
        fixture.terminate()
        fixture.wait(timeout=3)
        fixture = None
        # An independently packaged real application, without a test backend.
        settings["Mode"] = 0
        start()
        application = subprocess.Popen(
            [
                str(package / "typelay"),
                "launch",
                "yad",
                "--entry",
                "--title=TypeLay actual application",
                "--text=Message",
                "--button=OK:0",
                "--entry-text=" + original,
            ],
            env=environment,
            stdout=subprocess.PIPE,
            stderr=sys.stderr,
        )
        focus(application.pid)
        wait_for(lambda: view("ValidTranslation"), "real YAD field did not show translation")
        key("Tab")
        time.sleep(0.15)
        key("Return")
        output, _ = application.communicate(timeout=5)
        assert application.returncode == 0 and translated(output.decode().strip())
        application = None
        evidence.append("unmodified YAD application: real entry translation and accepted output")
        assert control(paths, "settings") is True
        time.sleep(0.3)
        subprocess.run(["grim", str(arguments.output / "settings.png")], check=True, timeout=3)
        stop()
        evidence.append("settings, duplicate background start and graceful desktop shutdown")
        (arguments.output / "desktop-result.json").write_text(
            json.dumps({"passed": evidence}, indent=2)
        )
    finally:
        for child in (application, fixture, process):
            if child is not None and child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()
