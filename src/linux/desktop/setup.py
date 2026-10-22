"""Explicit-consent installation only. This process never receives user input text.

uv resolves Linux CPython wheels and verifies their hashes. A versioned environment
is built in its final location so absolute venv paths remain valid. The active
symlink is published by the host only after its serving candidate also handshakes.
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
import socket
import ssl
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


PREPARED_PREFIX = "TYPELAY_MODEL_PREPARED="

FAILURE_PREFIX = "TYPELAY_SETUP_FAILURE="
FAILURE_CODES = {"network", "disk-space", "integrity", "permission", "runtime", "unknown"}
DOWNLOAD_RESOURCES = {"argos-zh-en", "argos-en-zh", "hy-model", "hy-license", "unknown"}
NETWORK_REASONS = {"http", "dns", "tls", "timeout", "connection", "incomplete", "unknown"}
ARGOS_RESOURCES = {"zh_en": "argos-zh-en", "en_zh": "argos-en-zh"}
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
            - {
                "category",
                "stage",
                "errno",
                "exitCode",
                "dataFreeBytes",
                "cacheFreeBytes",
                "resource",
                "networkReason",
                "httpStatus",
            }
            or (
                "resource" in value
                and (
                    value["stage"] != "model-download"
                    or value["resource"] not in DOWNLOAD_RESOURCES
                )
            )
            or (
                "networkReason" in value
                and (
                    value["stage"] != "model-download"
                    or value["category"] != "network"
                    or value["networkReason"] not in NETWORK_REASONS
                )
            )
            or (
                "httpStatus" in value
                and (
                    value["stage"] != "model-download"
                    or value["category"] != "network"
                    or value.get("networkReason") != "http"
                    or type(value["httpStatus"]) is not int
                    or not 100 <= value["httpStatus"] <= 599
                )
            )
            or any(
                type(number) is not int
                for key, number in value.items()
                if key not in ("category", "stage", "resource", "networkReason")
            )
        ):
            return None
        return value["category"]
    except (ValueError, TypeError):
        return None


def report_failure(error, stage="prepare", data=None, cache=None, home=None, resource="unknown"):
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
    if stage == "model-download":
        diagnostic["resource"] = (
            resource if type(resource) is str and resource in DOWNLOAD_RESOURCES else "unknown"
        )
        if category == "network":
            reason = "unknown"
            if isinstance(error, urllib.error.HTTPError):
                reason = "http"
                if type(error.code) is int and 100 <= error.code <= 599:
                    diagnostic["httpStatus"] = error.code
            elif isinstance(cause, socket.gaierror):
                reason = "dns"
            elif isinstance(cause, ssl.SSLError):
                reason = "tls"
            elif isinstance(cause, TimeoutError):
                reason = "timeout"
            elif isinstance(cause, ConnectionError):
                reason = "connection"
            elif isinstance(cause, http.client.IncompleteRead) or (
                isinstance(error, OSError) and error.args == ("model-download-incomplete",)
            ):
                reason = "incomplete"
            diagnostic["networkReason"] = reason
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
    def __init__(self, hosts=("argos-net.com",)):
        super().__init__()
        self.hosts = frozenset(hosts)

    def redirect_request(self, request, fp, code, msg, headers, newurl):
        parsed = urllib.parse.urlsplit(newurl)
        if (
            parsed.scheme != "https"
            or parsed.hostname not in self.hosts
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
    return download_resource(
        direction.id,
        direction.url,
        cache / f"{direction.id}-{direction.version}.argosmodel",
        MAX_ARCHIVE,
    )


def download_resource(pair, url, archive, limit, *, expected=None, hosts=("argos-net.com",)):
    if (
        archive.is_file()
        and not archive.is_symlink()
        and archive.stat().st_nlink == 1
        and 0 < archive.stat().st_size <= limit
    ):
        if expected is None or (
            archive.stat().st_size == expected["bytes"] and digest(archive) == expected["sha256"]
        ):
            return archive, url
        archive.unlink()
    elif archive.exists() or archive.is_symlink():
        raise ValueError("model-cache-path")
    partial = archive.with_suffix(".part")
    metadata = partial.with_suffix(".part.json")

    def discard():
        partial.unlink(missing_ok=True)
        metadata.unlink(missing_ok=True)
        metadata.with_suffix(".tmp").unlink(missing_ok=True)

    offset, saved, checksum = 0, {}, hashlib.sha256()
    try:
        if (
            partial.is_symlink()
            or metadata.is_symlink()
            or metadata.stat().st_size > 4096
            or partial.stat().st_nlink != 1
            or metadata.stat().st_nlink != 1
        ):
            raise ValueError("model-resume-metadata")
        saved = json.loads(metadata.read_text(encoding="utf-8"))
        offset = saved["size"]
        if (
            saved["url"] != url
            or type(offset) is not int
            or type(saved["total"]) is not int
            or not 0 < offset <= saved["total"] <= limit
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

    opener = urllib.request.build_opener(Redirects(hosts))
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
            try:
                error.close()
            except Exception:
                # Response cleanup must not replace the original network failure.
                raise error from None
            if error.code != 416 or not offset or attempt:
                raise
            discard()
            offset, saved, checksum = 0, {}, hashlib.sha256()
            continue
        with response:
            length = int(response.headers.get("Content-Length", "0"))
            if (
                length < 0
                or length > limit
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
                if os.fstat(output.fileno()).st_nlink != 1:
                    raise ValueError("model-cache-path")
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
                    fd = os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_NOFOLLOW, 0o600)
                    with os.fdopen(fd, "w", encoding="utf-8") as stream:
                        if os.fstat(stream.fileno()).st_nlink != 1:
                            raise ValueError("model-cache-path")
                        stream.truncate(0)
                        json.dump(value, stream)
                        stream.flush()
                        os.fsync(stream.fileno())
                    pending.replace(metadata)

                try:
                    while block := response.read(65536):
                        if count + len(block) > limit or time.monotonic() - started > 900:
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
            if expected is not None and (
                count != expected["bytes"] or checksum.hexdigest() != expected["sha256"]
            ):
                discard()
                raise ValueError("model-fixed-hash")
            partial.replace(archive)
            metadata.unlink(missing_ok=True)
            return archive, url
    raise OSError("model-download-retry")


def extract_model(archive, destination, direction):
    with zipfile.ZipFile(archive) as package:
        entries = package.infolist()
        declared_bytes = sum(item.file_size for item in entries)
        if len(entries) > 30000 or declared_bytes > 2 * MAX_ARCHIVE:
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
        retained_bytes = 0
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
                retained_bytes += target_file.tell()
        required = (
            "metadata.json",
            "sentencepiece.model",
            "model/config.json",
            "model/model.bin",
            "model/shared_vocabulary.json",
        )
        if not all((destination / item).is_file() for item in required):
            raise ValueError("model-layout")
        # Observed archive bytes, including cache hits, are not a publisher pin.
        # Diagnostics are returned only after the existing ZIP/layout gates pass.
        return {
            "observedSha256": digest(archive),
            "compressedBytes": archive.stat().st_size,
            "declaredUnpackedBytes": declared_bytes,
            "retainedBytes": retained_bytes,
        }


def argos_archive_observation(direction, sizes):
    return {
        "packageId": ARGOS_RESOURCES[direction.id],
        "version": direction.version,
        "catalogUrl": direction.url,
        "provenance": "observed-local-archive-not-publisher-verified",
        **sizes,
    }


def retained_argos_observation(value, direction):
    # Optional diagnostics never grant identity, Ready, or space-budget credit.
    # Carry only our bounded, path-free shape; do not propagate arbitrary fields
    # from an editable old inventory into a later diagnostic artifact.
    if not isinstance(value, dict):
        return None
    sizes = {
        key: value.get(key)
        for key in ("observedSha256", "compressedBytes", "declaredUnpackedBytes", "retainedBytes")
    }
    if (
        value != argos_archive_observation(direction, sizes)
        or not isinstance(sizes["observedSha256"], str)
        or not re.fullmatch(r"[0-9a-f]{64}", sizes["observedSha256"])
        or any(
            type(sizes[key]) is not int
            for key in ("compressedBytes", "declaredUnpackedBytes", "retainedBytes")
        )
    ):
        return None
    if not (
        0 < sizes["compressedBytes"] <= MAX_ARCHIVE
        and 0 < sizes["retainedBytes"] <= sizes["declaredUnpackedBytes"] <= 2 * MAX_ARCHIVE
    ):
        return None
    return value.copy()


def argos_archive_summary(dependencies, directions):
    """Path-free successful-install diagnostics; never a readiness decision."""
    result = []
    keys = (
        "packageId",
        "version",
        "catalogUrl",
        "provenance",
        "observedSha256",
        "compressedBytes",
        "declaredUnpackedBytes",
        "retainedBytes",
    )
    for direction in directions:
        matches = [
            item
            for item in dependencies
            if isinstance(item, dict) and item.get("name") == direction.id
        ]
        observation = retained_argos_observation(
            {key: matches[0].get(key) for key in keys} if len(matches) == 1 else None,
            direction,
        )
        result.append(
            {"status": "recorded", **observation}
            if observation is not None
            else {
                "packageId": ARGOS_RESOURCES[direction.id],
                "version": direction.version,
                "status": "missing",
            }
        )
    return result


def legacy_directions(value, catalog):
    available = tuple(direction.id for direction in catalog)
    if (
        not isinstance(value, dict)
        or type(value.get("schema")) is not int
        or value["schema"] not in (1, 2)
    ):
        raise ValueError("model-inventory")
    if value["schema"] == 1 and "directions" not in value:
        return available
    selected = value.get("directions")
    if (
        not isinstance(selected, list)
        or not selected
        or any(not isinstance(item, str) for item in selected)
        or len(set(selected)) != len(selected)
        or not set(selected).issubset(available)
        or value["schema"] == 1
        and set(selected) != set(available)
    ):
        raise ValueError("model-inventory")
    return tuple(item for item in available if item in selected)


def installation_directions(models, catalog):
    available = tuple(direction.id for direction in catalog)
    for path in (models.parent, models, models / "installed.json"):
        if path.is_symlink():
            raise ValueError("model-inventory")
    marker = models / "installed.json"
    metadata = marker.stat()
    if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > 8 * 1024 * 1024:
        raise ValueError("model-inventory")
    selected = legacy_directions(read_metadata(marker, 8 * 1024 * 1024), catalog)
    activation = models.parent / "activation.json"
    try:
        metadata = activation.lstat()
    except FileNotFoundError:
        recorded = available  # Only an absent legacy record implies full intent.
    else:
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > 4096:
            raise ValueError("model-activation")
        value = read_metadata(activation, 4096)
        recorded = legacy_directions(value, catalog)
        if set(value) != (
            {"schema", "previous"} if value["schema"] == 1 else {"schema", "previous", "directions"}
        ):
            raise ValueError("model-activation")
        previous = value["previous"]
        if previous is not None and (
            not isinstance(previous, str)
            or not re.fullmatch(r"[0-9a-f]{32}", previous)
            or previous == models.parent.name
        ):
            raise ValueError("model-activation")
    if selected != recorded:
        raise ValueError("model-directions")
    return selected


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("model-duplicate-field")
        value[key] = item
    return value


def read_metadata(path, limit):
    if path.is_symlink():
        raise ValueError("model-metadata")
    info = path.stat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > limit:
        raise ValueError("model-metadata")
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def package_catalog(home):
    sys.path.insert(0, str(home / "worker"))
    from typelay_worker.catalog import load_package_catalog

    return load_package_catalog(home / "worker/model-catalog.json")


def hy_assets(home):
    # Resolve this distribution explicitly; a previously imported worker must not
    # make another distribution's closure appear available.
    import importlib.util

    module = importlib.util.spec_from_file_location(
        "typelay_setup_hy_assets", home / "worker/typelay_worker/hy_assets.py"
    )
    if module is None or module.loader is None:
        raise ValueError("hy-native-unavailable")
    assets = importlib.util.module_from_spec(module)
    module.loader.exec_module(assets)
    return assets


def check_hy_runtime(home):
    try:
        hy_assets(home).verify_native()
        return {"available": True, "reason": None}
    except (OSError, ValueError, KeyError, TypeError, ImportError):
        return {"available": False, "reason": "native-unavailable"}


def current_generation(components):
    current = components / "current"
    if current.is_symlink():
        value = os.readlink(current)
        if not re.fullmatch(r"[0-9a-f]{32}", value):
            raise ValueError("model-current")
        root = components / value
        if root.is_symlink() or not root.is_dir():
            raise ValueError("model-current")
        return value
    if current.exists():
        raise ValueError("model-current")
    return None


def package_identity(selection):
    return {
        "id": selection.package.id,
        "version": selection.package.version,
        "directions": [direction.id for direction in selection.directions],
    }


def read_package_state(root, catalog):
    from typelay_worker.catalog import read_active_selection, read_installation_selection

    for directory in (root, root / "models"):
        if directory.is_symlink() or not directory.is_dir():
            raise ValueError("model-inventory")
    inventory = read_metadata(root / "models/installed.json", 8 * 1024 * 1024)
    installed = read_installation_selection(inventory, catalog)
    active = read_active_selection(inventory, catalog)
    if inventory["schema"] != 3:
        installation_directions(root / "models", catalog.legacy_directions)
        return inventory, installed, active, None
    activation = read_metadata(root / "activation.json", 16384)
    if (
        set(activation)
        != {"schema", "previous", "packages", "operation", "removedPackages", "recoveryPrevious"}
        or type(activation["schema"]) is not int
        or activation["schema"] != 3
        or activation["operation"] not in ("Install", "Repair", "Select", "Remove")
    ):
        raise ValueError("model-activation")
    for name in ("previous", "recoveryPrevious"):
        value = activation[name]
        if value is not None and (
            not isinstance(value, str)
            or not re.fullmatch(r"[0-9a-f]{32}", value)
            or value == root.name
        ):
            raise ValueError("model-activation")

    def identities(entries):
        if not isinstance(entries, list):
            raise ValueError("model-activation")
        result = {}
        for item in entries:
            if not isinstance(item, dict) or set(item) != {"id", "version", "directions"}:
                raise ValueError("model-activation")
            package = next(
                (package for package in catalog.packages if package.id == item["id"]), None
            )
            ids = item["directions"]
            if (
                package is None
                or item["version"] != package.version
                or package.id in result
                or not isinstance(ids, list)
                or not ids
                or any(not isinstance(direction, str) for direction in ids)
                or len(set(ids)) != len(ids)
                or not set(ids).issubset(direction.id for direction in package.directions)
            ):
                raise ValueError("model-activation")
            result[package.id] = (package.version, frozenset(ids))
        return result

    if identities(activation["packages"]) != identities(
        [package_identity(item) for item in active]
    ):
        raise ValueError("model-activation")
    removed = identities(activation["removedPackages"])
    if activation["operation"] == "Remove":
        if (
            len(removed) != 1
            or activation["recoveryPrevious"] is not None
            or set(removed) & {item.package.id for item in installed}
        ):
            raise ValueError("model-activation")
    elif removed or activation["recoveryPrevious"] != activation["previous"]:
        raise ValueError("model-activation")
    return inventory, installed, active, activation


def validate_package_request(request, catalog):
    if (
        not isinstance(request, dict)
        or set(request)
        != {"format", "operation", "packageIds", "activePackages", "expectedCurrent"}
        or type(request["format"]) is not int
        or request["format"] != 1
        or request["operation"] not in ("Install", "Repair", "Select", "Remove")
    ):
        raise ValueError("model-package-request")
    known = {package.id for package in catalog.packages}
    for key in ("packageIds", "activePackages"):
        ids = request[key]
        if (
            not isinstance(ids, list)
            or any(not isinstance(item, str) or item not in known for item in ids)
            or len(ids) != len(set(ids))
        ):
            raise ValueError("model-package-request")
    if (
        len(
            {package.backend for package in catalog.packages if package.id in request["packageIds"]}
        )
        > 1
    ):
        raise ValueError("model-package-request")
    selected = [package for package in catalog.packages if package.id in request["activePackages"]]
    if not selected or not (
        all(package.backend == "argos" for package in selected)
        or len(selected) == 1
        and selected[0].backend == "hy-mt-llama-cpu"
    ):
        raise ValueError("model-package-request")
    previous = request["expectedCurrent"]
    if previous is not None and (
        not isinstance(previous, str) or not re.fullmatch(r"[0-9a-f]{32}", previous)
    ):
        raise ValueError("model-package-request")
    operation, ids = request["operation"], request["packageIds"]
    if (
        (operation == "Select" and ids)
        or (operation != "Select" and not ids)
        or (operation == "Remove" and len(ids) != 1)
    ):
        raise ValueError("model-package-request")


def plain_files(root):
    if any(path.is_symlink() for path in (root, *root.parents)) or not root.is_dir():
        raise ValueError("model-package-path")
    result = []
    for path in sorted(root.rglob("*")):
        info = path.lstat()
        if stat.S_ISDIR(info.st_mode):
            continue
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
            raise ValueError("model-package-path")
        result.append(path)
    return result


def copy_package(source, destination, files):
    expected = {item.path: item.sha256 for item in files}
    actual = plain_files(source)
    if {path.relative_to(source).as_posix() for path in actual} != set(expected):
        raise ValueError("model-package-files")
    for path in actual:
        relative = path.relative_to(source)
        if digest(path) != expected[relative.as_posix()]:
            raise ValueError("model-package-hash")
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        shutil.copyfile(path, target)
        if digest(target) != expected[relative.as_posix()]:
            raise ValueError("model-package-hash")


def runtime_copy_plan(root):
    if any(path.is_symlink() for path in (root, *root.parents)) or not root.is_dir():
        raise ValueError("python-runtime-path")
    root = root.resolve(strict=True)
    result = []

    def walk(path, relative, ancestors):
        resolved = path.resolve(strict=True)
        if not resolved.is_relative_to(root) or resolved in ancestors:
            raise ValueError("python-runtime-path")
        info = resolved.stat()
        if stat.S_ISDIR(info.st_mode):
            for child in sorted(resolved.iterdir()):
                walk(child, relative / child.name, ancestors | {resolved})
        elif stat.S_ISREG(info.st_mode):
            result.append((resolved, relative, stat.S_IMODE(info.st_mode)))
        else:
            raise ValueError("python-runtime-path")

    walk(root, Path(), set())
    return result


def copy_runtime(source, destination, plan):
    for path, relative, mode in plan:
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        shutil.copyfile(path, target)
        target.chmod(mode & 0o700)
    # Managed CPython is relocatable. venv metadata, entry points and activation
    # scripts are not: rewrite only bounded text in the copied venv, never binaries.
    venv = destination / "venv"
    candidates = [venv / "pyvenv.cfg", *(venv / "bin").iterdir()]
    for path in candidates:
        if path.is_file() and path.stat().st_size <= 1024 * 1024:
            try:
                text = path.read_text(encoding="utf-8")
            except UnicodeError:
                continue
            path.write_text(text.replace(str(source), str(destination)), encoding="utf-8")
    executable = venv / "bin/python"
    result = subprocess.run(
        [
            str(executable),
            "-I",
            "-B",
            "-c",
            "import json,sys,ctranslate2,sentencepiece; print(json.dumps({'prefix':sys.prefix,'basePrefix':sys.base_prefix,'dependencies':[ctranslate2.__file__,sentencepiece.__file__]}))",
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=60,
        env={
            key: value
            for key, value in os.environ.items()
            if not key.startswith(("UV_", "PIP_", "PYTHON"))
        },
        check=True,
    )
    if len(result.stdout) > 16384:
        raise ValueError("python-runtime-path")
    try:
        value = json.loads(result.stdout)
        dependencies = value.get("dependencies")
        if (
            Path(value["prefix"]) != venv
            or not Path(value["basePrefix"]).is_relative_to(destination / "python")
            or not isinstance(dependencies, list)
            or len(dependencies) != 2
            or any(
                not Path(path).resolve(strict=True).is_relative_to(destination)
                for path in dependencies
            )
        ):
            raise ValueError("python-runtime-path")
        if not (destination / "requirements.lock").is_file():
            raise ValueError("python-runtime-path")
    except (ValueError, TypeError, KeyError) as error:
        raise ValueError("python-runtime-path") from error
    return executable, destination / "requirements.lock"


def prepare_runtime(home, version, uv, set_stage):
    set_stage("runtime")
    print("正在安装私有 CPython 3.13.16 运行时", flush=True)
    uv("python", "install", PYTHON, "--no-bin")
    python = Path(
        uv("python", "find", PYTHON, "--managed-python", "--no-python-downloads", capture=True)
    ).resolve(strict=True)
    if not python.is_relative_to(version):
        raise ValueError("python-installation-path")
    venv = version / "runtime/venv"
    uv("venv", venv, "--python", python, "--no-python-downloads")
    executable = venv / "bin/python"
    # uv may still emit its managed-directory alias. Normalize only this
    # unpublished venv's link to the exact interpreter already validated.
    resolved = executable.resolve(strict=True)
    if (
        not executable.is_symlink()
        or resolved != python
        or not resolved.is_relative_to(version)
        or not executable.samefile(python)
    ):
        raise ValueError("python-venv-path")
    executable.unlink()
    executable.symlink_to(python)
    set_stage("dependencies")
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
    return executable, requirements


def install_packages(home, data, cache, request):
    catalog = package_catalog(home)
    validate_package_request(request, catalog)
    for path in (data, cache):
        private_directory(path)
    components = private_directory(data / "components")
    download_cache = private_directory(cache / "components")
    lock = os.open(components / "install.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    version, prepared, stage = None, False, "prepare"
    resource = "unknown"
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        previous = current_generation(components)
        if previous != request["expectedCurrent"]:
            raise ValueError("model-preparation-stale")
        old_inventory, installed, active = None, (), ()
        if previous is not None:
            old_inventory, installed, active, _ = read_package_state(components / previous, catalog)
        old = {item.package.id: item for item in installed}
        ids, operation = set(request["packageIds"]), request["operation"]
        if (
            operation == "Install"
            and ids & set(old)
            or operation in ("Repair", "Remove")
            and not ids.issubset(old)
            or operation == "Remove"
            and ids & {item.package.id for item in active}
            or operation in ("Repair", "Remove")
            and set(request["activePackages"]) != {item.package.id for item in active}
        ):
            raise ValueError("model-package-request")
        retained = set(old) - (ids if operation == "Remove" else set())
        result_ids = retained | (ids if operation == "Install" else set())
        if not set(request["activePackages"]).issubset(result_ids):
            raise ValueError("model-package-request")
        selected = [package for package in catalog.packages if package.id in result_ids]
        downloading_hy = any(
            package.backend == "hy-mt-llama-cpu" and package.id in ids for package in selected
        ) and operation in ("Install", "Repair")
        if downloading_hy or any(
            package.backend == "hy-mt-llama-cpu" and package.id in request["activePackages"]
            for package in selected
        ):
            hy_assets(home).verify_native()
        source_runtime = components / previous / "runtime" if previous is not None else None
        may_rebuild_runtime = operation in ("Install", "Repair")

        def rebuildable(error):
            return may_rebuild_runtime and (
                isinstance(error, (FileNotFoundError, subprocess.CalledProcessError))
                or isinstance(error, ValueError)
                and error.args == ("python-runtime-path",)
                or isinstance(error, OSError)
                and error.errno == errno.ENOEXEC
            )

        runtime_plan = None
        if source_runtime is not None:
            try:
                runtime_plan = runtime_copy_plan(source_runtime)
            except Exception as error:
                if not rebuildable(error):
                    raise
        copied_runtime_bytes = (
            sum(path.stat().st_size for path, _, _ in runtime_plan)
            if runtime_plan is not None
            else 0
        )
        # Install/Repair may rebuild a damaged private runtime. Reserve that
        # fallback before copying anything; offline operations cannot enter it.
        runtime_budget = 1024 * 1024 * 1024 if may_rebuild_runtime else 0
        required_data = max(copied_runtime_bytes, runtime_budget)
        required_cache = runtime_budget
        fixed = None
        if downloading_hy:
            fixed = hy_assets(home).read_json(home / "worker/native/hy/model.lock.json")
            package = next(package for package in selected if package.backend == "hy-mt-llama-cpu")
            if (
                fixed["package_id"] != package.id
                or fixed["version"] != package.version
                or fixed["directions"] != [direction.id for direction in package.directions]
            ):
                raise ValueError("model-fixed-identity")
        for item in installed:
            if item.package.id in retained and not (
                operation == "Repair" and item.package.id in ids
            ):
                source = components / previous / "models"
                source /= (
                    (Path("packages") / item.package.id / item.package.version)
                    if old_inventory["schema"] == 3
                    else Path(item.directions[0].id)
                )
                required_data += sum(path.stat().st_size for path in plain_files(source))
        if operation in ("Install", "Repair"):
            for package in selected:
                if package.id not in ids:
                    continue
                size = (
                    sum(fixed[key]["bytes"] for key in ("model", "model_license"))
                    if package.backend == "hy-mt-llama-cpu"
                    else MAX_ARCHIVE
                )
                required_cache += size
                required_data += size if package.backend == "hy-mt-llama-cpu" else 2 * MAX_ARCHIVE
        margin = 64 * 1024 * 1024
        same_disk = data.stat().st_dev == cache.stat().st_dev
        if (
            shutil.disk_usage(data).free
            < required_data + (required_cache if same_disk else 0) + margin
            or not same_disk
            and shutil.disk_usage(cache).free < required_cache + margin
        ):
            raise OSError(errno.ENOSPC, "model-space")
        print(
            f"本次最多新增组件 {required_data // (1024 * 1024)} MiB、下载缓存 {required_cache // (1024 * 1024)} MiB；准备期间保留当前组件。",
            flush=True,
        )
        version = private_directory(components / uuid.uuid4().hex)
        stage = "runtime"
        requirements = None
        if runtime_plan is not None:
            try:
                _, requirements = copy_runtime(source_runtime, version / "runtime", runtime_plan)
            except Exception as error:
                if not rebuildable(error):
                    raise
                if (version / "runtime").exists():
                    shutil.rmtree(version / "runtime")
        if requirements is None:
            if not may_rebuild_runtime:
                raise ValueError("python-runtime-path")
            environment = {
                key: value
                for key, value in os.environ.items()
                if not key.startswith(("UV_", "PIP_", "PYTHON"))
            }
            environment.update(
                UV_PYTHON_INSTALL_DIR=str(version / "runtime/python"),
                UV_CACHE_DIR=str(download_cache / "uv"),
                UV_NO_PROGRESS="1",
                UV_NO_CONFIG="1",
                PYTHONNOUSERSITE="1",
                UV_LINK_MODE="copy",
            )

            def uv(*arguments, capture=False):
                process = subprocess.Popen(
                    ["uv", *map(str, arguments), "--no-config"],
                    env=environment,
                    stdout=subprocess.PIPE if capture else subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                )
                try:
                    output, _ = process.communicate(timeout=1200)
                    if process.returncode:
                        raise subprocess.CalledProcessError(process.returncode, "component-setup")
                    return output.decode().strip() if capture else None
                finally:
                    if process.poll() is None:
                        process.terminate()
                        process.wait(timeout=5)

            def set_stage(value):
                nonlocal stage
                stage = value

            _, requirements = prepare_runtime(home, version, uv, set_stage)
        stage = "model-verify"
        models = private_directory(version / "models")
        packages = []
        from typelay_worker.catalog import ModelFile, read_file_manifest

        for package in selected:
            observation = None
            destination = models / "packages" / package.id / package.version
            replacing = operation in ("Install", "Repair") and package.id in ids
            if replacing:
                directions = (
                    old[package.id].directions if operation == "Repair" else package.directions
                )
                if package.backend == "argos":
                    stage = "model-download"
                    resource = ARGOS_RESOURCES.get(package.directions[0].id, "unknown")
                    archive, _ = download(package.directions[0], download_cache)
                    stage = "model-extract"
                    try:
                        sizes = extract_model(archive, destination, package.directions[0])
                        observation = argos_archive_observation(package.directions[0], sizes)
                    except (ValueError, zipfile.BadZipFile):
                        archive.unlink(missing_ok=True)
                        raise
                else:
                    package_cache = private_directory(
                        private_directory(download_cache / package.id) / package.version
                    )
                    destination.mkdir(parents=True, mode=0o700)
                    for kind in ("model", "model_license"):
                        record = fixed[kind]
                        url = (
                            package.url
                            if kind == "model"
                            else package.url.rsplit("/", 1)[0] + "/LICENSE.txt"
                        )
                        stage = "model-download"
                        resource = "hy-model" if kind == "model" else "hy-license"
                        archive, _ = download_resource(
                            package.id,
                            url,
                            package_cache / record["file"],
                            record["bytes"] + 65536,
                            expected=record,
                            hosts=(
                                "huggingface.co",
                                "cdn-lfs.huggingface.co",
                                "cdn-lfs.hf.co",
                                "cdn-lfs-us-1.hf.co",
                                "cdn-lfs-eu-1.hf.co",
                                "cas-bridge.xethub.hf.co",
                                "us.aws.cdn.hf.co",
                                "eu.aws.cdn.hf.co",
                            ),
                        )
                        shutil.copyfile(archive, destination / record["file"])
                    stage = "model-verify"
                    for kind in ("model", "model_license"):
                        record = fixed[kind]
                        hy_assets(home).verify_file(destination / record["file"], record)
                files = tuple(
                    ModelFile(path.relative_to(destination).as_posix(), digest(path))
                    for path in plain_files(destination)
                )
                identity = {
                    "id": package.id,
                    "version": package.version,
                    "directions": [direction.id for direction in directions],
                }
            else:
                item = old[package.id]
                source = components / previous / "models"
                if old_inventory["schema"] == 3:
                    source /= Path("packages") / package.id / package.version
                    files = item.files
                else:
                    direction = item.directions[0].id
                    source /= direction
                    files = tuple(
                        ModelFile(file.path[len(direction) + 1 :], file.sha256)
                        for file in read_file_manifest(old_inventory["files"])
                        if file.path.startswith(direction + "/")
                    )
                stage = "model-verify"
                copy_package(source, destination, files)
                identity = package_identity(item)
                if package.backend == "argos" and old_inventory["schema"] == 3:
                    entry = next(
                        entry for entry in old_inventory["packages"] if entry["id"] == package.id
                    )
                    observation = retained_argos_observation(
                        entry.get("archiveObservation"), package.directions[0]
                    )
            packages.append(
                {
                    **identity,
                    **({"archiveObservation": observation} if observation is not None else {}),
                    "files": [{"path": file.path, "sha256": file.sha256} for file in files],
                }
            )
        inventory = {
            "schema": 3,
            "packages": packages,
            "activePackages": request["activePackages"],
            "pythonVersion": PYTHON,
            "requirementsSha256": digest(requirements),
        }
        from typelay_worker.catalog import read_active_selection

        chosen = read_active_selection(inventory, catalog)
        (models / "installed.json").write_text(json.dumps(inventory, indent=2), encoding="utf-8")
        activation = {
            "schema": 3,
            "previous": previous,
            "packages": [package_identity(item) for item in chosen],
            "operation": operation,
            "removedPackages": [package_identity(old[identity]) for identity in ids]
            if operation == "Remove"
            else [],
            "recoveryPrevious": None if operation == "Remove" else previous,
        }
        with (version / "activation.json").open("x", encoding="utf-8") as stream:
            json.dump(activation, stream)
            stream.flush()
            os.fsync(stream.fileno())
        for directory in (version, components):
            fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            try:
                os.fsync(fd)
            finally:
                os.close(fd)
        print(PREPARED_PREFIX + version.name, flush=True)
        prepared = True
        return 0
    except Paused:
        raise
    except Exception as error:
        report_failure(error, stage, data, cache, home, resource)
        return 1
    finally:
        try:
            if version is not None and not prepared:
                shutil.rmtree(version)
        finally:
            os.close(lock)


def cleanup_packages(home, data, cache):
    """Discard only fixed caches outside every current/recovery inventory.

    Called after confirmed activation and after that lease has been released.
    Any unknown reference leaves every cache alone; no newest-directory inference.
    """
    components = private_directory(data / "components")
    download_cache = private_directory(cache / "components")
    lock = os.open(components / "install.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        catalog = package_catalog(home)
        current = current_generation(components)
        if current is None:
            return False
        referenced, generations = set(), set()
        generation = current
        while generation is not None:
            if generation in generations:
                return False
            generations.add(generation)
            inventory, installed, _, activation = read_package_state(
                components / generation, catalog
            )
            referenced.update((item.package.id, item.package.version) for item in installed)
            if inventory["schema"] == 3:
                generation = activation["recoveryPrevious"]
            else:
                path = components / generation / "activation.json"
                generation = read_metadata(path, 16384)["previous"] if path.exists() else None
        candidates, folders = [], []
        for package in catalog.packages:
            if (package.id, package.version) in referenced:
                continue
            if package.backend == "argos":
                archive = (
                    download_cache / f"{package.directions[0].id}-{package.version}.argosmodel"
                )
                candidates += [
                    archive,
                    archive.with_suffix(".part"),
                    archive.with_suffix(".part.json"),
                    archive.with_suffix(".part.tmp"),
                ]
            else:
                folder = download_cache / package.id / package.version
                if folder.is_symlink() or folder.parent.is_symlink():
                    return False
                fixed = hy_assets(home).read_json(home / "worker/native/hy/model.lock.json")
                if fixed["package_id"] != package.id or fixed["version"] != package.version:
                    return False
                for kind in ("model", "model_license"):
                    archive = folder / fixed[kind]["file"]
                    candidates += [
                        archive,
                        archive.with_suffix(".part"),
                        archive.with_suffix(".part.json"),
                        archive.with_suffix(".part.tmp"),
                    ]
                folders += [folder, folder.parent]
        for path in candidates:
            if path.is_symlink() or path.exists() and not path.is_file():
                return False
        if current_generation(components) != current:
            return False
        for path in candidates:
            path.unlink(missing_ok=True)
        for folder in folders:
            if folder.is_dir() and not any(folder.iterdir()):
                folder.rmdir()
        return True
    except (OSError, ValueError, KeyError, TypeError):
        return False
    finally:
        os.close(lock)


def install(home, data, cache, requested_directions=None, repair=False):
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
    try:
        version = private_directory(components / uuid.uuid4().hex)
    except BaseException:
        os.close(lock)
        raise
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
    prepared = False

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
    resource = "unknown"
    try:
        current = components / "current"
        previous = os.readlink(current) if current.is_symlink() else None
        if (previous is not None and not re.fullmatch(r"[0-9a-f]{32}", previous)) or (
            previous is None and current.exists()
        ):
            raise ValueError("model-current")
        catalog = model_catalog(home)
        available = tuple(direction.id for direction in catalog)
        requested = available if requested_directions is None else tuple(requested_directions)
        if (
            not requested
            or any(not isinstance(item, str) for item in requested)
            or len(set(requested)) != len(requested)
            or not set(requested).issubset(available)
        ):
            raise ValueError("model-directions")
        stage = "model-verify"
        committed = ()
        if previous is not None:
            try:
                committed = installation_directions(components / previous / "models", catalog)
            except (ValueError, FileNotFoundError):
                legacy_known = False
                for marker, limit in (
                    (components / previous / "models/installed.json", 8 * 1024 * 1024),
                    (components / previous / "activation.json", 16384),
                ):
                    try:
                        record = read_metadata(marker, limit)
                    except (ValueError, FileNotFoundError):
                        continue
                    if (
                        not isinstance(record, dict)
                        or record.get("schema") == 3
                        or "packages" in record
                        or "activePackages" in record
                    ):
                        raise OSError("model-package-repair-required")
                    try:
                        legacy_directions(record, catalog)
                        if marker.name == "activation.json":
                            expected = {"schema", "previous"}
                            if record["schema"] == 2:
                                expected.add("directions")
                            prior = record.get("previous")
                            if (
                                set(record) != expected
                                or prior is not None
                                and (
                                    not isinstance(prior, str)
                                    or not re.fullmatch(r"[0-9a-f]{32}", prior)
                                    or prior == previous
                                )
                            ):
                                raise ValueError("model-activation")
                        legacy_known = True
                    except ValueError:
                        pass
                if not legacy_known:
                    raise OSError("model-installation-unknown")
                if not repair or set(requested) != set(available):
                    raise
        selected = set(committed) | set(requested)
        directions = tuple(direction for direction in catalog if direction.id in selected)
        schema = 1 if len(directions) == len(catalog) else 2

        def set_stage(value):
            nonlocal stage
            stage = value

        executable, requirements = prepare_runtime(home, version, uv, set_stage)
        models = private_directory(version / "models")
        dependencies = []
        for direction in directions:
            pair = direction.id
            stage = "model-download"
            resource = ARGOS_RESOURCES.get(direction.id, "unknown")
            archive, url = download(direction, download_cache)
            stage = "model-extract"
            try:
                sizes = extract_model(archive, models / pair, direction)
            except (ValueError, zipfile.BadZipFile):
                archive.unlink(missing_ok=True)
                raise
            stage = "model-verify"
            # Catalog URL names the requested artifact, not proven cache origin.
            # Existing archive bytes are observed, never publisher-verified.
            dependencies.append(
                {
                    "name": pair,
                    "version": direction.version,
                    "url": url,
                    **argos_archive_observation(direction, sizes),
                }
            )
        inventory = {
            "schema": schema,
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
        print("正在校验所选模型并执行真实 CPU 翻译自检", flush=True)
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
        if len(result) > 16384:
            raise ValueError("model-self-check")
        check = json.loads(result)
        results = check.get("results") if isinstance(check, dict) else None
        if (
            not isinstance(results, list)
            or check.get("status") != "passed"
            or check.get("device") != "cpu"
            or len(results) != len(directions)
            or any(
                not isinstance(item, dict)
                or not isinstance(item.get("direction"), str)
                or item.get("status") != "translated"
                or item.get("nonempty") is not True
                or item.get("differs") is not True
                for item in results
            )
            or sorted(item.get("direction", "") for item in results)
            != sorted(direction.id for direction in directions)
        ):
            raise ValueError("model-self-check")
        # Existing CI collects this successful self-check. Export only bounded
        # observations, never the inventory's paths or arbitrary metadata.
        check["archiveObservations"] = argos_archive_summary(inventory["dependencies"], directions)
        (version / "self-check.json").write_text(json.dumps(check), encoding="utf-8")
        # Prepare records the exact predecessor under the installation flock.
        # The host rechecks it after reacquiring that same lock, before hello.
        with (version / "activation.json").open("x", encoding="utf-8") as stream:
            activation = {"schema": schema, "previous": previous}
            if schema == 2:
                activation["directions"] = [direction.id for direction in directions]
            json.dump(activation, stream)
            stream.flush()
            os.fsync(stream.fileno())
        for directory in (version, components):
            fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            try:
                os.fsync(fd)
            finally:
                os.close(fd)
        print(PREPARED_PREFIX + version.name, flush=True)
        prepared = True
        return 0
    except Paused:
        raise
    except Exception as error:
        report_failure(error, stage, data, cache, home, resource)
        return 1
    finally:
        # Successful preparation hands cleanup ownership to activation. Callers
        # must wait for exit 0, rather than act on the descriptor line alone.
        try:
            if not prepared:
                shutil.rmtree(version)
        finally:
            os.close(lock)


if __name__ == "__main__":
    os.umask(0o077)
    home = None
    try:
        if (
            len(sys.argv) == 3
            and sys.argv[1] == "--check-hy-runtime"
            and Path(sys.argv[2]).is_absolute()
        ):
            print(json.dumps(check_hy_runtime(Path(sys.argv[2])), separators=(",", ":")))
            raise SystemExit(0)
        if (
            len(sys.argv) == 5
            and sys.argv[1] == "--cleanup-packages"
            and all(Path(value).is_absolute() for value in sys.argv[2:])
        ):
            print(
                json.dumps(
                    {"cleaned": cleanup_packages(*(Path(value) for value in sys.argv[2:]))},
                    separators=(",", ":"),
                )
            )
            raise SystemExit(0)
        supervised = sys.argv[1:2] == ["--supervised"]
        arguments = sys.argv[2:] if supervised else sys.argv[1:]
        if len(arguments) not in (3, 4) or any(
            not Path(value).is_absolute() for value in arguments[:3]
        ):
            raise ValueError("setup-arguments")
        request = (
            json.loads(arguments[3], object_pairs_hook=unique_object)
            if len(arguments) == 4
            else {"directions": None, "repair": False}
        )
        package_request = isinstance(request, dict) and "format" in request
        if package_request:
            validate_package_request(request, package_catalog(Path(arguments[0])))
        elif (
            not isinstance(request, dict)
            or set(request) != {"directions", "repair"}
            or type(request["repair"]) is not bool
            or request["directions"] is not None
            and not isinstance(request["directions"], list)
        ):
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
        raise SystemExit(
            install_packages(*(Path(value) for value in arguments[:3]), request)
            if package_request
            else install(
                *(Path(value) for value in arguments[:3]), request["directions"], request["repair"]
            )
        )
    except Paused:
        presentation = setup_presentation(home)
        print(
            presentation["pausedMessage"] if presentation else PRESENTATION_UNAVAILABLE, flush=True
        )
        raise SystemExit(75) from None
    except Exception as error:
        report_failure(error, home=home)
        raise SystemExit(1) from None
