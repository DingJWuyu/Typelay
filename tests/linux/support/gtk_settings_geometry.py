"""Strict first-screen GTK settings viewport calibration for Linux UI tests."""


def settings_viewport_from_pane(initial, current):
    """Recover only the setup page's calibrated GTK3 viewport sentinel.

    GTK 3.24.52's accessibility on_screen check treats a viewport as its own
    ancestor. A deep scroll can therefore report G_MININT for its origin even
    though only its bin window moved. This is not a general bounds fallback:
    the original direct pair, frame, width and relative origin must still
    match the first-screen observation. The draft footer may shorten both
    native allocations; only their matching smaller height is admissible.

    https://github.com/GNOME/gtk/blob/3.24.52/gtk/a11y/gtkwidgetaccessible.c#L778-L809
    https://github.com/GNOME/gtk/blob/3.24.52/gtk/gtkviewport.c#L1038-L1060
    """
    if initial is None or current is None:
        return None
    path = initial["ancestry"]
    if (
        len(path) < 3
        or path[0][2] != "viewport"
        or path[1][2] != "scroll-pane"
        or path[-1][2] != "frame"
        or any(role in ("viewport", "scroll-pane", "frame") for _, _, role in path[2:-1])
        or current["ancestry"] != path
        or current["pid"] != initial["pid"]
        or any(node is None or pid != initial["pid"] for node, pid, _ in path)
        or not initial["alive"]
        or not current["alive"]
        or initial["showing"] != (True, True)
        or current["showing"] != (True, True)
    ):
        return None
    for bounds in (
        initial["viewport"],
        initial["pane"],
        initial["frame"],
        current["pane"],
        current["frame"],
    ):
        if (
            len(bounds) != 4
            or any(type(value) is not int or not -(2**31) < value < 2**31 for value in bounds)
            or bounds[2] <= 0
            or bounds[3] <= 0
        ):
            return None
    pane, frame = current["pane"], current["frame"]
    old_pane, old_frame = initial["pane"], initial["frame"]
    if (
        initial["viewport"] != old_pane
        or current["viewport"] != (-(2**31), -(2**31), *pane[2:])
        or pane[2] != old_pane[2]
        or pane[3] > old_pane[3]
        or frame[2:] != old_frame[2:]
        or (pane[0] - frame[0], pane[1] - frame[1])
        != (old_pane[0] - old_frame[0], old_pane[1] - old_frame[1])
        or not (
            frame[0] <= pane[0] <= frame[0] + frame[2] - pane[2]
            and frame[1] <= pane[1] <= frame[1] + frame[3] - pane[3]
        )
    ):
        return None
    return pane


def settings_scroll_snapshot(pid, viewport):
    from gi.repository import Atspi

    clip_roles = {
        Atspi.Role.VIEWPORT: "viewport",
        Atspi.Role.SCROLL_PANE: "scroll-pane",
        Atspi.Role.FRAME: "frame",
    }
    # Only the fixed application-support page's direct native pair
    # is eligible. Keep the actual proxies, not names as identity.
    path, states = [], []
    ancestor = viewport
    for _ in range(16):
        if ancestor is None:
            return None
        ancestor.clear_cache()
        owner_pid, role = ancestor.get_process_id(), ancestor.get_role()
        if owner_pid != pid:
            return None
        path.append((ancestor, owner_pid, clip_roles.get(role, role)))
        states.append(ancestor.get_state_set())
        if role == Atspi.Role.FRAME:
            break
        ancestor = ancestor.get_parent()
    else:
        return None
    if (
        len(path) < 3
        or path[0][2] != "viewport"
        or path[1][2] != "scroll-pane"
        or path[1][0].get_name() not in ("设置页：应用", "设置页：本地模型")
        or path[-1][0].get_name() != "TypeLay 设置"
    ):
        return None
    snapshot = {
        "pid": pid,
        "ancestry": tuple(path),
        "showing": tuple(states[index].contains(Atspi.StateType.SHOWING) for index in (1, -1)),
        "alive": not any(state.contains(Atspi.StateType.DEFUNCT) for state in states),
    }
    for name, index in (("viewport", 0), ("pane", 1), ("frame", -1)):
        rect = Atspi.Component.get_extents(
            path[index][0].get_component_iface(), Atspi.CoordType.SCREEN
        )
        snapshot[name] = (rect.x, rect.y, rect.width, rect.height)
    return snapshot
