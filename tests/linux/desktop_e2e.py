"""Real model/browser/cooperative Entry acceptance; generic GTK still refuses."""

import ctypes
import json
import os
import re
import select
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


def prepare_tab(stack):
    """Preconnect to the existing XTest server; deliver one real key pair later."""
    x11 = ctypes.CDLL("libX11.so.6")
    xtst = ctypes.CDLL("libXtst.so.6")
    display_type = ctypes.c_void_p
    x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
    x11.XOpenDisplay.restype = display_type
    x11.XCloseDisplay.argtypes = [display_type]
    x11.XCloseDisplay.restype = ctypes.c_int
    x11.XStringToKeysym.argtypes = [ctypes.c_char_p]
    x11.XStringToKeysym.restype = ctypes.c_ulong
    x11.XKeysymToKeycode.argtypes = [display_type, ctypes.c_ulong]
    x11.XKeysymToKeycode.restype = ctypes.c_ubyte
    x11.XSync.argtypes = [display_type, ctypes.c_int]
    x11.XSync.restype = ctypes.c_int
    xtst.XTestQueryExtension.argtypes = [display_type] + [ctypes.POINTER(ctypes.c_int)] * 4
    xtst.XTestQueryExtension.restype = ctypes.c_int
    xtst.XTestFakeKeyEvent.argtypes = [display_type, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
    xtst.XTestFakeKeyEvent.restype = ctypes.c_int
    display = x11.XOpenDisplay(None)
    assert display, "Cannot open existing XTest DISPLAY"
    stack.callback(x11.XCloseDisplay, display)
    extension = [ctypes.c_int() for _ in range(4)]
    assert xtst.XTestQueryExtension(display, *(ctypes.byref(value) for value in extension))
    keycode = x11.XKeysymToKeycode(display, x11.XStringToKeysym(b"Tab"))
    assert keycode, "XTest DISPLAY has no Tab keycode"

    def send_tab():
        pressed = xtst.XTestFakeKeyEvent(display, keycode, 1, 0)
        released = xtst.XTestFakeKeyEvent(display, keycode, 0, 0)
        # Flush and wait only for X server processing, not Wayland delivery.
        x11.XSync(display, 0)
        assert pressed and released, "XTest refused the Tab key pair"

    return send_tab


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
        "OnboardingComplete": arguments.desktop_scope != "setup",
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

    resources = ExitStack()
    try:
        send_tab = (
            prepare_tab(resources) if arguments.desktop_scope in ("all", "browser", "qt") else None
        )
        start()
        # A duplicate background request must use the existing real session.
        subprocess.run([str(package / "typelay"), "--background"], env=environment, check=True)
        assert process.poll() is None
        if arguments.desktop_scope in ("all", "browser"):
            # Exercise the real extension, native host, niri PID authority, shared
            # controller and actual CPU model together, not only a DOM transport seam.
            from playwright.sync_api import sync_playwright
            from support.browser_process import chromium_context

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
                        send_tab()
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
                ready = wait_for(
                    ready_to_accept, "Firefox acceptance expired while taking evidence"
                )
                firefox_tab = {
                    "beforeMs": now_ms(),
                    "reportedRemainingMs": ready.get("acceptanceRemainingMs"),
                }
                send_tab()
                firefox_tab["afterMs"] = now_ms()
                # Preserve the already-read rounds after injection, before failure
                # polling can replace them. Do not add a read to the key path.
                firefox_tab.update(
                    {
                        name: ready.get("input", {}).get(name)
                        for name in ("browserRounds", "browserCaptureCounts")
                    }
                )
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

                    def rich_ready_to_accept():
                        started = now_ms()
                        candidate = ready_to_accept()
                        status_elapsed_ms = now_ms() - started
                        if not candidate:
                            return None
                        if candidate["acceptanceRemainingMs"] - status_elapsed_ms <= 0:
                            return None
                        return {**candidate, "statusElapsedMs": status_elapsed_ms}

                    ready = wait_for(
                        rich_ready_to_accept, "rich editor acceptance expired while taking evidence"
                    )
                    rich_tab = {
                        "beforeMs": now_ms(),
                        "reportedRemainingMs": ready.get("acceptanceRemainingMs"),
                        "statusElapsedMs": ready["statusElapsedMs"],
                    }
                    send_tab()
                    rich_tab["afterMs"] = now_ms()
                    rich_tab.update(
                        {
                            name: ready.get("input", {}).get(name)
                            for name in ("browserRounds", "browserCaptureCounts")
                        }
                    )
                    (arguments.output / "rich-tab.json").write_text(json.dumps(rich_tab))

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
        if arguments.fcitx_cooperative and arguments.desktop_scope in ("all", "gtk", "gtk4"):
            gtk4 = arguments.desktop_scope == "gtk4"
            cooperation = arguments.fcitx_cooperative
            bundle_launcher = cooperation / "fcitx-cooperative.py"
            bundled = bundle_launcher.is_file()
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
                if bundled:
                    # Exercise the public per-process launcher with the normal
                    # addon configuration, not the earlier restricted fixture env.
                    for name in (
                        "FCITX_DATA_DIRS",
                        "FCITX_ADDON_DIRS",
                        "LD_LIBRARY_PATH",
                        "GTK_IM_MODULE",
                        "GTK_IM_MODULE_FILE",
                        "TYPELAY_FCITX_COOPERATIVE",
                    ):
                        gtk_environment.pop(name, None)
                else:
                    gtk_environment["TYPELAY_FCITX_AUDITED_ADDON_DIRS"] = (
                        f"{cooperation / 'lib'}:{core / 'lib/fcitx5'}"
                    )
                daemon = fixture = practice = None
                try:
                    with (arguments.output / "gtk-cooperative-framework.log").open("w") as log:
                        daemon_command = (
                            [sys.executable, str(bundle_launcher), "daemon"]
                            if bundled
                            else [
                                str(cooperation / "fcitx5"),
                                "--disable=all",
                                "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell,quickphrase",
                            ]
                        )
                        daemon = subprocess.Popen(
                            daemon_command,
                            env=gtk_environment,
                            stdout=log,
                            stderr=subprocess.STDOUT,
                        )
                        if bundled:

                            def bus_call(method, *values, controller=False):
                                return subprocess.check_output(
                                    [
                                        "gdbus",
                                        "call",
                                        "--session",
                                        "--dest",
                                        "org.fcitx.Fcitx5"
                                        if controller
                                        else "org.freedesktop.DBus",
                                        "--object-path",
                                        "/controller" if controller else "/org/freedesktop/DBus",
                                        "--method",
                                        (
                                            "org.fcitx.Fcitx.Controller1."
                                            if controller
                                            else "org.freedesktop.DBus."
                                        )
                                        + method,
                                        *values,
                                    ],
                                    env=gtk_environment,
                                    text=True,
                                    stderr=subprocess.DEVNULL,
                                    timeout=3,
                                ).strip()

                            def private_owner():
                                assert daemon.poll() is None
                                try:
                                    owner = bus_call("GetNameOwner", "org.fcitx.Fcitx5")
                                    pid_text = bus_call(
                                        "GetConnectionUnixProcessID", "org.fcitx.Fcitx5"
                                    )
                                except subprocess.CalledProcessError:
                                    return None
                                pid = int(re.fullmatch(r"\(uint32 (\d+),\)", pid_text)[1])
                                assert pid == daemon.pid
                                assert (
                                    Path(f"/proc/{pid}/exe").resolve()
                                    == (cooperation / "core/bin/fcitx5").resolve()
                                )
                                assert b"--disable=ibusfrontend" in Path(
                                    f"/proc/{pid}/cmdline"
                                ).read_bytes().split(b"\0")
                                return owner

                            old_owner = wait_for(
                                private_owner,
                                "relocated private Fcitx did not own its original session bus",
                            )
                            duplicate = subprocess.run(
                                daemon_command,
                                env=gtk_environment,
                                stdout=log,
                                stderr=subprocess.STDOUT,
                                timeout=30,
                            )
                            assert duplicate.returncode != 0 and private_owner() == old_owner
                            # Observe the actual bundled config script opening, not
                            # a source-string check or a replacement GUI command.
                            libc = ctypes.CDLL(None, use_errno=True)
                            libc.inotify_add_watch.argtypes = [
                                ctypes.c_int,
                                ctypes.c_char_p,
                                ctypes.c_uint32,
                            ]
                            watch = libc.inotify_init1(os.O_NONBLOCK | os.O_CLOEXEC)
                            assert watch >= 0
                            try:
                                script = cooperation / "core/bin/fcitx5-configtool"
                                assert libc.inotify_add_watch(watch, os.fsencode(script), 0x20) >= 0
                                bus_call("Configure", controller=True)
                                assert select.select([watch], [], [], 5)[0], (
                                    "private Configure did not open its relocated script"
                                )
                                assert os.read(watch, 4096)
                            finally:
                                os.close(watch)
                            bus_call("Restart", controller=True)
                            wait_for(
                                lambda: (owner := private_owner()) and owner != old_owner,
                                "private Restart did not preserve its executable and startup arguments",
                            )
                            evidence.append(
                                "Relocated private Fcitx: existing-owner refusal, real Configure script, foreground Restart and original session bus"
                            )
                        fixture = subprocess.Popen(
                            (
                                [
                                    sys.executable,
                                    str(bundle_launcher),
                                    "launch",
                                    *(["--gtk4"] if gtk4 else []),
                                ]
                                if bundled
                                else []
                            )
                            + [str(arguments.native / ("gtk4-fixture" if gtk4 else "gtk-fixture"))],
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
                        if gtk4:
                            switch_im("keyboard-us")
                            source_before = gtk_state()["first"]
                            key("ctrl+shift+u")
                            wait_for(
                                lambda: not gtk_state().get("imeClear")
                                and not status().get("acceptanceReady"),
                                "GTK4 Unicode composition retained Clear or a write permit",
                            )
                            typed("41")
                            assert gtk_state()["first"] == source_before
                            key("space")
                            wait_for(
                                lambda: gtk_state().get("first") == source_before + "A"
                                and gtk_state().get("imeClear"),
                                "GTK4 Unicode composition did not commit normally",
                            )
                            evidence.append(
                                "GTK4 actual Unicode composition blocks TypeLay and commits through the original input method"
                            )
                        switch_im("keyboard-us")
                        multiline = gtk_state()["multiline"]
                        multiline_source = multiline["text"]
                        prefix, suffix = "Before 😀\n", "\nAfter untouched"
                        subprocess.run(
                            [
                                "xdotool",
                                "mousemove",
                                "--sync",
                                str(round(multiline["x"] + 12)),
                                str(round(multiline["y"] + 12)),
                                "click",
                                "1",
                            ],
                            check=True,
                            timeout=3,
                        )
                        wait_for(
                            lambda: gtk_state().get("focus") == "fixture-multiline"
                            and gtk_state().get("focusedImeClear"),
                            "GTK multiline input did not acquire its actual clear IM context",
                        )
                        key("ctrl+Home")
                        key("Down")
                        key("Home")
                        key("shift+Down")
                        key("shift+End")
                        wait_for(
                            lambda: gtk_state()["multiline"]["start"] == len(prefix)
                            and gtk_state()["multiline"]["end"]
                            == len(multiline_source) - len(suffix)
                            and ready_to_accept(),
                            "GTK multiline selected range did not receive a candidate",
                        )
                        before_write = trace_checkpoint()
                        before_actions = gtk_state().get("userActionsStarted", 0)
                        key("Tab")
                        multiline_after = wait_for(
                            lambda: value
                            if (value := gtk_state()["multiline"]["text"]) != multiline_source
                            else None,
                            "GTK multiline Tab did not replace the selected range",
                        )
                        wait_for(
                            lambda: verified_write(before_write),
                            "GTK multiline write lacked a verified receipt",
                        )
                        assert multiline_after.startswith(prefix) and multiline_after.endswith(
                            suffix
                        )
                        middle = multiline_after[len(prefix) : -len(suffix)]
                        assert "\n" in middle and re.search(r"[\u3400-\u9fff]", middle)
                        assert gtk_state()["focus"] == "fixture-multiline"
                        if not gtk4:
                            assert gtk_state()["userActionsStarted"] == before_actions + 1
                            assert gtk_state()["userActionsFinished"] == before_actions + 1
                        if gtk4:
                            assert gtk_state()["multiline"]["canUndo"]
                            key("ctrl+z")
                            wait_for(
                                lambda: gtk_state()["multiline"]["text"] == multiline_source,
                                "GTK4 native undo did not restore the selected replacement",
                            )
                            key("ctrl+shift+z")
                            wait_for(
                                lambda: gtk_state()["multiline"]["text"] == multiline_after,
                                "GTK4 native redo did not restore the translated range",
                            )
                        switch_im("pinyin")
                        key("ctrl+End")
                        typed("nihao")
                        wait_for(
                            lambda: gtk_state().get("focusedImeComposing")
                            and not status().get("acceptanceReady"),
                            "GTK multiline real pinyin did not revoke acceptance",
                        )
                        assert gtk_state()["multiline"]["text"] == multiline_after
                        key("space")
                        wait_for(
                            lambda: gtk_state()["multiline"]["text"] != multiline_after
                            and gtk_state().get("focusedImeClear"),
                            "GTK multiline pinyin did not commit normally",
                        )
                        switch_im("keyboard-us")
                        evidence.append(
                            "GTK plain TextView: selected multiline CPU translation, UTF-16 emoji surroundings, one Tab and real pinyin protection"
                            + (
                                "; GTK4 native undo/redo"
                                if gtk4
                                else "; GTK3 host user-action grouping"
                            )
                        )
                        for field in gtk_state()["excludedTextViews"]:
                            before_write = trace_checkpoint()
                            subprocess.run(
                                [
                                    "xdotool",
                                    "mousemove",
                                    "--sync",
                                    str(round(field["x"] + 12)),
                                    str(round(field["y"] + 12)),
                                    "click",
                                    "1",
                                ],
                                check=True,
                                timeout=3,
                            )
                            wait_for(
                                lambda: gtk_state().get("focus") == field["name"]
                                and status().get("input", {}).get("captureStatus")
                                in ("excluded-or-sensitive", "no-native-field")
                                and not status().get("acceptanceReady"),
                                "GTK non-plain TextView was not refused before capture",
                            )
                            assert not new_write_outcomes(before_write)
                        evidence.append(
                            "GTK tagged, embedded-object and readonly TextViews: refused before source capture"
                        )
                        if gtk4:
                            source_before = gtk_state()["first"]
                            for field in gtk_state()["protectedFields"]:
                                before_write = trace_checkpoint()
                                subprocess.run(
                                    [
                                        "xdotool",
                                        "mousemove",
                                        "--sync",
                                        str(round(field["x"] + field["width"] / 2)),
                                        str(round(field["y"] + field["height"] / 2)),
                                        "click",
                                        "1",
                                    ],
                                    check=True,
                                    timeout=3,
                                )
                                wait_for(
                                    lambda: gtk_state().get("focus") == field["name"]
                                    and status().get("input", {}).get("captureStatus")
                                    in ("excluded-or-sensitive", "no-native-field")
                                    and not status().get("acceptanceReady"),
                                    "GTK4 protected field was not refused before capture",
                                )
                                key("Tab")
                                wait_for(
                                    lambda: gtk_state().get("focus") != field["name"],
                                    "protected-field Tab was intercepted",
                                )
                                assert gtk_state()["first"] == source_before
                                assert not new_write_outcomes(before_write)
                            evidence.append(
                                "GTK4 hidden Entry and GtkPasswordEntry: refused capture, unchanged source and ordinary Tab navigation"
                            )
                        # Restart the same production client with persisted OnDemand
                        # settings. No fake translator or test-only command dispatch.
                        stop()
                        settings["Mode"] = 1
                        start()
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})

                        def click_entry(name):
                            value = gtk_state()
                            entry = value[name + "Entry"]
                            # GtkSearchEntry owns icon clicks without focusing its text.
                            subprocess.run(
                                [
                                    "xdotool",
                                    "mousemove",
                                    "--window",
                                    nested[0],
                                    str(int((entry["x"] + entry["width"] / 2) * value["scale"])),
                                    str(int((entry["y"] + entry["height"] / 2) * value["scale"])),
                                    "click",
                                    "1",
                                ],
                                check=True,
                                timeout=3,
                            )
                            wait_for(
                                lambda: gtk_state().get("focus") == "fixture-" + name,
                                "actual mouse click did not focus the GTK " + name + " entry",
                            )

                        click_entry("first")
                        switch_im("pinyin")
                        key("ctrl+a")
                        key("BackSpace")
                        wait_for(
                            lambda: gtk_state().get("first") == "" and gtk_state().get("imeClear"),
                            "OnDemand refusal source did not clear through real input",
                        )
                        typed("nihao")
                        wait_for(
                            lambda: gtk_state().get("imeComposing")
                            and not status().get("acceptanceReady"),
                            "OnDemand refusal did not observe real pinyin composition",
                        )
                        before_refusal = status().get("commandFeedbackRevision", 0)
                        refusal_source = gtk_state()
                        before_write = trace_checkpoint()
                        key("ctrl+shift+F10")
                        refusal = wait_for(
                            lambda: value
                            if (value := status()).get("commandFeedbackRevision", 0)
                            > before_refusal
                            and (value.get("operationFeedback") or {}).get("code")
                            == "shortcut-imeunclear"
                            else None,
                            "one GTK F10 during composition did not report its safe refusal",
                        )
                        assert refusal.get("operationStatus")
                        feedback_observed_at = now_ms()
                        refused_source = wait_for(
                            lambda: value
                            if (value := gtk_state()).get("observedAtMs", 0) >= feedback_observed_at
                            else None,
                            "GTK source state did not advance beyond the refusal feedback",
                        )
                        assert refused_source["first"] == "" and refused_source.get("imeComposing")
                        for counter in ("insertions", "deletions"):
                            assert (
                                refused_source["firstEntry"][counter]
                                == refusal_source["firstEntry"][counter]
                            )
                        assert not new_write_outcomes(before_write)
                        key("space")
                        committed_source = wait_for(
                            lambda: value
                            if bool(
                                re.search(
                                    r"[\u3400-\u9fff]", (value := gtk_state()).get("first", "")
                                )
                            )
                            and value.get("imeClear")
                            else None,
                            "refused F10 disturbed the original pinyin candidate commit",
                        )
                        assert (
                            committed_source["firstEntry"]["insertions"]
                            == refused_source["firstEntry"]["insertions"] + 1
                        )
                        assert (
                            committed_source["firstEntry"]["deletions"]
                            == refused_source["firstEntry"]["deletions"]
                        )
                        evidence.append(
                            "GTK explicit F10 during real pinyin composition: shared refusal feedback, no write, original candidate still commits"
                        )

                        pending_direct = "正在直接翻译；可以切换输入框，当前操作仍绑定原输入框。"
                        for changes_source in (False, True):
                            click_entry("first")
                            switch_im("keyboard-us")
                            source = (
                                "Please send me the updated schedule. We need to review the final "
                                "version before the meeting and share the changes with the team."
                                if not changes_source
                                else "Please keep the revised message safe. The application may update "
                                "this field while the original translation is still being prepared."
                            )
                            key("ctrl+a")
                            key("BackSpace")
                            wait_for(
                                lambda: gtk_state().get("first") == ""
                                and gtk_state().get("imeClear"),
                                "OnDemand GTK source did not clear through real input",
                            )
                            typed(source)
                            wait_for(
                                lambda: gtk_state().get("first") == source
                                and gtk_state().get("imeClear"),
                                "OnDemand GTK source did not receive the actual keyboard text",
                            )
                            captures = (
                                status()
                                .get("input", {})
                                .get("captureCounts", {})
                                .get("supported", 0)
                            )
                            wait_for(
                                lambda: not status().get("overlayVisible")
                                and status().get("input", {}).get("captureStatus") == "supported"
                                and status()
                                .get("input", {})
                                .get("captureCounts", {})
                                .get("supported", 0)
                                > captures,
                                "OnDemand GTK source did not remain dormant with a fresh capture",
                            )
                            before = gtk_state()
                            first_before, second_before = (
                                before["firstEntry"],
                                before["secondEntry"],
                            )
                            assert first_before["start"] == len(source)
                            assert first_before["end"] == len(source)
                            assert status().get("serviceStatus") != pending_direct
                            before_write = trace_checkpoint()
                            key("ctrl+shift+F10")
                            dispatched_at = now_ms()
                            wait_for(
                                lambda: status().get("serviceStatus") == pending_direct,
                                "single GTK F10 did not dispatch before the background transition; "
                                "a model result that already completed cannot prove this case",
                            )
                            dispatched = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("observedAtMs", 0) >= dispatched_at
                                and value.get("imeClear")
                                and (value["firstEntry"]["ime"] or {}).get("flags") == 3
                                else None,
                                "GTK F10 modifier releases did not retire in the actual source context",
                            )
                            assert (
                                dispatched["first"] == source
                                and dispatched["firstEntry"]["focused"]
                            )
                            if changes_source:
                                # A real post-snapshot edit must survive the eventual
                                # model result. There is no retry or model delay.
                                key("exclam")
                                wait_for(
                                    lambda: gtk_state().get("first") == source + "!",
                                    "actual post-snapshot GTK source edit did not arrive",
                                )
                            click_entry("second")
                            key("ctrl+a")
                            detached = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("focus") == "fixture-second"
                                and value["secondEntry"]["start"] == 0
                                and value["secondEntry"]["end"] == len(value["second"])
                                and value.get("focusedImeClear")
                                else None,
                                "background GTK destination did not retain real focus and selection",
                            )
                            case = "changed-source" if changes_source else "retained-source"
                            record = {
                                "before": before,
                                "dispatched": dispatched,
                                "detached": detached,
                            }
                            record_path = arguments.output / (
                                "gtk-background-direct-" + case + ".json"
                            )
                            record_path.write_text(json.dumps(record, indent=2))
                            assert detached["first"] == source + ("!" if changes_source else ""), (
                                "the real model completed before A detached; background write was not exercised"
                            )
                            first_ime = detached["firstEntry"]["ime"]
                            assert first_ime and first_ime["flags"] == 17
                            assert first_ime["server"] == first_ime["applied"]
                            assert (
                                first_ime["context"] == dispatched["firstEntry"]["ime"]["context"]
                            )
                            if not changes_source:
                                assert (
                                    first_ime["input"] == dispatched["firstEntry"]["ime"]["input"]
                                )
                            terminal = (
                                "直接写回未获确认，请检查原输入框；不会自动重试。"
                                if changes_source
                                else "已直接替换原输入框文字。"
                            )
                            wait_for(
                                lambda: status().get("serviceStatus") == terminal,
                                "background GTK direct translation did not reach its real terminal outcome",
                            )
                            terminal_at = now_ms()
                            after = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("observedAtMs", 0) >= terminal_at
                                else None,
                                "GTK fixture did not observe the completed direct operation",
                            )
                            first_after, second_after = after["firstEntry"], after["secondEntry"]
                            record["after"] = after
                            record["terminal"] = status().get("serviceStatus")
                            record_path.write_text(json.dumps(record, indent=2))
                            assert after["focus"] == "fixture-second"
                            assert after["second"] == before["second"]
                            for member in (
                                "focused",
                                "start",
                                "end",
                                "insertions",
                                "deletions",
                                "focusIn",
                                "focusOut",
                            ):
                                assert second_after[member] == detached["secondEntry"][member]
                            assert second_after["insertions"] == second_before["insertions"]
                            assert second_after["deletions"] == second_before["deletions"]
                            assert not first_after["focused"]
                            assert first_after["focusIn"] == first_before["focusIn"]
                            assert first_after["focusOut"] == first_before["focusOut"] + 1
                            assert not new_write_outcomes(before_write), (
                                "direct work used the Tab write path"
                            )
                            if changes_source:
                                assert after["first"] == source + "!"
                                assert first_after["insertions"] == first_before["insertions"] + 1
                                assert first_after["deletions"] == first_before["deletions"]
                            else:
                                assert translated(after["first"])
                                # Standard GtkEntryBuffer exposes one delete and
                                # one insert for this one real direct replacement.
                                assert first_after["insertions"] == first_before["insertions"] + 1
                                assert first_after["deletions"] == first_before["deletions"] + 1
                                for member in ("firstInsertContext", "firstDeleteContext"):
                                    changed = after[member]
                                    assert (
                                        changed["secondFocused"]
                                        and changed["secondText"] == before["second"]
                                    )
                                    assert (
                                        changed["secondStart"] == detached["secondEntry"]["start"]
                                    )
                                    assert changed["secondEnd"] == detached["secondEntry"]["end"]
                                    assert changed["firstFocusIn"] == first_before["focusIn"]
                                    assert changed["firstFocusOut"] == first_after["focusOut"]
                        evidence.append(
                            "GTK Entry background direct: one real F10/CPU-model write to A while B "
                            "keeps focus, text and selection; a post-snapshot source edit is retained "
                            "and the model write is rejected"
                        )
                        if not gtk4 and bundled:
                            # The shipped practice window has no test snapshot or
                            # translator. Observe only its owned accessibility tree.
                            from support.gtk_accessibility import bounds, focused, named, text

                            request({"Action": {"CloseWindow": {"id": window["id"]}}})
                            assert fixture.wait(timeout=5) == 0
                            stop()
                            settings["OnboardingComplete"] = False
                            settings["Mode"] = 0
                            start()
                            assert not status()["setup"]["onboardingComplete"]
                            practice_environment = dict(gtk_environment)
                            practice_environment.pop("GTK3_MODULES", None)
                            practice = subprocess.Popen(
                                [
                                    sys.executable,
                                    str(bundle_launcher),
                                    "launch",
                                    str(package / "typelay"),
                                    "--practice",
                                ],
                                env=practice_environment,
                                stdout=sys.stdout,
                                stderr=sys.stderr,
                            )
                            practice_window = wait_for(
                                lambda: next(
                                    (
                                        item
                                        for item in contents(request("Windows"), "Windows")
                                        if item.get("pid") == practice.pid
                                    ),
                                    None,
                                ),
                                "shipped practice window did not map",
                            )
                            request({"Action": {"FocusWindow": {"id": practice_window["id"]}}})
                            request({"Action": {"FullscreenWindow": {"id": practice_window["id"]}}})
                            source = wait_for(
                                lambda: named(practice.pid, "练习输入框"),
                                "practice source was not accessible",
                            )
                            wait_for(
                                lambda: focused(source) and status().get("sourceSupported"),
                                "real practice source did not focus and connect",
                            )
                            subprocess.run(
                                ["fcitx5-remote", "-s", "keyboard-us"],
                                env=gtk_environment,
                                check=True,
                                timeout=3,
                            )
                            typed("Please review this note.")
                            wait_for(
                                lambda: text(source) == "Please review this note.",
                                "practice did not receive actual keyboard input",
                            )
                            wait_for(
                                ready_to_accept,
                                "practice did not receive a production model candidate",
                            )
                            before_write = trace_checkpoint()
                            key("Tab")
                            wait_for(
                                lambda: re.search(r"[\u3400-\u9fff]", text(source)),
                                "practice Tab did not write the actual model translation",
                            )
                            wait_for(
                                lambda: verified_write(before_write),
                                "practice acceptance did not verify",
                            )
                            assert focused(source)
                            key("alt+r")
                            wait_for(
                                lambda: text(source) == "" and focused(source),
                                "explicit practice reset did not clear and focus its own field",
                            )
                            editing_source = "Please send me the final version."
                            typed(editing_source)
                            wait_for(
                                lambda: text(source) == editing_source and ready_to_accept(),
                                "second practice source did not become ready",
                            )
                            candidate = wait_for(
                                lambda: named(practice.pid, "TypeLay 译文候选"),
                                "production candidate lacked its accessible name",
                            )
                            subprocess.run(
                                ["grim", str(arguments.output / "practice-preclick.png")],
                                check=True,
                                timeout=3,
                            )
                            current_window = next(
                                (
                                    item
                                    for item in contents(request("Windows"), "Windows")
                                    if item.get("pid") == practice.pid
                                ),
                                {},
                            )
                            assert current_window.get("is_focused") is True
                            wait_for(
                                lambda: focused(source) and ready_to_accept(),
                                "practice candidate was not ready after collecting evidence",
                            )
                            area, frame = bounds(
                                candidate, practice.pid, current_window["layout"]["window_size"]
                            )
                            x, y, width, height = area
                            point = [x + width // 2, y + height // 2]
                            (arguments.output / "practice-preclick.json").write_text(
                                json.dumps(
                                    {
                                        "candidateInFrame": area,
                                        "frameReportedBounds": frame,
                                        "clickWindowCoordinates": point,
                                    },
                                    indent=2,
                                )
                            )
                            subprocess.run(
                                [
                                    "xdotool",
                                    "mousemove",
                                    "--window",
                                    nested[0],
                                    str(point[0]),
                                    str(point[1]),
                                    "click",
                                    "1",
                                ],
                                check=True,
                                timeout=3,
                            )
                            wait_for(
                                lambda: focused(candidate) and view("EditingTranslation"),
                                "actual practice candidate click did not enter editing",
                            )
                            draft = "Practice edited candidate."
                            key("ctrl+a")
                            typed(draft)
                            wait_for(
                                lambda: text(candidate) == draft,
                                "practice draft did not receive real keyboard input",
                            )
                            assert text(source) == editing_source
                            before_return = trace_checkpoint()
                            key("Tab")
                            wait_for(
                                lambda: focused(source)
                                and ready_to_accept("EditedTranslationPending"),
                                "practice first Tab did not return without writing",
                            )
                            assert text(source) == editing_source and not new_write_outcomes(
                                before_return
                            )
                            before_write = trace_checkpoint()
                            key("Tab")
                            wait_for(
                                lambda: text(source) == draft and verified_write(before_write),
                                "practice second Tab did not verify the exact draft",
                            )
                            subprocess.run(
                                ["grim", str(arguments.output / "practice.png")],
                                check=True,
                                timeout=3,
                            )
                            key("alt+d")
                            assert practice.wait(timeout=5) == 0
                            assert status()["setup"]["onboardingComplete"]
                            assert not status()["setup"]["tutorialVisible"]
                            evidence.append(
                                "Shipped GTK practice window: production CPU model and one Tab, actual candidate editing and two-stage Tab, explicit completion; accessibility readback without a tutorial controller"
                            )
                finally:
                    for child in (practice, fixture, daemon):
                        if child is not None and child.poll() is None:
                            child.terminate()
                            try:
                                child.wait(timeout=5)
                            except subprocess.TimeoutExpired:
                                child.kill()
                                child.wait()
        if arguments.desktop_scope == "qt":
            from qt_desktop import run_qt

            evidence.extend(
                run_qt(
                    arguments,
                    environment,
                    nested_window=nested[0],
                    wait_for=wait_for,
                    status=status,
                    ready_to_accept=ready_to_accept,
                    key=key,
                    typed=typed,
                    send_tab=send_tab,
                    trace_checkpoint=trace_checkpoint,
                    new_write_outcomes=new_write_outcomes,
                    verified_write=verified_write,
                )
            )
        if arguments.desktop_scope == "setup":
            assert not status().get("setup", {}).get("tutorialVisible")
            support = wait_for(
                lambda: status().get("setup", {}).get("applicationSupport"),
                "first-run settings did not receive the shared application-support presentation",
            )
            assert support["title"] == "应用支持"
            assert any(row["id"] == "desktop-text" for row in support["rows"])
            assert not status()["setup"]["onboardingComplete"]
            from support.gtk_accessibility import Atspi, named, text

            def settings_control(name):
                return wait_for(
                    lambda: named(process.pid, name), f"settings control missing: {name}"
                )

            def activate_setting(name):
                node = settings_control(name)
                action = node.get_action_iface()
                assert action is not None and Atspi.Action.do_action(action, 0), name
                return node

            # Drive actual GTK actions; the model being ready never completes an
            # application connection or a canceled component selection.
            desktop_support = next(row for row in support["rows"] if row["id"] == "desktop-text")
            primary = settings_control(desktop_support["primaryAction"]["label"])
            # A GTK action can activate a clipped button. Check its actual
            # allocation before any focus/scroll/action changes the first screen.
            subprocess.run(
                ["grim", str(arguments.output / "application-support.png")], check=True, timeout=3
            )
            assert primary.get_state_set().contains(Atspi.StateType.SHOWING)
            area = Atspi.Component.get_extents(
                primary.get_component_iface(), Atspi.CoordType.SCREEN
            )
            assert area.width > 0 and area.height > 0
            viewport_bounds = []
            ancestor = primary.get_parent()
            for _ in range(16):
                assert ancestor is not None and ancestor.get_process_id() == process.pid
                ancestor.clear_cache()
                role = ancestor.get_role()
                if role in (Atspi.Role.SCROLL_PANE, Atspi.Role.VIEWPORT, Atspi.Role.FRAME):
                    clip = Atspi.Component.get_extents(
                        ancestor.get_component_iface(), Atspi.CoordType.SCREEN
                    )
                    if role == Atspi.Role.FRAME:
                        frame = clip
                        assert ancestor.get_name() == "TypeLay 设置"
                        break
                    viewport_bounds.append(clip)
                ancestor = ancestor.get_parent()
            else:
                raise AssertionError("settings action exceeds its bounded GTK ancestry")
            assert viewport_bounds, "application support did not expose its scrolling viewport"
            (arguments.output / "application-support-layout.json").write_text(
                json.dumps(
                    {
                        "primaryAction": desktop_support["primaryAction"]["label"],
                        "primaryInFrame": [
                            area.x - frame.x,
                            area.y - frame.y,
                            area.width,
                            area.height,
                        ],
                        "scrollViewportsInFrame": [
                            [clip.x - frame.x, clip.y - frame.y, clip.width, clip.height]
                            for clip in viewport_bounds
                        ],
                        "frameReportedBounds": [frame.x, frame.y, frame.width, frame.height],
                    },
                    indent=2,
                    ensure_ascii=False,
                )
            )
            # All rectangles come from the same GTK SCREEN coordinate space.
            # Its Wayland origin need not be the compositor's global origin.
            for clip in [*viewport_bounds, frame]:
                assert clip.x <= area.x and clip.y <= area.y, (
                    "primary action starts outside its viewport or frame"
                )
                assert (
                    area.x + area.width <= clip.x + clip.width
                    and area.y + area.height <= clip.y + clip.height
                ), "primary action is clipped on the default application-support screen"
            evidence.append(
                "Selected application primary action fully visible in the default GTK viewport"
            )
            activate_setting(desktop_support["primaryAction"]["label"])
            bundled_component = (package / "support/desktop/component.json").is_file()
            if bundled_component:
                assert desktop_support["primaryAction"]["id"] == "Install"
                settings_control(support["labels"]["confirm"])
            else:
                settings_control(support["labels"]["chooseComponent"])
            activate_setting(support["labels"]["cancel"])
            assert not status()["setup"]["onboardingComplete"]
            if bundled_component:
                row = next(
                    row
                    for row in status()["setup"]["applicationSupport"]["rows"]
                    if row["id"] == "desktop-text"
                )
                assert row["state"] == "NeedsInstall"
                activate_setting(row["primaryAction"]["label"])
                activate_setting(support["labels"]["confirm"])

                def prepared_component():
                    row = next(
                        row
                        for row in status()["setup"]["applicationSupport"]["rows"]
                        if row["id"] == "desktop-text"
                    )
                    return row if row["state"] == "AwaitingApproval" else None

                # This includes integrity checking and unpacking the real shipped
                # archive, not a GUI animation or a readiness sleep.
                row = wait_for(prepared_component, "same-package preparation failed", timeout=60)
                assert not any(row["capabilities"].values()), row
                assert not status()["setup"]["onboardingComplete"]
                evidence.append(
                    "Desktop component preparation: cancel leaves files unprepared; explicit "
                    "approval safely unpacks the shipped archive without claiming a connection"
                )

            # Use the real GTK selection/action interfaces and paste the system
            # clipboard into the existing practice field after closing each dialog.
            # This does not load an extension or complete browser approval.
            choices = [row for row in support["rows"] if not row["showInMore"]]
            chromium = next(row for row in choices if row["id"] == "chromium")
            selector = settings_control(desktop_support["title"])
            assert selector.get_role() == Atspi.Role.COMBO_BOX
            assert Atspi.Selection.select_child(
                selector.get_selection_iface(), choices.index(chromium)
            )
            activate_setting(support["labels"]["details"])
            instructions = next(
                action
                for action in [chromium["primaryAction"], *chromium["secondaryActions"]]
                if action and action["id"] == "OpenInstructions"
            )
            for index, expected in enumerate(
                ("chrome://extensions/", str(package.resolve() / "browser/chromium"))
            ):
                activate_setting(instructions["label"])
                copy_name = support["labels"]["copy"] + "：" + expected
                copy_button = settings_control(copy_name)
                assert copy_button.get_state_set().contains(Atspi.StateType.SHOWING)
                if index == 0:
                    subprocess.run(
                        ["grim", str(arguments.output / "application-support-instructions.png")],
                        check=True,
                        timeout=3,
                    )
                assert Atspi.Component.grab_focus(copy_button.get_component_iface())
                key("space")
                settings_control("已复制。")
                activate_setting(support["labels"]["cancel"])
                wait_for(
                    lambda: named(process.pid, copy_name) is None,
                    "closed instructions retained their copy control",
                )
                activate_setting(support["labels"]["tryIt"])
                practice_window = wait_for(
                    lambda: (
                        window
                        if (
                            window := contents(request("FocusedWindow"), "FocusedWindow") or {}
                        ).get("title")
                        == "TypeLay 首次使用"
                        else None
                    ),
                    "clipboard practice field did not gain compositor focus",
                )
                source = settings_control("教程练习原文")
                assert text(source) == ""
                assert Atspi.Component.grab_focus(source.get_component_iface())
                key("ctrl+v")
                wait_for(
                    lambda: text(source) == expected,
                    "copy button did not place the exact address or folder on the clipboard",
                )
                request({"Action": {"CloseWindow": {"id": practice_window["id"]}}})
                wait_for(
                    lambda: not status()["setup"]["tutorialVisible"],
                    "closing clipboard practice did not dismiss the window",
                )
                assert not status()["setup"]["onboardingComplete"]
            assert Atspi.Selection.select_child(selector.get_selection_iface(), 0)
            evidence.append(
                "Real GTK browser instructions: both copy buttons paste the exact management "
                "address and component folder after dialog dismissal; reopening works and "
                "closing practice does not complete onboarding"
            )
            trial = settings_control(support["labels"]["tryIt"])
            assert Atspi.Component.grab_focus(trial.get_component_iface())
            key("ctrl+Page_Up")
            enabled = settings_control("设置：启用 TypeLay")
            wait_for(
                lambda: enabled.get_state_set().contains(Atspi.StateType.SHOWING),
                "general settings page did not become visible",
            )
            activate_setting("设置：启用 TypeLay")
            wait_for(
                lambda: not json.loads(paths.settings.read_text())["Enabled"],
                "switch did not save immediately",
            )
            saved = json.loads(paths.settings.read_text())
            settings_control("保存修改")
            wake = settings_control("设置：唤醒翻译")
            direct = settings_control("设置：直接翻译替换")
            assert Atspi.EditableText.set_text_contents(
                wake.get_editable_text_iface(), "Ctrl+Shift+F8"
            )
            wait_for(
                lambda: status()["setup"]["settingsDraft"],
                "shortcut edit was not retained as draft",
            )
            assert "离开此页后仍会保留" in text(settings_control("设置草稿"))
            activate_setting("设置：启用 TypeLay")
            wait_for(
                lambda: json.loads(paths.settings.read_text())["Enabled"],
                "second switch change was not saved",
            )
            assert text(wake) == "Ctrl+Shift+F8", (
                "settings notification overwrote the pending shortcut"
            )
            assert Atspi.EditableText.set_text_contents(
                direct.get_editable_text_iface(), "Ctrl+Shift+F8"
            )
            activate_setting("保存修改")
            wait_for(
                lambda: text(settings_control("设置反馈")) == "未保存：请检查输入。草稿仍保留。",
                "invalid shortcut pair did not report rejection",
            )
            assert json.loads(paths.settings.read_text())["WakeShortcut"] == saved["WakeShortcut"]
            assert status()["setup"]["settingsDraft"], "invalid shortcut pair discarded the draft"
            assert Atspi.EditableText.set_text_contents(
                direct.get_editable_text_iface(), saved["DirectShortcut"]
            )
            activate_setting("保存修改")
            wait_for(
                lambda: json.loads(paths.settings.read_text())["WakeShortcut"] == "Ctrl+Shift+F8",
                "valid shortcut pair did not persist",
            )
            assert Atspi.EditableText.set_text_contents(
                wake.get_editable_text_iface(), saved["WakeShortcut"]
            )
            activate_setting("保存修改")
            wait_for(
                lambda: not status()["setup"]["settingsDraft"],
                "saved shortcuts still appeared dirty",
            )
            assert text(settings_control("设置反馈")) == "设置已保存"
            assert Atspi.EditableText.set_text_contents(
                wake.get_editable_text_iface(), "Ctrl+Shift+F8"
            )
            assert Atspi.Component.grab_focus(wake.get_component_iface())
            key("ctrl+Page_Down")
            wait_for(
                lambda: trial.get_state_set().contains(Atspi.StateType.SHOWING),
                "application-support page did not become visible with a pending draft",
            )
            assert status()["setup"]["settingsDraft"]
            key("ctrl+Page_Up")
            assert text(wake) == "Ctrl+Shift+F8", "page navigation discarded the shortcut draft"
            window = wait_for(
                lambda: next(
                    (
                        window
                        for window in (contents(request("Windows"), "Windows") or [])
                        if window.get("title") == "TypeLay 设置"
                    ),
                    None,
                ),
                "settings window missing before close",
            )
            request({"Action": {"CloseWindow": {"id": window["id"]}}})
            keep_editing = settings_control("继续编辑")
            wait_for(
                lambda: keep_editing.get_state_set().contains(Atspi.StateType.FOCUSED),
                "closing a draft did not default to keeping the user's work",
            )
            key("Return")
            wait_for(
                lambda: named(process.pid, "放弃修改") is None,
                "default keep-editing action did not dismiss the close prompt",
            )
            assert status()["setup"]["settingsDraft"]
            assert text(wake) == "Ctrl+Shift+F8"
            request({"Action": {"CloseWindow": {"id": window["id"]}}})
            activate_setting("放弃修改")
            wait_for(
                lambda: not status()["setup"]["settingsDraft"],
                "explicit discard retained the unsaved draft",
            )
            assert json.loads(paths.settings.read_text())["WakeShortcut"] == saved["WakeShortcut"]
            assert control(paths, "settings") is True
            wake = settings_control("设置：唤醒翻译")
            assert text(wake) == saved["WakeShortcut"]
            assert Atspi.Component.grab_focus(wake.get_component_iface())
            key("ctrl+Page_Down")
            activate_setting(support["labels"]["tryIt"])
            wait_for(
                lambda: status().get("setup", {}).get("tutorialVisible"),
                "explicit local-model trial did not open",
            )
            wait_for(
                lambda: (contents(request("FocusedWindow"), "FocusedWindow") or {}).get("title")
                == "TypeLay 首次使用",
                "tutorial did not gain real compositor focus before typing",
            )
            assert not status()["setup"]["onboardingComplete"]
            key("ctrl+a")
            typed("Please send me the final version.")
            key("alt+g")
            wait_for(
                lambda: status().get("setup", {}).get("tutorialState") == "preview",
                "explicit tutorial preview did not use the actual local model",
                125,
            )
            subprocess.run(
                ["grim", str(arguments.output / "tutorial-preview.png")], check=True, timeout=3
            )
            assert not status()["setup"]["onboardingComplete"]
            key("alt+d")
            wait_for(
                lambda: status().get("setup", {}).get("onboardingComplete")
                and not status()["setup"]["tutorialVisible"],
                "explicit tutorial completion was not saved",
            )
            settings = json.loads(paths.settings.read_text())
            assert settings["OnboardingComplete"] is True
            stop()
            start()
            assert not status().get("setup", {}).get("tutorialVisible")
            assert control(paths, "tutorial") is True
            wait_for(
                lambda: status().get("setup", {}).get("tutorialVisible"),
                "completed tutorial could not be reopened explicitly",
            )
            wait_for(
                lambda: (contents(request("FocusedWindow"), "FocusedWindow") or {}).get("title")
                == "TypeLay 首次使用",
                "reopened tutorial did not gain real compositor focus",
            )
            key("alt+s")
            wait_for(
                lambda: not status().get("setup", {}).get("tutorialVisible"),
                "explicit tutorial skip did not close the practice window",
            )
            evidence.append(
                "First-run application support stays separate from model readiness; explicit local-model trial, completion, persisted restart and optional reopening without intercepting target-app Tab"
            )
            if bundled_component:
                from support.gtk_accessibility import focused

                def desktop_row():
                    view = (status().get("setup") or {}).get("applicationSupport") or {}
                    return next(
                        (row for row in view.get("rows", []) if row["id"] == "desktop-text"),
                        None,
                    )

                def desktop_action(identity):
                    row = desktop_row()
                    action = next(action for action in row["actions"] if action["id"] == identity)
                    if row["primaryAction"]["id"] != identity:
                        details = settings_control(support["labels"]["details"])
                        if not details.get_state_set().contains(Atspi.StateType.EXPANDED):
                            activate_setting(support["labels"]["details"])
                    activate_setting(action["label"])
                    activate_setting(support["labels"]["confirm"])

                # Only this isolated HOME is touched. The private daemon must not
                # read/write even these synthetic "existing user" profile files.
                user_profile = Path(os.environ["XDG_CONFIG_HOME"]) / "fcitx5/profile"
                user_profile.parent.mkdir(parents=True, exist_ok=True)
                user_profile.write_text("# Existing user profile must remain unchanged\n")
                before_profile = user_profile.read_bytes()
                assert control(paths, "settings") is True
                # A restarted settings window initially opens its general page.
                wake = settings_control("设置：唤醒翻译")
                assert Atspi.Component.grab_focus(wake.get_component_iface())
                key("ctrl+Page_Down")
                wait_for(
                    lambda: (row := desktop_row())
                    and any(a["id"] == "Connect" for a in row["actions"]),
                    "prepared component did not offer the explicit practice connection",
                )
                managed_window = None

                def close_managed_window():
                    if managed_window is not None and any(
                        window["id"] == managed_window["id"]
                        and window.get("pid") == managed_window["pid"]
                        for window in contents(request("Windows"), "Windows")
                    ):
                        request({"Action": {"CloseWindow": {"id": managed_window["id"]}}})

                def cleanup_managed_session():
                    import asyncio

                    from desktop_session import DesktopSession

                    try:
                        close_managed_window()
                        wait_for(
                            lambda: not any(
                                managed_window is not None and window["id"] == managed_window["id"]
                                for window in contents(request("Windows"), "Windows")
                            ),
                            "owned practice did not close during cleanup",
                            timeout=5,
                        )
                        # The production method only signals its verified owned
                        # daemon and refuses while any application still uses it.
                        asyncio.run(DesktopSession(paths).stop_session())
                    except (
                        OSError,
                        EOFError,
                        ValueError,
                        AssertionError,
                        subprocess.SubprocessError,
                    ):
                        print("Managed session cleanup could not confirm completion", flush=True)

                resources.callback(cleanup_managed_session)
                desktop_action("Connect")
                managed_window = wait_for(
                    lambda: next(
                        (
                            window
                            for window in contents(request("Windows"), "Windows")
                            if window.get("title") == "TypeLay 实操练习"
                        ),
                        None,
                    ),
                    "approved connection did not open the shipped practice window",
                    timeout=60,
                )

                try:
                    source = wait_for(
                        lambda: named(managed_window["pid"], "练习输入框"),
                        "managed practice field missing",
                    )
                    wait_for(
                        lambda: focused(source) and status().get("sourceSupported"),
                        "managed practice did not connect through the production adapter",
                    )
                    typed(original)
                    wait_for(lambda: text(source) == original, "practice lost keyboard input")
                    wait_for(ready_to_accept, "managed practice lacked a model candidate")
                    checkpoint = trace_checkpoint()
                    key("Tab")
                    wait_for(lambda: translated(text(source)), "managed practice Tab did not write")
                    wait_for(lambda: verified_write(checkpoint), "managed write was not verified")
                    stop()
                    assert any(
                        window["id"] == managed_window["id"]
                        and window.get("pid") == managed_window["pid"]
                        for window in contents(request("Windows"), "Windows")
                    ), "closing TypeLay closed the user's practice application"
                    start()
                    assert control(paths, "settings") is True
                    wake = settings_control("设置：唤醒翻译")
                    assert Atspi.Component.grab_focus(wake.get_component_iface())
                    key("ctrl+Page_Down")
                    wait_for(
                        lambda: (row := desktop_row())
                        and row["details"]
                        and not any(a["id"] == "Remove" for a in row["actions"]),
                        "an application still using the components was offered removal",
                    )
                finally:
                    # A cleanup error must not replace the original test failure.
                    if sys.exc_info()[0] is None:
                        close_managed_window()
                    else:
                        try:
                            close_managed_window()
                        except (OSError, EOFError, ValueError):
                            print("Managed practice close was not confirmed", flush=True)
                wait_for(
                    lambda: (row := desktop_row())
                    and any(a["id"] == "Remove" for a in row["actions"]),
                    "normally closed practice did not release its component lease",
                )
                desktop_action("Remove")
                wait_for(
                    lambda: (row := desktop_row()) and row["state"] == "NeedsInstall",
                    "explicit removal did not finish the private session and remove components",
                    timeout=30,
                )
                assert user_profile.read_bytes() == before_profile
                assert paths.settings.is_file() and status().get("modelReady")
                assert not (paths.data / "components/desktop-text/current").exists()
                assert (paths.data / "components/desktop-text/practice-profile").is_dir()
                evidence.append(
                    "Managed desktop connection: explicit approval opens the shipped practice, "
                    "real model and one Tab verify; normal client quit preserves the application; normal "
                    "application close permits explicit private-session stop and component removal, "
                    "with original user profile, models, settings and practice configuration retained"
                )
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
                        "uncooperative GTK inputs, tagged or embedded-object GTK buffers, uncooperative or nonstandard Qt fields, nonstandard GTK widgets and generic Electron"
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
        resources.close()
        for child in (process,):
            if child is not None and child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()
