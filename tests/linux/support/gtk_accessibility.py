"""Observe owned GTK test windows through the real desktop accessibility API."""

import time
from collections import deque

import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi, GLib  # noqa: E402


def named(pid, name):
    desktop = Atspi.get_desktop(0)
    for index in range(desktop.get_child_count()):
        app = desktop.get_child_at_index(index)
        if app is None:
            continue  # The remote accessibility tree may still be changing.
        try:
            if app.get_process_id() != pid:
                continue
        except GLib.Error:
            continue  # An unrelated owned fixture may have just exited.
        # The unified Apps page exposes installed application rows. An actual
        # 797-node GTK tree puts navigation beyond the retired 256-node DFS cap.
        # Breadth-first observation reaches shallow chrome before list details;
        # keep an explicit finite limit and fail visibly if the fixture exceeds it.
        pending = deque([app])
        for _ in range(4096):
            if not pending:
                break
            node = pending.popleft()
            if node is None:
                continue  # A child can disappear between count and lookup.
            try:
                node.clear_cache()
                if node.get_name() == name:
                    return node
                pending.extend(node.get_child_at_index(i) for i in range(node.get_child_count()))
            except GLib.Error as error:
                if (
                    "as object /org/a11y/atspi/accessible/" not in error.message
                    or " does not exist" not in error.message
                ):
                    raise
                # A settings refresh can retire this exact proxy while its
                # siblings remain valid. The caller keeps its original deadline
                # and obtains a fresh tree on its next ordinary observation.
                continue
        else:
            if pending:
                raise AssertionError("owned GTK accessibility tree exceeds 4096 nodes")
    return None


def text(node):
    node.clear_cache()
    interface = node.get_text_iface()
    assert interface is not None, "owned GTK field did not expose its actual text interface"
    # Accessible.get_text is the deprecated interface getter with no offsets.
    # GI returns the same proxy object, so select the Text interface explicitly.
    return Atspi.Text.get_text(interface, 0, -1)


def focused(node):
    node.clear_cache()
    return node.get_state_set().contains(Atspi.StateType.FOCUSED)


def bounds(node, pid, expected_size, *, expected_role=None, expected_name=None):
    """Resolve an owned popup's bounds relative to its actual GTK frame.

    WINDOW can be relative to the popover's Gdk toplevel. Subtract the
    enclosing FRAME (or explicitly named DIALOG) in the same coordinate
    space instead; Wayland SCREEN coordinates are not desktop-global.
    """
    if expected_role is None:
        expected_role = Atspi.Role.FRAME
    assert expected_role in (Atspi.Role.FRAME, Atspi.Role.DIALOG)
    node.clear_cache()
    assert node.get_process_id() == pid, "candidate belongs to another process"
    assert node.get_state_set().contains(Atspi.StateType.SHOWING)
    area = Atspi.Component.get_extents(node.get_component_iface(), Atspi.CoordType.SCREEN)
    frame = node
    for _ in range(16):
        assert frame is not None and frame.get_process_id() == pid, "owned GTK frame unavailable"
        frame.clear_cache()
        if frame.get_role() in (Atspi.Role.FRAME, Atspi.Role.DIALOG):
            assert frame.get_role() == expected_role, "unexpected GTK toplevel role"
            if expected_name is not None:
                assert frame.get_name() == expected_name, (
                    "GTK toplevel title differs from niri window"
                )
            break
        frame = frame.get_parent()
    else:
        raise AssertionError("owned GTK frame exceeds the bounded ancestry")
    assert frame.get_state_set().contains(Atspi.StateType.SHOWING)
    outer = Atspi.Component.get_extents(frame.get_component_iface(), Atspi.CoordType.SCREEN)
    assert (outer.width, outer.height) == tuple(expected_size), "GTK frame differs from niri window"
    x, y = area.x - outer.x, area.y - outer.y
    assert 0 < area.width <= outer.width and 0 < area.height <= outer.height
    assert 0 <= x <= outer.width - area.width and 0 <= y <= outer.height - area.height
    return (x, y, area.width, area.height), (outer.x, outer.y, outer.width, outer.height)


