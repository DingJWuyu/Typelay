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
    config.parent.mkdir(parents=True)
    # Controlled initial state only skips the tutorial. Preferences below are
    # changed through the actual accessible controls.
    config.write_text(json.dumps({"OnboardingComplete": True}))
    actions, geometry = [], []

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

        # Open and cancel the actual component chooser or install confirmation.
        saved_before_action = json.loads(config.read_text())
        click("App connection: Desktop apps")
        cancel = wait(
            lambda: visible_named(pid, "Cancel") or visible_named(pid, "取消"),
            "connection action dialog",
        )
        capture("apps-connection-action")
        cancel_name = cancel.get_name()
        click(cancel_name)
        wait(lambda: visible_named(pid, cancel_name) is None, "connection action cancelled")
        capture("apps-after-action")
        saved_after_action = json.loads(config.read_text())
        assert saved_after_action == saved_before_action, "cancel changed saved preferences"
        save("settings-saved.json", saved_after_action)
        save(
            "result.json",
            {
                "status": "passed",
                "features": ["settings/apps-readability"],
                "initialFixture": {"OnboardingComplete": True},
                "modelValidation": "not-run",
            },
        )
    finally:
        subprocess.run([app, "--quit"], check=True, timeout=15)


if __name__ == "__main__":
    main()
