"""Real model/browser/cooperative Entry acceptance; generic GTK still refuses."""

import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time
from contextlib import ExitStack
from http.server import HTTPServer
from pathlib import Path

from main import control
from native_e2e import key
from niri import contents, request
from paths import Paths
from wire import now_ms


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
    environment = dict(os.environ)
    trace_path = arguments.output / "controller.jsonl"
    environment["TYPELAY_TRACE"] = str(trace_path)
    process = None
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

    def typed(text):
        subprocess.run(
            ["xdotool", "type", "--clearmodifiers", "--delay", "2", text], check=True, timeout=5
        )

    def trace_checkpoint():
        # Stop at a complete record if the controller is appending right now.
        return trace_path.read_bytes().rfind(b"\n") + 1

    def new_write_outcomes(offset):
        with trace_path.open("rb") as trace:
            trace.seek(offset)
            data = trace.read()
        complete = data[: data.rfind(b"\n") + 1]
        return [
            row["operation"]
            for line in complete.splitlines()
            if (row := json.loads(line))["operation"].startswith("write-")
            and row["operation"] != "write-start"
        ]

    def verified_write(offset):
        outcomes = new_write_outcomes(offset)
        assert len(outcomes) <= 1 and all(value == "write-Verified-None" for value in outcomes), (
            f"real controller did not verify this single Tab: {outcomes}"
        )
        return bool(outcomes)

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

    def translated(value):
        return bool(value and value != original and re.search(r"[\u3400-\u9fff]", value))

    page_state = """({focused: document.hasFocus(), visible: !document.hidden,
      active: document.activeElement?.id,
      sourceLength: document.querySelector('#source')?.value.length})"""

    def browser_evidence(name, inspect):
        try:
            (arguments.output / f"{name}-live.json").write_text(
                json.dumps({"desktop": status(), "page": inspect()}, indent=2)
            )
            subprocess.run(
                ["grim", str(arguments.output / f"{name}-live.png")], check=False, timeout=3
            )
        except Exception:
            pass  # Observability must not replace the original browser failure.

    try:
        start()
        # A duplicate background request must use the existing real session.
        subprocess.run([str(package / "typelay"), "--background"], env=environment, check=True)
        assert process.poll() is None
        if arguments.desktop_scope != "gtk":
            # Exercise the real extension, native host, niri PID authority, shared
            # controller and actual CPU model together, not only a DOM transport seam.
            from browser_process import chromium_context
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
                    browser_scope = chromium_context(
                        playwright,
                        "/usr/bin/chromium",
                        browser_home / "config/chromium",
                        browser_environment,
                        [
                            "--ozone-platform=wayland",
                            f"--disable-extensions-except={extension}",
                            f"--load-extension={extension}",
                        ],
                    )
                    browser = browser_scope.__enter__()
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
                        page.goto("http://127.0.0.1/typelay-contract")
                        window = wait_for(
                            lambda: next(
                                (
                                    item
                                    for item in contents(request("Windows"), "Windows")
                                    if (item.get("title") or "").startswith(
                                        "TypeLay browser contracts"
                                    )
                                ),
                                None,
                            ),
                            "the real Wayland browser window did not map",
                        )
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        # DOM focus alone does not leave Chromium's initially
                        # selected address bar. Enter the field as a user does.
                        page.locator("#source").click()
                        wait_for(
                            lambda: page.evaluate(
                                "document.hasFocus() && document.activeElement.id === 'source'"
                            ),
                            "Chromium did not acquire actual Wayland keyboard focus",
                        )
                        typed(original)
                        wait_for(
                            lambda: page.locator("#source").input_value() == original,
                            "XTest input did not reach Chromium source",
                        )
                        wait_for(
                            ready_to_accept,
                            "browser native route/model did not grant Tab acceptance",
                        )
                        assert page.locator("#source").input_value() == original
                        subprocess.run(
                            ["grim", str(arguments.output / "browser.png")], check=True, timeout=3
                        )
                        # Screenshot capture can outlive the 100 ms acceptance permit.
                        # Observe the current readiness after it, then send one Tab only.
                        wait_for(
                            ready_to_accept, "browser acceptance expired while taking evidence"
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
                    except BaseException:
                        browser_evidence("chromium", lambda: page.evaluate(page_state))
                        raise
                    finally:
                        browser_scope.__exit__(None, None, None)
            # Observe teardown of the Chromium source before accepting any Firefox
            # readiness, even though this scenario deliberately uses the same text.
            wait_for(
                lambda: (data := status()) and not data.get("overlayVisible"),
                "Chromium source session did not withdraw before Firefox",
            )
            from firefox_native_e2e import Fixture, firefox

            with (
                tempfile.TemporaryDirectory(prefix="typelay-firefox-desktop-") as temporary,
                ExitStack() as stack,
            ):
                browser_home = Path(temporary)
                firefox_environment = {
                    **environment,
                    "HOME": str(browser_home),
                    "XDG_CONFIG_HOME": str(browser_home / "config"),
                    "XDG_DATA_HOME": str(browser_home / "data"),
                    "XDG_CACHE_HOME": str(browser_home / "cache"),
                    "MOZ_ENABLE_WAYLAND": "1",
                    "GDK_BACKEND": "wayland",
                }
                # Keep the real NIRI_SOCKET, WAYLAND_DISPLAY and XDG_RUNTIME_DIR.
                for operation in (["install"], ["browser-install", "--browser", "firefox"]):
                    subprocess.run(
                        [sys.executable, str(package / "install.py"), *operation],
                        env=firefox_environment,
                        check=True,
                        timeout=30,
                    )
                addon = package / "browser/typelay-firefox-unsigned.xpi"
                server = stack.enter_context(HTTPServer(("127.0.0.1", 0), Fixture))
                server.editor_bundle = arguments.editor_bundle
                thread = threading.Thread(target=server.serve_forever, daemon=True)
                thread.start()
                stack.callback(thread.join, 3)
                stack.callback(server.shutdown)
                web = stack.enter_context(
                    firefox(
                        browser_home,
                        firefox_environment,
                        "/usr/bin/firefox",
                        "/usr/bin/geckodriver",
                        arguments.output / "firefox-geckodriver.log",
                    )
                )
                stack.callback(
                    browser_evidence,
                    "firefox",
                    lambda: web(
                        "POST", "/execute/sync", {"script": "return " + page_state, "args": []}
                    ),
                )
                assert (
                    web("POST", "/moz/addon/install", {"path": str(addon), "temporary": True})
                    == "typelay@typelay.local"
                )
                web("POST", "/url", {"url": f"http://127.0.0.1:{server.server_port}/desktop"})
                window = wait_for(
                    lambda: next(
                        (
                            item
                            for item in contents(request("Windows"), "Windows")
                            if (item.get("title") or "").startswith("TypeLay Firefox contracts")
                        ),
                        None,
                    ),
                    "the real Wayland Firefox window did not map",
                )
                print(
                    "Firefox compositor binding: "
                    + json.dumps({"id": window["id"], "pid": window.get("pid")})
                )
                request({"Action": {"FocusWindow": {"id": window["id"]}}})
                web(
                    "POST",
                    "/execute/sync",
                    {"script": "document.querySelector('#source').focus();", "args": []},
                )
                wait_for(
                    lambda: web(
                        "POST",
                        "/execute/sync",
                        {
                            "script": "return document.hasFocus() && document.activeElement.id === 'source';",
                            "args": [],
                        },
                    ),
                    "Firefox did not acquire actual Wayland keyboard focus",
                )
                typed(original)
                # XTest completion confirms injection, not that the Wayland/browser
                # event queue has applied the final character. Observe the exact
                # source after this one injection, never resend text or relax it.
                wait_for(
                    lambda: web(
                        "POST",
                        "/execute/sync",
                        {"script": "return document.querySelector('#source').value;", "args": []},
                    )
                    == original,
                    "XTest input did not reach Firefox source",
                )
                wait_for(ready_to_accept, "Firefox native route/model did not grant Tab acceptance")

                def firefox_value():
                    return web(
                        "POST",
                        "/execute/sync",
                        {
                            "script": "return document.querySelector('#source').value;",
                            "args": [],
                        },
                    )

                assert firefox_value() == original
                subprocess.run(
                    ["grim", str(arguments.output / "firefox-browser.png")], check=True, timeout=3
                )
                wait_for(ready_to_accept, "Firefox acceptance expired while taking evidence")
                firefox_tab = {"beforeMs": now_ms()}
                key("Tab")
                firefox_tab["afterMs"] = now_ms()
                (arguments.output / "firefox-tab.json").write_text(json.dumps(firefox_tab))
                wait_for(
                    lambda: translated(firefox_value()),
                    "Firefox Tab did not accept the real translation",
                )
                assert (
                    web(
                        "POST",
                        "/execute/sync",
                        {
                            "script": "return document.activeElement.id;",
                            "args": [],
                        },
                    )
                    == "source"
                ), "Firefox Tab also navigated"
                evidence.append(
                    "Wayland Firefox: real extension, native host, compositor process binding, CPU model and Tab"
                )
                if arguments.editor_bundle:
                    web("POST", "/url", {"url": f"http://127.0.0.1:{server.server_port}/rich"})
                    web(
                        "POST",
                        "/execute/sync",
                        {"script": "return editorProbeSetup('guarded');", "args": []},
                    )
                    wait_for(
                        lambda: web(
                            "POST",
                            "/execute/sync",
                            {
                                "script": "return document.hasFocus() && document.activeElement.id === 'editor-source';",
                                "args": [],
                            },
                        ),
                        "Firefox rich editor did not acquire keyboard focus",
                    )
                    key("Shift_L")
                    wait_for(ready_to_accept, "rich editor did not receive a real model candidate")
                    subprocess.run(
                        ["grim", str(arguments.output / "firefox-rich.png")], check=True, timeout=3
                    )
                    wait_for(
                        ready_to_accept, "rich editor acceptance expired while taking evidence"
                    )
                    key("Tab")

                    def rich_value():
                        return web(
                            "POST",
                            "/execute/sync",
                            {"script": "return editorProbeInspect();", "args": []},
                        )

                    actual = wait_for(
                        lambda: value if translated((value := rich_value())["domAfter"]) else None,
                        "rich editor Tab did not accept the actual model result",
                    )
                    assert actual["preserved"] and actual["domAfter"].startswith("Before ")
                    assert actual["domAfter"].endswith(" world after untouched")
                    assert "<strong>" in actual["after"] and "<em>untouched</em>" in actual["after"]
                    assert (
                        web(
                            "POST",
                            "/execute/sync",
                            {"script": "return document.activeElement.id;", "args": []},
                        )
                        == "editor-source"
                    )
                    evidence.append(
                        "Wayland Firefox rich editor: selected-range CPU translation, native transaction, preserved surrounding marks and one Tab"
                    )
        if arguments.fcitx_cooperative and arguments.desktop_scope != "browser":
            cooperation = arguments.fcitx_cooperative
            with tempfile.TemporaryDirectory(prefix="typelay-gtk-cooperative-") as temporary:
                home = Path(temporary)
                config = home / "config/fcitx5"
                config.mkdir(parents=True)
                (config / "profile").write_text(
                    "[Groups/0]\nName=Default\nDefault Layout=us\nDefaultIM=pinyin\n"
                    "[Groups/0/Items/0]\nName=keyboard-us\nLayout=\n"
                    "[Groups/0/Items/1]\nName=pinyin\nLayout=\n[GroupOrder]\n0=Default\n"
                )
                state_path = arguments.output / "gtk-cooperative-state.json"
                gtk_environment = {
                    **environment,
                    "HOME": str(home),
                    "XDG_CONFIG_HOME": str(home / "config"),
                    "XDG_DATA_HOME": str(home / "data"),
                    "XDG_CACHE_HOME": str(home / "cache"),
                    "FCITX_CONFIG_HOME": str(config),
                    "FCITX_DATA_HOME": str(home / "data/fcitx5"),
                    "FCITX_DATA_DIRS": "/usr/share/fcitx5",
                    "FCITX_ADDON_DIRS": str(cooperation / "lib") + ":/usr/lib/fcitx5",
                    "LD_LIBRARY_PATH": str(cooperation / "lib"),
                    "GTK_IM_MODULE": "fcitx",
                    "GTK_IM_MODULE_FILE": str(cooperation / "immodules.cache"),
                    "GDK_BACKEND": "wayland",
                    "GTK3_MODULES": str(arguments.native / "libtypelay-gtk3.so"),
                    "TYPELAY_FIXTURE_STATE": str(state_path),
                    "TYPELAY_FIXTURE_INITIAL_TEXT": original,
                    "TYPELAY_FIXTURE_COOPERATIVE": "1",
                    "TYPELAY_FIXTURE_FULLSCREEN": "1",
                    "TYPELAY_FCITX_COOPERATIVE": "1",
                }
                core = cooperation / "core-prefix"
                if core.is_dir():
                    gtk_environment.update(
                        LD_LIBRARY_PATH=f"{cooperation / 'lib'}:{core / 'lib'}",
                        FCITX_ADDON_DIRS=f"{cooperation / 'lib'}:{core / 'lib/fcitx5'}",
                        FCITX_DATA_DIRS=f"{core / 'share/fcitx5'}:/usr/share/fcitx5",
                    )
                gtk_environment.pop("TYPELAY_DESKTOP", None)
                daemon = fixture = None
                try:
                    with (arguments.output / "gtk-cooperative-framework.log").open("w") as log:
                        daemon = subprocess.Popen(
                            [
                                str(cooperation / "fcitx5"),
                                "--disable=all",
                                "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell,quickphrase",
                            ],
                            env=gtk_environment,
                            stdout=log,
                            stderr=subprocess.STDOUT,
                        )
                        fixture = subprocess.Popen(
                            [str(arguments.native / "gtk-fixture")],
                            env=gtk_environment,
                            stdout=sys.stdout,
                            stderr=sys.stderr,
                        )

                        def gtk_state():
                            assert daemon.poll() is None and fixture.poll() is None
                            try:
                                return json.loads(state_path.read_text())
                            except (FileNotFoundError, json.JSONDecodeError):
                                return {}

                        def switch_im(name):
                            subprocess.run(
                                ["fcitx5-remote", "-s", name],
                                env=gtk_environment,
                                check=True,
                                timeout=3,
                            )
                            wait_for(
                                lambda: subprocess.check_output(
                                    ["fcitx5-remote", "-n"],
                                    env=gtk_environment,
                                    text=True,
                                    timeout=3,
                                ).strip()
                                == name
                                and gtk_state().get("focusedImeClear"),
                                f"actual focused {name} context did not settle",
                            )

                        def enter_source(text):
                            assert gtk_state()["focus"] == "fixture-first"
                            key("ctrl+a")
                            key("BackSpace")
                            wait_for(
                                lambda: gtk_state().get("first") == ""
                                and gtk_state().get("imeClear")
                                and not status().get("acceptanceReady"),
                                "source clearing did not settle and withdraw the old permit",
                            )
                            typed(text)
                            wait_for(
                                lambda: gtk_state().get("first") == text
                                and gtk_state().get("imeClear"),
                                "real keyboard input did not reach the exact GTK source",
                            )
                            wait_for(
                                ready_to_accept,
                                "typed GTK source did not receive a model candidate",
                            )

                        window = wait_for(
                            lambda: next(
                                (
                                    item
                                    for item in contents(request("Windows"), "Windows")
                                    if item.get("pid") == fixture.pid
                                ),
                                None,
                            ),
                            "cooperative GTK window did not map under niri",
                        )
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        wait_for(
                            lambda: gtk_state().get("imeFocused"),
                            "real cooperating input context did not focus",
                        )
                        subprocess.run(["fcitx5-remote", "-o"], env=gtk_environment, check=True)
                        subprocess.run(
                            ["fcitx5-remote", "-s", "pinyin"], env=gtk_environment, check=True
                        )
                        wait_for(
                            lambda: gtk_state().get("imeClear"),
                            "pinyin capability did not become clear",
                        )
                        wait_for(
                            ready_to_accept,
                            "cooperative GTK source did not receive a real model candidate",
                        )
                        assert gtk_state()["first"] == original
                        subprocess.run(
                            ["grim", str(arguments.output / "gtk-cooperative.png")],
                            check=True,
                            timeout=3,
                        )
                        wait_for(ready_to_accept, "GTK acceptance expired while taking evidence")
                        before_write = trace_checkpoint()
                        key("Tab")
                        wait_for(
                            lambda: translated(gtk_state().get("first")),
                            "GTK Tab did not apply the real translation",
                        )
                        wait_for(
                            lambda: verified_write(before_write),
                            "GTK changed text without a verified production write receipt",
                        )
                        assert gtk_state()["focus"] == "fixture-first"
                        switch_im("keyboard-us")
                        typed_source = "Please send me the latest report."
                        enter_source(typed_source)
                        before_write = trace_checkpoint()
                        key("Tab")
                        wait_for(
                            lambda: translated(gtk_state().get("first")),
                            "GTK Tab did not replace the actually typed source",
                        )
                        wait_for(
                            lambda: verified_write(before_write),
                            "actually typed GTK source did not receive a verified write receipt",
                        )
                        assert gtk_state()["focus"] == "fixture-first"
                        evidence.append(
                            "Wayland GTK keyboard-us: actual typed source, CPU translation, one Tab, verified write and retained focus"
                        )

                        editing_source = "Please review this note."
                        enter_source(editing_source)
                        actual = wait_for(
                            lambda: value
                            if (value := gtk_state()).get("fullscreen") and value.get("candidate")
                            else None,
                            "fullscreen GTK candidate did not expose real widget geometry",
                        )
                        geometry = dict(
                            line.split("=", 1)
                            for line in subprocess.check_output(
                                ["xdotool", "getwindowgeometry", "--shell", nested[0]],
                                text=True,
                                timeout=3,
                            ).splitlines()
                        )
                        scale = actual["scale"]
                        assert actual["width"] * scale == int(geometry["WIDTH"])
                        assert actual["height"] * scale == int(geometry["HEIGHT"])
                        candidate = actual["candidate"]
                        x = candidate["x"] + candidate["width"] // 2
                        y = candidate["y"] + candidate["height"] // 2
                        assert 0 <= x < actual["width"] and 0 <= y < actual["height"]
                        subprocess.run(
                            [
                                "xdotool",
                                "mousemove",
                                "--window",
                                nested[0],
                                str(x * scale),
                                str(y * scale),
                                "click",
                                "1",
                            ],
                            check=True,
                            timeout=3,
                        )
                        wait_for(
                            lambda: gtk_state().get("focus") == "typelay-candidate"
                            and view("EditingTranslation"),
                            "clicking the actual GTK candidate did not enter the production editor",
                        )
                        switch_im("keyboard-us")
                        draft = "Manually edited candidate."
                        key("ctrl+a")
                        typed(draft)
                        wait_for(
                            lambda: (gtk_state().get("candidate") or {}).get("text") == draft
                            and gtk_state().get("focusedImeClear"),
                            "real GTK editor did not retain the typed draft",
                        )
                        assert gtk_state()["first"] == editing_source
                        before_return = trace_checkpoint()
                        key("Tab")
                        wait_for(
                            lambda: gtk_state().get("focus") == "fixture-first"
                            and gtk_state().get("imeClear")
                            and ready_to_accept("EditedTranslationPending"),
                            "first editor Tab did not return with the draft awaiting acceptance",
                        )
                        assert gtk_state()["first"] == editing_source
                        assert not new_write_outcomes(before_return), (
                            "first editor Tab performed a write"
                        )
                        before_write = trace_checkpoint()
                        key("Tab")
                        wait_for(
                            lambda: gtk_state().get("first") == draft,
                            "second Tab did not accept the exact manual draft",
                        )
                        wait_for(
                            lambda: verified_write(before_write),
                            "manual draft acceptance did not receive a verified write receipt",
                        )
                        assert gtk_state()["focus"] == "fixture-first"
                        evidence.append(
                            "Wayland GTK editor: actual candidate click and draft typing, first Tab returns without writing, second Tab verifies the exact draft"
                        )

                        switch_im("pinyin")
                        key("ctrl+a")
                        key("BackSpace")
                        wait_for(
                            lambda: gtk_state().get("first") == "" and gtk_state().get("imeClear"),
                            "source clearing did not settle",
                        )
                        typed("nihao")
                        wait_for(
                            lambda: gtk_state().get("imeComposing"),
                            "real pinyin composition was not observed",
                        )
                        wait_for(
                            lambda: not status().get("acceptanceReady"),
                            "composition retained a write permit",
                        )
                        assert gtk_state()["first"] == ""
                        key("space")
                        wait_for(
                            lambda: bool(
                                re.search(r"[\u3400-\u9fff]", gtk_state().get("first", ""))
                            ),
                            "pinyin candidate did not commit normally",
                        )
                        evidence.append(
                            "Wayland GTK Entry with opt-in Fcitx cooperation: CPU model, native overlay, one Tab and preserved real pinyin composition"
                        )
                finally:
                    for child in (fixture, daemon):
                        if child is not None and child.poll() is None:
                            child.terminate()
                            try:
                                child.wait(timeout=5)
                            except subprocess.TimeoutExpired:
                                child.kill()
                                child.wait()
        assert control(paths, "settings") is True
        time.sleep(0.3)
        subprocess.run(["grim", str(arguments.output / "settings.png")], check=True, timeout=3)
        stop()
        evidence.append("settings, duplicate background start and graceful desktop shutdown")
        (arguments.output / "desktop-result.json").write_text(
            json.dumps(
                {
                    "scope": arguments.desktop_scope + "-desktop-and-lifecycle",
                    "passed": evidence,
                    "unsupported": [
                        "uncooperative GTK inputs, GTK TextView, Qt, GTK4 and generic Electron"
                    ],
                },
                indent=2,
            )
        )
    except BaseException:
        try:
            (arguments.output / "failure-status.json").write_text(json.dumps(status(), indent=2))
            subprocess.run(["grim", str(arguments.output / "failure.png")], check=False, timeout=3)
        except (OSError, subprocess.SubprocessError):
            pass  # Diagnostic failures must not replace the original assertion.
        raise
    finally:
        for child in (process,):
            if child is not None and child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()
