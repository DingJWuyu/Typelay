"""Build provenance for the fixed Index adapter, not a quality authorization.

The reviewed host profile is generated explicitly. Normal builds only record
their real inputs; a changed input cannot add a host-supported quality profile.
"""

import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKER = ROOT / "src/shared/TypeLay.Worker"
NATIVE = WORKER / "native/index"
COMPILE_OPTIONS = {
    "linux-x86_64": [
        "-std=c++17",
        "-O2",
        "-fPIC",
        "-fvisibility=hidden",
        "-Wall",
        "-Wextra",
        "-Werror",
        "-shared",
    ],
    "win-x64": [
        "/nologo",
        "/LD",
        "/MD",
        "/std:c++17",
        "/EHsc",
        "/utf-8",
        "/O2",
        "/W4",
        "/WX",
    ],
}


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


LINK_INPUTS = (
    "eng/linux/index/exports.map",
    "eng/windows/index/llama.def",
    "eng/windows/index/ggml.def",
)


def native_sources(root=NATIVE, repository=ROOT):
    result = {
        name: sha(root / name)
        for name in (
            "index_adapter.cpp",
            "index_adapter.h",
            "chat_template.jinja",
            "headers.lock.json",
        )
    }
    for item in json.loads((root / "headers.lock.json").read_text(encoding="utf-8"))["files"]:
        name = item["file"]
        if Path(name).name != name:
            raise ValueError("invalid Index header path")
        result["include/" + name] = sha(root / "include" / name)
    for name in LINK_INPUTS:
        result[name] = sha(repository / name)
    return result


def confirm_native_sources(expected, root=NATIVE, repository=ROOT):
    if native_sources(root, repository) != expected:
        raise ValueError("Index build inputs changed during compilation")


def cpu_material(path):
    value = json.loads(Path(path).read_text(encoding="utf-8"))
    # No meaningful lock constraint is discarded. Archive provenance is fixed
    # too; sorting records removes only ordering, never a hash/size/kind field.
    return {**value, "files": sorted(value["files"], key=lambda item: item["file"])}


def profile():
    files = [WORKER / "run_worker.py", WORKER / "model-catalog.json", WORKER / "requirements.txt"]
    files += sorted((WORKER / "typelay_worker").glob("*.py"))
    material = {
        "schema": 1,
        "workerFiles": {p.relative_to(WORKER).as_posix(): sha(p) for p in files},
        "nativeSources": native_sources(),
        "modelLockSha256": sha(NATIVE / "model.lock.json"),
        "compileOptions": COMPILE_OPTIONS,
        "cpuLocks": {
            "linux-x86_64": cpu_material(NATIVE / "cpu-runtime.lock.json"),
            "win-x64": cpu_material(NATIVE / "cpu-runtime.win-x64.lock.json"),
        },
        "dependencies": {"python": "3.13.16", "lingua-language-detector": "2.1.1"},
    }
    identity = hashlib.sha256(
        json.dumps(material, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()
    return {"id": "index-v1-" + identity, **material}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.write_text(json.dumps(profile(), indent=2) + "\n", encoding="utf-8")
