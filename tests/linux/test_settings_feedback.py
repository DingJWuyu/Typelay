"""GTK feedback controls and overlay delivery with a controlled Host port.

Run the GTK cases in an isolated display and D-Bus session. They exercise native
widgets, not physical input, a desktop notification server, or real persistence.
"""

import asyncio
import os
import sys
import tempfile
import unittest
from copy import deepcopy
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from overlay import Overlay  # noqa: E402
from tray import Tray  # noqa: E402
from ui import Settings  # noqa: E402

NOTICE = "已保存，请重新打开设置确认当前状态。"
LANGUAGES = {
    "source": [{"code": "zh", "label": "中文"}, {"code": "en", "label": "英语"}],
    "target": [{"code": "zh", "label": "中文"}, {"code": "en", "label": "英语"}],
}
SETTINGS = {
    "enabled": True,
    "mode": "Automatic",
    "startup": False,
    "experimentalWriteback": False,
    "source": "zh",
    "target": "en",
    "theme": "light",
    "uiLanguage": "zh-CN",
    "reduceMotion": True,
    "acceptShortcut": "Tab",
    "exitShortcut": "Esc",
    "wakeShortcut": "Ctrl+Shift+F9",
    "directShortcut": "Ctrl+Shift+F10",
    "excludedApplications": [],
    "onboardingComplete": True,
}
GUIDANCE = {
    "languageChoices": LANGUAGES,
    "modeDescription": "自动翻译",
    "acceptanceDescription": "按 Tab 接受当前译文。",
}


class Host:
    def __init__(self):
        self.commits = []

    async def ask(self, operation, data=None, *, timeout=None):
        if operation == "getStyleSettings":
            return {
                "settingsRevision": 1,
                "isReleased": False,
                "canChange": False,
                "savedPreferences": {"default": None, "applications": {}},
            }
        if operation == "getGlossarySettings":
            return {
                "settingsRevision": 1,
                "isReleased": False,
                "canApply": False,
                "savedSnapshot": {"rules": []},
            }
        if operation == "settingsDraft":
            return {}
        if operation == "sessionShortcutSettings":
            return {"acceptLabel": "接受译文", "exitLabel": "退出当前翻译", "scopeNote": ""}
        if operation == "translationPreparation":
            return {
                "statusText": "尚未准备模型",
                "guidance": "",
                "primaryAction": {"id": "NextStep", "label": "下一步", "enabled": False},
                "secondaryActions": [],
                "directions": [],
                "canSelectDirections": False,
            }
        if operation == "getApplicationList":
            return {"settingsRevision": 1, "applications": []}
        if operation not in ("dismissOnboarding", "traySettings"):
            raise AssertionError(f"Unexpected Host operation: {operation}")
        self.commits.append((operation, deepcopy(data)))
        return {
            "saved": True,
            "state": "CommittedNotificationFailed",
            "revision": 2,
            "startup": "NotRequested",
            "message": NOTICE,
        }


