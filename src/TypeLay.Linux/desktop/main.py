"""TypeLay Linux entry point. Control sockets accept commands, never source text."""

import argparse
import asyncio
import fcntl
import json
import os
import signal
import socket
import subprocess
import sys

from paths import Paths, connect_private, peer_credentials
from wire import encode, now_ms, receive_sync


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
        "qt6PluginInstalled": (paths.home / "native/qt6/generic/libtypelay-qt6.so").is_file(),
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


async def desktop(paths, background, lock):
    import gi

    gi.require_version("Gtk", "3.0")
    gi.require_version("Gdk", "3.0")
    from gi.repository import Gdk, GLib, Gtk
    from input import Input
    from link import Link
    from tray import Tray
    from ui import Overlay, Settings

    initialized, _ = Gtk.init_check([])
    if not initialized:
        raise RuntimeError("wayland-gtk-unavailable")
    stopping = asyncio.Event()
    tasks = set()
    components = {"input": None, "overlay": None, "settings": None, "tray": None}
    status = {"modelReady": False}
    installer = None
    install_task = None

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
            settings.configure(value)
            overlay.configure(value)
            components["tray"].configure(value)
            if not value["onboardingComplete"]:
                settings.show()
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

    async def install():
        nonlocal installer
        settings = components["settings"]
        settings.message.set_text("正在安装组件，完成自检后才会启用新版本。")
        try:
            installer = await asyncio.create_subprocess_exec(
                str(paths.home / "native/typelay-supervise"),
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
            while line := await installer.stdout.readline():
                if len(line) > 1024:
                    raise ValueError("setup-output-size")
                settings.message.set_text(line.decode("utf-8", errors="replace").strip())
            code = await installer.wait()
            if code != 0:
                settings.message.set_text(
                    "组件安装失败，原版本保持不变。可用 typelay --install-models 重试。"
                )
            else:
                ready = await host.ask("reloadModel", timeout=125)
                settings.message.set_text(
                    "本地翻译组件已就绪" if ready else "组件已安装，但翻译服务启动失败。"
                )
        finally:
            if installer and installer.returncode is None:
                installer.terminate()
                await installer.wait()
            installer = None

    def install_requested():
        nonlocal install_task
        if install_task is None or install_task.done():
            install_task = task(install())

    peers = set()
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server_watch = 0
    try:
        components["input"] = Input(GLib, Gdk, paths, host, task)
        components["settings"] = Settings(host, task, stopping.set, install_requested)
        components["overlay"] = Overlay(
            host, components["input"], task, components["settings"].show
        )

        components["tray"] = Tray(
            paths,
            host,
            task,
            components["settings"].show,
            stopping.set,
            components["settings"].report_tray,
        )

        async def command(operation, value):
            if operation in ("wake", "direct"):
                return await components["input"].trigger(operation, value)
            if operation == "settings":
                components["settings"].show()
                return True
            if operation == "quit":
                GLib.timeout_add(50, lambda: (stopping.set(), False)[1])
                return True
            if operation == "status":
                return {
                    **status,
                    "trayAvailable": components["tray"].indicator is not None,
                    "trayConnected": components["tray"].connected,
                    "nativePeers": sum(bool(peer.lifetime) for peer in components["input"].peers),
                    "overlayVisible": components["overlay"].visible,
                    "editing": components["overlay"].editing,
                    "acceptanceReady": components["overlay"].acceptance_ready,
                    "viewState": (components["overlay"].value or {}).get("state"),
                    "sourceSupported": components["input"].focused_peer() is not None,
                    "input": components["input"].diagnostics(),
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
                peer_credentials(connection)
                if len(peers) >= 8:
                    raise OSError("control-capacity")
                peer = Link(
                    GLib,
                    connection.fileno(),
                    connection.fileno(),
                    lambda *_: None,
                    command,
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
        if not background:
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
    group.add_argument("--quit", action="store_true", help="退出当前 Wayland 会话的 TypeLay")
    group.add_argument("--doctor", action="store_true", help="输出不包含输入内容的安装及会话状态")
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
        plugins = environment.get("QT_QPA_GENERIC_PLUGINS", "").split(",")
        environment["QT_QPA_GENERIC_PLUGINS"] = ",".join(
            dict.fromkeys([*filter(None, plugins), "typelay"])
        )
        plugin_paths = environment.get("QT_PLUGIN_PATH", "").split(":")
        environment["QT_PLUGIN_PATH"] = ":".join(
            dict.fromkeys([str(paths.home / "native/qt6"), *filter(None, plugin_paths)])
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
    operation = "quit" if args.quit else "status" if args.background else "settings"
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
            loop.run_until_complete(desktop(paths, args.background, lock))
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
