"""Full-byte read-only identity gate for the private Windows Hy fixture.

This module never downloads, repairs, loads a library, or runs a model. The
preparer and each isolated worker independently use the same gate, including
cache hits. Runtime extraction is an exact allowlist, never a ZIP extractall.
"""

import hashlib
import json
import stat
import struct
import sys
import zipfile
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[5]
ROOT = REPOSITORY / "src/shared/TypeLay.Worker/native/hy"
WINDOWS_ROOT = REPOSITORY / "eng/windows/hy"
sys.path.insert(0, str(REPOSITORY / "src/shared/TypeLay.Worker"))
from typelay_worker.hy_assets import CHUNK, digest, plain_path, safe_name, verify_file  # noqa: E402

LOCK = ROOT / "assets.lock.json"


def read_lock():
    lock = json.loads(plain_path(LOCK).read_text(encoding="utf-8"))
    if lock["schema"] != 1:
        raise ValueError("unsupported asset lock")
    for key in ("archive_members", "runtime_files"):
        names = [safe_name(item["file"]) for item in lock[key]]
        if len(names) != len(set(name.casefold() for name in names)):
            raise ValueError("duplicate case-insensitive lock member")
    expected = {
        "ggml-cpu-" + name + ".dll"
        for name in (
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
    }
    variants = {item["file"] for item in lock["runtime_files"] if item["kind"] == "cpu_variant"}
    dependencies = {"llama.dll", "ggml.dll", "ggml-base.dll", "libomp.dll"}
    if variants != expected or {item["file"] for item in lock["runtime_files"]} != (
        expected | dependencies | {"LICENSE-LLVM-OpenMP"}
    ):
        raise ValueError("fixed CPU-only runtime closure mismatch")
    by_name = {item["file"]: item for item in lock["archive_members"]}
    for record in lock["runtime_files"]:
        original = by_name[record["file"]]
        if any(record[key] != original[key] for key in ("bytes", "sha256")):
            raise ValueError("runtime member is not tied to fixed ZIP bytes")
    return lock


def verify_sources(lock):
    for record in lock["source_files"]:
        relative = Path(record["file"])
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError("source lock escape")
        verify_file(ROOT / relative, record)
    for record in lock["windows_source_files"]:
        verify_file(WINDOWS_ROOT / safe_name(record["file"]), record)
    headers = json.loads((ROOT / "headers.lock.json").read_text(encoding="utf-8"))
    if headers["commit"] != lock["runtime_archive"]["commit"] or len(headers["files"]) != 7:
        raise ValueError("header/runtime revision mismatch")
    names = {safe_name(record["file"]) for record in headers["files"]}
    if names != {path.name for path in (ROOT / "include").glob("*.h")}:
        raise ValueError("unexpected or missing upstream header")
    for record in headers["files"]:
        verify_file(ROOT / "include" / record["file"], record)


def verify_archive(path, lock):
    """Check the whole ZIP, every entry and each full decompressed member hash."""
    verify_file(path, lock["runtime_archive"])
    expected = {record["file"]: record for record in lock["archive_members"]}
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        names = [safe_name(entry.filename) for entry in entries]
        if len(names) != len(set(name.casefold() for name in names)) or set(names) != set(expected):
            raise ValueError("ZIP has missing, duplicate or unexpected members")
        for entry in entries:
            mode = entry.external_attr >> 16
            kind = stat.S_IFMT(mode)
            if (
                entry.is_dir()
                or entry.filename != entry.orig_filename
                or kind not in (0, stat.S_IFREG)
                or entry.flag_bits & 1
                or entry.external_attr & 0x410  # DOS directory or reparse-point bits.
                or entry.file_size != expected[entry.filename]["bytes"]
            ):
                raise ValueError("ZIP link, directory, encryption or size mismatch")
            value = hashlib.sha256()
            size = 0
            with archive.open(entry) as stream:
                for block in iter(lambda: stream.read(CHUNK), b""):
                    size += len(block)
                    if size > entry.file_size:
                        raise ValueError("ZIP member byte bound")
                    value.update(block)
            if size != entry.file_size or value.hexdigest() != expected[entry.filename]["sha256"]:
                raise ValueError("ZIP member full hash mismatch")
    return len(entries)


def verify_runtime(runtime, lock):
    runtime = plain_path(runtime, directory=True)
    expected = {record["file"] for record in lock["runtime_files"]}
    if {path.name for path in runtime.iterdir()} != expected:
        raise ValueError("runtime has missing or unexpected files")
    for record in lock["runtime_files"]:
        verify_file(runtime / record["file"], record)
    return {
        "directory": str(runtime),
        "files": lock["runtime_files"],
        "variants": sorted(
            (item for item in lock["runtime_files"] if item["kind"] == "cpu_variant"),
            key=lambda item: item["file"],
        ),
        "lock_sha256": digest(LOCK),
    }


def gguf_template(path):
    # This fixed GGUF's metadata ends around 5 MiB. Bound parsing to 8 MiB.
    with Path(path).open("rb") as stream:
        data = stream.read(8 * CHUNK)
    position = 0

    def take(count):
        nonlocal position
        if count < 0 or position + count > len(data):
            raise ValueError("GGUF metadata exceeds fixed read bound")
        result = data[position : position + count]
        position += count
        return result

    def number(fmt):
        return struct.unpack(fmt, take(struct.calcsize(fmt)))[0]

    def string():
        return take(number("<Q")).decode("utf-8", "strict")

    formats = {
        0: "<B",
        1: "<b",
        2: "<H",
        3: "<h",
        4: "<I",
        5: "<i",
        6: "<f",
        7: "<?",
        10: "<Q",
        11: "<q",
        12: "<d",
    }

    def value(kind, depth=0):
        if depth > 1:
            raise ValueError("nested GGUF metadata array")
        if kind == 8:
            return string()
        if kind == 9:
            item_kind, count = number("<I"), number("<Q")
            if count > 200000:
                raise ValueError("GGUF metadata array bound")
            # Consume without retaining the vocabulary's 120818 strings.
            for _ in range(count):
                value(item_kind, depth + 1)
            return None
        return number(formats[kind])

    if take(4) != b"GGUF" or number("<I") != 3:
        raise ValueError("GGUF format identity")
    number("<Q")
    count = number("<Q")
    if count > 1024:
        raise ValueError("GGUF metadata key bound")
    template = None
    seen = set()
    for _ in range(count):
        key, kind = string(), number("<I")
        if key in seen:
            raise ValueError("duplicate GGUF metadata key")
        seen.add(key)
        item = value(kind)
        if key == "tokenizer.chat_template":
            template = item
    if not isinstance(template, str):
        raise ValueError("missing fixed GGUF template")
    return hashlib.sha256(template.encode("utf-8")).hexdigest()


def verify_downloads(assets, lock):
    assets = plain_path(assets, directory=True)
    expected = {lock[key]["file"] for key in ("model", "model_license", "runtime_archive")}
    if {path.name for path in assets.iterdir()} != expected:
        raise ValueError("download cache has missing or unexpected files")
    for key in ("model", "model_license"):
        verify_file(assets / lock[key]["file"], lock[key])
    count = verify_archive(assets / lock["runtime_archive"]["file"], lock)
    if gguf_template(assets / lock["model"]["file"]) != lock["template_sha256"]:
        raise ValueError("fixed template hash mismatch")
    return count


def verify(assets, runtime, adapter):
    lock = read_lock()
    verify_sources(lock)
    count = verify_downloads(assets, lock)
    cpu = verify_runtime(runtime, lock)
    adapter = plain_path(adapter)
    if adapter.name != "typelay_hy.dll":
        raise ValueError("fixed adapter name mismatch")
    report_path = plain_path(adapter.parent / "build-verification.json")
    report = json.loads(report_path.read_text(encoding="utf-8"))
    if report["asset_lock_sha256"] != digest(LOCK) or report["abi"] != 5:
        raise ValueError("adapter build input identity mismatch")
    if report["adapter_exports"] != [
        "hy_audit",
        "hy_close",
        "hy_open_v5",
        "hy_translate",
        "hy_translate_glossary_v5",
        "hy_translate_styled_glossary_v5",
        "hy_translate_styled_v5",
    ]:
        raise ValueError("adapter fixture export identity mismatch")
    verify_file(adapter, report["adapter"])
    return lock, {
        "model_full_hash": lock["model"]["sha256"],
        "runtime_archive_full_hash": lock["runtime_archive"]["sha256"],
        "runtime_regular_members_verified": count,
        "template_sha256": lock["template_sha256"],
        "cpu_runtime": cpu,
        "adapter_sha256": report["adapter"]["sha256"],
        "adapter_path": str(adapter),
        "adapter_record": report["adapter"],
        "model_path": str(Path(assets) / lock["model"]["file"]),
    }