class GtkFeedbackTests(unittest.IsolatedAsyncioTestCase):
    @classmethod
    def setUpClass(cls):
        private = tempfile.TemporaryDirectory(prefix="typelay-settings-feedback-")
        cls.addClassCleanup(private.cleanup)
        root = Path(private.name)
        environment = {
            "HOME": str(root),
            "XDG_CONFIG_HOME": str(root / "config"),
            "XDG_DATA_HOME": str(root / "data"),
            "XDG_CACHE_HOME": str(root / "cache"),
            "GSETTINGS_BACKEND": "memory",
            "GIO_USE_VFS": "local",
            "GTK_IM_MODULE": "simple",
        }
        isolated = patch.dict(os.environ, environment)
        isolated.start()
        cls.addClassCleanup(isolated.stop)

    async def asyncSetUp(self):
        self.assertTrue(Gtk.init_check()[0], "GTK feedback tests require an isolated display")
        self.tasks = []
        self.host = Host()

    def schedule(self, coroutine):
        self.tasks.append(asyncio.create_task(coroutine))

    async def wait_for(self, condition, description):
        try:
            async with asyncio.timeout(3):
                while True:
                    while GLib.MainContext.default().pending():
                        GLib.MainContext.default().iteration(False)
                    for task in self.tasks:
                        if task.done():
                            task.result()
                    if condition():
                        return
                    await asyncio.sleep(0.005)
        except TimeoutError:
            self.fail(f"Timed out waiting for {description}")

    async def asyncTearDown(self):
        for task in self.tasks:
            if not task.done():
                task.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)
        while GLib.MainContext.default().pending():
            GLib.MainContext.default().iteration(False)

    async def open_settings(self):
        async def support_refresh():
            return None

        async def support_action(application, action):
            self.assertEqual((application, action), ("desktop-text", "ListManagedApplications"))
            return []

        settings = Settings(
            self.host,
            self.schedule,
            lambda: None,
            lambda *_: None,
            lambda: None,
            None,
            support_refresh,
            support_action,
            lambda *_: None,
        )
        self.addCleanup(settings.window.destroy)
        self.addCleanup(settings.tutorial.window.destroy)
        settings.configure(deepcopy(SETTINGS))
        settings.render_guidance(deepcopy(GUIDANCE))
        settings.show()
        await self.wait_for(
            lambda: settings.window.get_mapped() and all(task.done() for task in self.tasks),
            "settings opening",
        )
        self.assertEqual(settings.message.get_text(), "")
        return settings

    async def test_language_swap_shows_notification_warning_after_committing(self):
        settings = await self.open_settings()
        settings.sidebar.select_row(settings.nav_rows["language"])
        await self.wait_for(
            lambda: settings.language_swap.get_mapped() and settings.language_swap.is_sensitive(),
            "language swap button",
        )
        settings.language_swap.clicked()
        await self.wait_for(
            lambda: settings.message.get_text() == NOTICE and settings.message.get_mapped(),
            "visible settings save warning",
        )
        self.assertEqual(self.host.commits, [("traySettings", {"source": "en", "target": "zh"})])
        self.assertTrue(settings.language_swap.is_sensitive())
        self.assertTrue(settings.window.get_mapped())

    async def test_completed_tutorial_closes_and_leaves_its_warning_visible_in_settings(self):
        settings = await self.open_settings()
        settings.report({"modelReady": True, "modelInstalled": True})
        settings.open_tutorial()
        tutorial = settings.tutorial
        await self.wait_for(tutorial.window.get_mapped, "tutorial window")
        tutorial.window.get_window().focus(Gdk.CURRENT_TIME)
        await self.wait_for(tutorial.window.is_active, "tutorial keyboard focus")
        for page in range(1, 4):
            await self.wait_for(
                lambda: tutorial.next.get_mapped() and tutorial.next.is_sensitive(),
                "tutorial continue button",
            )
            tutorial.next.clicked()
            await self.wait_for(
                lambda: tutorial.steps.get_visible_child_name() == str(page),
                f"tutorial page {page}",
            )
        await self.wait_for(
            lambda: tutorial.complete.get_mapped() and tutorial.complete.is_sensitive(),
            "tutorial completion button",
        )
        tutorial.complete.clicked()
        await self.wait_for(
            lambda: not tutorial.window.get_mapped()
            and settings.message.get_text() == NOTICE
            and settings.message.get_mapped(),
            "closed tutorial and visible settings save warning",
        )
        self.assertEqual(self.host.commits, [("dismissOnboarding", None)])
        self.assertTrue(settings.window.get_mapped())

    async def test_tray_toggle_shows_notification_warning_after_committing(self):
        tray = Tray(
            SimpleNamespace(home=ROOT / "src/linux"),
            self.host,
            self.schedule,
            lambda: None,
            lambda: None,
            lambda *_: None,
        )
        self.addCleanup(tray.close)
        tray.configure(deepcopy(SETTINGS))
        tray.render_guidance(deepcopy(GUIDANCE))
        tray.show_panel()
        await self.wait_for(
            lambda: tray.enabled.get_mapped() and tray.enabled.is_sensitive(),
            "tray enable switch",
        )
        tray.enabled.set_active(False)
        await self.wait_for(
            lambda: tray.save_notice.get_text() == NOTICE and tray.save_notice.get_mapped(),
            "visible tray save warning",
        )
        self.assertEqual(self.host.commits, [("traySettings", {"enabled": False})])
        self.assertFalse(tray.enabled.get_active())
        self.assertTrue(tray.enabled.is_sensitive())


class OverlayFeedbackTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tasks = []
        self.reports = []
        self.presentations = []
        self.peer = object()
        record = SimpleNamespace(connection=self.peer)

        async def present_source(_record, payload):
            self.presentations.append(deepcopy(payload["view"]))
            return {"visible": True}

        self.input = SimpleNamespace(
            glib=SimpleNamespace(timeout_add=lambda *_: 1),
            language_configuration_open=False,
            cancel_language_configuration=lambda: None,
            current_presentation=lambda view: record if view else None,
            presentation_current=lambda value: value is record,
            presented=lambda *_: None,
            present_source=present_source,
            withdraw_presentation=lambda: None,
            conceal_source=lambda _: self.presentations.append(None),
            set_overlay_focus=lambda _: None,
            settings_commit_reported=self.reports.append,
        )

    def schedule(self, coroutine):
        self.tasks.append(asyncio.create_task(coroutine))

    async def drain(self):
        async with asyncio.timeout(3):
            while any(not task.done() for task in self.tasks):
                await asyncio.gather(*self.tasks)
        for task in self.tasks:
            task.result()

    async def asyncTearDown(self):
        for task in self.tasks:
            if not task.done():
                task.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)

    def view(self, sequence):
        return {
            "state": "Ready",
            "status": "",
            "sourceLanguage": "zh",
            "targetLanguage": "en",
            "viewContext": {
                "identity": {
                    "version": 1,
                    "interactionId": "00000000-0000-0000-0000-000000000001",
                    "displaySequence": sequence,
                    "draftRevision": 0,
                    "actionRevision": 1,
                },
                "token": {"revision": sequence},
            },
        }

    async def test_style_warning_reaches_settings_feedback_even_after_its_source_changes(self):
        for retired in (False, True):
            with self.subTest(source_retired=retired):
                self.reports.clear()
                initial, replacement = self.view(1), self.view(2)
                token = initial["viewContext"]["token"]
                report = {
                    "saved": True,
                    "code": "style-saved-notification-failed",
                    "message": NOTICE,
                }

                async def ask(operation, data=None):
                    self.assertEqual(operation, "sessionStyle")
                    self.assertEqual(data["token"], token)
                    if retired:
                        overlay.render(replacement)
                    return report

                overlay = Overlay(SimpleNamespace(ask=ask), self.input, self.schedule, lambda: None)
                overlay.render(initial)
                await self.drain()
                await overlay.set_session_style(
                    self.peer, overlay.revision, token, {"settingsRevision": 1, "style": None}
                )
                await self.drain()
                self.assertEqual(self.reports, [report])
                self.assertEqual(
                    self.presentations[-1]["viewContext"]["token"],
                    replacement["viewContext"]["token"] if retired else token,
                )

    async def test_retired_overlay_delivers_the_saved_warning_without_reviving_its_view(self):
        async def ask(operation, data=None):
            self.assertEqual(operation, "traySettings")
            self.assertEqual(data, {"source": "en", "target": "zh"})
            overlay.hide()
            return await Host().ask(operation, data)

        overlay = Overlay(SimpleNamespace(ask=ask), self.input, self.schedule, lambda: None)
        overlay.configure({"source": "zh", "target": "en"})
        overlay.render_guidance(deepcopy(GUIDANCE))
        overlay.render(self.view(1))
        await self.drain()
        self.assertIsNotNone(self.presentations[-1])
        await overlay.set_languages({"source": "en", "target": "zh"})
        await self.drain()
        self.assertEqual(len(self.reports), 1)
        self.assertEqual(self.reports[0]["message"], NOTICE)
        self.assertIsNone(self.presentations[-1])


if __name__ == "__main__":
    unittest.main()
