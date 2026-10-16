"""Explicit-consent installation only. This process never receives user input text.

uv resolves Linux CPython wheels and verifies their hashes. A versioned environment
is built in its final location so absolute venv paths remain valid. The active
symlink changes only after the real offline worker passes both translation checks.
"""

import errno
import fcntl
import hashlib
import http.client
import json
import os
import re
import select
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


FAILURE_PREFIX = "TYPELAY_SETUP_FAILURE="
FAILURE_CODES = {"network", "disk-space", "integrity", "permission", "runtime", "unknown"}
PRESENTATION_UNAVAILABLE = "无法读取安装结果说明，请重新启动 TypeLay 后重试。"
FAILURE_STAGES = {
    "prepare",
    "runtime",
    "dependencies",
    "model-download",
    "model-extract",
    "model-verify",
    "self-check",
    "activate",
}


def setup_presentation(home, code=None):
    """Read shared copy from this package's host without starting a desktop or model."""
    executable = home / "app/TypeLay.Linux" if home is not None else None
    if executable is None or not executable.is_file():
        return None
    try:
        # Reading failure guidance must still work when the disk is full.
        process = subprocess.Popen(
            [str(executable), "--setup-presentation", code if code in FAILURE_CODES else "unknown"],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
        )
        try:
            encoded = bytearray()
            deadline = time.monotonic() + 3
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0 or not select.select([process.stdout], [], [], remaining)[0]:
                    return None
                part = os.read(process.stdout.fileno(), min(4096, 16385 - len(encoded)))
                if not part:
                    break
                encoded.extend(part)
                if len(encoded) > 16384:
                    return None
            if process.wait(timeout=max(0, deadline - time.monotonic())) != 0:
                return None
        finally:
            try:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=1)
            finally:
                process.stdout.close()
        value = json.loads(encoded)
        failure = value["failure"]
        if (
            failure["code"] not in FAILURE_CODES
            or any(
                not isinstance(failure[key], str) or not failure[key]
                for key in ("message", "nextStep", "retryLabel")
            )
            or any(
                not isinstance(value[key], str) or not value[key]
                for key in ("pausedMessage", "resumeLabel")
            )
        ):
            return None
        return value
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        return None


def failure_message(home, code):
    value = setup_presentation(home, code)
    if value is None:
        return PRESENTATION_UNAVAILABLE
    failure = value["failure"]
    return failure["message"] + "\n" + failure["nextStep"]


def failure_code(line, previous=None):
    """Keep the first valid failure, including unknown, if later cleanup also fails."""
    if previous is not None:
        return previous
    if not line.startswith(FAILURE_PREFIX) or len(line) > 1024:
        return None
    try:
        value = json.loads(line[len(FAILURE_PREFIX) :])
        if (
            not isinstance(value, dict)
            or value.get("category") not in FAILURE_CODES
            or value.get("stage") not in FAILURE_STAGES
            or set(value)
            - {"category", "stage", "errno", "exitCode", "dataFreeBytes", "cacheFreeBytes"}
            or any(
                type(number) is not int
                for key, number in value.items()
                if key not in ("category", "stage")
            )
        ):
            return None
        return value["category"]
    except (ValueError, TypeError):
        return None


