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
    run(
        [
            "cc",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-g",
            ROOT / "tests/linux/fixtures/gtk_fixture.c",
            "-o",
            out / "gtk3-fixture",
            *flags("gtk+-3.0", "json-glib-1.0"),
        ]
    )
    run(
        [
            "cmake",
            "-S",
            ROOT / "src/linux/native",
            "-B",
            out / "gtk3-native-build",
            "-G",
            "Ninja",
            "-DCMAKE_BUILD_TYPE=RelWithDebInfo",
        ]
    )
    run(
        [
            "cmake",
            "--build",
            out / "gtk3-native-build",
            "--target",
            "typelay-gtk3",
            "--parallel",
            "2",
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
                "gtk3_fixture_sha256": sha256(out / "gtk3-fixture"),
                "build_inputs_sha256": sha256(out / "build-inputs.json"),
                "build_metadata_sha256": sha256(out / "versions.json"),
            },
            indent=2,
        )
        + "\n"
    )


def wait(predicate, label, *, deadline=None):
    deadline = (
        min(time.monotonic() + 15, deadline) if deadline is not None else time.monotonic() + 15
    )
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


def session(out: Path, core_version: str, deadline: float, glossary_toolkit=None):
    def remaining():
        budget = deadline - time.monotonic()
        if budget <= 0:
            raise TimeoutError("cooperative session operation deadline elapsed")
        return budget

    def session_run(args, **kwargs):
        kwargs["timeout"] = min(kwargs.get("timeout", 180), remaining())
        return run(args, **kwargs)

    def session_wait(predicate, label):
        remaining()
        return wait(predicate, label, deadline=deadline)

    evidence = out / f"{glossary_toolkit}-glossary-session" if glossary_toolkit else out
    evidence.mkdir(parents=True, exist_ok=True)
    prefix = out / "core-prefix"
    for name in ("command", "command.tmp", "state.json", "result.json", "cleanup.json"):
        (evidence / name).unlink(missing_ok=True)
    if glossary_toolkit:
        for suffix in ("result.json", "startup.json"):
            (out / f"{glossary_toolkit}-glossary-{suffix}").unlink(missing_ok=True)
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
            TYPELAY_COOPERATIVE_DIR=str(evidence),
        )
        env["PKG_CONFIG_PATH"] = f"{prefix}/lib/pkgconfig:{prefix}/share/pkgconfig"
        versions = json.loads((out / "versions.json").read_text())
        assert versions["server_source_version"] == core_version
        assert (
            session_run([out / "fcitx5", "--version"], env=env, stderr=subprocess.STDOUT)
            == core_version
        )
        assert session_run(["pkg-config", "--modversion", "Fcitx5Core"], env=env) == core_version
        children = []
        mapping_connection = None
        with (
            (evidence / "framework.log").open("w") as framework_log,
            (evidence / "client.log").open("w") as client_log,
        ):
            try:
                mapping_connection = fixed_compose_mapping(evidence)
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
                        return json.loads((evidence / "state.json").read_text())
                    except (FileNotFoundError, json.JSONDecodeError):
                        return {}

                session_wait(lambda: state().get("ready"), "mapped GTK fixture")
                windows = session_wait(
                    lambda: subprocess.run(
                        ["xdotool", "search", "--onlyvisible", "--pid", str(children[-1].pid)],
                        env=env,
                        text=True,
                        capture_output=True,
                        timeout=min(5, remaining()),
                    ).stdout.split(),
                    "mapped fixture",
                )
                window = windows[0]
                session_run(["xdotool", "windowfocus", "--sync", window], env=env)
                session_wait(lambda: state().get("probe_valid"), "actual bound Fcitx IC handshake")
                (evidence / "engine-before.txt").write_text(
                    session_run(["fcitx5-remote", "-n"], env=env) + "\n"
                )
                session_run(["fcitx5-remote", "-o"], env=env)
                session_run(["fcitx5-remote", "-s", "pinyin"], env=env)
                (evidence / "engine-after.txt").write_text(
                    session_run(["fcitx5-remote", "-n"], env=env) + "\n"
                )
                session_wait(lambda: state().get("known"), "patched engine cooperation")
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
                (evidence / "server-loaded-libraries.json").write_text(
                    json.dumps(loaded, indent=2) + "\n"
                )
                request = 0

                def command(name):
                    nonlocal request
                    request += 1
                    temp = evidence / "command.tmp"
                    temp.write_text(f"{request} {name}\n")
                    temp.replace(evidence / "command")
                    return session_wait(
                        lambda: s if (s := state()).get("request") == request else None, name
                    )

                if glossary_toolkit:
                    sys.path.insert(0, str(ROOT / "tests/linux"))
                    from native_e2e import gtk_source_glossary

                    gtk_source_glossary(out, env, deadline, glossary_toolkit)
                    return

                session_run(["xdotool", "key", "--window", window, "dead_acute"], env=env)
                session_wait(lambda: state().get("composing"), "real core XCompose sequence")
                assert command("capture")["result"] == "refused-ime"
                session_run(["xdotool", "key", "--window", window, "e"], env=env)
                session_wait(
                    lambda: state().get("settled") and state().get("length", 0) > 0,
                    "core compose commit",
                )
                key_queue_ack = state().get("key_queue_ack", 0)
                session_run(
                    ["xdotool", "type", "--window", window, "--delay", "15", "nihao"], env=env
                )
                # xdotool completion only means the X server received its events.
                # Wait for a marker from the same actual GTK/Fcitx event queue
                # before candidate selection, without changing the composition.
                session_run(["xdotool", "key", "--window", window, "F24"], env=env)
                session_wait(
                    lambda: state().get("key_queue_ack", 0) > key_queue_ack,
                    "actual GTK/Fcitx typed-key queue acknowledgement",
                )
                session_wait(lambda: state().get("composing"), "real pinyin preedit")
                assert command("capture")["result"] == "refused-ime"
                assert command("candidate")["result"] == "refused-undelivered"
                session_wait(
                    lambda: state().get("settled") and state().get("length", 0) > 0,
                    "actual candidate commit applied",
                )
                assert command("capture")["result"] == "captured"
                assert command("accept")["result"] == "accepted"
                assert state()["replacement"] is True
                before = command("capture")
                assert before["result"] == "captured"
                session_run(["xdotool", "key", "--window", window, "Left"], env=env)
                session_wait(
                    lambda: state().get("epoch", 0) > before["epoch"],
                    "actual navigation input epoch",
                )
                assert state()["unchanged_capture"] is True
                assert command("accept")["result"] == "refused-input"
                before = command("capture")
                assert before["result"] == "captured"
                session_run(["xdotool", "key", "--window", window, "dead_acute"], env=env)
                session_wait(lambda: state().get("composing"), "core compose before cancellation")
                session_run(["xdotool", "key", "--window", window, "Escape"], env=env)
                session_wait(lambda: state().get("settled"), "core compose cancellation")
                assert state()["unchanged_capture"] is True
                assert command("accept")["result"] == "refused-input"
                assert command("capture")["result"] == "captured"
                assert command("mutate")["result"] == "mutated"
                assert command("accept")["result"] == "refused-stale"
                assert command("capture")["result"] == "captured"
                assert command("focus")["result"] == "focused-other"
                assert command("accept")["result"] == "refused-focus"
                assert command("refocus")["result"] == "focused-original"
                session_wait(lambda: state().get("settled"), "context after refocus")
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
                session_run(["xdotool", "type", "--window", window, "--delay", "15", "1."], env=env)
                session_wait(
                    lambda: state().get("settled") and state().get("length") == 2,
                    "numeric punctuation before old transaction",
                )
                assert command("deferred-switch")["result"] == "refused-old-pinyin-transaction"
                session_wait(lambda: state().get("settled"), "actual keyboard engine cooperation")
                assert session_run(["fcitx5-remote", "-n"], env=env) == "keyboard-us"
                (out / "engine-keyboard.txt").write_text("keyboard-us\n")
                assert command("clear")["result"] == "cleared"
                session_run(
                    ["xdotool", "type", "--window", window, "--delay", "15", "hello"], env=env
                )
                session_wait(
                    lambda: state().get("settled") and state().get("length") == 5,
                    "keyboard ordinary input",
                )
                assert command("capture")["result"] == "captured"
                assert command("accept")["result"] == "accepted"
                before_focus = command("capture")
                assert before_focus["result"] == "captured"
                assert command("focus")["result"] == "focused-other"
                assert command("refocus")["result"] == "focused-original"
                after_focus = session_wait(
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
                session_wait(lambda: state().get("other_focused"), "actual second field focus")
                session_run(
                    ["xdotool", "type", "--window", window, "--delay", "15", "world"], env=env
                )
                session_wait(
                    lambda: state().get("other_length") == 5, "second field ordinary input"
                )
                assert command("detached-proof")["result"] == "detached-clear"
                session_run(["fcitx5-remote", "-s", "pinyin"], env=env)
                assert session_run(["fcitx5-remote", "-n"], env=env) == "pinyin"
                session_run(
                    ["xdotool", "type", "--window", window, "--delay", "15", "nihao"], env=env
                )
                ack = state().get("key_queue_ack", 0)
                session_run(["xdotool", "key", "--window", window, "F24"], env=env)
                session_wait(
                    lambda: state().get("key_queue_ack", 0) > ack
                    and state().get("other_preedit", 0) > 0,
                    "second field real preedit delivered",
                )
                assert command("detached-proof")["result"] == "detached-clear"
                assert state()["other_preedit"] > 0
                session_run(["xdotool", "key", "--window", window, "Escape"], env=env)
                session_wait(
                    lambda: state().get("other_preedit") == 0, "second field composition cancelled"
                )
                session_run(["fcitx5-remote", "-s", "keyboard-us"], env=env)
                assert command("refocus")["result"] == "focused-original"
                session_wait(lambda: state().get("settled"), "original keyboard field returned")
                session_run(["xdotool", "key", "--window", window, "dead_acute"], env=env)
                session_wait(
                    lambda: state().get("composing") or state().get("pending"),
                    "keyboard compose pending",
                )
                assert command("capture")["result"] == "refused-ime"
                session_run(["xdotool", "key", "--window", window, "e"], env=env)
                session_wait(lambda: state().get("settled"), "keyboard compose commit")
                assert command("capture")["result"] == "captured"
                assert command("accept-server-paused")["result"] == "accepted"
                assert state()["replacement"] is True
                session_wait(
                    lambda: state().get("settled"), "owned server resumed after local-only write"
                )
                for mode, key in (
                    ("QuickPhrase", "super+grave"),
                    ("Clipboard", "ctrl+semicolon"),
                    ("Unicode", "ctrl+shift+u"),
                    ("IMSelector", "ctrl+alt+i"),
                ):
                    before_mode = state()
                    session_run(["xdotool", "key", "--window", window, key], env=env)
                    session_wait(lambda: state().get("composing"), f"real active {mode} mode")
                    assert state()["length"] == before_mode["length"]
                    assert command("capture")["result"] == "refused-ime"
                    session_run(["xdotool", "key", "--window", window, "Escape"], env=env)
                    session_wait(lambda: state().get("settled"), f"{mode} mode cancelled")
                    assert command("capture")["result"] == "captured"

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
                    daemon.wait(timeout=min(5, remaining()))
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
                    session_wait(
                        lambda: state().get("probe_valid")
                        and state().get("generation", 0) > generation,
                        f"same GTK client reconnected for unknown {label}",
                    )
                    session_run(["fcitx5-remote", "-s", "keyboard-us"], env=env)
                    session_wait(
                        lambda: state().get("probe_valid") and not state().get("known"),
                        f"actual unreviewed {label} refuses capability",
                    )
                    assert command("capture")["result"] == "refused-ime"
                    assert command("clear")["result"] == "cleared"
                    session_run(
                        ["xdotool", "type", "--window", window, "--delay", "15", "ordinary"],
                        env=env,
                    )
                    session_wait(
                        lambda: state().get("length") == 8,
                        f"ordinary input preserved with unknown {label}",
                    )
                assert command("replace-delegate")["result"] == "old-delegate-released"
                (evidence / "result.json").write_text(
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
                children[1].wait(timeout=min(5, remaining()))
            finally:
                try:
                    for process in reversed(children):
                        if process.poll() is None:
                            process.send_signal(signal.SIGCONT)
                            process.terminate()
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait(timeout=5)
                finally:
                    if mapping_connection:
                        x11, display = mapping_connection
                        x11.XCloseDisplay(display)

                    (evidence / "cleanup.json").write_text(
                        json.dumps(
                            {
                                "childrenExited": all(p.poll() is not None for p in children),
                                "children": [
                                    {"pid": p.pid, "returncode": p.returncode} for p in children
                                ],
                            }
                        )
                        + "\n"
                    )


def run_owned_session(command, *, env, timeout):
    # Keep the session leader unreaped until its whole owned process group has
    # been signalled. Its PID/PGID cannot be reused for an unrelated process.
    # The last two seconds belong to emergency cleanup, not more test work.
    if timeout <= 2:
        raise ValueError("owned session needs a cleanup reserve")
    deadline = time.monotonic() + timeout
    process = subprocess.Popen(command, env=env, start_new_session=True)
    try:
        while os.waitid(os.P_PID, process.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT) is None:
            remaining = deadline - 2 - time.monotonic()
            if remaining <= 0:
                raise subprocess.TimeoutExpired(command, timeout - 2)
            time.sleep(min(0.025, remaining))
    finally:
        # Covers timeout, interruption and an exited wrapper leaving children.
        # All members inherit this new session; no names or user-wide PID scan.
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait(timeout=max(0, deadline - time.monotonic()))
    if process.returncode:
        raise subprocess.CalledProcessError(process.returncode, command)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--core-version", choices=(CORE_VERSION,), default=CORE_VERSION)
    parser.add_argument("--session", action="store_true")
    parser.add_argument("--session-deadline", type=float, help=argparse.SUPPRESS)
    parser.add_argument("--build-only", action="store_true")
    parser.add_argument("--glossary-toolkit", choices=("gtk3", "gtk4"), help=argparse.SUPPRESS)
    args = parser.parse_args()
    default_name = f"fcitx-cooperative-{CORE_VERSION}"
    out = (args.output or ROOT / "artifacts/linux" / default_name).resolve()
    out.mkdir(parents=True, exist_ok=True)
    if args.session:
        if args.session_deadline is None:
            parser.error("--session requires the outer absolute deadline")
        session(out, args.core_version, args.session_deadline, args.glossary_toolkit)
    else:
        build(out, args.core_version)
        if not args.build_only:
            # Independent desktops/daemons reuse the same already-built assets.
            for toolkit in (None, "gtk4", "gtk3"):
                run_owned_session(
                    [
                        "dbus-run-session",
                        "--",
                        "xvfb-run",
                        "-a",
                        "-s",
                        "-screen 0 1280x960x24",
                        sys.executable,
                        str(Path(__file__).resolve()),
                        "--session",
                        *(["--glossary-toolkit", toolkit] if toolkit else []),
                        "--session-deadline",
                        str(time.monotonic() + (115 if toolkit else 98)),
                        "--core-version",
                        args.core_version,
                        "--output",
                        str(out),
                    ],
                    timeout=145 if toolkit else 120,
                    env=os.environ | {"TYPELAY_COOPERATIVE_OWNED_XVFB": "1"},
                )
                evidence = out / f"{toolkit}-glossary-session" if toolkit else out
                assert json.loads((evidence / "cleanup.json").read_text())["childrenExited"]


if __name__ == "__main__":
    main()
