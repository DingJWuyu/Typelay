"""The public settings adapter sends one backend key sequence, not physical input."""

import ctypes
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from support import gtk_accessibility as access


class ShortcutInputTests(unittest.TestCase):
    def test_recording_sends_one_x11_sequence_after_focus_and_action(self):
        for stored, expected in (
            ("Esc", "Escape"),
            ("Ctrl+Esc", "ctrl+Escape"),
            ("Ctrl+Shift+F8", "ctrl+shift+F8"),
        ):
            with self.subTest(stored=stored):
                events = []
                node = SimpleNamespace(
                    clear_cache=lambda: None,
                    get_state_set=lambda: SimpleNamespace(contains=lambda _state: True),
                    get_role=lambda: "button",
                    get_component_iface=lambda: "component",
                    get_action_iface=lambda: "action",
                    get_description=lambda: "按下按键…",
                )
                atspi = SimpleNamespace(
                    Role=SimpleNamespace(PUSH_BUTTON="button"),
                    StateType=SimpleNamespace(SHOWING="showing", SENSITIVE="sensitive"),
                    Component=SimpleNamespace(
                        grab_focus=lambda _component: events.append("focus") or True
                    ),
                    Action=SimpleNamespace(
                        do_action=lambda _action, _index: events.append("action") or True
                    ),
                )
                with patch.multiple(
                    access, Atspi=atspi, named=lambda _pid, _name: node, focused=lambda _node: True
                ):
                    result = access.record_shortcut(
                        42, "退出当前翻译", stored, lambda value: events.append(("key", value))
                    )
                self.assertIs(result, node)
                self.assertEqual(events, ["focus", "action", ("key", expected)])
                # X11's actual symbol database accepts the emitted base name;
                # the product spelling Esc is not itself an X11 keysym.
                x11 = ctypes.CDLL("libX11.so.6")
                x11.XStringToKeysym.argtypes = [ctypes.c_char_p]
                x11.XStringToKeysym.restype = ctypes.c_ulong
                self.assertNotEqual(x11.XStringToKeysym(expected.split("+")[-1].encode()), 0)


if __name__ == "__main__":
    unittest.main()
