"""Owned, detached Linux cooperation processes. Closing TypeLay never closes user apps."""

import argparse
import asyncio
import ctypes
import fcntl
import hashlib
import json
import os
import re
import shutil
import signal
import stat
import subprocess
import sys
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from desktop_component import DesktopComponent, directory, read_small  # noqa: E402


def boot_identity():
    return Path("/proc/sys/kernel/random/boot_id").read_text().strip()


def session_identity():
    # Scope input-method ownership to this session bus, while removal protects
    # leases from every login/session belonging to the user.
    value = (
        os.environ.get("DBUS_SESSION_BUS_ADDRESS", "")
        + "\n"
        + os.environ.get("XDG_RUNTIME_DIR", "")
    )
    return hashlib.sha256(value.encode()).hexdigest()


def process_identity(pid):
    """A PID alone is never an ownership claim; do not read command lines or environments."""
    try:
        process = Path("/proc") / str(pid)
        if process.stat().st_uid != os.getuid():
            return None
        fields = (process / "stat").read_text().rsplit(")", 1)[1].split()
        if fields[0] == "Z":
            return None
        return {"pid": pid, "startTime": int(fields[19])}
    except (OSError, ValueError, IndexError):
        return None


def managed_root(data):
    return Path(data) / "components/desktop-text"


