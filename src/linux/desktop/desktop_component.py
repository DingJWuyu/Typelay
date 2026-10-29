"""Prepare the cooperation archive shipped by this TypeLay build, without executing it."""

import fcntl
import hashlib
import json
import os
import re
import shutil
import stat
import tarfile
import uuid
from contextlib import contextmanager
from pathlib import Path, PurePosixPath

BUNDLE = "TypeLay-Fcitx-cooperative"
ARCHIVE = BUNDLE + ".tar.gz"
PAYLOAD = "support/desktop"
MAX_ARCHIVE = 2 * 1024**3
MAX_EXPANDED = 4 * 1024**3
MAX_ENTRIES = 50000
RESERVE = 64 * 1024**2


@contextmanager
def directory(path, *, create=False):
    """Open every directory without following links, including existing ancestors."""
    path = Path(path)
    if not path.is_absolute():
        raise ValueError("component-path-not-absolute")
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY)
    try:
        for part in path.parts[1:]:
            if create:
                try:
                    os.mkdir(part, mode=0o700, dir_fd=fd)
                except FileExistsError:
                    pass
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
            os.close(fd)
            fd = child
        yield fd
    finally:
        os.close(fd)


@contextmanager
def regular_file(path, *, maximum):
    with directory(path.parent) as parent:
        fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=parent)
    with os.fdopen(fd, "rb") as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_size > maximum:
            raise ValueError("component-file-invalid")
        yield stream


def read_small(path, maximum):
    with regular_file(path, maximum=maximum) as stream:
        result = stream.read(maximum + 1)
    if len(result) > maximum:
        raise ValueError("component-file-too-large")
    return result


def digest(path, maximum=MAX_EXPANDED):
    with regular_file(path, maximum=maximum) as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def checksum(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def relative_path(value):
    if not isinstance(value, str) or not value or "\x00" in value:
        raise ValueError("component-archive-path-invalid")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in value.split("/")):
        raise ValueError("component-archive-path-invalid")
    return path


