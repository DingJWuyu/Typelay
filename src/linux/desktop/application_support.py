"""Linux installation facts and explicit actions behind application support settings."""

import asyncio
import hashlib
import importlib.util
import json
import os
import re
import shlex
import shutil
import signal
import stat
from datetime import datetime, timedelta, timezone
from pathlib import Path

from wire import now_ms

BROWSERS = ("firefox", "chromium", "google-chrome")
BROWSER_MANAGEMENT_URIS = {
    "firefox": "about:debugging#/runtime/this-firefox",
    "chromium": "chrome://extensions/",
    "google-chrome": "chrome://extensions/",
}


class ApplicationSupport:
    """Reuse the shipped installer; registration never implies a live connection."""

    def __init__(self, paths):
        self.paths = paths
        self.root = Path.home() / ".local/lib/typelay"
        self.installer = None
        self.last_results = {}
        self.cooperative_directory = None
        installer_path = paths.home / "install.py"
        if installer_path.is_file():
            specification = importlib.util.spec_from_file_location(
                "typelay_installer", installer_path
            )
            try:
                installer = importlib.util.module_from_spec(specification)
                specification.loader.exec_module(installer)
                self.installer = installer
            except (OSError, ImportError, SyntaxError, ValueError):
                pass

    def browser_status(self, browser):
        if browser not in BROWSERS:
            raise ValueError("unknown-browser")
        if self.installer is None:
            return {"state": "unavailable", "reason": "installer-missing"}
        executable = self.root / "current/typelay-browser-host"
        host_installed = executable.is_file() and os.access(executable, os.X_OK)
        manifest = self.installer.browser_manifest(browser)
        if not manifest.exists() and not manifest.is_symlink():
            return {
                "state": "not-registered",
                "reason": "browser-registration-missing"
                if host_installed
                else "browser-host-missing",
            }
        if not self.installer.browser_owned(manifest, self.root):
            return {"state": "conflict", "reason": "foreign-browser-registration"}
        try:
            value = json.loads(manifest.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {"state": "conflict", "reason": "browser-registration-unreadable"}
        if not isinstance(value, dict):
            return {"state": "conflict", "reason": "browser-registration-unreadable"}
        allowed = value.get("allowed_extensions" if browser == "firefox" else "allowed_origins")
        if browser == "firefox":
            valid = allowed == ["typelay@typelay.local"]
            extension_id = "typelay@typelay.local"
        else:
            match = (
                re.fullmatch(r"chrome-extension://([a-p]{32})/", allowed[0])
                if isinstance(allowed, list) and len(allowed) == 1 and isinstance(allowed[0], str)
                else None
            )
            valid = match is not None
            extension_id = match[1] if match else None
        if not host_installed:
            return {
                "state": "repair-needed",
                "reason": "browser-host-missing",
                "extensionId": extension_id,
            }
        return {
            "state": "registered" if valid else "repair-needed",
            "reason": "awaiting-browser-connection" if valid else "browser-registration-invalid",
            "extensionId": extension_id,
        }

    def browser_extension_ids(self, browser):
        """Read only registrations whose unpacked path is this shipped extension."""
        if browser not in BROWSERS or browser == "firefox" or self.installer is None:
            return []
        extension = self.component_folder(browser)
        if not (extension / "manifest.json").is_file():
            return []
        profile_root = self.installer.browser_manifest(browser).parent.parent
        result = set()
        for profile in [profile_root / "Default", *profile_root.glob("Profile *")]:
            for name in ("Preferences", "Secure Preferences"):
                preferences = profile / name
                try:
                    if preferences.stat().st_size > 16 * 1024 * 1024:
                        continue
                    value = json.loads(preferences.read_text(encoding="utf-8"))
                    entries = value.get("extensions", {}).get("settings", {})
                    if not isinstance(entries, dict):
                        continue
                    for identity, entry in entries.items():
                        if not re.fullmatch(r"[a-p]{32}", identity) or not isinstance(entry, dict):
                            continue
                        path = entry.get("path")
                        if (
                            isinstance(path, str)
                            and Path(path).is_absolute()
                            and Path(path).resolve() == extension.resolve()
                        ):
                            result.add(identity)
                except (OSError, ValueError, AttributeError, RuntimeError):
                    continue
        return sorted(result)

    async def browser_action(self, browser, action, extension_id=None):
        result = await self._browser_action(browser, action, extension_id)
        self.last_results[browser] = result
        return result

    async def _browser_action(self, browser, action, extension_id=None):
        if browser not in BROWSERS or action not in ("connect", "remove"):
            return {"ok": False, "reason": "invalid-support-action"}
        status = self.browser_status(browser)
        if status["state"] in ("unavailable", "conflict") or (
            action == "connect" and status["reason"] == "browser-host-missing"
        ):
            return {"ok": False, "reason": status["reason"]}
        if action == "connect" and browser != "firefox" and extension_id is None:
            identities = self.browser_extension_ids(browser)
            if len(identities) == 1:
                extension_id = identities[0]
            elif not identities and status.get("extensionId"):
                # A validated registration remains authoritative across package
                # relocation; do not require the user to retype its identifier.
                extension_id = status["extensionId"]
            else:
                return {"ok": False, "reason": "browser-extension-unconfirmed"}
        if (
            action == "connect"
            and browser != "firefox"
            and not re.fullmatch(r"[a-p]{32}", extension_id or "")
        ):
            return {"ok": False, "reason": "chromium-extension-id-required"}
        arguments = [
            "/usr/bin/python3",
            "-I",
            "-B",
            str(self.paths.home / "install.py"),
            "browser-install" if action == "connect" else "browser-uninstall",
            "--browser",
            browser,
        ]
        if action == "connect" and browser != "firefox":
            arguments.extend(["--extension-id", extension_id])
        process = None
        try:
            process = await asyncio.create_subprocess_exec(
                *arguments, stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL
            )
            async with asyncio.timeout(15):
                code = await process.wait()
            actual = self.browser_status(browser)
            expected = "registered" if action == "connect" else "not-registered"
            if code == 0 and actual["state"] == expected:
                return {"ok": True, "reason": actual["reason"]}
            return {
                "ok": False,
                "reason": actual["reason"]
                if actual["state"] in ("conflict", "unavailable")
                else "browser-registration-failed",
            }
        except (OSError, TimeoutError):
            return {"ok": False, "reason": "browser-registration-failed"}
        finally:
            if process is not None and process.returncode is None:
                process.terminate()
                await process.wait()

    def facts(self, peers=(), enabled=True):
        """Return recent diagnostics, never authority to capture or modify a field."""
        connected = set()
        observations = {}
        stamp = now_ms()
        checked_at = datetime.now(timezone.utc)
        capability_keys = ("preview", "tabAccept", "directReplace")
        browser_executables = {
            "firefox": "firefox",
            "firefox-bin": "firefox",
            "chromium": "chromium",
            "chromium-browser": "chromium",
            "chrome": "google-chrome",
            "google-chrome": "google-chrome",
        }
        for peer in peers:
            if not peer.lifetime or not peer.link.channel.alive:
                continue
            identity = (
                "desktop-text"
                if peer.backend in ("gtk3", "gtk4", "qt6")
                else browser_executables.get(peer.application)
                if peer.backend == "browser"
                else None
            )
            if identity is None:
                continue
            connected.add(identity)
            observation = getattr(peer, "support_observation", None)
            if (
                not isinstance(observation, dict)
                or type(observation.get("atMs")) is not int
                or not 0 <= stamp - observation["atMs"] <= 30000
                or any(type(observation.get(key)) is not bool for key in capability_keys)
            ):
                continue
            previous = observations.get(identity)
            if previous is None or observation["atMs"] > previous["atMs"]:
                observations[identity] = {
                    key: observation[key] for key in ("atMs", *capability_keys)
                }
            elif observation["atMs"] == previous["atMs"]:
                # Simultaneous fields must not combine into a capability no
                # individual field demonstrated.
                for key in capability_keys:
                    previous[key] = previous[key] and observation[key]
        facts = []
        for browser in BROWSERS:
            status = self.browser_status(browser)
            state, reason = {
                "not-registered": ("NeedsInstall", "RegistrationMissing"),
                "registered": ("Registered", "AwaitingConnection"),
                "repair-needed": ("Error", "RegistrationInvalid"),
                "conflict": ("Error", "RegistrationConflict"),
                "unavailable": ("Error", "ComponentMissing"),
            }[status["state"]]
            actions = ["Refresh", "OpenInstructions"]
            if status["state"] not in ("unavailable", "conflict"):
                if status["reason"] != "browser-host-missing":
                    actions.extend(["Connect", "Repair"])
                if status["state"] in ("registered", "repair-needed"):
                    actions.append("Remove")
            if status["reason"] == "browser-host-missing":
                state, reason = "Error", "ComponentMissing"
                if (self.paths.home / "MANIFEST.sha256").is_file():
                    actions.append("Install")
            elif (
                browser != "firefox"
                and status["state"] == "not-registered"
                and len(self.browser_extension_ids(browser)) != 1
            ):
                state, reason = "AwaitingApproval", "AwaitingApproval"
                actions = [action for action in actions if action not in ("Connect", "Repair")]
            if status["state"] == "registered" and browser in connected:
                state, reason = "Connected", "FieldUnobserved"
            result = self.last_results.get(browser)
            if (
                result
                and result.get("reason") == "browser-extension-unconfirmed"
                and len(self.browser_extension_ids(browser)) == 1
            ):
                result = None
            if (
                result
                and not result["ok"]
                and state != "Connected"
                and reason not in ("RegistrationConflict", "ComponentMissing")
            ):
                reason = {
                    "browser-extension-unconfirmed": "AwaitingApproval",
                    "chromium-extension-id-required": "InvalidExtensionId",
                }.get(result["reason"], "RegistrationFailed")
                state = "AwaitingApproval" if reason == "AwaitingApproval" else "Error"
            details = self.instructions(browser)
            if status["reason"] == "browser-host-missing":
                details = (
                    "本地通信主机尚未安装。安装操作会把当前 TypeLay 包部署到用户目录，创建用户级启动入口；"
                    "不使用 sudo，不下载模型，不修改输入法或浏览器权限。完成后还需连接所选浏览器。\n\n"
                    + details
                )
            if status["state"] == "not-registered" and browser in connected:
                state, reason = "RestartRequired", "RestartRequired"
                actions = ["Refresh", "OpenInstructions"]
                details = (
                    "本地通信注册已移除，但浏览器现有会话仍与 TypeLay 连接。"
                    "请先保存网页中的工作，再完整退出该浏览器；关闭一个标签页不代表连接已经结束。"
                    "重新打开后回到这里检查连接。\n\n"
                    "如不再使用 TypeLay，可在浏览器扩展页移除扩展。"
                    "TypeLay 不会结束浏览器进程或更改其扩展权限。"
                )
            facts.append(
                {
                    "id": browser,
                    "state": state,
                    "reason": reason,
                    "capabilities": {},
                    "supportedActions": actions,
                    "details": details,
                    "applicationDetected": bool(
                        shutil.which(browser)
                        or browser in connected
                        or status["state"] == "registered"
                    ),
                }
            )
        desktop_state, desktop_reason = "NeedsInstall", "ComponentMissing"
        desktop_details = self.instructions("desktop-text")
        component = self.last_results.get("desktop-text")
        if component:
            desktop_state = "AwaitingApproval" if component["ok"] else "Error"
            desktop_reason = "UserApprovalRequired" if component["ok"] else "DetectionFailed"
            desktop_details += "\n\n" + component.get("detail", "")
        if "desktop-text" in connected:
            desktop_state, desktop_reason = "Connected", "FieldUnobserved"
        facts.append(
            {
                "id": "desktop-text",
                "state": desktop_state,
                "reason": desktop_reason,
                "capabilities": {},
                "supportedActions": ["CheckComponent", "Refresh", "OpenInstructions"],
                "details": desktop_details,
                "applicationDetected": True,
            }
        )
        facts.append(
            {
                "id": "vscode",
                "state": "Unknown",
                "reason": "AwaitingApproval",
                "capabilities": {},
                "supportedActions": ["Refresh", "OpenInstructions"],
                "details": self.instructions("vscode"),
                "applicationDetected": bool(shutil.which("code")),
            }
        )
        # No Linux Edge installer or verified live detection exists here.
        facts.append(
            {
                "id": "microsoft-edge",
                "state": "Unsupported",
                "reason": "NotImplemented",
                "capabilities": {},
                "supportedActions": ["Refresh"],
                "applicationDetected": bool(shutil.which("microsoft-edge")),
            }
        )
        for fact in facts:
            fact["checkedAt"] = None
            if not enabled and fact["state"] in ("Connected", "Registered", "BuiltInAvailable"):
                fact["reason"] = "Paused"
                fact["capabilities"] = {}
                continue
            observation = observations.get(fact["id"])
            if fact["state"] != "Connected" or observation is None:
                continue
            capabilities = {key: observation[key] for key in capability_keys}
            fact["capabilities"] = capabilities
            fact["reason"] = (
                "PreviewOnly"
                if capabilities["preview"]
                and not capabilities["tabAccept"]
                and not capabilities["directReplace"]
                else "NoDirectWrite"
                if capabilities["preview"]
                and capabilities["tabAccept"]
                and not capabilities["directReplace"]
                else "LimitedFields"
            )
            fact["checkedAt"] = (
                checked_at - timedelta(milliseconds=stamp - observation["atMs"])
            ).isoformat(timespec="milliseconds")
        return facts

    def instructions(self, identity):
        """Platform-specific permission steps, without assigning product state."""
        if identity == "firefox":
            return (
                "1. 打开 Firefox 扩展页（about:debugging#/runtime/this-firefox），"
                "选择“临时载入附加组件”，在下方文件夹中选择 manifest.json。\n"
                "2. 在浏览器确认网页访问权限，回到此页连接。\n"
                "3. 保存网页中的工作后刷新目标网页，在普通文本框输入后回到这里检查连接。\n\n"
                "随包扩展尚未获 Mozilla 签名；临时扩展在 Firefox 退出后失效。"
                "每次重新启动 Firefox 后需重新临时加载。TypeLay 不关闭签名校验。"
            )
        if identity in ("chromium", "google-chrome"):
            return (
                "1. 打开浏览器后，复制下方管理地址，粘贴到浏览器地址栏并打开。"
                "浏览器不允许 TypeLay 直接打开这个内部页面。\n"
                "2. 在扩展页开启开发者模式，选择“加载已解压的扩展程序”，"
                "选择下方 TypeLay 扩展文件夹。\n"
                "3. 在浏览器确认网页访问权限，回到此页连接。无需复制扩展 ID。\n"
                "4. 保存网页中的工作后刷新目标网页，在普通文本框输入后回到这里检查连接。\n\n"
                "随包扩展尚未上架商店，只提供开发加载。TypeLay 只使用浏览器标准配置中与本包路径"
                "完全匹配的唯一扩展记录；尚未找到或发现多个记录时不会注册。"
                "若加载后仍没有连接按钮，请保存工作，正常退出并重新打开浏览器后再检查。"
            )
        if identity == "desktop-text":
            return (
                "普通 GTK/Qt 输入框需要匹配且来源可信的可选 Fcitx 合作包。"
                "可以选择组件文件夹作静态完整性检查，但只与该包自身清单比对，不验证来源、系统依赖或运行兼容。"
                "本页不会执行所选目录中的脚本或程序。\n"
                "切换前须完成输入法候选、退出原 Fcitx 并备份个人配置；如桌面会自动重启原服务，"
                "须先自行暂停它。按随包说明启用私有会话并重新打开目标应用。"
                "恢复时退出私有会话并重新启动原输入法；个人词库、模型和 TypeLay 设置保留。\n"
                "GTK3/GTK4 标准单行和纯文本多行、Qt6 标准单行和纯文本多行仍有各自验证边界。"
                "仅加载模块而没有明确输入法事务时，TypeLay 保持不可写。"
            )
        if identity == "vscode":
            return (
                "在 VS Code 扩展视图选择“从 VSIX 安装”，选择下方文件夹中的 typelay-editor.vsix。"
                "然后在用户设置中将 typelay.hostPath 设为下方启动器路径。\n"
                "请先完成输入法候选；这里只提供安装入口，尚未验证编辑器扩展已安装或连接。\n"
                + str(self.paths.launcher)
            )
        return ""

    def component_folder(self, identity):
        installed = self.root / "current"
        home = installed if (installed / "typelay-browser-host").is_file() else self.paths.home
        if identity == "firefox":
            return home / "browser/firefox"
        if identity in ("chromium", "google-chrome"):
            return home / "browser/chromium"
        if identity == "vscode":
            return home / "vscode"
        if identity == "desktop-text" and self.cooperative_directory:
            return self.cooperative_directory
        return self.paths.home

    def open_browser(self, browser):
        """Launch the selected browser; never load or approve an extension."""
        if browser not in BROWSERS:
            raise ValueError("unknown-browser")
        executable = shutil.which(browser)
        if executable is None:
            raise OSError("browser-not-installed")
        from gi.repository import Gio, GLib

        arguments = (
            [executable, "--new-tab", BROWSER_MANAGEMENT_URIS[browser]]
            if browser == "firefox"
            # Chromium rejects external command-line navigation to internal
            # extension pages. The user opens the copied address themselves.
            else [executable, "about:blank"]
        )
        try:
            application = Gio.AppInfo.create_from_commandline(
                shlex.join(arguments), None, Gio.AppInfoCreateFlags.NONE
            )
            if not application.launch([], None):
                raise OSError("browser-launch-failed")
        except GLib.Error as error:
            raise OSError("browser-launch-failed") from error

    async def install_client(self):
        process = None
        try:
            process = await asyncio.create_subprocess_exec(
                "/usr/bin/python3",
                "-I",
                "-B",
                str(self.paths.home / "install.py"),
                "install",
                stdout=asyncio.subprocess.DEVNULL,
                stderr=asyncio.subprocess.DEVNULL,
                start_new_session=True,
            )
            async with asyncio.timeout(120):
                code = await process.wait()
            executable = self.root / "current/typelay-browser-host"
            ok = code == 0 and executable.is_file() and os.access(executable, os.X_OK)
            return {"ok": ok, "reason": "client-installed" if ok else "client-installation-failed"}
        except (OSError, TimeoutError):
            return {"ok": False, "reason": "client-installation-failed"}
        finally:
            if process is not None and process.returncode is None:
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    async with asyncio.timeout(3):
                        await process.wait()
                except TimeoutError:
                    try:
                        os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                    await process.wait()

    @staticmethod
    def inspect_component(directory):
        """Hash declared files only. A self-authored manifest is not provenance."""
        limitation = (
            "完整性检查只比对所选包自身清单，不验证来源、系统依赖或运行兼容；没有执行包内程序。"
        )
        try:
            root = Path(directory).resolve(strict=True)
            manifest = root / "manifest.json"
            if manifest.is_symlink() or manifest.stat().st_size > 4 * 1024 * 1024:
                raise ValueError("manifest-unavailable")
            value = json.loads(manifest.read_text(encoding="utf-8"))
            files = value.get("files")
            if (
                value.get("schemaVersion") != 1
                or not isinstance(files, dict)
                or not 0 < len(files) <= 20000
            ):
                raise ValueError("manifest-invalid")
            if "fcitx-cooperative.py" not in files:
                raise ValueError("cooperative-launcher-missing")
            total = 0
            for relative, expected in files.items():
                if not isinstance(expected, dict):
                    raise ValueError("manifest-entry-invalid")
                path = Path(relative)
                if path.is_absolute() or ".." in path.parts or not path.parts:
                    raise ValueError("manifest-path-invalid")
                file = root / path
                if not file.resolve().is_relative_to(root):
                    raise ValueError("manifest-path-escaped")
                if "symlink" in expected:
                    if not file.is_symlink() or os.readlink(file) != expected["symlink"]:
                        raise ValueError("bundle-link-mismatch")
                else:
                    info = file.lstat()
                    digest = expected.get("sha256")
                    if (
                        not stat.S_ISREG(info.st_mode)
                        or not isinstance(digest, str)
                        or not re.fullmatch(r"[0-9a-f]{64}", digest)
                    ):
                        raise ValueError("bundle-file-invalid")
                    total += info.st_size
                    if total > 16 * 1024**3:
                        raise ValueError("bundle-size-limit")
                    with file.open("rb") as stream:
                        if hashlib.file_digest(stream, "sha256").hexdigest() != digest:
                            raise ValueError("bundle-checksum-mismatch")
            return {"ok": True, "reason": "cooperative-integrity-checked", "detail": limitation}
        except (OSError, ValueError, TypeError, AttributeError, RuntimeError) as error:
            return {
                "ok": False,
                "reason": "cooperative-integrity-failed",
                "detail": limitation + "\n" + str(error),
            }

    async def execute(self, identity, action, directory=None):
        if identity in BROWSERS and action == "OpenInstructions":
            self.open_browser(identity)
            return {"ok": True}
        if action == "Refresh":
            if identity in BROWSERS:
                self.last_results.pop(identity, None)
            return {"ok": True}
        if identity in BROWSERS and action == "Install":
            result = await self.install_client()
            self.last_results[identity] = result
            return result
        if identity in BROWSERS and action in ("Connect", "Repair", "Remove"):
            return await self.browser_action(
                identity, "remove" if action == "Remove" else "connect"
            )
        if identity == "desktop-text" and action == "CheckComponent" and directory:
            self.cooperative_directory = Path(directory)
            result = await asyncio.to_thread(self.inspect_component, directory)
            self.last_results[identity] = result
            return result
        return {"ok": False, "reason": "invalid-support-action"}