def report_failure(error, stage="prepare", data=None, cache=None, home=None):
    cause = error.reason if isinstance(error, urllib.error.URLError) else error
    number = cause.errno if isinstance(cause, OSError) else None
    category = "unknown"
    if number in (errno.ENOSPC, errno.EDQUOT):
        category = "disk-space"
    elif number in (errno.EACCES, errno.EPERM, errno.EROFS):
        category = "permission"
    elif isinstance(
        error, (urllib.error.URLError, ConnectionError, TimeoutError, http.client.HTTPException)
    ) or (isinstance(error, OSError) and error.args == ("model-download-incomplete",)):
        category = "network"
    elif isinstance(error, zipfile.BadZipFile) or (
        isinstance(error, ValueError)
        and stage in ("model-download", "model-extract", "model-verify")
    ):
        category = "integrity"
    elif (
        isinstance(error, subprocess.SubprocessError)
        or (isinstance(error, ValueError) and stage == "self-check")
        or (isinstance(error, FileNotFoundError) and stage in ("runtime", "dependencies"))
    ):
        category = "runtime"
    diagnostic = {"category": category, "stage": stage}
    if type(number) is int:
        diagnostic["errno"] = number
    if isinstance(error, subprocess.CalledProcessError) and type(error.returncode) is int:
        diagnostic["exitCode"] = error.returncode
    for key, path in (("dataFreeBytes", data), ("cacheFreeBytes", cache)):
        if path is not None:
            try:
                diagnostic[key] = shutil.disk_usage(path).free
            except OSError:
                pass
    try:
        print(FAILURE_PREFIX + json.dumps(diagnostic, separators=(",", ":")), flush=True)
        print(failure_message(home, category), flush=True)
    except (OSError, Paused):
        # Once the failure is reported, a late pause cannot replace its outcome.
        pass


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


def model_catalog(home):
    # Source checkouts and installed packages have different layouts. Setup consumes
    # only the explicitly selected distribution, just like its worker subprocess.
    sys.path.insert(0, str(home / "worker"))
    from typelay_worker.catalog import load_catalog

    return load_catalog(home / "worker/model-catalog.json")


def download(direction, cache):
    pair, url = direction.id, direction.url
    archive = cache / f"{pair}-{direction.version}.argosmodel"
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


def extract_model(archive, destination, direction):
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
        if (
            description.get("from_code") != direction.source
            or description.get("to_code") != direction.target
            or description.get("package_version") != direction.version
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

    stage = "prepare"
    try:
        directions = model_catalog(home)
        stage = "runtime"
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
        stage = "dependencies"
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
        for direction in directions:
            pair = direction.id
            stage = "model-download"
            archive, url = download(direction, download_cache)
            stage = "model-extract"
            try:
                extract_model(archive, models / pair, direction)
            except (ValueError, zipfile.BadZipFile):
                archive.unlink(missing_ok=True)
                raise
            stage = "model-verify"
            # No upstream archive hash exists for these pinned Argos entries.
            # This is a recorded first-install TLS download hash, not a publisher pin.
            dependencies.append(
                {
                    "name": pair,
                    "version": direction.version,
                    "url": url,
                    "observedSha256": digest(archive),
                }
            )
        inventory = {
            "schema": 1,
            "directions": [direction.id for direction in directions],
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
        stage = "self-check"
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
        stage = "activate"
        pending = components / (".current-" + version.name)
        pending.symlink_to(version.name, target_is_directory=True)
        os.replace(pending, components / "current")
        active = True
        try:
            print("本地翻译组件安装完成，真实双向翻译自检通过。", flush=True)
        except OSError:
            pass
        return 0
    except Paused:
        raise
    except Exception as error:
        report_failure(error, stage, data, cache, home)
        return 1
    finally:
        # A stop between the atomic rename and the next Python instruction must
        # never remove the newly active, fully verified version.
        if not active and (components / "current").resolve() != version.resolve():
            shutil.rmtree(version)
        (components / (".current-" + version.name)).unlink(missing_ok=True)
        os.close(lock)


if __name__ == "__main__":
    os.umask(0o077)
    home = None
    try:
        supervised = len(sys.argv) == 5 and sys.argv[1] == "--supervised"
        arguments = sys.argv[2:] if supervised else sys.argv[1:]
        if len(arguments) != 3 or any(not Path(value).is_absolute() for value in arguments):
            raise ValueError("setup-arguments")
        home = Path(arguments[0])
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
        presentation = setup_presentation(home)
        print(
            presentation["pausedMessage"] if presentation else PRESENTATION_UNAVAILABLE, flush=True
        )
        raise SystemExit(75) from None
    except Exception as error:
        report_failure(error, home=home)
        raise SystemExit(1) from None
