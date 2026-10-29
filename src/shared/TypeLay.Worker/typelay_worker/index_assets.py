"""Read-only identity gate for the separately packaged Index b11913 runtime.

Installed model data cannot supply a replacement lock or executable. This
module never downloads, extracts archives, loads a library, or runs inference.
"""

import hashlib
import json
import os
import platform
import re
import stat
from pathlib import Path

NATIVE_ROOT = Path(__file__).resolve().parents[1] / "native" / "index"
CHUNK = 1024 * 1024
ABI = 1
PACKAGE_ID = "index-translate-2b-q4-k-m"
BACKEND = "index-translate-llama-cpu"
VERSION = "3a0eca6ce931d88d00614d427ab3ed8ff0743902"
UPSTREAM = "7aca0d2ea8d92b56911233b822db01f9fbb1ae50"
TEMPLATE_SHA256 = "273d8e0e683b885071fb17e08d71e5f2a5ddfb5309756181681de4f5a1822d80"
MODEL_SHA256 = "044b313d29342bd3b2c77cbb64023ca0d209bd9b3247763f9d162767ef2d746a"
LEGACY_EXPORTS = ["index_close", "index_open_v1", "index_translate_v1"]
EXPORTS = ["index_close", "index_open_v1", "index_translate_alternative_v1", "index_translate_v1"]
DIRECTIONS = (
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
    "zh_en",
    "en_zh",
)
LEGACY_CAPABILITIES = {"styles": ["natural", "formal"], "glossary": True, "styled_glossary": True}
CAPABILITIES = {**LEGACY_CAPABILITIES, "alternative": True}
VARIANTS = (
    "alderlake",
    "cannonlake",
    "cascadelake",
    "cooperlake",
    "haswell",
    "icelake",
    "ivybridge",
    "piledriver",
    "sandybridge",
    "sapphirerapids",
    "skylakex",
    "sse42",
    "x64",
    "zen4",
)
DEPENDENCIES = ("libggml-base.so.0", "libggml.so.0", "libllama.so.0")
WINDOWS_DEPENDENCIES = ("libomp.dll", "ggml-base.dll", "ggml.dll", "llama.dll")
WINDOWS_CPU_LOCK = "cpu-runtime.win-x64.lock.json"


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(CHUNK), b""):
            value.update(block)
    return value.hexdigest()


def plain_path(path, *, directory=False, allow_hardlink=False):
    """Reject links/reparse points in the leaf and every existing ancestor."""
    path = Path(os.path.abspath(path))
    for current in (path, *path.parents):
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("linked or reparse path prohibited")
    info = path.stat()
    if directory:
        if not stat.S_ISDIR(info.st_mode):
            raise ValueError("directory required")
    elif not stat.S_ISREG(info.st_mode) or (info.st_nlink != 1 and not allow_hardlink):
        raise ValueError("single-link regular file required")
    return path


def safe_name(name):
    if (
        not isinstance(name, str)
        or not name
        or name in (".", "..")
        or name[-1:] in (" ", ".")
        or any(char in name for char in '/\\:\x00<>"|?*')
        or any(ord(char) < 32 for char in name)
    ):
        raise ValueError("unsafe flat asset name")
    reserved = {"CON", "PRN", "AUX", "NUL"} | {
        prefix + str(number) for prefix in ("COM", "LPT") for number in range(1, 10)
    }
    if name.split(".")[0].upper() in reserved:
        raise ValueError("Windows device asset name prohibited")
    return name


def verify_file(path, record):
    path = plain_path(path)
    if path.stat().st_size != record["bytes"] or digest(path) != record["sha256"]:
        raise ValueError("fixed asset full hash mismatch: " + path.name)
    return path


