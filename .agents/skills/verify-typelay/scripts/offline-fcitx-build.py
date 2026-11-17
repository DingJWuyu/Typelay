#!/usr/bin/env python3
"""Supply local raw archives to the production Fcitx builder in an offline namespace."""

import argparse
import socket
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT))
from eng.linux.cooperative import fcitx, third_party  # noqa: E402


def copy(source, destination):
    if not source.is_file():
        raise FileNotFoundError(f"offline build archive missing: {source}")
    subprocess.run(["cp", "--reflink=auto", source, destination], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache-build", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if sorted(name for _, name in socket.if_nameindex()) != ["lo"]:
        parser.error("run inside unshare --user --map-current-user --net")
    cache, output = args.cache_build.resolve(strict=True), args.output.resolve()
    if (
        not output.is_relative_to(ROOT / "artifacts")
        or output.is_relative_to(cache)
        or cache.is_relative_to(output)
    ):
        parser.error("output must be in this worktree's artifacts and separate from the cache")
    archives = {}
    for project, (commit, _) in third_party.FCITX_PINS.items():
        owner = "kpu" if project == "kenlm" else "fcitx"
        archives[f"https://codeload.github.com/{owner}/{project}/tar.gz/{commit}"] = (
            cache / f"{project}-{commit}.tar.gz"
        )
    for directory, name, _ in third_party.DATA:
        archives[f"https://download.fcitx-im.org/data/{name}"] = (
            cache / "sources" / directory / name
        )
    spell = Path("sources/fcitx5/src/modules/spell/en_dict-20121020.tar.gz")
    for source in (*archives.values(), cache / spell):
        if not source.is_file():
            raise FileNotFoundError(f"offline build archive missing: {source}")
    output.mkdir(parents=True, exist_ok=True)
    original_download = third_party.download

    def cached_download(url, destination, digest):
        source = archives[url]  # An unexpected source fails without network fallback.
        if not destination.exists():
            copy(source, destination)
        # Keep the production builder's exact pinned hash check.
        original_download(url, destination, digest)
        # DATA requests occur after all source trees have been extracted. CMake
        # verifies this additional upstream archive against its own pinned hash.
        if url.startswith("https://download.fcitx-im.org/data/") and not (output / spell).exists():
            copy(cache / spell, output / spell)

    third_party.download = cached_download
    try:
        fcitx.build(output, third_party.CORE_VERSION)
    finally:
        third_party.download = original_download


if __name__ == "__main__":
    main()
