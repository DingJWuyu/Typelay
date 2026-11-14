#!/usr/bin/env python3
"""Run one verification command in an owned Xvfb/D-Bus/niri session."""

import argparse
import ctypes
import fcntl
import json
import os
import shutil
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def copy_cache(source, destination):
    """Copy download bytes, preserving uv's relative links without sharing writes."""
    source = source.resolve(strict=True)
    if not source.is_dir():
        raise ValueError(f"download cache is not a directory: {source}")
    destination.mkdir(parents=True, exist_ok=True)
    subprocess.run(["cp", "-a", "--reflink=auto", str(source) + "/.", str(destination)], check=True)


def network_child(output, command):
    parent = os.environ["TYPELAY_VERIFY_PARENT_NETNS"]
    current = os.readlink("/proc/self/ns/net")
    interfaces = sorted(name for _, name in socket.if_nameindex())
    if current == parent or interfaces != ["lo"]:
        raise RuntimeError("offline verification requires a new network namespace with only lo")
    # Browser journeys serve local fixtures. Only this namespace's loopback is enabled.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as control:
        fcntl.ioctl(control, 0x8914, struct.pack("16sH22x", b"lo", 1))  # SIOCSIFFLAGS, IFF_UP
    # Only setup needs CAP_NET_ADMIN in the new user namespace. Do not carry
    # ambient capabilities into the desktop or its browser sandbox.
    if ctypes.CDLL(None, use_errno=True).prctl(47, 4, 0, 0, 0) != 0:
        raise OSError(ctypes.get_errno(), "PR_CAP_AMBIENT_CLEAR_ALL")
    save(
        output / "network.json",
        {
            "mode": "offline",
            "parentNamespace": parent,
            "namespace": current,
            "interfaces": interfaces,
            "netDev": Path("/proc/net/dev").read_text(),
        },
    )
    os.execvpe(command[0], command, os.environ)


def wlan_received():
    for line in Path("/proc/net/dev").read_text().splitlines():
        name, separator, counters = line.partition(":")
        if separator and name.strip() == "wlan0":
            return int(counters.split()[0])
    return None


def remove_scratch(scratch, runtime, *, keep, remaining):
    if not keep and not remaining:
        for path in (scratch, runtime):
            if path is not None and path.exists():
                shutil.rmtree(path)


def descendants():
    parents = {}
    for path in Path("/proc").glob("[0-9]*/stat"):
        try:
            fields = path.read_text().rsplit(")", 1)[1].split()
            parents[int(path.parent.name)] = int(fields[1])
        except (OSError, ValueError, IndexError):
            continue
    owned = {os.getpid()}
    while children := {pid for pid, parent in parents.items() if parent in owned} - owned:
        owned.update(children)
    return owned - {os.getpid()}


def reap():
    while True:
        try:
            if os.waitpid(-1, os.WNOHANG)[0] == 0:
                return
        except ChildProcessError:
            return


def cleanup():
    stopped = set()
    for sig in (signal.SIGTERM, signal.SIGKILL):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            reap()
            owned = descendants()
            if not owned:
                return {"stoppedPids": sorted(stopped), "remainingPids": []}
            for pid in owned:
                try:
                    descriptor = os.pidfd_open(pid)
                    try:
                        # Pin identity before rechecking ancestry; PID reuse must not
                        # turn cleanup into a signal to an unrelated desktop process.
                        if pid in descendants():
                            signal.pidfd_send_signal(descriptor, sig)
                            stopped.add(pid)
                    finally:
                        os.close(descriptor)
                except ProcessLookupError:
                    pass
            time.sleep(0.1)
    reap()
    return {"stoppedPids": sorted(stopped), "remainingPids": sorted(descendants())}


