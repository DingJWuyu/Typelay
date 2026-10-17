"""Real Firefox temporary extension/native-host contract, with disposable user data.

The socket endpoint only supplies desktop metadata approval; Firefox, the
extension, native messaging and packaged host are real. No model, physical IME
or compositor identity is certified here. Run under an isolated Xvfb DISPLAY.

Mozilla's temporary-install endpoint and profile lifecycle:
https://github.com/mozilla/gecko-dev/blob/master/testing/geckodriver/src/command.rs
https://firefox-source-docs.mozilla.org/testing/geckodriver/Profiles.html
"""

import argparse
import hashlib
import http.client
import json
import os
import re
import shutil
import signal
import socket
import subprocess
import tempfile
import threading
import time
import unittest
from contextlib import ExitStack, contextmanager
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import browser_native_e2e

ELEMENT = "element-6066-11e4-a52e-4f735466cecf"


class Fixture(BaseHTTPRequestHandler):
    def do_GET(self):
        bundle = getattr(self.server, "editor_bundle", None)
        if bundle and self.path in ("/rich", "/editor.js"):
            body = (
                bundle.read_bytes()
                if self.path == "/editor.js"
                else (
                    b"<!doctype html><meta charset=utf-8><div id=mount></div>"
                    b"<script src='/editor.js'></script>"
                )
            )
            self.send_response(200)
            self.send_header(
                "Content-Type",
                "application/javascript" if self.path == "/editor.js" else "text/html",
            )
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        body = b"""<!doctype html><meta charset='utf-8'><title>TypeLay Firefox contracts</title>
<textarea id='source'></textarea>
<script>
window.fixtureEvents = [];
for (const type of ['focus', 'blur', 'focusin', 'focusout', 'beforeinput', 'input',
  'compositionstart', 'compositionend', 'pagehide', 'pageshow', 'freeze', 'visibilitychange']) {
  window.addEventListener(type, event => {
    fixtureEvents.push({type, trusted: event.isTrusted, composing: event.isComposing === true,
      source: event.target === document.querySelector('#source'),
      wall: Date.now(), monotonic: performance.now()});
    if (fixtureEvents.length > 64) fixtureEvents.shift();
  }, true);
}
</script>"""
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format, *_args):
        pass


@contextmanager
def firefox(root, environment, binary, driver, log_path):
    profiles = root / "profiles"
    profiles.mkdir()
    log_path.parent.mkdir(parents=True, exist_ok=True)
    with log_path.open("w+") as log:
        process = subprocess.Popen(
            [driver, "--host", "127.0.0.1", "--port", "0", "--profile-root", str(profiles)],
            env=environment,
            stdout=log,
            stderr=log,
            start_new_session=True,
        )
        session = None
        try:
            deadline = time.monotonic() + 15
            while True:
                log.seek(0)
                match = re.search(r"Listening on 127\.0\.0\.1:(\d+)", log.read())
                if match:
                    break
                if process.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError("geckodriver did not start; see " + str(log_path))
                time.sleep(0.05)
            port = int(match[1])

            def request(method, path, data=None):
                connection = http.client.HTTPConnection("127.0.0.1", port, timeout=30)
                try:
                    connection.request(
                        method,
                        path,
                        body=json.dumps(data) if data is not None else None,
                        headers={"Content-Type": "application/json"},
                    )
                    response = connection.getresponse()
                    value = json.loads(response.read())["value"]
                    if response.status >= 400:
                        raise RuntimeError(f"WebDriver {method} {path}: {value}")
                    return value
                finally:
                    connection.close()

            result = request(
                "POST",
                "/session",
                {
                    "capabilities": {
                        "alwaysMatch": {
                            "browserName": "firefox",
                            "moz:firefoxOptions": {"binary": binary},
                        }
                    },
                },
            )
            session = result["sessionId"]
            capabilities = result["capabilities"]
            print(
                "Firefox test versions: "
                + json.dumps(
                    {
                        "firefox": capabilities.get("browserVersion"),
                        "geckodriver": capabilities.get("moz:geckodriverVersion"),
                    }
                ),
                flush=True,
            )
            prefix = f"/session/{session}"
            yield lambda method, path, data=None: request(method, prefix + path, data)
        finally:
            try:
                if session:
                    request("DELETE", f"/session/{session}")
            finally:
                # Only the process group created above belongs to this test.
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)


