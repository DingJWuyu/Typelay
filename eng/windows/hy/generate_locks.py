"""Derive the two Windows runtime/build locks from the reviewed upstream asset lock.

No network access, archive extraction or native/model execution. The source
assets.lock.json remains the sole upstream file-identity record.
"""

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "src/shared/TypeLay.Worker"))
from typelay_worker.hy_assets import (  # noqa: E402
    NATIVE_ROOT,
    UPSTREAM,
    VARIANTS,
    WINDOWS_ARCHIVE_SHA256,
    WINDOWS_CPU_LOCK,
    WINDOWS_DEPENDENCIES,
    read_json,
)


def generate(*, check=False):
    source = read_json(NATIVE_ROOT / "assets.lock.json")
    archive = source["runtime_archive"]
    if (
        source["schema"] != 1
        or archive["file"] != "llama-b11717-bin-win-cpu-x64.zip"
        or archive["bytes"] != 19662390
        or archive["sha256"] != WINDOWS_ARCHIVE_SHA256
        or archive["tag"] != "b11717"
        or archive["commit"] != UPSTREAM
        or archive["url"]
        != "https://github.com/ggml-org/llama.cpp/releases/download/b11717/llama-b11717-bin-win-cpu-x64.zip"
    ):
        raise ValueError("fixed official Windows CPU archive identity mismatch")
    records = source["runtime_files"]
    members = {r["file"]: r for r in source["archive_members"]}
    expected = {f"ggml-cpu-{name}.dll": "cpu_variant" for name in VARIANTS}
    expected.update(dict.fromkeys(WINDOWS_DEPENDENCIES, "dependency"))
    expected["LICENSE-LLVM-OpenMP"] = "license"
    if len(records) != len(expected) or {r["file"]: r["kind"] for r in records} != expected:
        raise ValueError("fixed Windows CPU closure mismatch")
    for record in records:
        if any(record[key] != members[record["file"]][key] for key in ("bytes", "sha256")):
            raise ValueError("CPU member is not tied to the reviewed archive")
    outputs = {
        NATIVE_ROOT / WINDOWS_CPU_LOCK: {
            "schema": 1,
            "platform": "win-x64",
            "upstream_tag": archive["tag"],
            "upstream_revision": archive["commit"],
            "runtime_archive_sha256": archive["sha256"],
            "files": records,
        },
        Path(__file__).with_name("runtime-archive.lock.json"): {"schema": 1, **archive},
    }
    for path, value in outputs.items():
        text = json.dumps(value, indent=2) + "\n"
        if check:
            if path.read_text(encoding="utf-8") != text:
                raise ValueError("regenerate Windows Hy runtime locks: " + path.name)
        else:
            path.write_text(text, encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--check", action="store_true")
    generate(check=parser.parse_args().check)
