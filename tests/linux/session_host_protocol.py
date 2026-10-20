"""Real Linux host pipe/configuration boundary, without a desktop or model.

The host runs with private temporary settings and inherited stdin/stdout. This
covers serialization and owner-loop settings behavior, not native key routing.
"""

import argparse
import asyncio
import fcntl
import hashlib
import json
import os
import shutil
import signal
import struct
import sys
import tempfile
import time
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from input import Input  # noqa: E402
from vscode_e2e import profile_data_home  # noqa: E402

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

    async def ask(self, operation, data=None, *, timeout=3):
        self.sequence += 1
        identity = self.sequence
        await self.send({"id": identity, "op": operation, "data": data})
        return (
            await self.receive_until(lambda value: value.get("reply") == identity, timeout=timeout)
        )["data"]

    async def receive_until(self, predicate, *, timeout=3):
        async with asyncio.timeout(timeout):
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

    def model_generation(
        self,
        *,
        previous=None,
        directions=None,
        held=False,
        held_translation=False,
        legacy=False,
        hello_directions=None,
        active_packages=None,
        hello_packages=None,
        installed_package_ids=None,
        package_directions=None,
        operation="Install",
        removed_packages=None,
    ):
        # These real Python pipe peers exercise ownership and publication, never
        # claim model inference. Production worker inventory tests live separately.
        generation = uuid.uuid4().hex
        root = self.home / "data/components" / generation
        (root / "models").mkdir(parents=True)
        (root / "runtime/python").mkdir(parents=True)
        (root / "runtime/venv/bin").mkdir(parents=True)
        shutil.copy2(Path("/usr/bin/python3").resolve(), root / "runtime/python/python")
        (root / "runtime/venv/bin/python").symlink_to("../../python/python")
        inventory = {
            "schema": 2 if directions is not None and len(directions) == 1 else 1,
            "directions": directions if directions is not None else ["zh_en", "en_zh"],
            "held": held,
            "heldTranslation": held_translation,
            "helloDirections": hello_directions,
            "helloPackages": hello_packages,
        }
        activation = {"schema": 1, "previous": previous}
        if directions is not None and len(directions) == 1:
            activation.update(schema=2, directions=directions)
        if active_packages is not None:
            catalog = json.loads(
                (
                    Path(__file__).resolve().parents[2]
                    / "src/shared/TypeLay.Worker/model-catalog.json"
                ).read_text()
            )
            packages = [
                {
                    "id": item["id"],
                    "version": item["version"],
                    "directions": (package_directions or {}).get(
                        item["id"], [direction["id"] for direction in item["directions"]]
                    ),
                    "files": [{"path": "model-file", "sha256": "0" * 64}],
                }
                for item in catalog["packages"]
                if installed_package_ids is None or item["id"] in installed_package_ids
            ]
            inventory.pop("directions")
            inventory.update(schema=3, packages=packages, activePackages=list(active_packages))
            activation = {
                "schema": 3,
                "previous": previous,
                "packages": [
                    {key: item[key] for key in ("id", "version", "directions")}
                    for item in packages
                    if item["id"] in active_packages
                ],
                "operation": operation,
                "removedPackages": removed_packages or [],
                "recoveryPrevious": None if operation == "Remove" else previous,
            }
        (root / "models/installed.json").write_text(json.dumps(inventory))
        if not legacy:
            (root / "activation.json").write_text(json.dumps(activation))
        if held:
            os.mkfifo(root / "hello-gate")
        if held_translation:
            os.mkfifo(root / "translation-gate")
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
(root / 'supervisor-pid').write_text(str(os.getppid()))
while True:
    header = sys.stdin.buffer.read(4)
    if not header: break
    request = json.loads(sys.stdin.buffer.read(struct.unpack('<I', header)[0]))
    with (root / 'requests').open('a') as log: log.write(request['op'] + '\n')
    if request['op'] == 'hello':
        if settings['held']:
            with (root / 'hello-gate').open() as gate: gate.read(1)
        if settings['schema'] == 3:
            packages = [{key: item[key] for key in ('id', 'version', 'directions')} for item in settings['packages'] if item['id'] in settings['activePackages']]
            directions = [direction for item in packages for direction in item['directions']]
            response = {'status': 'ready', 'packages': packages if settings['helloPackages'] is None else settings['helloPackages']}
        else:
            directions = settings['directions']
            response = {'status': 'ready'}
        response['directions'] = settings['helloDirections'] if settings['helloDirections'] is not None else directions
    else:
        if settings.get('heldTranslation'):
            with (root / 'translation-gate').open() as gate: gate.read(1)
        response = {'status': 'translated', 'text': root.name}
    response['id'] = request['id']
    encoded = json.dumps(response).encode()
    sys.stdout.buffer.write(struct.pack('<I', len(encoded)) + encoded)
    sys.stdout.buffer.flush()
