"""Explicit-consent installation only. This process never receives user input text.

uv resolves Linux CPython wheels and verifies their hashes. A versioned environment
is built in its final location so absolute venv paths remain valid. The active
symlink changes only after the real offline worker passes both translation checks.
"""

import fcntl
import hashlib
import json
import os
import re
import shutil
import signal
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
from email.utils import parsedate_to_datetime
from pathlib import Path, PurePosixPath

sys.path.insert(0, str(Path(__file__).resolve().parent))
from paths import private_directory  # noqa: E402

PYTHON = "3.13.16"
MAX_ARCHIVE = 1024 * 1024 * 1024


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


class Redirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        parsed = urllib.parse.urlsplit(newurl)
        if (
            parsed.scheme != "https"
            or parsed.hostname != "argos-net.com"
            or parsed.username is not None
            or parsed.password is not None
            or parsed.port not in (None, 443)
        ):
            raise OSError("model-download-origin")
        return super().redirect_request(request, fp, code, msg, headers, newurl)


class Paused(Exception):
    pass


def pause(_signal, _frame):
    raise Paused()


def validator(headers):
    etag = headers.get("ETag", "")
    if re.fullmatch(r'"[\x21\x23-\x7e]{0,512}"', etag):
        return {"ETag": etag}
    if etag:
        return {}
    # If-Range requires a strong validator. A Last-Modified date is only strong
    # when there is enough separation to rule out clock ambiguity; keep a
    # conservative 60-second margin (RFC 9110 sections 8.8.2.2 and 13.1.5).
    modified = headers.get("Last-Modified", "")
    try:
        age = parsedate_to_datetime(headers.get("Date", "")) - parsedate_to_datetime(modified)
        if age.total_seconds() >= 60 and len(modified) <= 128:
            return {"Last-Modified": modified}
    except (TypeError, ValueError, OverflowError):
        pass
    return {}


