#!/usr/bin/env python3
"""Read and operate the actual Apps page in the owned verification session."""

import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parents[4] / "tests/linux"))
from support.gtk_accessibility import (  # noqa: E402
    settings_navigation,
    visible_named,
)

INSTALL_CONFIRMATION = {
    "en": {
        "title": "Desktop text fields: Install connection components…",
        "body": (
            "TypeLay connection components will be installed for the selected app. "
            "Check the actual connection after installation; the input method will not be "
            "switched and app processes will not be terminated automatically.\n\n"
            "Installation verifies and extracts desktop connection components from the current "
            "TypeLay package into your user directory, retaining the original archive, source "
            "code and licenses. Trust in the components comes from the package you obtained; "
            "bundled file hashes are not a publisher signature. This only prepares files. "
            "It does not run bundled programs, switch input methods or open apps."
        ),
        "cancel": "Cancel",
        "confirm": "Continue",
    },
    "zh-CN": {
        "title": "桌面普通输入框：安装接入组件…",
        "body": (
            "将安装所选应用的 TypeLay 接入组件。安装完成后仍须检查实际连接；"
            "不会自动切换输入法或结束应用进程。\n\n"
            "安装将从当前 TypeLay 主包校验并展开桌面接入组件到用户目录，"
            "保留原始压缩包、源码与许可证。来源信任继承于你取得的主包；"
            "自带文件哈希不是发布者签名。只准备文件，不运行包内程序、切换输入法或打开应用。"
        ),
        "cancel": "取消",
        "confirm": "继续",
    },
}


def wait(observe, description):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if value := observe():
            return value
        time.sleep(0.05)
    raise TimeoutError(description)