def read_json(path):
    path = plain_path(path)
    if path.stat().st_size > 128 * 1024:
        raise ValueError("asset manifest exceeds size bound")

    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate asset manifest key")
            result[key] = value
        return result

    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def read_cpu_lock(platform_name="linux-x86_64"):
    windows = platform_name == "win-x64"
    if platform_name not in ("win-x64", "linux-x86_64"):
        raise ValueError("unsupported Index CPU runtime platform")
    lock = read_json(NATIVE_ROOT / (WINDOWS_CPU_LOCK if windows else "cpu-runtime.lock.json"))
    if (
        type(lock["schema"]) is not int
        or lock["schema"] != 1
        or lock["platform"] != platform_name
        or lock["upstream_revision"] != UPSTREAM
        or lock["upstream_tag"] != "b11913"
    ):
        raise ValueError("unsupported fixed Index CPU runtime lock")
    records = lock["files"]
    names = [safe_name(record["file"]) for record in records]
    expected = {
        (f"ggml-cpu-{name}.dll" if windows else f"libggml-cpu-{name}.so") for name in VARIANTS
    }
    dependencies = set(WINDOWS_DEPENDENCIES if windows else DEPENDENCIES)
    licenses = {"LICENSE-LLVM-OpenMP"} if windows else set()
    if (
        len(names) != len(set(name.casefold() if windows else name for name in names))
        or {r["file"] for r in records if r["kind"] == "cpu_variant"} != expected
        or {r["file"] for r in records if r["kind"] == "dependency"} != dependencies
        or {r["file"] for r in records if r["kind"] == "license"} != licenses
        or set(names) != expected | dependencies | licenses
        or any(
            type(r["bytes"]) is not int
            or r["bytes"] <= 0
            or not isinstance(r["sha256"], str)
            or not re.fullmatch(r"[0-9a-f]{64}", r["sha256"])
            for r in records
        )
    ):
        raise ValueError("fixed Index CPU-only runtime closure mismatch")
    return lock


def verify_cpu_runtime(directory, platform_name="linux-x86_64"):
    directory = plain_path(directory, directory=True)
    lock = read_cpu_lock(platform_name)
    if {path.name for path in directory.iterdir()} != {item["file"] for item in lock["files"]}:
        raise ValueError("Index runtime has missing or unexpected files")
    for record in lock["files"]:
        verify_file(directory / record["file"], record)
    return {
        "directory": str(directory),
        "files": lock["files"],
        "variants": sorted(
            (r for r in lock["files"] if r["kind"] == "cpu_variant"), key=lambda r: r["file"]
        ),
        "lock_sha256": digest(
            NATIVE_ROOT
            / (WINDOWS_CPU_LOCK if platform_name == "win-x64" else "cpu-runtime.lock.json")
        ),
    }


def read_model_lock():
    model = read_json(NATIVE_ROOT / "model.lock.json")
    if (
        type(model["schema"]) is not int
        or model["schema"] != 1
        or type(model["abi"]) is not int
        or model["abi"] != ABI
        or model["package_id"] != PACKAGE_ID
        or model["version"] != VERSION
        or model["directions"] != list(DIRECTIONS)
        or model["template_sha256"] != TEMPLATE_SHA256
        or model["model_license"]
        != {
            "file": "model_LICENSE.txt",
            "bytes": 11358,
            "sha256": "c95bae1d1ce0235ecccd3560b772ec1efb97f348a79f0fbe0a634f0c2ccefe2c",
            "url": "https://raw.githubusercontent.com/bilibili/Index-Translate/912e86c8325d6c7c29a6a0b09c5f53482c6602f6/LICENSE",
        }
        or model["architecture"] != "qwen35"
        or model["vocab_size"] != 248320
        or model["metadata_eos"] != 248044
        or model["generated_eog"] != [248044, 248046]
        or model["im_start"] != 248045
        or model["enable_thinking"] is not False
        or model["context"] != 4096
        or model["max_output_tokens"] != 1024
        or model["decoding"]
        != {"temperature": 0, "top_p": 1, "top_k": 0, "min_p": 0, "repeat_penalty": 1}
        or model["model"]
        != {
            "file": "Index-Translate-2B.Q4_K_M.gguf",
            "bytes": 1312164352,
            "sha256": MODEL_SHA256,
            "url": f"https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF/resolve/{VERSION}/Index-Translate-2B.Q4_K_M.gguf",
        }
    ):
        raise ValueError("fixed Index model lock identity mismatch")
    return model


