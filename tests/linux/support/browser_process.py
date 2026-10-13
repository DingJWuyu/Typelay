"""Disposable real Chromium with Playwright's default focus emulation disabled."""

import os
import signal
import subprocess
import time
from contextlib import contextmanager
from pathlib import Path


@contextmanager
def chromium_context(playwright, executable, profile, environment, arguments=()):
    profile = Path(profile)
    profile.mkdir(parents=True, exist_ok=True)
    # Chromium leaves this discovery file behind after a normal shutdown.
    # This helper owns the disposable profile; only the new child may publish
    # the endpoint used by this launch, even when reusing its saved extensions.
    port_file = profile / "DevToolsActivePort"
    port_file.unlink(missing_ok=True)
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
        process = subprocess.Popen(
            command, env=environment, stdout=log, stderr=log, start_new_session=True
        )
        browser = None
        try:
            deadline = time.monotonic() + 15
            port = None
            while port is None:
                try:
                    lines = port_file.read_text().splitlines()
                    if (
                        len(lines) >= 2
                        and lines[0].isdigit()
                        and 0 < int(lines[0]) < 65536
                        and lines[1].startswith("/devtools/browser/")
                    ):
                        port = int(lines[0])
                        break
                except FileNotFoundError:
                    pass
                if process.poll() is not None or time.monotonic() >= deadline:
                    log.seek(0)
                    raise RuntimeError("isolated Chromium did not start: " + log.read()[-4000:])
                time.sleep(0.05)
            # A second CDP session cannot undo overrides owned by Playwright's
            # first session. no_defaults prevents their creation altogether.
            browser = playwright.chromium.connect_over_cdp(
                f"http://127.0.0.1:{port}", no_defaults=True
            )
            yield browser.contexts[0]
        finally:
            try:
                if browser:
                    try:
                        # Closing a CDP attachment only disconnects Playwright.
                        # Ask Chromium to flush/close its owned profile before removal.
                        browser.new_browser_cdp_session().send("Browser.close")
                    finally:
                        browser.close()
            finally:
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait()
