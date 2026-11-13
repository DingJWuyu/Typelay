#!/usr/bin/env python3
"""Type synthetic text in stock gtk3-demo and observe the production preview."""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi, GLib  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parents[4] / "tests/linux"))
from support.gtk_accessibility import select_settings_page  # noqa: E402


def wait(observe, message, timeout=30):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if value := observe():
            return value
        time.sleep(0.2)
    raise TimeoutError(message)


def tree(pid):
    desktop = Atspi.get_desktop(0)
    pending = [desktop.get_child_at_index(i) for i in range(desktop.get_child_count())]
    pending = [node for node in pending if node and node.get_process_id() == pid]
    nodes = []
    for _ in range(4096):
        if not pending:
            return nodes
        node = pending.pop(0)
        if node is None:
            continue
        try:
            node.clear_cache()
            nodes.append(node)
            pending.extend(node.get_child_at_index(i) for i in range(node.get_child_count()))
        except GLib.Error:
            continue
    raise RuntimeError("accessibility tree exceeded 4096 nodes")


def shown(node):
    return node.get_state_set().contains(Atspi.StateType.SHOWING)


def node_text(node):
    node.clear_cache()
    interface = node.get_text_iface()
    return Atspi.Text.get_text(interface, 0, -1) if interface else ""


def snapshot(pid):
    rows = []
    for node in tree(pid):
        if shown(node):
            rows.append(
                {"name": node.get_name(), "role": node.get_role_name(), "text": node_text(node)}
            )
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, required=True)
    args = parser.parse_args()
    output = Path(os.environ["TYPELAY_VERIFY_OUTPUT"])
    session = json.loads((output / "session.json").read_text())
    assert session["NIRI_SOCKET"] == os.environ["NIRI_SOCKET"]
    package = Path(os.environ["TYPELAY_VERIFY_PACKAGE"])
    cooperative = Path(os.environ["TYPELAY_VERIFY_COOPERATIVE"]) / "fcitx-cooperative.py"
    app = str(package / "typelay")
    launch = ["/usr/bin/python3", str(cooperative)]
    source = "Please keep the backup until Friday."
    actions = []

    def record(action):
        actions.append({"at": time.time(), **action})
        (output / "actions.json").write_text(json.dumps(actions, ensure_ascii=False, indent=2))

    def run(command, *, timeout=15, env=None):
        record({"command": list(map(str, command))})
        return subprocess.check_output(list(map(str, command)), text=True, timeout=timeout, env=env)

    def start(command, name):
        record({"command": list(map(str, command)), "log": name})
        with (output / name).open("w") as log:
            return subprocess.Popen(list(map(str, command)), stdout=log, stderr=subprocess.STDOUT)

    def save(name, value):
        (output / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")

    def doctor():
        return json.loads(subprocess.check_output([app, "--doctor"], text=True, timeout=10))

    def windows():
        return json.loads(subprocess.check_output(["niri", "msg", "--json", "windows"], text=True))

    def click(pid, name):
        def locate():
            found = [
                n
                for n in tree(pid)
                if shown(n) and n.get_name() == name and n.get_action_iface() is not None
            ]
            assert len(found) <= 1, f"ambiguous control: {name}"
            return found[0] if found else None

        node = wait(locate, f"missing control: {name}")
        action = node.get_action_iface()
        assert Atspi.Action.get_n_actions(action) == 1, name
        record({"action": "AT-SPI action", "pid": pid, "name": name, "role": node.get_role_name()})
        assert Atspi.Action.do_action(action, 0), name

    try:
        (output / "preflight.log").write_text(run([*launch, "preflight"], timeout=60))
        # Seed only the official download cache. The installer still verifies every byte.
        catalog = json.loads((package / "worker/model-catalog.json").read_text())
        model = next(p for p in catalog["packages"] if p["id"] == "index-translate-2b-q4-k-m")
        cache = (
            Path(os.environ["XDG_CACHE_HOME"])
            / "typelay/components"
            / model["id"]
            / model["version"]
        )
        shutil.copytree(args.cache.resolve(), cache)
        with (output / "model-install.log").open("w") as log:
            record({"command": [app, "--install-models"], "emptyDataDirectory": True})
            subprocess.run(
                [app, "--install-models"],
                stdout=log,
                stderr=subprocess.STDOUT,
                check=True,
                timeout=600,
            )
        start([*launch, "daemon"], "fcitx.log")
        wait(
            lambda: "true"
            in subprocess.check_output(
                [
                    "gdbus",
                    "call",
                    "--session",
                    "--dest",
                    "org.freedesktop.DBus",
                    "--object-path",
                    "/org/freedesktop/DBus",
                    "--method",
                    "org.freedesktop.DBus.NameHasOwner",
                    "org.fcitx.Fcitx5",
                ],
                text=True,
            ),
            "private Fcitx bus owner missing",
        )
        start([*launch, "launch", app, "--settings"], "typelay.log")
        wait(lambda: (doctor().get("session") or {}).get("modelReady"), "model not ready", 90)
        save("doctor-ready.json", doctor())
        settings = wait(
            lambda: next((w for w in windows() if w["title"] == "TypeLay 设置"), None),
            "settings window missing",
        )
        settings_pid = settings["pid"]
        save("settings-before.json", snapshot(settings_pid))
        record({"action": "AT-SPI sidebar selection", "pid": settings_pid, "name": "语言与风格"})
        select_settings_page(settings_pid, "语言与风格")
        click(settings_pid, "设置：源语言")
        click(settings_pid, "语言选项：en")
        click(settings_pid, "设置：目标语言")
        click(settings_pid, "语言选项：zh")
        settings_path = Path(os.environ["XDG_CONFIG_HOME"]) / "typelay/settings.json"
        wait(
            lambda: settings_path.exists()
            and json.loads(settings_path.read_text()).get("Source") == "en"
            and json.loads(settings_path.read_text()).get("Target") == "zh",
            "direction not saved",
        )
        shutil.copyfile(settings_path, output / "settings-saved.json")
        run(["grim", str(output / "settings.png")])
        run(["niri", "msg", "action", "close-window", "--id", str(settings["id"])])
        start(
            [*launch, "launch", app, "launch", "gtk3-demo", "--run=entry_completion"],
            "gtk-demo.log",
        )
        window = wait(
            lambda: next((w for w in windows() if w["title"] == "Entry Completion"), None),
            "ordinary GTK entry missing",
        )
        pid = window["pid"]
        run(["niri", "msg", "action", "focus-window", "--id", str(window["id"])])

        def entry():
            nodes = [
                n
                for n in tree(pid)
                if shown(n)
                and n.get_role() == Atspi.Role.TEXT
                and n.get_state_set().contains(Atspi.StateType.EDITABLE)
            ]
            assert len(nodes) <= 1, "ambiguous ordinary GTK field"
            return nodes[0] if nodes else None

        field = wait(entry, "GTK entry is not accessible")
        assert Atspi.Component.grab_focus(field.get_component_iface())
        assert node_text(field) == ""
        save("before-typing.json", snapshot(pid))
        run(["grim", str(output / "before-typing.png")])
        keys_env = dict(os.environ, DISPLAY=os.environ["TYPELAY_VERIFY_XDISPLAY"])
        outer = run(
            ["xdotool", "search", "--onlyvisible", "--name", "^niri$"], env=keys_env
        ).split()
        assert len(outer) == 1, "owned Xvfb must have one niri window"
        run(["xdotool", "windowfocus", "--sync", outer[0]], env=keys_env)
        run(["xdotool", "type", "--clearmodifiers", "--delay", "60", source], env=keys_env)
        wait(lambda: node_text(field) == source, "typed source differs from GTK field")
        wait(
            lambda: (doctor().get("session") or {}).get("viewState") == "ValidTranslation",
            "no valid translation",
            90,
        )
        rows = wait(
            lambda: (
                rows if any(re.search(r"[\u4e00-\u9fff]", row["text"]) for row in rows) else None
            )
            if (rows := snapshot(pid))
            else None,
            "translated GTK text missing",
        )
        assert node_text(field) == source, "preview unexpectedly changed the source"
        save("translation.json", rows)
        after = doctor()
        assert after["session"]["sourceSupported"] is True
        assert after["session"]["nativePeers"] == 1
        assert after["session"]["modelReady"] is True
        save("doctor-after.json", after)
        run(["grim", str(output / "translation.png")])
        inventory = (
            Path(os.environ["XDG_DATA_HOME"]) / "typelay/components/current/models/installed.json"
        )
        shutil.copyfile(inventory, output / "installed.json")
        save(
            "result.json",
            {
                "status": "passed",
                "feature": "preview/gtk3-entry",
                "source": source,
                "sourceUnchanged": True,
                "translatedText": [
                    row["text"] for row in rows if re.search(r"[\u4e00-\u9fff]", row["text"])
                ],
                "physicalInputTested": False,
                "writebackTested": False,
            },
        )
    finally:
        # The outer session also reaps all descendants after success, failure or timeout.
        for command in (
            [app, "--quit"],
            [str(cooperative.parent / "core/bin/fcitx5-remote"), "-e"],
        ):
            try:
                run(command, timeout=10)
            except (subprocess.SubprocessError, OSError):
                pass


if __name__ == "__main__":
    main()
