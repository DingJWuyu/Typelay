"""Default Index journey through real cooperative GTK3 Entry and TextView widgets."""

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from contextlib import ExitStack
from pathlib import Path

from desktop_e2e import (
    acceptance_observation_current,
    fixture_module_identity,
    prepare_background_direct,
    wait_for,
)
from main import control
from native_e2e import key
from niri import contents, request
from paths import Paths
from support.gtk_accessibility import Atspi, named, select_settings_page
from support.xtest import prepare_key, prepare_pointer
from wire import now_ms

SOURCE = "Please keep the backup until Friday."
ENTRY_DRAFT = "Manual backup checked."
TEXT_DRAFT = "Checked ✓\nManual line 😀"
PREFIX, SUFFIX = "Before 😀\n", "\nAfter untouched"
STRUCTURE_SOURCE = "  Please keep https://example.com\r\nThank you.  "
# GtkTextView's real clipboard paste normalizes CRLF before TypeLay captures it.
STRUCTURE_FIELD = "  Please keep https://example.com\nThank you.  "


def run(arguments):
    output = arguments.output
    sys.stdout = sys.stderr = (output / "inside.log").open("w", buffering=1)
    package = arguments.package
    cooperative = arguments.fcitx_cooperative
    private = Path(os.environ["TYPELAY_VERIFY_OUTPUT"])
    session = json.loads((private / "session.json").read_text())
    assert session["NIRI_SOCKET"] == os.environ["NIRI_SOCKET"]
    assert package == Path(session["TYPELAY_VERIFY_PACKAGE"])
    os.environ["DISPLAY"] = os.environ["TYPELAY_VERIFY_XDISPLAY"]
    paths = Paths(package)
    trace_path, state_path = output / "controller.jsonl", output / "fields.json"
    environment = dict(os.environ, TYPELAY_TRACE=str(trace_path))
    launcher = [sys.executable, str(cooperative / "fcitx-cooperative.py")]
    app = str(package / "typelay")
    processes, actions = [], []
    result = {
        "status": "failed",
        "feature": "writeback/gtk3-index",
        "sourceCommit": json.loads((package / "build-info.json").read_text()),
        "fixture": "stock GtkEntry, GtkSearchEntry and plain GtkTextView with read-only observations",
        "initialSettings": {
            "Schema": 1,
            "UiLanguage": "zh-CN",
            "Source": "en",
            "Target": "zh",
            "OnboardingComplete": True,
            "ExperimentalWriteback": True,
            "Mode": 0,
        },
        "runnerSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "substitutes": [],
        "physicalInputTested": False,
        "nativeUndo": "unsupported-by-gtk3-textbuffer",
        "cases": {
            name: {"status": "not-run"}
            for name in ("entry", "textview", "structure", "direct", "unsupported")
        },
    }

    def save(name, value):
        (output / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")

    def record(action, **detail):
        actions.append({"action": action, "atMs": now_ms(), **detail})
        save("actions.json", actions)

    def start(command, name, env=environment):
        with (output / name).open("w") as log:
            process = subprocess.Popen(command, env=env, stdout=log, stderr=subprocess.STDOUT)
        processes.append(process)
        return process

    def status():
        try:
            return control(paths, "status") or {}
        except (OSError, EOFError, ValueError):
            return {}

    def state():
        assert fixture.poll() is None, "owned GTK fixture exited"
        try:
            return json.loads(state_path.read_text())
        except (OSError, ValueError):
            return {}

    def checkpoint():
        return trace_path.read_bytes().rfind(b"\n") + 1

    def rows(offset):
        with trace_path.open("rb") as stream:
            stream.seek(offset)
            data = stream.read()
        return [json.loads(line) for line in data[: data.rfind(b"\n") + 1].splitlines()]

    def writes(offset):
        return [row for row in rows(offset) if row["operation"].startswith("write-")]

    def ready(expected="ValidTranslation"):
        started = now_ms()
        value = status()
        return (
            value
            if (
                value.get("viewState") == expected
                and value.get("overlayVisible")
                and acceptance_observation_current(value, now_ms() - started)
            )
            else None
        )

    def picture(label):
        save(label + ".json", {"fields": state(), "desktop": status()})
        subprocess.run(["grim", str(output / (label + ".png"))], check=True, timeout=3)

    def typed(text):
        record("XTest type", text=text)
        expected_focus = state().get("focus")
        # Use the original GTK input method for non-ASCII characters, not a setter.
        for part in re.split(r"([^\x20-\x7e])", text):
            if not part:
                continue
            if part == "\n":
                key("Return")
            elif len(part) == 1 and ord(part) > 126:
                key("ctrl+shift+u")
                subprocess.run(
                    ["xdotool", "type", "--clearmodifiers", hex(ord(part))[2:]],
                    check=True,
                    timeout=3,
                )
                key("Return")
            else:
                subprocess.run(
                    ["xdotool", "type", "--clearmodifiers", "--delay", "2", part],
                    check=True,
                    timeout=5,
                )
            observed = now_ms()
            wait_for(
                lambda: state().get("observedAtMs", 0) >= observed,
                "typed text observation did not advance",
            )
            assert state().get("focus") == expected_focus, "typing left the original focused editor"

    def clicked(field):
        actual = state()
        rectangle = actual[field]
        pointer(
            int(
                (rectangle["x"] + (12 if field == "multiline" else rectangle["width"] / 2))
                * actual["scale"]
            ),
            int(
                (
                    rectangle["y"]
                    + (12 if field == "multiline" else min(rectangle["height"] / 2, 12))
                )
                * actual["scale"]
            ),
            timing={},
        )

    def pasted(text):
        # Only the private Wayland compositor's clipboard receives synthetic text.
        record("private clipboard paste", text=text)
        subprocess.run(
            ["wl-copy", "--type", "text/plain;charset=utf-8"],
            input=text,
            text=True,
            check=True,
            timeout=3,
        )
        assert subprocess.check_output(["wl-paste", "--no-newline"], timeout=3) == text.encode()
        key("ctrl+v")

    def key_offset():
        return (output / "fixture.log").stat().st_size

    def observed_tab(offset, phase):
        def observe():
            with (output / "fixture.log").open("rb") as stream:
                stream.seek(offset)
                value = stream.read().decode(errors="replace")
            matches = re.findall(
                r"TypeLay GTK3 session-key kind=tab phase="
                + phase
                + r" consumed=([01]) atMonoMs=(\d+)",
                value,
            )
            return (
                {"consumed": matches[-1][0] == "1", "atMs": int(matches[-1][1])}
                if matches
                else None
            )

        observed = wait_for(observe, "GTK did not observe the original Tab " + phase)
        assert observed["consumed"], observed
        return observed

    def edit_accept(label, source_text, draft, field, focus):
        result["cases"][label] = {"status": "failed", "input": SOURCE, "draft": draft}
        before = wait_for(lambda: state() if ready() else None, "candidate unavailable", 90)
        candidate = before["candidate"]["text"]
        assert candidate and candidate != SOURCE and "\n" not in candidate
        assert re.search(r"备份", candidate) and re.search(r"周五|星期五|礼拜五", candidate), (
            candidate
        )
        assert (
            state()[field]["text"] == source_text
            if field == "multiline"
            else state()["first"] == source_text
        )
        picture(label + "-preview")
        clicked("candidate")
        wait_for(
            lambda: state().get("focus") == "typelay-candidate"
            and status().get("viewState") == "EditingTranslation",
            "candidate click did not open editor",
        )
        key("ctrl+a")
        observed = now_ms()
        wait_for(
            lambda: state().get("observedAtMs", 0) >= observed,
            "editor selection observation did not advance",
        )
        if field == "multiline":
            assert (state()[field]["start"], state()[field]["end"]) == (
                before[field]["start"],
                before[field]["end"],
            ), "selecting the draft changed the original source range"
        typed(draft)
        edited = wait_for(
            lambda: value
            if (value := state()).get("focusedImeClear")
            and (value.get("candidate") or {}).get("text") == draft
            else None,
            "manual draft differs from real editor",
        )
        captures = status()["input"]["captureCounts"].get("supported", 0)
        picture(label + "-editing")
        before_write = checkpoint()
        record("first default Tab down", field=focus)
        tab(True)
        try:
            returned = wait_for(
                lambda: value
                if (value := state()).get("focus") == focus
                and value.get("focusedImeClear")
                and ready("EditedTranslationPending")
                else None,
                "first Tab did not return with a freshly eligible draft",
            )
            assert not writes(before_write), "first Tab wrote to the source"
            assert (
                returned[field]["text"] == source_text
                if field == "multiline"
                else returned["first"] == source_text
            )
            # A held repeat remains the first physical press; it cannot accept.
            repeat_start = key_offset()
            tab(True)
            repeated = observed_tab(repeat_start, "repeat")
            assert not writes(before_write), "held first Tab wrote to the source"
        finally:
            release_start = key_offset()
            tab(False)
        released = observed_tab(release_start, "release")
        # Editing intentionally freezes source capture. Returning resumes real
        # capture/render refreshes, which must preserve the manual candidate.
        refreshed = wait_for(
            lambda: value
            if (value := status())["input"]["captureCounts"].get("supported", 0) > captures
            and ready("EditedTranslationPending")
            and state()["candidate"]["text"] == draft
            else None,
            "fresh source capture did not retain the returned manual draft",
        )
        assert not writes(before_write), "return or refresh wrote to the source"
        if field == "multiline":
            assert (returned[field]["start"], returned[field]["end"]) == (
                before[field]["start"],
                before[field]["end"],
            )
        picture(label + "-returned")
        before_count = (
            state().get("userActionsStarted", 0)
            if field == "multiline"
            else state()[field]["insertions"]
        )
        accepted_observation = wait_for(
            lambda: ready("EditedTranslationPending"),
            "released first Tab lost the fresh acceptance qualification",
        )
        record("second independent default Tab", field=focus, afterRelease=released["atMs"])
        timing = {}
        tab(True, sync=False, timing=timing)
        tab(False, timing=timing)
        assert timing["downStartedAtMs"] >= released["atMs"]
        expected = PREFIX + draft + SUFFIX if field == "multiline" else draft
        wait_for(
            lambda: (state()[field]["text"] if field == "multiline" else state()["first"])
            == expected,
            "second Tab did not write the exact manual draft",
        )
        outcome = wait_for(
            lambda: found
            if (found := [row for row in writes(before_write) if row["operation"] != "write-start"])
            else None,
            "accepted draft has no write receipt",
        )
        assert len(outcome) == 1 and outcome[0]["operation"] == "write-Verified-None", outcome
        after = state()
        assert after["focus"] == focus
        if field == "multiline":
            assert after["userActionsStarted"] == after["userActionsFinished"] == before_count + 1
        else:
            assert after[field]["insertions"] == before_count + 1
            assert after[field]["deletions"] == before[field]["deletions"] + 1
        picture(label + "-accepted")
        result["cases"][label] = {
            "status": "passed",
            "input": SOURCE,
            "visibleTranslation": candidate,
            "draft": draft,
            "before": before,
            "edited": edited,
            "returned": returned,
            "refreshed": refreshed,
            "heldRepeat": repeated,
            "release": released,
            "acceptance": accepted_observation,
            "secondTab": timing,
            "after": after,
            "writeReceipts": outcome,
            "nativeUndo": False,
        }
        save("result.json", result)

    fixture = None
    try:
        subprocess.run([*launcher, "preflight"], check=True, timeout=60)
        assert not (paths.data / "components/current").exists(), (
            "journey requires an independent final model data path"
        )
        catalog = json.loads((package / "worker/model-catalog.json").read_text())
        model = next(
            value for value in catalog["packages"] if value["id"] == "index-translate-2b-q4-k-m"
        )
        cache = (
            Path(os.environ["XDG_CACHE_HOME"])
            / "typelay/components"
            / model["id"]
            / model["version"]
        )
        shutil.copytree(arguments.model_cache, cache)
        with (output / "model-install.log").open("w") as log:
            subprocess.run(
                [app, "--install-models"],
                stdout=log,
                stderr=subprocess.STDOUT,
                check=True,
                timeout=600,
            )
        inventory = paths.data / "components/current/models/installed.json"
        shutil.copyfile(inventory, output / "installed.json")
        paths.settings.write_text(json.dumps(result["initialSettings"]))
        save("initial-settings.json", result["initialSettings"])
        daemon = start([*launcher, "daemon"], "fcitx.log")

        def daemon_ready():
            assert daemon.poll() is None, "owned Fcitx daemon exited"
            return "true" in subprocess.check_output(
                [
                    "gdbus",
                    "call",
                    "--session",
                    "--dest",
                    "org.freedesktop.DBus",
                    "--object-path",
                    "/org/freedesktop/DBus",
                    "--method",
                    "org.freedesktop.DBus.NameHasOwner",
                    "org.fcitx.Fcitx5",
                ],
                text=True,
                timeout=3,
            )

        wait_for(daemon_ready, "private Fcitx did not become ready")
        desktop = start([*launcher, "launch", app, "--background"], "desktop.log")

        def model_ready():
            assert desktop.poll() is None, "owned production desktop exited"
            return status().get("modelReady")

        wait_for(model_ready, "formal Index did not reach Ready", 125)
        result["modelInferenceProfileId"] = status().get("modelInferenceProfileId")
        assert (
            result["modelInferenceProfileId"]
            == "index-v1-d1ba1b7b9a3e78434e49767a43586a097ce40ef7607d170f1097cfae67c95180"
        )
        result["packageFileSystem"] = subprocess.check_output(
            ["stat", "-f", "-c", "%T", package], text=True
        ).strip()
        assert result["packageFileSystem"] != "tmpfs"
        save(
            "doctor-ready.json",
            json.loads(subprocess.check_output([app, "--doctor"], text=True, timeout=10)),
        )
        nested = subprocess.check_output(
            ["xdotool", "search", "--onlyvisible", "--name", "^niri$"], text=True, timeout=3
        ).splitlines()
        assert len(nested) == 1
        subprocess.run(["xdotool", "windowfocus", "--sync", nested[0]], check=True, timeout=3)
        fixture_environment = dict(
            environment,
            TYPELAY_FIXTURE_STATE=str(state_path),
            TYPELAY_FIXTURE_COOPERATIVE="1",
            TYPELAY_FIXTURE_FULLSCREEN="1",
            TYPELAY_FIXTURE_INITIAL_TEXT=SOURCE,
        )
        fixture = start(
            [*launcher, "launch", app, "launch", str(arguments.native / "gtk-fixture")],
            "fixture.log",
            fixture_environment,
        )
        window = wait_for(
            lambda: next(
                (
                    item
                    for item in contents(request("Windows"), "Windows")
                    if item.get("pid") == fixture.pid
                ),
                None,
            ),
            "GTK fixture window missing",
        )
        request({"Action": {"FocusWindow": {"id": window["id"]}}})
        wait_for(lambda: state().get("focusedImeClear"), "GTK input method remains unknown")
        save(
            "fixture-identity.json",
            fixture_module_identity(
                fixture.pid, arguments.native / "gtk-fixture", package / "native/libtypelay-gtk3.so"
            ),
        )
        with ExitStack() as resources:
            tab = prepare_key(resources, "Tab")
            pointer = prepare_pointer(resources, nested[0])
            edit_accept("entry", SOURCE, ENTRY_DRAFT, "firstEntry", "fixture-first")
            clicked("multiline")
            wait_for(
                lambda: state().get("focus") == "fixture-multiline"
                and state().get("focusedImeClear"),
                "plain TextView did not focus",
            )
            key("ctrl+a")
            typed(PREFIX + SOURCE + SUFFIX)
            wait_for(
                lambda: state()["multiline"]["text"] == PREFIX + SOURCE + SUFFIX,
                "Unicode multiline source differs",
            )
            key("ctrl+Home")
            key("Down")
            key("Home")
            key("shift+End")
            wait_for(
                lambda: state()["multiline"]["start"] == len(PREFIX)
                and state()["multiline"]["end"] == len(PREFIX + SOURCE),
                "TextView selection differs",
            )
            edit_accept(
                "textview", PREFIX + SOURCE + SUFFIX, TEXT_DRAFT, "multiline", "fixture-multiline"
            )
            result["cases"]["structure"] = {"status": "failed", "input": STRUCTURE_SOURCE}
            key("ctrl+a")
            pasted(STRUCTURE_SOURCE)
            structured = wait_for(
                lambda: value
                if (value := state())["multiline"]["text"] == STRUCTURE_FIELD and ready()
                else None,
                "structured source has no visible Index translation",
                90,
            )
            translated = structured["candidate"]["text"]
            assert translated.count("https://example.com") == 1
            assert translated.count("\n") == 1 and "\r" not in translated
            assert len(translated) - len(translated.lstrip(" \t")) == 2
            assert len(translated) - len(translated.rstrip(" \t")) == 2
            assert "保留" in translated and "谢" in translated, translated
            picture("structure-preview")
            result["cases"]["structure"] = {
                "status": "passed",
                "input": STRUCTURE_SOURCE,
                "capturedSource": STRUCTURE_FIELD,
                "visibleTranslation": translated,
                "fields": structured,
            }
            # Change the actual saved mode through the user-facing settings control.
            assert control(paths, "settings") is True
            select_settings_page(desktop.pid, "通用")
            switch = wait_for(
                lambda: named(desktop.pid, "设置：工作模式：按需"),
                "OnDemand settings control missing",
            )
            assert Atspi.Action.do_action(switch.get_action_iface(), 0)
            wait_for(
                lambda: json.loads(paths.settings.read_text()).get("Mode") == 1,
                "OnDemand mode was not saved",
            )
            settings_window = wait_for(
                lambda: next(
                    (
                        item
                        for item in contents(request("Windows"), "Windows")
                        if item.get("pid") == desktop.pid
                    ),
                    None,
                ),
                "settings window missing",
            )
            request({"Action": {"CloseWindow": {"id": settings_window["id"]}}})
            request({"Action": {"FocusWindow": {"id": window["id"]}}})
            clicked("firstEntry")
            wait_for(
                lambda: state().get("focus") == "fixture-first" and state().get("focusedImeClear"),
                "original direct field did not focus",
            )
            key("ctrl+a")
            typed(SOURCE)
            wait_for(
                lambda: state()["first"] == SOURCE
                and state().get("focusedImeClear")
                and not status().get("overlayVisible"),
                "direct source did not settle",
            )
            result["cases"]["direct"] = {"status": "failed"}
            before, offset = state(), checkpoint()
            record("default Direct shortcut and focus B", source=SOURCE)
            send_direct = prepare_background_direct(resources, nested[0])
            send_direct(before, False)
            detached = wait_for(
                lambda: value
                if (value := state()).get("focus") == "fixture-second"
                and value.get("focusedImeClear")
                else None,
                "new field did not acquire focus",
            )
            key("ctrl+a")
            detached = wait_for(
                lambda: value
                if (value := state())["secondEntry"]["start"] == 0
                and value["secondEntry"]["end"] == len(value["second"])
                else None,
                "new field selection did not become visible",
            )
            assert not writes(offset), (
                "Direct completed before the new field selection was observed"
            )
            wait_for(
                lambda: status().get("serviceStatus") == "已直接替换原输入框文字。",
                "background Direct did not confirm the original field replacement",
                90,
            )
            after = state()
            direct = [row for row in rows(offset) if row["operation"].startswith("direct-write-")]
            assert len(direct) == 2 and direct[-1]["detail"]["status"] == "Verified", direct
            assert (
                after["first"] != SOURCE
                and "备份" in after["first"]
                and re.search(r"周五|星期五|礼拜五", after["first"])
            )
            assert after["focus"] == "fixture-second" and after["second"] == before["second"]
            for member in (
                "focused",
                "start",
                "end",
                "insertions",
                "deletions",
                "focusIn",
                "focusOut",
            ):
                assert after["secondEntry"][member] == detached["secondEntry"][member], member
            assert after["firstEntry"]["insertions"] == before["firstEntry"]["insertions"] + 1
            assert after["firstEntry"]["deletions"] == before["firstEntry"]["deletions"] + 1
            for member in ("firstInsertContext", "firstDeleteContext"):
                changed = after[member]
                assert changed["secondFocused"] and changed["secondText"] == before["second"]
                assert (changed["secondStart"], changed["secondEnd"]) == (
                    detached["secondEntry"]["start"],
                    detached["secondEntry"]["end"],
                )
            settled = writes(offset)
            assert len(settled) == 1 and settled[0]["operation"] == "write-Verified-None", settled
            assert settled[0]["planId"] == direct[0]["planId"] == direct[-1]["planId"]
            picture("direct-background")
            result["cases"]["direct"] = {
                "status": "passed",
                "input": SOURCE,
                "visibleTranslation": after["first"],
                "before": before,
                "detached": detached,
                "after": after,
                "receipts": direct,
                "writeReceipts": settled,
            }
            result["cases"]["unsupported"] = {"status": "failed"}
            for excluded in state()["excludedTextViews"]:
                pointer(
                    int((excluded["x"] + 12) * after["scale"]),
                    int((excluded["y"] + 12) * after["scale"]),
                    timing={},
                )
                wait_for(
                    lambda: state().get("focus") == excluded["name"]
                    and status().get("input", {}).get("captureStatus")
                    in ("excluded-or-sensitive", "no-native-field")
                    and not status().get("acceptanceReady"),
                    "unsupported TextView was not refused",
                )
            result["cases"]["unsupported"] = {
                "status": "passed",
                "fields": state()["excludedTextViews"],
            }
        result["status"] = "passed"
    except BaseException:
        if fixture is not None and fixture.poll() is None:
            try:
                picture("failure")
            except (OSError, subprocess.SubprocessError):
                pass  # Preserve the original failed action if evidence capture also fails.
        raise
    finally:
        save("result.json", result)
        for command in ([app, "--quit"], [str(cooperative / "core/bin/fcitx5-remote"), "-e"]):
            subprocess.run(command, stdout=sys.stdout, stderr=sys.stderr, timeout=10, check=False)
        cleanup = []
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=7)
            cleanup.append({"pid": process.pid, "exitCode": process.returncode})
        save("process-cleanup.json", cleanup)
