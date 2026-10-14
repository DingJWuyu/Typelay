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
            "shortcuts": {
                "wake": "Ctrl+Shift+F9",
                "direct": "Ctrl+Shift+F10",
                "accept": accept,
                "exit": exit_key,
            }
        }

    def latest(self, name):
        return next(value["data"] for value in reversed(self.events) if value.get("event") == name)

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
        self.assertIn("已连接", view["progressNotice"])
        public_view = await self.ask("onDemandPractice")
        self.assertFalse(public_view["completed"])
        self.assertIn("尚未开始", public_view["progressNotice"])
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
