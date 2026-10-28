"""Verify pinned Index build inputs and generate the private template header.

No model, download, inference or compilation. The header is build-only and
must never be shipped in worker/native/index.
"""

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "src/shared/TypeLay.Worker"))
from typelay_worker.index_assets import (  # noqa: E402
    NATIVE_ROOT,
    TEMPLATE_SHA256,
    UPSTREAM,
    digest,
    plain_path,
    read_json,
    read_model_lock,
    safe_name,
    verify_file,
)


def verify_build_inputs(output):
    output = plain_path(output, directory=True)
    lock = read_json(NATIVE_ROOT / "headers.lock.json")
    if lock["commit"] != UPSTREAM or len(lock["files"]) != 7:
        raise ValueError("Index header/runtime revision mismatch")
    for item in lock["files"]:
        verify_file(NATIVE_ROOT / "include" / safe_name(item["file"]), item)
    verify_file(NATIVE_ROOT / "include/LICENSE.llama.cpp", lock["license"])
    model = read_model_lock()
    verify_file(NATIVE_ROOT / "include/LICENSE.Index-Translate", model["model_license"])
    template = plain_path(NATIVE_ROOT / "chat_template.jinja")
    if template.stat().st_size != 7755 or digest(template) != TEMPLATE_SHA256:
        raise ValueError("fixed Index template mismatch")
    # The pinned Jinja source is ASCII. A byte initializer preserves its exact
    # contents under both MSVC and GCC, independent of newline conversion.
    data = template.read_bytes()
    text = "constexpr const char TEMPLATE[] = {\n" + ",".join(str(b) for b in data) + ",0};\n"
    target = output / "index_template.h"
    with target.open("x", encoding="ascii") as stream:
        stream.write(text)
    return {"template_sha256": TEMPLATE_SHA256, "upstream_revision": UPSTREAM}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(verify_build_inputs(args.output)))
