#!/usr/bin/env python3
"""Run one verification command in an owned Xvfb/D-Bus/niri session."""

import argparse
import ctypes
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from pathlib import Path


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


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
    parser.add_argument("--inside", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("provide -- COMMAND [ARGUMENT ...]")
    output = args.output.resolve()
    if args.inside:
        return inside(output, command)
    if not args.package or not args.cooperative:
        parser.error("--package and --cooperative are required")
    for tool in ("Xvfb", "xvfb-run", "xauth", "dbus-run-session", "niri", "grim", "xdotool"):
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
    try:
        runtime_parent = Path(f"/run/user/{os.getuid()}")
        assert runtime_parent.is_dir() and runtime_parent.stat().st_uid == os.getuid()
        runtime = Path(tempfile.mkdtemp(prefix="typelay-verify-", dir=runtime_parent))
        package = (runtime if args.package_filesystem == "tmpfs" else scratch) / "package"
        if args.package_filesystem == "tmpfs":
            fs = subprocess.check_output(["stat", "-f", "-c", "%T", runtime], text=True).strip()
            assert fs == "tmpfs", "diagnostic package path requires executable tmpfs"
        shutil.copytree(args.package.resolve(), package)
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
        )
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
            process = subprocess.Popen(
                [
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
                ],
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
    finally:
        receipt = cleanup()
        receipt.update(runtime=str(runtime), scratch=str(scratch), commandExitCode=code)
        if receipt["remainingPids"]:
            code = 1
        if code == 0:
            shutil.rmtree(scratch)
            if runtime is not None:
                shutil.rmtree(runtime)
        receipt["scratchRemoved"] = not scratch.exists()
        receipt["runtimeRemoved"] = runtime is None or not runtime.exists()
        save(output / "cleanup.json", receipt)
        print(f"Evidence: {output}; exit: {code}", flush=True)
    return code


if __name__ == "__main__":

    def interrupted(signum, frame):
        raise KeyboardInterrupt(f"signal {signum}")

    signal.signal(signal.SIGTERM, interrupted)
    sys.exit(main())
