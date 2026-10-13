"""One real extension/native-host round trip using an isolated installed package.

No transport stub or existing browser profile. The synthetic socket endpoint
stands in for the desktop metadata approver, not for the browser or native host.
This does not load a translation model or certify physical IME/visual behavior.
"""

import argparse
import hashlib
import json
import os
import shlex
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

from playwright.sync_api import Error as PlaywrightError
from playwright.sync_api import sync_playwright
from support.browser_process import chromium_context


class NativeBrowserContract(unittest.TestCase):
    package = None
    executable = "/usr/bin/chromium"

    def test_package_host_routes_current_document_without_reusing_removed_target(self):
        with tempfile.TemporaryDirectory(prefix="typelay-native-browser-") as directory:
            root = Path(directory)
            environment = dict(
                os.environ,
                HOME=str(root),
                XDG_CONFIG_HOME=str(root / "config"),
                XDG_DATA_HOME=str(root / "data"),
                XDG_CACHE_HOME=str(root / "cache"),
                XDG_RUNTIME_DIR=str(root / "run"),
                WAYLAND_DISPLAY="typelay-browser-test",
                NIRI_SOCKET=str(root / "niri-test.sock"),
            )
            commands = root / "bin"
            commands.mkdir()
            browser_launcher = commands / "chromium"
            # The settings action launches the same real, disposable browser.
            # Match browser_process's root-only CI flag without adding any
            # test flags or profile controls to the shipped application.
            browser_launcher.write_text(
                "#!/bin/sh\nexec "
                + shlex.join(
                    [
                        self.executable,
                        f"--user-data-dir={root / 'config/chromium'}",
                        *(["--no-sandbox"] if os.getuid() == 0 else []),
                    ]
                )
                + ' "$@"\n'
            )
            browser_launcher.chmod(0o700)
            environment["PATH"] = str(commands) + os.pathsep + environment["PATH"]
            runtime = root / "run"
            runtime.mkdir(mode=0o700)
            session_id = hashlib.sha256(
                (environment["WAYLAND_DISPLAY"] + "\n" + environment["NIRI_SOCKET"]).encode()
            ).hexdigest()[:16]
            session = runtime / "typelay" / session_id
            session.mkdir(parents=True, mode=0o700)
            (runtime / "typelay").chmod(0o700)
            subprocess.run(
                ["python3", str(self.package / "install.py"), "install"],
                env=environment,
                check=True,
                timeout=30,
                capture_output=True,
            )
            installed = root / ".local/lib/typelay/current"
            extension = installed / "browser/chromium"
            with patch.dict(os.environ, environment):
                support = ApplicationSupport(Paths(installed))
                self.assertEqual(support.browser_status("chromium")["state"], "not-registered")
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
                listener.bind(str(session / "native.sock"))
                (session / "native.sock").chmod(0o600)
                listener.listen(1)
                listener.settimeout(15)
                with sync_playwright() as playwright:
                    # Loading is a browser-owned approval in the product. The
                    # existing isolated fixture loads only this shipped payload,
                    # then closes normally so Chromium saves its actual record.
                    with chromium_context(
                        playwright,
                        self.executable,
                        root / "config/chromium",
                        environment,
                        [
                            f"--disable-extensions-except={extension}",
                            f"--load-extension={extension}",
                        ],
                    ) as context:
                        if not context.service_workers:
                            context.wait_for_event("serviceworker", timeout=15_000)
                    scope = chromium_context(
                        playwright,
                        self.executable,
                        root / "config/chromium",
                        environment,
                        [
                            f"--disable-extensions-except={extension}",
                            f"--load-extension={extension}",
                        ],
                    )
                    context = scope.__enter__()
                    try:
                        opened_page = None
                        try:
                            with context.expect_page(timeout=10_000) as opened:
                                self.support_action(installed, environment, "OpenInstructions")
                            opened_page = opened.value
                            opened_page.wait_for_url("chrome://extensions/", timeout=10_000)
                        except (PlaywrightError, AssertionError, subprocess.TimeoutExpired):
                            try:
                                self.save_open_failure(context, opened_page, root)
                            except (OSError, PlaywrightError) as error:
                                print("Browser evidence could not be saved:", error, flush=True)
                            raise
                        with patch.dict(os.environ, environment):
                            self.assertEqual(
                                support.browser_status("chromium")["state"], "not-registered"
                            )
                        context.route(
                            "http://127.0.0.1/**",
                            lambda route: route.fulfill(
                                body="<!doctype html><textarea id='source'></textarea>",
                                content_type="text/html",
                            ),
                        )
                        first = context.new_page()
                        first.goto("http://127.0.0.1/first")
                        first.locator("#source").focus()
                        first.keyboard.insert_text("Before connection")
                        listener.setblocking(False)
                        with self.assertRaises(BlockingIOError):
                            listener.accept()
                        listener.settimeout(15)
                        # Follow the production settings action. Neither the
                        # user nor this test supplies an extension identifier.
                        self.support_action(installed, environment, "Connect")
                        with patch.dict(os.environ, environment):
                            row = next(fact for fact in support.facts() if fact["id"] == "chromium")
                            self.assertEqual(row["state"], "Registered")
                            self.assertFalse(any(row["capabilities"].values()))
                        # The settings instructions ask the user to reload the
                        # target page after connecting; no private page is
                        # automatically reloaded by TypeLay.
                        first.reload()
                        first.locator("#source").focus()
                        first.keyboard.insert_text("First document")
                        listener.settimeout(15)
                        connection, _ = listener.accept()
                        with connection:
                            connection.settimeout(10)
                            previous_hello = self.receive(connection)
                            self.assertEqual(previous_hello["event"], "hello")
                        # A real native connection just closed. Refresh at once,
                        # while reconnection is throttled; that user request must
                        # eventually connect without another focus/click/reload.
                        first.reload()
                        first.locator("#source").focus()
                        first.keyboard.insert_text("First document")
                        connection, _ = listener.accept()
                        with connection:
                            connection.settimeout(10)
                            hello = self.receive(connection)
                            self.assertEqual(hello["event"], "hello")
                            self.assertEqual(hello["data"]["backend"], "browser")
                            self.assertEqual(hello["data"]["protocol"], 2)
                            self.assertNotEqual(
                                hello["data"]["lifetime"], previous_hello["data"]["lifetime"]
                            )
                            self.sequence = 0
                            # Browser callbacks continue in another process while
                            # this test's socket receives the native-host reply.
                            original = self.focused_description(connection)
                            self.assertIsNotNone(original)
                            snapshot = self.request(
                                connection,
                                "capture",
                                {
                                    "target": original["target"],
                                    "authority": original["authority"],
                                },
                            )
                            self.assertEqual(snapshot["text"], "First document")
                            second = context.new_page()
                            second.goto("http://127.0.0.1/second")
                            second.bring_to_front()
                            second.locator("#source").focus()
                            second.keyboard.insert_text("Second document")
                            current = self.focused_description(
                                connection, previous=original["target"]
                            )
                            self.assertIsNotNone(current)
                            self.assertNotEqual(current["target"], original["target"])
                            snapshot = self.request(
                                connection,
                                "capture",
                                {
                                    "target": current["target"],
                                    "authority": current["authority"],
                                },
                            )
                            self.assertEqual(snapshot["text"], "Second document")
                            first.evaluate("document.querySelector('#source').remove()")
                            self.assertIsNone(
                                self.request(
                                    connection,
                                    "describe",
                                    {
                                        "target": original["target"],
                                    },
                                )
                            )
                            self.assertEqual(
                                second.locator("#source").input_value(), "Second document"
                            )
                    finally:
                        scope.__exit__(None, None, None)

    @staticmethod
    def save_open_failure(context, opened, root):
        """One bounded snapshot of this disposable browser; never retry the failed action."""
        output = Path(__file__).resolve().parents[2] / "artifacts/linux/browser-native"
        output.mkdir(parents=True, exist_ok=True)
        pages = context.pages[:4]
        report = {
            "expectedUrl": "chrome://extensions/",
            "openedPage": {"url": opened.url, "closed": opened.is_closed()} if opened else None,
            "pages": [
                {"url": page.url, "closed": page.is_closed(), "selected": page is opened}
                for page in pages
            ],
        }
        print("Browser pages at failure:", json.dumps(report, ensure_ascii=False), flush=True)
        for index, page in enumerate(pages):
            value = report["pages"][index]
            if page.is_closed():
                continue
            for state in ("domcontentloaded", "load"):
                try:
                    page.wait_for_load_state(state, timeout=100)
                    value[state] = True
                except PlaywrightError as error:
                    value[state] = str(error)[:300]
            for name, selector in (
                ("extensionManager", "extensions-manager"),
                ("browserErrorPage", "#main-frame-error"),
            ):
                try:
                    page.locator(selector).wait_for(state="attached", timeout=250)
                    value[name] = True
                except PlaywrightError as error:
                    value[name] = str(error)[:300]
            try:
                page.screenshot(path=str(output / f"open-page-{index}.png"), timeout=2000)
            except (OSError, PlaywrightError) as error:
                value["screenshotError"] = str(error)[:300]
        try:
            with (root / "config/chromium/chromium.log").open("rb") as log:
                log.seek(0, os.SEEK_END)
                log.seek(max(0, log.tell() - 65536))
                (output / "chromium.log").write_bytes(log.read())
        except OSError as error:
            report["chromiumLogError"] = str(error)[:300]
        evidence = json.dumps(report, ensure_ascii=False, indent=2)
        print("Browser open failure evidence:\n" + evidence, flush=True)
        (output / "open-failure.json").write_text(evidence, encoding="utf-8")

    def support_action(self, installed, environment, action):
        # Isolate the product's asyncio loop from Playwright's synchronous loop.
        process = subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                "-c",
                "import asyncio, json, sys; from pathlib import Path; "
                "sys.path.insert(0, str(Path(sys.argv[1]) / 'desktop')); "
                "from application_support import ApplicationSupport; from paths import Paths; "
                "support = ApplicationSupport(Paths(sys.argv[1])); "
                "result = asyncio.run(support.execute('chromium', sys.argv[2])); "
                "print(json.dumps(result)); sys.exit(0 if result['ok'] else 1)",
                str(installed),
                action,
            ],
            env=environment,
            capture_output=True,
            text=True,
            timeout=20,
        )
        self.assertEqual(process.returncode, 0, (process.stdout, process.stderr))

    def focused_description(self, connection, previous=None):
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            description = self.request(connection, "describe")
            if description and description["focused"] and description["target"] != previous:
                return description
        self.fail("Browser did not report the new focused document")

    @staticmethod
    def receive(connection):
        def exact(length):
            data = bytearray()
            while len(data) < length:
                part = connection.recv(length - len(data))
                if not part:
                    raise EOFError("native-host-closed")
                data.extend(part)
            return data

        length = struct.unpack("<I", exact(4))[0]
        if not 0 < length <= 256 * 1024:
            raise ValueError("native-frame-size")
        return json.loads(exact(length))

    def request(self, connection, operation, data=None):
        self.sequence += 1
        packet = json.dumps({"id": self.sequence, "op": operation, "data": data or {}}).encode()
        connection.sendall(struct.pack("<I", len(packet)) + packet)
        for _ in range(128):
            message = self.receive(connection)
            if message.get("id") == self.sequence:
                return message.get("data")
        self.fail("Native host did not return the matching bounded request")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--chromium", default=shutil.which("chromium") or "/usr/bin/chromium")
    arguments, remaining = parser.parse_known_args()
    NativeBrowserContract.package = arguments.package.resolve()
    NativeBrowserContract.executable = arguments.chromium
    sys.path.insert(0, str(NativeBrowserContract.package / "desktop"))
    from application_support import ApplicationSupport
    from paths import Paths

    unittest.main(argv=[__file__, *remaining])
