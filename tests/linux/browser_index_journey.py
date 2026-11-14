"""Default Index journey through the installed browser extension and desktop."""

import json
import os
import shutil
import subprocess
import time
from contextlib import ExitStack
from pathlib import Path

from main import control
from niri import contents, request
from paths import Paths
from playwright.sync_api import sync_playwright
from support.browser_process import chromium_context, extension_worker
from support.gtk_accessibility import Atspi, named, select_settings_page
from support.xtest import prepare_key, prepare_xtest

INDEX = "index-translate-2b-q4-k-m"
PROFILE = "index-v1-d1ba1b7b9a3e78434e49767a43586a097ce40ef7607d170f1097cfae67c95180"
SOURCE = "Please keep the backup until Friday."
DRAFT = "人工确认：请把备份保留到周五。\n已复核🙂"
SELECTION = "  Please keep https://example.com\nThank you.  "
PREFIX, SUFFIX = "前缀🙂\n", "\n后缀🚀"
DIRECT = SOURCE


def run(arguments):
    output = arguments.output
    output.mkdir(parents=True, exist_ok=True)
    session = json.loads((Path(os.environ["TYPELAY_VERIFY_OUTPUT"]) / "session.json").read_text())
    assert session["NIRI_SOCKET"] == os.environ["NIRI_SOCKET"]
    assert arguments.package == Path(session["TYPELAY_VERIFY_PACKAGE"])
    os.environ["DISPLAY"] = os.environ["TYPELAY_VERIFY_XDISPLAY"]
    # Keep the installed package, model and disposable browser on the normal
    # filesystem. Only private sockets use the small XDG runtime directory.
    stages = {
        name: "not-run" for name in ("prepare-default", "preview", "draft", "selection", "direct")
    }
    result = {
        "featureIds": ["models/default-cli", "preview/browser-index", "writeback/browser-index"],
        "stages": stages,
        "fixture": "ordinary top-level textarea page with page-owned input observations",
        "transportStub": False,
        "input": "private XTest Tab; trusted browser text insertion and DOM selection setup",
        "initialSettingsInjection": {"Source": "en", "Target": "zh", "OnboardingComplete": True},
        "quality": "requires sentence-by-sentence review of samples.json",
        "nativeUndo": "not-run; standard textarea setRangeText is not native undo proof",
    }
    stage = "prepare-default"
    process, page, paths = None, None, None
    samples, actions = [], []

    def save(name, value):
        (output / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")

    def wait(observe, message, timeout=3):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            value = observe()
            if value:
                return value
            time.sleep(0.025)
        raise AssertionError(message)

    def command(*args, timeout=15):
        actions.append({"command": list(map(str, args))})
        save("actions.json", actions)
        return subprocess.check_output(list(map(str, args)), text=True, timeout=timeout)

    def status():
        try:
            return control(paths, "status") or {}
        except (OSError, EOFError, ValueError):
            return {}

    def state():
        return page.evaluate("""() => ({focused:document.hasFocus(),active:document.activeElement.id,
          fields:Object.fromEntries(['source','other'].map(id=>{const e=document.getElementById(id);
            return [id,{text:e.value,start:e.selectionStart,end:e.selectionEnd}]})),
          events:[...journeyEvents]})""")

    def capture(name):
        value = {"page": state(), "desktop": status()}
        save("actions.json", actions)
        save(name + ".json", value)
        page.screenshot(path=str(output / (name + ".png")))
        return value

    def rows():
        if not trace.exists():
            return []
        raw = trace.read_bytes()
        return [json.loads(line) for line in raw[: raw.rfind(b"\n") + 1].splitlines()]

    def writes(since):
        return [row for row in rows()[since:] if row["operation"].startswith("write-")]

    def terminal_write(since):
        outcomes = [row for row in writes(since) if row["operation"] != "write-start"]
        assert len(outcomes) <= 1 and all(
            row["operation"] == "write-Verified-None" for row in outcomes
        ), outcomes
        return bool(outcomes)

    try:
        identity = json.loads((arguments.package / "build-info.json").read_text())
        assert identity["sourceDirty"] is False
        result["package"] = identity
        command("/usr/bin/python3", arguments.package / "install.py", "install", timeout=30)
        installed = (Path.home() / ".local/lib/typelay/current").resolve(strict=True)
        result["installedPackage"] = str(installed)
        result["packageFilesystem"] = command("stat", "-f", "-c", "%T", installed).strip()
        assert result["packageFilesystem"] != "tmpfs"
        paths = Paths(installed)
        assert not (paths.data / "components/current").exists()
        catalog = json.loads((installed / "worker/model-catalog.json").read_text())
        model = next(item for item in catalog["packages"] if item["id"] == INDEX)
        shutil.copytree(
            arguments.index_cache, paths.cache / "components" / INDEX / model["version"]
        )
        app = installed / "typelay"
        actions.append({"command": [str(app), "--install-models"], "emptyDataDirectory": True})
        save("actions.json", actions)
        with (output / "model-install.log").open("w") as log:
            subprocess.run(
                [str(app), "--install-models"],
                stdout=log,
                stderr=subprocess.STDOUT,
                check=True,
                timeout=600,
            )
        current = (paths.data / "components/current").resolve(strict=True)
        shutil.copyfile(current / "models/installed.json", output / "installed.json")
        result["modelGeneration"] = current.name
        result["privateUv"] = json.loads((installed / "support/uv/source.json").read_text())
        settings = {
            "Schema": 1,
            "UiLanguage": "zh-CN",
            "Source": "en",
            "Target": "zh",
            "OnboardingComplete": True,
            "ExperimentalWriteback": False,
            "Mode": 0,
        }
        assert not paths.settings.exists()
        paths.settings.write_text(json.dumps(settings))
        save("initial-settings.json", settings)
        trace = output / "controller.jsonl"
        with (output / "typelay.log").open("w") as log:
            process = subprocess.Popen(
                [str(app), "--background"],
                env={**os.environ, "TYPELAY_TRACE": str(trace)},
                stdout=log,
                stderr=subprocess.STDOUT,
            )
        ready = wait(
            lambda: data if (data := status()).get("modelReady") else None,
            "default Index did not become Ready",
            90,
        )
        assert ready["modelInferenceProfileId"] == PROFILE
        save("doctor-ready.json", json.loads(command(app, "--doctor")))
        result["modelInferenceProfileId"] = ready["modelInferenceProfileId"]
        stages[stage] = "passed"
        with ExitStack() as stack:
            connection = prepare_xtest(stack)
            send_tab = prepare_key(stack, "Tab", connection=connection)

            def tab(down):
                timing = {}
                send_tab(down, timing=timing)
                actions.append({"action": "XTest Tab", "down": down, "timing": timing})

            outer = command("xdotool", "search", "--onlyvisible", "--name", "^niri$").splitlines()
            assert len(outer) == 1
            command("xdotool", "windowfocus", "--sync", outer[0])
            playwright = stack.enter_context(sync_playwright())
            extension = installed / "browser/chromium"
            browser = stack.enter_context(
                chromium_context(
                    playwright,
                    arguments.chromium,
                    paths.config.parent / arguments.browser_kind,
                    dict(os.environ),
                    [
                        "--ozone-platform=wayland",
                        f"--disable-extensions-except={extension}",
                        f"--load-extension={extension}",
                    ],
                )
            )
            worker = extension_worker(browser, extension)
            result["extensionId"] = worker.url.split("/")[2]
            command(
                "/usr/bin/python3",
                installed / "install.py",
                "browser-install",
                "--browser",
                arguments.browser_kind,
                "--extension-id",
                worker.url.split("/")[2],
            )
            browser.route(
                "http://127.0.0.1/typelay-index",
                lambda route: route.fulfill(
                    content_type="text/html",
                    body="""<!doctype html><meta charset=utf-8><title>TypeLay Index journey</title>
              <style>body{font:18px sans-serif;padding:32px}textarea{display:block;width:600px;height:140px;margin:24px}</style>
              <label>Original field A<textarea id=source></textarea></label><label>New field B<textarea id=other></textarea></label>
              <script>window.journeyEvents=[];for(const id of ['source','other']) document.getElementById(id).addEventListener('input',event=>{
                const b=document.getElementById('other');journeyEvents.push({id,type:event.inputType,data:event.data,trusted:event.isTrusted,
                  active:document.activeElement.id,b:{text:b.value,start:b.selectionStart,end:b.selectionEnd}})});</script>""",
                ),
            )
            page = browser.new_page()

            def final_browser_evidence():
                try:
                    capture("browser-final")
                except Exception as error:
                    result["finalBrowserEvidenceError"] = type(error).__name__

            stack.callback(final_browser_evidence)
            page.goto("http://127.0.0.1/typelay-index")
            window = wait(
                lambda: next(
                    (
                        item
                        for item in contents(request("Windows"), "Windows")
                        if (item.get("title") or "").startswith("TypeLay Index journey")
                    ),
                    None,
                ),
                "browser window did not map",
            )
            request({"Action": {"FocusWindow": {"id": window["id"]}}})
            cdp = browser.new_cdp_session(page)

            def editor():
                return next(
                    (
                        node
                        for node in cdp.send("Accessibility.getFullAXTree")["nodes"]
                        if not node.get("ignored")
                        and node.get("name", {}).get("value") == "TypeLay 译文"
                        and node.get("role", {}).get("value") == "textbox"
                    ),
                    None,
                )

            def candidate():
                value = status()
                if value.get("viewState") == "TranslationFailed":
                    result["translationFailure"] = next(
                        (
                            row.get("detail")
                            for row in reversed(rows())
                            if row["operation"] == "translate-done"
                        ),
                        None,
                    )
                    raise AssertionError("real Index rejected this candidate; see controller.jsonl")
                if value.get("viewState") != "ValidTranslation":
                    return None
                node = editor()
                return (value, node) if node and node.get("value", {}).get("value") else None

            def insert(text, field="source"):
                actions.append(
                    {"action": "trusted browser text input", "field": field, "text": text}
                )
                page.locator("#" + field).click()
                page.keyboard.press("Control+a")
                page.keyboard.insert_text(text)
                wait(
                    lambda: state()["fields"][field]["text"] == text,
                    "trusted browser text input did not arrive",
                )

            def permit(view="ValidTranslation"):
                data = status()
                return (
                    data
                    if data.get("viewState") == view
                    and data.get("acceptanceReady")
                    and data.get("acceptanceRemainingMs", 0) > 0
                    else None
                )

            def press_tab():
                tab(True)
                tab(False)

            def sample(name, source, translated):
                assert translated != source and translated.strip()
                assert translated.count("\n") == source.count("\n")
                assert translated.count("🙂") == source.count("🙂")
                samples.append({"case": name, "source": source, "translation": translated})
                save("samples.json", samples)

            stage = "preview"
            insert(SOURCE)
            report, node = wait(candidate, "real Index preview did not render", 60)
            sample(stage, SOURCE, node["value"]["value"])
            assert state()["fields"]["source"]["text"] == SOURCE
            assert report["input"]["sessionShortcuts"]["sourceLocalAcceptanceVersion"] == 1
            capture("preview")
            stages[stage] = "passed"

            stage = "draft"
            quad = cdp.send("DOM.getBoxModel", {"backendNodeId": node["backendDOMNodeId"]})[
                "model"
            ]["border"]
            page.mouse.click(sum(quad[::2]) / 4, sum(quad[1::2]) / 4)
            actions.append(
                {"action": "click actual candidate", "backendDOMNodeId": node["backendDOMNodeId"]}
            )
            wait(
                lambda: status().get("viewState") == "EditingTranslation",
                "candidate click did not enter editing",
            )
            page.keyboard.press("Control+a")
            page.keyboard.insert_text(DRAFT)
            actions.append({"action": "edit actual candidate", "draft": DRAFT})
            wait(
                lambda: (editor() or {}).get("value", {}).get("value") == DRAFT,
                "manual draft did not remain visible",
            )
            before = capture("draft-before-return")
            checkpoint = len(rows())
            # A real display refresh and screenshot must not overwrite the draft.
            assert (editor() or {}).get("value", {}).get("value") == DRAFT
            tab(True)
            try:
                wait(
                    lambda: state()["active"] == "source"
                    and status().get("viewState") == "EditedTranslationPending",
                    "first default Tab did not return to the original field",
                )
                tab(True)  # A held repeat must not turn Return into Accept.
                assert state()["fields"] == before["page"]["fields"]
                assert state()["events"] == before["page"]["events"]
                assert not writes(checkpoint)
            finally:
                tab(False)
            returned = capture("draft-returned")
            assert returned["page"]["fields"] == before["page"]["fields"]
            assert not writes(checkpoint)
            wait(
                lambda: permit("EditedTranslationPending"),
                "released Tab did not obtain a fresh draft permit",
            )
            press_tab()
            wait(
                lambda: state()["fields"]["source"]["text"] == DRAFT and terminal_write(checkpoint),
                "second independent default Tab did not accept the exact draft",
            )
            accepted = capture("draft-accepted")
            assert accepted["page"]["active"] == "source"
            assert len(accepted["page"]["events"]) == len(before["page"]["events"]) + 1
            assert accepted["page"]["fields"]["other"] == before["page"]["fields"]["other"]
            stages[stage] = "passed"

            stage = "selection"
            insert(PREFIX + SELECTION + SUFFIX)
            page.locator("#source").evaluate(
                "(element, text) => element.setSelectionRange(5, 5 + text.length)", SELECTION
            )
            report, node = wait(candidate, "selected real Index preview did not render", 60)
            translation = node["value"]["value"]
            sample(stage, SELECTION, translation)
            assert "https://example.com" in translation
            before = capture("selection-before")
            assert before["page"]["fields"]["source"]["start"] == 5
            checkpoint = len(rows())
            wait(permit, "selection candidate has no current permit")
            press_tab()
            expected = PREFIX + translation + SUFFIX
            wait(
                lambda: state()["fields"]["source"]["text"] == expected
                and terminal_write(checkpoint),
                "selection acceptance did not preserve the outside text",
            )
            accepted = capture("selection-accepted")
            caret = len((PREFIX + translation).encode("utf-16-le")) // 2
            assert accepted["page"]["fields"]["source"] == {
                "text": expected,
                "start": caret,
                "end": caret,
            }
            assert len(accepted["page"]["events"]) == len(before["page"]["events"]) + 1
            stages[stage] = "passed"

            stage = "direct"
            assert control(paths, "settings") is True
            settings_window = wait(
                lambda: next(
                    (
                        item
                        for item in contents(request("Windows"), "Windows")
                        if item.get("pid") == process.pid and item.get("title") == "TypeLay 设置"
                    ),
                    None,
                ),
                "owned settings window did not map",
            )
            request({"Action": {"FocusWindow": {"id": settings_window["id"]}}})
            select_settings_page(process.pid, "通用")
            mode = wait(
                lambda: named(process.pid, "设置：工作模式：按需"), "OnDemand control missing"
            )
            assert Atspi.Action.do_action(mode.get_action_iface(), 0)
            wait(
                lambda: json.loads(paths.settings.read_text())["Mode"] == 1,
                "OnDemand setting did not save",
            )
            save("settings-saved.json", json.loads(paths.settings.read_text()))
            request({"Action": {"CloseWindow": {"id": settings_window["id"]}}})
            request({"Action": {"FocusWindow": {"id": window["id"]}}})
            insert("Keep field B unchanged. 🙂", "other")
            insert(DIRECT)
            wait(
                lambda: not status().get("overlayVisible") and status().get("sourceSupported"),
                "OnDemand source did not remain dormant and supported",
            )
            before = capture("direct-before")
            checkpoint = len(rows())
            # Observe only command identity and times at the real extension
            # ports. These listeners neither change nor replay any message.
            worker.evaluate("""() => {
              globalThis.journeyCommandTimes=[];
              native.onMessage.addListener(m=>{
                if(m.op==='trigger-shortcut' && journeyCommandTimes.length<16)
                  journeyCommandTimes.push({channel:'request',kind:m.data.kind,
                    triggeredAtMs:m.data.triggeredAtMs,generation:m.data.generation});
              });
              for(const port of documents.values()) port.onMessage.addListener(m=>{
                if(m.event==='shortcut' && journeyCommandTimes.length<16)
                  journeyCommandTimes.push({channel:'event',kind:m.data.kind,
                    acceptedAtMs:m.data.acceptedAtMs,triggeredAtMs:m.data.triggeredAtMs,
                    generation:m.data.generation,observedAtMs:m.data.native?.observedAtMs});
              });
            }""")
            try:
                command(app, "--direct")
                wait(
                    lambda: status().get("serviceStatus")
                    == "正在直接翻译；可以切换输入框，当前操作仍绑定原输入框。",
                    "direct did not freeze original A before focus moved",
                )
            finally:
                save("direct-command-times.json", worker.evaluate("journeyCommandTimes"))
            page.locator("#other").click()
            page.locator("#other").evaluate("element => element.setSelectionRange(5, 12)")
            detached = state()
            assert detached["fields"]["source"] == before["page"]["fields"]["source"]
            assert detached["active"] == "other"
            save("direct-detached.json", detached)
            wait(
                lambda: status().get("serviceStatus") == "已直接替换原输入框文字。",
                "background direct did not reach Verified completion",
                60,
            )
            completed = capture("direct-completed")
            after = completed["page"]
            assert after["active"] == "other" and after["focused"]
            assert after["fields"]["other"] == detached["fields"]["other"]
            source = after["fields"]["source"]
            assert source["start"] == source["end"] == len(source["text"].encode("utf-16-le")) // 2
            events = after["events"][len(before["page"]["events"]) :]
            assert len(events) == 1 and events[0]["id"] == "source"
            assert events[0]["active"] == "other" and events[0]["b"] == detached["fields"]["other"]
            direct_rows = [
                row for row in rows()[checkpoint:] if row["operation"].startswith("direct-write-")
            ]
            assert [row["operation"] for row in direct_rows] == [
                "direct-write-start",
                "direct-write-result",
            ]
            assert direct_rows[-1]["detail"]["status"] == "Verified"
            assert direct_rows[0]["planId"] == direct_rows[1]["planId"]
            save("direct-receipt.json", direct_rows)
            sample(stage, DIRECT, after["fields"]["source"]["text"])
            stages[stage] = "passed"
            save("doctor-after.json", json.loads(command(app, "--doctor")))
    except BaseException as error:
        stages[stage] = "failed"
        result["failure"] = {"stage": stage, "type": type(error).__name__, "message": str(error)}
        if page is not None and not page.is_closed():
            capture("failure")
        raise
    finally:
        if process is not None:
            try:
                result["quitRequested"] = control(paths, "quit") is True
                result["desktopExitCode"] = process.wait(timeout=7)
            finally:
                result["desktopExited"] = process.poll() is not None
        save("result.json", result)
