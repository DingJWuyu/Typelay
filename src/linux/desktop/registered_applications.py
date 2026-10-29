"""Discover registered local applications and resolve a narrow, zero-file Exec subset."""

import os
import re
import shutil
import stat
import subprocess
from pathlib import Path

# Desktop Entry quoting is narrower than shell syntax. Validate it before using
# GLib to undo quotes, then expand field codes once, in the specified order.
# https://specifications.freedesktop.org/desktop-entry/latest/exec-variables.html
_ARGUMENT = re.compile(r'"(?:[^"\\$`\x00-\x1f]|\\["\\$`])*"|[^\s"\'\\><~|&;$*?#()`]+')
_INDIRECT_EXECUTABLE = re.compile(
    r"(?:ba|da|k|z|fi|c|tc)?sh|busybox|env|flatpak|snap|gtk-launch|xdg-open|"
    r"python(?:[0-9.]+)?|perl(?:[0-9.]+)?|ruby(?:[0-9.]+)?|node(?:js)?|java|"
    r"wine(?:64)?|mono"
)
_TOOLKITS = {"libgtk-3.so.0": "gtk3", "libgtk-4.so.1": "gtk4", "libQt6Widgets.so.6": "qt6"}


def _registered(identity, *, for_exclusion=False):
    from gi.repository import Gio, GLib

    if (
        not isinstance(identity, str)
        or len(identity) > 255
        or not identity.endswith(".desktop")
        or any(character in identity for character in ("/", "\\", "\x00"))
        or identity in (".desktop", "..desktop")
    ):
        raise ValueError("请选择已注册的本机应用。")
    try:
        entry = Gio.DesktopAppInfo.new(identity)
    except TypeError as error:  # PyGObject reports a NULL constructor this way.
        raise ValueError("应用已移除或不再可用，请重新选择。") from error
    if entry is None or entry.get_id() != identity or not entry.should_show():
        raise ValueError("应用已移除或不再可用，请重新选择。")
    filename = entry.get_filename()
    path = Path(filename).resolve(strict=True)
    roots = [GLib.get_user_data_dir(), *GLib.get_system_data_dirs()]
    if not any(
        path.is_relative_to(root := (Path(directory) / "applications").resolve())
        and path.relative_to(root).as_posix().replace("/", "-") == identity
        for directory in roots
    ):
        raise ValueError("应用不在标准注册目录中。")
    if not path.is_file() or path.stat().st_size > 1024 * 1024:
        raise ValueError("应用启动项无效。")
    keys = GLib.KeyFile()
    keys.load_from_file(str(path), GLib.KeyFileFlags.NONE)
    group = "Desktop Entry"
    if (
        keys.get_string(group, "Type") != "Application"
        or entry.get_boolean("Hidden")
        or (
            not for_exclusion
            and (
                entry.get_boolean("Terminal")
                or entry.has_key("X-Flatpak")
                or entry.has_key("X-SnapInstanceName")
                or entry.get_string("Path")
            )
        )
    ):
        raise ValueError("此启动方式暂不能通过 TypeLay 打开。")
    return entry, keys.get_string(group, "Exec")


def _arguments(command):
    from gi.repository import GLib

    if (
        not command
        or len(command) > 16384
        or any(ord(character) < 32 or ord(character) == 127 for character in command)
    ):
        raise ValueError("此应用的启动参数暂不支持。")
    tokens, cursor = [], 0
    for match in _ARGUMENT.finditer(command):
        if command[cursor : match.start()].strip(" ") or (tokens and cursor == match.start()):
            raise ValueError("此应用的启动参数暂不支持。")
        tokens.append(match.group())
        cursor = match.end()
    if not tokens or command[cursor:].strip(" "):
        raise ValueError("此应用的启动参数暂不支持。")
    _, parsed = GLib.shell_parse_argv(command)
    arguments, files = [], 0
    for index, (raw, argument) in enumerate(zip(tokens, parsed, strict=True)):
        if argument in ("%f", "%F", "%u", "%U") and not raw.startswith('"'):
            files += 1
            if index == 0 or files > 1:
                raise ValueError("此应用的启动参数暂不支持。")
            continue
        # %% is literal, never reconsider its result as another field code.
        if re.search(r"%(?!%)", argument.replace("%%", "")):
            raise ValueError("此应用的启动参数暂不支持。")
        arguments.append(argument.replace("%%", "%"))
    if not arguments or "=" in arguments[0]:
        raise ValueError("此应用的启动命令暂不支持。")
    return arguments


