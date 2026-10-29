"""Actual Index setup/host/model lifecycle, not GTK, writeback or quality acceptance."""

import argparse
import asyncio
import fcntl
import hashlib
import json
import os
import shutil
import signal
import struct
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
INDEX = "index-translate-2b-q4-k-m"
PROFILE = "index-v1-3b99612f3676f5ac8ef34c81e99bf8874a5ba5271207a15d39e947c9d42ef87d"


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


class Check:
    def __init__(self, package, cache, output):
        self.package, self.cache, self.output = package, cache, output
        self.data, self.config = output / "data", output / "config"
        self.downloads = output / "cache"
        self.children, self.sequence = [], 0
        self.host = None
        self.status = None
        self.stage = "release-identity"
        self.report = {
            "scope": "linux-index-production-package-lifecycle",
            "desktop": "not-run",
            "quality": "not-reviewed",
            "budgetSeconds": 600,
            "inferenceProfiles": {},
            "stages": {
                name: "not-run"
                for name in (
                    "release-identity",
                    "prepare-default",
                    "activate-index",
                    "ordinary-and-structure",
                    "cold-start",
                    "failed-repair-retains-current",
                    "cancelled-candidate-retains-current",
                    "repair",
                    "install-argos",
                    "select-index",
                    "select-argos",
                    "remove-index",
                )
            },
            "cleanup": [],
        }

    def begin(self, name):
        self.stage = name

    def passed(self):
        self.report["stages"][self.stage] = "passed"

    def current(self):
        path = self.data / "components/current"
        return os.readlink(path) if path.is_symlink() else None

    def inventory(self):
        return json.loads(
            (self.data / "components" / self.current() / "models/installed.json").read_text()
        )

    async def process(self, command, *, pipes=False):
        error = (self.output / f"process-{len(self.children)}.stderr").open("xb")
        try:
            process = await asyncio.create_subprocess_exec(
                *map(str, command),
                stdin=asyncio.subprocess.PIPE if pipes else asyncio.subprocess.DEVNULL,
                stdout=asyncio.subprocess.PIPE,
                stderr=error,
            )
        finally:
            error.close()
        self.children.append(process)
        return process

    async def prepare(self, request=None, *, expect_failure=False):
        command = [
            "/usr/bin/python3",
            "-I",
            "-B",
            self.package / "desktop/setup.py",
            self.package,
            self.data,
            self.downloads,
        ]
        if request is not None:
            command.append(json.dumps(request))
        process = await self.process(command)
        output, _ = await process.communicate()
        (self.output / f"prepare-{len(self.children)}.txt").write_bytes(output)
        if expect_failure:
            assert process.returncode != 0, "stale-prepare-not-refused"
            return None
        assert process.returncode == 0, "production-prepare-failed"
        markers = [
            line.split("=", 1)[1]
            for line in output.decode().splitlines()
            if line.startswith("TYPELAY_MODEL_PREPARED=")
        ]
        assert len(markers) == 1 and len(markers[0]) == 32, "missing-prepared-generation"
        return markers[0]

    def request(self, operation, packages, active, *, current=None):
        return {
            "format": 1,
            "operation": operation,
            "packageIds": packages,
            "activePackages": active,
            "expectedCurrent": self.current() if current is None else current,
        }

    async def send(self, value):
        content = json.dumps(value).encode()
        self.host.stdin.write(struct.pack("<I", len(content)) + content)
        await self.host.stdin.drain()

    async def receive(self, predicate):
        while True:
            count = struct.unpack("<I", await self.host.stdout.readexactly(4))[0]
            assert 0 < count <= 262144, "host-frame-size"
            value = json.loads(await self.host.stdout.readexactly(count))
            if value.get("event") == "status":
                self.status = value["data"]
            if predicate(value):
                return value
            if "op" in value:
                await self.send({"reply": value["id"], "data": None})

    async def ask(self, operation, data=None):
        self.sequence += 1
        identity = self.sequence
        await self.send({"id": identity, "op": operation, "data": data})
        async with asyncio.timeout(60):
            return (await self.receive(lambda value: value.get("reply") == identity))["data"]

    async def start(self):
        self.status = None
        self.host = await self.process(
            [
                self.package / "native/typelay-supervise",
                "--require-exit-proof",
                self.package / "app/TypeLay.Linux",
                self.package,
                self.config,
                self.data,
            ],
            pipes=True,
        )
        self.host.stdin.write(b"\x01")
        await self.host.stdin.drain()
        assert await self.ask("start") is True

    async def ready(self, active):
        async with asyncio.timeout(60):
            if not self.status or not self.status.get("modelReady"):
                await self.receive(
                    lambda value: value.get("event") == "status" and value["data"].get("modelReady")
                )
        assert (
            self.status["modelReady"] and [p["id"] for p in self.status["modelPackages"]] == active
        )
        if active == [INDEX]:
            assert len(self.status["modelDirections"]) == 14
            assert self.status.get("modelInferenceProfileId") == PROFILE, (
                "index-profile-unqualified"
            )
            self.report["inferenceProfiles"][self.stage] = self.status["modelInferenceProfileId"]

    async def activate(self, generation, active):
        assert (
            await self.ask(
                "activateModel", {"identity": uuid.uuid4().hex, "generation": generation}
            )
            == "confirmed"
        )
        assert self.current() == generation
        await self.ready(active)

    async def stop(self):
        await self.send({"event": "quit", "data": None})
        self.host.stdin.close()
        await asyncio.wait_for(self.host.wait(), 7)
        assert self.host.returncode == 200, "host-family-exit-unconfirmed"
        self.host = None

    async def translate(self, text, source, target):
        assert await self.ask("settings", {"source": source, "target": target}) is True
        request = {
            "id": 1,
            "operation": "translateEditorSnapshot",
            "data": {
                "documentId": str(uuid.uuid4()),
                "version": 1,
                "selection": {"start": 0, "length": len(text.encode("utf-16-le")) // 2},
                "text": text,
                "languageId": "plaintext",
            },
        }
        response = await self.ask(
            "translateEditorSnapshot", {"identity": uuid.uuid4().hex, "request": request}
        )
        assert (
            "data" in response and response["data"]["documentId"] == request["data"]["documentId"]
        ), "index-inference-refused"
        result = response["data"]["translation"]
        assert result.strip()
        if "\n" in text:
            assert (
                result.count("\r\n") == 1
                and result.count("\r") == result.count("\n") == 1
                and result.count("https://example.com") == 1
                and result.startswith("  ")
                and result.endswith("  ")
                and len(result) - len(result.lstrip(" \t")) == 2
                and len(result) - len(result.rstrip(" \t")) == 2
            ), "structure-mismatch"

    async def lifecycle(self):
        worker = self.package / "worker"
        self.report["payloadHashes"] = {
            str(path.relative_to(self.package)): digest(path)
            for folder in (worker, self.package / "app", self.package / "native")
            for path in folder.rglob("*")
            if path.is_file()
        }
        lock = json.loads((worker / "native/index/model.lock.json").read_text())
        target = self.downloads / "components" / INDEX / lock["version"]
        target.mkdir(parents=True)
        for kind in ("model", "model_license"):
            record = lock[kind]
            source = self.cache / record["file"]
            assert source.stat().st_size == record["bytes"] and digest(source) == record["sha256"]
            shutil.copyfile(source, target / record["file"])
        self.passed()
        self.begin("prepare-default")
        generation = await self.prepare()
        assert self.current() is None, "preparation-published-current"
        self.passed()
        self.begin("activate-index")
        await self.start()
        await self.activate(generation, [INDEX])
        self.passed()
        self.begin("ordinary-and-structure")
        for text, source, target in (
            ("我没有同意删除备份。", "zh", "en"),
            ("Please keep the backup until Friday.", "en", "zh"),
            ("  Please keep https://example.com\r\nThank you.  ", "en", "zh"),
        ):
            await self.translate(text, source, target)
        self.passed()
        self.begin("cold-start")
        await self.stop()
        await self.start()
        await self.ready([INDEX])
        assert self.current() == generation
        self.passed()
        self.begin("failed-repair-retains-current")
        await self.prepare(
            self.request("Repair", [INDEX], [INDEX], current="0" * 32), expect_failure=True
        )
        assert self.current() == generation
        await self.ready([INDEX])
        self.passed()
        self.begin("cancelled-candidate-retains-current")
        abandoned = await self.prepare(self.request("Repair", [INDEX], [INDEX]))
        self.sequence += 1
        reply, identity = self.sequence, uuid.uuid4().hex
        await self.send(
            {
                "id": reply,
                "op": "activateModel",
                "data": {"identity": identity, "generation": abandoned},
            }
        )
        await self.send({"event": "cancelModelActivation", "data": identity})
        async with asyncio.timeout(60):
            response = await self.receive(lambda value: value.get("reply") == reply)
        assert response["data"] == "unchanged" and self.current() == generation
        await self.ready([INDEX])
        self.passed()
        for name, operation, packages, active in (
            ("repair", "Repair", [INDEX], [INDEX]),
            ("install-argos", "Install", ["argos-zh-en"], ["argos-zh-en"]),
            ("select-index", "Select", [], [INDEX]),
            ("select-argos", "Select", [], ["argos-zh-en"]),
            ("remove-index", "Remove", [INDEX], ["argos-zh-en"]),
        ):
            self.begin(name)
            await self.activate(
                await self.prepare(self.request(operation, packages, active)), active
            )
            if name == "install-argos":
                assert any(p["id"] == INDEX for p in self.inventory()["packages"])
            if name == "remove-index":
                assert all(p["id"] != INDEX for p in self.inventory()["packages"])
            self.passed()
        await self.stop()

    async def run(self):
        started = time.monotonic()
        try:
            async with asyncio.timeout(600):
                await self.lifecycle()
        except Exception as error:
            self.report["stages"][self.stage] = "failed"
            self.report["failure"] = type(error).__name__ + ": " + str(error)[:200]
        finally:
            # One independent cleanup budget, never a fresh budget for each child.
            try:
                async with asyncio.timeout(7):
                    for process in self.children:
                        if process.returncode is None:
                            process.send_signal(signal.SIGTERM)
                    for process in self.children:
                        await process.wait()
            except (TimeoutError, ProcessLookupError):
                self.report["cleanup"].append("owned-exit-unconfirmed")
            self.report["allOwnedSupervisorsExited"] = all(
                p.returncode is not None for p in self.children
            )
            # The worker use-lock is held by its supervisor through complete reaping.
            for lock in (self.data / "components").glob("*/use.lock"):
                with lock.open("rb") as stream:
                    try:
                        fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    except BlockingIOError:
                        self.report["cleanup"].append("generation-worker-still-owned")
            self.report["elapsedSeconds"] = time.monotonic() - started
            (self.output / "result.json").write_text(json.dumps(self.report, indent=2) + "\n")
        return (
            0
            if (
                all(v == "passed" for v in self.report["stages"].values())
                and not self.report["cleanup"]
                and self.report["allOwnedSupervisorsExited"]
            )
            else 1
        )


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    for name in ("package", "cache", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    package, cache, output = (
        getattr(args, name).resolve() for name in ("package", "cache", "output")
    )
    if output.exists() or not output.is_relative_to(ROOT / "artifacts"):
        parser.error("output must be a new checkout artifacts directory")
    output.mkdir(parents=True)
    return asyncio.run(Check(package, cache, output).run())


if __name__ == "__main__":
    raise SystemExit(main())