def use_lock(data, *, exclusive=False):
    with directory(managed_root(data)) as parent:
        info = os.fstat(parent)
        if info.st_uid != os.getuid() or info.st_mode & 0o077:
            raise OSError("cooperation-directory-unsafe")
        fd = os.open("use.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600, dir_fd=parent)
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or info.st_nlink != 1:
            raise OSError("cooperation-lock-unsafe")
        fcntl.flock(fd, (fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH) | fcntl.LOCK_NB)
        return fd
    except BaseException:
        os.close(fd)
        raise


def action_lock(data):
    with directory(managed_root(data)) as root:
        fd = os.open("start.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600, dir_fd=root)
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or info.st_nlink != 1:
            raise OSError("cooperation-start-lock-invalid")
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        return fd
    except BaseException:
        os.close(fd)
        raise


def children(pid):
    # /proc/task/*/children is optional in Linux kernels. stat's parent TGID
    # also covers fork from any thread, without reading argv or environments.
    result = set()
    for process in Path("/proc").iterdir():
        if not process.name.isdigit():
            continue
        try:
            fields = (process / "stat").read_text().rsplit(")", 1)[1].split()
        except FileNotFoundError:
            continue  # The process exited during this read-only snapshot.
        if int(fields[1]) == pid:
            result.add(int(process.name))
    return result


def receipts(data):
    """Read durable launch evidence; an absent guardian does not mean absent descendants."""
    root = managed_root(data) / "sessions"
    try:
        with directory(root) as fd:
            names = os.listdir(fd)
    except FileNotFoundError:
        return []
    if len(names) > 1024:
        raise OSError("cooperation-session-records-invalid")
    result = []
    for name in names:
        if name.startswith(".pending-"):
            continue
        if re.fullmatch(r"[0-9a-f]{32}\.json", name) is None:
            raise OSError("cooperation-session-records-invalid")
        try:
            value = json.loads(read_small(root / name, 16384))
        except FileNotFoundError:
            continue  # A guardian removed its receipt after reaping every child.
        if (
            not isinstance(value, dict)
            or value.get("schemaVersion") != 1
            or not isinstance(value.get("bootId"), str)
            or re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", value["bootId"]) is None
            or not isinstance(value.get("session"), str)
            or re.fullmatch(r"[0-9a-f]{64}", value["session"]) is None
            or value.get("kind") not in ("daemon", "application")
            or not isinstance(value.get("bundle"), str)
            or not isinstance(value.get("package"), str)
            or value.get("state") not in ("starting", "running")
        ):
            raise OSError("cooperation-session-records-invalid")
        for key in ("guardian", "child"):
            identity = value.get(key)
            if key == "child" and identity is None and value["state"] == "starting":
                continue
            if (
                not isinstance(identity, dict)
                or type(identity.get("pid")) is not int
                or identity["pid"] <= 1
                or type(identity.get("startTime")) is not int
                or identity["startTime"] < 0
            ):
                raise OSError("cooperation-session-records-invalid")
        value["name"] = name
        result.append(value)
    return result


def removal_lock(data):
    """Hold until removal finishes. Only a changed kernel boot proves old orphans are gone."""
    fd = use_lock(data, exclusive=True)
    try:
        current_boot = boot_identity()
        for receipt in receipts(data):
            if receipt["bootId"] == current_boot:
                raise OSError("cooperation-session-active-or-orphaned")
        with directory(managed_root(data) / "sessions") as sessions:
            for receipt in receipts(data):
                os.unlink(receipt["name"], dir_fd=sessions)
        return fd
    except FileNotFoundError:
        return fd
    except BaseException:
        os.close(fd)
        raise


def write_receipt(root, name, value):
    temporary = ".pending-" + uuid.uuid4().hex
    with directory(root, create=True) as parent:
        fd = os.open(
            temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=parent
        )
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                json.dump(value, stream)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, name, src_dir_fd=parent, dst_dir_fd=parent)
            os.fsync(parent)
        finally:
            try:
                os.unlink(temporary, dir_fd=parent)
            except FileNotFoundError:
                pass


def guard(data, name, kind, bundle, package, lock, command):
    """Own only the new process family; retain its lease until every descendant exits."""
    if re.fullmatch(r"[0-9a-f]{32}\.json", name) is None or not command:
        raise ValueError("cooperation-launch-invalid")
    # Unlike the worker supervisor, this guardian has no parent-death signal and
    # never signals its descendants. A detached application's work must survive UI exit.
    libc = ctypes.CDLL(None, use_errno=True)
    if libc.prctl(36, 1, 0, 0, 0) != 0:  # PR_SET_CHILD_SUBREAPER
        raise OSError(ctypes.get_errno(), "cooperation-subreaper-unavailable")
    signal.signal(signal.SIGHUP, signal.SIG_IGN)
    root = managed_root(data) / "sessions"
    value = {
        "schemaVersion": 1,
        "bootId": boot_identity(),
        "session": session_identity(),
        "guardian": process_identity(os.getpid()),
        "child": None,
        "kind": kind,
        "bundle": str(bundle),
        "package": str(package),
        "state": "starting",
    }
    child = None
    finished = False
    gate_read = gate_write = None
    try:
        gate_read, gate_write = os.pipe()
        write_receipt(root, name, value)
        child = os.fork()
        if child == 0:
            os.close(gate_write)
            # The parent publishes the PID + start time before any executable
            # may start or fork. If the guardian dies first, no process starts.
            if os.read(gate_read, 1) != b"1":
                os._exit(125)
            os.close(gate_read)
            os.setsid()
            os.set_inheritable(lock, True)
            signal.signal(signal.SIGHUP, signal.SIG_DFL)
            try:
                os.execvpe(command[0], command, os.environ)
            except (OSError, ValueError):
                os._exit(127)
        os.close(gate_read)
        gate_read = None
        value["child"] = process_identity(child)
        if value["child"] is None:
            raise OSError("cooperation-child-identity-unavailable")
        value["state"] = "running"
        write_receipt(root, name, value)
        os.write(gate_write, b"1")
        os.close(gate_write)
        gate_write = None
        result = 125
        while True:
            try:
                reaped, status = os.waitpid(-1, 0)
            except ChildProcessError:
                finished = True
                break
            if reaped == child:
                result = os.waitstatus_to_exitcode(status)
        return result
    finally:
        for fd in (gate_read, gate_write):
            if fd is not None:
                os.close(fd)
        # Never erase evidence after an unexpected interruption. It protects
        # potentially reparented descendants even if every inherited fd closes.
        if child is None or finished:
            with directory(root) as parent:
                try:
                    os.unlink(name, dir_fd=parent)
                except FileNotFoundError:
                    pass
                os.fsync(parent)
        os.close(lock)


def bus_owner(service):
    def call(method):
        return subprocess.check_output(
            [
                "gdbus",
                "call",
                "--session",
                "--dest",
                "org.freedesktop.DBus",
                "--object-path",
                "/org/freedesktop/DBus",
                "--method",
                "org.freedesktop.DBus." + method,
                service,
            ],
            text=True,
            stderr=subprocess.DEVNULL,
            timeout=5,
        ).strip()

    state = call("NameHasOwner")
    if state == "(false,)":
        return None
    if state != "(true,)":
        raise OSError("cooperation-session-bus-unavailable")
    match = re.fullmatch(r"\(uint32 (\d+),\)", call("GetConnectionUnixProcessID"))
    if match is None:
        raise OSError("cooperation-session-bus-unavailable")
    return int(match[1])


def input_process_present(name):
    result = subprocess.run(
        ["pgrep", "-u", str(os.getuid()), "-x", name],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        timeout=5,
    )
    if result.returncode not in (0, 1):
        raise OSError("cooperation-process-check-unavailable")
    return result.returncode == 0


def daemon_environment(data, environment):
    """Use a private practice profile only for the newly requested Fcitx daemon."""
    env = dict(environment)
    private = managed_root(data) / "practice-profile" / session_identity()
    for variable, name in (
        ("HOME", "home"),
        ("XDG_CONFIG_HOME", "config"),
        ("XDG_DATA_HOME", "data"),
        ("XDG_CACHE_HOME", "cache"),
    ):
        target = private / name
        with directory(target, create=True) as fd:
            if os.fstat(fd).st_uid != os.getuid() or os.fstat(fd).st_mode & 0o077:
                raise OSError("cooperation-profile-unsafe")
        env[variable] = str(target)
    # The shipped Fcitx StandardPath gives these overrides precedence over XDG.
    # The existing launcher supplies its audited private + official system paths.
    for variable in (
        "FCITX_CONFIG_HOME",
        "FCITX_DATA_HOME",
        "FCITX_CONFIG_DIRS",
        "FCITX_DATA_DIRS",
        "FCITX_ADDON_DIRS",
        "XDG_CONFIG_DIRS",
        "XDG_DATA_DIRS",
        "SKIP_FCITX_PATH",
        "SKIP_FCITX_USER_PATH",
    ):
        env.pop(variable, None)
    profile = Path(env["XDG_CONFIG_HOME"]) / "fcitx5"
    with directory(profile, create=True) as fd:
        try:
            output = os.open(
                "profile", os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=fd
            )
        except FileExistsError:
            info = os.stat("profile", dir_fd=fd, follow_symlinks=False)
            if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid():
                raise OSError("cooperation-profile-unsafe")
        else:
            with os.fdopen(output, "w", encoding="utf-8") as stream:
                stream.write(
                    "[Groups/0]\nName=Default\nDefault Layout=us\nDefaultIM=pinyin\n"
                    "[Groups/0/Items/0]\nName=keyboard-us\nLayout=\n"
                    "[Groups/0/Items/1]\nName=pinyin\nLayout=\n[GroupOrder]\n0=Default\n"
                )
    return env


class DesktopSession:
    """Async explicit actions and observations for the current Linux cooperation session."""

    def __init__(self, paths):
        self.paths = paths
        self.component = DesktopComponent(paths)
        self.guardians = []

    def _status(self):
        try:
            self.guardians = [process for process in self.guardians if process.poll() is None]
            current = [
                item
                for item in receipts(self.paths.data)
                if item["bootId"] == boot_identity() and item["session"] == session_identity()
            ]
            for item in current:
                if process_identity(item["guardian"]["pid"]) != item["guardian"]:
                    return {
                        "state": "abnormal",
                        "reason": "session-owner-lost",
                        "detail": "会话监督进程异常退出。相关应用不会被强制关闭；请重启系统后再移除组件。",
                    }
            owner = bus_owner("org.fcitx.Fcitx5")
            for item in current:
                child = item.get("child")
                if item["kind"] == "daemon" and child is not None:
                    if process_identity(child["pid"]) == child:
                        executable = Path(f"/proc/{child['pid']}/exe")
                        expected = Path(item["bundle"]) / "core/bin/fcitx5"
                        if owner == child["pid"] and executable.resolve(
                            strict=True
                        ) == expected.resolve(strict=True):
                            try:
                                can_stop = (
                                    not any(row["kind"] == "application" for row in current)
                                    and children(item["guardian"]["pid"]) == {child["pid"]}
                                    and not children(child["pid"])
                                )
                            except (OSError, ValueError, IndexError):
                                can_stop = False
                            return {
                                "state": "active",
                                "bundle": item["bundle"],
                                "daemon": child,
                                "canStop": can_stop,
                            }
                        if owner is None:
                            return {"state": "starting", "reason": "private-session-starting"}
            if owner is not None or input_process_present("fcitx5"):
                return {
                    "state": "conflict",
                    "reason": "existing-fcitx-session",
                    "detail": "已有 Fcitx 正在运行。请完成候选并自行退出原输入法后再启用；TypeLay 不会停止或切换它。",
                }
            if bus_owner("org.freedesktop.IBus") is not None or input_process_present(
                "ibus-daemon"
            ):
                return {
                    "state": "conflict",
                    "reason": "existing-ibus-session",
                    "detail": "已有 IBus 正在运行。请完成候选并自行退出原输入法后再启用；TypeLay 不会停止或切换它。",
                }
            return {"state": "applications-running" if current else "inactive"}
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
            return {
                "state": "unknown",
                "reason": "session-state-unconfirmed",
                "detail": "无法确认当前输入法会话，未启动或切换输入法。请检查会话后重试。",
            }

    async def refresh(self):
        return await asyncio.to_thread(self._status)

    def _spawn(self, kind, bundle, command, lock, *, startup_id=None):
        name = uuid.uuid4().hex + ".json"
        environment = dict(os.environ)
        environment.pop("TYPELAY_DESKTOP", None)
        # A fresh explicit request belongs to this application only, never a
        # daemon or a later launch inheriting the desktop's startup environment.
        environment.pop("DESKTOP_STARTUP_ID", None)
        environment.pop("XDG_ACTIVATION_TOKEN", None)
        if kind == "daemon":
            environment = daemon_environment(self.paths.data, environment)
        elif startup_id:
            environment["DESKTOP_STARTUP_ID"] = startup_id
        process = subprocess.Popen(
            [
                "/usr/bin/python3",
                "-I",
                "-B",
                str(Path(__file__).resolve()),
                "--guardian",
                str(self.paths.data),
                name,
                kind,
                str(bundle),
                str(self.paths.home),
                str(lock),
                "--",
                *command,
            ],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            pass_fds=(lock,),
            start_new_session=True,
            env=environment,
        )
        self.guardians.append(process)
        return process, name

    def _start(self):
        lock = None
        start = None
        try:
            lock = use_lock(self.paths.data)
            start = action_lock(self.paths.data)
            status = self._status()
            if status["state"] == "active":
                return {"ok": True, **status}
            if status["state"] not in ("inactive", "applications-running"):
                return {"ok": False, **status}
            bundle = self.component.verify_prepared()
            if any(
                item["bootId"] == boot_identity() and item["bundle"] != str(bundle)
                for item in receipts(self.paths.data)
            ):
                return {
                    "ok": False,
                    "reason": "previous-component-in-use",
                    "detail": "请先正常关闭仍使用上一版桌面组件的应用，再启用新版。",
                }
            command = [
                "/usr/bin/python3",
                "-I",
                "-B",
                str(bundle / "fcitx-cooperative.py"),
                "daemon",
            ]
            process, _ = self._spawn("daemon", bundle, command, lock)
            deadline = time.monotonic() + 180
            while process.poll() is None:
                status = self._status()
                if status["state"] == "active":
                    return {"ok": True, **status}
                if status["state"] in ("conflict", "abnormal"):
                    return {"ok": False, **status}
                if time.monotonic() >= deadline:
                    return {
                        "ok": False,
                        "state": "starting",
                        "reason": "private-session-start-pending",
                    }
                time.sleep(0.1)
            return {
                "ok": False,
                "reason": "private-session-start-failed",
                "detail": "私有输入法未能启动。没有停止原输入法；请查看组件依赖与版本要求。",
            }
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
            return {"ok": False, "reason": "private-session-start-failed"}
        finally:
            for fd in (start, lock):
                if fd is not None:
                    os.close(fd)

    async def start_session(self):
        return await asyncio.to_thread(self._start)

    def _launch(self, argv, toolkit, startup_id):
        lock = None
        start = None
        try:
            if (
                toolkit not in ("gtk3", "gtk4", "qt6")
                or not isinstance(argv, (list, tuple))
                or not argv
                or not argv[0]
                or any(not isinstance(arg, str) or "\x00" in arg for arg in argv)
            ):
                raise ValueError("cooperation-application-invalid")
            lock = use_lock(self.paths.data)
            start = action_lock(self.paths.data)
            status = self._status()
            if status["state"] != "active":
                return {"ok": False, **status}
            bundle = self.component.verify_prepared()
            if str(bundle) != status["bundle"]:
                return {"ok": False, "reason": "previous-component-in-use"}
            application = [str(self.paths.launcher), "launch", *argv] if toolkit == "gtk3" else argv
            command = [
                "/usr/bin/python3",
                "-I",
                "-B",
                str(bundle / "fcitx-cooperative.py"),
                "launch",
                *([] if toolkit == "gtk3" else ["--" + toolkit]),
                *application,
            ]
            process, name = self._spawn("application", bundle, command, lock, startup_id=startup_id)
            guardian = process_identity(process.pid)
            if guardian is None:
                return {"ok": False, "reason": "application-start-unconfirmed"}
            deadline = time.monotonic() + 5
            while process.poll() is None:
                launched = next(
                    (
                        item
                        for item in receipts(self.paths.data)
                        if item["name"] == name and item["state"] == "running"
                    ),
                    None,
                )
                if launched is not None:
                    application = {"receipt": launched, "guardian": guardian}
                    if not self.owns_application(application, launched["child"]["pid"]):
                        return {"ok": False, "reason": "application-start-unconfirmed"}
                    return {
                        "ok": True,
                        "reason": "application-start-requested",
                        "application": application,
                    }
                if time.monotonic() >= deadline:
                    break
                time.sleep(0.05)
            return {"ok": False, "reason": "application-start-unconfirmed"}
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
            return {"ok": False, "reason": "application-start-failed"}
        finally:
            for fd in (start, lock):
                if fd is not None:
                    os.close(fd)

    async def launch_application(self, argv, toolkit="gtk3", *, startup_id=None):
        return await asyncio.to_thread(self._launch, argv, toolkit, startup_id)

    def owns_application(self, application, pid):
        """Receipt plus this launch's actual Popen identity; neither proves itself."""
        if not isinstance(application, dict) or type(pid) is not int:
            return False
        try:
            launched, guardian = application["receipt"], application["guardian"]
            child = launched["child"]
            parent = int(
                (Path("/proc") / str(pid) / "stat").read_text().rsplit(")", 1)[1].split()[1]
            )
            return (
                launched["kind"] == "application"
                and launched["bootId"] == boot_identity()
                and launched["session"] == session_identity()
                and launched["package"] == str(self.paths.home)
                and child["pid"] == pid
                and process_identity(pid) == child
                and launched["guardian"] == guardian
                and process_identity(guardian["pid"]) == guardian
                and parent == guardian["pid"]
                and launched in receipts(self.paths.data)
                and process_identity(pid) == child
            )
        except (OSError, ValueError, KeyError, TypeError, IndexError, RuntimeError):
            return False

    def _stop(self):
        lock = start = handle = None
        try:
            lock = use_lock(self.paths.data)
            start = action_lock(self.paths.data)
            status = self._status()
            if status["state"] != "active":
                return {"ok": False, **status}
            identity = status["daemon"]
            handle = os.pidfd_open(identity["pid"])
            # Opening a pidfd does not retrospectively validate a recycled PID.
            # Recheck its receipt and bus ownership after acquiring the handle.
            confirmed = self._status()
            if (
                confirmed.get("state") != "active"
                or confirmed.get("daemon") != identity
                or process_identity(identity["pid"]) != identity
            ):
                return {"ok": False, "reason": "private-session-owner-changed"}
            current = [
                item
                for item in receipts(self.paths.data)
                if item["bootId"] == boot_identity() and item["session"] == session_identity()
            ]
            daemon = next(
                (
                    item
                    for item in current
                    if item["kind"] == "daemon" and item.get("child") == identity
                ),
                None,
            )
            if (
                daemon is None
                or any(item["kind"] == "application" for item in current)
                or children(daemon["guardian"]["pid"]) != {identity["pid"]}
                or children(identity["pid"])
            ):
                return {
                    "ok": False,
                    "reason": "cooperative-applications-running",
                    "detail": "请先正常关闭通过本会话打开的练习窗口、应用及其子窗口，再结束输入法会话。",
                }
            if (
                process_identity(identity["pid"]) != identity
                or bus_owner("org.fcitx.Fcitx5") != identity["pid"]
            ):
                return {"ok": False, "reason": "private-session-owner-changed"}
            signal.pidfd_send_signal(handle, signal.SIGTERM)
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline:
                remaining = [
                    item for item in receipts(self.paths.data) if item["name"] == daemon["name"]
                ]
                if not remaining and process_identity(identity["pid"]) != identity:
                    return {
                        "ok": True,
                        "reason": "private-session-stopped",
                        "detail": "本次私有输入法会话已结束。原输入法未被自动启动，请按原方式自行恢复。",
                    }
                time.sleep(0.05)
            return {
                "ok": False,
                "reason": "private-session-stop-pending",
                "detail": "已请求本次私有输入法退出，仍有自有进程未结束；未强制关闭应用，暂不能移除组件。",
            }
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
            return {"ok": False, "reason": "private-session-stop-unconfirmed"}
        finally:
            for fd in (handle, start, lock):
                if fd is not None:
                    os.close(fd)

    async def stop_session(self):
        return await asyncio.to_thread(self._stop)

    def _remove_files(self):
        install = lease = None
        try:
            with directory(self.paths.data / "components") as components:
                install = os.open(
                    "install.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600, dir_fd=components
                )
            info = os.fstat(install)
            if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid():
                raise OSError("component-install-lock-invalid")
            fcntl.flock(install, fcntl.LOCK_EX | fcntl.LOCK_NB)
            lease = removal_lock(self.paths.data)
            with directory(managed_root(self.paths.data)) as root:
                try:
                    current = os.readlink("current", dir_fd=root)
                    if re.fullmatch(r"releases/[0-9a-f]{64}", current) is None:
                        raise OSError("managed-component-pointer-invalid")
                except FileNotFoundError:
                    current = None
                try:
                    info = os.stat("releases", dir_fd=root, follow_symlinks=False)
                    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid():
                        raise OSError("managed-component-releases-invalid")
                except FileNotFoundError:
                    pass
                else:
                    shutil.rmtree("releases", dir_fd=root)
                if current is not None:
                    os.unlink("current", dir_fd=root)
            return {
                "ok": True,
                "reason": "cooperative-component-removed",
                "detail": "受管桌面组件已移除；模型、TypeLay设置与私有练习配置保留。原输入法没有被自动启动。",
            }
        except (OSError, ValueError, RuntimeError):
            return {
                "ok": False,
                "reason": "cooperative-component-in-use",
                "detail": "组件仍在使用中或其会话状态无法确认，未完成移除。请正常关闭相关应用并检查会话。",
            }
        finally:
            for fd in (lease, install):
                if fd is not None:
                    os.close(fd)

    async def remove_component(self):
        state = await self.refresh()
        if state["state"] == "active":
            stopped = await self.stop_session()
            if not stopped["ok"]:
                return stopped
        elif state["state"] == "abnormal":
            return {"ok": False, **state}
        return await asyncio.to_thread(self._remove_files)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--guardian", action="store_true", required=True)
    parser.add_argument("data", type=Path)
    parser.add_argument("name")
    parser.add_argument("kind", choices=("daemon", "application"))
    parser.add_argument("bundle", type=Path)
    parser.add_argument("package", type=Path)
    parser.add_argument("lock", type=int)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    arguments = parser.parse_args()
    command = arguments.command[1:] if arguments.command[:1] == ["--"] else arguments.command
    try:
        raise SystemExit(
            guard(
                arguments.data,
                arguments.name,
                arguments.kind,
                arguments.bundle,
                arguments.package,
                arguments.lock,
                command,
            )
        )
    except (OSError, ValueError, RuntimeError):
        raise SystemExit(125) from None