class FirefoxNativeContract(unittest.TestCase):
    package = None
    executable = "/usr/bin/firefox"
    driver = "/usr/bin/geckodriver"
    editor_bundle = None

    def test_real_extension_host_write_and_document_identity(self):
        with (
            tempfile.TemporaryDirectory(prefix="typelay-firefox-") as directory,
            ExitStack() as stack,
        ):
            root = Path(directory)
            environment = dict(
                os.environ,
                HOME=str(root),
                XDG_CONFIG_HOME=str(root / "config"),
                XDG_DATA_HOME=str(root / "data"),
                XDG_CACHE_HOME=str(root / "cache"),
                XDG_RUNTIME_DIR=str(root / "run"),
                WAYLAND_DISPLAY="typelay-firefox-test",
                NIRI_SOCKET=str(root / "niri-test.sock"),
                MOZ_ENABLE_WAYLAND="0",
                GDK_BACKEND="x11",
            )
            runtime = root / "run"
            runtime.mkdir(mode=0o700)
            identity = hashlib.sha256(
                (environment["WAYLAND_DISPLAY"] + "\n" + environment["NIRI_SOCKET"]).encode()
            ).hexdigest()[:16]
            session = runtime / "typelay" / identity
            session.mkdir(parents=True, mode=0o700)
            session.parent.chmod(0o700)
            for operation in (["install"], ["browser-install", "--browser", "firefox"]):
                subprocess.run(
                    ["python3", str(self.package / "install.py"), *operation],
                    env=environment,
                    check=True,
                    timeout=30,
                    capture_output=True,
                )
            addon = self.package / "browser/typelay-firefox-unsigned.xpi"

            listener = stack.enter_context(socket.socket(socket.AF_UNIX, socket.SOCK_STREAM))
            listener.bind(str(session / "native.sock"))
            (session / "native.sock").chmod(0o600)
            listener.listen(1)
            listener.settimeout(15)
            server = stack.enter_context(HTTPServer(("127.0.0.1", 0), Fixture))
            server.editor_bundle = self.editor_bundle
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            stack.callback(thread.join, 3)
            stack.callback(server.shutdown)
            url = f"http://127.0.0.1:{server.server_port}"
            log_path = Path("artifacts/linux/firefox-native/geckodriver.log").resolve()
            web = stack.enter_context(
                firefox(root, environment, self.executable, self.driver, log_path)
            )
            installed = web("POST", "/moz/addon/install", {"path": str(addon), "temporary": True})
            self.assertEqual(installed, "typelay@typelay.local")
            first = web("GET", "/window")
            web("POST", "/url", {"url": url + "/first"})
            self.type_source(web, "First document")
            connection, _ = listener.accept()
            connection = stack.enter_context(connection)
            connection.settimeout(10)
            native = browser_native_e2e.NativeBrowserContract()
            native.sequence = 0
            native.events = []
            hello = native.receive(connection)
            self.assertEqual(hello["event"], "hello")
            self.assertEqual(hello["data"]["backend"], "browser")
            self.assertEqual(hello["data"]["protocol"], 2)
            native.configure_session(connection)
            original = native.focused_description(connection)
            self.assertEqual(
                original["sessionBindings"],
                {
                    "generation": "native-default",
                    "accept": "Tab",
                    "exit": "Esc",
                    "sourceReady": True,
                    "editorReady": True,
                    "shortcutDispatch": "page",
                    "target": original["target"],
                    "sourceLocalAcceptanceVersion": 0,
                },
            )
            snapshot = self.capture(native, connection, original, web)
            self.assertEqual(snapshot["text"], "First document")
            replacement = "Translated text"
            # The same local CLOCK_BOOTTIME domain as the actual packaged host.
            # Keep the production 250 ms direct-write bound unchanged.
            receipt = native.request(
                connection,
                "direct",
                {
                    "generation": "native-default",
                    "target": original["target"],
                    "authority": snapshot["authority"],
                    "approved": snapshot["authority"],
                    "expiresAtMs": time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1_000_000 + 240,
                    "plan": {
                        "id": "firefox-one-write",
                        "expected": {
                            "target": {
                                "backend": "linux-browser",
                                "value": original["target"],
                                "lifetime": hello["data"]["lifetime"],
                            },
                            "text": snapshot["text"],
                            "selection": snapshot["selection"],
                        },
                        "scope": None,
                        "replacement": replacement,
                        "resultText": replacement,
                        "caret": len(replacement),
                    },
                },
            )
            self.assertEqual(receipt["status"], "Verified")
            self.assertEqual(self.source_value(web), replacement)

            second = web("POST", "/window/new", {"type": "tab"})["handle"]
            web("POST", "/window", {"handle": second})
            web("POST", "/url", {"url": url + "/second"})
            self.type_source(web, "Second document")
            current = native.focused_description(connection, previous=original["target"])
            snapshot = self.capture(native, connection, current, web)
            self.assertEqual(snapshot["text"], "Second document")
            web("POST", "/window", {"handle": first})
            web(
                "POST",
                "/execute/sync",
                {
                    "script": "document.querySelector('#source').remove();",
                    "args": [],
                },
            )
            self.assertIsNone(
                native.request(connection, "describe", {"target": original["target"]})
            )
            web("POST", "/window", {"handle": second})
            self.assertEqual(self.source_value(web), "Second document")
            if self.editor_bundle:
                web("POST", "/url", {"url": url + "/rich"})
                web(
                    "POST",
                    "/execute/sync",
                    {"script": "return editorProbeSetup('guarded');", "args": []},
                )
                # A real non-composing key establishes the isolated adapter's
                # initial observation; page code never asserts IME authority.
                web(
                    "POST",
                    "/actions",
                    {
                        "actions": [
                            {
                                "type": "key",
                                "id": "keyboard",
                                "actions": [
                                    {"type": "keyDown", "value": "\ue008"},
                                    {"type": "keyUp", "value": "\ue008"},
                                ],
                            }
                        ]
                    },
                )
                rich = native.focused_description(connection, previous=current["target"])
                snapshot = self.capture(native, connection, rich, web)
                self.assertEqual(snapshot["text"], "Before hello world after untouched")
                receipt = native.request(
                    connection,
                    "direct",
                    {
                        "generation": "native-default",
                        "target": rich["target"],
                        "authority": snapshot["authority"],
                        "approved": snapshot["authority"],
                        "expiresAtMs": time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1_000_000
                        + 240,
                        "plan": {
                            "id": "firefox-rich-write",
                            "expected": {
                                "target": {
                                    "backend": "linux-browser",
                                    "value": rich["target"],
                                    "lifetime": hello["data"]["lifetime"],
                                },
                                "text": snapshot["text"],
                                "selection": snapshot["selection"],
                            },
                            "scope": {"start": 7, "end": 12},
                            "replacement": "bonjour",
                            "resultText": "Before bonjour world after untouched",
                            "caret": 14,
                        },
                    },
                )
                self.assertEqual(receipt["status"], "Verified")
                actual = web(
                    "POST", "/execute/sync", {"script": "return editorProbeInspect();", "args": []}
                )
                self.assertEqual(
                    actual["after"],
                    "<p>Before <strong>bonjour world</strong> after <em>untouched</em></p>",
                )
                self.assertTrue(actual["preserved"])
                history = web(
                    "POST", "/execute/sync", {"script": "return editorProbeHistory();", "args": []}
                )
                self.assertEqual(
                    history["undone"],
                    "<p>Before <strong>hello world</strong> after <em>untouched</em></p>",
                )
                self.assertEqual(history["redone"], actual["after"])

            # Navigation can retain content-script ports in Firefox's bfcache
            # (Mozilla bug 1370368). Close the actual browsing contexts, including
            # their histories, while keeping one unmatched blank tab alive.
            old_lifetime = hello["data"]["lifetime"]
            blank = web("POST", "/window/new", {"type": "tab"})["handle"]
            web("POST", "/window", {"handle": blank})
            self.assertEqual(web("GET", "/url"), "about:blank")
            for handle in (first, second):
                web("POST", "/window", {"handle": handle})
                web("DELETE", "/window")
            web("POST", "/window", {"handle": blank})
            self.assertEqual(web("GET", "/window/handles"), [blank])
            deadline = time.monotonic() + 10
            while True:
                remaining = deadline - time.monotonic()
                self.assertGreater(remaining, 0, "Old native connection never closed")
                connection.settimeout(remaining)
                if not connection.recv(65536):
                    break
            web("POST", "/url", {"url": url + "/reconnected"})
            self.type_source(web, "Fresh document")
            fresh_connection, _ = listener.accept()
            fresh_connection = stack.enter_context(fresh_connection)
            fresh_connection.settimeout(10)
            fresh_hello = native.receive(fresh_connection)
            self.assertEqual(fresh_hello["event"], "hello")
            self.assertNotEqual(fresh_hello["data"]["lifetime"], old_lifetime)
            fresh_description = native.focused_description(fresh_connection)
            self.assertNotEqual(fresh_description["target"], current["target"])
            # A new native handshake does not make its first description a
            # durable observation. Use the same bounded fresh-read path as above.
            fresh_snapshot = self.capture(native, fresh_connection, fresh_description, web)
            self.assertIsNotNone(fresh_snapshot)
            self.assertEqual(fresh_snapshot["text"], "Fresh document")
            self.assertIsNone(
                native.request(fresh_connection, "describe", {"target": current["target"]})
            )

    def capture(self, native, connection, description, web):
        # Match production observation polling: each read gets a NEW metadata
        # approval for this exact target. Never replay expired authority or a write.
        target = description["target"]
        deadline = time.monotonic() + 3
        previous_timeout = connection.gettimeout()
        rejected = 0
        first_failure = None
        latest = None

        def summary(value):
            if value is None:
                return None
            authority = value["authority"]
            return {
                "focused": value["focused"],
                "imeKnown": value["metadata"]["hasTextEdit"],
                **{
                    key: authority[key]
                    for key in ("observedAtMs", "contentRevision", "inputRevision", "focusRevision")
                },
            }

        try:
            while time.monotonic() < deadline:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                connection.settimeout(remaining)
                started = time.monotonic()
                latest = native.request(connection, "describe", {"target": target})
                describe_ms = round((time.monotonic() - started) * 1000, 2)
                if latest is None or not latest["focused"]:
                    rejected += 1
                    print(
                        f"Firefox observation: describeMs={describe_ms} not-ready rejected={rejected}",
                        flush=True,
                    )
                    continue
                self.assertEqual(latest["target"], target)
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                connection.settimeout(remaining)
                before = time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1_000_000
                snapshot = native.request(
                    connection,
                    "capture",
                    {
                        "target": target,
                        "authority": latest["authority"],
                    },
                )
                after = time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1_000_000
                if snapshot is None:
                    rejected += 1
                    if first_failure is None:
                        first_failure = {
                            "beforeMs": before,
                            "afterMs": after,
                            "description": summary(latest),
                        }
                print(
                    "Firefox observation: "
                    + json.dumps(
                        {
                            "describeMs": describe_ms,
                            "captureMs": after - before,
                            "ready": snapshot is not None,
                            "rejected": rejected,
                        }
                    ),
                    flush=True,
                )
                if snapshot is not None:
                    self.assertEqual(snapshot["target"], target)
                    return snapshot
        except TimeoutError:
            pass  # The bounded observation remains failed below; never turn timeout into success.
        finally:
            connection.settimeout(previous_timeout)

        page = web(
            "POST",
            "/execute/sync",
            {
                "script": """return {focused: document.hasFocus(), visible: !document.hidden,
              wall: Date.now(), monotonic: performance.now(), events: window.fixtureEvents};""",
                "args": [],
            },
        )
        self.fail(
            "Firefox capture did not become ready within 3 seconds: "
            + json.dumps(
                {
                    "rejected": rejected,
                    "firstFailure": first_failure,
                    "lastDescription": summary(latest),
                    "page": page,
                },
                separators=(",", ":"),
            )
        )

    @staticmethod
    def type_source(web, text):
        element = web("POST", "/element", {"using": "css selector", "value": "#source"})[ELEMENT]
        web("POST", f"/element/{element}/click", {})
        web("POST", f"/element/{element}/value", {"text": text})

    @staticmethod
    def source_value(web):
        return web(
            "POST",
            "/execute/sync",
            {
                "script": "return document.querySelector('#source').value;",
                "args": [],
            },
        )


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--firefox", default=shutil.which("firefox") or "/usr/bin/firefox")
    parser.add_argument("--editor-bundle", type=Path)
    parser.add_argument(
        "--geckodriver", default=shutil.which("geckodriver") or "/usr/bin/geckodriver"
    )
    arguments, remaining = parser.parse_known_args()
    FirefoxNativeContract.package = arguments.package.resolve()
    FirefoxNativeContract.executable = arguments.firefox
    FirefoxNativeContract.driver = arguments.geckodriver
    FirefoxNativeContract.editor_bundle = (
        arguments.editor_bundle.resolve(strict=True) if arguments.editor_bundle else None
    )
    unittest.main(argv=[__file__, *remaining])
