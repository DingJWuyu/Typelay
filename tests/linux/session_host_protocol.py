"""Real Linux host pipe/configuration boundary, without a desktop or model.

The host runs with private temporary settings and inherited stdin/stdout. This
covers serialization and owner-loop settings behavior, not native key routing.
"""

import argparse
import asyncio
import json
import struct
import tempfile
import time
import unittest
import uuid
from pathlib import Path

HOST = None


class SessionHostProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="typelay-session-host-")
        self.addCleanup(self.directory.cleanup)
        self.home = Path(self.directory.name)
        self.events = []
        self.sequence = 0
        self.input_reply = lambda _operation, _data: None
        self.process = await asyncio.create_subprocess_exec(
            str(HOST),
            str(self.home / "package"),
            str(self.home / "config"),
            str(self.home / "data"),
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )

    async def asyncTearDown(self):
        try:
            async with asyncio.timeout(3):
                if self.process.returncode is None:
                    await self.send({"event": "quit", "data": None})
                await self.process.communicate()
        except (TimeoutError, BrokenPipeError, ConnectionResetError):
            if self.process.returncode is None:
                self.process.kill()
            await asyncio.wait_for(self.process.communicate(), 3)

    async def send(self, value):
        data = json.dumps(value).encode()
        self.process.stdin.write(struct.pack("<I", len(data)) + data)
        await self.process.stdin.drain()

    async def ask(self, operation, data=None):
        self.sequence += 1
        identity = self.sequence
        await self.send({"id": identity, "op": operation, "data": data})
        return (await self.receive_until(lambda value: value.get("reply") == identity))["data"]

    async def receive_until(self, predicate):
        async with asyncio.timeout(3):
            while True:
                length = struct.unpack("<I", await self.process.stdout.readexactly(4))[0]
                self.assertLessEqual(length, 256 * 1024)
                value = json.loads(await self.process.stdout.readexactly(length))
                if "event" in value:
                    self.events.append(value)
                if predicate(value):
                    return value
                if "op" in value:
                    await self.send(
                        {"reply": value["id"], "data": self.input_reply(value["op"], value["data"])}
                    )
                else:
                    self.assertIn("event", value)

    @staticmethod
    def draft(accept="F7", exit_key="F8"):
        return {
            "session": str(uuid.uuid4()),
            "request": 1,
            "shortcuts": {
                "wake": "Ctrl+Shift+F9",
                "direct": "Ctrl+Shift+F10",
                "accept": accept,
                "exit": exit_key,
            },
        }

    async def test_exclusion_draft_merges_only_local_changes_with_latest_settings(self):
        draft = self.draft()
        draft["exclusions"] = {
            "baseline": ["removed-elsewhere", "remove-here", "keep"],
            "values": ["removed-elsewhere", "KEEP", "added-here"],
        }
        self.assertTrue(
            await self.ask(
                "settings",
                {
                    "theme": "dark",
                    "source": "zh",
                    "excludedApplications": ["External", "Remove-Here", "Keep"],
                },
            )
        )
        self.assertTrue((await self.ask("saveSettingsDraft", draft))["saved"])
        settings = self.latest("settings")
        self.assertEqual(settings["excludedApplications"], ["External", "Keep", "added-here"])
        self.assertEqual((settings["theme"], settings["source"]), ("dark", "zh"))

    async def test_discard_during_retirement_prevents_the_pending_draft_from_being_saved(self):
        await self.bind_automatic_practice()
        before = (self.home / "config/settings.json").read_bytes()
        draft = self.draft()
        draft["exclusions"] = {"baseline": [], "values": ["unsaved-app"]}
        self.sequence += 1
        save_id = self.sequence
        await self.send({"id": save_id, "op": "saveSettingsDraft", "data": draft})
        retirement = await self.receive_until(
            lambda item: item.get("op") == "retireAutomaticPractice"
        )
        await self.send(
            {
                "event": "cancelSettingsDraft",
                "data": {"session": draft["session"], "request": draft["request"]},
            }
        )
        # This ordinary request drains the earlier notification on the owner
        # loop while the save still awaits the real retirement acknowledgement.
        await self.ask("settingsDraft")
        await self.send({"reply": retirement["id"], "data": True})
        reply = await self.receive_until(lambda item: item.get("reply") == save_id)
        self.assertFalse(reply["data"]["saved"])
        self.assertEqual((self.home / "config/settings.json").read_bytes(), before)

    async def test_wrong_generation_or_session_and_late_cancellation_cannot_undo_a_save(self):
        await self.bind_automatic_practice()
        draft = self.draft()
        draft["exclusions"] = {"baseline": [], "values": ["saved-app"]}
        self.sequence += 1
        save_id = self.sequence
        await self.send({"id": save_id, "op": "saveSettingsDraft", "data": draft})
        retirement = await self.receive_until(
            lambda item: item.get("op") == "retireAutomaticPractice"
        )
        for identity in (
            {"session": draft["session"], "request": draft["request"] + 1},
            {"session": str(uuid.uuid4()), "request": draft["request"]},
        ):
            await self.send({"event": "cancelSettingsDraft", "data": identity})
        await self.ask("settingsDraft")
        await self.send({"reply": retirement["id"], "data": True})
        reply = await self.receive_until(lambda item: item.get("reply") == save_id)
        self.assertTrue(reply["data"]["saved"])
        saved = (self.home / "config/settings.json").read_bytes()
        self.assertEqual(json.loads(saved)["ExcludedApplications"], ["saved-app"])
        await self.send(
            {
                "event": "cancelSettingsDraft",
                "data": {"session": draft["session"], "request": draft["request"]},
            }
        )
        await self.ask("settingsDraft")
        self.assertEqual((self.home / "config/settings.json").read_bytes(), saved)

    async def test_failed_exclusion_save_keeps_disk_and_retry_merges_later_external_changes(self):
        self.assertTrue(await self.ask("settings", {"excludedApplications": ["keep", "remove"]}))
        before = (self.home / "config/settings.json").read_bytes()
        draft = self.draft()
        draft["exclusions"] = {"baseline": ["keep", "remove"], "values": ["keep", "added"]}
        obstruction = self.home / "config/settings.json.tmp"
        obstruction.mkdir()
        self.assertFalse((await self.ask("saveSettingsDraft", draft))["saved"])
        self.assertEqual((self.home / "config/settings.json").read_bytes(), before)
        obstruction.rmdir()
        self.assertTrue(
            await self.ask("settings", {"excludedApplications": ["keep", "remove", "external"]})
        )
        draft["request"] += 1
        self.assertTrue((await self.ask("saveSettingsDraft", draft))["saved"])
        self.assertEqual(
            self.latest("settings")["excludedApplications"], ["keep", "external", "added"]
        )

    async def test_exclusion_capacity_rejects_the_whole_draft_without_saving_shortcuts(self):
        self.assertTrue(
            await self.ask("settings", {"excludedApplications": [f"app-{n}" for n in range(256)]})
        )
        before = (self.home / "config/settings.json").read_bytes()
        draft = self.draft()
        draft["exclusions"] = {"baseline": [], "values": ["overflow"]}
        self.assertFalse((await self.ask("saveSettingsDraft", draft))["saved"])
        self.assertEqual((self.home / "config/settings.json").read_bytes(), before)

    def latest(self, name):
        return next(value["data"] for value in reversed(self.events) if value.get("event") == name)

    async def bind_automatic_practice(self):
        # Exercise the actual C# private-pipe contract without starting a model
        # or claiming that these supplied observations prove a native desktop.
        self.assertTrue(
            await self.ask("settings", {"enabled": True, "mode": "Automatic", "theme": "dark"})
        )
        run = str(uuid.uuid4())
        self.assertTrue(await self.ask("beginAutomaticPractice", run))
        target = {
            "backend": "linux-gtk3",
            "value": str(uuid.uuid4()),
            "lifetime": str(uuid.uuid4()),
        }
        binding = {
            "runId": run,
            "target": target,
            "connectionId": str(uuid.uuid4()),
            "commandGeneration": self.latest("shortcuts")["generation"],
            "nativeFocusRevision": 1,
            "contextGeneration": 7,
        }
        await self.send(
            {
                "event": "state",
                "data": {
                    "ready": True,
                    "nativeReady": True,
                    "target": target,
                    "focus": "Source",
                    "connectionId": binding["connectionId"],
                },
            }
        )
        self.assertTrue(await self.ask("bindAutomaticPractice", binding))
        return binding

    async def test_private_pause_does_not_save_or_show_paused_until_executor_acknowledges(self):
        binding = await self.bind_automatic_practice()
        before = (self.home / "config/settings.json").read_bytes()
        self.sequence += 1
        pause_id = self.sequence
        await self.send({"id": pause_id, "op": "traySettings", "data": {"enabled": False}})
        retirement = await self.receive_until(
            lambda item: item.get("op") == "retireAutomaticPractice"
        )
        self.assertEqual(retirement["data"], binding)
        self.assertNotEqual(self.latest("shortcuts")["generation"], binding["commandGeneration"])
        self.assertEqual((self.home / "config/settings.json").read_bytes(), before)
        self.assertTrue(self.latest("settings")["enabled"])
        self.assertIsNone(await self.ask("automaticPracticeProgress"))
        self.assertFalse(await self.ask("beginAutomaticPractice", str(uuid.uuid4())))
        await self.send({"reply": retirement["id"], "data": True})
        reply = await self.receive_until(lambda item: item.get("reply") == pause_id)
        self.assertIs(reply["data"], True)
        self.assertFalse(self.latest("settings")["enabled"])
        self.assertFalse(json.loads((self.home / "config/settings.json").read_text())["Enabled"])
        self.assertTrue(await self.ask("endAutomaticPractice", binding["runId"]))

    async def test_unconfirmed_private_retirement_blocks_pause_and_new_authority(self):
        binding = await self.bind_automatic_practice()
        before = (self.home / "config/settings.json").read_bytes()
        calls = []

        def refuse(operation, data):
            calls.append((operation, data))
            return False

        self.input_reply = refuse
        self.assertFalse(await self.ask("traySettings", {"enabled": False}))
        self.assertEqual(calls, [("retireAutomaticPractice", binding)])
        self.assertEqual((self.home / "config/settings.json").read_bytes(), before)
        self.assertTrue(self.latest("settings")["enabled"])
        self.assertFalse(await self.ask("beginAutomaticPractice", str(uuid.uuid4())))
        self.assertFalse(await self.ask("endAutomaticPractice", binding["runId"]))
        self.assertFalse(await self.ask("reloadModel"))
        self.assertEqual(len(calls), 1, "unconfirmed retirement was silently retried")

    async def test_identical_save_and_invalid_binding_do_not_retire_private_practice(self):
        binding = await self.bind_automatic_practice()
        calls = []
        self.input_reply = lambda operation, data: calls.append((operation, data))
        settings = self.latest("settings")
        before = await self.ask("automaticPracticeProgress")
        self.assertTrue(await self.ask("settings", dict(settings)))
        self.assertEqual(await self.ask("automaticPracticeProgress"), before)
        for key in ("runId", "connectionId", "commandGeneration"):
            self.assertFalse(
                await self.ask("bindAutomaticPractice", {**binding, key: str(uuid.uuid4())})
            )
        self.assertFalse(await self.ask("bindAutomaticPractice", {**binding, "extra": True}))
        for key in ("nativeFocusRevision", "contextGeneration"):
            for invalid in (None, True, 0, -1, 1.5, "1", 2**53):
                self.assertFalse(await self.ask("bindAutomaticPractice", {**binding, key: invalid}))
            self.assertFalse(
                await self.ask(
                    "bindAutomaticPractice", {k: v for k, v in binding.items() if k != key}
                )
            )
        self.assertFalse(
            await self.ask(
                "bindAutomaticPractice",
                {**binding, "target": {**binding["target"], "lifetime": str(uuid.uuid4())}},
            )
        )
        self.assertEqual(calls, [])
        self.assertEqual(await self.ask("automaticPracticeProgress"), before)
        self.assertFalse(settings["experimentalWriteback"])

    async def test_automatic_presentation_keeps_the_explicit_mode_change_separate_from_progress(
        self,
    ):
        self.assertTrue(await self.ask("settings", {"mode": "OnDemand"}))
        presentation = await self.ask("automaticPractice")
        self.assertIn("自动", presentation["confirmationText"])
        self.assertIn("不会自动恢复", presentation["confirmationText"])
        self.assertIn("新的原文", presentation["replaceStep"])
        self.assertFalse(presentation["completed"])
        self.assertFalse(await self.ask("startAutomaticPractice", False))
        self.assertEqual(self.latest("settings")["mode"], "OnDemand")
        self.assertEqual(
            await self.ask("finishAutomaticPractice", str(uuid.uuid4())),
            {"saved": False, "retrySave": False},
        )

    async def test_tray_persistence_failure_keeps_current_settings_and_allows_explicit_retry(self):
        self.assertTrue(await self.ask("settings", {"enabled": True, "theme": "dark"}))
        settings_file = self.home / "config/settings.json"
        before = settings_file.read_bytes()
        blocker = settings_file.with_suffix(".json.tmp")
        blocker.mkdir()
        self.events.clear()
        self.assertIs(await self.ask("traySettings", {"enabled": False}), False)
        self.assertEqual(settings_file.read_bytes(), before)
        self.assertEqual(self.events, [], "failed persistence reconfigured the session")
        blocker.rmdir()
        self.assertIs(await self.ask("traySettings", {"enabled": False}), True)
        self.assertFalse(self.latest("settings")["enabled"])
        self.assertFalse(json.loads(settings_file.read_text())["Enabled"])

    async def test_tray_intent_preserves_other_settings_changed_after_menu_was_rendered(self):
        self.assertTrue(await self.ask("settings", {"enabled": True, "theme": "light"}))
        rendered = self.latest("settings")
        # The menu was rendered before another settings surface committed these fields.
        newer = {
            **rendered,
            "theme": "dark",
            "excludedApplications": ["private-editor"],
            "source": "zh",
            "acceptShortcut": "F7",
        }
        self.assertTrue(await self.ask("settings", newer))
        self.assertTrue(await self.ask("traySettings", {"enabled": False}))
        saved = self.latest("settings")
        self.assertEqual(saved, {**newer, "enabled": False})
        self.assertEqual(
            json.loads((self.home / "config/settings.json").read_text())["Theme"], "dark"
        )

    async def test_tray_rejects_unknown_fields_wrong_types_and_invalid_values_without_writing(self):
        self.assertTrue(await self.ask("settings", {"enabled": True}))
        settings_file = self.home / "config/settings.json"
        before = settings_file.stat().st_mtime_ns
        self.events.clear()
        for patch in ({"theme": "dark"}, {"enabled": 0}, {"mode": 1}, {"target": "auto"}, {}):
            with self.subTest(patch=patch):
                self.assertIs(await self.ask("traySettings", patch), False)
        self.sequence += 1
        duplicate = (
            '{"id":%d,"op":"traySettings","data":{"enabled":false,"enabled":true}}' % self.sequence
        ).encode()
        self.process.stdin.write(struct.pack("<I", len(duplicate)) + duplicate)
        await self.process.stdin.drain()
        reply = await self.receive_until(lambda value: value.get("reply") == self.sequence)
        self.assertIs(reply["data"], False)
        self.assertEqual(settings_file.stat().st_mtime_ns, before)
        self.assertEqual(self.events, [])

    async def test_support_summary_crosses_the_host_pipe_without_losing_failure_reason(self):
        fact = {
            "id": "desktop-text",
            "state": "Connected",
            "reason": "PreviewOnly",
            "capabilities": {"preview": True, "tabAccept": True, "directReplace": True},
        }
        view = await self.ask("applicationSupport", {"facts": [fact]})
        row = view["rows"][0]
        self.assertEqual(row["summaryText"], "目前仅确认预览译文，尚未确认替换能力。")
        self.assertEqual(row["reasonText"], "目前仅确认显示译文；Tab 接受和直接替换尚不可用。")
        self.assertFalse(row["capabilities"]["tabAccept"])
        self.assertFalse(row["capabilities"]["directReplace"])

        view = await self.ask(
            "applicationSupport", {"facts": [{**fact, "state": "Error", "reason": "ActionFailed"}]}
        )
        self.assertEqual(
            view["rows"][0]["summaryText"],
            "操作未完成。请查看详情，处理原因后再试；未将这次操作标为成功。",
        )

    async def test_preparation_uses_real_installation_facts_and_current_phase(self):
        missing = await self.ask("translationPreparation", {"phase": "Idle"})
        self.assertIsInstance(missing, dict)
        self.assertEqual(missing["primaryAction"]["id"], "Prepare")
        self.assertEqual(missing["secondaryActions"][0]["id"], "NextStep")
        self.assertEqual(missing["secondaryActions"][0]["label"], "选择接入方式")

        component = self.home / "data/components/current"
        (component / "runtime/venv/bin").mkdir(parents=True)
        (component / "runtime/venv/bin/python").touch()
        (component / "models").mkdir()
        (component / "models/installed.json").write_text("{}")
        installed = await self.ask("translationPreparation", {"phase": "Idle"})
        self.assertNotEqual(installed["primaryAction"]["id"], "NextStep")
        self.assertIn("已安装", installed["statusText"] + installed["guidance"])
        for phase, action, enabled in (
            ("Preparing", "Pause", True),
            ("Pausing", "Pause", False),
            ("Paused", "Resume", True),
            ("StartingModel", "Prepare", False),
            ("Failed", "Retry", True),
        ):
            with self.subTest(phase=phase):
                view = await self.ask(
                    "translationPreparation", {"phase": phase, "failureCode": "network"}
                )
                self.assertEqual(view["primaryAction"]["id"], action)
                self.assertEqual(view["primaryAction"]["enabled"], enabled)
                if phase == "Failed":
                    self.assertIn("网络", view["statusText"] + view["guidance"])

    async def test_connection_status_follows_listener_without_claiming_application_access(self):
        self.assertTrue(await self.ask("start"))
        self.assertEqual(self.latest("status")["connectionState"], "ListenerUnavailable")
        await self.send({"event": "state", "data": {"ready": True}})
        connected = (
            await self.receive_until(
                lambda value: value.get("event") == "status"
                and value["data"].get("connectionState") == "Connected"
            )
        )["data"]
        self.assertEqual(connected["connectionStatus"], "输入服务已连接")
        self.assertFalse(connected["modelReady"])
        self.assertIn("尚未连接原生输入适配器", connected["shortcutStatus"])
        await self.send({"event": "state", "data": {"ready": False}})
        await self.receive_until(
            lambda value: value.get("event") == "status"
            and value["data"].get("connectionState") == "ListenerUnavailable"
        )

    async def test_settings_guidance_tracks_saved_mode_and_shortcuts_in_owner_order(self):
        self.assertTrue(await self.ask("start"))
        guidance = [value for value in self.events if value.get("event") == "settingsGuidance"]
        self.assertEqual(len(guidance), 1, "initial settings omitted their guidance")
        self.assertIn("自动模式", guidance[0]["data"]["modeDescription"])
        self.assertIn("即时保存", guidance[0]["data"]["saveScopeDescription"])
        self.assertIn("排除", guidance[0]["data"]["saveScopeDescription"])
        self.assertIn("设置目录权限", guidance[0]["data"]["saveUnconfirmedMessage"])
        self.assertIn("打开设置检查", guidance[0]["data"]["unknownSaveMessage"])

        saved = self.latest("settings")
        self.events.clear()
        self.assertTrue(await self.ask("settings", {**saved, "mode": "OnDemand"}))
        self.assertEqual(
            [event["event"] for event in self.events if event["event"].startswith("settings")],
            ["settings", "settingsGuidance"],
        )
        self.assertIn("按需模式", self.latest("settingsGuidance")["modeDescription"])
        draft = self.draft("F7", "F8")
        draft["shortcuts"].update(wake="Ctrl+F3", direct="Alt+F4")
        self.assertTrue((await self.ask("saveSettingsDraft", draft))["saved"])
        guidance = self.latest("settingsGuidance")
        self.assertIn("Ctrl+F3", guidance["modeDescription"])
        self.assertIn("Alt+F4", guidance["modeDescription"])
        self.assertNotIn("Ctrl+Shift+F9", guidance["modeDescription"])
        self.assertIn("F7", guidance["acceptanceDescription"])
        self.assertIn("F8", guidance["acceptanceDescription"])
        self.assertNotIn("Tab", guidance["acceptanceDescription"])
        before = len(self.events)
        self.assertFalse((await self.ask("saveSettingsDraft", self.draft("F7", "F7")))["saved"])
        self.assertFalse(
            any(event["event"] == "settingsGuidance" for event in self.events[before:]),
            "a rejected draft was presented as active settings",
        )
        self.assertTrue(await self.ask("settings", {**self.latest("settings"), "enabled": False}))
        self.assertIn("已暂停", self.latest("settingsGuidance")["modeDescription"])

    async def test_staged_shortcuts_merge_with_latest_settings_and_emit_one_generation(self):
        self.assertTrue(
            await self.ask(
                "settings",
                {
                    "enabled": False,
                    "source": "zh",
                    "target": "en",
                    "theme": "dark",
                    "experimentalWriteback": False,
                    "excludedApplications": ["private-app"],
                },
            )
        )
        result = await self.ask("saveSettingsDraft", self.draft())
        self.assertIsInstance(result, dict)
        self.assertTrue(result["saved"])
        settings = self.latest("settings")
        self.assertEqual((settings["acceptShortcut"], settings["exitShortcut"]), ("F7", "F8"))
        self.assertFalse(settings["enabled"])
        self.assertFalse(settings["experimentalWriteback"])
        self.assertEqual(settings["theme"], "dark")
        self.assertEqual(settings["excludedApplications"], ["private-app"])
        configuration = self.latest("shortcuts")
        self.assertEqual((configuration["accept"], configuration["exit"]), ("F7", "F8"))
        self.assertFalse(configuration["enabled"])
        self.assertFalse(configuration["sessionEnabled"])
        self.assertNotEqual(uuid.UUID(configuration["generation"]).int, 0)
        saved = json.loads((self.home / "config/settings.json").read_text())
        self.assertEqual((saved["AcceptShortcut"], saved["ExitShortcut"]), ("F7", "F8"))

    async def test_practice_progress_requires_fresh_run_exact_target_and_real_completion(self):
        self.assertTrue(
            await self.ask(
                "settings", {"enabled": True, "mode": "OnDemand", "source": "en", "target": "zh"}
            )
        )
        run = str(uuid.uuid4())
        target = {
            "backend": "linux-gtk3",
            "value": str(uuid.uuid4()),
            "lifetime": str(uuid.uuid4()),
        }
        self.assertIsNone(await self.ask("onDemandPracticeProgress"))
        self.assertFalse(await self.ask("finishOnDemandPractice", run))
        self.assertTrue(await self.ask("beginOnDemandPractice", run))
        progress = await self.ask("onDemandPracticeProgress")
        self.assertEqual(progress["runId"], run)
        self.assertIsNone(progress["target"])
        self.assertFalse(progress["completed"])
        self.assertTrue(await self.ask("bindOnDemandPractice", {"runId": run, "target": target}))
        self.assertFalse(
            await self.ask(
                "bindOnDemandPractice",
                {"runId": run, "target": {**target, "value": str(uuid.uuid4())}},
            )
        )
        for invalid in (
            {"runId": run, "target": target, "completed": True},
            {"runId": run, "target": {**target, "backend": "linux-browser"}},
            {"runId": run, "target": {**target, "lifetime": "unknown"}},
        ):
            self.assertFalse(await self.ask("bindOnDemandPractice", invalid))
        self.assertFalse(
            await self.ask("finishOnDemandPractice", run), "binding alone completed practice"
        )
        view = await self.ask("onDemandPractice", run)
        self.assertFalse(view["completed"])
        self.assertEqual("已识别练习输入框，尚未生成预览。", view["progressNotice"])
        public_view = await self.ask("onDemandPractice")
        self.assertFalse(public_view["completed"])
        self.assertEqual("开始练习后，这里会显示进度。", public_view["progressNotice"])
        self.assertTrue(
            await self.ask(
                "settings", {"enabled": False, "mode": "OnDemand", "source": "en", "target": "zh"}
            )
        )
        self.assertIsNone(await self.ask("onDemandPracticeProgress"))
        self.assertFalse(await self.ask("bindOnDemandPractice", {"runId": run, "target": target}))
        self.assertFalse(await self.ask("finishOnDemandPractice", run))
        self.assertFalse(self.latest("settings")["onboardingComplete"])

    async def test_tutorial_dismissal_preserves_latest_mode_language_and_practice_state(self):
        self.assertTrue(
            await self.ask(
                "settings", {"enabled": True, "mode": "OnDemand", "source": "en", "target": "zh"}
            )
        )
        self.assertTrue(
            await self.ask(
                "settings",
                {
                    "enabled": False,
                    "mode": "Automatic",
                    "source": "zh",
                    "target": "en",
                    "theme": "dark",
                },
            )
        )
        self.assertTrue(await self.ask("dismissOnboarding"))
        settings = self.latest("settings")
        self.assertTrue(settings["onboardingComplete"])
        self.assertFalse(settings["enabled"])
        self.assertEqual(
            (settings["mode"], settings["source"], settings["target"], settings["theme"]),
            ("Automatic", "zh", "en", "dark"),
        )
        self.assertIsNone(await self.ask("onDemandPracticeProgress"))
        self.assertFalse((await self.ask("onDemandPractice"))["completed"])

    async def test_automatic_mode_configures_session_keys_without_enabling_on_demand_keys(self):
        self.assertTrue((await self.ask("saveSettingsDraft", self.draft("F1", "F24")))["saved"])
        configuration = self.latest("shortcuts")
        self.assertFalse(configuration["enabled"])
        self.assertTrue(configuration["sessionEnabled"])
        hints = await self.ask("sessionShortcutSettings")
        self.assertEqual(hints["acceptHint"], "F1 接受译文")
        self.assertEqual(hints["returnHint"], "F1 返回原输入框")
        self.assertEqual(hints["exitHint"], "F24 退出当前翻译")
        self.assertEqual(hints["passThroughHint"], "F1 交还应用")

    async def test_duplicate_pair_rejects_whole_draft_and_preserves_saved_pair(self):
        self.assertTrue((await self.ask("saveSettingsDraft", self.draft()))["saved"])
        generation = self.latest("shortcuts")["generation"]
        result = await self.ask("saveSettingsDraft", self.draft("F12", "F12"))
        self.assertFalse(result["saved"])
        self.assertEqual(result["message"], "请选择有效且不同的接受与退出快捷键。")
        self.assertEqual(self.latest("shortcuts")["generation"], generation)
        hints = await self.ask("sessionShortcutSettings")
        self.assertEqual((hints["acceptKey"], hints["exitKey"]), ("F7", "F8"))

    async def test_target_binding_acknowledgement_decodes_canonical_pair(self):
        self.assertTrue((await self.ask("saveSettingsDraft", self.draft()))["saved"])
        target = {"backend": "linux-gtk3", "value": "test-field", "lifetime": str(uuid.uuid4())}
        await self.send(
            {
                "event": "state",
                "data": {
                    "ready": True,
                    "nativeReady": True,
                    "target": target,
                    "focus": "Source",
                    "sessionShortcuts": {
                        "target": target,
                        "generation": self.latest("shortcuts")["generation"],
                        "bindings": {"accept": "F7", "exit": "F8"},
                        "sourceReady": True,
                        "editorReady": True,
                    },
                },
            }
        )
        hints = await self.ask("sessionShortcutSettings")
        self.assertEqual(hints["acceptKey"], "F7")
        self.assertIsNone(self.process.returncode)

    async def test_read_only_capture_gains_editor_capability_only_after_target_acknowledgement(
        self,
    ):
        self.assertTrue((await self.ask("saveSettingsDraft", self.draft()))["saved"])
        target = {"backend": "linux-gtk3", "value": "test-field", "lifetime": str(uuid.uuid4())}
        await self.send(
            {
                "event": "state",
                "data": {
                    "ready": True,
                    "nativeReady": True,
                    "target": target,
                    "focus": "Source",
                    "userEpoch": 1,
                    "sourceUserEpoch": 1,
                    "foregroundEpoch": 1,
                    "windowEpoch": 1,
                    "contentEpoch": 1,
                },
            }
        )

        acknowledgement = None

        def input_reply(operation, context):
            self.assertEqual(operation, "capture")
            self.assertEqual(
                context["sessionShortcuts"]["bindings"], {"accept": "F7", "exit": "F8"}
            )
            return {
                "focus": "Source",
                "application": "fixture",
                "sessionShortcuts": acknowledgement,
                "userEpoch": 1,
                "foregroundEpoch": 1,
                "snapshot": {
                    "target": target,
                    "text": "",
                    "selection": {"start": 0, "end": 0},
                    "bounds": {"x": 0, "y": 0, "width": 100, "height": 20},
                    "multiline": False,
                    "access": "Supported",
                    "ime": "Clear",
                    "observedAtMs": time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1_000_000,
                    "inputEpoch": 1,
                    "focusEpoch": 1,
                    "writeSupported": False,
                    "directWriteSupported": False,
                },
            }

        self.input_reply = input_reply
        self.assertTrue(await self.ask("start"))
        rendered = (await self.receive_until(lambda value: value.get("event") == "render"))["data"]
        context = rendered["viewContext"]
        self.assertEqual(context["token"]["target"], target)
        self.assertEqual(
            context["token"]["commandGeneration"], self.latest("shortcuts")["generation"]
        )
        self.assertFalse(context["canEdit"])
        self.assertFalse(rendered["editable"])
        self.assertFalse(rendered["canAccept"])
        self.assertEqual(rendered["sessionShortcuts"]["passThroughHint"], "F7 交还应用")
        response = await self.ask("beginEdit", context["token"])
        self.assertEqual(response, {"allowed": False, "token": None})

        # A native consumer may only confirm its editor handler after the first
        # read-only render has created it. A later real capture must upgrade the
        # target capability without requiring an already-editable view.
        acknowledgement = {
            "target": target,
            "generation": self.latest("shortcuts")["generation"],
            "bindings": {"accept": "F7", "exit": "F8"},
            "sourceReady": True,
            "editorReady": True,
        }
        upgraded = (
            await self.receive_until(
                lambda value: value.get("event") == "render"
                and value["data"].get("viewContext", {}).get("canEdit") is True
            )
        )["data"]
        self.assertEqual(upgraded["viewContext"]["token"], context["token"])
        self.assertFalse(upgraded["editable"])
        self.assertFalse(upgraded["canAccept"])

    async def test_malformed_binding_acknowledgement_cannot_be_restored_to_default_authority(self):
        self.assertTrue((await self.ask("saveSettingsDraft", self.draft()))["saved"])
        target = {"backend": "linux-gtk3", "value": "test-field", "lifetime": str(uuid.uuid4())}
        await self.send(
            {
                "event": "state",
                "data": {
                    "target": target,
                    "sessionShortcuts": {
                        "target": target,
                        "generation": self.latest("shortcuts")["generation"],
                        "bindings": {"accept": "F7", "exit": "F7"},
                        "sourceReady": True,
                        "editorReady": True,
                    },
                },
            }
        )
        await asyncio.wait_for(self.process.wait(), 3)
        self.assertIsNotNone(self.process.returncode)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    arguments, remaining = parser.parse_known_args()
    HOST = arguments.host.resolve()
    if not HOST.is_file():
        parser.error("--host must identify a built TypeLay.Linux executable")
    unittest.main(argv=[__file__, *remaining])
