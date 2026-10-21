"""Linux entry point. Only the explicit editor snapshot carries text on control."""

import argparse
import asyncio
import fcntl
import json
import os
import re
import select
import signal
import socket
import struct
import subprocess
import sys
import time
import uuid

from paths import Paths, connect_private, peer_credentials
from setup import FAILURE_PREFIX, PREPARED_PREFIX, failure_code
from wire import MAX_FRAME, Decoder, encode, now_ms, receive_sync

ACTIVATION_TIMEOUT = 225


def control(paths, operation, data=None, *, timeout=None):
    deadline = time.monotonic() + timeout if timeout is not None else None
    with connect_private(
        paths.control, min(1.5, timeout) if timeout is not None else 1.5
    ) as connection:
        if deadline is not None:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("control-deadline")
            connection.settimeout(remaining)
        elif operation == "activateModel":
            connection.settimeout(ACTIVATION_TIMEOUT + 5)
        connection.sendall(encode({"id": 1, "op": operation, "data": data}))
        if deadline is None:
            return receive_sync(connection).get("data")
        decoder = Decoder()
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("control-deadline")
            connection.settimeout(remaining)
            chunk = connection.recv(65536)
            if not chunk:
                raise EOFError("connection-closed")
            frames = decoder.feed(chunk)
            if frames:
                if len(frames) != 1:
                    raise ValueError("control-response")
                return frames[0].get("data")


def valid_activation(value):
    return (
        isinstance(value, dict)
        and set(value) == {"identity", "generation"}
        and all(
            isinstance(item, str) and re.fullmatch(r"[0-9a-f]{32}", item) for item in value.values()
        )
    )


def prepared_generation(message, previous):
    if not message.startswith(PREPARED_PREFIX):
        return previous
    value = message[len(PREPARED_PREFIX) :]
    if previous is not None or not re.fullmatch(r"[0-9a-f]{32}", value):
        raise ValueError("model-preparation-result")
    return value


async def activate_model(host, value):
    if not valid_activation(value):
        return "unchanged"
    confirmed = False
    try:
        result = await host.ask("activateModel", value, timeout=ACTIVATION_TIMEOUT)
        confirmed = result == "confirmed"
        return result if result in ("confirmed", "unchanged", "unconfirmed") else "unconfirmed"
    finally:
        # Link.ask only removes its future. Explicit identity cancellation also
        # reaches the host when its control peer disconnects or GUI pauses.
        if not confirmed:
            try:
                host.send("cancelModelActivation", value["identity"])
            except (OSError, EOFError):
                pass


async def setup_observation(paths, command, *arguments):
    process = await asyncio.create_subprocess_exec(
        "/usr/bin/python3",
        "-I",
        "-B",
        str(paths.home / "desktop/setup.py"),
        command,
        str(paths.home),
        *map(str, arguments),
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.DEVNULL,
    )
    try:
        output, _ = await asyncio.wait_for(process.communicate(), 30)
        if process.returncode or len(output) > 1024:
            raise ValueError("setup-observation")
        return json.loads(output)
    finally:
        if process.returncode is None:
            try:
                process.terminate()
            except ProcessLookupError:
                pass
            try:
                await asyncio.wait_for(process.wait(), 2)
            except TimeoutError:
                try:
                    process.kill()
                except ProcessLookupError:
                    pass
                await asyncio.wait_for(process.wait(), 2)


