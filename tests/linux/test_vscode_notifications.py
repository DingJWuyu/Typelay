"""Check the VS Code consumer's bounded observation through real Playwright DOM APIs."""

import html
import os
import shutil
import tempfile
import unittest

from playwright.sync_api import sync_playwright
from vscode_e2e import visible_terminal_notification


class NotificationObservationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        executable = shutil.which("chromium")
        if not executable:
            raise RuntimeError("This DOM regression requires an existing Chromium executable")
        home = tempfile.TemporaryDirectory(prefix="typelay-notification-dom-")
        cls.addClassCleanup(home.cleanup)
        cls.playwright = sync_playwright().start()
        cls.addClassCleanup(cls.playwright.stop)
        cls.browser = cls.playwright.chromium.launch(
            executable_path=executable,
            headless=True,
            env={**os.environ, "HOME": home.name},
        )
        cls.addClassCleanup(cls.browser.close)

    def setUp(self):
        self.page = self.browser.new_page()
        self.addCleanup(self.page.close)

    def notification(self, message, source="TypeLay Editor", hidden=False):
        # VS Code notificationsList.ts supplies the row label even when the
        # notificationsViewer.ts details/source row is collapsed and empty.
        owner = f", source: {source}" if source is not None else ""
        label = f"Warning: {message}{owner}, notification"
        return (
            '<div class="notifications-list-container">'
            f'<div class="monaco-list-row" aria-label="{html.escape(label, quote=True)}"'
            + (' style="display:none"' if hidden else "")
            + '><div class="notification-list-item">'
            '<div class="notification-list-item-source"></div>'
            f'<div class="notification-list-item-message"><span>{html.escape(message)}</span>'
            "</div></div></div></div>"
        )

    def test_visible_owned_terminal_message_returns_finite_state(self):
        self.page.set_content(
            self.notification("TypeLay：本机模型未就绪。请先在 TypeLay 中完成模型安装。")
        )
        self.assertEqual(visible_terminal_notification(self.page), "not-ready")

    def test_hidden_or_unowned_notices_do_not_stop_waiting(self):
        message = "TypeLay：本机模型未就绪。请先在 TypeLay 中完成模型安装。"
        for source, hidden in [
            ("TypeLay Editor", True),
            (None, False),
            ("Other Extension", False),
            ("TypeLay Editor Tools", False),
        ]:
            with self.subTest(source=source, hidden=hidden):
                self.page.set_content(self.notification(message, source=source, hidden=hidden))
                self.assertIsNone(visible_terminal_notification(self.page))

    def test_editor_alerts_progress_and_unknown_notices_do_not_stop_waiting(self):
        message = "TypeLay：本机模型未就绪。请先在 TypeLay 中完成模型安装。"
        for content in [
            f'<div class="monaco-editor">{message}</div>',
            f'<div role="alert">{message}</div>',
            self.notification("TypeLay 正在本机翻译"),
            self.notification("TypeLay：请先完成或取消当前翻译。"),
            self.notification(message + " 额外正文"),
        ]:
            with self.subTest(content=content):
                self.page.set_content(content)
                self.assertIsNone(visible_terminal_notification(self.page))

    def test_owned_label_alone_does_not_classify_an_unrelated_visible_message(self):
        self.page.set_content(
            self.notification("TypeLay：本机模型未就绪。请先在 TypeLay 中完成模型安装。")
        )
        self.page.locator(".notification-list-item-message").evaluate(
            "node => node.textContent = 'Unrelated visible text'"
        )
        self.assertIsNone(visible_terminal_notification(self.page))

    def test_hidden_copy_does_not_mask_a_visible_terminal_notice(self):
        message = "TypeLay：本机模型没有为当前文本提供译文，原文未修改。"
        self.page.set_content(self.notification(message, hidden=True) + self.notification(message))
        self.assertEqual(visible_terminal_notification(self.page), "no-translation")


if __name__ == "__main__":
    unittest.main()
