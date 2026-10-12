"""Disposable real Chromium with Playwright's default focus emulation disabled."""

import os
import subprocess
import time
from contextlib import contextmanager
from pathlib import Path


@contextmanager
def chromium_context(playwright, executable, profile, environment, arguments=()):
    profile = Path(profile)
    profile.mkdir(parents=True, exist_ok=True)
    command = [
        executable,
        f"--user-data-dir={profile}",
        "--remote-debugging-address=127.0.0.1",
        "--remote-debugging-port=0",
        "--no-first-run",
        "--no-default-browser-check",
        "--window-size=1280,960",
        *arguments,
    ]
    if os.getuid() == 0:  # Disposable root CI container, never a user profile.
        command.append("--no-sandbox")
    with (profile / "chromium.log").open("w+") as log:
        process = subprocess.Popen(command, env=environment, stdout=log, stderr=log)
        browser = None
        try:
            port_file = profile / "DevToolsActivePort"
            deadline = time.monotonic() + 15
            while not port_file.is_file():
                if process.poll() is not None or time.monotonic() >= deadline:
                    log.seek(0)
                    raise RuntimeError("isolated Chromium did not start: " + log.read()[-4000:])
                time.sleep(0.05)
            port = int(port_file.read_text().splitlines()[0])
            # A second CDP session cannot undo overrides owned by Playwright's
            # first session. no_defaults prevents their creation altogether.
            browser = playwright.chromium.connect_over_cdp(
                f"http://127.0.0.1:{port}", no_defaults=True
            )
            yield browser.contexts[0]
        finally:
            try:
                if browser:
                    browser.close()
            finally:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
