"""Compile the shared production adapter against an existing fixed CPU runtime.

No download, archive extraction, model access, inference or llama.cpp build.
The output is the complete runtime-only worker/native/hy payload.
"""

import argparse
import json
import platform
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "src/shared/TypeLay.Worker"))
from typelay_worker.hy_assets import (  # noqa: E402
    ABI,
    EXTENDED_EXPORTS,
    NATIVE_ROOT,
    UPSTREAM,
    digest,
    plain_path,
    read_json,
    safe_name,
    verify_cpu_runtime,
    verify_file,
)


def build(runtime, output):
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        raise RuntimeError("adapter build requires Linux x86_64")
    verified = verify_cpu_runtime(runtime)
    output = Path(output).absolute()
    if output.exists():
        plain_path(output, directory=True)
        if any(output.iterdir()):
            raise ValueError("adapter output directory must be empty")
    else:
        output.mkdir(parents=True)
        plain_path(output, directory=True)
    # These source pins are build inputs only and never reach production startup.
    source_lock = read_json(NATIVE_ROOT / "assets.lock.json")
    for record in source_lock["source_files"]:
        name = Path(record["file"])
        if name.is_absolute() or ".." in name.parts:
            raise ValueError("adapter source lock path escape")
        verify_file(NATIVE_ROOT / name, record)
    headers = read_json(NATIVE_ROOT / "headers.lock.json")
    if headers["commit"] != UPSTREAM or len(headers["files"]) != 7:
        raise ValueError("adapter header/runtime revision mismatch")
    for record in headers["files"]:
        verify_file(NATIVE_ROOT / "include" / safe_name(record["file"]), record)
    adapter = output / "libtypelay_hy.so"
    subprocess.run(
        [
            "g++",
            "-std=c++17",
            "-O2",
            "-fPIC",
            "-fvisibility=hidden",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-shared",
            "-I" + str(NATIVE_ROOT / "include"),
            str(NATIVE_ROOT / "hy_adapter.cpp"),
            "-L" + verified["directory"],
            "-Wl,-rpath,$ORIGIN/cpu-runtime",
            "-Wl,-z,defs",
            "-Wl,--version-script," + str(Path(__file__).with_name("exports.map")),
            "-l:libllama.so.0",
            "-l:libggml.so.0",
            "-l:libggml-base.so.0",
            "-o",
            str(adapter),
        ],
        check=True,
        timeout=120,
    )
    symbols = subprocess.check_output(
        ["nm", "-D", "--defined-only", str(adapter)], text=True, timeout=30
    )
    exports = sorted(line.split()[-1] for line in symbols.splitlines() if line.strip())
    if exports != EXTENDED_EXPORTS:
        raise ValueError("production adapter must export exactly the extended ABI5 exports")
    dynamic = subprocess.check_output(["readelf", "--dynamic", str(adapter)], text=True, timeout=30)
    runpaths = re.findall(r"\(RUNPATH\).*\[([^]]*)\]", dynamic)
    if runpaths != ["$ORIGIN/cpu-runtime"] or "(RPATH)" in dynamic:
        raise ValueError("production adapter private runtime search path mismatch")
    cpu = output / "cpu-runtime"
    cpu.mkdir()
    for record in verified["files"]:
        shutil.copyfile(Path(verified["directory"]) / record["file"], cpu / record["file"])
    verify_cpu_runtime(cpu)
    for name in ("model.lock.json", "cpu-runtime.lock.json"):
        shutil.copyfile(NATIVE_ROOT / name, output / name)
    shutil.copyfile(NATIVE_ROOT / "include/LICENSE.llama.cpp", output / "LICENSE.llama.cpp")

    def record(path):
        return {"file": path.name, "bytes": path.stat().st_size, "sha256": digest(path)}

    manifest = {
        "schema": 1,
        "platform": "linux-x86_64",
        "abi": ABI,
        "exports": exports,
        "upstream_revision": UPSTREAM,
        "model_lock_sha256": digest(output / "model.lock.json"),
        "cpu_runtime_lock_sha256": digest(output / "cpu-runtime.lock.json"),
        "adapter": record(adapter),
        "license": record(output / "LICENSE.llama.cpp"),
    }
    (output / "production-adapter.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
    )
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--runtime", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    build(args.runtime, args.output)
