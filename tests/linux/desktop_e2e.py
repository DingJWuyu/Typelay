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


def prepare_key(stack, name):
    """Preconnect to XTest; each call delivers one real down or up event."""
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
    keycode = x11.XKeysymToKeycode(display, x11.XStringToKeysym(name.encode("ascii")))
    assert keycode, f"XTest DISPLAY has no {name} keycode"

    def send_key(down, sync=True):
        sent = xtst.XTestFakeKeyEvent(display, keycode, int(down), 0)
        # Flush and wait only for X server processing, not Wayland delivery.
        if sync:
            x11.XSync(display, 0)
        assert sent, f"XTest refused {name} key event"

    return send_key


def prepare_tab(stack):
    send_key = prepare_key(stack, "Tab")

    def send_tab():
        send_key(True, sync=False)
        send_key(False)

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

    session_bindings = getattr(arguments, "gtk_session_bindings", False)
    accept_key = "Tab"
    gtk_acceptance_phases = []
    last_acceptance_observation = None

    def status():
        try:
            return control(paths, "status") or {}
        except (OSError, EOFError, ValueError):
            return {}

    def view(state):
        data = status()
        return data if data.get("viewState") == state and data.get("overlayVisible") else None

    def ready_to_accept(state="ValidTranslation"):
        nonlocal last_acceptance_observation
        started = now_ms()
        data = view(state)
        finished = now_ms()
        if data and data.get("acceptanceReady"):
            last_acceptance_observation = {
                "statusStartedAtMs": started,
                "statusReceivedAtMs": finished,
                "acceptanceRemainingMs": data.get("acceptanceRemainingMs"),
                "nativePublications": data.get("input", {}).get("nativePublications", []),
            }
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
            prepare_tab(resources)
            if arguments.desktop_scope in ("all", "browser", "qt", "gtk4")
            else None
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

                        def key_trace_checkpoint():
                            sys.stderr.flush()
                            return (arguments.output / "inside.log").stat().st_size

                        def key_trace_since(offset, kind, phase):
                            with (arguments.output / "inside.log").open("rb") as stream:
                                stream.seek(offset)
                                lines = stream.read().decode("utf-8", errors="replace")
                            matches = re.findall(
                                rf"TypeLay GTK{4 if gtk4 else 3} session-key kind={kind} "
                                rf"phase={phase} consumed=([01]) atMonoMs=(\d+)",
                                lines,
                            )
                            if not matches:
                                return None
                            consumed, observed = matches[-1]
                            return {"consumed": consumed == "1", "atMonoMs": int(observed)}

                        def observed_key(offset, kind, phase, consumed):
                            receipt = wait_for(
                                lambda: key_trace_since(offset, kind, phase),
                                f"native {kind} {phase} dispatch was not observed",
                            )
                            assert receipt["consumed"] is consumed, receipt
                            wait_for(
                                lambda: gtk_state().get("observedAtMs", 0) >= receipt["atMonoMs"],
                                "fixture did not observe state after the native key decision",
                            )

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
                        key_started = now_ms()
                        if gtk4:
                            # One preconnected native injection, with no process
                            # startup after readiness. This does not alter TTL.
                            send_tab()
                        else:
                            key(accept_key)
                        key_finished = now_ms()
                        gtk_acceptance_phases.append(
                            {
                                "stage": "default-first-accept",
                                "gtk": 4 if gtk4 else 3,
                                "readiness": last_acceptance_observation,
                                "keySendStartedAtMs": key_started,
                                "keySendFinishedAtMs": key_finished,
                            }
                        )
                        wait_for(
                            lambda: translated(gtk_state().get("first")),
                            f"GTK {accept_key} did not apply the real translation",
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
                        key(accept_key)
                        wait_for(
                            lambda: translated(gtk_state().get("first")),
                            f"GTK {accept_key} did not replace the actually typed source",
                        )
                        wait_for(
                            lambda: verified_write(before_write),
                            "actually typed GTK source did not receive a verified write receipt",
                        )
                        assert gtk_state()["focus"] == "fixture-first"
                        evidence.append(
                            "Wayland GTK keyboard-us: actual typed source, CPU translation, one configured acceptance key, verified write and retained focus"
                        )

                        def edit_and_accept(accept_key, hold_return=False):
                            editing_source = "Please review this note."
                            enter_source(editing_source)
                            actual = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("fullscreen")
                                and value.get("candidate")
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
                            baseline = (
                                gtk_state()["functionKeys"][0].copy() if hold_return else None
                            )
                            try:
                                if hold_return:
                                    subprocess.run(
                                        ["xdotool", "keydown", accept_key], check=True, timeout=2
                                    )
                                else:
                                    key(accept_key)
                                wait_for(
                                    lambda: gtk_state().get("focus") == "fixture-first"
                                    and gtk_state().get("imeClear")
                                    and ready_to_accept("EditedTranslationPending"),
                                    f"first editor {accept_key} did not return with the draft awaiting acceptance",
                                )
                                assert gtk_state()["first"] == editing_source
                                assert not new_write_outcomes(before_return), (
                                    f"first editor {accept_key} performed a write"
                                )
                                if hold_return:
                                    # Observe the real held-key decision passively. A new
                                    # application key would correctly invalidate this draft.
                                    repeated = key_trace_checkpoint()
                                    subprocess.run(
                                        ["xdotool", "keydown", accept_key], check=True, timeout=2
                                    )
                                    observed_key(repeated, "function", "repeat", True)
                                    assert gtk_state()["first"] == editing_source
                                    assert not new_write_outcomes(before_return)
                            finally:
                                if hold_return:
                                    released = key_trace_checkpoint()
                                    subprocess.run(
                                        ["xdotool", "keyup", accept_key], check=True, timeout=2
                                    )
                            if hold_return:
                                observed_key(released, "function", "release", True)
                                assert gtk_state()["functionKeys"][0] == baseline, (
                                    "editor return leaked its held repeat or release to the source"
                                )
                                assert gtk_state()["first"] == editing_source
                                assert not new_write_outcomes(before_return)
                                wait_for(
                                    lambda: ready_to_accept("EditedTranslationPending"),
                                    "held key release did not retain the recaptured manual candidate",
                                )
                            before_write = trace_checkpoint()
                            key(accept_key)
                            wait_for(
                                lambda: gtk_state().get("first") == draft,
                                f"second {accept_key} did not accept the exact manual draft",
                            )
                            wait_for(
                                lambda: verified_write(before_write),
                                "manual draft acceptance did not receive a verified write receipt",
                            )
                            assert gtk_state()["focus"] == "fixture-first"
                            evidence.append(
                                "Wayland GTK editor: actual candidate click and draft typing, first configured key returns without writing, second independent key verifies the exact draft"
                            )

                        edit_and_accept("Tab")

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
                            "Wayland GTK Entry with opt-in Fcitx cooperation: CPU model, native overlay, one configured acceptance key and preserved real pinyin composition"
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
                        key(accept_key)
                        multiline_after = wait_for(
                            lambda: value
                            if (value := gtk_state()["multiline"]["text"]) != multiline_source
                            else None,
                            f"GTK multiline {accept_key} did not replace the selected range",
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
                            "GTK plain TextView: selected multiline CPU translation, UTF-16 emoji surroundings, one configured acceptance key and real pinyin protection"
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
                        if session_bindings:
                            # Keep every original default-key stage above, including
                            # the TextView/IME checks, before the narrower F-key stage.
                            click_entry("first")
                            switch_im("keyboard-us")
                            enter_source(original)
                            before_default_exit = trace_checkpoint()
                            key("Escape")
                            wait_for(
                                lambda: not status().get("overlayVisible")
                                and gtk_state().get("candidate") is None,
                                "default Escape did not exit the actual GTK session",
                            )
                            assert gtk_state()["first"] == original
                            assert not new_write_outcomes(before_default_exit)
                            stop()
                            settings.update({"AcceptShortcut": "F1", "ExitShortcut": "F24"})
                            start()
                            request({"Action": {"FocusWindow": {"id": window["id"]}}})
                            click_entry("first")
                            switch_im("keyboard-us")
                            enter_source(original)
                            # The old default must remain the application's Tab,
                            # even while a valid F1 acceptance permit exists.
                            before_navigation = trace_checkpoint()
                            before_tab = gtk_state()
                            tab_trace = key_trace_checkpoint()
                            try:
                                key("Tab")
                                wait_for(
                                    lambda: gtk_state().get("focus") == "fixture-second",
                                    "unbound Tab did not navigate to the application's second field",
                                )
                                observed_key(tab_trace, "tab", "press", False)
                                observed_key(tab_trace, "tab", "release", False)
                                if gtk4:
                                    after_tab = gtk_state()
                                    assert after_tab["tabKeys"] == {
                                        "down": before_tab["tabKeys"]["down"] + 1,
                                        "up": before_tab["tabKeys"]["up"] + 1,
                                    }
                                    transitions = [
                                        item
                                        for item in after_tab["focusTransitions"]
                                        if item["sequence"] > before_tab["focusSequence"]
                                    ]
                                    assert len(transitions) == (
                                        after_tab["focusSequence"] - before_tab["focusSequence"]
                                    ), "fixture focus history overflowed during one Tab press"
                                    assert not any(
                                        item["scope"] == "overlay" for item in transitions
                                    ), (
                                        "read-only GTK preview entered the application's Tab focus chain"
                                    )
                                assert gtk_state()["first"] == original
                                assert not new_write_outcomes(before_navigation)
                            finally:
                                try:
                                    (arguments.output / "session-tab-navigation.json").write_text(
                                        json.dumps(
                                            {"before": before_tab, "after": gtk_state()}, indent=2
                                        )
                                    )
                                except Exception:
                                    pass  # Preserve the original failure if the fixture exited.
                            key("shift+Tab")
                            wait_for(
                                lambda: gtk_state().get("focus") == "fixture-first"
                                and ready_to_accept(),
                                "source did not recover after ordinary Tab navigation",
                            )
                            before_modified = gtk_state()["functionKeys"][0].copy()
                            key("ctrl+F1")
                            wait_for(
                                lambda: gtk_state()["functionKeys"][0]["up"]
                                > before_modified["up"],
                                "modified F1 was intercepted by the session binding",
                            )
                            assert gtk_state()["first"] == original
                            assert not new_write_outcomes(before_navigation)
                            wait_for(ready_to_accept, "source was not recaptured after modified F1")
                            before_write = trace_checkpoint()
                            key("F1")
                            wait_for(
                                lambda: translated(gtk_state().get("first"))
                                and verified_write(before_write),
                                "configured F1 did not verify its single source write",
                            )
                            edit_and_accept("F1", hold_return=True)
                            # The same fixture stays alive across the real client's
                            # settings restart. Native ownership must survive both
                            # overlay destruction and the newly applied generation.
                            enter_source("Please keep this source until a new key press.")
                            before_exit = gtk_state()["functionKeys"][23].copy()
                            try:
                                subprocess.run(["xdotool", "keydown", "F24"], check=True, timeout=2)
                                wait_for(
                                    lambda: not status().get("overlayVisible")
                                    and gtk_state().get("candidate") is None,
                                    "configured F24 did not exit the actual GTK session",
                                )
                                repeated = key_trace_checkpoint()
                                subprocess.run(["xdotool", "keydown", "F24"], check=True, timeout=2)
                                observed_key(repeated, "function", "repeat", True)
                            finally:
                                released = key_trace_checkpoint()
                                subprocess.run(["xdotool", "keyup", "F24"], check=True, timeout=2)
                            observed_key(released, "function", "release", True)
                            assert gtk_state()["functionKeys"][23] == before_exit, (
                                "hidden GTK overlay lost the consumed exit-key pair"
                            )
                            # A refused F7 down predates its assignment as accept.
                            # New publication must not upgrade that held press.
                            source_before = gtk_state()["first"]
                            before_f7 = gtk_state()["functionKeys"][6]["down"]
                            subprocess.run(["xdotool", "keydown", "F7"], check=True, timeout=2)
                            try:
                                wait_for(
                                    lambda: gtk_state()["functionKeys"][6]["down"] > before_f7,
                                    "unbound F7 did not reach the actual source",
                                )
                                key("Tab")
                                wait_for(
                                    lambda: gtk_state().get("focus") == "fixture-second",
                                    "held unbound F7 blocked normal same-window navigation",
                                )
                                key("shift+Tab")
                                wait_for(
                                    lambda: gtk_state().get("focus") == "fixture-first",
                                    "source did not regain focus while F7 was held",
                                )
                                stop()
                                settings["AcceptShortcut"] = "F7"
                                start()
                                request({"Action": {"FocusWindow": {"id": window["id"]}}})
                                wait_for(
                                    ready_to_accept,
                                    "new F7 configuration did not obtain a fresh source permit",
                                )
                                before_repeat = trace_checkpoint()
                                before_f7 = gtk_state()["functionKeys"][6]["down"]
                                subprocess.run(["xdotool", "keydown", "F7"], check=True, timeout=2)
                                wait_for(
                                    lambda: gtk_state()["functionKeys"][6]["down"] > before_f7,
                                    "rebound held F7 repeat was stolen from the source",
                                )
                                assert gtk_state()["first"] == source_before
                                assert not new_write_outcomes(before_repeat)
                            finally:
                                subprocess.run(["xdotool", "keyup", "F7"], check=True, timeout=2)
                            wait_for(ready_to_accept, "released F7 did not allow fresh capture")
                            before_write = trace_checkpoint()
                            key("F7")
                            wait_for(
                                lambda: gtk_state()["first"] != source_before
                                and verified_write(before_write),
                                "independent F7 press did not accept after rebinding",
                            )
                            evidence.append(
                                "GTK F1/F24: original editor press only returns; repeat/release "
                                "stay consumed after focus return/hide; unbound held F7 stays "
                                "with the source across rebinding until a fresh physical down"
                            )

                            # The remaining original OnDemand/pause/direct stages
                            # retain their defaults in the next ordinary restart.
                            settings.update({"AcceptShortcut": "Tab", "ExitShortcut": "Esc"})

                        # Restart the same production client with persisted OnDemand
                        # settings. No fake translator or test-only command dispatch.
                        stop()
                        settings["Mode"] = 1
                        start()
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})

                        # Both GTK generations use the same real settings and
                        # source-widget accessibility actions. No resume protocol
                        # event or test-only command stands in for the button.
                        from support.gtk_accessibility import Atspi, named

                        def visible_control(pid, name):
                            node = named(pid, name)
                            if node is None:
                                return None
                            states = node.get_state_set()
                            return (
                                node
                                if states.contains(Atspi.StateType.SHOWING)
                                and states.contains(Atspi.StateType.ENABLED)
                                else None
                            )

                        resume_accessibility = {}

                        def resume_control():
                            if not gtk4:
                                return visible_control(fixture.pid, "恢复")
                            node = named(fixture.pid, "恢复")
                            resume_accessibility.clear()
                            resume_accessibility["found"] = node is not None
                            if node is None:
                                return None
                            states = node.get_state_set()
                            resume_accessibility.update(
                                ownerMatches=node.get_process_id() == fixture.pid,
                                nameMatches=node.get_name() == "恢复",
                                buttonRole=node.get_role() == Atspi.Role.PUSH_BUTTON,
                                showing=states.contains(Atspi.StateType.SHOWING),
                                sensitive=states.contains(Atspi.StateType.SENSITIVE),
                                enabled=states.contains(Atspi.StateType.ENABLED),
                            )
                            # GTK 4.22.5 collect_states exports SENSITIVE for an
                            # actionable widget, but does not export ENABLED:
                            # https://github.com/GNOME/gtk/blob/4.22.5/gtk/a11y/gtkatspicontext.c
                            return (
                                node
                                if all(
                                    resume_accessibility[key]
                                    for key in (
                                        "ownerMatches",
                                        "nameMatches",
                                        "buttonRole",
                                        "showing",
                                        "sensitive",
                                    )
                                )
                                else None
                            )

                        click_entry("first")
                        switch_im("keyboard-us")
                        key("ctrl+a")
                        key("BackSpace")
                        wait_for(
                            lambda: gtk_state().get("first") == "" and gtk_state().get("imeClear"),
                            "GTK pause source did not clear through real input",
                        )
                        pause_source = "Please review this paused message."
                        typed(pause_source)
                        wait_for(
                            lambda: gtk_state().get("first") == pause_source
                            and gtk_state().get("imeClear"),
                            "GTK pause source did not receive actual keyboard input",
                        )
                        before_wake = status()["input"]["captureCounts"].get("supported", 0)
                        wait_for(
                            lambda: not status().get("overlayVisible")
                            and status()["input"]["captureCounts"].get("supported", 0)
                            > before_wake,
                            "GTK pause source did not become dormant with a fresh shortcut lease",
                        )
                        key("ctrl+shift+F9")
                        wait_for(ready_to_accept, "GTK pause setup did not obtain a real candidate")
                        assert control(paths, "settings") is True
                        settings_window = wait_for(
                            lambda: next(
                                (
                                    item
                                    for item in contents(request("Windows"), "Windows")
                                    if item.get("pid") == process.pid
                                    and item.get("title") == "TypeLay 设置"
                                ),
                                None,
                            ),
                            "GTK pause settings window did not map",
                        )
                        request({"Action": {"FocusWindow": {"id": settings_window["id"]}}})
                        enabled = wait_for(
                            lambda: visible_control(process.pid, "设置：启用 TypeLay"),
                            "GTK pause setting was not accessible and visible",
                        )
                        assert json.loads(paths.settings.read_text())["Enabled"]
                        action = enabled.get_action_iface()
                        assert action and Atspi.Action.do_action(action, 0)
                        wait_for(
                            lambda: not json.loads(paths.settings.read_text())["Enabled"],
                            "real GTK settings switch did not pause TypeLay",
                        )
                        paused_settings = json.loads(paths.settings.read_text())
                        assert paused_settings["Mode"] == 1
                        request({"Action": {"CloseWindow": {"id": settings_window["id"]}}})
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        click_entry("first")
                        pause_wait = {}

                        def paused_visible():
                            current = status()
                            resume_visible = resume_control() is not None
                            pause_wait.update(
                                viewState=current.get("viewState"),
                                overlayVisible=current.get("overlayVisible") is True,
                                acceptanceReady=current.get("acceptanceReady") is True,
                                resumeVisible=resume_visible,
                                resumeAccessibility=dict(resume_accessibility),
                            )
                            return (
                                pause_wait["viewState"] == "Paused"
                                and pause_wait["overlayVisible"]
                                and resume_visible
                                and not pause_wait["acceptanceReady"]
                            )

                        try:
                            wait_for(
                                paused_visible,
                                "paused GTK source did not show its actual weakened resume button",
                            )
                        except BaseException:
                            try:
                                (arguments.output / "paused-before-cleanup.json").write_text(
                                    json.dumps(
                                        {"wait": dict(pause_wait), "status": status()}, indent=2
                                    )
                                )
                                subprocess.run(
                                    ["grim", str(arguments.output / "paused-before-cleanup.png")],
                                    check=False,
                                    timeout=3,
                                )
                            except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
                                pass  # Preserve the actual wait failure before resource teardown.
                            raise
                        before_pause_input = gtk_state()
                        assert before_pause_input["first"] == pause_source
                        assert before_pause_input["candidate"] is None
                        paused_captures = dict(status()["input"]["captureCounts"])
                        pause_trace = trace_checkpoint()
                        key("End")
                        key("exclam")
                        typed_while_paused = wait_for(
                            lambda: value
                            if (value := gtk_state()).get("first") == pause_source + "!"
                            and value["firstEntry"]["insertions"]
                            == before_pause_input["firstEntry"]["insertions"] + 1
                            and value.get("imeClear")
                            and view("Paused")
                            else None,
                            "ordinary GTK input during pause was lost, replayed or translated",
                        )
                        assert (
                            typed_while_paused["firstEntry"]["deletions"]
                            == before_pause_input["firstEntry"]["deletions"]
                        )
                        assert typed_while_paused["second"] == before_pause_input["second"]
                        assert typed_while_paused["candidate"] is None
                        assert not status().get("acceptanceReady")
                        assert status()["input"]["captureCounts"] == paused_captures
                        assert not new_write_outcomes(pause_trace)
                        with trace_path.open("rb") as trace:
                            trace.seek(pause_trace)
                            data = trace.read()
                        assert not any(
                            json.loads(line)["operation"] == "translate-start"
                            for line in data[: data.rfind(b"\n") + 1].splitlines()
                        ), "paused GTK source scheduled model work"
                        resume = wait_for(
                            resume_control,
                            "actual GTK resume button disappeared before activation",
                        )
                        action = resume.get_action_iface()
                        assert action and Atspi.Action.do_action(action, 0)
                        wait_for(
                            lambda: json.loads(paths.settings.read_text())["Enabled"]
                            and not status().get("overlayVisible")
                            and status()["input"]["captureCounts"].get("supported", 0)
                            > paused_captures.get("supported", 0),
                            "GTK resume did not leave OnDemand dormant with a fresh observation",
                        )
                        assert json.loads(paths.settings.read_text()) == {
                            **paused_settings,
                            "Enabled": True,
                        }, "GTK resume changed preferences beyond Enabled"
                        assert gtk_state()["first"] == pause_source + "!"
                        assert gtk_state()["focus"] == "fixture-first"
                        key("ctrl+shift+F9")
                        wait_for(
                            ready_to_accept,
                            "resumed OnDemand GTK source did not wake on the next real F9",
                        )
                        assert gtk_state()["first"] == pause_source + "!"
                        evidence.append(
                            "GTK pause: real settings switch and native resume button, one ordinary "
                            "character, no capture/model/write while paused; resume changes only Enabled "
                            "and OnDemand remains dormant until the next real F9"
                        )
                        click_entry("second")
                        wait_for(
                            lambda: not status().get("overlayVisible"),
                            "GTK pause scenario did not retire its explicit wake after leaving",
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

                        if gtk4 and session_bindings:
                            from gtk_session_reentry import run_case

                            with (
                                ExitStack() as reentry_resources,
                                tempfile.TemporaryDirectory(prefix="tl-gtk-reentry-") as directory,
                            ):
                                run_case(
                                    fixture.args,
                                    gtk_environment,
                                    arguments.output,
                                    prepare_key(reentry_resources, "F1"),
                                    Path(directory),
                                    "resume-editor",
                                    False,
                                    nested[0],
                                )
                            evidence.append(
                                "GTK4 editor recovery: stale generation/revision refused; "
                                "authorized resume restores actual editor focus before rendering, "
                                "without writing either source field"
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
                            before_feedback = (
                                status().get("operationFeedback", {}).get("revision", 0)
                            )
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
                            detached = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("focus") == "fixture-second"
                                and value.get("focusedImeClear")
                                else None,
                                "background GTK destination did not retain real focus with a Clear IME",
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
                            if changes_source:
                                assert detached["first"] == source + "!", (
                                    "the actual post-snapshot edit was not retained after leaving A"
                                )
                            # The model may finish after A loses focus but before this
                            # snapshot. The synchronous mutation contexts below must
                            # prove B owned focus and retained its actual selection.
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
                                "输入内容、选区或字段已变化，TypeLay 未替换文字。请确认目标输入框后再操作。"
                                if changes_source
                                else "已直接替换原输入框文字。"
                            )
                            wait_for(
                                lambda: (report := status()).get("serviceStatus") == terminal
                                and (
                                    not changes_source
                                    or (
                                        (feedback := report.get("operationFeedback", {})).get(
                                            "code"
                                        )
                                        == "write-rejected"
                                        and feedback.get("revision", 0) > before_feedback
                                    )
                                ),
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
                            "keeps focus, text and its observed caret/selection (nonempty selection "
                            "is not required); a post-snapshot source edit is retained "
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

                        if session_bindings:
                            from gtk_session_reentry import run_reentry_checks

                            with ExitStack() as reentry_resources:
                                run_reentry_checks(
                                    fixture.args,
                                    gtk_environment,
                                    arguments.output,
                                    prepare_key(reentry_resources, "F1"),
                                    gtk4=gtk4,
                                    nested=nested[0],
                                )
                            evidence.append(
                                "GTK mutation callbacks: delete/insert/final selection "
                                "generation changes preserve the issued Unknown receipt, "
                                "stop subsequent actions and quarantine without retry; "
                                "same-generation controls verify actual body and selection"
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
            from support.gtk_accessibility import Atspi, GLib, named, text

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
                assert control(paths, "tutorial") is True
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
            assert control(paths, "startOnDemandPractice", {"confirmedSwitch": True}) is False, (
                "practice silently enabled a paused client"
            )
            assert not json.loads(paths.settings.read_text())["Enabled"]
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
                lambda: text(settings_control("设置反馈"))
                == "唤醒与直接替换请使用 Ctrl / Alt（可加 Shift）与字母、数字或 F1-F24 的组合，暂不支持 F12；两个快捷键不能相同。",
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
            accept_key = settings_control("设置：接受译文")
            exit_key = settings_control("设置：退出当前翻译")
            assert Atspi.Selection.select_child(accept_key.get_selection_iface(), 7)
            assert Atspi.Selection.select_child(exit_key.get_selection_iface(), 7)
            wait_for(
                lambda: status()["setup"]["settingsDraft"],
                "session key choice saved without explicit draft submission",
            )
            assert json.loads(paths.settings.read_text())["AcceptShortcut"] == "Tab"
            activate_setting("保存修改")
            wait_for(
                lambda: text(settings_control("设置反馈"))
                == "请选择有效且不同的接受与退出快捷键。",
                "duplicate session key did not reject the complete draft",
            )
            assert status()["setup"]["settingsDraft"]
            assert json.loads(paths.settings.read_text())["ExitShortcut"] == "Esc"
            assert Atspi.Selection.select_child(exit_key.get_selection_iface(), 8)
            activate_setting("保存修改")
            wait_for(
                lambda: json.loads(paths.settings.read_text())["AcceptShortcut"] == "F7"
                and json.loads(paths.settings.read_text())["ExitShortcut"] == "F8"
                and not status()["setup"]["settingsDraft"],
                "valid session key pair was not persisted together",
            )
            assert (
                json.loads(paths.settings.read_text())["ExperimentalWriteback"]
                == saved["ExperimentalWriteback"]
            ), "saving session keys changed experimental writeback"
            assert Atspi.Selection.select_child(accept_key.get_selection_iface(), 0)
            assert Atspi.Selection.select_child(exit_key.get_selection_iface(), 0)
            activate_setting("保存修改")
            wait_for(
                lambda: json.loads(paths.settings.read_text())["AcceptShortcut"] == "Tab"
                and json.loads(paths.settings.read_text())["ExitShortcut"] == "Esc"
                and not status()["setup"]["settingsDraft"],
                "default session keys were not restored for later desktop scenarios",
            )
            assert Atspi.Selection.select_child(accept_key.get_selection_iface(), 12)
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

            def close_prompt_gone():
                try:
                    return named(process.pid, "放弃修改") is None
                except GLib.Error as error:
                    # Only the known closing-dialog child race is transient.
                    # Re-read the real tree; a vanished child is not proof that
                    # every copy of the prompt has closed.
                    if error.domain == "atspi_error" and re.fullmatch(
                        r'Method "GetChildAtIndex".*object /org/a11y/atspi/accessible/\d+ does not exist\s*',
                        error.message,
                        flags=re.DOTALL,
                    ):
                        return False
                    raise

            wait_for(
                close_prompt_gone,
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
            assert json.loads(paths.settings.read_text())["AcceptShortcut"] == "Tab"
            assert Atspi.Selection.is_child_selected(accept_key.get_selection_iface(), 0)
            assert Atspi.Component.grab_focus(wake.get_component_iface())
            key("ctrl+Page_Down")
            activate_setting(support["labels"]["tryIt"])
            assert not status()["setup"]["tutorialVisible"]
            assert selector.get_name() == desktop_support["title"]
            assert control(paths, "tutorial") is True
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
                    latest_preferences = json.loads(paths.settings.read_text())
                    practice_view = control(paths, "practiceStatus")["presentation"]
                    assert (
                        control(paths, "startOnDemandPractice", {"confirmedSwitch": False}) is False
                    ), "a stale OnDemand button silently changed Automatic mode"
                    assert json.loads(paths.settings.read_text())["Mode"] == 0

                    def practice_action(label):
                        node = wait_for(
                            lambda: named(managed_window["pid"], label),
                            f"practice action missing: {label}",
                        )
                        assert Atspi.Action.do_action(node.get_action_iface(), 0)
                        return node

                    practice_action(practice_view["startLabel"])
                    practice_action(practice_view["cancelLabel"])
                    assert json.loads(paths.settings.read_text())["Mode"] == 0
                    practice_action(practice_view["startLabel"])
                    cancel = wait_for(
                        lambda: named(managed_window["pid"], practice_view["cancelLabel"]),
                        "practice mode confirmation missing",
                    )
                    # Pick the affirmative action in this real dialog, not the
                    # identically labelled start button behind its modal window.
                    actions = cancel.get_parent()
                    confirm = next(
                        actions.get_child_at_index(index)
                        for index in range(actions.get_child_count())
                        if actions.get_child_at_index(index).get_name()
                        == practice_view["startLabel"]
                    )
                    assert Atspi.Action.do_action(confirm.get_action_iface(), 0)
                    wait_for(
                        lambda: json.loads(paths.settings.read_text())["Mode"] == 1
                        and focused(source),
                        "confirmed practice did not persist OnDemand mode and return to its field",
                    )
                    assert json.loads(paths.settings.read_text()) == {
                        **latest_preferences,
                        "Mode": 1,
                    }, "starting practice overwrote another saved preference"
                    practice_view = control(paths, "practiceStatus")["presentation"]
                    for key_name in ("previewStep", "replaceStep", "capabilityNote"):
                        wait_for(
                            lambda name=key_name: named(managed_window["pid"], practice_view[name]),
                            f"practice did not render the shared {key_name}",
                        )
                    key("alt+r")
                    wait_for(
                        lambda: text(source) == "" and focused(source), "practice reset failed"
                    )
                    typed(original)
                    wait_for(lambda: text(source) == original, "on-demand practice lost source")
                    key("ctrl+shift+F9")
                    wait_for(ready_to_accept, "practice F9 did not produce the real model preview")
                    assert text(source) == original, "practice preview changed the source"
                    candidate = wait_for(
                        lambda: named(managed_window["pid"], "TypeLay 译文候选"),
                        "practice model preview was not accessible",
                    )
                    expected_translation = text(candidate)
                    assert translated(expected_translation)
                    direct_replaced = "已直接替换原输入框文字。"
                    assert status().get("operationStatus") != direct_replaced
                    before_direct = status().get("commandFeedbackRevision", 0)
                    checkpoint = trace_checkpoint()
                    key("ctrl+shift+F10")
                    # Direct replacement has its own terminal feedback, not the
                    # Tab acceptance write outcome recorded by verified_write.
                    wait_for(
                        lambda: (value := status()).get("operationStatus") == direct_replaced
                        and value.get("commandFeedbackRevision", 0) > before_direct
                        and text(source) == expected_translation
                        and focused(source),
                        "practice F10 did not replace through the production model/input path",
                    )
                    assert not new_write_outcomes(checkpoint), (
                        "practice F10 used the Tab write path"
                    )
                    practice_text = text(source)
                    key("ctrl+a")
                    assert control(paths, "settings") is True
                    trial = settings_control(support["labels"]["tryIt"])
                    assert Atspi.Component.grab_focus(trial.get_component_iface())
                    key("ctrl+Page_Up")
                    language = settings_control("设置：源语言")
                    assert Atspi.Selection.select_child(language.get_selection_iface(), 1)
                    wait_for(
                        lambda: json.loads(paths.settings.read_text())["Source"] == "zh",
                        "practice language change did not save",
                    )
                    changed_view = control(paths, "practiceStatus")["presentation"]
                    assert changed_view["languageNeedsChange"]
                    assert changed_view["sampleText"] != practice_view["sampleText"]
                    assert (
                        control(paths, "startOnDemandPractice", {"confirmedSwitch": True}) is False
                    ), "practice accepted equal source and target languages"
                    request({"Action": {"FocusWindow": {"id": managed_window["id"]}}})
                    wait_for(
                        lambda: focused(source)
                        and named(managed_window["pid"], changed_view["currentStateNotice"]),
                        "practice did not refresh the shared language notice",
                    )
                    # Changing the sample changes only the placeholder. Actual
                    # typing replaces the preserved selection, rather than appending.
                    assert text(source) == practice_text
                    typed("Selection survived")
                    wait_for(
                        lambda: text(source) == "Selection survived",
                        "practice settings refresh changed the source selection",
                    )
                    assert control(paths, "settings") is True
                    assert Atspi.Selection.select_child(language.get_selection_iface(), 2)
                    wait_for(
                        lambda: json.loads(paths.settings.read_text())["Source"] == "en",
                        "restoring the source language did not save",
                    )
                    assert json.loads(paths.settings.read_text())["Mode"] == 1
                    evidence.append(
                        "Shared on-demand practice: cancel and stale unconfirmed start preserve mode; "
                        "explicit confirmation persists mode; real F9 previews without writes and "
                        "real F10 retranslates/replaces; presentation refresh preserves source/selection"
                    )
                    settings = json.loads(paths.settings.read_text())
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
                    trial = settings_control(support["labels"]["tryIt"])
                    assert Atspi.Component.grab_focus(trial.get_component_iface())
                    key("ctrl+Page_Up")
                    mode = settings_control("设置：工作模式")
                    assert Atspi.Selection.select_child(mode.get_selection_iface(), 0)
                    wait_for(
                        lambda: json.loads(paths.settings.read_text())["Mode"] == 0,
                        "the user's later mode change did not save",
                    )
                    assert Atspi.Component.grab_focus(wake.get_component_iface())
                    key("ctrl+Page_Down")
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
                assert json.loads(paths.settings.read_text())["Mode"] == 0, (
                    "closing practice overwrote the user's later mode change"
                )
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
        if gtk_acceptance_phases:
            try:
                (arguments.output / "gtk-acceptance-phases.json").write_text(
                    json.dumps(gtk_acceptance_phases, indent=2)
                )
            except OSError:
                pass  # Preserve the actual acceptance failure and cleanup.
        resources.close()
        for child in (process,):
            if child is not None and child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()
