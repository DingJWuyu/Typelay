"""TypeLay Linux entry point. Control sockets accept commands, never source text."""

import argparse
import asyncio
import fcntl
import json
import os
import signal
import socket
import struct
import subprocess
import sys
import uuid

from paths import Paths, connect_private, peer_credentials
from setup import FAILURE_PREFIX, PRESENTATION_UNAVAILABLE, failure_code
from wire import MAX_FRAME, encode, now_ms, receive_sync


def control(paths, operation, data=None):
    with connect_private(paths.control, 1.5) as connection:
        connection.sendall(encode({"id": 1, "op": operation, "data": data}))
        return receive_sync(connection).get("data")


def doctor(paths):
    from niri import contents, request

    result = {
        "target": "CachyOS x86_64 / niri / Wayland",
        "wayland": bool(os.environ.get("WAYLAND_DISPLAY")),
        "hostInstalled": (paths.home / "app/TypeLay.Linux").is_file(),
        "gtkModuleInstalled": paths.module.is_file(),
        "browserHostInstalled": (paths.home / "typelay-browser-host").is_file(),
        "workerInstalled": paths.python.is_file(),
        "modelInventoryPresent": (paths.models / "installed.json").is_file(),
    }
    try:
        result["niriVersion"] = contents(request("Version"), "Version")
    except (OSError, EOFError, ValueError, KeyError):
        result["niriVersion"] = None
    try:
        result["session"] = control(paths, "status")
    except (OSError, EOFError, ValueError):
        result["session"] = None
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["wayland"] and result["niriVersion"] and result["hostInstalled"] else 1


