"""Pinned Linux preparation tool. Never resolve uv through the user's PATH."""

import hashlib
import json
import os
import stat
import subprocess
from pathlib import Path

PIN = {
    "schema": 1,
    "version": "0.13.6",
    "url": "https://github.com/astral-sh/uv/releases/download/0.13.6/uv-x86_64-unknown-linux-gnu.tar.gz",
    "bytes": 16799024,
    "sha256": "f29c3e94ffabd7e6f3ba521dba169a9b7ec12100bf3257087f0f24f6123f3645",
    "files": {
        "uv": {
            "bytes": 37784096,
            "sha256": "23803452c0fbe125033054cd9ff245f1d649fb65b4b73f68429f4abda432436d",
        },
        "LICENSE-MIT": {
            "bytes": 1077,
            "sha256": "860e3d7a86b84e6a7012c7a635fc64df475cebc6cce34dfeb73a5982ec58176c",
        },
        "LICENSE-APACHE": {
            "bytes": 11357,
            "sha256": "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4",
        },
    },
}
STEPS = {
    "tool-verify",
    "python-install",
    "python-find",
    "venv-create",
    "dependencies-resolve",
    "dependencies-install",
}
REASONS = {"missing", "integrity", "source", "permission", "exit", "timeout", "output", "launch"}


class PreparationError(Exception):
    def __init__(self, step, reason, *, exit_code=None, number=None):
        super().__init__("private-runtime-preparation")
        self.step, self.reason = step, reason
        self.exit_code, self.number = exit_code, number


def verify_file(path, record):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_size != record["bytes"]:
        raise ValueError("private-uv-integrity")
    with path.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != record["sha256"]:
            raise ValueError("private-uv-integrity")


def executable(home):
    root = Path(home).resolve(strict=True)
    directory = root / "support/uv"
    try:
        if directory.resolve(strict=True) != directory:
            raise PreparationError("tool-verify", "source")
        source = directory / "source.json"
        if not stat.S_ISREG(source.lstat().st_mode) or source.stat().st_size > 4096:
            raise PreparationError("tool-verify", "source")
        if json.loads(source.read_text(encoding="utf-8")) != PIN:
            raise PreparationError("tool-verify", "source")
        for name, record in PIN["files"].items():
            verify_file(directory / name, record)
        tool = directory / "uv"
        if not os.access(tool, os.X_OK):
            raise PreparationError("tool-verify", "permission")
        return tool
    except FileNotFoundError as error:
        raise PreparationError("tool-verify", "missing", number=error.errno) from None
    except PermissionError as error:
        raise PreparationError("tool-verify", "permission", number=error.errno) from None
    except (ValueError, UnicodeError, OSError) as error:
        raise PreparationError(
            "tool-verify", "integrity", number=getattr(error, "errno", None)
        ) from None


def run(home, environment, *arguments, capture=False):
    command = arguments[0] if arguments[0] == "venv" else "-".join(arguments[:2])
    step = {
        "python-install": "python-install",
        "python-find": "python-find",
        "venv": "venv-create",
        "pip-compile": "dependencies-resolve",
        "pip-install": "dependencies-install",
    }[command]
    tool = executable(home)
    try:
        process = subprocess.Popen(
            [str(tool), *map(str, arguments), "--no-config"],
            env=environment,
            stdout=subprocess.PIPE if capture else subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        try:
            output, _ = process.communicate(timeout=1200)
            if process.returncode:
                raise PreparationError(step, "exit", exit_code=process.returncode)
            if capture:
                if len(output) > 16384:
                    raise PreparationError(step, "output")
                return output.decode("utf-8").strip()
            return None
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        raise PreparationError(step, "timeout") from None
    except (OSError, UnicodeError) as error:
        raise PreparationError(step, "launch", number=getattr(error, "errno", None)) from None
