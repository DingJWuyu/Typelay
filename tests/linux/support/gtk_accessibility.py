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


def visible_named(pid, name, *, role=None, root=None):
    """Return one actually showing owned control, rejecting same-role ambiguity."""
    if root is None:
        desktop = Atspi.get_desktop(0)
        roots = [
            app
            for index in range(desktop.get_child_count())
            if (app := desktop.get_child_at_index(index)) is not None
            and app.get_process_id() == pid
        ]
    else:
        roots = [root]
    pending, matches = list(roots), []
    for _ in range(4096):
        if not pending:
            break
        node = pending.pop()
        if node is None:
            continue
        node.clear_cache()
        assert node.get_process_id() == pid, "visible GTK node changed owner"
        if (
            node.get_name() == name
            and (role is None or node.get_role() == role)
            and node.get_state_set().contains(Atspi.StateType.SHOWING)
        ):
            matches.append(node)
        pending.extend(node.get_child_at_index(index) for index in range(node.get_child_count()))
    else:
        assert not pending, "owned GTK accessibility tree exceeds 4096 nodes"
    assert len(matches) <= 1, "ambiguous visible GTK control: " + name
    return matches[0] if matches else None


def onboarding_content(pid, title, action_name):
    """Resolve the owned frame's content by its real onboarding action.

    GTK CSD adds a title LABEL outside this branch. Do not assume a child
    index or accept the first of two identically named title labels.
    """
    frame = visible_named(pid, title, role=Atspi.Role.FRAME)
    if frame is None:
        return None
    action = visible_named(pid, action_name, role=Atspi.Role.PUSH_BUTTON, root=frame)
    if action is None:
        return None
    branch, seen = action, set()
    for _ in range(16):
        branch.clear_cache()
        assert branch.get_process_id() == pid
        assert branch not in seen, "cyclic onboarding content ancestry"
        seen.add(branch)
        parent = branch.get_parent()
        assert parent is not None and parent.get_process_id() == pid
        if parent == frame:
            return branch
        branch = parent
    raise AssertionError("onboarding content exceeds bounded ancestry")


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
    # An explicit None is for same-GTK-space containment only, not conversion
    # to compositor pointer coordinates. CSD frame extents include shadows;
    # Wayland window geometry excludes them. Pointer callers keep exact sizing.
    if expected_size is not None:
        assert (outer.width, outer.height) == tuple(expected_size), (
            "GTK frame differs from niri window: "
            f"frame={(outer.x, outer.y, outer.width, outer.height)}, "
            f"niriSize={tuple(expected_size)}, "
            f"field={(area.x, area.y, area.width, area.height)}"
        )
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
    Esc is recorded by Dismiss; it cancels recording for the other actions.
    """
    deadline = time.monotonic() + timeout
    name = "设置：" + title
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
    action = node.get_action_iface()
    assert time.monotonic() < deadline, "shortcut recorder action exhausted its interaction budget"
    assert Atspi.Action.do_action(action, 0), name
    while time.monotonic() < deadline:
        if focused(node) and node.get_description() == "按下按键…":
            break
        time.sleep(0.05)
    else:
        raise AssertionError("shortcut recorder did not take focus: " + title)
    assert time.monotonic() < deadline, "shortcut recording exhausted its interaction budget"
    # Stored/product spelling is Esc; the X11 keysym is Escape. xdotool
    # silently ignores an unknown symbol, so do not send the display spelling.
    key_names = {"Ctrl": "ctrl", "Shift": "shift", "Alt": "alt", "Super": "super", "Esc": "Escape"}
    send_key("+".join(key_names.get(part, part) for part in chord.split("+")))
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


def scroll_settings_control(pid, node, *, deadline, at_start=False):
    """Navigate the owned Apps pane's real scrollbar, never activate a hidden peer.

    GTK3 cannot expose offscreen child coordinates. Walk the finite public
    scrollbar range once; final callers still prove focus and every clip.
    """
    import math

    name = node.get_name()
    path = []
    peer = node
    for _ in range(24):
        assert peer is not None and peer not in path
        peer.clear_cache()
        assert peer.get_process_id() == pid
        assert not peer.get_state_set().contains(Atspi.StateType.DEFUNCT)
        path.append(peer)
        if peer.get_role() == Atspi.Role.FRAME:
            break
        peer = peer.get_parent()
    else:
        raise AssertionError("settings scroll ancestry exceeds its bound")
    viewports = [index for index, peer in enumerate(path) if peer.get_role() == Atspi.Role.VIEWPORT]
    assert len(viewports) == 1
    index = viewports[0]
    assert index + 1 < len(path)
    viewport, pane, frame = path[index], path[index + 1], path[-1]
    assert pane.get_role() == Atspi.Role.SCROLL_PANE and pane.get_name() == "设置页：应用"
    assert frame.get_name() == "TypeLay 设置"
    assert not any(
        peer.get_role() in (Atspi.Role.SCROLL_PANE, Atspi.Role.FRAME)
        for peer in path[:index] + path[index + 2 : -1]
    )
    assert viewport.get_child_count() == 1
    content = viewport.get_child_at_index(0)
    assert (
        content is not None and content.get_parent() == viewport and content.get_process_id() == pid
    )
    count = pane.get_child_count()
    assert 0 < count <= 8
    bars = []
    for child_index in range(count):
        child = pane.get_child_at_index(child_index)
        assert child is not None and child.get_parent() == pane and child.get_process_id() == pid
        child.clear_cache()
        if child.get_role() == Atspi.Role.SCROLL_BAR and child.get_state_set().contains(
            Atspi.StateType.VERTICAL
        ):
            bars.append(child)
    assert len(bars) == 1, "Apps pane has no unique public vertical scrollbar"
    bar = bars[0]
    value = bar.get_value_iface()
    assert value is not None

    def rectangle(peer):
        area = Atspi.Component.get_extents(peer.get_component_iface(), Atspi.CoordType.SCREEN)
        assert area.x != -(2**31) and area.y != -(2**31) and area.width > 0 and area.height > 0
        return area

    def current(*, require_bar=True):
        assert time.monotonic() < deadline, "settings scrolling exhausted the original deadline"
        assert node.get_name() == name
        for child, parent in zip(path, path[1:]):
            child.clear_cache()
            assert child.get_process_id() == pid and child.get_parent() == parent
            assert not child.get_state_set().contains(Atspi.StateType.DEFUNCT)
        frame.clear_cache()
        assert frame.get_state_set().contains(Atspi.StateType.ACTIVE)
        bar.clear_cache()
        assert bar.get_process_id() == pid and bar.get_parent() == pane
        assert bar.get_role() == Atspi.Role.SCROLL_BAR
        bar_states = bar.get_state_set()
        assert bar_states.contains(Atspi.StateType.VERTICAL)
        assert not bar_states.contains(Atspi.StateType.DEFUNCT)
        for peer in (pane, bar) if require_bar else (pane,):
            peer.clear_cache()
            states = peer.get_state_set()
            assert all(
                states.contains(state)
                for state in (
                    Atspi.StateType.SHOWING,
                    Atspi.StateType.SENSITIVE,
                    Atspi.StateType.ENABLED,
                )
            )
        assert bar.get_parent() == pane and content.get_parent() == viewport
        clip, outer = rectangle(pane), rectangle(frame)
        native_viewport = Atspi.Component.get_extents(
            viewport.get_component_iface(), Atspi.CoordType.SCREEN
        )
        assert (native_viewport.width, native_viewport.height) == (clip.width, clip.height)
        assert outer.x <= clip.x <= outer.x + outer.width - clip.width
        assert outer.y <= clip.y <= outer.y + outer.height - clip.height
        lower = Atspi.Value.get_minimum_value(value)
        upper = Atspi.Value.get_maximum_value(value)
        position = Atspi.Value.get_current_value(value)
        assert all(math.isfinite(number) for number in (lower, upper, position))
        assert lower <= position <= upper
        return clip, lower, upper, position

    if at_start:
        # An AUTOMATIC scrollbar is legitimately hidden when this collapsed
        # page fits. Reading its exact zero range is not a scroll/input grant.
        clip, lower, upper, position = current(require_bar=False)
        if lower == upper == position:
            node.clear_cache()
            assert node.get_state_set().contains(Atspi.StateType.SHOWING)
            area = rectangle(node)
            for peer in (viewport, pane, frame):
                peer.clear_cache()
                assert peer.get_state_set().contains(Atspi.StateType.SHOWING)
                observed = rectangle(peer)
                assert observed.x <= area.x <= observed.x + observed.width - area.width
                assert observed.y <= area.y <= observed.y + observed.height - area.height
            _, low, high, actual = current(require_bar=False)
            assert (low, high, actual) == (lower, upper, position)
            assert time.monotonic() < deadline
            return node

    clip, lower, upper, position = current()
    page_size = clip.height
    steps = math.ceil((upper - lower) / page_size)
    assert steps <= 32, "Apps pane exceeds the bounded public scroll range"

    def move(requested):
        before, low, high, old = current()
        assert (low, high, before.height) == (lower, upper, page_size)
        assert lower <= requested <= upper
        if requested == old:
            return
        old_content = rectangle(content)
        # A Value setter is public navigation, not a button action or input grant.
        assert time.monotonic() < deadline
        assert Atspi.Value.set_current_value(value, requested)
        assert Atspi.Value.get_current_value(value) == requested
        while time.monotonic() < deadline:
            _, _, _, actual = current()
            assert actual == requested, "Apps scrollbar changed during this navigation"
            moved = rectangle(content)
            if moved.y == old_content.y + old - requested:
                return
            time.sleep(0.05)
        raise AssertionError("Apps content did not follow its public scrollbar")

    move(lower)
    if at_start:
        return node
    for step in range(steps + 1):
        current()
        node.clear_cache()
        if node.get_state_set().contains(Atspi.StateType.SHOWING):
            area = rectangle(node)
            _, _, _, position = current()
            observed = Atspi.Component.get_extents(
                viewport.get_component_iface(), Atspi.CoordType.SCREEN
            )
            # GTK3 can hide the viewport's screen origin after scrolling. This
            # helper never invents that origin or replaces the caller's original
            # calibrated ancestry/full-clip gate.
            if observed.x != -(2**31) and observed.y != -(2**31):
                assert observed.width > 0 and observed.height > 0
                assert observed.x <= area.x <= observed.x + observed.width - area.width, (
                    "Settings target exceeds its observed horizontal viewport: "
                    f"viewport={(observed.x, observed.y, observed.width, observed.height)}, "
                    f"target={(area.x, area.y, area.width, area.height)}, "
                    f"position={position}, step={step}"
                )
                assert area.height <= observed.height
                delta = (
                    area.y - observed.y
                    if area.y < observed.y
                    else max(0, area.y + area.height - observed.y - observed.height)
                )
                move(max(lower, min(upper, position + delta)))
            return node
        if step < steps:
            move(min(upper, lower + (step + 1) * page_size))
    raise AssertionError(
        "Settings target is not visible at the end of its public scroll range: " + name
    )


def focus_settings_control(pid, node, timeout=15):
    """Reveal an exact settings peer through GTK focus, then prove all clips.

    GTK3 does not implement Component.scroll_to. This uses its native focus
    adjustment, not a hidden control Action or a guessed viewport fallback.
    """
    deadline = time.monotonic() + timeout
    node.clear_cache()
    assert node.get_process_id() == pid
    name = node.get_name()
    assert Atspi.Component.grab_focus(node.get_component_iface()), name

    def settings_control_visible():
        node.clear_cache()
        assert node.get_process_id() == pid and node.get_name() == name
        states = node.get_state_set()
        if not all(
            states.contains(state)
            for state in (
                Atspi.StateType.SHOWING,
                Atspi.StateType.FOCUSED,
                Atspi.StateType.SENSITIVE,
                Atspi.StateType.ENABLED,
            )
        ):
            return False
        area = Atspi.Component.get_extents(node.get_component_iface(), Atspi.CoordType.SCREEN)
        if area.width <= 0 or area.height <= 0:
            return False
        ancestor = node.get_parent()
        seen = {node}
        # The real GTK searchable popover adds six nested accessibility peers;
        # its owned settings frame is at depth 19. Keep traversal bounded and
        # reject cycles while checking every clip, not just the final frame.
        for _ in range(24):
            assert ancestor is not None and ancestor.get_process_id() == pid
            assert ancestor not in seen, "settings accessibility ancestry contains a cycle"
            seen.add(ancestor)
            ancestor.clear_cache()
            role = ancestor.get_role()
            if role in (
                Atspi.Role.VIEWPORT,
                Atspi.Role.SCROLL_PANE,
                Atspi.Role.FRAME,
                Atspi.Role.DIALOG,
                Atspi.Role.ALERT,
            ):
                clip = Atspi.Component.get_extents(
                    ancestor.get_component_iface(), Atspi.CoordType.SCREEN
                )
                if not (
                    clip.width > 0
                    and clip.height > 0
                    and clip.x != -(2**31)
                    and clip.y != -(2**31)
                    and clip.x <= area.x <= clip.x + clip.width - area.width
                    and clip.y <= area.y <= clip.y + clip.height - area.height
                ):
                    return False
                if role in (Atspi.Role.FRAME, Atspi.Role.DIALOG, Atspi.Role.ALERT):
                    assert (
                        role in (Atspi.Role.DIALOG, Atspi.Role.ALERT)
                        or ancestor.get_name() == "TypeLay 设置"
                    )
                    return True
            ancestor = ancestor.get_parent()
        raise AssertionError("settings control exceeds its bounded owned ancestry")

    while time.monotonic() < deadline:
        if settings_control_visible():
            assert settings_control_visible(), "settings control moved after focus: " + name
            return node
        time.sleep(0.05)
    raise AssertionError("settings control did not focus/scroll fully into view: " + name)


def language_label(node):
    """Read the current selected language from the visible native button."""
    node.clear_cache()
    assert node.get_role() == Atspi.Role.PUSH_BUTTON
    return node.get_description()


def select_language(pid, title, code, timeout=15):
    """Choose an owned current option from the real native search popover."""
    deadline = time.monotonic() + timeout

    def accessible(name):
        while time.monotonic() < deadline:
            node = named(pid, name)
            if node is not None and node.get_state_set().contains(Atspi.StateType.SHOWING):
                return focus_settings_control(pid, node, timeout=deadline - time.monotonic())
            time.sleep(0.05)
        raise AssertionError("language control was not accessible: " + name)

    button = accessible("设置：" + title)
    assert button.get_role() == Atspi.Role.PUSH_BUTTON
    assert Atspi.Action.do_action(button.get_action_iface(), 0)
    option = accessible("语言选项：" + code)
    assert option.get_role() == Atspi.Role.PUSH_BUTTON
    assert Atspi.Action.do_action(option.get_action_iface(), 0)
    return button
