"""Build the Windows production ABI1 adapter and runtime-only, no-model payload.

Requires an already verified CPU runtime and an installed x64 MSVC toolchain.
Never downloads assets, opens a GGUF, enables audit, or compiles llama.cpp.
"""

import argparse
import ctypes
import json
import platform
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "src/shared/TypeLay.Worker"))
from typelay_worker.index_assets import (  # noqa: E402
    ABI,
    CAPABILITIES,
    EXPORTS,
    NATIVE_ROOT,
    TEMPLATE_SHA256,
    UPSTREAM,
    WINDOWS_CPU_LOCK,
    digest,
    plain_path,
    verify_cpu_runtime,
)

from eng.index_profile import COMPILE_OPTIONS, confirm_native_sources, native_sources  # noqa: E402
from eng.linux.index.prepare_assets import verify_build_inputs  # noqa: E402
from eng.windows.index.toolchain import sdk_environment  # noqa: E402


def production_exports(raw):
    exports = []
    for line in raw.splitlines():
        fields = line.split()
        if (
            len(fields) < 3
            or not fields[0].isdigit()
            or not re.fullmatch(rb"[0-9A-Fa-f]+", fields[1])
        ):
            continue
        # Forwarded exports have no RVA, or append "= target" after the name.
        index = 3 if re.fullmatch(rb"[0-9A-Fa-f]{8,16}", fields[2]) else 2
        if len(fields) <= index:
            raise ValueError("malformed adapter export listing")
        exports.append(fields[index].decode("ascii", "strict"))
    exports.sort()
    if exports != EXPORTS:
        raise ValueError("production adapter must expose exactly the extended Index ABI1 exports")
    return exports


def build(runtime, output):
    if (
        platform.system() != "Windows"
        or platform.machine().lower() not in ("amd64", "x86_64")
        or ctypes.sizeof(ctypes.c_void_p) != 8
    ):
        raise RuntimeError("production adapter build requires Windows x64")
    verified = verify_cpu_runtime(runtime, "win-x64")
    output = Path(output).absolute()
    if output.exists() or output.is_symlink():
        raise ValueError("production payload output already exists")
    output.parent.mkdir(parents=True, exist_ok=True)
    plain_path(output.parent, directory=True)
    with tempfile.TemporaryDirectory(prefix=".index-build-", dir=output.parent) as temporary:
        work = Path(temporary)
        sources = native_sources()
        verify_build_inputs(work)
        payload = work / "payload"
        payload.mkdir()

        def command(arguments, *, env=None, shell_command=False):
            # shell_command is a raw CreateProcess command line for cmd.exe only.
            if shell_command != isinstance(arguments, str):
                raise ValueError("unexpected compiler command form")
            return subprocess.run(
                arguments, cwd=work, env=env, check=True, capture_output=True, timeout=120
            ).stdout

        env = sdk_environment(command)
        # These MSVC variables otherwise inject unrecorded compile options.
        env.pop("CL", None)
        env.pop("_CL_", None)
        lib = shutil.which("lib.exe", path=env["PATH"])
        for name in ("llama", "ggml"):
            command(
                [
                    lib,
                    "/nologo",
                    "/machine:x64",
                    f"/def:{Path(__file__).parent / (name + '.def')}",
                    f"/out:{work / (name + '.lib')}",
                ],
                env=env,
            )
        adapter = payload / "typelay_index.dll"
        command(
            [
                shutil.which("cl.exe", path=env["PATH"]),
                *COMPILE_OPTIONS["win-x64"],
                f"/I{NATIVE_ROOT / 'include'}",
                f"/I{work}",
                str(NATIVE_ROOT / "index_adapter.cpp"),
                f"/Fo{work / 'index_adapter.obj'}",
                "/link",
                f"/LIBPATH:{work}",
                "llama.lib",
                "ggml.lib",
                "/MACHINE:X64",
                "/INCREMENTAL:NO",
                "/DYNAMICBASE",
                "/NXCOMPAT",
                f"/OUT:{adapter}",
                f"/IMPLIB:{work / 'typelay_index.lib'}",
            ],
            env=env,
        )
        confirm_native_sources(sources)
        plain_path(adapter)
        exports = production_exports(
            command(
                [
                    shutil.which("dumpbin.exe", path=env["PATH"]),
                    "/nologo",
                    "/exports",
                    str(adapter),
                ],
                env=env,
            )
        )
        cpu = payload / "cpu-runtime"
        cpu.mkdir()
        for record in verified["files"]:
            shutil.copyfile(Path(verified["directory"]) / record["file"], cpu / record["file"])
        verify_cpu_runtime(cpu, "win-x64")
        for name in ("model.lock.json", WINDOWS_CPU_LOCK):
            shutil.copyfile(NATIVE_ROOT / name, payload / name)
        shutil.copyfile(NATIVE_ROOT / "include/LICENSE.llama.cpp", payload / "LICENSE.llama.cpp")
        shutil.copyfile(
            NATIVE_ROOT / "include/LICENSE.Index-Translate", payload / "LICENSE.Index-Translate"
        )

        def record(path):
            return {"file": path.name, "bytes": path.stat().st_size, "sha256": digest(path)}

        manifest = {
            "schema": 1,
            "platform": "win-x64",
            "abi": ABI,
            "native_sources": sources,
            "compile_options": COMPILE_OPTIONS["win-x64"],
            "capabilities": CAPABILITIES,
            "exports": exports,
            "upstream_revision": UPSTREAM,
            "template_sha256": TEMPLATE_SHA256,
            "model_lock_sha256": digest(payload / "model.lock.json"),
            "cpu_runtime_lock_sha256": digest(payload / WINDOWS_CPU_LOCK),
            "adapter": record(adapter),
            "license": record(payload / "LICENSE.llama.cpp"),
            "template_license": record(payload / "LICENSE.Index-Translate"),
        }
        (payload / "production-adapter.json").write_text(
            json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
        )
        payload.rename(output)
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--runtime", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    build(args.runtime, args.output)
