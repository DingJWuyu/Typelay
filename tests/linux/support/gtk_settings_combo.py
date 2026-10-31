"""Public AT-SPI interaction with an owned GTK3 settings ComboBox menu.

GTK menu items expose native MenuShell selection, not widget keyboard focus.
The popup has its own POPUP layer and clipping rectangle; its logical ComboBox
parent is not a drawing viewport. Ordinary focused-control helpers stay strict.
"""

import time

import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi  # noqa: E402


class SettingsComboMenu:
    """One explicit opening, with a single optional menu-item action."""

    def __init__(self, pid, combo, deadline):
        self.pid, self.combo, self.deadline = pid, combo, deadline
        self.item_actions = 0
        self.original_name = combo.get_name()
        assert combo.get_role() == Atspi.Role.COMBO_BOX and self.shown(combo)
        area, node, seen = self.rectangle(combo), combo.get_parent(), {combo}
        for _ in range(24):
            assert node is not None and node.get_process_id() == pid and node not in seen
            seen.add(node)
            self.states(node)
            role = node.get_role()
            if role in (Atspi.Role.VIEWPORT, Atspi.Role.SCROLL_PANE, Atspi.Role.FRAME):
                clip = self.rectangle(node)
                assert self.contains(clip, area), (
                    f"settings combo is clipped: role={role}, clip={clip}, target={area}"
                )
            if role == Atspi.Role.FRAME:
                assert node.get_name() == "TypeLay 设置"
                assert self.states(node).contains(Atspi.StateType.ACTIVE)
                self.frame = node
                break
            node = node.get_parent()
        else:
            raise AssertionError("settings combo exceeds bounded owner ancestry")
        self.initial = Atspi.Selection.get_selected_child(combo.get_selection_iface(), 0)
        assert self.initial is not None and self.initial.get_name() == self.original_name
        action = combo.get_action_iface()
        assert Atspi.Action.get_n_actions(action) == 1
        assert Atspi.Action.get_action_name(action, 0) == "press"
        assert time.monotonic() < self.deadline, "settings combo opening deadline expired"
        assert Atspi.Action.do_action(action, 0)
        self.menu = combo.get_child_at_index(0)
        self.wait(lambda: self.shown(self.menu), "settings combo popup did not show")

    @staticmethod
    def states(node):
        node.clear_cache()
        return node.get_state_set()

    @staticmethod
    def rectangle(node):
        rect = Atspi.Component.get_extents(node.get_component_iface(), Atspi.CoordType.SCREEN)
        return rect.x, rect.y, rect.width, rect.height

    @staticmethod
    def contains(outer, inner):
        x, y, width, height = inner
        left, top, outer_width, outer_height = outer
        return (
            min(width, height, outer_width, outer_height) > 0
            and -(2**31) not in (x, y, left, top)
            and left <= x <= left + outer_width - width
            and top <= y <= top + outer_height - height
        )

    def shown(self, node):
        states = self.states(node)
        return node.get_process_id() == self.pid and all(
            states.contains(value)
            for value in (
                Atspi.StateType.SHOWING,
                Atspi.StateType.ENABLED,
                Atspi.StateType.SENSITIVE,
            )
        )

    def wait(self, predicate, message):
        while time.monotonic() < self.deadline:
            if predicate():
                return
            time.sleep(0.025)
        raise AssertionError(message)

    def current(self):
        combo, menu = self.combo, self.menu
        if (
            time.monotonic() >= self.deadline
            or not self.shown(combo)
            or not self.shown(menu)
            or combo.get_role() != Atspi.Role.COMBO_BOX
            or combo.get_name() != self.original_name
            or menu.get_role() != Atspi.Role.MENU
            or menu.get_parent() != combo
            or combo.get_child_at_index(0) != menu
            or Atspi.Component.get_layer(menu.get_component_iface()) != Atspi.ComponentLayer.POPUP
        ):
            return False
        selection = menu.get_selection_iface()
        if selection is None or Atspi.Selection.get_n_selected_children(selection) != 1:
            return False
        current = Atspi.Selection.get_selected_child(selection, 0)
        return (
            current == self.initial
            and current.get_parent() == menu
            and self.shown(current)
            and self.states(current).contains(Atspi.StateType.SELECTED)
        )

    def select(self, title):
        if self.item_actions or not self.current():
            return False
        assert 0 < self.menu.get_child_count() <= 64
        matches = []
        for index in range(self.menu.get_child_count()):
            item = self.menu.get_child_at_index(index)
            if item.get_name() == title and self.shown(item):
                matches.append(item)
        assert len(matches) == 1, "ambiguous or unavailable settings menu item: " + title
        item = matches[0]
        assert item.get_role() == Atspi.Role.MENU_ITEM and item.get_parent() == self.menu
        assert self.contains(self.rectangle(self.menu), self.rectangle(item)), "menu item clipped"
        assert self.current() and self.shown(item), "settings popup retired before action"
        action = item.get_action_iface()
        assert Atspi.Action.get_n_actions(action) == 1
        assert Atspi.Action.get_action_name(action, 0) == "click"
        assert self.current(), "settings popup retired while reading its action"
        assert time.monotonic() < self.deadline, "settings combo selection deadline expired"
        self.item_actions += 1
        assert Atspi.Action.do_action(action, 0)

        def selected_and_closed():
            self.combo.clear_cache()
            selected = Atspi.Selection.get_selected_child(self.combo.get_selection_iface(), 0)
            return (
                selected is not None
                and selected.get_name() == title
                and not self.states(self.menu).contains(Atspi.StateType.SHOWING)
                and self.states(self.frame).contains(Atspi.StateType.ACTIVE)
            )

        self.wait(selected_and_closed, "menu selection did not close and restore its owner")
        return True
