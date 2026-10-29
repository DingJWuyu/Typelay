"""Exercise the real native settings widgets against an isolated production host.

No model, desktop input or user settings are used. This verifies presentation
and settings persistence, not translation quality or source-field writeback.
"""

import argparse
import asyncio
import json
import os
import socket
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))


def main():
    import gi

    gi.require_version("Gtk", "3.0")
    gi.require_version("Gdk", "3.0")
    from types import SimpleNamespace

    from gi.events import GLibEventLoop
    from gi.repository import Gdk, GLib, Gtk
    from link import Link
    from registered_applications import list_managed_applications
    from tray import Tray
    from ui import Settings

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", type=Path, required=True)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--inspect", action="store_true")
    options = parser.parse_args()
    assert Gtk.init_check([])[0], "GTK display unavailable"
    options.output.mkdir(parents=True, exist_ok=True)

    async def run():
        temporary = tempfile.TemporaryDirectory(prefix="typelay-settings-ui-")
        directory = Path(temporary.name)
        (directory / "config").mkdir()
        (directory / "data").mkdir()
        (directory / "config/settings.json").write_text(
            json.dumps({"UiLanguage": "zh-CN", "OnboardingComplete": True, "Startup": False})
        )
        parent, child = socket.socketpair()
        log = (options.output / "host.log").open("w")
        process = subprocess.Popen(
            [
                str(options.dotnet),
                str(options.host),
                str(ROOT),
                str(directory / "config"),
                str(directory / "data"),
            ],
            stdin=child,
            stdout=child,
            stderr=log,
            env={**os.environ, "DOTNET_ROOT": str(options.dotnet.parent)},
        )
        child.close()
        surface = tray = None
        state, guidance, status = {}, {}, {}
        jobs = set()
        failures = []
        closed = asyncio.Event()

        def notification(operation, value):
            if operation == "settings":
                state.update(value)
                if surface:
                    surface.configure(value)
                if tray:
                    tray.configure(value)
            elif operation == "settingsGuidance":
                guidance.update(value)
                if surface:
                    surface.render_guidance(value)
                if tray:
                    tray.render_guidance(value)
            elif operation == "status":
                status.update(value)
                if surface:
                    surface.report(value)
                if tray:
                    tray.report(value)

        def tasks(coroutine):
            task = asyncio.create_task(coroutine)
            jobs.add(task)

            def finished(future):
                jobs.discard(future)
                if not future.cancelled() and future.exception():
                    failures.append(repr(future.exception()))
                    print("TASK ERROR", repr(future.exception()), flush=True)

            task.add_done_callback(finished)
            return task

        host = Link(GLib, parent.fileno(), parent.fileno(), notification, None, closed.set)

        async def support_refresh():
            return await host.ask("applicationSupport", {"facts": []}, timeout=2)

        async def support_action(identity, action, *args, **kwargs):
            if action == "ListManagedApplications":
                return list_managed_applications()
            raise ValueError("No desktop connection is available in this presentation test.")

        async def preview(_source):
            raise ValueError("No model installed.")

        async def settle(predicate=lambda: not jobs):
            async with asyncio.timeout(8):
                while not predicate():
                    await asyncio.sleep(0.025)
            await asyncio.sleep(0.1)
            assert not failures, failures

        def capture(name):
            window = surface.window.get_window()
            width, height = surface.window.get_size()
            Gdk.pixbuf_get_from_window(window, 0, 0, width, height).savev(
                str(options.output / (name + ".png")), "png", [], []
            )

        try:
            assert await host.ask("start", timeout=5) is True
            surface = Settings(
                host,
                tasks,
                closed.set,
                lambda *_args, **_kwargs: None,
                lambda: None,
                preview,
                support_refresh,
                support_action,
                lambda: None,
            )
            surface.configure(state)
            surface.render_guidance(guidance)
            surface.report(status)
            surface.window.connect("destroy", lambda *_: closed.set())
            surface.show()
            await settle()
            print(
                "WINDOW REQUEST",
                tuple(surface.window.get_default_size()),
                tuple(surface.window.get_preferred_width()),
                tuple(surface.window.get_preferred_height()),
                flush=True,
            )
            print("WINDOW STATE", int(surface.window.get_window().get_state()), flush=True)
            surface.pages.set_transition_duration(0)
            for name in (
                "general",
                "appearance",
                "language",
                "glossary",
                "shortcuts",
                "application-support",
                "model",
                "about",
            ):
                surface.pages.set_visible_child_name(name)
                await asyncio.sleep(0.25)
                print(
                    "PAGE",
                    name,
                    "size",
                    tuple(surface.window.get_size()),
                    "preferred",
                    tuple(surface.pages.get_visible_child().get_preferred_width()),
                    flush=True,
                )
                capture(name)
            surface.pages.set_visible_child_name("general")
            assert not status.get("modelReady"), "No model may be simulated as ready"
            # Actual control signals must commit through the host, then round-trip.
            surface.preference_options["accent"]["ocean"].set_active(True)
            await settle(lambda: state.get("accent") == "ocean")
            surface.controls["reduceMotion"].set_active(True)
            await settle(lambda: state.get("reduceMotion") is True)
            surface.controls["textSize"].set_value(18)
            await settle(lambda: state.get("textSize") == 18)
            assert await host.ask("togglePause") is True
            await settle(lambda: state.get("enabled") is False)
            assert await host.ask("togglePause") is True
            await settle(lambda: state.get("enabled") is True)
            saved = json.loads((directory / "config/settings.json").read_text())
            assert (
                saved["Accent"] == "ocean"
                and saved["TextSize"] == 18
                and saved["ReduceMotion"] is True
            )
            assert await host.ask("settingsPatch", {"textSize": 99}) is False
            assert state["textSize"] == 18
            # CRUD is available offline, but never grants model application quality.
            translation = surface.translation
            assert translation.glossary_state["isReleased"] is False
            translation.new_button.clicked()
            translation.source_term.set_text("TypeLay")
            translation.target_term.set_text("TypeLay")
            translation.add.clicked()
            await settle(lambda: not translation.dirty and not translation.saving)
            glossary = await host.ask("getGlossarySettings")
            assert glossary["savedSnapshot"]["rules"][0]["sourceTerm"] == "TypeLay"
            assert glossary["canApply"] is False
            translation.search.set_text("no match")
            await settle(lambda: not translation.rules.get_children()[0].get_child_visible())
            translation.search.set_text("")
            translation.remove_rule(0)
            await settle(lambda: not translation.dirty and not translation.saving)
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"] == []
            surface.toast.undo.clicked()
            await settle()
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"][0][
                "sourceTerm"
            ] == "TypeLay"
            # An app exclusion and its toast undo only change that app's policy.
            surface.toggle_application("fixture-editor", False)
            await settle(lambda: "fixture-editor" in (state.get("excludedApplications") or []))
            await settle()
            surface.toast.undo.clicked()
            await settle(lambda: "fixture-editor" not in (state.get("excludedApplications") or []))
            # Recording a new key saves immediately after its complete combination.
            surface.controls["pauseShortcut"].set_text("Ctrl+Shift+F11")
            surface.draft_changed("pauseShortcut")
            await settle(lambda: state.get("pauseShortcut") == "Ctrl+Shift+F11")
            # A renderer must never silently save fallback values on reopen.
            surface.dismiss()
            surface.show()
            await settle()
            assert state["textSize"] == 18
            surface.pages.set_visible_child_name("appearance")
            capture("appearance-saved")
            # The tray renders the real native quick panel, not a Gtk.Menu.
            tray = Tray(
                SimpleNamespace(home=ROOT / "src/linux", session=Path("settings-ui")),
                host,
                tasks,
                surface.show,
                closed.set,
                lambda *_: None,
            )
            tray.configure(state)
            tray.render_guidance(guidance)
            tray.report(status)
            tray.show_panel()
            await settle()
            await asyncio.sleep(0.15)
            tray_window = tray.window.get_window()
            width, height = tray.window.get_size()
            Gdk.pixbuf_get_from_window(tray_window, 0, 0, width, height).savev(
                str(options.output / "quick-panel.png"), "png", [], []
            )
            assert tray.window.get_visible() and not isinstance(tray.panel, Gtk.Menu)
            tray.enabled.set_active(False)
            await settle(lambda: state.get("enabled") is False)
            tray.enabled.set_active(True)
            await settle(lambda: state.get("enabled") is True)
            tray.hide_panel()
            surface.tutorial.show()
            await asyncio.sleep(0.15)
            window = surface.tutorial.window.get_window()
            width, height = surface.tutorial.window.get_size()
            Gdk.pixbuf_get_from_window(window, 0, 0, width, height).savev(
                str(options.output / "tutorial.png"), "png", [], []
            )
            surface.tutorial.dismiss()
            # Switching product chrome never translates user-provided terms.
            translation.new_button.clicked()
            translation.source_term.set_text("关闭")
            translation.target_term.set_text("Close")
            translation.add.clicked()
            await settle(lambda: not translation.saving and not translation.dirty)
            surface.controls["uiLanguage"].set_active_id("en")
            await settle(lambda: state.get("uiLanguage") == "en")
            assert surface.page_headings["general"].get_text() == "General"
            assert surface.tutorial.next.get_label() == "Continue"
            assert tray.actions["设置…"].get_label() == "Settings…"
            assert any(
                rule["sourceTerm"] == "关闭"
                for rule in (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"]
            )
            for page in (
                "general",
                "appearance",
                "language",
                "glossary",
                "shortcuts",
                "application-support",
                "model",
                "about",
            ):
                surface.pages.set_visible_child_name(page)
                await asyncio.sleep(0.1)
                capture(page + "-en")
            surface.show_language_picker()
            await settle()
            assert surface.language_dialog.get_visible()
            surface.language_dialog.destroy()
            (options.output / "result.json").write_text(
                json.dumps(
                    {
                        "result": "passed",
                        "modelReady": False,
                        "pages": 8,
                        "checks": [
                            "real-host-acknowledged-preferences",
                            "pause-resume",
                            "invalid-preference-rejected",
                            "reopen-preserves-settings",
                            "glossary-crud-and-undo",
                            "application-disable-and-undo",
                            "quick-panel-pause-resume",
                            "tutorial-native-render",
                            "english-chrome-keeps-user-terms",
                            "language-search-picker",
                        ],
                        "failures": failures,
                    },
                    indent=2,
                )
            )
            print("SETTINGS UI PASS", flush=True)
            if options.inspect:
                async with asyncio.timeout(1800):
                    await closed.wait()
        finally:
            if tray:
                tray.close()
            if surface:
                surface.window.destroy()
                surface.tutorial.window.destroy()
            for task in jobs:
                task.cancel()
            if jobs:
                await asyncio.gather(*jobs, return_exceptions=True)
            try:
                host.send("quit")
            except (OSError, EOFError):
                pass
            parent.close()
            try:
                await asyncio.to_thread(process.wait, 5)
            except subprocess.TimeoutExpired:
                process.terminate()
                await asyncio.to_thread(process.wait, 5)
            log.close()
            temporary.cleanup()

    try:
        with asyncio.Runner(
            loop_factory=lambda: GLibEventLoop(GLib.MainContext.default())
        ) as runner:
            runner.run(run())
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
