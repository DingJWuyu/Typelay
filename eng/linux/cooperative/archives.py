"""Supply pinned raw archives from a flat local cache or the official URL."""

import hashlib
import subprocess
import urllib.request
from pathlib import Path


def validate_cache(archive_cache, output):
    """Resolve cache identity before any output mutation, including symlink aliases."""
    if archive_cache is None:
        return None
    cache, output = Path(archive_cache).resolve(), output.resolve()
    if output.is_relative_to(cache) or cache.is_relative_to(output):
        raise ValueError(f"Build output and archive cache must be separate: {output}, {cache}")
    for path in output.rglob("*"):
        if path.is_symlink():
            target = path.resolve()
            if target.is_relative_to(cache) or cache.is_relative_to(target):
                raise ValueError(
                    f"Build output must be separate from archive cache: {path} -> {target}"
                )
    return cache


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def download(url, destination, digest, archive_cache=None):
    if archive_cache is not None:
        archive_cache = validate_cache(archive_cache, destination.parent)
        source = archive_cache / destination.name
        if not source.is_file():
            raise FileNotFoundError(f"Local build archive missing: {source}")
        if sha256(source) != digest:
            raise RuntimeError(f"Official source/data checksum mismatch: {source}")
        subprocess.run(
            ["cp", "--reflink=auto", "--remove-destination", source, destination], check=True
        )
    elif not destination.exists():
        with urllib.request.urlopen(url, timeout=180) as response:
            destination.write_bytes(response.read())
    if sha256(destination) != digest:
        raise RuntimeError(f"Official source/data checksum mismatch: {destination}")
