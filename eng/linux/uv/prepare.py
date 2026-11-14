"""Package the fixed Linux uv executable and its upstream licenses."""

import argparse
import json
import os
import shutil
import sys
import tarfile
import tempfile
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from private_uv import PIN, executable, verify_file  # noqa: E402


def prepare(cache, output, *, offline=False):
    cache, output = Path(cache).resolve(), Path(output).resolve()
    if output == cache or output.is_relative_to(cache) or cache.is_relative_to(output):
        raise ValueError("private-uv-output-overlaps-cache")
    if output.exists():
        raise ValueError("private-uv-output-exists")
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / "uv-0.13.8-x86_64-unknown-linux-gnu.tar.gz"
    if not archive.exists() and not archive.is_symlink():
        if offline:
            raise FileNotFoundError("private-uv-offline-cache-missing")
        partial = archive.with_suffix(".partial")
        deadline = time.monotonic() + 120
        with partial.open("xb") as destination:
            try:
                request = urllib.request.Request(PIN["url"], headers={"User-Agent": "TypeLay/1"})
                with urllib.request.urlopen(request, timeout=20) as response:
                    if response.status != 200 or not response.url.startswith("https://"):
                        raise ValueError("private-uv-download-source")
                    count = 0
                    for block in iter(lambda: response.read(1024 * 1024), b""):
                        count += len(block)
                        if count > PIN["bytes"] or time.monotonic() > deadline:
                            raise ValueError("private-uv-download-budget")
                        destination.write(block)
                destination.flush()
                verify_file(partial, PIN)
                os.link(partial, archive)
            finally:
                partial.unlink(missing_ok=True)
    verify_file(archive, PIN)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".private-uv-", dir=output.parent) as directory:
        staging = Path(directory) / "support/uv"
        staging.mkdir(parents=True)
        with tarfile.open(archive, "r|gz") as package:
            for member in package:
                if member.name != "uv-x86_64-unknown-linux-gnu/uv":
                    continue
                if not member.isfile() or member.size != PIN["files"]["uv"]["bytes"]:
                    raise ValueError("private-uv-archive-member")
                with package.extractfile(member) as source, (staging / "uv").open("xb") as target:
                    shutil.copyfileobj(source, target)
                break
            else:
                raise ValueError("private-uv-archive-member")
        (staging / "uv").chmod(0o755)
        for name in ("LICENSE-MIT", "LICENSE-APACHE"):
            shutil.copyfile(Path(__file__).with_name(name), staging / name)
        (staging / "source.json").write_text(json.dumps(PIN, indent=2) + "\n", encoding="utf-8")
        executable(Path(directory))
        staging.rename(output)
    return {"version": PIN["version"], "archiveSha256": PIN["sha256"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--offline", action="store_true")
    args = parser.parse_args()
    print(json.dumps(prepare(args.cache, args.output, offline=args.offline)))
