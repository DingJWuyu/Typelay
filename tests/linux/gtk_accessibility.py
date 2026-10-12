"""Observe owned GTK test windows through the real desktop accessibility API."""

import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi, GLib  # noqa: E402


def named(pid, name):
    desktop = Atspi.get_desktop(0)
    for index in range(desktop.get_child_count()):
        app = desktop.get_child_at_index(index)
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


def bounds(node):
    node.clear_cache()
    assert node.get_state_set().contains(Atspi.StateType.SHOWING)
    return node.get_component_iface().get_extents(Atspi.CoordType.WINDOW)


def geometry_evidence(node, pid):
    """Record coordinates only within this fixture's accessibility ancestry."""
    ancestry = []
    for _ in range(16):
        if node is None:
            break
        try:
            if node.get_process_id() != pid:
                break
            node.clear_cache()
            states = node.get_state_set()
            row = {
                "role": int(node.get_role()),
                "showing": states.contains(Atspi.StateType.SHOWING),
                "focused": states.contains(Atspi.StateType.FOCUSED),
            }
            component = node.get_component_iface()
            if component is not None:
                for name in ("WINDOW", "SCREEN", "PARENT"):
                    coordinate_type = getattr(Atspi.CoordType, name, None)
                    if coordinate_type is None:
                        row[name] = None
                        continue
                    try:
                        area = Atspi.Component.get_extents(component, coordinate_type)
                        row[name] = [area.x, area.y, area.width, area.height]
                    except GLib.Error:
                        row[name] = "unavailable"
            ancestry.append(row)
            node = node.get_parent()
        except GLib.Error:
            ancestry.append({"error": "owned-accessible-unavailable"})
            break
    return ancestry