async def tutorial_preview(paths, text):
    """Use the production shared ModelClient through its existing one-shot port."""
    identity = str(uuid.uuid4())
    selection = {"start": 0, "length": len(text.encode("utf-16-le")) // 2}
    request = {
        "id": 1,
        "operation": "translateEditorSnapshot",
        "data": {
            "documentId": identity,
            "version": 1,
            "selection": selection,
            "text": text,
            "languageId": "plaintext",
        },
    }
    process = await asyncio.create_subprocess_exec(
        str(paths.home / "native/typelay-supervise"),
        str(paths.home / "app/TypeLay.Linux"),
        "--editor-host",
        str(paths.home),
        str(paths.config),
        str(paths.data),
        stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.DEVNULL,
    )
    try:
        async with asyncio.timeout(140):
            process.stdin.write(encode(request))
            await process.stdin.drain()
            process.stdin.close()
            length = struct.unpack("<I", await process.stdout.readexactly(4))[0]
            if not 0 < length <= MAX_FRAME:
                raise ValueError("tutorial-response-size")
            value = json.loads(await process.stdout.readexactly(length))
            code = await process.wait()
        result = value.get("data") if isinstance(value, dict) else None
        if (
            code
            or not isinstance(value, dict)
            or value.get("id") != 1
            or not isinstance(result, dict)
            or result.get("documentId") != identity
            or result.get("version") != 1
            or result.get("selection") != selection
            or not isinstance(result.get("translation"), str)
            or not result["translation"].strip()
            or "\0" in result["translation"]
        ):
            raise ValueError("tutorial-translation-unavailable")
        return result["translation"]
    except asyncio.IncompleteReadError:
        raise EOFError("tutorial-host-closed") from None
    finally:
        if process.returncode is None:
            process.terminate()
            await process.wait()


async def desktop(paths, background, lock, tutorial=False):
    import gi

    gi.require_version("Gtk", "3.0")
    gi.require_version("Gdk", "3.0")
    from application_support import ApplicationSupport
    from gi.repository import Gdk, GLib, Gtk
    from input import Input
    from link import Link
    from overlay import Overlay
    from practice_session import PracticeSession
    from tray import Tray
    from ui import Settings

    initialized, _ = Gtk.init_check([])
    if not initialized:
        raise RuntimeError("wayland-gtk-unavailable")
    stopping = asyncio.Event()
    tasks = set()
    components = {"input": None, "overlay": None, "settings": None, "tray": None}
    status = {"modelReady": False}
    installer = None
    install_task = None
    pause_installation = False

    def task(coroutine):
        if len(tasks) >= 64:
            coroutine.close()
            stopping.set()
            return
        future = asyncio.create_task(coroutine)
        tasks.add(future)

        def finished(result):
            tasks.discard(result)
            if not result.cancelled() and result.exception() is not None:
                print("TypeLay: desktop operation failed safely.", file=sys.stderr)
                stopping.set()

        future.add_done_callback(finished)
        return future

    def notification(operation, value):
        input_service = components["input"]
        if input_service:
            input_service.notification(operation, value)
        overlay, settings = components["overlay"], components["settings"]
        if operation == "settings":
            first_settings = settings.value is None
            settings.configure(value)
            overlay.configure(value)
            components["tray"].configure(value)
            if (
                first_settings
                and not value["onboardingComplete"]
                and not settings.tutorial.window.get_visible()
            ):
                settings.show()
        elif operation == "settingsGuidance":
            settings.render_guidance(value)
        elif operation == "render":
            overlay.render(value)
        elif operation == "hide":
            overlay.hide()
        elif operation == "resumeEditor":
            overlay.resume()
        elif operation == "status":
            # Do not expose the process identifier or any input content via CLI.
            status.update({key: item for key, item in value.items() if key != "lastApplication"})
            settings.report(value)
            components["tray"].report(value)
        elif operation == "operationFeedback":
            components["tray"].feedback(value)
            status["operationFeedback"] = components["tray"].feedback_status

    async def host_request(operation, value):
        return await components["input"].request(operation, value)

    process = subprocess.Popen(
        [
            str(paths.home / "native/typelay-supervise"),
            str(paths.home / "app/TypeLay.Linux"),
            str(paths.home),
            str(paths.config),
            str(paths.data),
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        cwd=paths.home,
    )
    host = Link(
        GLib,
        process.stdout.fileno(),
        process.stdin.fileno(),
        notification,
        host_request,
        stopping.set,
    )

    async def installation_result(state, failure=None, activated=False):
        settings = components["settings"]
        try:
            presentation = await host.ask("setupFailure", failure, timeout=2)
            if state == "paused":
                message, label = presentation["pausedMessage"], presentation["resumeLabel"]
            else:
                view = presentation["failure"]
                message = view["message"] + "\n" + view["nextStep"]
                if activated:
                    message += "\n组件已安装，但翻译服务尚未就绪。可退出并重新启动 TypeLay。"
                label = view["retryLabel"]
            settings.installation(state, message, label)
        except (OSError, ValueError, KeyError, TypeError, EOFError, TimeoutError):
            settings.installation(state, PRESENTATION_UNAVAILABLE)

    async def install():
        nonlocal installer
        settings = components["settings"]
        settings.installation(
            "running", "正在安装组件，完成自检后才会启用新版本；可暂停并保留已验证的下载进度。"
        )
        failure = None
        reload_started = False
        try:
            installer = await asyncio.create_subprocess_exec(
                "/usr/bin/python3",
                "-I",
                "-B",
                str(paths.home / "desktop/setup.py"),
                str(paths.home),
                str(paths.data),
                str(paths.cache),
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.DEVNULL,
            )
            if pause_installation:
                installer.terminate()
            while line := await installer.stdout.readline():
                if len(line) > 1024:
                    raise ValueError("setup-output-size")
                message = line.decode("utf-8", errors="replace").strip()
                if message.startswith(FAILURE_PREFIX):
                    failure = failure_code(message, failure)
                else:
                    settings.message.set_text(message)
            code = await installer.wait()
            if failure is None and (code == 75 or (pause_installation and code != 0)):
                await installation_result("paused")
            elif code != 0 or failure is not None:
                await installation_result("failed", failure)
            else:
                reload_started = True
                ready = await host.ask("reloadModel", timeout=125)
                if ready:
                    settings.installation("ready", "本地翻译组件已就绪")
                else:
                    await installation_result("failed", "runtime", activated=True)
        except (OSError, ValueError, EOFError, TimeoutError):
            await installation_result(
                "failed", "runtime" if reload_started else failure, activated=reload_started
            )
        finally:
            if installer and installer.returncode is None:
                installer.terminate()
                await installer.wait()
            installer = None

    def install_requested():
        nonlocal install_task, pause_installation
        if install_task is None or install_task.done():
            pause_installation = False
            install_task = task(install())

    def pause_requested():
        nonlocal pause_installation
        if install_task and not install_task.done() and not pause_installation:
            pause_installation = True
            components["settings"].installation("pausing", "正在暂停，保留已验证的下载进度…")
            if installer and installer.returncode is None:
                installer.terminate()

    peers = set()
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server_watch = 0
    try:
        components["input"] = Input(GLib, Gdk, paths, host, task)
        support = ApplicationSupport(paths)
        practice = PracticeSession(host, components["input"], support)

        async def support_view():
            await support.refresh()
            view = await host.ask(
                "applicationSupport",
                {
                    "facts": support.facts(
                        components["input"].peers,
                        enabled=bool(
                            components["settings"].value and components["settings"].value["enabled"]
                        ),
                    )
                },
                timeout=2,
            )

            return {**view, "desktopGuidance": support.desktop_guidance()}

        async def support_action(identity, action, directory=None):
            if identity == "desktop-text" and action in ("Connect", "Remove"):
                await practice.end(practice.pid)
            await support.execute(identity, action, directory)
            return await support_view()

        components["settings"] = Settings(
            host,
            task,
            stopping.set,
            install_requested,
            pause_requested,
            lambda text: tutorial_preview(paths, text),
            support_view,
            support_action,
            support.component_folder,
        )
        components["overlay"] = Overlay(
            host, components["input"], task, components["settings"].show
        )

        components["tray"] = Tray(
            paths,
            host,
            task,
            components["settings"].show,
            lambda: components["settings"].dismiss(quit_requested=stopping.set),
            components["settings"].report_tray,
        )

        async def command(operation, value, caller_pid=None):
            if operation in ("wake", "direct"):
                return await components["input"].trigger(operation, value)
            if operation == "settings":
                components["settings"].show()
                return True
            if operation == "tutorial":
                components["settings"].open_tutorial()
                return True
            if operation == "practiceStatus":
                progress = await practice.progress(caller_pid)
                return {
                    "modelReady": status.get("modelReady", False),
                    "managedPractice": support.owns_practice(caller_pid),
                    "presentation": await host.ask(
                        "onDemandPractice", progress["runId"] if progress else None, timeout=2
                    ),
                    "progress": progress,
                    "practiceEnabled": bool(
                        components["settings"].value and components["settings"].value["enabled"]
                    ),
                }
            if operation == "startOnDemandPractice":
                if (
                    not isinstance(value, dict)
                    or set(value) != {"confirmedSwitch"}
                    or type(value["confirmedSwitch"]) is not bool
                ):
                    return False
                return await practice.begin(caller_pid, value["confirmedSwitch"])
            if operation == "endOnDemandPractice":
                return await practice.end(caller_pid) if value is None else False
            if operation == "finishOnboarding":
                if (
                    not isinstance(value, dict)
                    or set(value) != {"outcome"}
                    or value["outcome"] not in ("completed", "skipped")
                    or components["settings"].value is None
                ):
                    return False
                if value["outcome"] == "completed":
                    saved = await practice.complete(caller_pid)
                else:
                    saved = await host.ask("dismissOnboarding") is True
                if saved:
                    components["settings"].tutorial.dismiss()
                return saved
            if operation == "quit":
                GLib.timeout_add(50, lambda: (stopping.set(), False)[1])
                return True
            if operation == "status":
                remaining = components["input"].acceptance_remaining_ms()
                return {
                    **status,
                    "trayAvailable": components["tray"].indicator is not None,
                    "trayConnected": components["tray"].connected,
                    "nativePeers": sum(bool(peer.lifetime) for peer in components["input"].peers),
                    "overlayVisible": components["overlay"].visible,
                    "editing": components["overlay"].editing,
                    "acceptanceReady": components["overlay"].acceptance_ready and remaining > 0,
                    "acceptanceRemainingMs": remaining,
                    "viewState": (components["overlay"].value or {}).get("state"),
                    "sourceSupported": components["input"].focused_peer() is not None,
                    "input": components["input"].diagnostics(),
                    "setup": components["settings"].diagnostics(),
                }
            return False

        paths.control.unlink(missing_ok=True)
        server.bind(str(paths.control))
        paths.control.chmod(0o600)
        server.listen(8)
        server.setblocking(False)

        def accept(_fd, _condition):
            try:
                connection, _ = server.accept()
            except BlockingIOError:
                return True
            try:
                pid, _, _ = peer_credentials(connection)
                if len(peers) >= 8:
                    raise OSError("control-capacity")
                peer = Link(
                    GLib,
                    connection.fileno(),
                    connection.fileno(),
                    lambda *_: None,
                    lambda operation, value: command(operation, value, pid),
                    lambda: peers.discard(peer),
                    connection,
                )
                peers.add(peer)
                GLib.timeout_add(2000, lambda: (peer.close(), False)[1])
            except OSError:
                connection.close()
            return True

        server_watch = GLib.io_add_watch(server.fileno(), GLib.IO_IN, accept)
        loop = asyncio.get_running_loop()
        for sig in (signal.SIGTERM, signal.SIGINT):
            loop.add_signal_handler(sig, stopping.set)
        loop.set_exception_handler(
            lambda *_: (
                print("TypeLay: event dispatch failed safely.", file=sys.stderr),
                stopping.set(),
            )
        )
        await host.ask("start", timeout=5)
        if tutorial:
            components["settings"].open_tutorial()
        elif not background and not components["settings"].tutorial.window.get_visible():
            components["settings"].show()
        await stopping.wait()
    finally:
        for future in list(tasks):
            future.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
        if server_watch:
            GLib.source_remove(server_watch)
        for peer in list(peers):
            peer.close()
        server.close()
        if components["overlay"]:
            components["overlay"].hide()
        if components["input"]:
            components["input"].close()
        if components["tray"]:
            components["tray"].close()
        if components["settings"]:
            components["settings"].tutorial.window.destroy()
            components["settings"].window.destroy()
        if host.channel.alive:
            host.send("quit")
            await asyncio.sleep(0.05)
        host.close()
        process.stdin.close()
        process.stdout.close()
        try:
            await asyncio.to_thread(process.wait, 2)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:
                await asyncio.to_thread(process.wait, 2)
            except subprocess.TimeoutExpired:
                process.kill()
                await asyncio.to_thread(process.wait)
        paths.control.unlink(missing_ok=True)
        os.close(lock)


def main():
    parser = argparse.ArgumentParser(description="TypeLay CachyOS / niri / Wayland 客户端")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--background", action="store_true", help="启动后台会话，不主动打开设置")
    group.add_argument("--settings", action="store_true", help="打开设置")
    group.add_argument("--tutorial", action="store_true", help="打开本地试译与实际输入框练习指南")
    group.add_argument(
        "--practice", action="store_true", help="在当前输入法环境打开真实 GTK 实操窗口"
    )
    group.add_argument("--quit", action="store_true", help="退出当前 Wayland 会话的 TypeLay")
    group.add_argument("--doctor", action="store_true", help="输出不包含输入内容的安装及会话状态")
    group.add_argument(
        "--editor-host", action="store_true", help="处理一次明确发起的编辑器翻译快照"
    )
    group.add_argument("--install-models", action="store_true", help="下载、校验并安装本地翻译组件")
    group.add_argument("--version", action="store_true")
    group.add_argument("--wake", action="store_true", help="由 niri 快捷键唤醒当前已连接输入框")
    group.add_argument(
        "--direct", action="store_true", help="由 niri 快捷键直接翻译当前已连接输入框"
    )
    parser.add_argument(
        "command", nargs=argparse.REMAINDER, help="launch APPLICATION [ARGUMENT ...]"
    )
    args = parser.parse_args()
    if args.version:
        print("TypeLay Linux preview 0.2.0")
        return 0
    paths = Paths()
    if args.editor_host:
        if args.command:
            parser.error("--editor-host does not accept application arguments")
        host = paths.home / "app/TypeLay.Linux"
        os.execv(
            str(host),
            [str(host), "--editor-host", str(paths.home), str(paths.config), str(paths.data)],
        )
    if args.wake or args.direct:
        from niri import contents, request

        stamp = now_ms()
        window = contents(request("FocusedWindow"), "FocusedWindow")
        accepted = bool(
            window
            and control(
                paths,
                "wake" if args.wake else "direct",
                {"windowId": window["id"], "triggeredAtMs": stamp},
            )
        )
        if not accepted:
            print("TypeLay: 当前输入框或按需快捷键尚未就绪，未启动新的翻译替换。", file=sys.stderr)
        return 0 if accepted else 1
    if args.practice:
        if args.command:
            parser.error("--practice 不接受应用参数")
        args.command = [
            "launch",
            "/usr/bin/python3",
            "-I",
            "-B",
            str(paths.home / "desktop/practice.py"),
        ]
    if args.command:
        if len(args.command) < 2 or args.command[0] != "launch":
            parser.error("使用 typelay launch APPLICATION [ARGUMENT ...]")
        if not paths.module.is_file():
            raise OSError("native-module-unavailable")
        environment = dict(os.environ)
        environment.pop("TYPELAY_DESKTOP", None)
        modules = environment.get("GTK3_MODULES", "").split(":")
        environment["GTK3_MODULES"] = ":".join(
            dict.fromkeys([str(paths.module), *filter(None, modules)])
        )
        os.execvpe(args.command[1], args.command[1:], environment)
    if args.doctor:
        return doctor(paths)
    if args.install_models:
        os.execv(
            "/usr/bin/python3",
            [
                "/usr/bin/python3",
                "-I",
                "-B",
                str(paths.home / "desktop/setup.py"),
                str(paths.home),
                str(paths.data),
                str(paths.cache),
            ],
        )
    operation = (
        "quit"
        if args.quit
        else "tutorial"
        if args.tutorial
        else "status"
        if args.background
        else "settings"
    )
    try:
        if control(paths, operation):
            return 0
    except (OSError, EOFError, ValueError):
        if args.quit:
            return 0
    if not os.environ.get("WAYLAND_DISPLAY") or not os.environ.get("NIRI_SOCKET"):
        raise OSError("niri-wayland-session-required")
    lock = os.open(paths.session / "desktop.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        os.close(lock)
        print("TypeLay: 当前会话正在启动，请从应用菜单重新打开设置。", file=sys.stderr)
        return 1
    os.umask(0o077)
    os.environ["TYPELAY_DESKTOP"] = "1"
    os.environ["GDK_BACKEND"] = "wayland"
    from gi.events import GLibEventLoop
    from gi.repository import GLib

    loop = GLibEventLoop(GLib.MainContext.default())
    try:
        with loop:
            loop.run_until_complete(desktop(paths, args.background, lock, args.tutorial))
    finally:
        loop.close()
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, EOFError, ValueError, RuntimeError, ImportError):
        print(
            "TypeLay: 无法启动。请确认依赖已安装，并在 niri Wayland 会话运行 typelay --doctor。",
            file=sys.stderr,
        )
        raise SystemExit(1) from None
