"""Explicit-consent installation only. This process never receives user input text.

uv resolves Linux CPython wheels and verifies their hashes. A versioned environment
is built in its final location so absolute venv paths remain valid. The active
symlink changes only after the real offline worker passes both translation checks.
"""

import fcntl
import hashlib
import json
import os
import shutil
import stat
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import uuid
import zipfile
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
        if parsed.scheme != "https" or parsed.hostname != "argos-net.com" or parsed.username:
            raise OSError("model-download-origin")
        return super().redirect_request(request, fp, code, msg, headers, newurl)


def download(pair, cache):
    url = f"https://argos-net.com/v1/translate-{pair}-1_9.argosmodel"
    archive = cache / f"{pair}-1.9.argosmodel"
    if archive.is_file() and not archive.is_symlink():
        return archive, url
    partial = archive.with_suffix(".part")
    opener = urllib.request.build_opener(Redirects())
    request = urllib.request.Request(url, headers={"User-Agent": "TypeLay/0.2.0"})
    started = time.monotonic()
    with opener.open(request, timeout=45) as response, partial.open("wb") as output:
        total = int(response.headers.get("Content-Length", "0"))
        if total > MAX_ARCHIVE:
            raise ValueError("model-download-size")
        count = 0
        last_progress = 0.0
        while block := response.read(65536):
            count += len(block)
            if count > MAX_ARCHIVE or time.monotonic() - started > 900:
                raise ValueError("model-download-limit")
            output.write(block)
            if time.monotonic() - last_progress > 2:
                last_progress = time.monotonic()
                print(f"正在下载 {pair} 模型：{count // (1024 * 1024)} MiB", flush=True)
        if total and count != total:
            raise OSError("model-download-incomplete")
        output.flush()
        os.fsync(output.fileno())
    partial.replace(archive)
    return archive, url


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

    def uv(*arguments, capture=False):
        result = subprocess.run(
            ["uv", *map(str, arguments), "--no-config"],
            env=environment,
            stdout=subprocess.PIPE if capture else subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=1200,
            check=True,
        )
        return result.stdout.decode().strip() if capture else None

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
        result = subprocess.run(
            [
                str(home / "native/typelay-supervise"),
                str(executable),
                "-I",
                "-B",
                str(home / "worker/run_worker.py"),
                "--models",
                str(models),
                "--self-check",
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            env=environment,
            timeout=180,
            check=True,
        )
        if len(result.stdout) > 16384 or json.loads(result.stdout).get("status") != "passed":
            raise ValueError("model-self-check")
        (version / "self-check.json").write_bytes(result.stdout)
        pending = components / (".current-" + version.name)
        pending.symlink_to(version.name, target_is_directory=True)
        os.replace(pending, components / "current")
        active = True
        print("本地翻译组件安装完成，真实双向翻译自检通过。", flush=True)
        return 0
    finally:
        if not active:
            shutil.rmtree(version)
        os.close(lock)


if __name__ == "__main__":
    try:
        if len(sys.argv) != 4 or any(not Path(value).is_absolute() for value in sys.argv[1:]):
            raise ValueError("setup-arguments")
        raise SystemExit(install(*(Path(value) for value in sys.argv[1:])))
    except (OSError, ValueError, subprocess.SubprocessError, zipfile.BadZipFile):
        print("组件安装失败，原安装保持不变。请检查网络、磁盘空间及 uv 是否已安装。", flush=True)
        raise SystemExit(1) from None