def verify_native():
    windows = platform.system() == "Windows" and platform.machine().lower() in ("amd64", "x86_64")
    if not windows and (platform.system() != "Linux" or platform.machine() != "x86_64"):
        raise ValueError("Index native runtime requires Linux x86_64 or Windows x64")
    root = plain_path(NATIVE_ROOT, directory=True)
    cpu_lock = WINDOWS_CPU_LOCK if windows else "cpu-runtime.lock.json"
    adapter_name = "typelay_index.dll" if windows else "libtypelay_index.so"
    manifest = read_json(root / "production-adapter.json")
    model = read_model_lock()
    if (
        type(manifest["schema"]) is not int
        or manifest["schema"] != 1
        or type(manifest["abi"]) is not int
        or manifest["abi"] != ABI
        or manifest["platform"] != ("win-x64" if windows else "linux-x86_64")
        or (manifest["exports"], manifest["capabilities"])
        not in ((LEGACY_EXPORTS, LEGACY_CAPABILITIES), (EXPORTS, CAPABILITIES))
        or manifest["upstream_revision"] != UPSTREAM
        or manifest["template_sha256"] != TEMPLATE_SHA256
        or manifest["model_lock_sha256"] != digest(root / "model.lock.json")
        or manifest["cpu_runtime_lock_sha256"] != digest(root / cpu_lock)
        or manifest["adapter"]["file"] != adapter_name
        or manifest["license"]["file"] != "LICENSE.llama.cpp"
        or manifest["template_license"]
        != {
            "file": "LICENSE.Index-Translate",
            "bytes": model["model_license"]["bytes"],
            "sha256": model["model_license"]["sha256"],
        }
        or {path.name for path in root.iterdir()}
        != {
            adapter_name,
            "production-adapter.json",
            "model.lock.json",
            cpu_lock,
            "LICENSE.llama.cpp",
            "LICENSE.Index-Translate",
            "cpu-runtime",
        }
    ):
        raise ValueError("Index production adapter manifest identity mismatch")
    adapter = verify_file(root / adapter_name, manifest["adapter"])
    verify_file(root / "LICENSE.llama.cpp", manifest["license"])
    verify_file(root / "LICENSE.Index-Translate", manifest["template_license"])
    return {
        **manifest["capabilities"],
        "adapter_path": str(adapter),
        "adapter_sha256": manifest["adapter"]["sha256"],
        "adapter_record": manifest["adapter"],
        "cpu_runtime": verify_cpu_runtime(
            root / "cpu-runtime", "win-x64" if windows else "linux-x86_64"
        ),
    }


def verify_production(package_root, *, verified_files=None):
    """verified_files may only be the inventory reader's completed full-hash map."""
    package_root = plain_path(package_root, directory=True)
    model = read_model_lock()
    records = [model["model"], model["model_license"]]
    expected = {safe_name(item["file"]) for item in records}
    if (
        len(expected) != 2
        or {p.name for p in package_root.iterdir()} != expected
        or (verified_files is not None and set(verified_files) != expected)
    ):
        raise ValueError("Index model package has missing or unexpected files")
    for record in records:
        path = plain_path(package_root / record["file"])
        checksum = digest(path) if verified_files is None else verified_files[record["file"]]
        if path.stat().st_size != record["bytes"] or checksum != record["sha256"]:
            raise ValueError("fixed installed Index model identity mismatch")
    return {
        **verify_native(),
        "model_path": str(package_root / model["model"]["file"]),
        "model_sha256": model["model"]["sha256"],
        "template_sha256": model["template_sha256"],
    }
