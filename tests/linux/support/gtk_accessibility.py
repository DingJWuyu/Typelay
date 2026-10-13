"""Observe owned GTK test windows through the real desktop accessibility API."""

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
        pending = [app]
        for _ in range(256):
            if not pending:
                break
            node = pending.pop()
            if node is None:
                continue  # A child can disappear between count and lookup.
            node.clear_cache()
            if node.get_name() == name:
                return node
            pending.extend(node.get_child_at_index(i) for i in range(node.get_child_count()))
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


def bounds(node, pid, expected_size):
    """Resolve an owned popup's bounds relative to its actual GTK frame.

    WINDOW can be relative to the popover's Gdk toplevel. Subtract the
    enclosing FRAME in the same reported coordinate space instead; this
    does not treat Wayland SCREEN coordinates as desktop-global positions.
    """
    node.clear_cache()
    assert node.get_process_id() == pid, "candidate belongs to another process"
    assert node.get_state_set().contains(Atspi.StateType.SHOWING)
    area = Atspi.Component.get_extents(node.get_component_iface(), Atspi.CoordType.SCREEN)
    frame = node
    for _ in range(16):
        assert frame is not None and frame.get_process_id() == pid, "owned GTK frame unavailable"
        frame.clear_cache()
        if frame.get_role() == Atspi.Role.FRAME:
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
