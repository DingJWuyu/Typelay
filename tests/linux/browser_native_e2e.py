"""One real extension/native-host round trip using an isolated installed package.

No transport stub or existing browser profile. The synthetic socket endpoint
stands in for the desktop metadata approver, not for the browser or native host.
This does not load a translation model or certify physical IME/visual behavior.
"""

import argparse
import hashlib
import json
import os
import shutil
import socket
import struct
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

from browser_process import chromium_context
from playwright.sync_api import sync_playwright


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
            extension = self.package / "browser/chromium"
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
                listener.bind(str(session / "native.sock"))
                (session / "native.sock").chmod(0o600)
                listener.listen(1)
                listener.settimeout(15)
                with sync_playwright() as playwright:
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
                        worker = (
                            context.service_workers[0]
                            if context.service_workers
                            else (context.wait_for_event("serviceworker", timeout=15_000))
                        )
                        extension_id = worker.url.split("/")[2]
                        subprocess.run(
                            [
                                "python3",
                                str(self.package / "install.py"),
                                "browser-install",
                                "--browser",
                                "chromium",
                                "--extension-id",
                                extension_id,
                            ],
                            env=environment,
                            check=True,
                            timeout=30,
                            capture_output=True,
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
                        first.keyboard.insert_text("First document")
                        connection, _ = listener.accept()
                        with connection:
                            connection.settimeout(10)
                            hello = self.receive(connection)
                            self.assertEqual(hello["event"], "hello")
                            self.assertEqual(hello["data"]["backend"], "browser")
                            self.assertEqual(hello["data"]["protocol"], 2)
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
    unittest.main(argv=[__file__, *remaining])
