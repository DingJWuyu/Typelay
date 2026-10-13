"""Read-only Fcitx launch-source observations, never permission to switch it.

No command, PID, unit name, environment or filesystem path escapes this module.
Even an observed dedicated service is not a restoration plan: stop hooks,
relationships, login environment and fresh identity need separate verification
and explicit user approval before any future action.
"""

import asyncio
import os
import re
import stat
import threading
import time
from pathlib import Path


class _Unconfirmed(Exception):
    def __init__(self, reason):
        self.reason = reason


def _small(path, limit=16384):
    with path.open("rb") as stream:
        value = stream.read(limit + 1)
    if len(value) > limit:
        raise _Unconfirmed("unit-unconfirmed")
    return value.decode("utf-8", errors="strict")


def _process(pid, uid):
    if type(pid) is not int or pid <= 1 or uid != os.getuid():
        raise _Unconfirmed("owner-unconfirmed")
    process = Path("/proc") / str(pid)
    if process.stat().st_uid != uid:
        raise _Unconfirmed("owner-unconfirmed")
    fields = _small(process / "stat").rsplit(")", 1)[1].split()
    if fields[0] in ("Z", "X"):
        raise _Unconfirmed("owner-unconfirmed")
    executable = process / "exe"
    info = executable.stat()
    target = os.readlink(executable)
    cgroups = _small(process / "cgroup").splitlines()
    unified = [line[3:] for line in cgroups if line.startswith("0::")]
    if len(unified) != 1:
        raise _Unconfirmed("unit-unconfirmed")
    return {
        "pid": pid,
        "uid": uid,
        "start": int(fields[19]),
        "executable": (info.st_dev, info.st_ino),
        "name": Path(target).name,
        "cgroup": unified[0],
    }


def _systemd_executable(identity):
    # A same-UID process can claim the well-known name and call itself systemd.
    # Only the installed, root-controlled manager binary establishes this source.
    for candidate in ("/usr/lib/systemd/systemd", "/lib/systemd/systemd"):
        try:
            executable = Path(candidate).resolve(strict=True)
            info = executable.stat()
            if (
                not stat.S_ISREG(info.st_mode)
                or (info.st_dev, info.st_ino) != identity["executable"]
            ):
                continue
            for path in (executable, *executable.parents):
                metadata = path.stat()
                if metadata.st_uid != 0 or metadata.st_mode & 0o022:
                    break
            else:
                return True
        except OSError:
            continue
    return False


def _single_process(cgroup, pid, unit, deadline):
    parts = cgroup.split("/")
    if (
        not cgroup.startswith("/")
        or any(part in (".", "..") for part in parts)
        or parts[-1] != unit
        or f"user@{os.getuid()}.service" not in parts
    ):
        raise _Unconfirmed("unit-unconfirmed")
    root = Path("/sys/fs/cgroup")
    path = root
    for part in parts[1:]:
        if not part:
            raise _Unconfirmed("unit-unconfirmed")
        path = path / part
        if not stat.S_ISDIR(path.lstat().st_mode):
            raise _Unconfirmed("unit-unconfirmed")
    pending = [path]
    seen = set()
    count = 0
    while pending:
        if time.monotonic() >= deadline:
            raise _Unconfirmed("unit-unconfirmed")
        current = pending.pop()
        count += 1
        if count > 128:
            raise _Unconfirmed("unit-unconfirmed")
        for value in _small(current / "cgroup.procs").split():
            member = int(value)
            if member != pid:
                raise _Unconfirmed("shared-process-group")
            seen.add(member)
        with os.scandir(current) as entries:
            for entry in entries:
                if entry.is_dir(follow_symlinks=False):
                    pending.append(Path(entry.path))
                    if len(pending) > 128:
                        raise _Unconfirmed("unit-unconfirmed")
    if seen != {pid}:
        raise _Unconfirmed("unit-unconfirmed")


