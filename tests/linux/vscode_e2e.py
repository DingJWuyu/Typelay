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


def profile_data_home(profile, model_data):
    if model_data is None:
        return profile / "share"
    model_data = model_data.resolve(strict=True)
    if not model_data.is_dir() or model_data.name != "typelay":
        raise ValueError("--model-data must identify the existing test XDG data directory typelay")
    # The production store rejects a symlinked components root. Use the existing
    # test installation directly; HOME, config, cache and runtime stay isolated.
    return model_data.parent


def visible_terminal_notification(page):
    """Observe only fixed terminal messages owned by the isolated TypeLay extension."""
    messages = {
        "unsupported-environment": "TypeLay 编辑器命令仅支持本机 Linux 的受信任工作区。",
        "host-not-configured": (
            "请在用户设置 typelay.hostPath 中填写已安装 TypeLay 启动器的绝对路径。"
        ),
        "unsupported-editor": (
            "TypeLay：仅支持本地纯文本或 Markdown 单选区。"
            "代码、终端、远程文档和未知编辑器不读取正文。"
        ),
        "invalid-text": "TypeLay：正文为空、包含无效字符或超过 8192 UTF-16 单位，未翻译。",
        "disabled": "TypeLay：TypeLay 当前已暂停。请在 TypeLay 设置中启用后重试。",
        "not-ready": "TypeLay：本机模型未就绪。请先在 TypeLay 中完成模型安装。",
        "no-translation": "TypeLay：本机模型没有为当前文本提供译文，原文未修改。",
        "translation-incomplete": (
            "TypeLay：本机翻译未完成。请检查 TypeLay 启动器和已安装模型；原文未由此命令替换。"
        ),
    }
    rows = page.locator(".notifications-list-container .monaco-list-row:visible")
    for status, message in messages.items():
        # VS Code's notification row label retains the extension displayName
        # even when its source/details row is collapsed. Require that owner and
        # the exact rendered message; never read generic alerts or editor text.
        label = re.compile(
            "^Warning: "
            + re.escape(message)
            + r", source: TypeLay Editor, notification(?:, [^\r\n]*)?$"
        )
        notice = rows.and_(page.get_by_label(label)).locator(
            ".notification-list-item-message:visible"
        )
        if notice.filter(has_text=re.compile("^" + re.escape(message) + "$")).first.is_visible():
            return status
    return None