""")
        return generation, root

    async def release_hello(self, root, gate="hello-gate"):
        async with asyncio.timeout(3):
            while True:
                try:
                    fd = os.open(root / gate, os.O_WRONLY | os.O_NONBLOCK)
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

    async def test_schema_three_switches_active_packages_and_restarts_from_the_exact_plan(self):
        old, old_root = self.model_generation()
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        hy, hy_root = self.model_generation(previous=old, active_packages=["hy-mt2-1-8b-q4-k-m"])
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": hy}),
            "confirmed",
        )
        self.assertFalse(Path(f"/proc/{old_root.joinpath('pid').read_text()}").exists())
        self.assertEqual(
            self.latest("status")["modelDirections"],
            [
                "zh_ja",
                "ja_zh",
                "zh_ko",
                "ko_zh",
                "zh_fr",
                "fr_zh",
                "zh_es",
                "es_zh",
                "zh_de",
                "de_zh",
                "zh_pt",
                "pt_zh",
            ],
        )
        self.assertEqual(hy_root.joinpath("requests").read_text().splitlines(), ["hello"])
        argos, argos_root = self.model_generation(
            previous=hy, active_packages=["argos-zh-en", "argos-en-zh"]
        )
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": argos}),
            "confirmed",
        )
        self.assertFalse(Path(f"/proc/{hy_root.joinpath('pid').read_text()}").exists())
        self.assertEqual(os.readlink(argos_root.parent / "current"), argos)
        await self.asyncTearDown()
        self.events.clear()
        await self.start_host()
        self.assertTrue(await self.ask("start"))
        await self.receive_until(
            lambda value: value.get("event") == "status" and value["data"]["modelReady"]
        )
        self.assertEqual(
            argos_root.joinpath("requests").read_text().splitlines(), ["hello", "hello"]
        )
        self.assertEqual(self.latest("status")["modelDirections"], ["zh_en", "en_zh"])

    async def test_language_guidance_tracks_installed_capability_without_resetting_saved_values(
        self,
    ):
        generation, _root = self.model_generation(active_packages=["hy-mt2-1-8b-q4-k-m"])
        self.assertEqual(
            await self.ask(
                "activateModel",
                {
                    "identity": uuid.uuid4().hex,
                    "generation": generation,
                },
            ),
            "confirmed",
        )
        preparation = {"phase": "Idle", "hyRuntimeAvailable": True, "hyUnavailableReason": None}
        await self.ask("translationPreparation", preparation)
        choices = self.latest("settingsGuidance")["languageChoices"]
        self.assertEqual(
            [item["code"] for item in choices["source"]],
            ["auto", "zh", "en", "ja", "ko", "fr", "es", "de", "pt"],
        )
        self.assertEqual(
            [item["code"] for item in choices["target"]],
            ["en", "zh", "ja", "ko", "fr", "es", "de", "pt"],
        )
        self.assertTrue(await self.ask("traySettings", {"source": "zh", "target": "fr"}))
        saved = (self.home / "config/settings.json").read_bytes()
        self.assertFalse(await self.ask("traySettings", {"source": "fr", "target": "ja"}))
        self.assertEqual((self.home / "config/settings.json").read_bytes(), saved)
        switched, switched_root = self.model_generation(
            previous=generation, active_packages=["argos-zh-en"], operation="Select"
        )
        self.assertEqual(
            await self.ask(
                "activateModel",
                {
                    "identity": uuid.uuid4().hex,
                    "generation": switched,
                },
            ),
            "confirmed",
        )
        view = await self.ask("translationPreparation", preparation)
        self.assertIn("当前模型不包含这个方向", view["guidance"])
        self.assertEqual((self.home / "config/settings.json").read_bytes(), saved)
        self.assertEqual(self.latest("settingsGuidance")["languageChoices"], choices)
        prior = json.loads((switched_root / "models/installed.json").read_text())
        removed_selection = [
            {key: item[key] for key in ("id", "version", "directions")}
            for item in prior["packages"]
            if item["id"] == "hy-mt2-1-8b-q4-k-m"
        ]
        removed, _ = self.model_generation(
            previous=switched,
            active_packages=["argos-zh-en"],
            installed_package_ids=["argos-zh-en", "argos-en-zh"],
            operation="Remove",
            removed_packages=removed_selection,
        )
        self.assertEqual(
            await self.ask(
                "activateModel",
                {
                    "identity": uuid.uuid4().hex,
                    "generation": removed,
                },
            ),
            "confirmed",
        )
        await self.ask("translationPreparation", preparation)
        choices = self.latest("settingsGuidance")["languageChoices"]
        self.assertEqual([item["code"] for item in choices["target"]], ["en", "zh", "fr"])
        self.assertEqual(choices["target"][-1]["label"], "Français（当前不可用）")
        self.assertEqual((self.home / "config/settings.json").read_bytes(), saved)

    async def test_old_hy_subset_and_missing_native_do_not_advertise_new_languages(self):
        generation, _root = self.model_generation(
            active_packages=["hy-mt2-1-8b-q4-k-m"],
            package_directions={"hy-mt2-1-8b-q4-k-m": ["zh_ja"]},
        )
        self.assertEqual(
            await self.ask(
                "activateModel",
                {
                    "identity": uuid.uuid4().hex,
                    "generation": generation,
                },
            ),
            "confirmed",
        )
        self.assertTrue(await self.ask("traySettings", {"source": "zh", "target": "ja"}))
        preparation = {"phase": "Idle", "hyRuntimeAvailable": True, "hyUnavailableReason": None}
        view = await self.ask("translationPreparation", preparation)
        choices = self.latest("settingsGuidance")["languageChoices"]
        self.assertEqual([item["code"] for item in choices["source"]], ["auto", "zh", "en"])
        self.assertEqual([item["code"] for item in choices["target"]], ["en", "zh", "ja"])
        self.assertEqual(self.latest("status")["modelDirections"], ["zh_ja"])
        row = next(row for row in view["packageRows"] if row["preview"])
        self.assertEqual(row["installedDirectionIds"], ["zh_ja"])
        self.assertEqual(row["readyDirectionIds"], ["zh_ja"])
        self.assertEqual(len(row["directionIds"]), 12)
        await self.ask(
            "translationPreparation",
            {
                **preparation,
                "hyRuntimeAvailable": False,
                "hyUnavailableReason": "native-unavailable",
            },
        )
        self.assertEqual(
            self.latest("settingsGuidance")["languageChoices"]["target"][-1],
            {"code": "ja", "label": "日本語（当前不可用）"},
        )
        self.assertEqual(self.latest("settings")["target"], "ja")

    async def test_remove_cannot_reactivate_removed_history_after_restart_failure(self):
        selected = ["argos-zh-en", "argos-en-zh"]
        old, old_root = self.model_generation(active_packages=selected)
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        prior = json.loads((old_root / "models/installed.json").read_text())
        removed = [
            {key: item[key] for key in ("id", "version", "directions")}
            for item in prior["packages"]
            if item["id"] == "hy-mt2-1-8b-q4-k-m"
        ]
        generation, root = self.model_generation(
            previous=old,
            active_packages=selected,
            installed_package_ids=selected,
            operation="Remove",
            removed_packages=removed,
        )
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": generation}
            ),
            "confirmed",
        )
        await self.asyncTearDown()
        inventory = json.loads((root / "models/installed.json").read_text())
        inventory["helloPackages"] = []
        (root / "models/installed.json").write_text(json.dumps(inventory))
        self.events.clear()
        await self.start_host()
        self.assertTrue(await self.ask("start"))
        await self.receive_until(
            lambda value: value.get("event") == "status"
            and not value["data"]["modelReady"]
            and value["data"]["model"] != "正在加载模型"
        )
        self.assertEqual(os.readlink(root.parent / "current"), generation)
        if old_root.exists():
            self.assertEqual(old_root.joinpath("requests").read_text().splitlines(), ["hello"])

    async def test_unknown_current_cannot_be_overwritten_by_legacy_full_repair(self):
        old, old_root = self.model_generation(active_packages=["argos-zh-en", "argos-en-zh"])
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        (old_root / "activation.json").unlink()
        (old_root / "models/installed.json").write_text("invalid")
        candidate, root = self.model_generation(previous=old)
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": candidate}
            ),
            "unchanged",
        )
        self.assertEqual(os.readlink(old_root.parent / "current"), old)
        self.assertFalse((root / "pid").exists())
        self.assertTrue(self.latest("status")["modelReady"])
        presentation = await self.ask("translationPreparation", {"phase": "Idle"})
        self.assertTrue(all(not row["actions"] for row in presentation["packageRows"]))

    def editor_request(self):
        text = "explicit source"
        return {
            "id": 1,
            "operation": "translateEditorSnapshot",
            "data": {
                "documentId": str(uuid.uuid4()),
                "version": 1,
                "selection": {"start": 0, "length": len(text)},
                "text": text,
                "languageId": "plaintext",
            },
        }

    async def test_explicit_snapshot_reuses_the_ready_hy_worker_and_exact_source_binding(self):
        generation, root = self.model_generation(active_packages=["hy-mt2-1-8b-q4-k-m"])
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": generation}
            ),
            "confirmed",
        )
        self.assertTrue(await self.ask("settings", {"source": "zh", "target": "ja"}))
        request = self.editor_request()
        response = await self.ask(
            "translateEditorSnapshot", {"identity": uuid.uuid4().hex, "request": request}
        )
        self.assertEqual(
            response,
            {
                "id": 1,
                "data": {
                    "documentId": request["data"]["documentId"],
                    "version": 1,
                    "selection": request["data"]["selection"],
                    "translation": generation,
                },
            },
        )
        self.assertEqual((root / "requests").read_text().splitlines(), ["hello", "translate"])
        invalid = {**request, "data": {**request["data"], "prompt": "not permitted"}}
        self.assertEqual(
            (
                await self.ask(
                    "translateEditorSnapshot", {"identity": uuid.uuid4().hex, "request": invalid}
                )
            )["error"],
            "invalid-request",
        )
        self.assertEqual((root / "requests").read_text().splitlines(), ["hello", "translate"])
        source = str(Path(__file__).resolve().parents[2] / "src/linux/desktop")
        sys.path.insert(0, source)
        try:
            from main import tutorial_preview
        finally:
            sys.path.remove(source)
        test = self

        class DesktopHost:
            async def ask(self, operation, data, *, timeout):
                return await asyncio.wait_for(test.ask(operation, data), timeout)

        self.assertEqual(await tutorial_preview(DesktopHost(), "本地试译"), generation)
        self.assertEqual(
            (root / "requests").read_text().splitlines(), ["hello", "translate", "translate"]
        )

    async def test_cancelled_or_settings_stale_snapshot_cannot_return_late_translation(self):
        generation, root = self.model_generation(held_translation=True)
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": generation}
            ),
            "confirmed",
        )
        for change in ("wrong-cancel", "cancel", "settings"):
            with self.subTest(change=change):
                identity = uuid.uuid4().hex
                self.sequence += 1
                reply = self.sequence
                before = len((root / "requests").read_text().splitlines())
                await self.send(
                    {
                        "id": reply,
                        "op": "translateEditorSnapshot",
                        "data": {"identity": identity, "request": self.editor_request()},
                    }
                )
                async with asyncio.timeout(3):
                    while len((root / "requests").read_text().splitlines()) == before:
                        await asyncio.sleep(0.01)
                if change == "settings":
                    self.assertTrue(await self.ask("settings", {"source": "en", "target": "zh"}))
                else:
                    await self.send(
                        {
                            "event": "cancelEditorSnapshot",
                            "data": identity if change == "cancel" else uuid.uuid4().hex,
                        }
                    )
                    self.assertIsNone(
                        await self.ask(
                            "translateEditorSnapshot",
                            {"identity": uuid.uuid4().hex, "request": self.editor_request()},
                        )
                    )
                await self.release_hello(root, "translation-gate")
                response = (await self.receive_until(lambda frame: frame.get("reply") == reply))[
                    "data"
                ]
                if change == "wrong-cancel":
                    self.assertEqual(response["data"]["translation"], generation)
                else:
                    self.assertEqual(response, {"id": 1, "error": "translation-failed"})
        self.assertEqual(
            (root / "requests").read_text().splitlines(),
            ["hello", "translate", "translate", "translate"],
        )
        self.assertTrue(self.latest("status")["modelReady"])

    async def test_remove_keeps_old_generation_while_another_consumer_holds_its_use_lock(self):
        selected = ["argos-zh-en", "argos-en-zh"]
        old, old_root = self.model_generation(active_packages=selected)
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        prior = json.loads((old_root / "models/installed.json").read_text())
        removed = [
            {key: item[key] for key in ("id", "version", "directions")}
            for item in prior["packages"]
            if item["id"] == "hy-mt2-1-8b-q4-k-m"
        ]
        generation, root = self.model_generation(
            previous=old,
            active_packages=selected,
            installed_package_ids=selected,
            operation="Remove",
            removed_packages=removed,
        )
        with (old_root / "use.lock").open("r+") as other:
            fcntl.flock(other, fcntl.LOCK_SH | fcntl.LOCK_NB)
            self.assertEqual(
                await self.ask(
                    "activateModel", {"identity": uuid.uuid4().hex, "generation": generation}
                ),
                "confirmed",
            )
            self.assertTrue(old_root.exists())
            self.assertTrue(self.latest("status")["modelReady"])
            self.assertTrue(self.latest("status")["modelCleanupPending"])
            self.assertEqual(os.readlink(root.parent / "current"), generation)

    async def test_repair_cannot_change_the_active_model_family(self):
        old, old_root = self.model_generation(active_packages=["argos-zh-en", "argos-en-zh"])
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        candidate, root = self.model_generation(
            previous=old,
            active_packages=["hy-mt2-1-8b-q4-k-m"],
            operation="Repair",
        )
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": candidate}
            ),
            "unchanged",
        )
        self.assertFalse((root / "pid").exists())
        self.assertEqual(os.readlink(old_root.parent / "current"), old)
        self.assertTrue(self.latest("status")["modelReady"])

    async def test_remove_cleanup_failure_keeps_confirmed_and_reports_unreleased_space(self):
        selected = ["argos-zh-en", "argos-en-zh"]
        ancestor, ancestor_root = self.model_generation(active_packages=selected)
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": ancestor}),
            "confirmed",
        )
        old, old_root = self.model_generation(previous=ancestor, active_packages=selected)
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        (ancestor_root / "models/installed.json").write_text("invalid")
        prior = json.loads((old_root / "models/installed.json").read_text())
        removed = [
            {key: item[key] for key in ("id", "version", "directions")}
            for item in prior["packages"]
            if item["id"] == "hy-mt2-1-8b-q4-k-m"
        ]
        generation, root = self.model_generation(
            previous=old,
            active_packages=selected,
            installed_package_ids=selected,
            operation="Remove",
            removed_packages=removed,
        )
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": generation}
            ),
            "confirmed",
        )
        self.assertTrue(self.latest("status")["modelReady"])
        self.assertTrue(self.latest("status")["modelCleanupPending"])
        self.assertEqual(os.readlink(root.parent / "current"), generation)
        self.assertTrue(old_root.exists())
        self.assertTrue(ancestor_root.exists())
        self.assertEqual(root.joinpath("requests").read_text().splitlines(), ["hello"])

    async def test_missing_activation_metadata_cannot_turn_schema_three_into_legacy_repair(self):
        old, old_root = self.model_generation(active_packages=["argos-zh-en", "argos-en-zh"])
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        (old_root / "activation.json").unlink()
        candidate, root = self.model_generation(previous=old)
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": candidate}
            ),
            "unchanged",
        )
        self.assertFalse((root / "pid").exists())
        self.assertTrue(Path(f"/proc/{old_root.joinpath('pid').read_text()}").exists())
        self.assertTrue(self.latest("status")["modelReady"])
        self.assertEqual(os.readlink(root.parent / "current"), old)

    async def test_invalid_schema_three_metadata_never_retires_the_live_worker(self):
        old, old_root = self.model_generation()
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        candidate, root = self.model_generation(
            previous=old, active_packages=["hy-mt2-1-8b-q4-k-m"]
        )
        metadata = json.loads((root / "activation.json").read_text())
        metadata["packages"][0]["version"] = "unknown"
        (root / "activation.json").write_text(json.dumps(metadata))
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": candidate}
            ),
            "unchanged",
        )
        self.assertFalse((root / "pid").exists())
        self.assertTrue(Path(f"/proc/{old_root.joinpath('pid').read_text()}").exists())
        self.assertTrue(self.latest("status")["modelReady"])
        self.assertEqual(os.readlink(root.parent / "current"), old)

    async def test_failed_hy_hello_reaps_candidate_then_reloads_the_exact_old_generation(self):
        old, old_root = self.model_generation()
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        first_pid = int((old_root / "pid").read_text())
        candidate, root = self.model_generation(
            previous=old, active_packages=["hy-mt2-1-8b-q4-k-m"], hello_packages=[]
        )
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": candidate}
            ),
            "unchanged",
        )
        self.assertNotEqual(int((old_root / "pid").read_text()), first_pid)
        self.assertFalse(Path(f"/proc/{first_pid}").exists())
        self.assertFalse(Path(f"/proc/{root.joinpath('pid').read_text()}").exists())
        self.assertEqual(old_root.joinpath("requests").read_text().splitlines(), ["hello", "hello"])
        self.assertEqual(os.readlink(root.parent / "current"), old)
        self.assertTrue(self.latest("status")["modelReady"])

    async def test_timed_out_owned_exit_retains_its_proof_owner_until_a_successor_reaps_it(self):
        old, old_root = self.model_generation()
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        supervisor = int((old_root / "supervisor-pid").read_text())
        os.kill(supervisor, signal.SIGSTOP)
        self.addCleanup(
            lambda: os.kill(supervisor, signal.SIGCONT)
            if Path(f"/proc/{supervisor}").exists()
            else None
        )
        candidate, root = self.model_generation(
            previous=old, active_packages=["hy-mt2-1-8b-q4-k-m"]
        )
        # ModelClient's bounded proof wait is five seconds; ordinary replies retain
        # their existing three-second budget.
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": candidate}, timeout=8
            ),
            "unchanged",
        )
        self.assertFalse((root / "pid").exists())
        self.assertFalse(self.latest("status")["modelReady"])
        self.sequence += 1
        request = self.sequence
        await self.send(
            {
                "id": request,
                "op": "activateModel",
                "data": {"identity": uuid.uuid4().hex, "generation": candidate},
            }
        )
        await self.receive_until(
            lambda value: value.get("event") == "status"
            and value["data"]["model"] == "正在切换模型"
        )
        self.assertFalse((root / "pid").exists())
        os.kill(supervisor, signal.SIGCONT)
        self.assertEqual(
            (await self.receive_until(lambda value: value.get("reply") == request))["data"],
            "confirmed",
        )
        self.assertFalse(Path(f"/proc/{supervisor}").exists())
        self.assertTrue(self.latest("status")["modelReady"])

    async def test_single_direction_can_be_supplemented_only_after_exact_candidate_hello(self):
        old, old_root = self.model_generation(directions=["zh_en"])
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        self.assertEqual(self.latest("status")["modelDirections"], ["zh_en"])
        incomplete, failed_root = self.model_generation(previous=old, hello_directions=["zh_en"])
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": incomplete}
            ),
            "unchanged",
        )
        self.assertEqual(os.readlink(old_root.parent / "current"), old)
        self.assertTrue(self.latest("status")["modelReady"])
        self.assertEqual(self.latest("status")["modelDirections"], ["zh_en"])
        self.assertTrue(Path(f"/proc/{old_root.joinpath('pid').read_text()}").exists())
        self.assertFalse(Path(f"/proc/{failed_root.joinpath('pid').read_text()}").exists())
        full, root = self.model_generation(previous=old)
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": full}),
            "confirmed",
        )
        self.assertEqual(self.latest("status")["modelDirections"], ["zh_en", "en_zh"])
        self.assertEqual(root.joinpath("requests").read_text().splitlines(), ["hello"])
        self.assertFalse(Path(f"/proc/{old_root.joinpath('pid').read_text()}").exists())

    async def test_conflicting_committed_intent_requires_full_repair_without_losing_old_service(
        self,
    ):
        old, old_root = self.model_generation()
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": old}),
            "confirmed",
        )
        marker = old_root / "models/installed.json"
        damaged = json.loads(marker.read_text())
        damaged.update(schema=2, directions=["zh_en"])
        marker.write_text(json.dumps(damaged))
        partial, partial_root = self.model_generation(previous=old, directions=["zh_en"])
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": partial}),
            "unchanged",
        )
        self.assertEqual(os.readlink(old_root.parent / "current"), old)
        self.assertFalse((partial_root / "pid").exists())
        self.assertTrue(self.latest("status")["modelReady"])
        self.assertEqual(self.latest("status")["modelDirections"], ["zh_en", "en_zh"])
        presentation = await self.ask("translationPreparation", {"phase": "Idle"})
        self.assertFalse(presentation["canSelectDirections"])
        self.assertFalse(presentation["primaryAction"]["enabled"])
        self.assertTrue(all(not row["actions"] for row in presentation["packageRows"]))
        full, root = self.model_generation(previous=old)
        self.assertEqual(
            await self.ask("activateModel", {"identity": uuid.uuid4().hex, "generation": full}),
            "confirmed",
        )
        self.assertEqual(os.readlink(root.parent / "current"), full)
        self.assertFalse(Path(f"/proc/{old_root.joinpath('pid').read_text()}").exists())

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
        active, active_root = self.model_generation(previous=previous, hello_directions=[])
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
                    previous=previous,
                    hello_directions=[] if damage == "previous-metadata" else None,
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
                    if "无法加载" not in self.latest("status")["model"]:
                        await self.receive_until(
                            lambda value: value.get("event") == "status"
                            and "无法加载" in value["data"]["model"]
                        )
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

    async def test_one_shot_activation_reports_only_the_failed_stage(self):
        for damage, failure in (
            ("metadata", "installation-invalid"),
            ("hello", "worker-not-ready"),
        ):
            with self.subTest(damage=damage):
                generation, root = self.model_generation(hello_directions=[])
                if damage == "metadata":
                    (root / "activation.json").write_text("private-invalid-metadata")
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
                self.assertEqual(
                    (helper.returncode, output, error), (1, b"unchanged\n", f"{failure}\n".encode())
                )
                self.assertFalse((root.parent / "current").exists())
                if damage == "metadata":
                    self.assertFalse((root / "pid").exists())
                else:
                    self.assertEqual((root / "requests").read_text().splitlines(), ["hello"])
                    self.assertFalse(Path(f"/proc/{root.joinpath('pid').read_text()}").exists())

    def editor_environment(self):
        runtime = self.home / "run"
        runtime.mkdir(exist_ok=True)
        session = runtime / "typelay" / hashlib.sha256(b"\n").hexdigest()[:16]
        session.mkdir(parents=True, exist_ok=True)
        return {
            **os.environ,
            "TYPELAY_HOME": str(self.home / "package"),
            "XDG_CONFIG_HOME": str(self.home / "cli-config"),
            "XDG_DATA_HOME": str(self.home / "cli-data"),
            "XDG_CACHE_HOME": str(self.home / "cli-cache"),
            "XDG_RUNTIME_DIR": str(runtime),
            "WAYLAND_DISPLAY": "",
            "NIRI_SOCKET": "",
        }, session

    async def launch_editor(self, environment):
        source = Path(__file__).resolve().parents[2] / "src/linux/desktop"
        process = await asyncio.create_subprocess_exec(
            sys.executable,
            "-I",
            "-B",
            "-c",
            "import runpy,sys; sys.path.insert(0,sys.argv.pop(1)); runpy.run_module('main',run_name='__main__')",
            str(source),
            "--editor-host",
            env=environment,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )

        async def stop():
            if process.returncode is None:
                process.terminate()
                await asyncio.wait_for(process.communicate(), 5)

        self.addAsyncCleanup(stop)
        return process

    async def test_vscode_profile_reaches_existing_model_store_without_directory_aliases(self):
        installed = self.home / "installed-data" / "typelay"
        (installed / "components").mkdir(parents=True)
        profile = self.home / "vscode-profile"
        data_home = profile_data_home(profile, installed)
        process = await asyncio.create_subprocess_exec(
            str(HOST),
            "--editor-host",
            str(self.home / "package"),
            str(profile / "config" / "typelay"),
            str(data_home / "typelay"),
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        request = self.editor_request()
        raw = json.dumps(request).encode()
        try:
            output, error = await asyncio.wait_for(
                process.communicate(struct.pack("<I", len(raw)) + raw), 5
            )
        finally:
            if process.returncode is None:
                process.kill()
                await asyncio.wait_for(process.communicate(), 3)
        self.assertEqual(struct.unpack("<I", output[:4])[0], len(output) - 4)
        # No model exists. Reaching the real store must report not-ready, rather
        # than the consumer's previous alias causing an earlier host-failed.
        self.assertEqual(json.loads(output[4:]), {"id": request["id"], "error": "not-ready"})
        self.assertEqual(process.returncode, 0, error)
        self.assertEqual(data_home / "typelay", installed)
        self.assertTrue((installed / "components" / "install.lock").is_file())
        self.assertFalse((profile / "share" / "typelay" / "components").exists())

    async def test_editor_cli_uses_private_desktop_snapshot_and_never_falls_back_on_connection_failure(
        self,
    ):
        generation, root = self.model_generation()
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": generation}
            ),
            "confirmed",
        )
        package = self.home / "package"
        (package / "app").mkdir()
        marker = self.home / "unexpected-one-shot"
        executable = package / "app/TypeLay.Linux"
        executable.write_text(
            f"#!/usr/bin/python3\nfrom pathlib import Path\nPath({str(marker)!r}).touch()\n"
        )
        executable.chmod(0o700)
        environment, session = self.editor_environment()
        request = self.editor_request()
        raw = json.dumps(request).encode()
        calls = []

        async def handle(reader, writer):
            try:
                length = struct.unpack("<I", await reader.readexactly(4))[0]
                frame = json.loads(await reader.readexactly(length))
                calls.append(frame)
                response = await self.ask(
                    "translateEditorSnapshot",
                    {"identity": uuid.uuid4().hex, "request": frame["data"]},
                )
                encoded = json.dumps({"reply": frame["id"], "data": response}).encode()
                writer.write(struct.pack("<I", len(encoded)) + encoded)
                await writer.drain()
            finally:
                writer.close()
                await writer.wait_closed()

        server = await asyncio.start_unix_server(handle, path=session / "control.sock")
        (session / "control.sock").chmod(0o600)
        with (session / "desktop.lock").open("w+") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            try:
                cli = await self.launch_editor(environment)
                output, error = await asyncio.wait_for(
                    cli.communicate(struct.pack("<I", len(raw)) + raw), 5
                )
                self.assertEqual(cli.returncode, 0, error)
                self.assertEqual(json.loads(output[4:])["data"]["translation"], generation)
                self.assertEqual(calls[0]["op"], "translateEditorSnapshot")
                self.assertEqual(calls[0]["data"], request)
            finally:
                server.close()
                await server.wait_closed()
            # Python 3.13+ removes the owned socket when the server closes;
            # older supported interpreters leave the same fixture path behind.
            (session / "control.sock").unlink(missing_ok=True)
            self.assertFalse((session / "control.sock").exists())
            cli = await self.launch_editor(environment)
            output, error = await asyncio.wait_for(
                cli.communicate(struct.pack("<I", len(raw)) + raw), 5
            )
            self.assertEqual(cli.returncode, 1, error)
            self.assertEqual(json.loads(output[4:]), {"id": 1, "error": "host-failed"})
            self.assertFalse(marker.exists())
        self.assertEqual((root / "requests").read_text().splitlines(), ["hello", "translate"])

    async def test_editor_without_desktop_keeps_startup_and_generation_locks_until_real_exit(self):
        generation, root = self.model_generation()
        (root.parent / "current").symlink_to(generation)
        environment, session = self.editor_environment()
        copied = self.home / "cli-data/typelay"
        shutil.copytree(self.home / "data", copied, symlinks=True)
        active = copied / "components" / generation
        inventory = json.loads((active / "models/installed.json").read_text())
        inventory["heldTranslation"] = True
        (active / "models/installed.json").write_text(json.dumps(inventory))
        os.mkfifo(active / "translation-gate")
        (self.home / "package/app").mkdir()
        (self.home / "package/app/TypeLay.Linux").symlink_to(HOST)
        cli = await self.launch_editor(environment)
        raw = json.dumps(self.editor_request()).encode()
        result = asyncio.create_task(cli.communicate(struct.pack("<I", len(raw)) + raw))
        async with asyncio.timeout(5):
            while (
                not (active / "requests").exists()
                or "translate" not in (active / "requests").read_text()
            ):
                await asyncio.sleep(0.01)
        with (
            (session / "desktop.lock").open("r+") as desktop,
            (active / "use.lock").open("r+") as use,
        ):
            for lock in (desktop, use):
                with self.assertRaises(BlockingIOError):
                    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            await self.release_hello(active, "translation-gate")
            output, error = await asyncio.wait_for(result, 5)
            self.assertEqual(cli.returncode, 0, error)
            self.assertEqual(json.loads(output[4:])["data"]["translation"], generation)
            for name in ("pid", "supervisor-pid"):
                self.assertFalse(Path(f"/proc/{(active / name).read_text()}").exists())
            for lock in (desktop, use):
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)

    async def test_install_cli_preserves_helper_outcome_without_forwarding_private_output(self):
        generation, root = self.model_generation()
        package = self.home / "package"
        (package / "desktop").mkdir()
        # Preparation and helper are controlled executable peers. The real CLI
        # runs once per case; these cases make no model-inference claim.
        (package / "desktop/setup.py").write_text(f"print('TYPELAY_MODEL_PREPARED={generation}')\n")
        (package / "app").mkdir()
        executable = package / "app/TypeLay.Linux"
        runtime = self.home / "run"
        runtime.mkdir()
        source = Path(__file__).resolve().parents[2] / "src/linux/desktop"
        for code, output, error, status, failure in (
            (1, "unchanged\n", "worker-not-ready\n", "unchanged", "worker-not-ready"),
            (1, "unchanged\n", "activation-failed\n", "unchanged", "activation-failed"),
            (9, "confirmed\n", "private-details\n", "confirmed", "unknown"),
            (1, "", "private-details\n", "empty", "unknown"),
            (
                1,
                "private-result\n",
                "installation-invalid\nprivate-details\n",
                "unknown",
                "unknown",
            ),
        ):
            with self.subTest(status=status):
                calls = root / "helper-calls"
                calls.unlink(missing_ok=True)
                executable.write_text(
                    "#!/usr/bin/python3\nimport pathlib,sys\n"
                    f"with pathlib.Path({str(calls)!r}).open('a') as log: log.write('call\\n')\n"
                    f"sys.stdout.write({output!r})\nsys.stderr.write({error!r})\n"
                    f"raise SystemExit({code})\n"
                )
                executable.chmod(0o700)
                cli = await asyncio.create_subprocess_exec(
                    sys.executable,
                    "-I",
                    "-B",
                    "-c",
                    "import runpy,sys; sys.path.insert(0,sys.argv.pop(1)); "
                    "runpy.run_module('main',run_name='__main__')",
                    str(source),
                    "--install-models",
                    env={
                        **os.environ,
                        "TYPELAY_HOME": str(package),
                        "XDG_CONFIG_HOME": str(self.home / "cli-config"),
                        "XDG_DATA_HOME": str(self.home / "cli-data"),
                        "XDG_CACHE_HOME": str(self.home / "cli-cache"),
                        "XDG_RUNTIME_DIR": str(runtime),
                        "WAYLAND_DISPLAY": "",
                        "NIRI_SOCKET": "",
                    },
                    stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.PIPE,
                )
                try:
                    stdout, stderr = await asyncio.wait_for(cli.communicate(), 5)
                finally:
                    if cli.returncode is None:
                        cli.terminate()
                        await asyncio.wait_for(cli.wait(), 3)
                self.assertEqual(cli.returncode, 1, stdout + stderr)
                self.assertIn(
                    f"activation-helper exit={code} status={status} failure={failure}".encode(),
                    stderr,
                )
                self.assertIn("已保留组件".encode(), stderr)
                self.assertNotIn(b"private-", stdout + stderr)
                self.assertEqual(calls.read_text().splitlines(), ["call"])

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

    async def test_translation_settings_closed_release_has_no_additions_and_ordinary_dto_is_separate(
        self,
    ):
        style = await self.ask("getStyleSettings")
        self.assertIsInstance(style, dict)
        self.assertFalse(style["isReleased"])
        self.assertFalse(style["canChange"])
        glossary = await self.ask("getGlossarySettings")
        self.assertFalse(glossary["isReleased"])
        self.assertFalse(glossary["canSave"])
        directions = {(item["source"], item["target"]) for item in glossary["directions"]}
        self.assertIn(("fr", "zh"), directions)
        self.assertNotIn(("fr", "en"), directions)
        identity = {
            "session": str(uuid.uuid4()),
            "request": 1,
            "expectedSettingsRevision": style["settingsRevision"],
        }
        result = await self.ask("changeStyle", {**identity, "style": "Natural"})
        self.assertEqual(result, {"saved": False, "code": "style-unavailable"})
        result = await self.ask(
            "changeGlossary",
            {**identity, "rules": [{"sourceTerm": "TypeLay", "targetTerm": "TypeLay"}]},
        )
        self.assertEqual(result, {"saved": False, "code": "glossary-not-released"})
        self.assertFalse(await self.ask("settings", {"styles": None}))
        self.assertFalse(await self.ask("settings", {"glossary": None}))
        self.assertTrue(await self.ask("settings", {"theme": "dark"}))
        ordinary = self.latest("settings")
        self.assertNotIn("styles", ordinary)
        self.assertNotIn("glossary", ordinary)

    async def test_overlay_style_route_accepts_only_frozen_session_payload_and_keeps_release_closed(
        self,
    ):
        before = await self.ask("getStyleSettings")
        token = {
            "target": {
                "backend": "linux-gtk3",
                "value": str(uuid.uuid4()),
                "lifetime": str(uuid.uuid4()),
            },
            "commandGeneration": str(uuid.uuid4()),
            "revision": 1,
        }
        request = {"token": token, "settingsRevision": before["settingsRevision"], "style": None}
        for style in (None, "Natural", "Formal"):
            self.assertEqual(
                await self.ask("sessionStyle", {**request, "style": style}),
                {"saved": False, "code": "style-not-released"},
            )
        for invalid in (
            None,
            {},
            {**request, "style": 1},
            {**request, "style": "natural"},
            {**request, "settingsRevision": -1},
            {**request, "settingsRevision": True},
            {**request, "application": "other"},
            {**request, "scope": "Global"},
        ):
            self.assertEqual(
                await self.ask("sessionStyle", invalid),
                {"saved": False, "code": "invalid-session-style"},
            )
        await self.send({"event": "alternative", "data": token})
        self.assertEqual(await self.ask("getStyleSettings"), before)
        self.assertFalse((self.home / "config/settings.json").exists())

    async def test_inline_glossary_route_rejects_forged_forms_and_keeps_settings_separate(self):
        before = await self.ask("getGlossarySettings")
        token = {
            "target": {
                "backend": "linux-gtk3",
                "value": str(uuid.uuid4()),
                "lifetime": str(uuid.uuid4()),
            },
            "commandGeneration": str(uuid.uuid4()),
            "revision": 1,
        }
        self.assertIsNone(await self.ask("sessionGlossaryBegin", token))
        request = {
            "formId": str(uuid.uuid4()),
            "token": token,
            "settingsRevision": before["settingsRevision"],
            "rule": {
                "sourceTerm": "PR",
                "targetTerm": "PR",
                "sourceLanguage": None,
                "targetLanguage": None,
            },
        }
        result = await self.ask("sessionGlossaryAdd", request)
        self.assertFalse(result["saved"])
        self.assertEqual(result["code"], "glossary-stale")
        self.assertTrue(result["message"])
        for invalid in (
            None,
            {},
            {**request, "formId": 3},
            {**request, "rules": [request["rule"]]},
            {**request, "settingsRevision": True},
            {**request, "rule": {**request["rule"], "application": "mail"}},
            {**request, "token": {**token, "revision": 0}},
        ):
            result = await self.ask("sessionGlossaryAdd", invalid)
            self.assertFalse(result["saved"])
            self.assertEqual(result["code"], "invalid-session-glossary")
        await self.send(
            {
                "event": "sessionGlossaryFocus",
                "data": {"formId": request["formId"], "token": token, "ime": "Clear"},
            }
        )
        self.assertEqual((await self.ask("sessionGlossaryAdd", request))["code"], "glossary-stale")
        await self.send({"event": "sessionGlossaryClose", "data": request["formId"]})
        self.assertEqual(await self.ask("getGlossarySettings"), before)
        self.assertFalse((self.home / "config/settings.json").exists())

    async def seed_translation_preferences(self):
        await self.asyncTearDown()
        settings_file = self.home / "config/settings.json"
        settings_file.parent.mkdir(exist_ok=True)
        settings_file.write_text(
            json.dumps(
                {
                    "Styles": {
                        "Default": "formal",
                        "Applications": {"editor": "natural", "mail": "formal"},
                    },
                    "Glossary": {
                        "Version": 1,
                        "Rules": [
                            {"SourceTerm": "TypeLay", "TargetTerm": "TypeLay"},
                            {
                                "SourceTerm": "projet",
                                "TargetTerm": "项目",
                                "SourceLanguage": "fr",
                                "TargetLanguage": "zh",
                            },
                        ],
                    },
                    "ExcludedApplications": ["private-editor"],
                }
            )
        )
        self.events.clear()
        await self.start_host()
        return settings_file

    @staticmethod
    def translation_request(revision, **payload):
        return {
            "session": str(uuid.uuid4()),
            "request": 1,
            "expectedSettingsRevision": revision,
            **payload,
        }

    async def test_saved_translation_preferences_recover_offline_without_opening_additions(self):
        settings_file = await self.seed_translation_preferences()
        style = await self.ask("getStyleSettings")
        self.assertFalse(style["isReleased"])
        self.assertTrue(style["canReset"])
        self.assertEqual(style["savedPreferences"]["default"], "Formal")
        self.assertEqual(
            [item["application"] for item in style["applications"]], ["editor", "mail"]
        )
        self.assertTrue(all(item["removal"]["canRemove"] for item in style["applications"]))
        glossary = await self.ask("getGlossarySettings")
        rules = glossary["savedSnapshot"]["rules"]
        before = settings_file.read_bytes()
        rejected = await self.ask(
            "removeStyleOverride",
            self.translation_request(style["settingsRevision"], application="not-saved"),
        )
        self.assertFalse(rejected["saved"], "an unknown app identity must not remove a stored key")
        self.assertEqual(settings_file.read_bytes(), before)
        removed = await self.ask(
            "removeStyleOverride",
            self.translation_request(style["settingsRevision"], application="editor"),
        )
        self.assertTrue(removed["saved"])
        state = await self.ask("getStyleSettings")
        self.assertEqual(
            state["savedPreferences"], {"default": "Formal", "applications": {"mail": "Formal"}}
        )
        self.assertEqual((await self.ask("getGlossarySettings"))["savedSnapshot"]["rules"], rules)
        self.assertTrue(await self.ask("settings", {"theme": "dark"}))
        self.assertNotIn("styles", self.latest("settings"))
        self.assertNotIn("glossary", self.latest("settings"))
        self.assertEqual(
            (await self.ask("getStyleSettings"))["savedPreferences"], state["savedPreferences"]
        )
        state = await self.ask("getStyleSettings")
        reset = await self.ask(
            "resetTranslationStyle", self.translation_request(state["settingsRevision"])
        )
        self.assertTrue(reset["saved"])
        self.assertIsNone((await self.ask("getStyleSettings"))["savedPreferences"])
        glossary = await self.ask("getGlossarySettings", [rules[1]])
        self.assertTrue(glossary["canSave"])
        self.assertFalse(glossary["isReleased"])
        result = await self.ask(
            "changeGlossary",
            self.translation_request(glossary["settingsRevision"], rules=[rules[1]]),
        )
        self.assertTrue(result["saved"])
        self.assertEqual(
            (await self.ask("getGlossarySettings"))["savedSnapshot"]["rules"], [rules[1]]
        )
        saved = json.loads(settings_file.read_text())
        self.assertNotIn("Styles", saved)
        self.assertEqual(saved["Glossary"]["Rules"][0]["SourceLanguage"], "fr")
        self.assertEqual(saved["Theme"], "dark")

    async def test_translation_recovery_conflicts_invalid_fields_and_failed_write_leave_preferences(
        self,
    ):
        settings_file = await self.seed_translation_preferences()
        state = await self.ask("getGlossarySettings")
        rules = state["savedSnapshot"]["rules"]
        draft = self.translation_request(state["settingsRevision"], rules=[rules[0]])
        self.assertTrue(await self.ask("settings", {"theme": "dark"}))
        before = settings_file.read_bytes()
        stale = await self.ask("changeGlossary", draft)
        self.assertEqual(stale, {"saved": False, "code": "glossary-stale"})
        state = await self.ask("getGlossarySettings")
        draft["expectedSettingsRevision"] = state["settingsRevision"]
        for payload in (
            {**draft, "session": ""},
            {**draft, "request": "1"},
            {**draft, "request": 0},
            {**draft, "token": {}},
            {**draft, "expectedSettingsRevision": -1},
        ):
            self.assertEqual(
                await self.ask("changeGlossary", payload),
                {"saved": False, "code": "invalid-translation-settings"},
            )
        altered = [{**rules[0], "targetTerm": "altered"}]
        self.assertFalse((await self.ask("getGlossarySettings", altered))["canSave"])
        self.assertEqual(
            await self.ask("changeGlossary", {**draft, "rules": altered}),
            {"saved": False, "code": "glossary-not-released"},
        )
        obstruction = settings_file.with_suffix(".json.tmp")
        obstruction.mkdir()
        self.assertEqual(
            await self.ask("changeGlossary", draft),
            {"saved": False, "code": "glossary-save-failed"},
        )
        self.assertEqual(settings_file.read_bytes(), before)
        self.assertEqual((await self.ask("getGlossarySettings"))["savedSnapshot"]["rules"], rules)
        obstruction.rmdir()
        self.assertTrue((await self.ask("changeGlossary", draft))["saved"])
        self.assertEqual(
            (await self.ask("getGlossarySettings"))["savedSnapshot"]["rules"], [rules[0]]
        )

    async def test_translation_cancel_during_retirement_is_exact_and_prevents_late_commit(self):
        settings_file = await self.seed_translation_preferences()
        for operation, payload in (
            ("resetTranslationStyle", {}),
            ("removeStyleOverride", {"application": "editor"}),
            ("changeGlossary", {"rules": []}),
        ):
            with self.subTest(operation=operation):
                binding = await self.bind_automatic_practice()
                self.sequence += 1
                end_id = self.sequence
                await self.send(
                    {"id": end_id, "op": "endAutomaticPractice", "data": binding["runId"]}
                )
                retirement = await self.receive_until(
                    lambda item: item.get("op") == "retireAutomaticPractice"
                )
                state = await self.ask("getStyleSettings")
                before = settings_file.read_bytes()
                request = self.translation_request(state["settingsRevision"], **payload)
                self.sequence += 1
                save_id = self.sequence
                await self.send({"id": save_id, "op": operation, "data": request})
                await self.ask("settingsDraft")
                await self.send(
                    {
                        "event": "cancelTranslationSettings",
                        "data": {"session": request["session"], "request": request["request"]},
                    }
                )
                # Drains the real owner loop while the dedicated save awaits ACK.
                await self.ask("settingsDraft")
                await self.send({"reply": retirement["id"], "data": True})
                replies = {}
                while len(replies) < 2:
                    reply = await self.receive_until(lambda item: "reply" in item)
                    replies[reply["reply"]] = reply["data"]
                self.assertTrue(replies[end_id])
                self.assertFalse(replies[save_id]["saved"])
                self.assertIn(replies[save_id]["code"], ("style-canceled", "glossary-canceled"))
                self.assertEqual(settings_file.read_bytes(), before)

    async def test_wrong_translation_cancel_and_late_cancel_do_not_undo_confirmed_save(self):
        settings_file = await self.seed_translation_preferences()
        binding = await self.bind_automatic_practice()
        self.sequence += 1
        end_id = self.sequence
        await self.send({"id": end_id, "op": "endAutomaticPractice", "data": binding["runId"]})
        retirement = await self.receive_until(
            lambda item: item.get("op") == "retireAutomaticPractice"
        )
        state = await self.ask("getStyleSettings")
        request = self.translation_request(state["settingsRevision"])
        self.sequence += 1
        save_id = self.sequence
        await self.send({"id": save_id, "op": "resetTranslationStyle", "data": request})
        for cancellation in (
            {"session": str(uuid.uuid4()), "request": request["request"]},
            {"session": request["session"], "request": request["request"] + 1},
        ):
            await self.send({"event": "cancelTranslationSettings", "data": cancellation})
        await self.ask("settingsDraft")
        await self.send({"reply": retirement["id"], "data": True})
        replies = {}
        while len(replies) < 2:
            reply = await self.receive_until(lambda item: "reply" in item)
            replies[reply["reply"]] = reply["data"]
        self.assertTrue(replies[end_id])
        self.assertTrue(replies[save_id]["saved"])
        saved = settings_file.read_bytes()
        await self.send(
            {
                "event": "cancelTranslationSettings",
                "data": {"session": request["session"], "request": request["request"]},
            }
        )
        await self.ask("settingsDraft")
        self.assertEqual(settings_file.read_bytes(), saved)
        self.assertIsNone((await self.ask("getStyleSettings"))["savedPreferences"])

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

    async def test_common_shortcut_conflict_keeps_settings_until_exact_draft_is_confirmed(self):
        await self.ask("settings", {"theme": "dark"})
        before = (self.home / "config/settings.json").read_bytes()
        draft = self.draft()
        draft["shortcuts"]["wake"] = "Ctrl+C"
        response = await self.ask("saveSettingsDraft", draft)
        self.assertFalse(response["saved"])
        self.assertTrue(response["confirmationRequired"])
        self.assertIn("Ctrl+C", response["message"])
        self.assertEqual((self.home / "config/settings.json").read_bytes(), before)
        draft["confirmedShortcuts"] = dict(draft["shortcuts"])
        draft["shortcuts"]["wake"] = "Ctrl+V"
        changed = await self.ask("saveSettingsDraft", draft)
        self.assertFalse(changed["saved"])
        self.assertEqual((self.home / "config/settings.json").read_bytes(), before)
        draft["confirmedShortcuts"] = dict(draft["shortcuts"])
        self.assertTrue((await self.ask("saveSettingsDraft", draft))["saved"])
        self.assertEqual(self.latest("settings")["wakeShortcut"], "Ctrl+V")
        self.assertEqual(self.latest("settings")["theme"], "dark")
        # Confirmation does not override invalid, internally conflicting bindings.
        draft["shortcuts"]["direct"] = "Ctrl+V"
        draft["confirmedShortcuts"] = dict(draft["shortcuts"])
        self.assertFalse((await self.ask("saveSettingsDraft", draft))["saved"])

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

    async def test_theme_persistence_failure_is_confirmed_and_allows_explicit_retry(self):
        self.assertIs(await self.ask("settings", {"theme": "dark"}), True)
        saved = self.latest("settings")
        revision = self.latest("status")["commandFeedbackRevision"]
        settings_file = self.home / "config/settings.json"
        before = settings_file.read_bytes()
        requested = {**saved, "theme": "system"}
        blocker = settings_file.with_suffix(".json.tmp")
        blocker.mkdir()
        try:
            self.events.clear()
            self.assertIs(await self.ask("settings", requested), False)
            self.assertEqual(settings_file.read_bytes(), before)
            self.assertEqual(self.events, [], "failed persistence reconfigured the session")
        finally:
            blocker.rmdir()
        self.assertIs(await self.ask("settings", requested), True)
        self.assertEqual(self.latest("settings")["theme"], "system")
        self.assertEqual(json.loads(settings_file.read_text())["Theme"], "system")
        self.assertEqual(self.latest("status")["commandFeedbackRevision"], revision + 1)

    async def test_ordinary_settings_host_side_effect_failure_remains_unconfirmed(self):
        self.assertIs(await self.ask("settings", {"theme": "dark"}), True)
        saved = self.latest("settings")
        settings_file = self.home / "config/settings.json"
        before = settings_file.read_bytes()
        # A failure outside SettingsStore's atomic file write cannot be reported
        # as a confirmed persistence failure by the ordinary settings endpoint.
        (self.home / "autostart").write_text("not a directory")
        self.events.clear()
        self.assertIsNone(await self.ask("settings", {**saved, "startup": True}))
        self.assertEqual(settings_file.read_bytes(), before)
        self.assertEqual(self.events, [])

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

    async def test_tray_preserves_a_supported_same_language_preference(self):
        self.assertTrue(await self.ask("traySettings", {"source": "fr", "target": "fr"}))
        saved = self.latest("settings")
        self.assertEqual((saved["source"], saved["target"]), ("fr", "fr"))

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
            {"source": "xx", "target": "xx"},
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

    async def test_tray_preserves_a_known_same_language_choice_offline(self):
        self.assertTrue(await self.ask("traySettings", {"source": "fr", "target": "fr"}))
        self.assertEqual(self.latest("settings")["source"], "fr")
        self.assertEqual(self.latest("settings")["target"], "fr")
        self.assertFalse(self.latest("status")["modelReady"])
        saved = json.loads((self.home / "config/settings.json").read_text())
        self.assertEqual((saved["Source"], saved["Target"]), ("fr", "fr"))

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

    async def test_missing_python_preserves_committed_inventory_without_granting_ready(self):
        generation, root = self.model_generation(directions=["zh_en"])
        (root.parent / "current").symlink_to(generation)
        (root / "runtime/venv/bin/python").unlink()
        view = await self.ask("translationPreparation", {"phase": "Idle"})
        self.assertEqual(
            [item["id"] for item in view["directions"] if item["installed"]], ["zh_en"]
        )
        self.assertTrue(await self.ask("start"))
        self.assertFalse(self.latest("status")["modelReady"])
        self.assertFalse((root / "pid").exists())
        candidate, candidate_root = self.model_generation(previous=generation)
        (candidate_root / "runtime/venv/bin/python").unlink()
        self.assertEqual(
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": candidate}
            ),
            "unchanged",
        )
        self.assertFalse((candidate_root / "pid").exists())
        self.assertEqual(os.readlink(root.parent / "current"), generation)

    async def test_preparation_uses_real_installation_facts_and_current_phase(self):
        missing = await self.ask("translationPreparation", {"phase": "Idle"})
        self.assertIsInstance(missing, dict)
        self.assertEqual(missing["primaryAction"]["id"], "Prepare")
        self.assertEqual(missing["secondaryActions"][0]["id"], "NextStep")
        self.assertEqual(missing["secondaryActions"][0]["label"], "选择接入方式")

        generation, component = self.model_generation(hello_directions=[])
        (component.parent / "current").symlink_to(generation)
        # Restart observes one immutable installed generation, not marker files
        # written through a moving current path in an already-running client.
        await self.asyncTearDown()
        await self.start_host()
        installed = await self.ask("translationPreparation", {"phase": "Idle"})
        self.assertEqual(installed["primaryAction"]["id"], "NextStep")
        self.assertTrue(
            all(
                row["installed"] and not row["ready"]
                for row in installed["packageRows"]
                if row["id"].startswith("argos-")
            )
        )
        self.assertTrue(
            any(
                action["request"]["operation"] == "Repair"
                for row in installed["packageRows"]
                for action in row["actions"]
            )
        )
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

    async def test_first_desktop_observation_before_shortcuts_keeps_startup_available(self):
        # Drive the production input's first maintenance tick before the host's
        # shortcut configuration arrives. GLib and the unused native listening
        # socket are controlled ports; host framing/decoding are real, with no model.
        pending = []
        loop = SimpleNamespace(
            IO_IN=1,
            PRIORITY_DEFAULT=0,
            timeout_add=lambda *_: 1,
            io_add_watch=lambda *_: 1,
            idle_add=lambda *_, **__: 1,
            source_remove=lambda *_: None,
        )
        listener = SimpleNamespace(
            bind=lambda path: Path(path).touch(),
            fileno=lambda: -1,
            listen=lambda *_: None,
            setblocking=lambda *_: None,
            close=lambda: None,
        )
        self.enterContext(mock.patch.dict(os.environ, {"NIRI_SOCKET": ""}))
        self.enterContext(mock.patch("socket.socket", return_value=listener))
        source = Input(
            loop,
            SimpleNamespace(keyval_from_name=lambda name: {"F9": 0xFFC6, "F10": 0xFFC7}[name]),
            SimpleNamespace(native=self.home / "native.sock"),
            SimpleNamespace(
                send=lambda event, data: pending.append({"event": event, "data": data})
            ),
            None,
        )
        self.addCleanup(source.close)
        source.expire()
        self.assertEqual(len(pending), 1)
        await self.send(pending.pop())
        self.assertTrue(await self.ask("start"))
        self.assertFalse(self.latest("status")["modelReady"])
        self.assertEqual(self.latest("status")["connectionState"], "ListenerUnavailable")
        self.assertIsInstance(await self.ask("translationPreparation", {"phase": "Idle"}), dict)

        source.notification("shortcuts", self.latest("shortcuts"))
        self.assertEqual(len(pending), 1)
        await self.send(pending.pop())
        self.assertIsInstance(await self.ask("translationPreparation", {"phase": "Idle"}), dict)
        self.assertFalse((await self.ask("getGlossarySettings"))["isReleased"])
        self.assertIsNone(self.process.returncode)

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
        self.assertTrue((await self.ask("saveSettingsDraft", draft))["confirmationRequired"])
        draft["confirmedShortcuts"] = dict(draft["shortcuts"])
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
        draft = self.draft("F1", "F24")
        self.assertTrue((await self.ask("saveSettingsDraft", draft))["confirmationRequired"])
        draft["confirmedShortcuts"] = dict(draft["shortcuts"])
        self.assertTrue((await self.ask("saveSettingsDraft", draft))["saved"])
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

    async def test_control_source_acknowledgement_does_not_grant_editor_capability(self):
        # Protocol/capability evidence only. The real GtkEntry route is exercised
        # by desktop_e2e with its production provider, host and model.
        self.assertTrue(
            (await self.ask("saveSettingsDraft", self.draft("Ctrl+F7", "Ctrl+F8")))["saved"]
        )
        target = {"backend": "linux-gtk3", "value": "test-field", "lifetime": str(uuid.uuid4())}
        acknowledgement = {
            "target": target,
            "generation": self.latest("shortcuts")["generation"],
            "bindings": {"accept": "Ctrl+F7", "exit": "Ctrl+F8"},
            "sourceReady": True,
            "editorReady": False,
        }
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
                    "sessionShortcuts": acknowledgement,
                },
            }
        )

        def capture(operation, context):
            self.assertEqual(operation, "capture")
            self.assertEqual(context["sessionShortcuts"]["bindings"], acknowledgement["bindings"])
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
                    "inputEpoch": 1,
                    "focusEpoch": 1,
                    "observedAtMs": time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1_000_000,
                    "writeSupported": True,
                    "directWriteSupported": True,
                },
            }

        self.input_reply = capture
        self.assertTrue(await self.ask("start"))
        rendered = (await self.receive_until(lambda value: value.get("event") == "render"))["data"]
        self.assertFalse(rendered["viewContext"]["canEdit"])
        self.assertFalse(rendered["editable"])
        self.assertEqual(
            await self.ask("beginEdit", rendered["viewContext"]["token"]),
            {"allowed": False, "token": None},
        )
        self.assertEqual((await self.ask("sessionShortcutSettings"))["acceptKey"], "Ctrl+F7")

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


class OwnedProcessExitTests(unittest.IsolatedAsyncioTestCase):
    async def launch(self, script, *, proof=True, release=True, inherited=(), environment=None):
        arguments = [str(SUPERVISOR)]
        if proof:
            arguments.append("--require-exit-proof")
        process = await asyncio.create_subprocess_exec(
            *arguments,
            sys.executable,
            "-c",
            script,
            pass_fds=inherited,
            env=environment,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        self.addAsyncCleanup(self.stop, process)
        if proof and release:
            process.stdin.write(b"\x01")
            await process.stdin.drain()
        return process

    async def stop(self, process):
        if process.returncode is None:
            process.send_signal(signal.SIGCONT)
            process.terminate()
        try:
            await asyncio.wait_for(process.communicate(), 3)
        except TimeoutError:
            process.kill()
            await asyncio.wait_for(process.communicate(), 3)

    async def test_generation_use_lock_is_owned_before_fork_and_until_family_exit(self):
        with tempfile.TemporaryDirectory(prefix="typelay-use-") as directory:
            path = Path(directory) / "use.lock"
            fd = os.open(path, os.O_RDWR | os.O_CREAT, 0o600)
            with os.fdopen(fd, "r+") as lock:
                environment = {**os.environ, "TYPELAY_MODEL_USE_LOCK": str(path)}
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                rejected = await self.launch(
                    "print('must-not-start', flush=True)", environment=environment
                )
                output, _ = await asyncio.wait_for(rejected.communicate(), 3)
                self.assertEqual((rejected.returncode, output), (200, b""))
                fcntl.flock(lock, fcntl.LOCK_UN)
                active = await self.launch(
                    "import signal; print('started', flush=True); signal.pause()",
                    environment=environment,
                )
                self.assertEqual(await asyncio.wait_for(active.stdout.readline(), 3), b"started\n")
                with self.assertRaises(BlockingIOError):
                    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                active.terminate()
                await asyncio.wait_for(active.communicate(), 3)
                self.assertEqual(active.returncode, 200)
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)

    async def test_failed_worker_has_exit_proof_without_changing_ordinary_exit_status(self):
        for proof, expected in ((False, 42), (True, 200)):
            with self.subTest(proof=proof):
                process = await self.launch("raise SystemExit(42)", proof=proof)
                await asyncio.wait_for(process.communicate(), 3)
                self.assertEqual(process.returncode, expected)

    async def test_worker_waits_for_owner_before_starting(self):
        process = await self.launch("print('started', flush=True)", release=False)
        with self.assertRaises(TimeoutError):
            await asyncio.wait_for(process.stdout.readline(), 0.1)
        self.assertIsNone(process.returncode)
        process.stdin.write(b"\x01")
        await process.stdin.drain()
        self.assertEqual(await asyncio.wait_for(process.stdout.readline(), 3), b"started\n")
        await asyncio.wait_for(process.communicate(), 3)
        self.assertEqual(process.returncode, 200)

    async def test_exit_proof_reaps_descendant_ignoring_term_after_cancelled_wait(self):
        read, write = os.pipe()
        # Closing the lifetime writer also releases the descendant if setup or
        # an assertion fails, before the supervisor cleanup runs.
        with os.fdopen(read, "rb") as lifetime, os.fdopen(write, "wb"):
            process = await self.launch(
                f"""
