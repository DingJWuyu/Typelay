"""Real installed desktop, real CPU model, real GTK fields and an unmodified YAD app."""

import json
import os
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

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

    def ready_to_accept(state="ValidTranslation"):
        data = view(state)
        return data if data and data.get("acceptanceReady") else None

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

    def focus(pid, title=None):
        def select():
            windows = contents(request("Windows"), "Windows")
            return next(
                (
                    window
                    for window in windows
                    if window.get("pid") == pid and (title is None or window.get("title") == title)
                ),
                None,
            )

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
        wait_for(ready_to_accept, "actual translation and Tab acceptance did not become ready")
        subprocess.run(["grim", str(arguments.output / "translation.png")], check=True, timeout=3)
        assert fixture_state()["first"] == original, "preview changed the source before acceptance"
        key("Tab")
        accepted = wait_for(
            changed_fixture,
            "compositor-delivered Tab did not accept the real candidate",
        )
        assert accepted["focus"] == "fixture-first", "Tab also navigated to another field"
        evidence.append(
            "automatic mode: real model preview, compositor-delivered Tab and source focus"
        )
        key("ctrl+a")
        typed(original)
        wait_for(ready_to_accept, "second candidate did not become ready")
        # A real pointer click in the candidate body of this fixed 1280x800
        # isolated desktop fixture. No command injects a draft or changes focus.
        subprocess.run(["xdotool", "mousemove", "340", "232", "click", "1"], check=True)
        wait_for(
            lambda: fixture_state().get("focus") == "typelay-candidate",
            "clicking the candidate did not focus the native translation editor",
        )
        manual = "Final version, approved."
        key("ctrl+a")
        typed(manual)
        key("Tab")

        def returned_draft():
            # The controller renders the pending draft before its asynchronous
            # native focus request completes. Observe the field itself as well.
            current = fixture_state()
            assert current.get("first") == original, "return from editor also accepted the draft"
            return (
                current
                if (
                    current.get("focus") == "fixture-first"
                    and ready_to_accept("EditedTranslationPending")
                )
                else None
            )

        wait_for(returned_draft, "first editor Tab did not return with the edited candidate")
        subprocess.run(["grim", str(arguments.output / "edited.png")], check=True, timeout=3)
        key("Tab")
        wait_for(lambda: fixture_state().get("first") == manual, "second Tab lost the edited draft")
        assert fixture_state()["focus"] == "fixture-first"
        evidence.append(
            "native editor click, manual draft, first Tab returns and second Tab accepts"
        )
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
        # Two real GTK toplevels owned by one process, not guessed screen origins.
        settings["Mode"] = 1
        start()
        fixture = subprocess.Popen(
            [str(package / "typelay"), "launch", str(arguments.native / "gtk-fixture")],
            env={**environment, "TYPELAY_FIXTURE_TWO_WINDOWS": "1"},
        )
        focus(fixture.pid, "TypeLay input contracts")
        key("ctrl+a")
        typed(original)
        # Wake uses the selected GTK parent, even though niri reports two windows
        # with this PID. The original native field identity survives focus changes.
        key("ctrl+shift+F9")
        wait_for(ready_to_accept, "multiple windows disabled the focused native field")
        key("Escape")
        wait_for(lambda: not status().get("overlayVisible"), "multiwindow Escape did not hide")
        key("ctrl+shift+F10")
        focus(fixture.pid, "TypeLay GTK second window")
        changed = wait_for(changed_fixture, "background translation lost its original window")
        assert changed["other"] == "The other window is unchanged."
        assert changed["focus"] == "fixture-other-window", "direct write activated its old window"
        key("ctrl+shift+F9")
        wait_for(ready_to_accept, "the second window did not support translation")
        key("Tab")
        wait_for(
            lambda: translated(fixture_state().get("other")), "second window Tab was misrouted"
        )
        assert fixture_state()["first"] == changed["first"], "second window write changed the first"
        assert fixture_state()["focus"] == "fixture-other-window"
        evidence.append(
            "same-process windows: focused preview and background direct write without misrouting"
        )
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
        wait_for(ready_to_accept, "real YAD field did not grant visible Tab acceptance")
        key("Tab")
        time.sleep(0.15)
        key("Return")
        output, _ = application.communicate(timeout=5)
        assert application.returncode == 0 and translated(output.decode().strip()), (
            application.returncode,
            output.decode(errors="replace"),
        )
        application = None
        evidence.append("unmodified YAD application: real entry translation and accepted output")
        # Exercise the real extension, native host, niri PID authority, shared
        # controller and actual CPU model together, not only a DOM transport seam.
        from playwright.sync_api import sync_playwright

        with tempfile.TemporaryDirectory(prefix="typelay-browser-desktop-") as temporary:
            browser_home = Path(temporary)
            browser_environment = {
                **environment,
                "HOME": str(browser_home),
                "XDG_CONFIG_HOME": str(browser_home / "config"),
                "XDG_DATA_HOME": str(browser_home / "data"),
                "XDG_CACHE_HOME": str(browser_home / "cache"),
            }
            subprocess.run(
                [sys.executable, str(package / "install.py"), "install"],
                env=browser_environment,
                check=True,
                timeout=30,
            )
            extension = package / "browser/chromium"
            with sync_playwright() as playwright:
                browser = playwright.chromium.launch_persistent_context(
                    str(browser_home / "config/chromium"),
                    executable_path="/usr/bin/chromium",
                    headless=False,
                    env=browser_environment,
                    ignore_default_args=["--disable-extensions"],
                    args=[
                        "--ozone-platform=wayland",
                        f"--disable-extensions-except={extension}",
                        f"--load-extension={extension}",
                    ],
                )
                try:
                    worker = (
                        browser.service_workers[0]
                        if browser.service_workers
                        else browser.wait_for_event("serviceworker", timeout=15000)
                    )
                    subprocess.run(
                        [
                            sys.executable,
                            str(package / "install.py"),
                            "browser-install",
                            "--browser",
                            "chromium",
                            "--extension-id",
                            worker.url.split("/")[2],
                        ],
                        env=browser_environment,
                        check=True,
                        timeout=10,
                    )
                    browser.route(
                        "http://127.0.0.1/typelay-contract",
                        lambda route: route.fulfill(
                            content_type="text/html",
                            body="<!doctype html><title>TypeLay browser contracts</title>"
                            "<style>textarea{margin:80px;width:500px;height:100px}</style>"
                            "<label>Message<textarea id='source'></textarea></label><input id='other'>",
                        ),
                    )
                    page = browser.new_page()
                    browser.new_cdp_session(page).send(
                        "Emulation.setFocusEmulationEnabled", {"enabled": False}
                    )
                    page.goto("http://127.0.0.1/typelay-contract")
                    window = wait_for(
                        lambda: next(
                            (
                                item
                                for item in contents(request("Windows"), "Windows")
                                if (item.get("title") or "").startswith("TypeLay browser contracts")
                            ),
                            None,
                        ),
                        "the real Wayland browser window did not map",
                    )
                    request({"Action": {"FocusWindow": {"id": window["id"]}}})
                    page.locator("#source").focus()
                    typed(original)
                    wait_for(
                        ready_to_accept, "browser native route/model did not grant Tab acceptance"
                    )
                    assert page.locator("#source").input_value() == original
                    subprocess.run(
                        ["grim", str(arguments.output / "browser.png")], check=True, timeout=3
                    )
                    key("Tab")
                    wait_for(
                        lambda: translated(page.locator("#source").input_value()),
                        "browser Tab did not accept the real translation",
                    )
                    assert page.evaluate("document.activeElement.id") == "source", (
                        "browser Tab also navigated"
                    )
                    evidence.append(
                        "Wayland Chromium: real extension, native host, compositor process binding, CPU model and Tab"
                    )
                finally:
                    browser.close()
        assert control(paths, "settings") is True
        time.sleep(0.3)
        subprocess.run(["grim", str(arguments.output / "settings.png")], check=True, timeout=3)
        stop()
        evidence.append("settings, duplicate background start and graceful desktop shutdown")
        (arguments.output / "desktop-result.json").write_text(
            json.dumps({"passed": evidence}, indent=2)
        )
    except BaseException:
        try:
            (arguments.output / "failure-status.json").write_text(json.dumps(status(), indent=2))
            subprocess.run(["grim", str(arguments.output / "failure.png")], check=False, timeout=3)
        except (OSError, subprocess.SubprocessError):
            pass  # Diagnostic failures must not replace the original assertion.
        raise
    finally:
        for child in (application, fixture, process):
            if child is not None and child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()
