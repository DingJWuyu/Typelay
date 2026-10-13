"""Trace the real GTK3/Fcitx client; never grant production write permission."""

from __future__ import annotations

import argparse
import json
import os
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def run(*args: str, **kwargs) -> str:
    return subprocess.check_output(args, text=True, timeout=15, **kwargs).strip()


def wait_for(predicate, description: str) -> None:
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.05)
    raise AssertionError(f"Timed out: {description}")


def session(output: Path) -> None:
    with tempfile.TemporaryDirectory(prefix="typelay-fcitx-") as temporary:
        home = Path(temporary)
        env = os.environ.copy()
        for variable, name in {
            "HOME": "home",
            "XDG_CONFIG_HOME": "config",
            "XDG_DATA_HOME": "data",
            "XDG_CACHE_HOME": "cache",
            "XDG_RUNTIME_DIR": "runtime",
        }.items():
            path = home / name
            path.mkdir(mode=0o700)
            env[variable] = str(path)
        config = home / "config/fcitx5"
        config.mkdir()
        (config / "profile").write_text(
            "[Groups/0]\nName=Default\nDefault Layout=us\nDefaultIM=pinyin\n"
            "[Groups/0/Items/0]\nName=keyboard-us\nLayout=\n"
            "[Groups/0/Items/1]\nName=pinyin\nLayout=\n[GroupOrder]\n0=Default\n"
        )
        env.update(GTK_IM_MODULE="fcitx", GDK_BACKEND="x11")
        versions = {
            "fcitx5": run("fcitx5", "--version", env=env, stderr=subprocess.STDOUT),
            "gtk3": run("pkg-config", "--modversion", "gtk+-3.0"),
            "gclient": run("pkg-config", "--modversion", "Fcitx5GClient"),
        }
        if shutil.which("pacman"):
            versions["chinese_addons"] = run("pacman", "-Q", "fcitx5-chinese-addons")
            versions["gtk_integration_package"] = run("pacman", "-Q", "fcitx5-gtk")
        elif shutil.which("dpkg-query"):
            versions["chinese_addons"] = run("dpkg-query", "-W", "fcitx5-chinese-addons")
        else:
            versions["chinese_addons"] = "package version unavailable"
        (output / "versions.json").write_text(json.dumps(versions, indent=2) + "\n")
        children = []
        with (
            (output / "framework.log").open("w") as framework_log,
            (output / "client.log").open("w") as client_log,
        ):
            try:
                children.append(
                    subprocess.Popen(
                        ["fcitx5", "--disable=wayland"],
                        env=env,
                        stdout=framework_log,
                        stderr=subprocess.STDOUT,
                    )
                )
                fixture_env = env | {
                    "LD_PRELOAD": str(output / "client-trace.so"),
                    "TYPELAY_FCITX_PROBE_SELECT_FIRST": "1",
                    "TYPELAY_FIXTURE_STATE": str(output / "field.json"),
                }
                children.append(
                    subprocess.Popen(
                        [str(output / "gtk-fixture")],
                        env=fixture_env,
                        stdout=client_log,
                        stderr=subprocess.STDOUT,
                    )
                )

                def trace() -> str:
                    return (output / "client.log").read_text()

                wait_for(lambda: "event=connected" in trace(), "Fcitx client connection")
                windows: list[str] = []

                def mapped_fixture() -> bool:
                    search = subprocess.run(
                        ["xdotool", "search", "--onlyvisible", "--pid", str(children[-1].pid)],
                        env=env,
                        text=True,
                        capture_output=True,
                        timeout=5,
                        check=False,
                    )
                    if search.returncode not in (0, 1):
                        raise RuntimeError(search.stderr)
                    windows[:] = search.stdout.splitlines()
                    return bool(windows)

                wait_for(mapped_fixture, "the owned GTK fixture window is mapped")
                names = {
                    window: run("xdotool", "getwindowname", window, env=env) for window in windows
                }
                (output / "windows.json").write_text(json.dumps(names, indent=2) + "\n")
                assert len(windows) == 1, f"Expected one owned fixture window: {names}"
                window = windows[0]
                run("xdotool", "windowfocus", "--sync", window, env=env)
                # Window focus and an arbitrary connected IC do not prove that
                # the focused field's real GClient has reached the daemon.
                # F24 carries no text; wait for both actual key replies before
                # selecting the engine on the daemon's most-recent context.
                handshake_offset = len(trace())
                run("xdotool", "key", "F24", env=env)

                def focused_client_replied() -> bool:
                    completed = [
                        line
                        for line in trace()[handshake_offset:].splitlines()
                        if "event=sync-return " in line or "event=reply-callback-exit " in line
                    ]
                    return len(completed) >= 2 and "pending=0 " in completed[-1]

                wait_for(focused_client_replied, "focused GClient handled the marker key")
                run("fcitx5-remote", "-s", "pinyin", env=env)
                run("fcitx5-remote", "-o", env=env)
                wait_for(
                    lambda: run("fcitx5-remote", "-n", env=env) == "pinyin",
                    "pinyin selected",
                )
                run("xdotool", "type", "n", env=env)
                wait_for(
                    lambda: "event=candidate-select-request" in trace(),
                    "actual candidate selection request",
                )
                wait_for(
                    lambda: "event=commit-string"
                    in trace().split("event=candidate-select-request", 1)[1],
                    "candidate selection produces real commit signal",
                )
                run("xdotool", "key", "Escape", env=env)
                run("xdotool", "type", "1.", env=env)

                def first_endswith(suffix: str) -> bool:
                    try:
                        return json.loads((output / "field.json").read_text())["first"].endswith(
                            suffix
                        )
                    except (FileNotFoundError, json.JSONDecodeError):
                        return False

                wait_for(lambda: first_endswith("1."), "pinyin half-width punctuation after digit")
                punctuation_offset = len(trace())
                run("xdotool", "key", "BackSpace", env=env)
                wait_for(lambda: first_endswith("1。"), "pinyin delayed punctuation replacement")
                punctuation_events = trace()[punctuation_offset:]
                assert "event=commit-string" in punctuation_events
                (output / "punctuation.log").write_text(punctuation_events)
                run("fcitx5-remote", "-s", "keyboard-us", env=env)
                wait_for(
                    lambda: run("fcitx5-remote", "-n", env=env) == "keyboard-us",
                    "original keyboard engine selected",
                )
                run("xdotool", "type", "--delay", "0", "synthetic", env=env)
                run("xdotool", "key", "Tab", env=env)
                run("xdotool", "key", "ctrl+a", env=env)
                run("xdotool", "type", "--delay", "0", "second", env=env)

                def fields_finished() -> bool:
                    try:
                        fields = json.loads((output / "field.json").read_text())
                        return "synthetic" in fields["first"] and fields["second"] == "second"
                    except (FileNotFoundError, json.JSONDecodeError):
                        return False

                wait_for(fields_finished, "unhandled keys reach real GTK fields")
                events = trace()
                assert "event=request-issued" in events
                assert "event=reply-callback-exit" in events
                assert "event=update-formatted-preedit" in events
                print(json.dumps(versions))
                print(
                    "Real GTK/Fcitx request, reply, preedit, candidate commit, delayed punctuation and field replay observed."
                )
                print("No claim of idle safety, physical input, Wayland coverage or writeback.")
            finally:
                for child in reversed(children):
                    child.terminate()
                for child in reversed(children):
                    try:
                        child.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait(timeout=5)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/linux/fcitx-probe")
    parser.add_argument("--session", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    if args.session:
        session(output)
        return
    commands = [
        (
            "client-trace.so",
            ROOT / "tests/linux/fixtures/fcitx_probe.c",
            ["-shared", "-fPIC"],
            ["gio-2.0", "Fcitx5GClient"],
            ["-ldl"],
        ),
        (
            "gtk-fixture",
            ROOT / "tests/linux/fixtures/gtk_fixture.c",
            [],
            ["gtk+-3.0", "json-glib-1.0"],
            [],
        ),
    ]
    for binary, source, options, packages, libraries in commands:
        flags = shlex.split(run("pkg-config", "--cflags", "--libs", *packages))
        run(
            "cc",
            "-Wall",
            "-Wextra",
            "-Werror",
            *options,
            str(source),
            *flags,
            *libraries,
            "-o",
            str(output / binary),
        )
    subprocess.run(
        [
            "dbus-run-session",
            "--",
            "xvfb-run",
            "-a",
            sys.executable,
            __file__,
            "--session",
            "--output",
            str(output),
        ],
        check=True,
        timeout=60,
    )


if __name__ == "__main__":
    main()
