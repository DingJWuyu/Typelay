"""Runner snapshot/request contracts only; these do not prove native GUI behavior."""

import sys
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from gtk_session_reentry import language_control_focused, render_preview  # noqa: E402


class LanguageFocusObservationTests(unittest.TestCase):
    def test_exact_attached_popup_focus_is_not_button_focus(self):
        # GTK3's real attached popup is not a descendant of its toggle button.
        snapshot = {
            "visible": True,
            "focused": False,
            "buttonFocused": False,
            "popupVisible": True,
            "popupFocused": True,
        }
        self.assertTrue(language_control_focused(snapshot))
        for changed in ({"visible": False}, {"popupVisible": False}, {"popupFocused": False}):
            with self.subTest(changed=changed):
                self.assertFalse(language_control_focused({**snapshot, **changed}))
        self.assertTrue(language_control_focused({"visible": True, "buttonFocused": True}))
        self.assertFalse(language_control_focused({"visible": True, "focused": True}))
        self.assertFalse(language_control_focused({}))


class PreviewDeadlineRequestTests(unittest.TestCase):
    def test_expired_deadline_is_replayed_without_reminting(self):
        client = Mock(hello={"backend": "gtk4", "lifetime": "fixture"})
        source = {"target": "source", "authority": {"inputRevision": 1}}
        evidence = {}
        with patch("gtk_session_reentry.now_ms", return_value=2000):
            render_preview(
                client, source, "generation", 4, "exit", expires_at=1000, evidence=evidence
            )
        self.assertEqual(client.call.call_args.args[1]["expiresAtMs"], 1000)
        self.assertEqual(evidence["expiresAtMs"], 1000)
        self.assertEqual(client.call.call_args.args[1]["revision"], 4)
        self.assertEqual(client.call.call_args.args[1]["authority"], source["authority"])

    def test_normal_new_render_retains_its_existing_eight_hundred_ms_window(self):
        client = Mock(hello={"backend": "gtk3", "lifetime": "fixture"})
        with patch("gtk_session_reentry.now_ms", return_value=2000):
            render_preview(client, {"target": "source", "authority": {}}, "generation", 5, "exit")
        self.assertEqual(client.call.call_args.args[1]["expiresAtMs"], 2800)


if __name__ == "__main__":
    unittest.main()
