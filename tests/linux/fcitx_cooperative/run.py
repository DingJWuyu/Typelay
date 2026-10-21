"""Build through the production recipe, then test real Fcitx/GTK cooperation.

Run: python tests/linux/fcitx_cooperative/run.py
Or: python tests/linux/fcitx_cooperative/run.py --core-version 5.1.22
Builds all official core frontends and private Core/Config/Utils,
requires CMake/Ninja/ECM/gettext and official Wayland/Plasma protocols, and writes
a separate fcitx-cooperative-5.1.22 output directory. It is not an installation.
Requires GTK3/GTK4 development files, json-glib, boost, zstd, GCC, pkgconf, patch,
glib-genmarshal, dbus-run-session, Xvfb, niri and xdotool. LibIME and its data
are built from fixed official sources; system LibIME is not used.
The separately packaged opt-in components do not change a user installation.
Downloaded sources, their licenses, modifications and binaries remain together
under artifacts/linux/fcitx-cooperative-5.1.22. Do not distribute binaries without the
corresponding modified sources and LGPL notices. User configuration is untouched.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import shlex
import signal
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(ROOT))
from eng.linux.cooperative import fcitx  # noqa: E402
from eng.linux.cooperative.inputs import (  # noqa: E402
    CORE_VERSION,
    FCITX_TEST_INPUTS,
    hashes,
    sha256,
)

ABI = ROOT / "src/linux/native/fcitx5/typelay-fcitx-v1.h"


def run(args, **kwargs):
    try:
        return subprocess.check_output(
            [str(a) for a in args], text=True, timeout=kwargs.pop("timeout", 180), **kwargs
        ).strip()
    except subprocess.CalledProcessError as error:
        if error.output:
            print(error.output, file=sys.stderr)
        raise


def flags(*packages):
    return shlex.split(run(["pkg-config", "--cflags", "--libs", *packages]))


def build(out: Path, core_version: str):
    inputs = hashes(FCITX_TEST_INPUTS)
    (out / "result.json").unlink(missing_ok=True)
    fcitx.build(out, core_version)
    run(
        [
            "cc",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-g",
            "-I" + str(ABI.parent),
            HERE / "fixture.c",
            "-o",
            out / "fixture",
            *flags("gtk+-3.0", "gmodule-2.0"),
        ]
    )
    run(
        [
            "cc",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-g",
            ROOT / "tests/linux/fixtures/gtk4_fixture.c",
            "-o",
            out / "gtk4-fixture",
            *flags("gtk4", "json-glib-1.0"),
        ]
    )
    if hashes(FCITX_TEST_INPUTS) != inputs:
        raise RuntimeError("Test inputs changed during fixture compilation")
    (out / "test-inputs.json").write_text(
        json.dumps(
            {
                "files": inputs,
                "fixture_sha256": sha256(out / "fixture"),
                "gtk4_fixture_sha256": sha256(out / "gtk4-fixture"),
                "build_inputs_sha256": sha256(out / "build-inputs.json"),
                "build_metadata_sha256": sha256(out / "versions.json"),
            },
            indent=2,
        )
        + "\n"
    )


def wait(predicate, label):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.025)
    raise AssertionError(f"Timed out: {label}")


def fixed_compose_mapping(out: Path):
    """Keep an owned Xvfb keymap stable until both actual clients have exited."""
    if os.environ.get("TYPELAY_COOPERATIVE_OWNED_XVFB") != "1":
        raise RuntimeError("Fixed compose mapping requires this runner's private Xvfb")
    x11 = ctypes.CDLL("libX11.so.6")
    display_t = ctypes.c_void_p
    keysym_t = ctypes.c_ulong
    x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
    x11.XOpenDisplay.restype = display_t
    x11.XCloseDisplay.argtypes = [display_t]
    x11.XDisplayKeycodes.argtypes = [
        display_t,
        ctypes.POINTER(ctypes.c_int),
        ctypes.POINTER(ctypes.c_int),
    ]
    x11.XGetKeyboardMapping.argtypes = [
        display_t,
        ctypes.c_ubyte,
        ctypes.c_int,
        ctypes.POINTER(ctypes.c_int),
    ]
    x11.XGetKeyboardMapping.restype = ctypes.POINTER(keysym_t)
    x11.XChangeKeyboardMapping.argtypes = [
        display_t,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.POINTER(keysym_t),
        ctypes.c_int,
    ]
    x11.XKeysymToKeycode.argtypes = [display_t, keysym_t]
    x11.XKeysymToKeycode.restype = ctypes.c_ubyte
    x11.XSync.argtypes = [display_t, ctypes.c_int]
    x11.XFree.argtypes = [ctypes.c_void_p]
    display = x11.XOpenDisplay(os.environ["DISPLAY"].encode())
    if not display:
        raise RuntimeError("Cannot open owned Xvfb for compose mapping")
    try:
        dead_acute = 0xFE51
        keycode = x11.XKeysymToKeycode(display, dead_acute)
        changed = not keycode
        if changed:
            low, high, width = ctypes.c_int(), ctypes.c_int(), ctypes.c_int()
            x11.XDisplayKeycodes(display, ctypes.byref(low), ctypes.byref(high))
            count = high.value - low.value + 1
            mapping = x11.XGetKeyboardMapping(display, low.value, count, ctypes.byref(width))
            if not mapping or width.value <= 0:
                raise RuntimeError("Cannot read owned Xvfb keymap")
            try:
                keycode = next(
                    (
                        low.value + row
                        for row in range(count)
                        if all(mapping[row * width.value + col] == 0 for col in range(width.value))
                    ),
                    0,
                )
            finally:
                x11.XFree(mapping)
            if not keycode:
                raise RuntimeError("No unused keycode for the compose fixture")
            symbols = (keysym_t * 1)(dead_acute)
            x11.XChangeKeyboardMapping(display, keycode, 1, symbols, 1)
            x11.XSync(display, 0)
        width = ctypes.c_int()
        mapping = x11.XGetKeyboardMapping(display, keycode, 1, ctypes.byref(width))
        if not mapping:
            raise RuntimeError("Cannot read back compose mapping")
        try:
            verified = any(mapping[col] == dead_acute for col in range(width.value))
        finally:
            x11.XFree(mapping)
        evidence = {
            "keycode": keycode,
            "mapping_created": changed,
            "dead_acute_readback": verified,
            "keysym_lookup_matches": x11.XKeysymToKeycode(display, dead_acute) == keycode,
        }
        (out / "compose-keymap.json").write_text(json.dumps(evidence, indent=2) + "\n")
        if not verified or not evidence["keysym_lookup_matches"]:
            raise RuntimeError("Compose mapping readback mismatch")
        # Retain the connection: Xvfb may reset its map when the last client closes.
        return x11, display
    except BaseException:
        x11.XCloseDisplay(display)
        raise


def session(out: Path, core_version: str, deadline: float):
    prefix = out / "core-prefix"
    for name in ("command", "command.tmp", "state.json", "result.json"):
        (out / name).unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix="typelay-fcitx-cooperative-") as temporary:
        private = Path(temporary)
        env = os.environ.copy()
        for var in (
            "HOME",
            "XDG_CONFIG_HOME",
            "XDG_DATA_HOME",
            "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR",
        ):
            path = private / var
            path.mkdir(mode=448)
            env[var] = str(path)
        config = Path(env["XDG_CONFIG_HOME"]) / "fcitx5"
        config.mkdir()
        (config / "profile").write_text(
            "[Groups/0]\nName=Default\nDefault Layout=us\nDefaultIM=pinyin\n[Groups/0/Items/0]\nName=keyboard-us\nLayout=\n[Groups/0/Items/1]\nName=pinyin\nLayout=\n[GroupOrder]\n0=Default\n"
        )
        (config / "conf").mkdir()
        (config / "conf/imselector.conf").write_text("[TriggerKey]\n0=Control+Alt+I\n")
        env.update(
            LD_LIBRARY_PATH=f"{out / 'lib'}:{prefix / 'lib'}",
            FCITX_ADDON_DIRS=f"{out / 'lib'}:{prefix / 'lib/fcitx5'}",
            TYPELAY_FCITX_AUDITED_ADDON_DIRS=f"{out / 'lib'}:{prefix / 'lib/fcitx5'}",
            FCITX_CONFIG_HOME=str(config),
            FCITX_DATA_HOME=str(Path(env["XDG_DATA_HOME"]) / "fcitx5"),
            FCITX_DATA_DIRS=f"{prefix / 'share/fcitx5'}:/usr/share/fcitx5",
            XDG_DATA_DIRS=f"{prefix / 'share'}:/usr/local/share:/usr/share",
            LIBIME_MODEL_DIRS=str(prefix / "lib/libime"),
            GTK_IM_MODULE="fcitx",
            GTK_IM_MODULE_FILE=str(out / "immodules.cache"),
            GDK_BACKEND="x11",
            TYPELAY_FCITX_PROBE="1",
            TYPELAY_FCITX_COOPERATIVE="1",
            TYPELAY_COOPERATIVE_DIR=str(out),
        )
        env["PKG_CONFIG_PATH"] = f"{prefix}/lib/pkgconfig:{prefix}/share/pkgconfig"
        versions = json.loads((out / "versions.json").read_text())
        assert versions["server_source_version"] == core_version
        assert run([out / "fcitx5", "--version"], env=env, stderr=subprocess.STDOUT) == core_version
        assert run(["pkg-config", "--modversion", "Fcitx5Core"], env=env) == core_version
        children = []
        mapping_connection = None
        with (
            (out / "framework.log").open("w") as framework_log,
            (out / "client.log").open("w") as client_log,
        ):
            try:
                mapping_connection = fixed_compose_mapping(out)
                children.append(
                    subprocess.Popen(
                        [
                            str(out / "fcitx5"),
                            "--disable=all",
                            "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell,quickphrase,clipboard,unicode,imselector,cloudpinyin",
                        ],
                        env=env,
                        stdout=framework_log,
                        stderr=subprocess.STDOUT,
                    )
                )
                children.append(
                    subprocess.Popen(
                        [str(out / "fixture")],
                        env=env | {"TYPELAY_COOPERATIVE_SERVER_PID": str(children[0].pid)},
                        stdout=client_log,
                        stderr=subprocess.STDOUT,
                    )
                )

                def state():
                    try:
                        return json.loads((out / "state.json").read_text())
                    except (FileNotFoundError, json.JSONDecodeError):
                        return {}

                wait(lambda: state().get("ready"), "mapped GTK fixture")
                windows = wait(
                    lambda: subprocess.run(
                        ["xdotool", "search", "--onlyvisible", "--pid", str(children[-1].pid)],
                        env=env,
                        text=True,
                        capture_output=True,
                        timeout=5,
                    ).stdout.split(),
                    "mapped fixture",
                )
                window = windows[0]
                run(["xdotool", "windowfocus", "--sync", window], env=env)
                wait(lambda: state().get("probe_valid"), "actual bound Fcitx IC handshake")
                (out / "engine-before.txt").write_text(run(["fcitx5-remote", "-n"], env=env) + "\n")
                run(["fcitx5-remote", "-o"], env=env)
                run(["fcitx5-remote", "-s", "pinyin"], env=env)
                (out / "engine-after.txt").write_text(run(["fcitx5-remote", "-n"], env=env) + "\n")
                wait(lambda: state().get("known"), "patched engine cooperation")
                mappings = Path(f"/proc/{children[0].pid}/maps").read_text()
                loaded = sorted(
                    {line.split()[-1] for line in mappings.splitlines() if "/" in line.split()[-1]}
                )
                for library in (
                    "libFcitx5Core.so",
                    "libFcitx5Config.so",
                    "libFcitx5Utils.so",
                    "libIMECore.so",
                    "libIMEPinyin.so",
                ):
                    matches = [Path(path).resolve() for path in loaded if library in path]
                    assert matches and all((path.is_relative_to(prefix) for path in matches)), (
                        library,
                        matches,
                    )
                models = [Path(path).resolve() for path in loaded if path.endswith("/zh_CN.lm")]
                assert models and all(
                    path.is_relative_to(prefix / "lib/libime") for path in models
                ), models
                (out / "server-loaded-libraries.json").write_text(
                    json.dumps(loaded, indent=2) + "\n"
                )
                request = 0

                def command(name):
                    nonlocal request
                    request += 1
                    temp = out / "command.tmp"
                    temp.write_text(f"{request} {name}\n")
                    temp.replace(out / "command")
                    return wait(
                        lambda: s if (s := state()).get("request") == request else None, name
                    )

                run(["xdotool", "key", "--window", window, "dead_acute"], env=env)
                wait(lambda: state().get("composing"), "real core XCompose sequence")
                assert command("capture")["result"] == "refused-ime"
                run(["xdotool", "key", "--window", window, "e"], env=env)
                wait(
                    lambda: state().get("settled") and state().get("length", 0) > 0,
                    "core compose commit",
                )
                key_queue_ack = state().get("key_queue_ack", 0)
                run(["xdotool", "type", "--window", window, "--delay", "15", "nihao"], env=env)
                # xdotool completion only means the X server received its events.
                # Wait for a marker from the same actual GTK/Fcitx event queue
                # before candidate selection, without changing the composition.
                run(["xdotool", "key", "--window", window, "F24"], env=env)
                wait(
                    lambda: state().get("key_queue_ack", 0) > key_queue_ack,
                    "actual GTK/Fcitx typed-key queue acknowledgement",
                )
                wait(lambda: state().get("composing"), "real pinyin preedit")
                assert command("capture")["result"] == "refused-ime"
                assert command("candidate")["result"] == "refused-undelivered"
                wait(
                    lambda: state().get("settled") and state().get("length", 0) > 0,
                    "actual candidate commit applied",
                )
                assert command("capture")["result"] == "captured"
                assert command("accept")["result"] == "accepted"
                assert state()["replacement"] is True
                before = command("capture")
                assert before["result"] == "captured"
                run(["xdotool", "key", "--window", window, "Left"], env=env)
                wait(
                    lambda: state().get("epoch", 0) > before["epoch"],
                    "actual navigation input epoch",
                )
                assert state()["unchanged_capture"] is True
                assert command("accept")["result"] == "refused-input"
                before = command("capture")
                assert before["result"] == "captured"
                run(["xdotool", "key", "--window", window, "dead_acute"], env=env)
                wait(lambda: state().get("composing"), "core compose before cancellation")
                run(["xdotool", "key", "--window", window, "Escape"], env=env)
                wait(lambda: state().get("settled"), "core compose cancellation")
                assert state()["unchanged_capture"] is True
                assert command("accept")["result"] == "refused-input"
                assert command("capture")["result"] == "captured"
                assert command("mutate")["result"] == "mutated"
                assert command("accept")["result"] == "refused-stale"
                assert command("capture")["result"] == "captured"
                assert command("focus")["result"] == "focused-other"
                assert command("accept")["result"] == "refused-focus"
                assert command("refocus")["result"] == "focused-original"
                wait(lambda: state().get("settled"), "context after refocus")
                assert command("capture")["result"] == "captured"
                nested = command("accept-nested")
                assert (
                    nested["result"] == "accepted"
                    and nested["nested_refused"]
                    and (nested["nested_depth"] >= 2)
                )
                assert command("capture")["result"] == "captured"
                changed = command("accept-reentrant-mutation")
                assert changed["result"] == "outcome-unknown" and changed["nested_refused"]
                assert command("clear")["result"] == "cleared"
                run(["xdotool", "type", "--window", window, "--delay", "15", "1."], env=env)
                wait(
                    lambda: state().get("settled") and state().get("length") == 2,
                    "numeric punctuation before old transaction",
                )
                assert command("deferred-switch")["result"] == "refused-old-pinyin-transaction"
                wait(lambda: state().get("settled"), "actual keyboard engine cooperation")
                assert run(["fcitx5-remote", "-n"], env=env) == "keyboard-us"
                (out / "engine-keyboard.txt").write_text("keyboard-us\n")
                assert command("clear")["result"] == "cleared"
                run(["xdotool", "type", "--window", window, "--delay", "15", "hello"], env=env)
                wait(
                    lambda: state().get("settled") and state().get("length") == 5,
                    "keyboard ordinary input",
                )
                assert command("capture")["result"] == "captured"
                assert command("accept")["result"] == "accepted"
                before_focus = command("capture")
                assert before_focus["result"] == "captured"
                assert command("focus")["result"] == "focused-other"
                assert command("refocus")["result"] == "focused-original"
                after_focus = wait(
                    lambda: s if (s := state()).get("settled") else None, "keyboard focus return"
                )
                assert after_focus["input_epoch"] == before_focus["input_epoch"]
                assert after_focus["generation"] == before_focus["generation"]
                assert after_focus["epoch"] > before_focus["epoch"]
                assert command("accept")["result"] == "refused-input"
                assert command("capture")["result"] == "captured"
                assert command("accept")["result"] == "accepted"
                # The independent inactive A context is queried while real B input
                # and then B pinyin composition are active. Preparation must not reset B.
                assert command("focus")["result"] == "focused-other"
                wait(lambda: state().get("other_focused"), "actual second field focus")
                run(["xdotool", "type", "--window", window, "--delay", "15", "world"], env=env)
                wait(lambda: state().get("other_length") == 5, "second field ordinary input")
                assert command("detached-proof")["result"] == "detached-clear"
                run(["fcitx5-remote", "-s", "pinyin"], env=env)
                assert run(["fcitx5-remote", "-n"], env=env) == "pinyin"
                run(["xdotool", "type", "--window", window, "--delay", "15", "nihao"], env=env)
                ack = state().get("key_queue_ack", 0)
                run(["xdotool", "key", "--window", window, "F24"], env=env)
                wait(
                    lambda: state().get("key_queue_ack", 0) > ack
                    and state().get("other_preedit", 0) > 0,
                    "second field real preedit delivered",
                )
                assert command("detached-proof")["result"] == "detached-clear"
                assert state()["other_preedit"] > 0
                run(["xdotool", "key", "--window", window, "Escape"], env=env)
                wait(
                    lambda: state().get("other_preedit") == 0, "second field composition cancelled"
                )
                run(["fcitx5-remote", "-s", "keyboard-us"], env=env)
                assert command("refocus")["result"] == "focused-original"
                wait(lambda: state().get("settled"), "original keyboard field returned")
                run(["xdotool", "key", "--window", window, "dead_acute"], env=env)
                wait(
                    lambda: state().get("composing") or state().get("pending"),
                    "keyboard compose pending",
                )
                assert command("capture")["result"] == "refused-ime"
                run(["xdotool", "key", "--window", window, "e"], env=env)
                wait(lambda: state().get("settled"), "keyboard compose commit")
                assert command("capture")["result"] == "captured"
                assert command("accept-server-paused")["result"] == "accepted"
                assert state()["replacement"] is True
                wait(lambda: state().get("settled"), "owned server resumed after local-only write")
                for mode, key in (
                    ("QuickPhrase", "super+grave"),
                    ("Clipboard", "ctrl+semicolon"),
                    ("Unicode", "ctrl+shift+u"),
                    ("IMSelector", "ctrl+alt+i"),
                ):
                    before_mode = state()
                    run(["xdotool", "key", "--window", window, key], env=env)
                    wait(lambda: state().get("composing"), f"real active {mode} mode")
                    assert state()["length"] == before_mode["length"]
                    assert command("capture")["result"] == "refused-ime"
                    run(["xdotool", "key", "--window", window, "Escape"], env=env)
                    wait(lambda: state().get("settled"), f"{mode} mode cancelled")
                    assert command("capture")["result"] == "captured"

                # Reuse this actual daemon and private desktop before the deliberate
                # unknown-origin restarts. No new daemon or per-case time allowance.
                sys.path.insert(0, str(ROOT / "tests/linux"))
                from native_e2e import gtk4_source_glossary

                gtk4_source_glossary(out, env, deadline)
                run(["xdotool", "windowfocus", "--sync", window], env=env)
                wait(lambda: state().get("settled"), "original GTK3 client refocused")

                # Both unknown origins and unknown addon implementations keep
                # ordinary input working while removing TypeLay authority.
                daemon = children[0]
                for label, extras, daemon_env in (
                    (
                        "source",
                        "",
                        env | {"TYPELAY_FCITX_AUDITED_ADDON_DIRS": str(out / "lib")},
                    ),
                    ("addon", ",ibusfrontend", env),
                ):
                    generation = state()["generation"]
                    daemon.terminate()
                    daemon.wait(timeout=5)
                    daemon = subprocess.Popen(
                        [
                            str(out / "fcitx5"),
                            "--disable=all",
                            "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell,quickphrase"
                            + extras,
                        ],
                        env=daemon_env,
                        stdout=framework_log,
                        stderr=subprocess.STDOUT,
                    )
                    children.append(daemon)
                    wait(
                        lambda: state().get("probe_valid")
                        and state().get("generation", 0) > generation,
                        f"same GTK client reconnected for unknown {label}",
                    )
                    run(["fcitx5-remote", "-s", "keyboard-us"], env=env)
                    wait(
                        lambda: state().get("probe_valid") and not state().get("known"),
                        f"actual unreviewed {label} refuses capability",
                    )
                    assert command("capture")["result"] == "refused-ime"
                    assert command("clear")["result"] == "cleared"
                    run(
                        ["xdotool", "type", "--window", window, "--delay", "15", "ordinary"],
                        env=env,
                    )
                    wait(
                        lambda: state().get("length") == 8,
                        f"ordinary input preserved with unknown {label}",
                    )
                assert command("replace-delegate")["result"] == "old-delegate-released"
                (out / "result.json").write_text(
                    json.dumps(
                        {
                            "passed": True,
                            "actual_gtk_compare_write": True,
                            "detached_independent_ic_typing_and_preedit": True,
                            "replaced_delegate_released": True,
                            "keyboard_us_compare_write": True,
                            "previous_engine_pending_refusal": True,
                            "keyboard_compose_refusal": True,
                            "empty_quickphrase_mode_refusal": True,
                            "temporary_modes_refusal": True,
                            "unknown_addon_preserves_input": True,
                            "unknown_library_origin_preserves_input": True,
                            "focus_preserves_input_epoch": True,
                            "actual_context_public_abi": True,
                            "local_only_write_while_server_paused": True,
                            "clear_caret_reset_batch": True,
                            "preedit_refusal": True,
                            "core_compose_refusal": True,
                            "unchanged_text_input_invalidates_capture": True,
                            "cancelled_compose_invalidates_capture": True,
                            "undelivered_candidate_refusal": True,
                            "stale_and_focus_refusal": True,
                            "iteration_reentry_refusal": True,
                            "reentrant_mutation_outcome_unknown": True,
                            "user_desktop_or_production_support": False,
                        },
                        indent=2,
                    )
                    + "\n"
                )
                command("quit")
                children[1].wait(timeout=5)
            finally:
                for process in reversed(children):
                    if process.poll() is None:
                        process.send_signal(signal.SIGCONT)
                        process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)

                if mapping_connection:
                    x11, display = mapping_connection
                    x11.XCloseDisplay(display)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--core-version", choices=(CORE_VERSION,), default=CORE_VERSION)
    parser.add_argument("--session", action="store_true")
    parser.add_argument("--session-deadline", type=float, help=argparse.SUPPRESS)
    parser.add_argument("--build-only", action="store_true")
    args = parser.parse_args()
    default_name = f"fcitx-cooperative-{CORE_VERSION}"
    out = (args.output or ROOT / "artifacts/linux" / default_name).resolve()
    out.mkdir(parents=True, exist_ok=True)
    if args.session:
        if args.session_deadline is None:
            parser.error("--session requires the outer absolute deadline")
        session(out, args.core_version, args.session_deadline)
    else:
        build(out, args.core_version)
        if not args.build_only:
            subprocess.run(
                [
                    "dbus-run-session",
                    "--",
                    "xvfb-run",
                    "-a",
                    sys.executable,
                    str(Path(__file__).resolve()),
                    "--session",
                    "--session-deadline",
                    str(time.monotonic() + 115),
                    "--core-version",
                    args.core_version,
                    "--output",
                    str(out),
                ],
                check=True,
                timeout=120,
                env=os.environ | {"TYPELAY_COOPERATIVE_OWNED_XVFB": "1"},
            )


if __name__ == "__main__":
    main()