def _inspect():
    unavailable = {"source": "unconfirmed", "reason": "session-bus-unavailable"}
    address = os.environ.get("DBUS_SESSION_BUS_ADDRESS", "")
    # An absent address must not spawn dbus-launch; reject exec/TCP transports.
    if (
        not address
        or len(address) > 4096
        or any(not part.startswith("unix:") for part in address.split(";"))
    ):
        return unavailable
    try:
        from gi.repository import Gio, GLib
    except ImportError:
        return unavailable
    connection = None
    cancellable = Gio.Cancellable()
    deadline = time.monotonic() + 3
    timer = threading.Timer(3, cancellable.cancel)
    timer.daemon = True
    timer.start()
    reason = "session-bus-unavailable"

    def call(destination, path, interface, method, signature, arguments, output):
        remaining = int((deadline - time.monotonic()) * 1000)
        if remaining <= 0:
            raise _Unconfirmed(reason)
        reply = connection.call_sync(
            destination,
            path,
            interface,
            method,
            GLib.Variant(signature, arguments),
            GLib.VariantType.new(output),
            Gio.DBusCallFlags.NO_AUTO_START,
            remaining,
            cancellable,
        )
        return reply.unpack()[0]

    def bus(method, name, output):
        return call(
            "org.freedesktop.DBus",
            "/org/freedesktop/DBus",
            "org.freedesktop.DBus",
            method,
            "(s)",
            (name,),
            output,
        )

    def owner(name):
        try:
            value = bus("GetNameOwner", name, "(s)")
        except GLib.Error as error:
            if Gio.DBusError.get_remote_error(error) == "org.freedesktop.DBus.Error.NameHasNoOwner":
                return None
            raise
        if not re.fullmatch(r":[0-9]+\.[0-9]+", value):
            raise _Unconfirmed(reason)
        return value

    def identity(unique):
        # Never resolve credentials against a well-known name that can change.
        uid = bus("GetConnectionUnixUser", unique, "(u)")
        pid = bus("GetConnectionUnixProcessID", unique, "(u)")
        return _process(pid, uid)

    try:
        connection = Gio.DBusConnection.new_for_address_sync(
            address,
            Gio.DBusConnectionFlags.AUTHENTICATION_CLIENT
            | Gio.DBusConnectionFlags.MESSAGE_BUS_CONNECTION,
            None,
            cancellable,
        )
        connection.set_exit_on_close(False)
        unique = owner("org.fcitx.Fcitx5")
        if unique is None:
            return {"source": "absent", "reason": "no-owner"}
        reason = "owner-unconfirmed"
        original = identity(unique)
        if original["name"] != "fcitx5":
            raise _Unconfirmed(reason)
        reason = "user-manager-unavailable"
        manager = owner("org.freedesktop.systemd1")
        if manager is None:
            raise _Unconfirmed(reason)
        manager_identity = identity(manager)
        if not _systemd_executable(manager_identity):
            raise _Unconfirmed(reason)
        reason = "unit-unconfirmed"
        unit_path = call(
            manager,
            "/org/freedesktop/systemd1",
            "org.freedesktop.systemd1.Manager",
            "GetUnitByPID",
            "(u)",
            (original["pid"],),
            "(o)",
        )

        def property_value(interface, name):
            return call(
                manager,
                unit_path,
                "org.freedesktop.DBus.Properties",
                "Get",
                "(ss)",
                ("org.freedesktop.systemd1." + interface, name),
                "(v)",
            )

        unit = property_value("Unit", "Id")
        if (
            not isinstance(unit, str)
            or re.fullmatch(r"[A-Za-z0-9_.:@\\-]+\.service", unit) is None
            or unit == "niri.service"
            or property_value("Unit", "LoadState") != "loaded"
            or property_value("Unit", "ActiveState") != "active"
            or property_value("Unit", "Transient") is not False
            or property_value("Service", "Type") not in ("simple", "exec", "dbus", "notify")
            or property_value("Service", "MainPID") != original["pid"]
            or property_value("Service", "ExecMainPID") != original["pid"]
            or property_value("Service", "ControlPID") != 0
        ):
            raise _Unconfirmed(reason)
        commands = property_value("Service", "ExecStart")
        # The recorded launch must be this actual process, not a wrapper, fork,
        # guessed service name or merely a process sharing a desktop's cgroup.
        if len(commands) != 1 or len(commands[0]) != 10:
            raise _Unconfirmed(reason)
        command = commands[0]
        if command[7] != original["pid"] or command[6] != 0:
            raise _Unconfirmed(reason)
        executable = Path(command[0])
        if not executable.is_absolute():
            raise _Unconfirmed(reason)
        info = executable.stat()
        if (info.st_dev, info.st_ino) != original["executable"]:
            raise _Unconfirmed(reason)
        # Require an actual recorded launch. Proc startTime is independently
        # bound by identity rereads; never compare its clock with systemd's.
        if not command[4]:
            raise _Unconfirmed(reason)
        cgroup = property_value("Service", "ControlGroup")
        if cgroup != original["cgroup"]:
            raise _Unconfirmed(reason)
        _single_process(cgroup, original["pid"], unit, deadline)
        if (
            owner("org.fcitx.Fcitx5") != unique
            or owner("org.freedesktop.systemd1") != manager
            or identity(unique) != original
            or identity(manager) != manager_identity
            or property_value("Service", "MainPID") != original["pid"]
            or property_value("Service", "ControlGroup") != cgroup
            or property_value("Service", "ExecStart") != commands
        ):
            raise _Unconfirmed("owner-changed")
        _single_process(cgroup, original["pid"], unit, deadline)
        if owner("org.fcitx.Fcitx5") != unique or identity(unique) != original:
            raise _Unconfirmed("owner-changed")
        return {"source": "user-service", "reason": "dedicated-user-service"}
    except _Unconfirmed as error:
        return {"source": "unconfirmed", "reason": error.reason}
    except (GLib.Error, OSError, ValueError, TypeError, IndexError, OverflowError):
        return {"source": "unconfirmed", "reason": reason}
    finally:
        if connection is not None:
            try:
                connection.close_sync(cancellable)
            except GLib.Error:
                pass
        timer.cancel()


async def detect_fcitx_source():
    """Return fixed observation categories; never a service action or permit."""
    return await asyncio.to_thread(_inspect)
