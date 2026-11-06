"""Production settings callbacks with controlled Host and rendering ports.

These check outcome delivery, not GTK pixels, focus, or a desktop session.
"""

import asyncio
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from overlay import Overlay  # noqa: E402
from tray import Tray  # noqa: E402
from ui import Settings, Tutorial  # noqa: E402

NOTICE = "已保存，请重新打开设置确认当前状态。"


class Host:
    async def ask(self, operation, data=None):
        return {
            "saved": True,
            "state": "CommittedNotificationFailed",
            "revision": 2,
            "startup": "NotRequested",
            "message": NOTICE,
        }


class SettingsFeedbackTests(unittest.IsolatedAsyncioTestCase):
    async def test_saved_tutorial_closes_after_delivering_its_shared_warning(self):
        reports = []
        tutorial = Tutorial.__new__(Tutorial)
        tutorial.owner = SimpleNamespace(
            host=Host(), settings_lock=asyncio.Lock(), report_settings_commit=reports.append
        )
        opening, token = object(), object()
        tutorial.pending = token
        tutorial.current = lambda *_: tutorial.pending is token

        def dismiss():
            tutorial.pending = None

        tutorial.dismiss = dismiss
        await tutorial.finish(opening, 1, token)
        self.assertIsNone(tutorial.pending)
        self.assertEqual(reports[0]["message"], NOTICE)

    async def test_tray_keeps_the_shared_notification_failure_after_a_known_commit(self):
        tray = Tray.__new__(Tray)
        tray.host = Host()
        tray.value = {"enabled": True}
        tray.closed = False
        tray.pending = True
        tray.save_problem = None
        tray.refresh = lambda: None
        await tray.save({"enabled": False})
        self.assertFalse(tray.value["enabled"])
        self.assertEqual(tray.save_problem, NOTICE)
        self.assertFalse(tray.pending)

    async def test_language_swap_keeps_the_shared_notification_failure_after_a_known_commit(self):
        settings = Settings.__new__(Settings)
        settings.host = Host()
        settings.value = {"source": "zh", "target": "en"}
        settings.settings_lock = asyncio.Lock()
        settings.swapping_languages = True
        settings.settings_feedback_owned = False
        notices = []
        settings.message = SimpleNamespace(set_text=notices.append)
        settings.configure = lambda value: None
        await settings.save_language_swap()
        self.assertEqual(notices, [NOTICE])
        self.assertFalse(settings.swapping_languages)

    async def test_retired_overlay_delivers_the_saved_warning_without_reviving_its_view(self):
        reports = []
        input_service = SimpleNamespace(
            glib=SimpleNamespace(timeout_add=lambda *_: 1),
            settings_commit_reported=reports.append,
        )
        overlay = Overlay(Host(), input_service, None, lambda: None)
        overlay.settings = {"source": "zh", "target": "en"}
        overlay.value = {"status": ""}
        overlay.language_choices = {"source": [{"code": "en"}], "target": [{"code": "zh"}]}

        async def ask(operation, data=None):
            overlay.value = None
            return await Host().ask(operation, data)

        overlay.host = SimpleNamespace(ask=ask)
        await overlay.set_languages({"source": "en", "target": "zh"})
        self.assertEqual(reports[0]["message"], NOTICE)
        self.assertIsNone(overlay.value)


if __name__ == "__main__":
    unittest.main()
