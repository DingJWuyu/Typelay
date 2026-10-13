#!/usr/bin/env python3
"""Explicit, per-process launch of the private Fcitx cooperation bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SERVICE = "org.fcitx.Fcitx5"
SOURCE_VERSIONS = {
    "fcitx5": "5.1.22",
    "fcitx5-chinese-addons": "5.1.14",
    "fcitx5-gtk": "5.1.7",
    "libime": "1.1.16",
}
PRIVATE_VERSIONS = {
    **{f"Fcitx5{name}": "5.1.22" for name in ("Core", "Config", "Utils")},
    **{f"LibIME{name}": "1.1.16" for name in ("Core", "Pinyin", "Table")},
}
HELPER_FILES = {
    "py_stroke.mb": "9b526eab3ef2679d63155c8304fc4b11f701c2bf5db7946e7d3e940f54f3aaa9",
    "py_table.mb": "19b3e593e5a211fe548f39c894d9a2e2e07a08e3edee0083087b1471e5ba026b",
}
USER_PACKAGE_VERSIONS = {"fcitx5-chinese-addons": "5.1.14", "libime": "1.1.16"}


def run(arguments, *, env=None):
    return subprocess.check_output(
        arguments, text=True, stderr=subprocess.STDOUT, timeout=15, env=env
    )


def bus(method, *arguments):
    return run(
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
            *arguments,
        ]
    ).strip()


def owner_pid(service=SERVICE):
    state = bus("NameHasOwner", service)
    if state == "(false,)":
        return None
    if state != "(true,)":
        raise RuntimeError("Cannot establish the session-bus Fcitx owner")
    value = bus("GetConnectionUnixProcessID", service)
    match = re.fullmatch(r"\(uint32 (\d+),\)", value)
    if not match:
        raise RuntimeError("Cannot establish the Fcitx process identity")
    return int(match[1])


def paths(first, existing, defaults=()):
    return ":".join(
        dict.fromkeys([*(str(p) for p in first), *(existing.split(":") if existing else defaults)])
    )


def environment(*, daemon=False, gtk4=False, qt6=False):
    env = os.environ.copy()
    env["LD_LIBRARY_PATH"] = paths(
        [ROOT / "lib", *([ROOT / "core/lib"] if daemon else [])], env.get("LD_LIBRARY_PATH")
    )
    if daemon:
        env["TYPELAY_FCITX_PRIVATE_SESSION"] = "1"
        env["TYPELAY_FCITX_AUDITED_ADDON_DIRS"] = f"{ROOT / 'lib'}:{ROOT / 'core/lib/fcitx5'}"
        env["LIBIME_MODEL_DIRS"] = str(ROOT / "core/lib/libime")
        env["XDG_DATA_DIRS"] = paths(
            [ROOT / "core/share"],
            env.get("XDG_DATA_DIRS"),
            ["/usr/local/share", "/usr/share"],
        )
        env["FCITX_ADDON_DIRS"] = paths(
            [ROOT / "lib", ROOT / "core/lib/fcitx5"],
            env.get("FCITX_ADDON_DIRS"),
            ["/usr/lib/fcitx5"],
        )
        env["FCITX_DATA_DIRS"] = paths(
            [ROOT / "core/share/fcitx5"],
            paths([], env.get("FCITX_DATA_DIRS"), ["/usr/local/share/fcitx5", "/usr/share/fcitx5"])
            + ":/usr/share/fcitx5",
            ["/usr/local/share/fcitx5", "/usr/share/fcitx5"],
        )
    elif qt6:
        env["QT_PLUGIN_PATH"] = paths([ROOT / "qt6/plugins"], env.get("QT_PLUGIN_PATH"))
        env["QT_QPA_GENERIC_PLUGINS"] = ",".join(
            dict.fromkeys(
                ["typelay", *filter(None, env.get("QT_QPA_GENERIC_PLUGINS", "").split(","))]
            )
        )
        env["QT_IM_MODULE"] = "fcitx5"
        env["TYPELAY_QT_COOPERATIVE"] = "1"
        env["TYPELAY_FCITX_COOPERATIVE"] = "1"
        env.pop("TYPELAY_QT_PROBE", None)
        env.pop("TYPELAY_QT_DELAY_FIRST_FINISHED", None)
    else:
        env["GTK_IM_MODULE"] = "fcitx"
        env["TYPELAY_FCITX_COOPERATIVE"] = "1"
    if gtk4:
        env["GTK_PATH"] = paths([ROOT / "lib/gtk-4.0"], env.get("GTK_PATH"))
    env.pop("TYPELAY_FCITX_PROBE", None)
    return env


def verify_qt(manifest):
    metadata = manifest.get("qt6")
    if (
        not isinstance(metadata, dict)
        or metadata.get("qtVersion") != "6.11.2"
        or metadata.get("fcitxQtVersion") != "5.1.15"
    ):
        raise RuntimeError("This bundle has no matching optional Qt 6.11.2 component")
    for package, version in (("qt6-base", "6.11.2"),):
        actual = run(["pacman", "-Q", package]).strip().split()
        if len(actual) != 2 or actual[0] != package or actual[1].rsplit("-", 1)[0] != version:
            raise RuntimeError(f"Version-matched user package required: {package} {version}")
    if os.environ.get("FCITX_QT_USE_SYNC", "").lower() not in ("", "0", "false"):
        raise RuntimeError("TypeLay Qt cooperation requires the original default async mode")
    provider = ROOT / "qt6/plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so"
    consumer = ROOT / "qt6/plugins/generic/libtypelay-qt6.so"
    probe = ROOT / "qt6/bin/qt-runtime-probe"
    env = environment(qt6=True)
    for binary in (provider, consumer, probe):
        if not binary.resolve().is_relative_to(ROOT / "qt6"):
            raise RuntimeError("Qt component escaped the private bundle")
    platform = Path("/usr/lib/qt6/plugins/platforms/libqwayland.so")
    for binary in (provider, consumer, probe, platform):
        output = run(["ldd", "-r", str(binary)], env=env)
        if "not found" in output or "undefined symbol:" in output:
            raise RuntimeError(f"Unresolved Qt dependencies: {binary.name}\n{output}")
        for line in output.splitlines():
            if "libQt6" in line and "=>" in line:
                path = Path(line.split("=>", 1)[1].strip().split(" (", 1)[0]).resolve()
                if path.parent != Path("/usr/lib").resolve() or not path.name.endswith(
                    ".so.6.11.2"
                ):
                    raise RuntimeError(f"Mixed or unsupported Qt runtime: {path.name}")
    observation = json.loads(run([str(probe), str(provider), str(consumer)], env=env))
    if observation.get("buildVersion") != "6.11.2" or observation.get("runtimeVersion") != "6.11.2":
        raise RuntimeError("Actual Qt runtime differs from the private plugin build")
    expected = {
        "provider": ("org.qt-project.Qt.QPlatformInputContextFactoryInterface.5.1", "fcitx5"),
        "consumer": ("org.qt-project.Qt.QGenericPluginFactoryInterface", "typelay"),
    }
    for name, (iid, key) in expected.items():
        plugin = observation.get(name, {})
        if (
            plugin.get("IID") != iid
            or plugin.get("version") != (6 << 16 | 11 << 8)
            or key not in plugin.get("MetaData", {}).get("Keys", [])
        ):
            raise RuntimeError(f"Unexpected Qt plugin metadata: {name}")
    return observation


def verify(*, gtk4=False, qt6=False):
    manifest = json.loads((ROOT / "manifest.json").read_text())
    if manifest.get("schemaVersion") != 1:
        raise RuntimeError("Unsupported bundle manifest")
    for relative, expected in manifest["files"].items():
        file = ROOT / relative
        if Path(relative).is_absolute() or ".." in Path(relative).parts:
            raise RuntimeError("Invalid manifest path")
        if "symlink" in expected:
            if not file.is_symlink() or os.readlink(file) != expected["symlink"]:
                raise RuntimeError(f"Bundle link mismatch: {relative}")
            if not file.resolve().is_relative_to(ROOT):
                raise RuntimeError(f"Bundle link escapes its directory: {relative}")
        else:
            if not file.is_file() or file.is_symlink():
                raise RuntimeError(f"Bundle file missing or replaced: {relative}")
            with file.open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            if digest != expected["sha256"]:
                raise RuntimeError(f"Bundle checksum mismatch: {relative}")
    sources = manifest["versions"].get("component_source_versions")
    if sources != SOURCE_VERSIONS:
        raise RuntimeError("Explicit private component source versions are missing or unsupported")
    if manifest["versions"].get("installed_private_versions") != PRIVATE_VERSIONS:
        raise RuntimeError(
            "Private installed Fcitx/LibIME library versions are missing or unsupported"
        )
    for package, expected in USER_PACKAGE_VERSIONS.items():
        actual = run(["pacman", "-Q", package]).strip().split()
        if len(actual) != 2 or actual[0] != package or actual[1].rsplit("-", 1)[0] != expected:
            raise RuntimeError(f"Version-matched user package required: {package} {expected}")
    for name, digest in HELPER_FILES.items():
        path = Path("/usr/share/fcitx5/pinyinhelper") / name
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise RuntimeError(f"Matching official Chinese 5.1.14 helper data required: {name}")
    for key in (
        "server_source_version",
        "server_runtime_version",
        "linked_core_development_version",
    ):
        if manifest["versions"].get(key) != "5.1.22":
            raise RuntimeError("This launcher requires the complete Fcitx 5.1.22 bundle")
    env = environment(daemon=True)
    binaries = [
        ROOT / "core/bin/fcitx5",
        *sorted(p for p in (ROOT / "lib").glob("*.so*") if p.name != "libtypelay-gtk4.so"),
    ]
    if gtk4:
        binaries += [
            ROOT / "lib/libtypelay-gtk4.so",
            ROOT / "lib/gtk-4.0/4.0.0/immodules/libim-fcitx5.so",
        ]
    binaries.extend(sorted((ROOT / "core/lib").glob("libIME*.so.0")))
    binaries.extend(sorted((ROOT / "core/lib/fcitx5").glob("*.so")))
    for binary in binaries:
        output = run(["ldd", "-r", str(binary)], env=env)
        if "not found" in output or "undefined symbol:" in output:
            raise RuntimeError(f"Unresolved runtime dependencies: {binary.name}\n{output}")
        for name in (
            "libFcitx5Core",
            "libFcitx5Config",
            "libFcitx5Utils",
            "libIMECore",
            "libIMEPinyin",
            "libIMETable",
        ):
            for line in output.splitlines():
                if f"{name}.so" in line and "=>" in line:
                    path = Path(line.split("=>", 1)[1].strip().split(" (", 1)[0])
                    if not path.resolve().is_relative_to(ROOT / "core/lib"):
                        raise RuntimeError(
                            f"System Fcitx/LibIME library mixed into private bundle: {name}"
                        )
        if gtk4:
            for name in ("libtypelay-gtk4.so", "libFcitx5GClient.so"):
                for line in output.splitlines():
                    if name in line and "=>" in line:
                        path = Path(line.split("=>", 1)[1].strip().split(" (", 1)[0])
                        if path.resolve().parent != (ROOT / "lib").resolve():
                            raise RuntimeError(
                                f"System client library mixed into GTK4 provider: {name}"
                            )
    if run([str(ROOT / "core/bin/fcitx5"), "--version"], env=env).strip() != "5.1.22":
        raise RuntimeError("Private daemon version mismatch")
    if qt6:
        verify_qt(manifest)
    return manifest


def module_cache(env):
    runtime = os.environ.get("XDG_RUNTIME_DIR")
    if not runtime:
        raise RuntimeError("XDG_RUNTIME_DIR is required for the per-session GTK module cache")
    directory = Path(runtime) / "typelay-fcitx-cooperative"
    directory.mkdir(mode=0o700, exist_ok=True)
    if directory.is_symlink() or directory.stat().st_uid != os.getuid():
        raise RuntimeError("Unsafe runtime cache directory")
    query = shutil.which("gtk-query-immodules-3.0")
    if not query:
        query = "/usr/lib/gtk-3.0/gtk-query-immodules-3.0"
    value = run([query, str(ROOT / "lib/im-fcitx5.so")], env=env)
    entries = [
        shlex.split(line)
        for line in value.splitlines()
        if line.strip() and not line.startswith("#")
    ]
    if not any(row and row[0] == str(ROOT / "lib/im-fcitx5.so") for row in entries) or not any(
        row and row[0] == "fcitx" for row in entries
    ):
        raise RuntimeError("The private GTK IM module did not register its actual fcitx context")
    key = hashlib.sha256(str(ROOT).encode()).hexdigest()[:20]
    destination = directory / f"{key}.cache"
    with tempfile.NamedTemporaryFile(mode="w", dir=directory, delete=False) as temporary:
        temporary.write(value)
        temporary_name = temporary.name
    os.replace(temporary_name, destination)
    return str(destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("preflight", "daemon", "launch"))
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    gtk4 = args.command[:1] == ["--gtk4"]
    qt6 = args.command[:1] == ["--qt6"]
    if gtk4 or qt6:
        args.command.pop(0)
        if args.action == "daemon":
            parser.error("Toolkit options apply to preflight or launch, not daemon")
    if args.action != "launch" and args.command:
        parser.error("Only launch accepts an application command")
    manifest = verify(gtk4=gtk4, qt6=qt6)
    print("Private components:", json.dumps(SOURCE_VERSIONS, sort_keys=True), flush=True)
    print("Private installed libraries:", json.dumps(PRIVATE_VERSIONS, sort_keys=True), flush=True)
    print(
        "Build-machine packages:",
        json.dumps(manifest["systemDependencies"]["packages"], sort_keys=True),
        flush=True,
    )
    print(
        "Installed user packages:\n"
        + run(
            [
                "pacman",
                "-Q",
                "fcitx5",
                "fcitx5-chinese-addons",
                "fcitx5-gtk",
                "libime",
                "opencc",
            ]
        ).strip(),
        flush=True,
    )
    if args.action == "preflight":
        print("Private Fcitx 5.1.22 runtime verified; no session or profile changed")
        return
    pid = owner_pid()
    if args.action == "daemon":
        if pid is not None:
            raise RuntimeError(
                "An Fcitx daemon already owns this session bus. Finish composition and explicitly "
                "exit it before switching. This launcher never stops or replaces another daemon."
            )
        # The original IBus frontend may replace an IBus daemon. Never start it
        # over another active implementation in a user's session.
        ibus_process = subprocess.run(
            ["pgrep", "-u", str(os.getuid()), "-x", "ibus-daemon"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            timeout=5,
        )
        if owner_pid("org.freedesktop.IBus") is not None or ibus_process.returncode != 1:
            raise RuntimeError(
                "An IBus daemon is active or its absence cannot be verified; no switch was made"
            )
        print("Starting private Fcitx on the existing session bus and existing profile", flush=True)
        os.execve(
            ROOT / "core/bin/fcitx5",
            [str(ROOT / "core/bin/fcitx5"), "-D", "--disable=ibusfrontend"],
            environment(daemon=True),
        )
    if not args.command:
        parser.error("launch requires an application command")
    if pid is None or Path(f"/proc/{pid}/exe").resolve() != (ROOT / "core/bin/fcitx5").resolve():
        raise RuntimeError(
            "The active Fcitx owner is not this private bundle; start its daemon first"
        )
    env = environment(gtk4=gtk4, qt6=qt6)
    if not gtk4 and not qt6:
        env["GTK_IM_MODULE_FILE"] = module_cache(env)
    os.execvpe(args.command[0], args.command, env)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"Fcitx cooperation: {error}", file=sys.stderr)
        raise SystemExit(1) from error
