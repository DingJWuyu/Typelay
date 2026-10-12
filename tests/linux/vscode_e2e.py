#!/usr/bin/env python3
"""Run real editor transaction regression in a disposable VS Code profile.

Requires an existing official Code/Code OSS executable. Does not install an
extension, fetch software/models, read a workspace, or change the user profile.
"""

import argparse
import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--code", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=Path("artifacts/linux/vscode"))
    arguments = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    code = arguments.code.resolve(strict=True)
    output = arguments.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    log = output / "extension-host.log"
    with tempfile.TemporaryDirectory(prefix="typelay-vscode-") as temporary:
        profile = Path(temporary)
        result_file = profile / "test-result.json"
        home = profile / "home"
        home.mkdir()
        command = [
            str(code),
            "--no-sandbox",
            "--wait",
            "--verbose",
            "--disable-gpu",
            "--disable-workspace-trust",
            "--skip-welcome",
            "--skip-release-notes",
            "--disable-telemetry",
            "--disable-updates",
            "--user-data-dir",
            str(profile / "data"),
            "--extensions-dir",
            str(profile / "extensions"),
            "--extensionDevelopmentPath=" + str(root / "src/TypeLay.Linux/vscode"),
            "--extensionTestsPath=" + str(root / "tests/linux/vscode_e2e.js"),
        ]
        environment = os.environ.copy()
        environment.update(
            HOME=str(home),
            XDG_CONFIG_HOME=str(profile / "config"),
            XDG_DATA_HOME=str(profile / "share"),
            XDG_CACHE_HOME=str(profile / "cache"),
            TYPELAY_VSCODE_RESULT=str(result_file),
        )
        # Never inherit Node startup injection into the isolated editor process.
        environment.pop("NODE_OPTIONS", None)
        environment.pop("ELECTRON_RUN_AS_NODE", None)
        with log.open("w", encoding="utf-8") as stream:
            process = subprocess.Popen(
                command,
                env=environment,
                start_new_session=True,
                stdout=stream,
                stderr=subprocess.STDOUT,
            )
            try:
                exit_code = process.wait(timeout=150)
            finally:
                import signal

                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)
                # CLI output is not the extension host log. Preserve evidence
                # before TemporaryDirectory cleanup, including startup failures.
                profile_logs = profile / "data" / "logs"
                if profile_logs.exists():
                    shutil.copytree(profile_logs, output / "profile-logs", dirs_exist_ok=True)
                if result_file.exists():
                    shutil.copyfile(result_file, output / "test-result.json")
        if exit_code:
            raise RuntimeError(f"VS Code exited {exit_code}; see {log}")
        if not result_file.exists():
            raise RuntimeError(f"VS Code did not start the transaction test runner; see {output}")
        result = json.loads(result_file.read_text(encoding="utf-8"))
        if result.get("status") != "passed":
            raise RuntimeError(f"VS Code transaction assertions did not pass; see {output}")
        print("TypeLay VS Code real editor transaction tests passed.")


if __name__ == "__main__":
    main()