def inside(output, command):
    scratch = output / "scratch"
    assert os.environ.get("TYPELAY_VERIFY_OUTPUT") == str(output)
    for key, name in (
        ("HOME", "home"),
        ("XDG_CONFIG_HOME", "config"),
        ("XDG_DATA_HOME", "data"),
        ("XDG_CACHE_HOME", "cache"),
    ):
        assert Path(os.environ[key]) == scratch / name, key
    runtime = Path(os.environ["XDG_RUNTIME_DIR"])
    assert Path(os.environ["NIRI_SOCKET"]).is_relative_to(runtime)
    assert (runtime / os.environ["WAYLAND_DISPLAY"]).is_socket()
    assert os.environ["DBUS_SESSION_BUS_ADDRESS"]
    # niri may remove DISPLAY for its Wayland clients. XTest targets only our Xvfb.
    assert os.environ["TYPELAY_VERIFY_XDISPLAY"].startswith(":")
    os.environ["TYPELAY_TEST_DISPLAY"] = os.environ["TYPELAY_VERIFY_XDISPLAY"]
    keys = (
        "HOME",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_CACHE_HOME",
        "XDG_RUNTIME_DIR",
        "NIRI_SOCKET",
        "WAYLAND_DISPLAY",
        "DBUS_SESSION_BUS_ADDRESS",
        "TYPELAY_VERIFY_XDISPLAY",
        "TYPELAY_VERIFY_PACKAGE",
        "TYPELAY_VERIFY_COOPERATIVE",
        "TYPELAY_VERIFY_INSTALL_CACHE",
        "TYPELAY_VERIFY_MODEL_CACHE",
        "TYPELAY_VERIFY_SCRATCH",
    )
    save(output / "session.json", {key: os.environ.get(key) for key in keys})
    with (output / "journey.log").open("w") as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
    save(output / "exit.json", {"exitCode": result.returncode})
    return result.returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--package", type=Path)
    parser.add_argument("--cooperative", type=Path)
    parser.add_argument(
        "--package-filesystem",
        choices=("normal", "tmpfs"),
        default="normal",
        help="copy the package beside evidence; tmpfs is an explicit diagnostic comparison",
    )
    parser.add_argument("--timeout", type=int, default=1200)
    parser.add_argument(
        "--install-cache", type=Path, help="download cache containing uv/ and optional model caches"
    )
    parser.add_argument("--model-cache", type=Path, help="Index model download cache")
    parser.add_argument(
        "--allow-downloads", action="store_true", help="explicitly allow network for download tests"
    )
    parser.add_argument(
        "--keep-scratch", action="store_true", help="retain installation copies for diagnosis"
    )
    parser.add_argument("--network-child", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--inside", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("provide -- COMMAND [ARGUMENT ...]")
    output = args.output.resolve()
    if args.network_child:
        return network_child(output, command)
    if args.inside:
        return inside(output, command)
    if not args.package or not args.cooperative:
        parser.error("--package and --cooperative are required")
    source_package = args.package.resolve()
    if output.is_relative_to(source_package):
        parser.error("output must be outside the source package")
    for tool in (
        "Xvfb",
        "xvfb-run",
        "xauth",
        "dbus-run-session",
        "niri",
        "grim",
        "xdotool",
        "cp",
        "unshare",
    ):
        if not shutil.which(tool):
            parser.error(f"missing tool: {tool}; read references/build.md")
    # Adopt orphaned grandchildren so cleanup also owns D-Bus-activated services.
    if ctypes.CDLL(None, use_errno=True).prctl(36, 1, 0, 0, 0) != 0:
        raise OSError(ctypes.get_errno(), "PR_SET_CHILD_SUBREAPER")
    output.mkdir(parents=True, exist_ok=False)
    os.chmod(output, 0o700)
    save(output / "invocation.json", {"argv": sys.argv, "cwd": os.getcwd()})
    scratch = output / "scratch"
    runtime = None
    code = 1
    received_before = wlan_received()
    started = time.time()
    try:
        runtime_parent = Path(f"/run/user/{os.getuid()}")
        assert runtime_parent.is_dir() and runtime_parent.stat().st_uid == os.getuid()
        runtime = Path(tempfile.mkdtemp(prefix="typelay-verify-", dir=runtime_parent))
        package = (runtime if args.package_filesystem == "tmpfs" else scratch) / "package"
        if args.package_filesystem == "tmpfs":
            fs = subprocess.check_output(["stat", "-f", "-c", "%T", runtime], text=True).strip()
            assert fs == "tmpfs", "diagnostic package path requires executable tmpfs"
        shutil.copytree(source_package, package)
        # Keep tool paths, but inherit no desktop, input method, model or Python state.
        env = {key: os.environ[key] for key in ("PATH", "LD_LIBRARY_PATH") if key in os.environ}
        for key, name in (
            ("HOME", "home"),
            ("XDG_CONFIG_HOME", "config"),
            ("XDG_DATA_HOME", "data"),
            ("XDG_CACHE_HOME", "cache"),
        ):
            path = scratch / name
            path.mkdir(parents=True, mode=0o700)
            env[key] = str(path)
        env.update(
            XDG_RUNTIME_DIR=str(runtime),
            TMPDIR=str(runtime),
            XDG_CONFIG_DIRS="/etc/xdg",
            XDG_DATA_DIRS="/usr/local/share:/usr/share",
            FCITX_CONFIG_HOME=str(scratch / "config/fcitx5"),
            FCITX_DATA_HOME=str(scratch / "data/fcitx5"),
            GDK_BACKEND="wayland",
            ATSPI_DBUS_IMPLEMENTATION="dbus-daemon",
            LIBGL_ALWAYS_SOFTWARE="1",
            LANG="en_US.UTF-8",
            LANGUAGE="zh_CN",
            PYTHONDONTWRITEBYTECODE="1",
            TYPELAY_VERIFY_OUTPUT=str(output),
            TYPELAY_VERIFY_PACKAGE=str(package),
            TYPELAY_VERIFY_COOPERATIVE=str(args.cooperative.resolve()),
            TYPELAY_VERIFY_SCRATCH=str(scratch),
            TYPELAY_VERIFY_PARENT_NETNS=os.readlink("/proc/self/ns/net"),
        )
        # Resolve the shared checkout before replacing HOME. Worktrees share caches,
        # but each journey receives a writable copy and an empty installation path.
        common = Path(
            subprocess.check_output(
                ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], text=True
            ).strip()
        ).parent
        install_source = args.install_cache or common / "artifacts/install-cache"
        model_source = args.model_cache or common / "artifacts/index-model-download"
        components = scratch / "cache/typelay/components"
        catalog = json.loads((package / "worker/model-catalog.json").read_text())
        model = next(p for p in catalog["packages"] if p["id"] == "index-translate-2b-q4-k-m")
        model_cache = components / model["id"] / model["version"]
        save(
            output / "cache-sources.json",
            {
                "install": str(install_source.resolve()),
                "model": str(model_source.resolve()),
                "destination": str(components),
                "allowDownloads": args.allow_downloads,
            },
        )
        for source, destination in ((install_source, components), (model_source, model_cache)):
            if source.is_dir():
                copy_cache(source, destination)
            elif not args.allow_downloads:
                raise FileNotFoundError(f"offline cache missing: {source}; downloads are disabled")
            else:
                destination.mkdir(parents=True, exist_ok=True)
        env.update(
            TYPELAY_VERIFY_INSTALL_CACHE=str(components),
            TYPELAY_VERIFY_MODEL_CACHE=str(model_cache),
        )
        if not args.allow_downloads:
            env["UV_OFFLINE"] = "1"
        # uv does not retain CPython archives in its wheel cache. Preserve the
        # original release layout under python/ and let pinned uv verify/install it.
        if (components / "python").is_dir():
            env["UV_PYTHON_INSTALL_MIRROR"] = (components / "python").as_uri()
        child = [
            sys.executable,
            str(Path(__file__).resolve()),
            "--inside",
            "--output",
            str(output),
            "--",
            *command,
        ]
        config = output / "niri.kdl"
        config.write_text(
            "hotkey-overlay { skip-at-startup; }\nanimations { off; }\n"
            'input { keyboard { xkb { layout "us"; }; }; }\n'
            "layout { default-column-width { proportion 0.8; }; }\n"
            + "spawn-at-startup "
            + " ".join(json.dumps(arg, ensure_ascii=False) for arg in child)
            + "\n"
        )
        save(
            output / "package-identity.json", json.loads((package / "build-info.json").read_text())
        )
        with (output / "niri.log").open("w") as log:
            desktop = [
                "dbus-run-session",
                "--",
                "xvfb-run",
                "-a",
                "-s",
                "-screen 0 1280x960x24 -nolisten tcp",
                "sh",
                "-c",
                'export TYPELAY_VERIFY_XDISPLAY="$DISPLAY"; exec niri --config "$1"',
                "sh",
                str(config),
            ]
            if args.allow_downloads:
                save(output / "network.json", {"mode": "allow-downloads", "explicitOptIn": True})
            else:
                desktop = [
                    "unshare",
                    "--user",
                    "--map-current-user",
                    "--keep-caps",
                    "--net",
                    sys.executable,
                    str(Path(__file__).resolve()),
                    "--network-child",
                    "--output",
                    str(output),
                    "--",
                    *desktop,
                ]
            process = subprocess.Popen(
                desktop,
                env=env,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            deadline = time.monotonic() + args.timeout
            while not (output / "exit.json").exists():
                if process.poll() is not None:
                    raise RuntimeError("private session exited; inspect niri.log")
                if time.monotonic() >= deadline:
                    raise TimeoutError("private session deadline; inspect journey.log")
                time.sleep(0.2)
            code = json.loads((output / "exit.json").read_text())["exitCode"]
            if code and not args.allow_downloads:
                print(
                    "Offline journey failed. Inspect journey.log and model-install.log; missing caches cannot download.",
                    file=sys.stderr,
                )
    finally:
        receipt = cleanup()
        receipt.update(runtime=str(runtime), scratch=str(scratch), commandExitCode=code)
        if receipt["remainingPids"]:
            code = 1
        remove_scratch(scratch, runtime, keep=args.keep_scratch, remaining=receipt["remainingPids"])
        receipt["keepScratch"] = args.keep_scratch
        receipt["scratchRemoved"] = not scratch.exists()
        receipt["runtimeRemoved"] = runtime is None or not runtime.exists()
        save(output / "cleanup.json", receipt)
        received_after = wlan_received()
        save(
            output / "wlan0.json",
            {
                "startedAt": started,
                "finishedAt": time.time(),
                "receiveBytesBefore": received_before,
                "receiveBytesAfter": received_after,
                "receiveBytesDelta": None
                if received_before is None or received_after is None
                else received_after - received_before,
                "scope": "host interface; includes unrelated traffic",
            },
        )
        print(f"Evidence: {output}; exit: {code}", flush=True)
    return code


if __name__ == "__main__":

    def interrupted(signum, frame):
        raise KeyboardInterrupt(f"signal {signum}")

    signal.signal(signal.SIGTERM, interrupted)
    sys.exit(main())
