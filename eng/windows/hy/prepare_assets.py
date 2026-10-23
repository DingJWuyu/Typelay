"""Prepare fixed Windows Hy assets, optionally compile the manual audit adapter.

The external entry is eng/windows/verify-hy-adapter.ps1 (prepare or prepare-product).
There is no inference, model package installation or llama.cpp compilation.
"""

import argparse
import ctypes
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile
from pathlib import Path

WINDOWS_ROOT = Path(__file__).resolve().parent
REPOSITORY = WINDOWS_ROOT.parents[2]
ROOT = REPOSITORY / "src/shared/TypeLay.Worker/native/hy"
sys.dont_write_bytecode = True
sys.path.insert(0, str(REPOSITORY))
sys.path.insert(0, str(REPOSITORY / "src/shared/TypeLay.Worker"))
sys.path.insert(0, str(REPOSITORY / "tests/windows/fixtures/TypeLay.NativeFixture/HyAdapter"))
from verify_assets import (  # noqa: E402
    CHUNK,
    LOCK,
    digest,
    plain_path,
    read_lock,
    verify_downloads,
    verify_file,
    verify_runtime,
    verify_sources,
)

from eng.windows.hy.toolchain import sdk_environment  # noqa: E402


class Preparation:
    def __init__(self, artifacts, budget):
        self.started = time.monotonic()
        self.deadline = self.started + budget
        self.artifacts = artifacts
        self.download = artifacts / "hy-download"
        self.runtime = artifacts / "hy-runtime"
        self.build = artifacts / "hy-build"
        self.results = artifacts / "hy-results"
        self.commands = []
        self.stage = "platform"
        self.tool_exit_code = None

    def remaining(self):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("600-second preparation deadline")
        return remaining

    def directory(self, path):
        if not path.exists():
            plain_path(path.parent, directory=True)
            path.mkdir()
        return plain_path(path, directory=True)

    def acquire(self, record):
        path = self.download / record["file"]
        if path.exists() or path.is_symlink():
            # An invalid cache is a failure. Do not silently replace and call it a hit.
            verify_file(path, record)
            self.remaining()
            return "verified-cache"
        partial = self.download / (record["file"] + ".partial")
        # Exclusive creation rejects any stale partial, link or replacement target.
        with partial.open("xb") as output:
            request = urllib.request.Request(
                record["url"], headers={"User-Agent": "TypeLay-Hy-fixture/1"}
            )
            with urllib.request.urlopen(request, timeout=min(30, self.remaining())) as response:
                if response.status != 200 or not response.url.startswith("https://"):
                    raise ValueError("fixed HTTPS asset download failed")
                value, size = hashlib.sha256(), 0
                for block in iter(lambda: response.read(CHUNK), b""):
                    self.remaining()
                    size += len(block)
                    if size > record["bytes"]:
                        raise ValueError("download exceeded fixed byte bound")
                    value.update(block)
                    output.write(block)
            if size != record["bytes"] or value.hexdigest() != record["sha256"]:
                raise ValueError("download full hash mismatch")
        # Windows rename fails if another target appeared; never overwrite a cache hit.
        partial.rename(path)
        verify_file(path, record)
        self.remaining()
        return "downloaded-full-hash-verified"

    def extract(self, lock):
        self.directory(self.runtime)
        expected = {item["file"] for item in lock["runtime_files"]}
        existing = {path.name for path in self.runtime.iterdir()}
        if existing:
            if existing != expected:
                raise ValueError("existing runtime is not the exact CPU closure")
            verify_runtime(self.runtime, lock)
            return
        # verify_downloads has already fully checked the ZIP. No untrusted name
        # becomes a destination: destinations come only from the reviewed lock.
        with zipfile.ZipFile(self.download / lock["runtime_archive"]["file"]) as archive:
            for record in lock["runtime_files"]:
                self.remaining()
                with archive.open(record["file"]) as source:
                    with (self.runtime / record["file"]).open("xb") as destination:
                        shutil.copyfileobj(source, destination, CHUNK)
                verify_file(self.runtime / record["file"], record)
        verify_runtime(self.runtime, lock)

    def command(self, command, *, env=None, shell_command=False):
        self.remaining()
        start = time.monotonic()
        # The outer PS deadline kills the still-live preparation process tree.
        # Do not kill only an intermediate cmd.exe and orphan its SDK child.
        # These are installed Microsoft SDK executables, never a downloaded EXE.
        result = subprocess.run(
            command,
            cwd=self.build,
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=False,
        )
        if len(result.stdout) > 65536:
            raise ValueError("SDK command diagnostic byte bound")
        if result.returncode:
            self.tool_exit_code = result.returncode
            raise RuntimeError("installed SDK command failed")
        if not shell_command:
            self.commands.append(
                {
                    "tool": str(command[0]),
                    "arguments": command[1:],
                    "elapsed_seconds": round(time.monotonic() - start, 6),
                    "exit_code": result.returncode,
                }
            )
        self.remaining()
        return result.stdout

    def run(self, *, assets_only=False):
        if not assets_only and (os.name != "nt" or ctypes.sizeof(ctypes.c_void_p) != 8):
            raise RuntimeError("preparation requires Windows x64")
        self.stage = "directories"
        for path in (self.artifacts, self.download, self.build, self.results):
            self.directory(path)
        # A prior local build may be reused, but no output may redirect writes.
        for path in self.build.iterdir():
            plain_path(path)
        for name in ("asset-verification.json", "build-verification.json"):
            path = self.results / name
            if path.exists() or path.is_symlink():
                plain_path(path)
        self.stage = "verify_sources"
        lock = read_lock()
        verify_sources(lock)
        self.stage = "validate_cache"
        expected = {lock[key]["file"] for key in ("model", "model_license", "runtime_archive")}
        if not {path.name for path in self.download.iterdir()} <= expected:
            raise ValueError("cache contains an unexpected file or partial download")
        sources = {}
        for key, stage in (
            ("model", "acquire_model"),
            ("model_license", "acquire_license"),
            ("runtime_archive", "acquire_runtime"),
        ):
            self.stage = stage
            sources[key] = self.acquire(lock[key])
        self.stage = "verify_full_hashes"
        members = verify_downloads(self.download, lock)
        self.remaining()
        self.stage = "extract_cpu_runtime"
        self.extract(lock)
        self.stage = "write_asset_identity"
        identity = {
            "asset_lock_sha256": digest(LOCK),
            "assets": {
                key: {**lock[key], "acquisition": sources[key]}
                for key in ("model", "model_license", "runtime_archive")
            },
            "template_sha256": lock["template_sha256"],
            "archive_members_verified": members,
            "runtime_files": lock["runtime_files"],
            "inference_executed": False,
        }
        (self.results / "asset-verification.json").write_text(
            json.dumps(identity, indent=2) + "\n", encoding="utf-8"
        )
        if assets_only:
            self.remaining()
            self.stage = "complete"
            print(json.dumps({"event": "assets_prepared", "inference_executed": False}))
            return
        self.stage = "resolve_sdk"
        env = sdk_environment(self.command)
        lib = shutil.which("lib.exe", path=env["PATH"])
        cl = shutil.which("cl.exe", path=env["PATH"])
        for name in ("llama", "ggml"):
            self.stage = "build_" + name + "_import"
            self.command(
                [
                    lib,
                    "/nologo",
                    "/machine:x64",
                    f"/def:{WINDOWS_ROOT / (name + '.def')}",
                    f"/out:{self.build / (name + '.lib')}",
                ],
                env=env,
            )
        self.stage = "compile_adapter"
        adapter = self.build / "typelay_hy.dll"
        self.command(
            [
                cl,
                "/nologo",
                "/LD",
                "/MD",
                "/std:c++17",
                "/EHsc",
                "/utf-8",
                "/O2",
                "/W4",
                "/WX",
                "/DHY_ENABLE_AUDIT",
                f"/I{ROOT / 'include'}",
                str(ROOT / "hy_adapter.cpp"),
                f"/Fo{self.build / 'hy_adapter.obj'}",
                "/link",
                f"/LIBPATH:{self.build}",
                "llama.lib",
                "ggml.lib",
                "/MACHINE:X64",
                "/INCREMENTAL:NO",
                "/DYNAMICBASE",
                "/NXCOMPAT",
                f"/OUT:{adapter}",
                f"/IMPLIB:{self.build / 'typelay_hy.lib'}",
            ],
            env=env,
        )
        plain_path(adapter)
        self.stage = "verify_exports"
        dumpbin = shutil.which("dumpbin.exe", path=env["PATH"])
        exports_text = self.command([dumpbin, "/nologo", "/exports", str(adapter)], env=env)
        exports = sorted(
            re.findall(
                rb"^\s*\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(\S+)\s*$",
                exports_text,
                flags=re.MULTILINE,
            )
        )
        if exports != [
            b"hy_audit",
            b"hy_close",
            b"hy_open_v5",
            b"hy_translate",
            b"hy_translate_english_v1",
            b"hy_translate_glossary_v5",
            b"hy_translate_styled_glossary_v5",
            b"hy_translate_styled_v5",
        ]:
            raise ValueError("built adapter must expose exactly eight fixture ABI5 exports")
        self.stage = "write_build_identity"
        report = {
            "abi": 5,
            "asset_lock_sha256": digest(LOCK),
            "source_files": lock["source_files"],
            "windows_source_files": lock["windows_source_files"],
            "adapter_exports": [name.decode("ascii") for name in exports],
            "adapter": {
                "file": adapter.name,
                "bytes": adapter.stat().st_size,
                "sha256": digest(adapter),
            },
            "commands": self.commands,
            "prepare_seconds": round(time.monotonic() - self.started, 6),
            "inference_executed": False,
        }
        self.remaining()
        text = json.dumps(report, indent=2) + "\n"
        (self.build / "build-verification.json").write_text(text, encoding="utf-8")
        (self.results / "build-verification.json").write_text(text, encoding="utf-8")
        self.stage = "complete"
        print(json.dumps({"event": "prepared", "abi": 5, "seconds": report["prepare_seconds"]}))


