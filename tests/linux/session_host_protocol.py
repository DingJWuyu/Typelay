"""Real Linux host pipe/configuration boundary, without a desktop or model.

The host runs with private temporary settings and inherited stdin/stdout. This
covers serialization and owner-loop settings behavior, not native key routing.
"""

import argparse
import asyncio
import fcntl
import json
import os
import shutil
import struct
import tempfile
import time
import unittest
import uuid
from pathlib import Path

HOST = None
SUPERVISOR = None


class SessionHostProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="typelay-session-host-")
        self.addCleanup(self.directory.cleanup)
        self.home = Path(self.directory.name)
        self.events = []
        self.sequence = 0
        self.input_reply = lambda _operation, _data: None
        await self.start_host()

    async def start_host(self):
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

    def model_generation(self, *, previous=None, directions=None, held=False, legacy=False):
        # These real Python pipe peers exercise ownership and publication, never
        # claim model inference. Production worker inventory tests live separately.
        generation = uuid.uuid4().hex
        root = self.home / "data/components" / generation
        (root / "models").mkdir(parents=True)
        (root / "runtime/python").mkdir(parents=True)
        (root / "runtime/venv/bin").mkdir(parents=True)
        shutil.copy2(Path("/usr/bin/python3").resolve(), root / "runtime/python/python")
        (root / "runtime/venv/bin/python").symlink_to("../../python/python")
        (root / "models/installed.json").write_text(
            json.dumps(
                {
                    "directions": directions if directions is not None else ["zh_en", "en_zh"],
                    "held": held,
                }
            )
        )
        if not legacy:
            (root / "activation.json").write_text(json.dumps({"schema": 1, "previous": previous}))
        if held:
            os.mkfifo(root / "hello-gate")
        package = self.home / "package"
        (package / "native").mkdir(parents=True, exist_ok=True)
        if not (package / "native/typelay-supervise").exists():
            shutil.copy2(SUPERVISOR, package / "native/typelay-supervise")
        (package / "worker").mkdir(exist_ok=True)
        (package / "worker/run_worker.py").write_text(r"""
import json, os, pathlib, struct, sys
root = pathlib.Path(sys.argv[sys.argv.index('--models') + 1]).parent
settings = json.loads((root / 'models/installed.json').read_text())
(root / 'pid').write_text(str(os.getpid()))
while True:
    header = sys.stdin.buffer.read(4)
    if not header: break
    request = json.loads(sys.stdin.buffer.read(struct.unpack('<I', header)[0]))
    with (root / 'requests').open('a') as log: log.write(request['op'] + '\n')
    if request['op'] == 'hello':
        if settings['held']:
            with (root / 'hello-gate').open() as gate: gate.read(1)
        response = {'status': 'ready', 'directions': settings['directions']}
    else: response = {'status': 'translated', 'text': root.name}
    response['id'] = request['id']
    encoded = json.dumps(response).encode()
    sys.stdout.buffer.write(struct.pack('<I', len(encoded)) + encoded)
    sys.stdout.buffer.flush()
""")
        return generation, root

    async def release_hello(self, root):
        async with asyncio.timeout(3):
            while True:
                try:
                    fd = os.open(root / "hello-gate", os.O_WRONLY | os.O_NONBLOCK)
                    break
                except OSError as error:
                    if error.errno != 6:  # ENXIO: the candidate has not reached hello yet.
                        raise
                    await asyncio.sleep(0.01)
        os.write(fd, b"!")
        os.close(fd)

    async def test_model_activation_confirms_one_candidate_and_rejects_partial_or_stale_successors(
        self,
    ):
        old, old_root = self.model_generation()
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        self.assertTrue(self.latest("status")["modelReady"])
        partial, partial_root = self.model_generation(previous=old, directions=["zh_en"])
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": partial}),
            "unchanged",
        )
        self.assertTrue(self.latest("status")["modelReady"])
        self.assertEqual(os.readlink(old_root.parent / "current"), old)
        fresh, fresh_root = self.model_generation(previous=old)
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": fresh}),
            "confirmed",
        )
        self.assertEqual(os.readlink(old_root.parent / "current"), fresh)
        self.assertFalse(Path(f"/proc/{old_root.joinpath('pid').read_text()}").exists())
        self.assertEqual(fresh_root.joinpath("requests").read_text().splitlines(), ["hello"])
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": partial}),
            "unchanged",
        )
        self.assertTrue(partial_root.is_dir())

    async def test_model_activation_shares_python_flock_and_cancellation_has_exact_identity(self):
        old, old_root = self.model_generation()
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        candidate, root = self.model_generation(previous=old, held=True)
        with (old_root.parent / "install.lock").open("r+") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.assertEqual(
                await self.ask(
                    "activateModel", {"identity": uuid.uuid4().hex, "generation": candidate}
                ),
                "unchanged",
            )
        self.assertFalse((root / "pid").exists())
        identity = uuid.uuid4().hex
        self.sequence += 1
        request = self.sequence
        await self.send(
            {
                "id": request,
                "op": "activateModel",
                "data": {"identity": identity, "generation": candidate},
            }
        )
        await self.send({"event": "cancelModelActivation", "data": uuid.uuid4().hex})
        await self.ask("settingsDraft")
        await self.release_hello(root)
        result = await self.receive_until(lambda value: value.get("reply") == request)
        self.assertEqual(result["data"], "confirmed")
        await self.send({"event": "cancelModelActivation", "data": identity})
        rejected, rejected_root = self.model_generation(previous=candidate, held=True)
        self.sequence += 1
        request = self.sequence
        cancelled = uuid.uuid4().hex
        await self.send(
            {
                "id": request,
                "op": "activateModel",
                "data": {"identity": cancelled, "generation": rejected},
            }
        )
        await self.send({"event": "cancelModelActivation", "data": cancelled})
        result = await self.receive_until(lambda value: value.get("reply") == request)
        self.assertEqual(result["data"], "unchanged")
        self.assertEqual(os.readlink(root.parent / "current"), candidate)
        self.assertTrue(self.latest("status")["modelReady"])
        self.assertTrue(rejected_root.is_dir())

    async def test_startup_recovers_only_explicit_previous_and_legacy_does_not_scan(self):
        previous, previous_root = self.model_generation(legacy=True)
        active, active_root = self.model_generation(previous=previous, directions=[])
        (active_root.parent / "current").symlink_to(active)
        self.assertTrue(await self.ask("start"))
        await self.receive_until(
            lambda value: value.get("event") == "status" and value["data"]["modelReady"]
        )
        self.assertEqual(os.readlink(active_root.parent / "current"), previous)
        self.assertEqual(previous_root.joinpath("requests").read_text().splitlines(), ["hello"])
        await self.asyncTearDown()
        (active_root / "activation.json").unlink()
        (active_root.parent / "current").unlink()
        (active_root.parent / "current").symlink_to(active)
        self.events.clear()
        await self.start_host()
        self.assertTrue(await self.ask("start"))
        failed = await self.receive_until(
            lambda value: value.get("event") == "status" and "无法加载" in value["data"]["model"]
        )
        self.assertFalse(failed["data"]["modelReady"])
        self.assertEqual(os.readlink(active_root.parent / "current"), active)
        self.assertEqual(previous_root.joinpath("requests").read_text().splitlines(), ["hello"])

    async def test_damaged_startup_stays_available_and_only_explicit_previous_can_recover(self):
        for damage in ("pointer", "runtime", "metadata", "metadata-directory", "previous-metadata"):
            with self.subTest(damage=damage):
                await self.asyncTearDown()
                previous, previous_root = self.model_generation(legacy=True)
                active, root = self.model_generation(
                    previous=previous, directions=[] if damage == "previous-metadata" else None
                )
                current = root.parent / "current"
                current.unlink(missing_ok=True)
                current.symlink_to("../outside" if damage == "pointer" else active)
                if damage == "runtime":
                    python = root / "runtime/venv/bin/python"
                    python.unlink()
                    python.symlink_to("/usr/bin/python3")
                if damage == "metadata":
                    (root / "activation.json").write_text(
                        json.dumps({"schema": "1", "previous": previous})
                    )
                if damage == "metadata-directory":
                    (root / "activation.json").unlink()
                    (root / "activation.json").mkdir()
                if damage == "previous-metadata":
                    (previous_root / "activation.json").write_text(
                        json.dumps({"schema": 999, "previous": None})
                    )
                self.events.clear()
                await self.start_host()
                self.assertTrue(await self.ask("start"))
                if damage == "runtime":
                    await self.receive_until(
                        lambda value: value.get("event") == "status" and value["data"]["modelReady"]
                    )
                    self.assertEqual(os.readlink(current), previous)
                    self.assertEqual(
                        (previous_root / "requests").read_text().splitlines(), ["hello"]
                    )
                else:
                    # Wait for the complete startup operation, not the transient
                    # active failure before an explicit previous is considered.
                    async with asyncio.timeout(3):
                        while not (await self.ask("translationPreparation", {"phase": "Idle"}))[
                            "primaryAction"
                        ]["enabled"]:
                            await asyncio.sleep(0)
                    status = self.latest("status")
                    self.assertFalse(status["modelReady"])
                    self.assertIn("无法加载", status["model"])
                    self.assertFalse((previous_root / "pid").exists())
                    self.assertEqual(
                        os.readlink(current), "../outside" if damage == "pointer" else active
                    )
                self.assertIsInstance(
                    await self.ask("translationPreparation", {"phase": "Idle"}), dict
                )

    async def test_one_shot_activation_confirms_publication_and_reaps_the_serving_candidate(self):
        generation, root = self.model_generation()
        helper = await asyncio.create_subprocess_exec(
            str(SUPERVISOR),
            str(HOST),
            "--activate-model",
            str(self.home / "package"),
            str(self.home / "data"),
            generation,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        try:
            output, error = await asyncio.wait_for(helper.communicate(), 5)
        finally:
            if helper.returncode is None:
                helper.terminate()
                await asyncio.wait_for(helper.wait(), 3)
        self.assertEqual((helper.returncode, output, error), (0, b"confirmed\n", b""))
        self.assertEqual(os.readlink(root.parent / "current"), generation)
        self.assertEqual((root / "requests").read_text().splitlines(), ["hello"])
        self.assertFalse(Path(f"/proc/{root.joinpath('pid').read_text()}").exists())

    async def test_cancel_during_practice_retirement_never_starts_candidate(self):
        await self.bind_automatic_practice()
        candidate, root = self.model_generation()
        self.sequence += 1
        request = self.sequence
        identity = uuid.uuid4().hex
        await self.send(
            {
                "id": request,
                "op": "activateModel",
                "data": {"identity": identity, "generation": candidate},
            }
        )
        retirement = await self.receive_until(
            lambda value: value.get("op") == "retireAutomaticPractice"
        )
        await self.send({"event": "cancelModelActivation", "data": identity})
        await self.ask("settingsDraft")
        await self.send({"reply": retirement["id"], "data": True})
        result = await self.receive_until(lambda value: value.get("reply") == request)
        self.assertEqual(result["data"], "unchanged")
        self.assertFalse((root / "pid").exists())
        self.assertFalse((root.parent / "current").exists())
        with (root.parent / "install.lock").open("r+") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)

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
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": uuid.uuid4().hex}
            ),
            "unchanged",
        )
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
        for patch in (
            {"theme": "dark"},
            {"enabled": 0},
            {"mode": 1},
            {"source": None},
            {"target": 1},
            {"target": "auto"},
            {"source": "fr", "target": "en"},
            {"source": "en", "target": "fr"},
            {"source": "fr", "target": "fr"},
            {},
        ):
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

        generation, component = self.model_generation(directions=[])
        (component.parent / "current").symlink_to(generation)
        # Restart observes one immutable installed generation, not marker files
        # written through a moving current path in an already-running client.
        await self.asyncTearDown()
        await self.start_host()
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

    async def test_language_choices_remain_available_without_claiming_a_ready_model(self):
        self.assertTrue(await self.ask("start"))
        status = self.latest("status")
        self.assertFalse(status["modelReady"])
        self.assertFalse(status["modelInstalled"])
        self.assertEqual(status["model"], "未安装本地模型")
        choices = self.latest("settingsGuidance")["languageChoices"]
        self.assertEqual([choice["code"] for choice in choices["source"]], ["auto", "zh", "en"])
        self.assertEqual([choice["code"] for choice in choices["target"]], ["en", "zh"])
        self.assertEqual(status["modelDirections"], [])

        saved = self.latest("settings")
        self.assertEqual((saved["source"], saved["target"]), ("auto", "en"))
        for source, target in (("auto", "zh"), ("zh", "zh"), ("en", "en"), ("en", "zh")):
            with self.subTest(source=source, target=target):
                self.assertTrue(
                    await self.ask("traySettings", {"source": source, "target": target})
                )
                self.assertEqual(self.latest("settingsGuidance")["languageChoices"], choices)
                self.assertEqual(
                    (self.latest("settings")["source"], self.latest("settings")["target"]),
                    (source, target),
                )
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": uuid.uuid4().hex}
            ),
            "unchanged",
        )
        self.assertEqual(self.latest("status")["modelDirections"], [])
        persisted = json.loads((self.home / "config/settings.json").read_text())
        self.assertEqual((persisted["Source"], persisted["Target"]), ("en", "zh"))

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

    async def test_control_session_keys_use_the_normal_draft_and_persistence_path(self):
        self.assertTrue(await self.ask("start"))
        result = await self.ask("saveSettingsDraft", self.draft("ctrl+f7", "Ctrl+F8"))
        self.assertTrue(result["saved"])
        settings = self.latest("settings")
        self.assertEqual(
            (settings["acceptShortcut"], settings["exitShortcut"]), ("Ctrl+F7", "Ctrl+F8")
        )
        configuration = self.latest("shortcuts")
        self.assertEqual((configuration["accept"], configuration["exit"]), ("Ctrl+F7", "Ctrl+F8"))
        self.assertEqual((await self.ask("sessionShortcutSettings"))["acceptKey"], "Ctrl+F7")
        saved = json.loads((self.home / "config/settings.json").read_text())
        self.assertEqual((saved["AcceptShortcut"], saved["ExitShortcut"]), ("Ctrl+F7", "Ctrl+F8"))
        for accept, exit_key in (("Ctrl+F7", "ctrl+f7"), ("F7", "Ctrl+F7"), ("Ctrl+Tab", "Esc")):
            self.assertFalse(
                (await self.ask("saveSettingsDraft", self.draft(accept, exit_key)))["saved"]
            )
            self.assertEqual(json.loads((self.home / "config/settings.json").read_text()), saved)

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
        self.assertEqual(result["message"], "接受与退出请选择有效且不同的主键。")
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
    parser.add_argument("--supervisor", type=Path)
    arguments, remaining = parser.parse_known_args()
    HOST = arguments.host.resolve()
    SUPERVISOR = (arguments.supervisor or HOST.parent.parent / "native/typelay-supervise").resolve()
    if not HOST.is_file():
        parser.error("--host must identify a built TypeLay.Linux executable")
    unittest.main(argv=[__file__, *remaining])