def _native_executable(executable):
    if not Path(executable).is_absolute():
        if "/" in executable:
            raise ValueError("此应用的启动命令暂不支持。")
        executable = shutil.which(executable)
    if not executable:
        raise ValueError("找不到应用的可执行文件。")
    path = Path(executable).absolute()
    info = path.stat()
    if not stat.S_ISREG(info.st_mode) or not os.access(path, os.X_OK):
        raise ValueError("找不到应用的可执行文件。")
    with path.open("rb") as stream:
        if stream.read(4) != b"\x7fELF":
            raise ValueError("无法确认脚本或包装启动器的应用进程。")
    return path


def _toolkit(path):
    # Only a system inspector reads the candidate. Never run it, use ldd, or
    # infer a toolkit from its name or transitive dependencies.
    result = subprocess.run(
        ["/usr/bin/readelf", "--dynamic", "--wide", str(path)],
        capture_output=True,
        text=True,
        timeout=3,
        check=True,
        env={"PATH": "/usr/bin:/bin", "LC_ALL": "C"},
    )
    dependencies = re.findall(r"\(NEEDED\).*?\[([^\]]+)\]", result.stdout)
    toolkits = {_TOOLKITS[name] for name in dependencies if name in _TOOLKITS}
    if len(toolkits) != 1:
        raise ValueError("尚未确认此应用的本机输入接入方式。")
    return toolkits.pop()


def list_applications(*, for_exclusion=False):
    """Visible registered local entries only; discovery never launches a process."""
    from gi.repository import Gio, GLib

    result = []
    for entry in Gio.AppInfo.get_all():
        if not isinstance(entry, Gio.DesktopAppInfo) or not entry.should_show():
            continue
        try:
            registered, _ = _registered(entry.get_id(), for_exclusion=for_exclusion)
            result.append({"id": registered.get_id(), "name": registered.get_display_name()})
        except (OSError, ValueError, GLib.Error):
            continue
    return sorted(result, key=lambda item: (item["name"].casefold(), item["id"]))


def resolve_application(identity):
    """Re-read a desktop ID; no caller-provided argv or desktop-file path is accepted."""
    from gi.repository import GLib

    try:
        entry, command = _registered(identity)
        arguments = _arguments(command)
        # Preserve a registered executable symlink name: argv[0] can select its mode.
        arguments[0] = str(_native_executable(arguments[0]))
        return {
            "id": identity,
            "name": entry.get_display_name(),
            "argv": arguments,
            "toolkit": _toolkit(arguments[0]),
        }
    except (OSError, subprocess.SubprocessError, GLib.Error) as error:
        raise ValueError("无法检查应用启动项，请重新选择或检查系统组件。") from error


def resolve_exclusion(identity):
    """Resolve a direct registered ELF to the same basename used by /proc/pid/exe."""
    from gi.repository import GLib

    try:
        entry, command = _registered(identity, for_exclusion=True)
        if entry.has_key("X-Flatpak") or entry.has_key("X-SnapInstanceName"):
            raise ValueError("无法确认沙箱应用的真实进程，请在高级编辑中填写可执行文件名。")
        arguments = _arguments(command)
        # Parameters can choose another program (for example a process wrapper).
        # Only zero-file launches identify the ELF itself without interpreting argv.
        if len(arguments) != 1:
            raise ValueError("无法确认带启动参数的应用进程，请在高级编辑中填写可执行文件名。")
        executable = _native_executable(arguments[0]).resolve(strict=True)
        name = executable.name.lower()
        if _INDIRECT_EXECUTABLE.fullmatch(name):
            raise ValueError("无法确认包装启动器的应用进程，请在高级编辑中填写可执行文件名。")
        if not name or any(character in name for character in ("\n", "\r")):
            raise ValueError("应用的可执行文件名无效。")
        return name
    except (OSError, GLib.Error) as error:
        raise ValueError("无法确认应用进程，请在高级编辑中填写可执行文件名。") from error


def list_managed_applications():
    """One visible app catalogue, with process policy only when identity is proven."""
    entries = list_applications(for_exclusion=True)
    for entry in entries:
        try:
            entry["application"] = resolve_exclusion(entry["id"])
        except (OSError, ValueError) as error:
            entry["application"] = ""
            entry["unavailableReason"] = str(error)
    return entries
