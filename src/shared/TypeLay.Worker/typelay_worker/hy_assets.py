"""Read-only asset identity gate for the packaged Hy runtime.

Only the worker's bundled fixed locks and production manifest are trusted inputs.
The installed model tree cannot provide a replacement asset lock or adapter.
No downloads, source/header checks, archive extraction or native loads occur here.
"""

import hashlib
import json
import os
import platform
import stat
from pathlib import Path

NATIVE_ROOT = Path(__file__).resolve().parents[1] / "native" / "hy"
CHUNK = 1024 * 1024
ABI = 3
EXPORTS = ["hy_close", "hy_open_v3", "hy_translate"]
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
)
UPSTREAM = "848bbda9041decd87904f7eb5a2990863ecce03b"
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


def read_cpu_lock():
    lock = read_json(NATIVE_ROOT / "cpu-runtime.lock.json")
    if (
        lock["schema"] != 1
        or lock["platform"] != "linux-x86_64"
        or lock["upstream_revision"] != UPSTREAM
        or lock["upstream_tag"] != "b11717"
    ):
        raise ValueError("unsupported fixed CPU runtime lock")
    records = lock["files"]
    names = [safe_name(record["file"]) for record in records]
    expected = {f"libggml-cpu-{name}.so" for name in VARIANTS}
    variants = {record["file"] for record in records if record["kind"] == "cpu_variant"}
    dependencies = {record["file"] for record in records if record["kind"] == "dependency"}
    if (
        len(names) != len(set(names))
        or variants != expected
        or dependencies != set(DEPENDENCIES)
        or set(names) != expected | set(DEPENDENCIES)
    ):
        raise ValueError("fixed CPU-only runtime closure mismatch")
    return lock


def verify_cpu_runtime(directory):
    """Validate the fixed CPU-only directory without loading any ELF library."""
    directory = plain_path(directory, directory=True)
    lock = read_cpu_lock()
    if {path.name for path in directory.iterdir()} != {item["file"] for item in lock["files"]}:
        raise ValueError("runtime has missing or unexpected files")
    for record in lock["files"]:
        verify_file(directory / record["file"], record)
    return {
        "directory": str(directory),
        "files": lock["files"],
        "variants": sorted(
            (item for item in lock["files"] if item["kind"] == "cpu_variant"),
            key=lambda item: item["file"],
        ),
        "lock_sha256": digest(NATIVE_ROOT / "cpu-runtime.lock.json"),
    }


def verify_native():
    """Check only shipped production inputs, never source/ZIP/fixture reports."""
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        raise ValueError("Hy production runtime requires Linux x86_64")
    root = plain_path(NATIVE_ROOT, directory=True)
    manifest = read_json(root / "production-adapter.json")
    if (
        manifest["schema"] != 1
        or manifest["abi"] != ABI
        or manifest["platform"] != "linux-x86_64"
        or manifest["exports"] != EXPORTS
        or manifest["upstream_revision"] != UPSTREAM
        or manifest["model_lock_sha256"] != digest(root / "model.lock.json")
        or manifest["cpu_runtime_lock_sha256"] != digest(root / "cpu-runtime.lock.json")
        or manifest["adapter"]["file"] != "libtypelay_hy.so"
        or manifest["license"]["file"] != "LICENSE.llama.cpp"
    ):
        raise ValueError("production adapter manifest identity mismatch")
    adapter = verify_file(root / manifest["adapter"]["file"], manifest["adapter"])
    verify_file(root / manifest["license"]["file"], manifest["license"])
    return {
        "adapter_path": str(adapter),
        "adapter_sha256": manifest["adapter"]["sha256"],
        "adapter_record": manifest["adapter"],
        "cpu_runtime": verify_cpu_runtime(root / "cpu-runtime"),
    }


def verify_production(package_root, *, verified_files=None):
    """Validate an installed Hy package before native loading.

    verified_files is the inventory reader's completed full-hash map, with
    canonical filenames relative to this exact package_root. It avoids reading
    the large GGUF twice. Never pass values claimed by an installed manifest.
    """
    package_root = plain_path(package_root, directory=True)
    model = read_json(NATIVE_ROOT / "model.lock.json")
    if (
        model["schema"] != 1
        or model["abi"] != ABI
        or model["package_id"] != "hy-mt2-1-8b-q4-k-m"
        or model["version"] != "a0c709d9fac510f2c807aa3af52872340dc37a4a"
        or model["directions"] != list(DIRECTIONS)
    ):
        raise ValueError("fixed model lock identity mismatch")
    records = [model["model"], model["model_license"]]
    expected = {safe_name(item["file"]) for item in records}
    if (
        len(expected) != 2
        or {path.name for path in package_root.iterdir()} != expected
        or (verified_files is not None and set(verified_files) != expected)
    ):
        raise ValueError("model package has missing or unexpected files")
    for record in records:
        path = plain_path(package_root / record["file"])
        checksum = digest(path) if verified_files is None else verified_files[record["file"]]
        if path.stat().st_size != record["bytes"] or checksum != record["sha256"]:
            raise ValueError("fixed installed model identity mismatch")
    return {
        **verify_native(),
        "model_path": str(package_root / model["model"]["file"]),
        "model_sha256": model["model"]["sha256"],
        "template_sha256": model["template_sha256"],
    }
