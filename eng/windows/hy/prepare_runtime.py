"""Explicitly prepare the pinned official CPU closure for Windows release builds.

Downloads only the 19,662,390-byte CPU ZIP, never a model or compiler. Existing
cache entries are fully checked and an invalid cache fails closed. Production
startup does not depend on this build-only archive or its download lock.
"""

import argparse
import hashlib
import json
import os
import stat
import sys
import tempfile
import time
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "src/shared/TypeLay.Worker"))
from typelay_worker.hy_assets import (  # noqa: E402
    CHUNK,
    plain_path,
    read_cpu_lock,
    read_json,
    safe_name,
    verify_cpu_runtime,
    verify_file,
)


def extract_runtime(archive, output, records):
    """Copy only locked regular files; reject archive ambiguity and links."""
    output = Path(output).absolute()
    output.parent.mkdir(parents=True, exist_ok=True)
    plain_path(output.parent, directory=True)
    if output.exists() or output.is_symlink():
        raise ValueError("runtime output already exists")
    with tempfile.TemporaryDirectory(prefix=".hy-cpu-", dir=output.parent) as temporary:
        staging = Path(temporary) / "runtime"
        staging.mkdir()
        with zipfile.ZipFile(archive) as package:
            entries, names = {}, set()
            for entry in package.infolist():
                name = safe_name(entry.filename)
                kind = stat.S_IFMT(entry.external_attr >> 16)
                if (
                    name.casefold() in names
                    or len(entries) >= 1024
                    or entry.filename != entry.orig_filename
                    or entry.is_dir()
                    or kind not in (0, stat.S_IFREG)
                    or entry.flag_bits & 1
                    or entry.external_attr & 0x410
                    or entry.file_size > 256 * 1024 * 1024
                ):
                    raise ValueError("unsafe or duplicate CPU archive entry")
                names.add(name.casefold())
                entries[name] = entry
            for record in records:
                name = safe_name(record["file"])
                entry = entries.get(name)
                if entry is None or entry.file_size != record["bytes"]:
                    raise ValueError("locked CPU archive member missing or size mismatch")
                with package.open(entry) as source, (staging / name).open("xb") as destination:
                    size = 0
                    for block in iter(lambda: source.read(CHUNK), b""):
                        size += len(block)
                        if size > record["bytes"]:
                            raise ValueError("CPU member byte bound")
                        destination.write(block)
                verify_file(staging / name, record)
        staging.rename(output)


def prepare(cache, output, *, offline=False):
    cache, output = Path(cache).absolute(), Path(output).absolute()
    lock = read_json(Path(__file__).with_name("runtime-archive.lock.json"))
    cpu = read_cpu_lock("win-x64")
    if (
        lock["schema"] != 1
        or lock["sha256"] != cpu["runtime_archive_sha256"]
        or lock["file"] != "llama-b11717-bin-win-cpu-x64.zip"
        or lock["bytes"] != 19662390
        or lock["tag"] != cpu["upstream_tag"]
        or lock["commit"] != cpu["upstream_revision"]
        or lock["url"]
        != "https://github.com/ggml-org/llama.cpp/releases/download/b11717/llama-b11717-bin-win-cpu-x64.zip"
    ):
        raise ValueError("build archive and production CPU lock disagree")
    cache.mkdir(parents=True, exist_ok=True)
    plain_path(cache, directory=True)
    archive = cache / safe_name(lock["file"])
    if {item.name for item in cache.iterdir()} - {archive.name}:
        raise ValueError("CPU cache contains unexpected files")
    source = "verified-cache"
    if archive.exists() or archive.is_symlink():
        verify_file(archive, lock)
    else:
        if offline:
            raise FileNotFoundError("fixed CPU archive unavailable in offline cache")
        deadline = time.monotonic() + 120
        partial = archive.with_suffix(archive.suffix + ".partial")
        try:
            with partial.open("xb") as destination:
                request = urllib.request.Request(lock["url"], headers={"User-Agent": "TypeLay/1"})
                with urllib.request.urlopen(request, timeout=20) as response:
                    if response.status != 200 or not response.url.startswith("https://"):
                        raise ValueError("fixed HTTPS CPU archive download failed")
                    size, digest = 0, hashlib.sha256()
                    for block in iter(lambda: response.read(CHUNK), b""):
                        size += len(block)
                        if time.monotonic() > deadline or size > lock["bytes"]:
                            raise ValueError("CPU archive download budget exceeded")
                        digest.update(block)
                        destination.write(block)
                if size != lock["bytes"] or digest.hexdigest() != lock["sha256"]:
                    raise ValueError("fixed CPU archive download full hash mismatch")
            # Exclusive publication cannot overwrite an entry that appeared meanwhile.
            os.link(partial, archive)
            partial.unlink()
        finally:
            partial.unlink(missing_ok=True)
        verify_file(archive, lock)
        source = "downloaded-full-hash-verified"
    if not output.exists() and not output.is_symlink():
        extract_runtime(archive, output, cpu["files"])
    verified = verify_cpu_runtime(output, "win-x64")
    return {
        "source": source,
        "archiveSha256": lock["sha256"],
        "files": len(verified["files"]),
        "runtimeBytes": sum(r["bytes"] for r in cpu["files"]),
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--offline", action="store_true")
    args = parser.parse_args()
    print(json.dumps(prepare(args.cache, args.output, offline=args.offline)))