def main():
    preparation = None
    try:
        # Keep malformed command-line values out of argparse's default stderr.
        parser = argparse.ArgumentParser(allow_abbrev=False, add_help=False, exit_on_error=False)
        parser.add_argument("--artifacts", type=Path)
        parser.add_argument("--budget-seconds", type=float)
        parser.add_argument("--assets-only", action="store_true")
        args, extra = parser.parse_known_args()
        if (
            extra
            or args.artifacts is None
            or not args.artifacts.is_absolute()
            or args.budget_seconds is None
            or not 0 < args.budget_seconds <= 600
        ):
            raise ValueError("invalid fixed preparation arguments")
        preparation = Preparation(args.artifacts, args.budget_seconds)
        preparation.run(assets_only=args.assets_only)
        return 0
    except Exception as error:
        name = type(error).__name__
        known = {
            "ArgumentError",
            "ValueError",
            "RuntimeError",
            "OSError",
            "FileNotFoundError",
            "FileExistsError",
            "PermissionError",
            "TimeoutError",
            "URLError",
            "HTTPError",
            "SSLError",
            "BadZipFile",
            "UnicodeDecodeError",
            "KeyError",
        }
        win32 = getattr(error, "winerror", None)
        failure = {
            "event": "prepare_failure",
            "stage": preparation.stage if preparation is not None else "arguments",
            "exception_type": name if name in known else "UnexpectedError",
            "win32": win32 if type(win32) is int else None,
            "tool_exit_code": preparation.tool_exit_code if preparation is not None else None,
        }
        text = json.dumps(failure, separators=(",", ":")) + "\n"
        sys.stderr.write(text)
        if preparation is not None:
            try:
                # Failure reporting must neither redirect writes through a link
                # nor create directories after the failed operation.
                plain_path(preparation.results, directory=True)
                target = preparation.results / "prepare-failure.json"
                if target.exists() or target.is_symlink():
                    plain_path(target)
                target.write_text(text, encoding="utf-8")
            except Exception:
                pass  # The same bounded failure was already emitted to stderr.
        return 2


if __name__ == "__main__":
    sys.exit(main())
