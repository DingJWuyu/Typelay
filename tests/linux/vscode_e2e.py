#!/usr/bin/env python3
"""Run real editor transaction regression in a disposable VS Code profile.

Requires an existing official Code/Code OSS native executable, not its CLI wrapper. Optional VSIX installation is limited to the disposable profile. Never fetches
software/models, opens user workspaces, or changes the real user profile.
"""

import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import tempfile
import time
from pathlib import Path


def drive_preview_ui(port, process, output, confirmation):
    """Click the actual custom Code dialogs; no extension API/UI substitutes."""
    from playwright.sync_api import Error, TimeoutError, sync_playwright

    with sync_playwright() as playwright:
        deadline = time.monotonic() + 45
        while True:
            if process.poll() is not None:
                raise RuntimeError("Code exited before the real UI became available")
            try:
                browser = playwright.chromium.connect_over_cdp(
                    f"http://127.0.0.1:{port}", timeout=1000
                )
                break
            except Error:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(0.1)
        deadline = time.monotonic() + 45
        while True:
            pages = [page for context in browser.contexts for page in context.pages]
            page = next((item for item in pages if item.url.startswith("vscode-file:")), None)
            if page is not None:
                break
            if process.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError("Code did not expose its workbench page")
            time.sleep(0.1)
        actions = [
            ("TypeLay 翻译预览", "确认替换", "selected-preview.png"),
            ("翻译整篇文档？", "翻译整篇文档", "whole-choice.png"),
            ("TypeLay 翻译预览", "取消", "whole-preview.png"),
        ]
        for title, button, screenshot in actions:
            dialog = page.get_by_role("dialog").filter(has_text=title)
            deadline = time.monotonic() + 215
            while True:
                if process.poll() is not None:
                    raise RuntimeError("Code exited before the expected preview interaction")
                try:
                    dialog.wait_for(state="visible", timeout=1000)
                    break
                except TimeoutError:
                    if time.monotonic() >= deadline:
                        page.screenshot(path=str(output / "ui-failure.png"))
                        (output / "dialog-roles.json").write_text(
                            json.dumps(
                                page.locator(
                                    '[role="dialog"], [role="alertdialog"]'
                                ).all_inner_texts(),
                                ensure_ascii=False,
                            ),
                            encoding="utf-8",
                        )
                        windows = subprocess.run(
                            ["xdotool", "search", "--onlyvisible", "--name", "."],
                            capture_output=True,
                            text=True,
                            check=False,
                            timeout=5,
                        )
                        titles = []
                        for window in windows.stdout.splitlines():
                            title_result = subprocess.run(
                                ["xdotool", "getwindowname", window],
                                capture_output=True,
                                text=True,
                                check=False,
                                timeout=5,
                            )
                            titles.append(title_result.stdout.strip())
                        (output / "window-titles.json").write_text(
                            json.dumps(titles, ensure_ascii=False), encoding="utf-8"
                        )
                        raise
            content = dialog.inner_text()
            if title == "TypeLay 翻译预览":
                assert "你好，世界。" in content and "译文" in content
                if screenshot == "whole-preview.png":
                    assert "保留原文" in content, (
                        "Whole-document preview must include the full source"
                    )
            page.screenshot(path=str(output / screenshot))
            if button == "确认替换":
                confirmation.write_text("user-confirmation", encoding="utf-8")
            label = re.compile(r"^(?:Cancel|取消)$") if button == "取消" else button
            dialog.get_by_role("button", name=label, exact=True).click()
            dialog.wait_for(state="hidden", timeout=10000)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--code", required=True, type=Path)
    parser.add_argument(
        "--vsix", type=Path, help="Install and exercise this package in the disposable profile"
    )
    parser.add_argument(
        "--code-cli", type=Path, help="Official Code CLI, used only for isolated VSIX installation"
    )
    parser.add_argument(
        "--model-data",
        type=Path,
        help="Existing test TypeLay data directory; used only with --host",
    )
    parser.add_argument(
        "--app", type=Path, help="Code application root for a split Electron package"
    )
    parser.add_argument(
        "--host", type=Path, help="Also require translation using this installed TypeLay host"
    )
    parser.add_argument("--output", type=Path, default=Path("artifacts/linux/vscode"))
    arguments = parser.parse_args()
    if bool(arguments.vsix) != bool(arguments.code_cli):
        parser.error("--vsix and --code-cli must be supplied together")
    if bool(arguments.host) != bool(arguments.model_data):
        parser.error("--host and --model-data must be supplied together")
    root = Path(__file__).resolve().parents[2]
    code = arguments.code.resolve(strict=True)
    with code.open("rb") as executable:
        if executable.read(4) != b"\x7fELF":
            raise ValueError("--code must name the native ELF executable, not the Code CLI wrapper")
    app = arguments.app.resolve(strict=True) if arguments.app else None
    output = arguments.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    log = output / "extension-host.log"
    storage = {
        str(directory): dict(zip(("total", "used", "free"), shutil.disk_usage(directory)))
        for directory in (Path("/dev/shm"), Path(tempfile.gettempdir()))
        if directory.exists()
    }
    (output / "storage.json").write_text(json.dumps(storage), encoding="utf-8")
    with tempfile.TemporaryDirectory(prefix="typelay-vscode-") as temporary:
        profile = Path(temporary)
        result_file = profile / "test-result.json"
        home = profile / "home"
        home.mkdir()
        runtime = profile / "runtime"
        runtime.mkdir(mode=0o700)
        port = None
        confirmation = profile / "confirmed.txt"
        if arguments.host:
            model_data = arguments.model_data.resolve(strict=True)
            data = profile / "share" / "typelay"
            data.mkdir(parents=True)
            (data / "components").symlink_to(model_data / "components", target_is_directory=True)
            settings_directory = profile / "data" / "User"
            settings_directory.mkdir(parents=True)
            (settings_directory / "settings.json").write_text(
                json.dumps(
                    {
                        "typelay.hostPath": str(arguments.host.resolve(strict=True)),
                        "window.dialogStyle": "custom",
                        "workbench.startupEditor": "none",
                        "telemetry.telemetryLevel": "off",
                    }
                ),
                encoding="utf-8",
            )
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                port = listener.getsockname()[1]
        command = [
            str(code),
            *([str(app)] if app else []),
            "--no-sandbox",
            "--verbose",
            "--disable-gpu",
            "--disable-workspace-trust",
            "--skip-welcome",
            "--skip-release-notes",
            "--disable-telemetry",
            "--disable-updates",
            "--user-data-dir",
            str(profile / "data"),
            "--extensions-dir",
            str(profile / "extensions"),
            "--extensionDevelopmentPath=" + str(root / "src/TypeLay.Linux/vscode"),
            "--extensionTestsPath=" + str(root / "tests/linux/vscode_e2e.js"),
        ]
        if port:
            command.extend(
                ["--remote-debugging-address=127.0.0.1", f"--remote-debugging-port={port}"]
            )
        environment = os.environ.copy()
        environment.update(
            HOME=str(home),
            XDG_CONFIG_HOME=str(profile / "config"),
            XDG_DATA_HOME=str(profile / "share"),
            XDG_CACHE_HOME=str(profile / "cache"),
            XDG_RUNTIME_DIR=str(runtime),
            TYPELAY_VSCODE_RESULT=str(result_file),
            TYPELAY_VSCODE_CONFIRM=str(confirmation),
        )
        # Never inherit Node startup injection into the isolated editor process.
        if arguments.host:
            environment["TYPELAY_EDITOR_HOST"] = str(arguments.host.resolve(strict=True))
        else:
            environment.pop("TYPELAY_EDITOR_HOST", None)
        environment.pop("NODE_OPTIONS", None)
        environment.pop("ELECTRON_RUN_AS_NODE", None)
        environment.pop("VSCODE_CLI", None)
        environment.pop("VSCODE_IPC_HOOK_CLI", None)
        if arguments.vsix:
            with (output / "install.log").open("w", encoding="utf-8") as install_log:
                subprocess.run(
                    [
                        str(arguments.code_cli.resolve(strict=True)),
                        "--no-sandbox",
                        "--user-data-dir",
                        str(profile / "data"),
                        "--extensions-dir",
                        str(profile / "extensions"),
                        "--install-extension",
                        str(arguments.vsix.resolve(strict=True)),
                        "--force",
                    ],
                    env=environment,
                    stdout=install_log,
                    stderr=subprocess.STDOUT,
                    timeout=60,
                    check=True,
                )
            installed = []
            for manifest in (profile / "extensions").glob("*/package.json"):
                package = json.loads(manifest.read_text(encoding="utf-8"))
                if (
                    package.get("publisher") == "typelay-local"
                    and package.get("name") == "typelay-editor"
                ):
                    installed.append(manifest.parent)
            if len(installed) != 1:
                raise RuntimeError("The VSIX did not install exactly one TypeLay extension")
            command = [
                "--extensionDevelopmentPath=" + str(installed[0])
                if argument.startswith("--extensionDevelopmentPath=")
                else argument
                for argument in command
            ]
        with log.open("w", encoding="utf-8") as stream:
            process = subprocess.Popen(
                command,
                env=environment,
                start_new_session=True,
                stdout=stream,
                stderr=subprocess.STDOUT,
            )
            try:
                if port:
                    drive_preview_ui(port, process, output, confirmation)
                exit_code = process.wait(timeout=300 if arguments.host else 150)
            finally:
                import signal

                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)
                # CLI output is not the extension host log. Preserve evidence
                # before TemporaryDirectory cleanup, including startup failures.
                profile_logs = profile / "data" / "logs"
                if profile_logs.exists():
                    shutil.copytree(profile_logs, output / "profile-logs", dirs_exist_ok=True)
                if result_file.exists():
                    shutil.copyfile(result_file, output / "test-result.json")
        if exit_code:
            raise RuntimeError(f"VS Code exited {exit_code}; see {log}")
        if not result_file.exists():
            raise RuntimeError(f"VS Code did not start the transaction test runner; see {output}")
        result = json.loads(result_file.read_text(encoding="utf-8"))
        if result.get("status") != "passed":
            raise RuntimeError(f"VS Code transaction assertions did not pass; see {output}")
        print("TypeLay VS Code real editor transaction tests passed.")


if __name__ == "__main__":
    main()