def main():
    output = Path(os.environ["TYPELAY_VERIFY_OUTPUT"])
    session = json.loads((output / "session.json").read_text())
    assert session["NIRI_SOCKET"] == os.environ["NIRI_SOCKET"]
    app = str(Path(os.environ["TYPELAY_VERIFY_PACKAGE"]) / "typelay")
    config = Path(os.environ["XDG_CONFIG_HOME"]) / "typelay/settings.json"
    assert not config.exists(), "this journey requires an empty private profile"
    config.parent.mkdir(parents=True, exist_ok=True)
    # Controlled initial state only skips the tutorial. Preferences below are
    # changed through the actual accessible controls.
    config.write_text(json.dumps({"OnboardingComplete": True}))
    actions, geometry, layout_issues = [], [], []

    def save(name, value):
        (output / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")

    def record(**value):
        actions.append(value)
        save("actions.json", actions)

    def windows():
        return json.loads(
            subprocess.check_output(["niri", "msg", "--json", "windows"], text=True, timeout=5)
        )

    def click(name, root=None):
        node = wait(lambda: visible_named(pid, name, root=root), name)
        assert node.get_state_set().contains(Atspi.StateType.SENSITIVE), name
        record(action="AT-SPI action", name=name, role=node.get_role_name())
        assert Atspi.Action.do_action(node.get_action_iface(), 0), name

    def page(title, content):
        selected = False

        def ready():
            nonlocal selected
            if not selected:
                navigation = settings_navigation(pid)
                if navigation is None:
                    return None
                selection, rows = navigation
                for index, row in enumerate(rows):
                    if row is not None and visible_named(pid, title, root=row) is not None:
                        record(action="AT-SPI sidebar selection", name=title)
                        assert Atspi.Selection.select_child(selection, index), title
                        selected = True
                        break
                if not selected:
                    return None
            return visible_named(pid, content)

        wait(ready, "settings page visible: " + title)

    def capture(name):
        subprocess.run(["grim", str(output / (name + ".png"))], check=True, timeout=5)
        record(action="screenshot", path=name + ".png")

    def install_confirmation(language, theme):
        expected = INSTALL_CONFIRMATION[language]
        saved_before = config.read_bytes()
        component = Path(os.environ["XDG_DATA_HOME"]) / "typelay/components/desktop-text"
        assert not component.exists(), "this journey requires uninstalled desktop components"
        click("App connection: Desktop apps" if language == "en" else "应用连接：桌面应用")

        def modal():
            desktop = Atspi.get_desktop(0)
            for index in range(desktop.get_child_count()):
                app_node = desktop.get_child_at_index(index)
                if app_node is None or app_node.get_process_id() != pid:
                    continue
                app_node.clear_cache()
                for child_index in range(app_node.get_child_count()):
                    node = app_node.get_child_at_index(child_index)
                    if node is None:
                        continue
                    node.clear_cache()
                    if node.get_role() in (
                        Atspi.Role.ALERT,
                        Atspi.Role.DIALOG,
                    ) and node.get_state_set().contains(Atspi.StateType.SHOWING):
                        return node
            return None

        dialog = wait(modal, "desktop component install confirmation")
        name = f"apps-{theme.lower()}-{language}-confirmation"
        modal_window = wait(
            lambda: next(
                (
                    item
                    for item in windows()
                    if item["pid"] == pid and item["id"] != window["id"] and item["is_focused"]
                ),
                None,
            ),
            "compositor shows the focused installation confirmation",
        )
        save(name + "-window.json", modal_window)
        capture(name)
        labels, buttons, pending = [], [], [dialog]
        for _ in range(128):
            if not pending:
                break
            node = pending.pop()
            if node.get_role() == Atspi.Role.LABEL and node.get_name():
                labels.append(node.get_name())
            elif node.get_role() == Atspi.Role.PUSH_BUTTON:
                buttons.append(node.get_name())
            pending.extend(node.get_child_at_index(i) for i in range(node.get_child_count()))
        assert not pending, "confirmation accessibility tree exceeds 128 nodes"
        save(name + ".json", {"language": language, "labels": labels, "buttons": buttons})
        assert expected["title"] in labels, "confirmation title does not match " + language
        assert expected["body"] in labels, "complete confirmation body does not match " + language
        assert sorted(buttons) == sorted([expected["cancel"], expected["confirm"]]), (
            "confirmation buttons do not match " + language
        )
        assert set(labels) <= set(expected.values()), "unexpected confirmation text"
        click(expected["cancel"], root=dialog)
        wait(lambda: modal() is None, "installation cancelled")
        wait(
            lambda: not any(item["id"] == modal_window["id"] for item in windows())
            and any(item["id"] == window["id"] and item["is_focused"] for item in windows()),
            "compositor returns to settings after cancellation",
        )
        capture(name + "-cancelled")
        assert config.read_bytes() == saved_before, "cancel changed saved preferences"
        assert not component.exists(), "cancel installed desktop components"
        save(name + "-cancelled.json", {"settingsUnchanged": True, "componentInstalled": False})

    def observe_words(name):
        for title in ("Desktop apps", "Browser extension"):
            title_node = visible_named(pid, title, role=Atspi.Role.LABEL)
            assert title_node is not None, title
            row = title_node.get_parent()
            for index in range(row.get_child_count()):
                node = row.get_child_at_index(index)
                if node.get_role() != Atspi.Role.LABEL:
                    continue
                text = node.get_name()
                interface = node.get_text_iface()
                words = []
                for word in re.finditer(r"[A-Za-z]+", text):
                    rectangles = [
                        Atspi.Text.get_character_extents(interface, offset, Atspi.CoordType.WINDOW)
                        for offset in range(word.start(), word.end())
                    ]
                    words.append({"word": word.group(), "rows": [r.y for r in rectangles]})
                geometry.append({"capture": name, "text": text, "words": words})
        save("text-geometry.json", geometry)
        assert all(len(set(word["rows"])) == 1 for item in geometry for word in item["words"]), (
            "Apps split an English word; inspect text-geometry.json and the screenshot"
        )

    def observe_connection_card(name):
        title = visible_named(pid, "Desktop apps", role=Atspi.Role.LABEL)
        card = title.get_parent().get_parent()
        # Connection rows and the next-step action belong to the same card.
        # Inspect the actual accessible rectangle, including its bottom inset.
        area = Atspi.Component.get_extents(card.get_component_iface(), Atspi.CoordType.WINDOW)
        entry = visible_named(pid, "Choose a connection", role=Atspi.Role.PUSH_BUTTON)
        entry_area = Atspi.Component.get_extents(
            entry.get_component_iface(), Atspi.CoordType.WINDOW
        )
        save(
            name + "-connection-card.json",
            {
                "card": [area.x, area.y, area.width, area.height],
                "entry": [entry_area.x, entry_area.y, entry_area.width, entry_area.height],
            },
        )
        if entry_area.y + entry_area.height + 8 > area.y + area.height:
            layout_issues.append(name + ": connection entry is outside its card padding")
        buttons = []
        for title in ("Desktop apps", "Browser extension"):
            button = visible_named(pid, "App connection: " + title, role=Atspi.Role.PUSH_BUTTON)
            bounds = Atspi.Component.get_extents(
                button.get_component_iface(), Atspi.CoordType.WINDOW
            )
            buttons.append({"title": title, "width": bounds.width, "height": bounds.height})
        save(name + "-buttons.json", buttons)
        if len({button["width"] for button in buttons}) != 1:
            layout_issues.append(name + ": connection actions have different widths")
        if any(button["height"] > 44 for button in buttons):
            layout_issues.append(name + ": connection action wraps beyond two lines")
        if (
            visible_named(pid, "Tray is not connected. Open Settings from the application menu.")
            is None
        ):
            layout_issues.append(name + ": English tray notice is missing")
        save("layout-issues.json", layout_issues)

    try:
        with (output / "typelay.log").open("w") as log:
            subprocess.Popen([app, "--settings"], stdout=log, stderr=subprocess.STDOUT)
        window = wait(
            lambda: next((w for w in windows() if w["title"] == "TypeLay 设置"), None),
            "settings window",
        )
        pid = window["pid"]
        wait(lambda: visible_named(pid, "外观"), "settings ready")
        page("外观", "外观：浅色")
        click("外观：浅色")
        click("设置：界面语言")
        combo = visible_named(pid, "设置：界面语言", role=Atspi.Role.COMBO_BOX)
        click("English", root=combo)
        wait(lambda: json.loads(config.read_text()).get("UiLanguage") == "en", "English saved")

        for theme in ("Light", "Dark"):
            page("Appearance", "Appearance: " + theme)
            click("Appearance: " + theme)
            wait(
                lambda: json.loads(config.read_text()).get("Theme") == theme.lower(),
                "theme saved",
            )
            page("Apps", "Desktop apps")
            for width in (900, 740):
                record(action="resize owned window", width=width, windowId=window["id"])
                subprocess.run(
                    [
                        "niri",
                        "msg",
                        "action",
                        "set-window-width",
                        "--id",
                        str(window["id"]),
                        str(width),
                    ],
                    check=True,
                    timeout=5,
                )

                def resized():
                    observed = next(w for w in windows() if w["id"] == window["id"])
                    return (
                        observed
                        if observed.get("layout", {}).get("window_size", [None])[0] == width
                        else None
                    )

                observed_window = wait(resized, "settings window width")
                name = f"apps-{theme.lower()}-{width}"
                save(name + "-window.json", observed_window)
                capture(name)
                observe_words(name)
                observe_connection_card(name)

            install_confirmation("en", theme)
            page("Appearance", "Appearance: " + theme)
            click("设置：界面语言")
            combo = visible_named(pid, "设置：界面语言", role=Atspi.Role.COMBO_BOX)
            click("简体中文", root=combo)
            wait(
                lambda: json.loads(config.read_text()).get("UiLanguage") == "zh-CN",
                "Simplified Chinese saved",
            )
            page("应用", "桌面应用")
            install_confirmation("zh-CN", theme)
            page("外观", "外观：浅色")
            click("设置：界面语言")
            combo = visible_named(pid, "设置：界面语言", role=Atspi.Role.COMBO_BOX)
            click("English", root=combo)
            wait(lambda: json.loads(config.read_text()).get("UiLanguage") == "en", "English saved")

        assert not layout_issues, "Apps layout: " + "; ".join(layout_issues)
        save("settings-saved.json", json.loads(config.read_text()))
        save(
            "result.json",
            {
                "status": "passed",
                "features": ["settings/apps-readability", "settings/apps-install-confirmation"],
                "initialFixture": {"OnboardingComplete": True},
                "modelValidation": "not-run",
            },
        )
    finally:
        subprocess.run([app, "--quit"], check=True, timeout=15)


if __name__ == "__main__":
    main()