import os, signal
read, write = os.pipe()
child = os.fork()
if child == 0:
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    os.write(write, b'!')
    os.read({lifetime.fileno()}, 1)
    os._exit(0)
os.read(read, 1)
print(os.getpid(), child, flush=True)
while True: signal.pause()
""",
                inherited=(lifetime.fileno(),),
            )
            root, child = map(int, (await asyncio.wait_for(process.stdout.readline(), 3)).split())
            process.send_signal(signal.SIGSTOP)
            process.terminate()
            with self.assertRaises(TimeoutError):
                await asyncio.wait_for(process.wait(), 0.1)
            self.assertTrue(Path(f"/proc/{child}").exists())
            process.send_signal(signal.SIGCONT)
            await asyncio.wait_for(process.communicate(), 3)
            self.assertEqual(process.returncode, 200)
            self.assertFalse(Path(f"/proc/{root}").exists())
            self.assertFalse(Path(f"/proc/{child}").exists())

    async def test_empty_group_cannot_prove_exit_while_detached_descendant_remains(self):
        read, write = os.pipe()
        with os.fdopen(read, "rb") as lifetime, os.fdopen(write, "wb") as release:
            process = await self.launch(
                f"""
import os, signal
read, write = os.pipe()
child = os.fork()
if child == 0:
    os.setsid()
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    os.write(write, b'!')
    os.read({lifetime.fileno()}, 1)
    os._exit(0)
os.read(read, 1)
print(child, flush=True)
raise SystemExit(42)
""",
                inherited=(lifetime.fileno(),),
            )
            child = int(await asyncio.wait_for(process.stdout.readline(), 3))
            with self.assertRaises(TimeoutError):
                await asyncio.wait_for(process.wait(), 0.9)
            self.assertTrue(Path(f"/proc/{child}").exists())
            release.close()
            await asyncio.wait_for(process.communicate(), 3)
            self.assertEqual(process.returncode, 200)
            self.assertFalse(Path(f"/proc/{child}").exists())


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