def install_models(paths):
    generation = None
    failure = None
    process = subprocess.Popen(
        [
            "/usr/bin/python3",
            "-I",
            "-B",
            str(paths.home / "desktop/setup.py"),
            str(paths.home),
            str(paths.data),
            str(paths.cache),
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
    )
    try:
        while line := process.stdout.readline(1025):
            if len(line) > 1024:
                raise ValueError("setup-output-size")
            message = line.decode("utf-8", errors="replace").strip()
            generation = prepared_generation(message, generation)
            failure = failure_code(message, failure)
            if not message.startswith(PREPARED_PREFIX):
                print(message, flush=True)
        code = process.wait()
        if code or generation is None or failure is not None:
            return code or 1
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=5)
        process.stdout.close()
    value = {"identity": uuid.uuid4().hex, "generation": generation}
    # A missing socket alone does not prove that no desktop is starting. Keep
    # its exact session lock through the helper's entire lifetime.
    lock = os.open(paths.session / "desktop.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            result = control(paths, "activateModel", value)
        else:
            helper = subprocess.Popen(
                [
                    str(paths.home / "native/typelay-supervise"),
                    str(paths.home / "app/TypeLay.Linux"),
                    "--activate-model",
                    str(paths.home),
                    str(paths.data),
                    generation,
                ],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            try:
                output, error = helper.communicate(timeout=ACTIVATION_TIMEOUT)
                reported = (
                    output.decode("ascii", errors="replace").strip()
                    if len(output) <= 64
                    else "unknown"
                )
                if reported not in ("confirmed", "unchanged", "unconfirmed"):
                    reported = "empty" if not output else "unknown"
                result = (
                    reported
                    if reported in ("confirmed", "unchanged", "unconfirmed")
                    else "unconfirmed"
                )
                if helper.returncode and result == "confirmed":
                    result = "unconfirmed"
                if result != "confirmed":
                    failure = (
                        error.decode("ascii", errors="replace").strip()
                        if len(error) <= 128
                        else "unknown"
                    )
                    if failure not in (
                        "installation-invalid",
                        "worker-not-ready",
                        "activation-failed",
                        "publication-unconfirmed",
                        "cancelled",
                    ):
                        failure = "unknown"
                    # Preserve this attempt's outcome, never raw child output or
                    # an inferred claim that an unconfirmed publish did not occur.
                    print(
                        f"TypeLay: activation-helper exit={helper.returncode} "
                        f"status={reported} failure={failure}",
                        file=sys.stderr,
                    )
            finally:
                if helper.poll() is None:
                    helper.terminate()
                    helper.wait(timeout=5)
    finally:
        os.close(lock)
    if result == "confirmed":
        print("本地翻译组件安装完成，真实双向自检与服务握手通过。", flush=True)
        return 0
    print("TypeLay: 新组件启用未确认，已保留组件，可重新打开设置检查。", file=sys.stderr)
    return 1


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


async def translate_editor_snapshot(host, request, timeout):
    identity = uuid.uuid4().hex
    completed = False
    try:
        result = await host.ask(
            "translateEditorSnapshot", {"identity": identity, "request": request}, timeout=timeout
        )
        completed = True
        return result
    finally:
        if not completed:
            try:
                host.send("cancelEditorSnapshot", identity)
            except (OSError, EOFError):
                pass


async def tutorial_preview(host, text):
    """Reuse the serving model; cancellation withdraws only this preview."""
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
    value = await translate_editor_snapshot(host, request, 140)
    result = value.get("data") if isinstance(value, dict) else None
    if (
        not isinstance(value, dict)
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


def editor_host(paths):
    """One source-bound command, using the desktop or a supervised one-shot."""
    deadline = time.monotonic() + 215
    identity = 0
    lock = None
    helper = None
    try:
        reading = min(deadline, time.monotonic() + 5)

        def exact(length):
            data = bytearray()
            while len(data) < length:
                remaining = reading - time.monotonic()
                if remaining <= 0 or not select.select([sys.stdin], [], [], remaining)[0]:
                    raise TimeoutError("editor-request")
                part = os.read(sys.stdin.fileno(), length - len(data))
                if not part:
                    raise EOFError("editor-request")
                data.extend(part)
            return bytes(data)

        length = struct.unpack("<I", exact(4))[0]
        if not 0 < length <= MAX_FRAME:
            raise ValueError("editor-request")
        request = json.loads(exact(length))
        if (
            isinstance(request, dict)
            and type(request.get("id")) is int
            and 0 < request["id"] < 2**63
        ):
            identity = request["id"]
        lock = os.open(
            paths.session / "desktop.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600
        )
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            # Never fall back after this decision, even if the desktop is still
            # starting, rejects the snapshot, disconnects or times out.
            response = control(
                paths,
                "translateEditorSnapshot",
                request,
                timeout=max(0.001, deadline - time.monotonic()),
            )
        else:
            environment = dict(os.environ)
            environment.pop("TYPELAY_MODEL_USE_LOCK", None)
            helper = subprocess.Popen(
                [
                    str(paths.home / "native/typelay-supervise"),
                    "--require-exit-proof",
                    str(paths.home / "app/TypeLay.Linux"),
                    "--editor-host",
                    str(paths.home),
                    str(paths.config),
                    str(paths.data),
                ],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                env=environment,
                pass_fds=(lock,),
                start_new_session=True,
            )
            output, _ = helper.communicate(
                b"\1" + encode(request), timeout=max(0.001, deadline - time.monotonic())
            )
            if helper.returncode != 200 or len(output) < 4 or len(output) > MAX_FRAME + 4:
                raise ValueError("editor-exit-unconfirmed")
            length = struct.unpack("<I", output[:4])[0]
            if length != len(output) - 4:
                raise ValueError("editor-response")
            response = json.loads(output[4:])
        if not isinstance(response, dict) or response.get("id") != identity:
            raise ValueError("editor-response")
        sys.stdout.buffer.write(encode(response))
        sys.stdout.buffer.flush()
        return 0
    except (OSError, ValueError, EOFError, TimeoutError, subprocess.SubprocessError):
        sys.stdout.buffer.write(encode({"id": identity, "error": "host-failed"}))
        sys.stdout.buffer.flush()
        return 1
    finally:
        if helper is not None:
            if helper.poll() is None:
                try:
                    helper.terminate()
                    helper.wait(timeout=5)
                except (ProcessLookupError, subprocess.TimeoutExpired):
                    # The isolated supervisor keeps the inherited desktop lock
                    # until real family cleanup; never SIGKILL the proof owner.
                    pass
            if helper.stdin:
                helper.stdin.close()
            if helper.stdout:
                helper.stdout.close()
        if lock is not None:
            os.close(lock)


async def desktop(paths, background, lock, tutorial=False):
    import gi

    gi.require_version("Gtk", "3.0")
    gi.require_version("Gdk", "3.0")
    from application_support import BROWSERS, ApplicationSupport
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
    installation_request = None

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
        if operation == "automaticPracticeCompletionRetiring":
            practice.completing(value)
        elif operation == "settings":
            first_settings = settings.value is None
            settings.configure(value)
            if settings.window.get_visible() and not settings.translation.saving:
                settings.translation.refresh()
            overlay.configure(value)
            components["tray"].configure(value)
            if (
                first_settings
                and not value["onboardingComplete"]
                and not settings.tutorial.window.get_visible()
            ):
                settings.show()
        elif operation == "translationSettingsChanged":
            if not settings.translation.saving:
                settings.translation.refresh()
        elif operation == "settingsGuidance":
            settings.render_guidance(value)
            overlay.render_guidance(value)
            components["tray"].render_guidance(value)
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
        if operation == "sessionGlossaryReturn":
            return await components["overlay"].return_glossary_to_source(value)
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

    def installation_result(state, failure=None, activated=False):
        settings = components["settings"]
        settings.installation(
            state,
            "新组件启用结果尚未确认。已保留本地组件，可重新启动 TypeLay 检查。"
            if activated
            else "",
            failure=failure,
        )

    async def install(request):
        nonlocal installer, installation_request
        settings = components["settings"]
        settings.installation(
            "running", "正在准备组件，文件校验并成功加载后才会启用；可暂停并保留已验证的下载进度。"
        )
        failure = None
        reload_started = False
        committed = False
        generation = None
        try:
            installer = await asyncio.create_subprocess_exec(
                "/usr/bin/python3",
                "-I",
                "-B",
                str(paths.home / "desktop/setup.py"),
                str(paths.home),
                str(paths.data),
                str(paths.cache),
                request,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.DEVNULL,
            )
            if pause_installation:
                installer.terminate()
            while line := await installer.stdout.readline():
                if len(line) > 1024:
                    raise ValueError("setup-output-size")
                message = line.decode("utf-8", errors="replace").strip()
                generation = prepared_generation(message, generation)
                if message.startswith(PREPARED_PREFIX):
                    continue
                if message.startswith(FAILURE_PREFIX):
                    failure = failure_code(message, failure)
                else:
                    settings.installation_progress(message)
            code = await installer.wait()
            if failure is None and (code == 75 or pause_installation):
                installation_result("paused")
            elif code != 0 or failure is not None or generation is None:
                installation_result("failed", failure)
            else:
                reload_started = True
                settings.installation("starting")
                result = await activate_model(
                    host, {"identity": uuid.uuid4().hex, "generation": generation}
                )
                if result == "confirmed":
                    committed = True
                    installation_request = None
                    settings.installation("ready")
                    cleaned = False
                    # The host enqueues its final status before the reply on
                    # this same ordered link. Publication alone is not exit proof.
                    if status.get("modelFailureCode") is None:
                        try:
                            result = await setup_observation(
                                paths, "--cleanup-packages", paths.data, paths.cache
                            )
                            cleaned = result == {"cleaned": True}
                        except (OSError, ValueError, EOFError, TimeoutError):
                            pass
                    if not cleaned or status.get("modelCleanupPending"):
                        settings.installation_progress(
                            "当前模型已启用，但部分旧组件或下载缓存尚未释放空间。"
                        )
                else:
                    installation_result("failed", "runtime", activated=result == "unconfirmed")
        except asyncio.CancelledError:
            if committed:
                settings.installation_progress("当前模型已启用，空间清理尚未完成。")
            elif reload_started:
                installation_result("failed", "runtime", activated=True)
            else:
                installation_result("paused")
        except (OSError, ValueError, EOFError, TimeoutError):
            if committed:
                settings.installation_progress("当前模型已启用，空间清理尚未完成。")
            else:
                installation_result(
                    "failed", "runtime" if reload_started else failure, activated=reload_started
                )
        finally:
            if installer and installer.returncode is None:
                installer.terminate()
                await installer.wait()
            installer = None

    def install_requested(directions=None, repair=False):
        nonlocal install_task, pause_installation, installation_request
        if install_task is None or install_task.done():
            if directions is not None or installation_request is None:
                installation_request = json.dumps(
                    {"directions": directions, "repair": repair}, separators=(",", ":")
                )
            pause_installation = False
            install_task = task(install(installation_request))

    def package_requested(request):
        nonlocal install_task, pause_installation, installation_request
        if install_task is not None and not install_task.done():
            return
        if request is None:
            installation_request = None
            components["settings"].installation("idle")
            return
        # Serialize now; later UI navigation or selection changes cannot mutate
        # the request replayed by Resume/Retry, including expectedCurrent.
        installation_request = json.dumps(request, separators=(",", ":"))
        pause_installation = False
        install_task = task(install(installation_request))

    async def preparation_refresh(phase, failure_code):
        native = {"available": False, "reason": "native-unavailable"}
        try:
            observed = await setup_observation(paths, "--check-hy-runtime")
            if observed == {"available": True, "reason": None}:
                native = observed
        except (OSError, ValueError, EOFError, TimeoutError):
            pass
        return await host.ask(
            "translationPreparation",
            {
                "phase": phase,
                "failureCode": failure_code,
                "hyRuntimeAvailable": native["available"],
                "hyUnavailableReason": native["reason"],
            },
        )

    def pause_requested():
        nonlocal pause_installation
        if install_task and not install_task.done() and not pause_installation:
            pause_installation = True
            components["settings"].installation("pausing", "正在暂停，保留已验证的下载进度…")
            if installer and installer.returncode is None:
                installer.terminate()
            else:
                install_task.cancel()

    peers = set()
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server_watch = 0
    try:
        support = ApplicationSupport(paths)
        components["input"] = Input(
            GLib, Gdk, paths, host, task, browser_registration=support.browser_registration
        )
        practice = PracticeSession(host, components["input"], support, task)

        async def support_view():
            await support.refresh()
            facts = support.facts(
                components["input"].peers,
                enabled=bool(
                    components["settings"].value and components["settings"].value["enabled"]
                ),
            )
            view = await host.ask(
                "applicationSupport",
                {"facts": facts},
                timeout=2,
            )

            return {
                **view,
                "desktopGuidance": support.desktop_guidance(),
                "installedApplication": support.installed_application_view(facts),
            }

        async def support_action(
            identity, action, directory=None, *, startup_id=None, desktop_id=None
        ):
            if identity == "desktop-text" and action in (
                "ListApplications",
                "ListExclusionApplications",
                "ResolveApplicationExclusion",
            ):
                from registered_applications import list_applications, resolve_exclusion

                if action == "ResolveApplicationExclusion":
                    return await asyncio.to_thread(resolve_exclusion, desktop_id)
                return await asyncio.to_thread(
                    list_applications, for_exclusion=action == "ListExclusionApplications"
                )
            if identity == "desktop-text" and action in ("Connect", "Remove"):
                await practice.end(practice.pid)
            if identity in BROWSERS and action == "Remove":
                components["input"].suspend_browser(identity)
            result = await support.execute(
                identity, action, directory, startup_id=startup_id, desktop_id=desktop_id
            )
            if identity in BROWSERS and action == "Remove" and result.get("ok"):
                # Include any granted peer that connected while the installer
                # was removing the registration. Completed receipts still settle.
                components["input"].suspend_browser(identity)
            if identity in BROWSERS and action in ("Connect", "Repair") and result.get("ok"):
                components["input"].suspend_browser(identity, False)
            components["input"].check_browser_registrations()
            return await support_view()

        components["settings"] = Settings(
            host,
            task,
            stopping.set,
            install_requested,
            pause_requested,
            lambda text: tutorial_preview(host, text),
            support_view,
            support_action,
            support.component_folder,
            package_requested,
            preparation_refresh,
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
            if operation == "activateModel":
                return await activate_model(host, value)
            if operation in ("wake", "direct"):
                return await components["input"].trigger(operation, value)
            if operation == "settings":
                components["settings"].show()
                return True
            if operation == "tutorial":
                components["settings"].open_tutorial()
                return True
            if operation == "practiceStatus":
                lesson = value.get("lesson", "onDemand") if isinstance(value, dict) else "onDemand"
                if lesson not in ("onDemand", "automatic"):
                    return None
                progress = await practice.progress(caller_pid, lesson=lesson)
                return {
                    "modelReady": status.get("modelReady", False),
                    "managedPractice": support.owns_practice(caller_pid),
                    "presentation": await host.ask(
                        "automaticPractice" if lesson == "automatic" else "onDemandPractice",
                        progress["runId"] if progress else None,
                        timeout=2,
                    ),
                    "progress": progress,
                    "ending": practice.ending,
                    "endUnconfirmed": practice.end_unconfirmed,
                    "finishPending": bool(
                        practice.finish_pending
                        and practice.finish_pending["pid"] == caller_pid
                        and practice.finish_pending["lesson"] == lesson
                        and support.owns_practice(caller_pid)
                    ),
                    "practiceEnabled": bool(
                        components["settings"].value and components["settings"].value["enabled"]
                    ),
                }
            if operation in ("startOnDemandPractice", "startAutomaticPractice"):
                if (
                    not isinstance(value, dict)
                    or set(value) != {"confirmedSwitch"}
                    or type(value["confirmedSwitch"]) is not bool
                ):
                    return False
                return await practice.begin(
                    caller_pid,
                    value["confirmedSwitch"],
                    lesson="automatic" if operation == "startAutomaticPractice" else "onDemand",
                )
            if operation == "endOnDemandPractice":
                return await practice.end(caller_pid) if value is None else False
            if operation == "finishOnboarding":
                if (
                    not isinstance(value, dict)
                    or set(value) not in ({"outcome"}, {"outcome", "lesson"})
                    or value.get("lesson", "onDemand") not in ("onDemand", "automatic")
                    or value["outcome"] not in ("completed", "skipped")
                    or components["settings"].value is None
                ):
                    return False
                if value["outcome"] == "completed":
                    saved = await practice.complete(
                        caller_pid, lesson=value.get("lesson", "onDemand")
                    )
                else:
                    saved = await host.ask("dismissOnboarding") is True
                if saved:
                    components["settings"].tutorial.dismiss()
                return saved
            if operation == "quit":
                GLib.timeout_add(50, lambda: (stopping.set(), False)[1])
                return True
            if operation == "status":
                remaining, hint_remaining = components["input"].acceptance_timing()
                return {
                    **status,
                    "trayAvailable": components["tray"].indicator is not None,
                    "trayConnected": components["tray"].connected,
                    "nativePeers": sum(bool(peer.lifetime) for peer in components["input"].peers),
                    "overlayVisible": components["overlay"].visible,
                    "editing": components["overlay"].editing,
                    "acceptanceReady": components["overlay"].acceptance_ready
                    and hint_remaining > 0,
                    "acceptanceRemainingMs": remaining,
                    "acceptanceHintRemainingMs": hint_remaining,
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
                deadline = None
                activation_peer = False
                editor_peer = False
                accepted_at = time.monotonic()

                async def peer_request(operation, value):
                    nonlocal deadline, activation_peer, editor_peer
                    if operation == "translateEditorSnapshot":
                        if editor_peer or activation_peer:
                            return None
                        editor_peer = True
                        remaining = 215 - (time.monotonic() - accepted_at)
                        if remaining <= 0:
                            return None
                        if deadline is not None:
                            GLib.source_remove(deadline)
                        deadline = GLib.timeout_add(
                            int(remaining * 1000), lambda: (peer.close(), False)[1]
                        )
                        return await translate_editor_snapshot(host, value, remaining)
                    if operation == "activateModel":
                        if activation_peer or editor_peer or not valid_activation(value):
                            return "unchanged"
                        activation_peer = True
                        if deadline is not None:
                            GLib.source_remove(deadline)
                        deadline = GLib.timeout_add(
                            (ACTIVATION_TIMEOUT + 5) * 1000, lambda: (peer.close(), False)[1]
                        )
                    return await command(operation, value, pid)

                def peer_closed():
                    if deadline is not None:
                        GLib.source_remove(deadline)
                    peers.discard(peer)

                peer = Link(
                    GLib,
                    connection.fileno(),
                    connection.fileno(),
                    lambda *_: None,
                    peer_request,
                    peer_closed,
                    connection,
                )
                peers.add(peer)
                deadline = GLib.timeout_add(2000, lambda: (peer.close(), False)[1])
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
        return editor_host(paths)
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
        return install_models(paths)
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
    except KeyboardInterrupt:
        print("TypeLay: 操作已停止，请重新打开设置检查组件状态。", file=sys.stderr)
        raise SystemExit(75) from None
    except (OSError, EOFError, ValueError, RuntimeError, ImportError, subprocess.SubprocessError):
        print(
            "TypeLay: 无法启动。请确认依赖已安装，并在 niri Wayland 会话运行 typelay --doctor。",
            file=sys.stderr,
        )
        raise SystemExit(1) from None
