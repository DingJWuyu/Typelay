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

from desktop_component import DesktopComponent
from desktop_session import DesktopSession
from fcitx_session_source import detect_fcitx_source
from paths import process_identity
from registered_applications import list_applications, resolve_application
from wire import now_ms

BROWSERS = ("firefox", "chromium", "google-chrome")
BROWSER_EXECUTABLES = {
    "firefox": "firefox",
    "firefox-bin": "firefox",
    "chromium": "chromium",
    "chromium-browser": "chromium",
    "chrome": "google-chrome",
    "google-chrome": "google-chrome",
}
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
        self.desktop_component = DesktopComponent(paths)
        self.desktop_session = DesktopSession(paths)
        self.desktop_session_state = {"state": "unchecked"}
        self.desktop_component_prepared = False
        self.refresh_revision = 0
        self.practice_launch = None
        self.selected_application = None
        self.application_launch = None
        self.application_result = ""
        self.application_failed = False
        self.original_method_recorded = False
        self.no_original_method = False
        self.original_input_seen = False
        self.practice_session_started = False
        self.practice_component_removed = False
        self.practice_session_stopped = False
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

    def browser_registration(self, origin, host_home, ancestors):
        """An explicit, current registration for this installed browser host.

        Ancestor identities come from the socket peer's original /proc lineage,
        never from the browser message. This is the existing same-user boundary,
        not isolation from malicious processes running as that user.
        """
        if (
            not isinstance(origin, str)
            or not re.fullmatch(r"chrome-extension://[a-p]{32}/", origin)
            or not isinstance(host_home, str)
            or not Path(host_home).is_absolute()
        ):
            return None
        try:
            installed = (self.root / "current").resolve(strict=True)
            if (
                Path(host_home).resolve(strict=True) != installed
                or self.paths.home.resolve(strict=True) != installed
            ):
                return None
            for pid, started in ancestors.items():
                browser = BROWSER_EXECUTABLES.get(process_identity(pid))
                if browser not in ("chromium", "google-chrome"):
                    continue
                status = self.browser_status(browser)
                if (
                    status["state"] == "registered"
                    and origin == f"chrome-extension://{status['extensionId']}/"
                ):
                    return {
                        "browser": browser,
                        "pid": pid,
                        "started": started,
                        "origin": origin,
                        "home": str(installed),
                    }
        except (OSError, ValueError, RuntimeError):
            pass
        return None

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
        for peer in peers:
            if not peer.lifetime or not peer.link.channel.alive:
                continue
            identity = (
                "desktop-text"
                if peer.backend in ("gtk3", "gtk4", "qt6")
                else BROWSER_EXECUTABLES.get(peer.application)
                if peer.backend == "browser"
                else None
            )
            if identity is None:
                continue
            if identity == "desktop-text" and self.selected_application is not None:
                if not self.desktop_session.owns_application(
                    self.application_launch, getattr(peer, "pid", None)
                ):
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
        facts.append(self.desktop_fact("desktop-text" in connected))
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

    def installed_application_view(self, facts):
        selected = self.selected_application
        fact = next(row for row in facts if row["id"] == "desktop-text")
        detail = self.application_result
        if selected and not self.application_failed and fact["state"] == "Connected":
            detail = ""
        elif selected and not self.application_failed and self.application_launch:
            detail = (
                "尚未检测到本次应用连接。若应用已运行，请保存工作并正常退出，再通过 TypeLay 打开。"
            )
        return {
            "id": selected["id"] if selected else None,
            "name": selected["name"] if selected else "尚未选择应用",
            "canSelect": self.desktop_component_prepared,
            "canOpen": bool(
                selected and selected.get("toolkit") and "Connect" in fact["supportedActions"]
            ),
            "result": detail,
        }

    async def select_application(self, identity):
        # Select from the current registry rather than trusting a UI label/path.
        entries = await asyncio.to_thread(list_applications)
        selected = next((entry for entry in entries if entry["id"] == identity), None)
        if selected is None:
            self.application_result = "应用已移除或不再可用，请重新选择。"
            self.application_failed = True
            return {"ok": False, "reason": "application-unavailable"}
        self.selected_application = selected
        self.application_launch = None
        self.application_failed = False
        try:
            application = await asyncio.to_thread(resolve_application, identity)
            selected["toolkit"] = application["toolkit"]
            self.application_result = "已选择，尚未打开或连接。"
            return {"ok": True}
        except ValueError as error:
            self.application_failed = True
            self.application_result = str(error)
            return {"ok": False, "reason": "application-launch-unsupported"}

    async def open_application(self, identity, startup_id):
        if not self.original_method_recorded:
            return {"ok": False, "reason": "original-method-not-recorded"}
        if not self.selected_application or self.selected_application["id"] != identity:
            return {"ok": False, "reason": "application-selection-changed"}
        try:
            application = await asyncio.to_thread(resolve_application, identity)
        except ValueError as error:
            self.application_failed = True
            self.application_result = str(error)
            return {"ok": False, "reason": "application-launch-unsupported"}
        result = await self.desktop_session.start_session()
        if result["ok"]:
            self.practice_session_started = True
            self.practice_component_removed = False
            self.practice_session_stopped = False
            result = await self.desktop_session.launch_application(
                application["argv"], toolkit=application["toolkit"], startup_id=startup_id
            )
        self.application_failed = not result["ok"]
        self.application_launch = result.get("application") if result["ok"] else None
        self.application_result = (
            "已请求打开应用，尚未确认连接。"
            if result["ok"]
            else result.get("detail") or "应用未能打开，请重新检查输入法与组件状态。"
        )
        await self.refresh()
        return result

    async def refresh(self):
        """Session checks may use D-Bus and /proc; never block the GTK/input loop."""
        self.refresh_revision += 1
        revision = self.refresh_revision
        if (
            self.desktop_component.status()["state"] != "missing"
            or self.desktop_component.root.exists()
            or self.practice_component_removed
            or self.original_method_recorded
        ):
            session = await self.desktop_session.refresh()
            if session["state"] == "conflict" and session.get("reason") == "existing-fcitx-session":
                session["source"] = await detect_fcitx_source()
            if revision != self.refresh_revision:
                return
            self.desktop_session_state = session
            if session["state"] == "active":
                self.practice_session_stopped = False
            if session["state"] == "conflict":
                self.original_input_seen = True
                if self.no_original_method:
                    # New observed input-method activity contradicts the prior
                    # user statement; do not carry it into a later connection.
                    self.no_original_method = self.original_method_recorded = False
        else:
            self.desktop_session_state = {"state": "unchecked"}

    def owns_practice(self, pid):
        return self.desktop_session.owns_application(self.practice_launch, pid)

    def release_practice(self, pid):
        if self.practice_launch and self.practice_launch["receipt"]["child"]["pid"] == pid:
            self.practice_launch = None

    def record_original_method(self, no_original=False):
        # This records the user's understanding only, never a restoration plan.
        if self.desktop_session_state["state"] not in ("inactive", "conflict", "active"):
            return False
        if no_original and (
            self.original_input_seen or self.desktop_session_state["state"] != "inactive"
        ):
            return False
        self.original_method_recorded = True
        self.no_original_method = no_original
        return True

    def desktop_guidance(self):
        """Present current session requirements separately from the full manual flow."""
        session = self.desktop_session_state
        state = session["state"]
        running = {
            "existing-fcitx-session": "Fcitx",
            "existing-ibus-session": "IBus",
        }.get(session.get("reason"))
        observed = (
            f"检测到 {running} 正在运行。"
            if state == "conflict" and running
            else "已有输入法正在运行。"
            if state == "conflict"
            else "当前未检测到 Fcitx/IBus 冲突。"
            if state == "inactive"
            else "TypeLay 私有输入法会话正在运行。"
            if state == "active"
            else "私有输入法已退出，请正常关闭仍在运行的应用或练习及其子窗口，再重新检查。"
            if state == "applications-running"
            else "私有输入法会话仍在启动，请稍后重新检查。"
            if state == "starting"
            else "当前输入法状态尚未确认，请先重新检查；未知状态不能开始或宣告恢复。"
        )
        recorded = (
            "你确认此前未运行输入法，无需恢复；这只是你的确认。"
            if self.no_original_method
            else "已由你确认记下原启动方式；TypeLay 没有验证该方式。"
            if self.original_method_recorded
            else "请先记下原输入法的启动方式，并确认知道结束私有会话后如何手动恢复。"
        )
        before = (
            "重新检查无冲突后，可通过 TypeLay 打开应用或开始练习。"
            if self.no_original_method
            else "保存工作、完成候选，手动退出原输入法；重新检查无冲突后，再打开应用或练习。"
        )
        restore = (
            "正常关闭应用或练习及其子窗口，再用本页“结束本次连接”停止私有输入法，保留接入组件；"
        )
        restore += (
            "你此前确认没有原输入法需要恢复，结束后请重新检查。"
            if self.no_original_method
            else "随后按记录手动恢复原输入法并重新检查。"
        )
        if self.practice_session_stopped:
            restore = (
                "本次私有输入法已结束，接入组件保留。你此前确认没有原输入法需要恢复，请重新检查。"
                if self.no_original_method and state == "inactive"
                else "本次私有输入法已结束，接入组件保留；这不能证明原启动方式已恢复，"
                "请按自己的记录手动恢复并核对，再重新检查。"
            )
        if self.practice_component_removed:
            restore = (
                "受管组件已移除，当前未检测到输入法冲突。你此前确认没有原输入法需要恢复。"
                if self.no_original_method and state == "inactive"
                else "受管组件已移除。这不能证明原启动方式已恢复，"
                "请按自己的记录手动恢复并核对，再重新检查。"
            )
        if (
            self.practice_component_removed
            or self.practice_session_stopped
            or state in ("active", "applications-running")
        ):
            notice = restore
        elif state in ("inactive", "conflict"):
            notice = observed if state == "conflict" else ""
            if not self.original_method_recorded:
                notice += (
                    "请先记下原输入法的启动方式并确认能手动恢复；"
                    "保存工作、完成候选后，手动退出原输入法并重新检查。"
                )
            elif not self.no_original_method:
                notice += (
                    "请保存工作、完成候选，手动退出原输入法后重新检查；结束私有会话后需手动恢复。"
                )
        else:
            notice = "" if state == "unchecked" else observed
        return {
            "title": "输入法接入与恢复",
            "notice": notice,
            "steps": observed + "\n" + recorded + "\n" + before + "\n" + restore,
            "originalMethodRecorded": self.original_method_recorded,
            "noOriginalMethod": self.no_original_method,
            "canRecordNoOriginalMethod": state == "inactive"
            and not self.original_input_seen
            and not self.original_method_recorded,
            "canRecordOriginalMethod": state in ("inactive", "conflict", "active")
            and not self.original_method_recorded,
        }

    def desktop_fact(self, connected=False):
        prepared = self.desktop_component.status()
        # Reuse this display observation; explicit launches still revalidate.
        self.desktop_component_prepared = prepared["state"] == "prepared"
        session = self.desktop_session_state
        state, reason = "NeedsInstall", "ComponentMissing"
        details = self.instructions("desktop-text")
        actions = ["CheckComponent", "Refresh", "OpenInstructions"]
        if prepared["state"] != "missing":
            actions = ["Refresh", "OpenInstructions"]
            if prepared["state"] == "prepared":
                state, reason = "AwaitingApproval", "UserApprovalRequired"
                details = (
                    "通过 TypeLay 打开应用或开始练习将使用本包的独立输入法会话。"
                    "请先完成当前候选并保存工作；已有 Fcitx/IBus 时必须由你自行退出，TypeLay 不会替你停止或切换。"
                    "会话使用独立配置和词库，不复制或修改个人输入法配置。关闭 TypeLay 不会关闭已打开的应用或练习窗口。"
                    "结束本次连接只停止私有输入法并保留组件；不会自动恢复原输入法，之后需要你自行启动。\n\n"
                    "移除前请正常关闭应用、练习及其子窗口。移除会结束仅由 TypeLay 启动的私有输入法并删除受管组件；"
                    "模型、TypeLay 设置和练习配置保留。\n\n"
                    "源码、许可证在组件文件夹，原始压缩包仍在主包中。准备完成或会话运行都不代表普通字段已经连接。"
                )
                if session["state"] == "inactive":
                    actions.extend(["Connect", "Remove"])
                elif session["state"] == "active":
                    details += "\n\n私有输入法会话正在运行，尚需从实际输入框确认连接和能力。"
                    if session["bundle"] == str(prepared["directory"]):
                        actions.append("Connect")
                    else:
                        details += " 仍是上一版组件的会话，请先结束它再使用新版本。"
                    if session.get("canStop"):
                        actions.extend(["Disconnect", "Remove"])
                    else:
                        details += (
                            " 请先正常关闭练习窗口、应用及其子窗口，再重新检查以结束连接或移除。"
                        )
                elif session["state"] == "conflict":
                    details += "\n\n" + session.get("detail", "已有输入法正在运行。")
                    source = session.get("source", {}).get("source")
                    if source == "user-service":
                        details += (
                            "\n\n已识别当前 Fcitx 来自专用用户服务。此检查只确认当前启动来源，"
                            "不会自动停止或恢复服务，也未确认具体服务名称或完整恢复方法；"
                            "结束私有会话后仍需按你记下并核对的方式手动恢复输入法。"
                        )
                    elif session.get("reason") == "existing-fcitx-session":
                        details += (
                            "\n\n原输入法的专用启动来源尚未确认。请记下原来的启动方式，"
                            "自行退出并在私有会话结束后按原方式恢复；这不表示系统不支持。"
                        )
                    actions.append("Remove")
                elif session["state"] == "abnormal":
                    state, reason = "Error", "DetectionFailed"
                    details += "\n\n" + session["detail"]
                elif session["state"] == "starting":
                    details += "\n\n私有会话仍在启动，请稍后检查连接；不会重复启动另一个输入法。"
                elif session["state"] == "applications-running":
                    details += (
                        "\n\n输入法已经退出，但应用或练习及其子窗口仍在运行。请正常关闭后重新检查。"
                    )
                elif session["state"] == "unknown":
                    details += "\n\n" + session["detail"]
                else:
                    details += "\n\n请先检查当前会话，再决定是否打开应用或练习。"
            else:
                actions.append("Install")
                details = (
                    "安装将从当前 TypeLay 主包校验并展开桌面接入组件到用户目录，"
                    "保留原始压缩包、源码与许可证。来源信任继承于你取得的主包；"
                    "自带文件哈希不是发布者签名。只准备文件，不运行包内程序、切换输入法或打开应用。"
                )
                if prepared["state"] == "invalid":
                    state, reason = "Error", "ComponentMissing"
                    details += "\n组件校验未通过，请使用完整可信的 TypeLay 主包重试。"
        if prepared["state"] != "prepared" and session["state"] == "active":
            details += "\n\n受管私有输入法会话仍在运行；组件文件变化不会自动停止该会话。"
            if session.get("canStop"):
                actions.extend(["Disconnect", "Remove"])
            else:
                details += " 请先正常关闭应用或练习及其子窗口，再重新检查。"
        if "Connect" in actions and not self.original_method_recorded:
            actions.remove("Connect")
        if "Connect" in actions:
            actions.remove("OpenInstructions")
        result = self.last_results.get("desktop-text")
        if result:
            if result.get("reason", "").startswith("cooperative-integrity-"):
                state = "AwaitingApproval" if result["ok"] else "Error"
                reason = "UserApprovalRequired" if result["ok"] else "DetectionFailed"
            elif not result["ok"]:
                state = "Error"
                reason = (
                    "ComponentMissing"
                    if result.get("reason") == "cooperative-preparation-failed"
                    else "ActionFailed"
                )
            details += "\n\n" + result.get("detail", "")
        if connected:
            state, reason = "Connected", "FieldUnobserved"
        return {
            "id": "desktop-text",
            "state": state,
            "reason": reason,
            "capabilities": {},
            "supportedActions": actions,
            "details": details,
            "applicationDetected": True,
        }

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
        if identity == "desktop-text":
            prepared = self.desktop_component.status()
            if prepared["state"] == "prepared":
                return prepared["directory"]
            if self.cooperative_directory:
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

    async def execute(self, identity, action, directory=None, *, startup_id=None, desktop_id=None):
        if identity == "desktop-text" and action == "SelectApplication":
            return await self.select_application(desktop_id)
        if identity == "desktop-text" and action == "OpenApplication":
            return await self.open_application(desktop_id, startup_id)
        if identity in BROWSERS and action == "OpenInstructions":
            self.open_browser(identity)
            return {"ok": True}
        if action == "Refresh":
            if identity in (*BROWSERS, "desktop-text"):
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
        if identity == "desktop-text" and action in (
            "RecordOriginalMethod",
            "RecordNoOriginalMethod",
        ):
            await self.refresh()
            return {"ok": self.record_original_method(action == "RecordNoOriginalMethod")}
        if identity == "desktop-text" and action == "Connect":
            if not self.original_method_recorded:
                return {"ok": False, "reason": "original-method-not-recorded"}
            result = await self.desktop_session.start_session()
            if result["ok"]:
                self.practice_session_started = True
                self.practice_component_removed = False
                self.practice_session_stopped = False
                result = await self.desktop_session.launch_application(
                    ["/usr/bin/python3", "-I", "-B", str(self.paths.home / "desktop/practice.py")],
                    toolkit="gtk3",
                    startup_id=startup_id,
                )
                if result["ok"]:
                    self.practice_launch = result["application"]
                    # A successful explicit practice launch becomes this page's
                    # current target. Cancellation/failure preserves the choice.
                    self.selected_application = self.application_launch = None
                    self.application_result = ""
                    self.application_failed = False
                    result = {
                        "ok": True,
                        "reason": "practice-start-requested",
                        "detail": "已请求打开 TypeLay 实操练习。若窗口未在前台，请切到该窗口并点击练习输入框；"
                        "尚未确认预览或替换能力。",
                    }
            self.last_results[identity] = result
            await self.refresh()
            return result
        if identity == "desktop-text" and action == "Disconnect":
            result = await self.desktop_session.stop_session()
            if result["ok"]:
                if result.get("reason") == "private-session-stopped":
                    self.practice_session_stopped = True
                self.practice_launch = self.application_launch = None
                self.application_result = "本次连接已结束；接入组件保留，再次打开应用时会重新检查。"
                self.application_failed = False
            self.last_results[identity] = result
            await self.refresh()
            return result
        if identity == "desktop-text" and action == "Remove":
            result = await self.desktop_session.remove_component()
            if result["ok"]:
                self.practice_component_removed = True
            self.last_results[identity] = result
            await self.refresh()
            return result
        if identity == "desktop-text" and action == "Install":
            result = await asyncio.to_thread(self.desktop_component.prepare)
            self.last_results[identity] = result
            return result
        if identity == "desktop-text" and action == "CheckComponent" and directory:
            self.cooperative_directory = Path(directory)
            result = await asyncio.to_thread(self.inspect_component, directory)
            self.last_results[identity] = result
            return result
        return {"ok": False, "reason": "invalid-support-action"}
