"""Explicitly prepare the pinned official CPU closure for Linux release builds.

Downloads only the 17.1 MiB CPU archive, never a model or compiler. Existing
cache entries are fully checked and an invalid cache fails closed. Production
startup does not depend on this build-only archive or its download lock.
"""

import argparse
import hashlib
import json
import os
import posixpath
import shutil
import sys
import tarfile
import tempfile
import time
import urllib.request
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
    """Copy only locked files; archive links are resolved without extraction."""
    output = Path(output).absolute()
    output.parent.mkdir(parents=True, exist_ok=True)
    plain_path(output.parent, directory=True)
    if output.exists() or output.is_symlink():
        raise ValueError("runtime output already exists")
    with tempfile.TemporaryDirectory(prefix=".hy-cpu-", dir=output.parent) as temporary:
        staging = Path(temporary) / "runtime"
        staging.mkdir()
        with tarfile.open(archive, "r:gz") as package:
            entries = {}
            for entry in package:
                name = entry.name.rstrip("/")
                if (
                    not name
                    or name.startswith("/")
                    or "\\" in name
                    or any(part in ("", ".", "..") for part in name.split("/"))
                    or name in entries
                    or len(entries) >= 1024
                    or not (entry.isfile() or entry.isdir() or entry.issym())
                    or entry.size > 256 * 1024 * 1024
                ):
                    raise ValueError("unsafe or duplicate CPU archive entry")
                entries[name] = entry
            for record in records:
                name = safe_name(record["file"])
                candidates = [key for key in entries if key.rsplit("/", 1)[-1] == name]
                if len(candidates) != 1:
                    raise ValueError("missing or ambiguous locked CPU archive member")
                key, visited = candidates[0], set()
                while entries[key].issym():
                    if key in visited:
                        raise ValueError("cyclic CPU archive link")
                    visited.add(key)
                    target = entries[key].linkname
                    if not target or target.startswith("/") or "\\" in target:
                        raise ValueError("unsafe CPU archive link")
                    key = posixpath.normpath(posixpath.join(posixpath.dirname(key), target))
                    if key not in entries or key.startswith("../"):
                        raise ValueError("CPU archive link escapes its verified contents")
                entry = entries[key]
                if not entry.isfile() or entry.size != record["bytes"]:
                    raise ValueError("locked CPU archive member size mismatch")
                with package.extractfile(entry) as source, (staging / name).open("xb") as dest:
                    shutil.copyfileobj(source, dest, CHUNK)
                verify_file(staging / name, record)
        staging.rename(output)


def prepare(cache, output, *, offline=False):
    cache, output = Path(cache).absolute(), Path(output).absolute()
    lock = read_json(Path(__file__).with_name("runtime-archive.lock.json"))
    cpu = read_cpu_lock()
    if lock["schema"] != 1 or lock["sha256"] != cpu["runtime_archive_sha256"]:
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
    verified = verify_cpu_runtime(output)
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