def download(pair, cache):
    if pair not in ("zh_en", "en_zh"):
        raise ValueError("model-direction")
    url = f"https://argos-net.com/v1/translate-{pair}-1_9.argosmodel"
    archive = cache / f"{pair}-1.9.argosmodel"
    if archive.is_file() and not archive.is_symlink() and 0 < archive.stat().st_size <= MAX_ARCHIVE:
        return archive, url
    partial = archive.with_suffix(".part")
    metadata = partial.with_suffix(".part.json")

    def discard():
        partial.unlink(missing_ok=True)
        metadata.unlink(missing_ok=True)
        metadata.with_suffix(".tmp").unlink(missing_ok=True)

    offset, saved, checksum = 0, {}, hashlib.sha256()
    try:
        if partial.is_symlink() or metadata.is_symlink() or metadata.stat().st_size > 4096:
            raise ValueError("model-resume-metadata")
        saved = json.loads(metadata.read_text(encoding="utf-8"))
        offset = saved["size"]
        if (
            saved["url"] != url
            or type(offset) is not int
            or type(saved["total"]) is not int
            or not 0 < offset <= saved["total"] <= MAX_ARCHIVE
            or not saved["validator"]
            or not isinstance(saved["validatorHeaders"], dict)
            or saved["validator"] != validator(saved["validatorHeaders"])
            or partial.stat().st_size < offset
        ):
            raise ValueError("model-resume-metadata")
        with partial.open("rb") as stream:
            remaining = offset
            while remaining:
                block = stream.read(min(65536, remaining))
                if not block:
                    raise ValueError("model-resume-size")
                checksum.update(block)
                remaining -= len(block)
        if checksum.hexdigest() != saved["sha256"]:
            raise ValueError("model-resume-hash")
    except (OSError, ValueError, KeyError, TypeError):
        discard()
        offset, saved, checksum = 0, {}, hashlib.sha256()

    opener = urllib.request.build_opener(Redirects())
    started = time.monotonic()
    # One fresh retry is sufficient for an ignored Range, stale validator or 416.
    for attempt in range(2):
        headers = {"User-Agent": "TypeLay/0.2.0", "Accept-Encoding": "identity"}
        if offset:
            headers.update(
                {"Range": f"bytes={offset}-", "If-Range": next(iter(saved["validator"].values()))}
            )
        try:
            response = opener.open(urllib.request.Request(url, headers=headers), timeout=45)
        except urllib.error.HTTPError as error:
            if error.code != 416 or not offset or attempt:
                raise
            error.close()
            discard()
            offset, saved, checksum = 0, {}, hashlib.sha256()
            continue
        with response:
            length = int(response.headers.get("Content-Length", "0"))
            if (
                length < 0
                or length > MAX_ARCHIVE
                or response.headers.get("Content-Encoding", "identity") != "identity"
            ):
                raise ValueError("model-download-size")
            resume = offset and response.status == 206
            if resume:
                match = re.fullmatch(
                    r"bytes ([0-9]+)-([0-9]+)/([0-9]+)", response.headers.get("Content-Range", "")
                )
                if (
                    not match
                    or int(match[1]) != offset
                    or int(match[2]) != saved["total"] - 1
                    or int(match[3]) != saved["total"]
                    or (length and length != saved["total"] - offset)
                    or validator(response.headers) != saved["validator"]
                ):
                    discard()
                    offset, saved, checksum = 0, {}, hashlib.sha256()
                    if attempt:
                        raise ValueError("model-download-range")
                    continue
            elif response.status != 200:
                raise ValueError("model-download-status")
            else:
                # A server ignoring Range (including a failed If-Range) supplies
                # a complete representation: overwrite, never append it.
                discard()
                offset, saved, checksum = 0, {}, hashlib.sha256()
            total = saved["total"] if resume else length
            identity = validator(response.headers)
            identity_headers = {
                key: response.headers[key]
                for key in ("ETag", "Last-Modified", "Date")
                if key in response.headers
            }
            count = offset
            last_progress = 0.0
            descriptor = os.open(partial, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
            with os.fdopen(descriptor, "r+b") as output:
                output.truncate(offset)
                output.seek(offset)

                def checkpoint():
                    output.flush()
                    os.fsync(output.fileno())
                    if not identity or not total or not 0 < count <= total:
                        metadata.unlink(missing_ok=True)
                        return
                    value = {
                        "url": url,
                        "size": count,
                        "total": total,
                        "validator": identity,
                        "validatorHeaders": identity_headers,
                        "sha256": checksum.hexdigest(),
                    }
                    pending = metadata.with_suffix(".tmp")
                    fd = os.open(
                        pending, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600
                    )
                    with os.fdopen(fd, "w", encoding="utf-8") as stream:
                        json.dump(value, stream)
                        stream.flush()
                        os.fsync(stream.fileno())
                    pending.replace(metadata)

                try:
                    while block := response.read(65536):
                        if count + len(block) > MAX_ARCHIVE or time.monotonic() - started > 900:
                            raise ValueError("model-download-limit")
                        output.write(block)
                        checksum.update(block)
                        count += len(block)
                        if time.monotonic() - last_progress > 2:
                            last_progress = time.monotonic()
                            checkpoint()
                            print(f"正在下载 {pair} 模型：{count // (1024 * 1024)} MiB", flush=True)
                    if (total and count != total) or not count:
                        raise OSError("model-download-incomplete")
                    output.flush()
                    os.fsync(output.fileno())
                except (OSError, Paused):
                    checkpoint()
                    raise
                except ValueError:
                    metadata.unlink(missing_ok=True)
                    raise
            partial.replace(archive)
            metadata.unlink(missing_ok=True)
            return archive, url
    raise OSError("model-download-retry")


def extract_model(archive, destination, pair):
    with zipfile.ZipFile(archive) as package:
        entries = package.infolist()
        if len(entries) > 30000 or sum(item.file_size for item in entries) > 2 * MAX_ARCHIVE:
            raise ValueError("model-archive-size")
        names = set()
        for item in entries:
            path = PurePosixPath(item.filename)
            if (
                path.is_absolute()
                or ".." in path.parts
                or "\\" in item.filename
                or ":" in item.filename
                or len(item.filename) > 1024
                or stat.S_ISLNK(item.external_attr >> 16)
                or item.filename in names
            ):
                raise ValueError("model-archive-path")
            names.add(item.filename)
        metadata = [
            item for item in entries if PurePosixPath(item.filename).name == "metadata.json"
        ]
        if len(metadata) != 1 or metadata[0].file_size > 65536:
            raise ValueError("model-metadata")
        description = json.loads(package.read(metadata[0]))
        source, target = pair.split("_")
        if (
            description.get("from_code") != source
            or description.get("to_code") != target
            or description.get("package_version") != "1.9"
        ):
            raise ValueError("model-direction")
        root = PurePosixPath(metadata[0].filename).parent
        for item in entries:
            path = PurePosixPath(item.filename)
            if item.is_dir() or not path.is_relative_to(root):
                continue
            relative = path.relative_to(root)
            # Keep the CT2 model and all package-level metadata/licenses. Stanza
            # dependencies are not part of TypeLay's actual translation engine.
            if len(relative.parts) > 1 and relative.parts[0] != "model":
                continue
            output = destination.joinpath(*relative.parts)
            output.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            with package.open(item) as source_file, output.open("xb") as target_file:
                shutil.copyfileobj(source_file, target_file, 65536)
        required = (
            "metadata.json",
            "sentencepiece.model",
            "model/config.json",
            "model/model.bin",
            "model/shared_vocabulary.json",
        )
        if not all((destination / item).is_file() for item in required):
            raise ValueError("model-layout")


def install(home, data, cache):
    for path in (data, cache):
        private_directory(path)
    components = private_directory(data / "components")
    download_cache = private_directory(cache / "components")
    lock = os.open(components / "install.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        os.close(lock)
        raise OSError("installation-already-running") from None
    version = private_directory(components / uuid.uuid4().hex)
    environment = {
        key: value
        for key, value in os.environ.items()
        if not key.startswith(("UV_", "PIP_", "PYTHON"))
    }
    environment.update(
        {
            "UV_PYTHON_INSTALL_DIR": str(version / "runtime/python"),
            "UV_CACHE_DIR": str(download_cache / "uv"),
            "UV_NO_PROGRESS": "1",
            "UV_NO_CONFIG": "1",
            "PYTHONNOUSERSITE": "1",
        }
    )
    active = False

    def run(arguments, timeout, capture=False):
        process = subprocess.Popen(
            list(map(str, arguments)),
            env=environment,
            stdout=subprocess.PIPE if capture else subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        try:
            output, _ = process.communicate(timeout=timeout)
            if process.returncode:
                raise subprocess.CalledProcessError(process.returncode, "component-setup")
            return output
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=5)

    def uv(*arguments, capture=False):
        result = run(["uv", *arguments, "--no-config"], 1200, capture)
        return result.decode().strip() if capture else None

    try:
        print("正在安装私有 CPython 3.13.16 运行时", flush=True)
        uv("python", "install", PYTHON, "--no-bin")
        python = Path(
            uv("python", "find", PYTHON, "--managed-python", "--no-python-downloads", capture=True)
        )
        if not python.resolve().is_relative_to(version):
            raise ValueError("python-installation-path")
        venv = version / "runtime/venv"
        uv("venv", venv, "--python", python, "--no-python-downloads")
        executable = venv / "bin/python"
        print("正在解析 Linux 依赖并验证 wheel 哈希", flush=True)
        requirements = version / "runtime/requirements.lock"
        uv(
            "pip",
            "compile",
            home / "worker/requirements.txt",
            "--python",
            executable,
            "--generate-hashes",
            "--only-binary",
            ":all:",
            "--index-url",
            "https://pypi.org/simple",
            "--output-file",
            requirements,
        )
        uv(
            "pip",
            "install",
            "--python",
            executable,
            "--require-hashes",
            "--only-binary",
            ":all:",
            "--index-url",
            "https://pypi.org/simple",
            "--requirements",
            requirements,
        )
        models = private_directory(version / "models")
        dependencies = []
        for pair in ("zh_en", "en_zh"):
            archive, url = download(pair, download_cache)
            try:
                extract_model(archive, models / pair, pair)
            except (ValueError, zipfile.BadZipFile):
                archive.unlink(missing_ok=True)
                raise
            # No upstream archive hash exists for these pinned Argos entries.
            # This is a recorded first-install TLS download hash, not a publisher pin.
            dependencies.append(
                {"name": pair, "version": "1.9", "url": url, "observedSha256": digest(archive)}
            )
        inventory = {
            "schema": 1,
            "pythonVersion": PYTHON,
            "dependencies": dependencies,
            "requirementsSha256": digest(requirements),
            "files": [
                {"path": path.relative_to(models).as_posix(), "sha256": digest(path)}
                for path in sorted(models.rglob("*"))
                if path.is_file()
            ],
        }
        (models / "installed.json").write_text(json.dumps(inventory, indent=2), encoding="utf-8")
        print("正在校验模型并执行真实中英双向 CPU 翻译自检", flush=True)
        result = run(
            [
                str(executable),
                "-I",
                "-B",
                str(home / "worker/run_worker.py"),
                "--models",
                str(models),
                "--self-check",
            ],
            timeout=180,
            capture=True,
        )
        if len(result) > 16384 or json.loads(result).get("status") != "passed":
            raise ValueError("model-self-check")
        (version / "self-check.json").write_bytes(result)
        pending = components / (".current-" + version.name)
        pending.symlink_to(version.name, target_is_directory=True)
        os.replace(pending, components / "current")
        active = True
        print("本地翻译组件安装完成，真实双向翻译自检通过。", flush=True)
        return 0
    finally:
        # A stop between the atomic rename and the next Python instruction must
        # never remove the newly active, fully verified version.
        if not active and (components / "current").resolve() != version.resolve():
            shutil.rmtree(version)
        (components / (".current-" + version.name)).unlink(missing_ok=True)
        os.close(lock)


if __name__ == "__main__":
    os.umask(0o077)
    try:
        supervised = len(sys.argv) == 5 and sys.argv[1] == "--supervised"
        arguments = sys.argv[2:] if supervised else sys.argv[1:]
        if len(arguments) != 3 or any(not Path(value).is_absolute() for value in arguments):
            raise ValueError("setup-arguments")
        if not supervised:
            # Exactly one process-family owner for GUI, CLI and explicit setup
            # invocations. Nested supervisors have independent sessions and
            # competing kill deadlines; installation children must inherit this
            # supervisor's group instead of creating another one.
            supervisor = str(Path(arguments[0]) / "native/typelay-supervise")
            os.execv(
                supervisor,
                [
                    supervisor,
                    sys.executable,
                    "-I",
                    "-B",
                    str(Path(__file__).resolve()),
                    "--supervised",
                    *arguments,
                ],
            )
        signal.signal(signal.SIGTERM, pause)
        signal.signal(signal.SIGINT, pause)
        raise SystemExit(install(*(Path(value) for value in arguments)))
    except Paused:
        print("安装已暂停，已验证的下载进度会保留。再次安装可继续，原版本保持不变。", flush=True)
        raise SystemExit(75) from None
    except (OSError, ValueError, subprocess.SubprocessError, zipfile.BadZipFile):
        print("组件安装失败，原安装保持不变。请检查网络、磁盘空间及 uv 是否已安装。", flush=True)
        raise SystemExit(1) from None