class DesktopComponent:
    """Only this main package supplies executable components; selection is unrelated."""

    def __init__(self, paths):
        self.paths = paths

    @property
    def root(self):
        return self.paths.data / "components/desktop-text"

    def _descriptor(self):
        descriptor_path = self.paths.home / PAYLOAD / "component.json"
        body = read_small(descriptor_path, 16384)
        value = json.loads(body)
        if (
            not isinstance(value, dict)
            or value.get("schemaVersion") != 1
            or value.get("archive") != ARCHIVE
            or not checksum(value.get("archiveSha256"))
            or not checksum(value.get("manifestSha256"))
            or not isinstance(value.get("sourceCommit"), str)
            or re.fullmatch(r"[0-9a-f]{40}", value["sourceCommit"]) is None
            or not isinstance(value.get("sourceVersions"), dict)
            or not value["sourceVersions"]
            or not isinstance(value.get("privateVersions"), dict)
            or not value["privateVersions"]
            or any(
                not isinstance(key, str) or not isinstance(version, str)
                for versions in (value["sourceVersions"], value["privateVersions"])
                for key, version in versions.items()
            )
            or "qtVersion" not in value
            or value["qtVersion"] is not None
            and not isinstance(value["qtVersion"], str)
        ):
            raise ValueError("component-descriptor-invalid")
        # These hashes inherit the user's trust in the main distribution. Neither
        # this manifest nor the archive's own manifest independently proves origin.
        records = {}
        for line in (
            read_small(self.paths.home / "MANIFEST.sha256", 8 * 1024**2).decode().splitlines()
        ):
            match = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
            if match is None or match[2] in records:
                raise ValueError("main-package-manifest-invalid")
            records[match[2]] = match[1]
        if (
            records.get(PAYLOAD + "/component.json") != hashlib.sha256(body).hexdigest()
            or records.get(PAYLOAD + "/" + ARCHIVE) != value["archiveSha256"]
        ):
            raise ValueError("main-package-component-mismatch")
        return value

    def status(self):
        """Bounded display facts only; never hash the archive or walk installed files."""
        try:
            value = self._descriptor()
        except FileNotFoundError:
            return {"state": "missing"}
        except (OSError, ValueError, TypeError):
            return {"state": "invalid", "reason": "component-descriptor-invalid"}
        target = self.root / "releases" / value["archiveSha256"] / BUNDLE
        try:
            with directory(self.root) as root:
                if os.readlink("current", dir_fd=root) != "releases/" + value["archiveSha256"]:
                    return {"state": "available"}
            if digest(target / "manifest.json", 4 * 1024**2) != value["manifestSha256"]:
                raise ValueError("component-manifest-mismatch")
            return {"state": "prepared", "directory": target}
        except FileNotFoundError:
            return {"state": "available"}
        except (OSError, ValueError):
            return {"state": "invalid", "reason": "managed-component-invalid"}

    @staticmethod
    def _manifest(bundle, expected, actual):
        encoded = read_small(bundle / "manifest.json", 4 * 1024**2)
        if hashlib.sha256(encoded).hexdigest() != expected["manifestSha256"]:
            raise ValueError("component-manifest-mismatch")
        value = json.loads(encoded)
        versions = value.get("versions", {})
        core = expected["sourceVersions"].get("fcitx5")
        qt = value.get("qt6")
        if (
            value.get("schemaVersion") != 1
            or value.get("bundleName") != BUNDLE
            or not core
            or value.get("coreVersion") != core
            or versions.get("component_source_versions") != expected["sourceVersions"]
            or versions.get("installed_private_versions") != expected["privateVersions"]
            or any(
                versions.get(key) != core
                for key in (
                    "server_source_version",
                    "server_runtime_version",
                    "linked_core_development_version",
                )
            )
            or (qt.get("qtVersion") if isinstance(qt, dict) else None) != expected["qtVersion"]
            or (qt is not None and not isinstance(qt, dict))
        ):
            raise ValueError("component-version-mismatch")
        files = value.get("files")
        if not isinstance(files, dict) or files != actual:
            raise ValueError("component-files-mismatch")
        for required in (
            "fonts/inter/Inter-Regular.ttf",
            "fonts/inter/Inter-Medium.ttf",
            "fonts/inter/Inter-SemiBold.ttf",
            "fonts/inter/LICENSE.txt",
            "fonts/source-serif/SourceSerif4-Regular.ttf",
            "fonts/source-serif/SourceSerif4-Semibold.ttf",
            "fonts/source-serif/LICENSE.md",
            "fonts/README.md",
            "fcitx-cooperative.py",
            "core/bin/fcitx5",
            "README.md",
            "THIRD_PARTY_NOTICES.md",
            "source/eng/linux/cooperative/fcitx.py",
        ):
            if "sha256" not in files.get(required, {}):
                raise ValueError("component-source-or-runtime-missing")
        if not any(name.startswith("source/upstream/") and "/LICENSES/" in name for name in files):
            raise ValueError("component-license-missing")

    @staticmethod
    def _space(fd, needed):
        space = os.fstatvfs(fd)
        if space.f_bavail * space.f_frsize < needed + RESERVE:
            raise OSError("component-disk-space-insufficient")

    def _extract(self, source, destination, expected):
        actual = {}
        links = {}
        seen = set()
        total = 0
        with tarfile.open(fileobj=source, mode="r:gz") as package:
            for item in package:
                path = relative_path(item.name.rstrip("/"))
                if path.parts[0] != BUNDLE or str(path) in seen or len(seen) >= MAX_ENTRIES:
                    raise ValueError("component-archive-entry-invalid")
                seen.add(str(path))
                target = destination / path
                if item.isdir():
                    with directory(target, create=True):
                        pass
                    continue
                if len(path.parts) == 1:
                    raise ValueError("component-archive-root-invalid")
                relative = str(PurePosixPath(*path.parts[1:]))
                if item.issym():
                    link = PurePosixPath(item.linkname)
                    resolved = os.path.normpath(str(path.parent / link))
                    if link.is_absolute() or not resolved.startswith(BUNDLE + "/"):
                        raise ValueError("component-link-escaped")
                    links[relative] = item.linkname
                    actual[relative] = {"symlink": item.linkname}
                    continue
                if not item.isfile() or item.size < 0 or item.issparse():
                    raise ValueError("component-archive-type-invalid")
                total += item.size
                if total > MAX_EXPANDED or relative == "manifest.json" and item.size > 4 * 1024**2:
                    raise ValueError("component-archive-size-limit")
                with directory(target.parent, create=True) as parent:
                    self._space(parent, item.size)
                    fd = os.open(
                        target.name,
                        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                        0o755 if item.mode & 0o111 else 0o644,
                        dir_fd=parent,
                    )
                    with os.fdopen(fd, "wb") as output, package.extractfile(item) as source:
                        checksummer = hashlib.sha256()
                        while chunk := source.read(1024 * 1024):
                            output.write(chunk)
                            checksummer.update(chunk)
                    if relative != "manifest.json":
                        actual[relative] = {"sha256": checksummer.hexdigest()}
        bundle = destination / BUNDLE
        self._manifest(bundle, expected, actual)
        # Create links only after every regular file has been written. A link
        # can never become an extraction parent, regardless of archive order.
        for relative, link in links.items():
            target = bundle / relative
            with directory(target.parent, create=True) as parent:
                os.symlink(link, target.name, dir_fd=parent)
        for relative in links:
            if not (bundle / relative).resolve(strict=True).is_relative_to(bundle):
                raise ValueError("component-link-escaped")

    def _verify_prepared(self, release, expected):
        bundle = release / BUNDLE
        actual = {}
        total = 0
        with directory(bundle):
            for base, directories, files, fd in os.fwalk(bundle, follow_symlinks=False):
                for name in [*directories, *files]:
                    path = Path(base) / name
                    relative = path.relative_to(bundle).as_posix()
                    info = os.stat(name, dir_fd=fd, follow_symlinks=False)
                    if stat.S_ISDIR(info.st_mode):
                        continue
                    if len(actual) >= MAX_ENTRIES:
                        raise ValueError("component-archive-size-limit")
                    if stat.S_ISLNK(info.st_mode):
                        if not path.resolve(strict=True).is_relative_to(bundle):
                            raise ValueError("component-link-escaped")
                        actual[relative] = {"symlink": os.readlink(name, dir_fd=fd)}
                    elif stat.S_ISREG(info.st_mode):
                        total += info.st_size
                        if total > MAX_EXPANDED:
                            raise ValueError("component-archive-size-limit")
                        if relative != "manifest.json":
                            actual[relative] = {"sha256": digest(path)}
                    else:
                        raise ValueError("component-file-invalid")
        self._manifest(bundle, expected, actual)

    def verify_prepared(self):
        """Execution callers hold the use lock and recheck the exact trusted build."""
        expected = self._descriptor()
        with directory(self.root) as root:
            if os.readlink("current", dir_fd=root) != "releases/" + expected["archiveSha256"]:
                raise OSError("component-not-prepared")
        release = self.root / "releases" / expected["archiveSha256"]
        self._verify_prepared(release, expected)
        return release / BUNDLE

    def prepare(self):
        """Explicit installation only. Retain the prior release; never modify the archive."""
        stage = None
        lock = None
        try:
            expected = self._descriptor()
            components = self.paths.data / "components"
            with directory(components, create=True) as parent:
                if os.fstat(parent).st_uid != os.getuid():
                    raise OSError("component-directory-owner")
                lock = os.open(
                    "install.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600, dir_fd=parent
                )
            if not stat.S_ISREG(os.fstat(lock).st_mode) or os.fstat(lock).st_uid != os.getuid():
                raise OSError("component-lock-invalid")
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with directory(self.root, create=True) as root:
                if os.fstat(root).st_uid != os.getuid() or os.fstat(root).st_mode & 0o077:
                    raise OSError("component-directory-owner")
            with directory(self.root / "releases", create=True) as releases:
                if os.fstat(releases).st_uid != os.getuid():
                    raise OSError("component-directory-owner")
                name = expected["archiveSha256"]
                target = self.root / "releases" / name
                with regular_file(
                    self.paths.home / PAYLOAD / ARCHIVE, maximum=MAX_ARCHIVE
                ) as source:
                    if (
                        hashlib.file_digest(source, "sha256").hexdigest()
                        != expected["archiveSha256"]
                    ):
                        raise ValueError("component-archive-mismatch")
                    if target.exists() or target.is_symlink():
                        self._verify_prepared(target, expected)
                    else:
                        self._space(releases, 0)
                        temporary = ".preparing-" + uuid.uuid4().hex
                        os.mkdir(temporary, mode=0o700, dir_fd=releases)
                        stage = self.root / "releases" / temporary
                        source.seek(0)
                        self._extract(source, stage, expected)
                        os.rename(temporary, name, src_dir_fd=releases, dst_dir_fd=releases)
                        stage = None
            with directory(self.root) as root:
                try:
                    info = os.stat("current", dir_fd=root, follow_symlinks=False)
                    if not stat.S_ISLNK(info.st_mode) or not re.fullmatch(
                        r"releases/[0-9a-f]{64}", os.readlink("current", dir_fd=root)
                    ):
                        raise OSError("managed-component-pointer-invalid")
                except FileNotFoundError:
                    pass
                pending = ".current-" + uuid.uuid4().hex
                os.symlink("releases/" + name, pending, dir_fd=root)
                try:
                    os.replace(pending, "current", src_dir_fd=root, dst_dir_fd=root)
                finally:
                    try:
                        os.unlink(pending, dir_fd=root)
                    except FileNotFoundError:
                        pass
            return {
                "ok": True,
                "reason": "cooperative-prepared",
                "detail": "同包桌面接入组件已准备，源码与许可证已保留，原始压缩包仍在主包中。尚未启动输入法或连接应用。",
            }
        except (
            OSError,
            ValueError,
            TypeError,
            AttributeError,
            RuntimeError,
            tarfile.TarError,
        ) as error:
            return {"ok": False, "reason": "cooperative-preparation-failed", "detail": str(error)}
        finally:
            try:
                if stage is not None:
                    shutil.rmtree(stage)
            finally:
                if lock is not None:
                    os.close(lock)
