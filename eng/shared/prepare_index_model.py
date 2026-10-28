"""Explicit CI/developer preparation of pinned Index assets, never product startup."""

import argparse
import hashlib
import json
import tempfile
import time
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def official_url(value):
    value = urllib.parse.urlsplit(value)
    host = value.hostname or ""
    return (
        value.scheme == "https"
        and not value.username
        and not value.password
        and value.port in (None, 443)
        and (
            host in ("huggingface.co", "raw.githubusercontent.com")
            or host.endswith(".huggingface.co")
            or host.endswith(".hf.co")
        )
    )


class OfficialRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        if not official_url(url):
            raise ValueError("download-origin")
        return super().redirect_request(request, response, code, message, headers, url)


def prepare(output):
    lock = json.loads((ROOT / "src/shared/TypeLay.Worker/native/index/model.lock.json").read_text())
    output.mkdir(parents=True, exist_ok=True)
    if any(path.is_symlink() for path in (output, *output.parents)):
        raise ValueError("linked-cache")
    deadline = time.monotonic() + 600
    opener = urllib.request.build_opener(OfficialRedirect())
    for kind in ("model", "model_license"):
        record = lock[kind]
        target = output / record["file"]
        if target.is_symlink() or target.name != record["file"]:
            raise ValueError("linked-or-invalid-cache")
        if target.exists():
            with target.open("rb") as stream:
                if (
                    target.stat().st_size != record["bytes"]
                    or hashlib.file_digest(stream, "sha256").hexdigest() != record["sha256"]
                ):
                    raise ValueError("fixed-cache-integrity")
            continue
        if not official_url(record["url"]):
            raise ValueError("download-origin")
        with tempfile.TemporaryDirectory(prefix=".index-model-", dir=output) as directory:
            pending = Path(directory) / "asset"
            with opener.open(record["url"], timeout=60) as response, pending.open("xb") as stream:
                if response.status != 200 or not official_url(response.url):
                    raise ValueError("download-origin")
                checksum, size = hashlib.sha256(), 0
                while block := response.read(1024 * 1024):
                    if time.monotonic() >= deadline:
                        raise TimeoutError("model-prepare-deadline")
                    size += len(block)
                    if size > record["bytes"]:
                        raise ValueError("model-download-size")
                    checksum.update(block)
                    stream.write(block)
            if size != record["bytes"] or checksum.hexdigest() != record["sha256"]:
                raise ValueError("model-download-integrity")
            if target.exists() or target.is_symlink():
                raise ValueError("concurrent-cache-change")
            pending.rename(target)
    print(json.dumps({"package": lock["package_id"], "version": lock["version"], "verified": True}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--output", type=Path, required=True)
    prepare(parser.parse_args().output.absolute())