def settings_navigation(pid):
    """Return the real sidebar selection and its labelled rows, if mapped."""
    sidebar = named(pid, "设置导航")
    pending = [sidebar] if sidebar else []
    for _ in range(32):
        if not pending:
            return None
        node = pending.pop()
        if node is None:
            continue
        node.clear_cache()
        selection = node.get_selection_iface()
        if selection is not None:
            rows = [node.get_child_at_index(i) for i in range(node.get_child_count())]
            return selection, rows
        pending.extend(node.get_child_at_index(i) for i in range(node.get_child_count()))
    raise AssertionError("settings sidebar exceeds its bounded accessibility tree")


def settings_page_row(rows, title):
    """Locate a sidebar row by its accessible label, not a product page index."""
    for index, row in enumerate(rows):
        pending = [row]
        for _ in range(8):
            if not pending:
                break
            node = pending.pop()
            if node is None:
                continue
            node.clear_cache()
            if node.get_name() == title:
                return index, row
            pending.extend(node.get_child_at_index(i) for i in range(node.get_child_count()))
    raise AssertionError("settings sidebar page missing: " + title)


def select_settings_page(pid, title, timeout=15):
    """Use GTK's actual sidebar selection interface; never call a UI handler."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        navigation = settings_navigation(pid)
        if navigation is not None:
            selection, rows = navigation
            try:
                index, row = settings_page_row(rows, title)
                break
            except AssertionError:
                pass  # The mapped window can precede its complete accessibility tree.
        time.sleep(0.05)
    else:
        raise AssertionError("settings sidebar page was not accessible: " + title)
    assert Atspi.Selection.select_child(selection, index), title
    while time.monotonic() < deadline:
        page = named(pid, "设置页：" + title)
        if (
            page is not None
            and page.get_state_set().contains(Atspi.StateType.SHOWING)
            and Atspi.Selection.is_child_selected(selection, index)
        ):
            return row
        time.sleep(0.05)
    raise AssertionError("selected settings page did not become visible: " + title)


def record_shortcut(pid, title, chord, send_key, timeout=15):
    """Record through the focused GTK button and real desktop key delivery.

    This is settings input, not trusted source input or global registration.
    Esc cancels recording; the explicit reset action restores the default Esc.
    """
    deadline = time.monotonic() + timeout
    name = "恢复默认：" + title if chord == "Esc" else "设置：" + title
    while time.monotonic() < deadline:
        node = named(pid, name)
        if node is not None:
            node.clear_cache()
            states = node.get_state_set()
            if (
                node.get_role() == Atspi.Role.PUSH_BUTTON
                and states.contains(Atspi.StateType.SHOWING)
                and states.contains(Atspi.StateType.SENSITIVE)
            ):
                break
        time.sleep(0.05)
    else:
        raise AssertionError("shortcut recorder was not accessible: " + name)
    assert Atspi.Component.grab_focus(node.get_component_iface()), name
    assert Atspi.Action.do_action(node.get_action_iface(), 0), name
    if chord == "Esc":
        return node
    while time.monotonic() < deadline:
        if focused(node) and node.get_description() == "按下快捷键…":
            break
        time.sleep(0.05)
    else:
        raise AssertionError("shortcut recorder did not take focus: " + title)
    send_key(
        chord.replace("Ctrl", "ctrl")
        .replace("Shift", "shift")
        .replace("Alt", "alt")
        .replace("Super", "super")
    )
    return node


def select_work_mode(pid, mode):
    """Activate the actual native segmented radio, not the retired combo box."""
    assert mode in (0, 1)
    name = "设置：工作模式：" + ("自动" if mode == 0 else "按需")
    node = named(pid, name)
    assert node is not None and node.get_role() == Atspi.Role.RADIO_BUTTON, name
    states = node.get_state_set()
    assert states.contains(Atspi.StateType.SHOWING) and states.contains(Atspi.StateType.SENSITIVE)
    assert Atspi.Action.do_action(node.get_action_iface(), 0), name
    return node