def drive_preview_ui(port, process, output, source):
    """Exercise the installed extension in normal Code, not extension-test mode."""
    from playwright.sync_api import Error, TimeoutError, sync_playwright

    original = "你好，世界。\n保留原文"
    terminal_notification = None
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
        session = browser.new_browser_cdp_session()
        deadline = time.monotonic() + 45
        while True:
            # Sync Playwright dispatches page/navigation events during API calls,
            # not Python sleep or cached browser.contexts/page.url reads.
            targets = session.send("Target.getTargets")
            pages = [page for context in browser.contexts for page in context.pages]
            page = next((item for item in pages if item.url.startswith("vscode-file:")), None)
            if page is not None:
                break
            if process.poll() is not None or time.monotonic() >= deadline:
                (output / "cdp-targets.json").write_text(json.dumps(targets), encoding="utf-8")
                raise RuntimeError("Code did not expose its workbench page")
            time.sleep(0.1)

        def quick_input(shortcut, value):
            page.keyboard.press(shortcut)
            box = page.locator(".quick-input-widget input").first
            box.wait_for(state="visible", timeout=10000)
            box.fill(value)
            return box

        def command(title):
            box = quick_input("Control+Shift+P", ">" + title)
            page.locator(".quick-input-list .monaco-list-row").filter(
                has_text=title
            ).first.wait_for(state="visible", timeout=10000)
            box.press("Enter")
            box.wait_for(state="hidden", timeout=10000)

        def dialog(title):
            nonlocal terminal_notification
            value = page.get_by_role("dialog").filter(has_text=title)
            deadline = time.monotonic() + 215
            while True:
                if process.poll() is not None:
                    raise RuntimeError("Code exited before the expected preview interaction")
                try:
                    value.wait_for(state="visible", timeout=1000)
                    return value
                except TimeoutError:
                    terminal_notification = visible_terminal_notification(page)
                    if terminal_notification is not None:
                        (output / "typelay-terminal-notification.json").write_text(
                            json.dumps({"status": "failed", "notification": terminal_notification}),
                            encoding="utf-8",
                        )
                        raise RuntimeError(
                            f"TypeLay reported a terminal notification: {terminal_notification}"
                        ) from None
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
                        raise

        def save_and_expect(check):
            page.keyboard.press("Control+s")
            deadline = time.monotonic() + 10
            while True:
                text = source.read_text(encoding="utf-8")
                if check(text):
                    return text
                if process.poll() is not None or time.monotonic() >= deadline:
                    page.screenshot(path=str(output / "ui-failure.png"))
                    raise AssertionError("Saved source did not match the expected editor operation")
                time.sleep(0.05)

        try:
            # The requested file is already opened by the normal CLI. A page URL
            # exists before workbench startup and keybindings have completed.
            editor = (
                page.locator(".monaco-editor .view-lines").filter(has_text="你好，世界。").first
            )
            editor.wait_for(state="visible", timeout=45000)
            editor.get_by_text("你好，世界。", exact=True).click()
            page.keyboard.press("Control+Home")
            page.keyboard.press("Shift+End")
            command("TypeLay: 翻译所选文字（先完成输入法候选）")
            preview = dialog("TypeLay 翻译预览")
            assert "你好，世界。" in preview.inner_text() and "译文" in preview.inner_text()
            assert "你好，世界。" in editor.inner_text() and "保留原文" in editor.inner_text()
            assert source.read_text(encoding="utf-8") == original
            page.screenshot(path=str(output / "selected-preview.png"))
            preview.get_by_role("button", name="确认替换", exact=True).click()
            preview.wait_for(state="hidden", timeout=10000)
            translated = save_and_expect(
                lambda text: text.endswith("\n保留原文")
                and bool(re.search(r"[A-Za-z]", text.split("\n")[0]))
            )
            assert translated != original
            page.screenshot(path=str(output / "selected-replaced.png"))
            page.keyboard.press("Control+z")
            save_and_expect(lambda text: text == original)

            page.keyboard.press("Control+Home")
            page.keyboard.press("Shift+ArrowRight")
            command("TypeLay: 翻译整篇文档")
            choice = dialog("翻译整篇文档？")
            page.screenshot(path=str(output / "whole-choice.png"))
            choice.get_by_role("button", name="翻译整篇文档", exact=True).click()
            choice.wait_for(state="hidden", timeout=10000)
            preview = dialog("TypeLay 翻译预览")
            assert "你好，世界。" in preview.inner_text() and "保留原文" in preview.inner_text()
            page.screenshot(path=str(output / "whole-preview.png"))
            preview.get_by_role("button", name=re.compile(r"^(?:Cancel|取消)$")).click()
            preview.wait_for(state="hidden", timeout=10000)
            save_and_expect(lambda text: text == original)
            assert "你好，世界。" in editor.inner_text() and "保留原文" in editor.inner_text()
        except Exception:
            if terminal_notification is not None:
                # The finite notification state is sufficient for this failure.
                raise
            page.screenshot(path=str(output / "ui-failure.png"))
            (output / "ui-failure.txt").write_text(
                page.locator("body").inner_text(), encoding="utf-8"
            )
            raise


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
        help="Existing test XDG data directory ending in typelay; used only with --host",
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
    if arguments.host and not arguments.vsix:
        parser.error("--host UI testing requires --vsix installation")
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
        source = profile / "source.txt"
        data_home = profile_data_home(profile, arguments.model_data)
        if arguments.host:
            source.write_text("你好，世界。\n保留原文", encoding="utf-8")
            settings_directory = profile / "data" / "User"
            settings_directory.mkdir(parents=True)
            (settings_directory / "settings.json").write_text(
                json.dumps(
                    {
                        "typelay.hostPath": str(arguments.host.resolve(strict=True)),
                        "window.dialogStyle": "custom",
                        "workbench.startupEditor": "none",
                        "telemetry.telemetryLevel": "off",
                        "files.autoSave": "off",
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
            "--extensionDevelopmentPath=" + str(root / "src/linux/vscode"),
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
            XDG_DATA_HOME=str(data_home),
            XDG_CACHE_HOME=str(profile / "cache"),
            XDG_RUNTIME_DIR=str(runtime),
            TYPELAY_VSCODE_RESULT=str(result_file),
        )
        # Never inherit Node startup injection into the isolated editor process.
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
        if arguments.host:
            # DialogService deliberately refuses prompts in extension-test mode.
            # Use the normal installed extension and real Command Palette instead.
            command = [
                argument
                for argument in command
                if not argument.startswith(("--extensionDevelopmentPath=", "--extensionTestsPath="))
            ]
            command.append(str(source))
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
                    drive_preview_ui(port, process, output, source)
                    if process.poll() not in (None, 0):
                        raise RuntimeError("Code exited abnormally during the real UI test")
                    result_file.write_text(
                        json.dumps(
                            {
                                "status": "passed",
                                "mode": "normal-ui",
                                "modelHostTest": True,
                                "checks": [
                                    "installed-vsix",
                                    "local-model",
                                    "preview-before-write",
                                    "selected-replacement",
                                    "single-undo",
                                    "explicit-whole-document",
                                    "cancel-no-write",
                                ],
                            }
                        ),
                        encoding="utf-8",
                    )
                    exit_code = 0  # The normal GUI is deliberately closed by the cleanup below.
                else:
                    exit_code = process.wait(timeout=150)
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
