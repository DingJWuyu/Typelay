"""Model download HTTP boundary with actual private cache files, without model downloads."""

import argparse
import asyncio
import contextlib
import errno
import hashlib
import io
import json
import os
import runpy
import shutil
import signal
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
import unittest
import urllib.error
import zipfile
from email.message import Message
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
sys.path.insert(0, str(ROOT / "src/shared/TypeLay.Worker"))
import setup  # noqa: E402
from application_support import ApplicationSupport  # noqa: E402
from paths import Paths  # noqa: E402
from typelay_worker.catalog import load_catalog  # noqa: E402
from wire import now_ms  # noqa: E402

DIRECTIONS = {direction.id: direction for direction in load_catalog()}


class Response(io.BytesIO):
    def __init__(self, body, status=200, headers=None, interrupt=False):
        super().__init__(body)
        self.status = status
        self.headers = Message()
        for key, value in (headers or {}).items():
            self.headers[key] = str(value)
        self.interrupt = interrupt

    def read(self, size=-1):
        if self.interrupt and self.tell():
            raise OSError("interrupted transport")
        return super().read(size)


class CatalogTests(unittest.TestCase):
    def test_relocated_setup_reads_the_selected_distribution_catalog(self):
        with tempfile.TemporaryDirectory(prefix="typelay-catalog-搬迁 ") as directory:
            home = Path(directory)
            shutil.copytree(ROOT / "src/shared/TypeLay.Worker", home / "worker")
            directions = setup.model_catalog(home)
            self.assertEqual([direction.id for direction in directions], ["zh_en", "en_zh"])
            self.assertEqual(directions[0].source_name, "中文")
            catalog = home / "worker/model-catalog.json"
            catalog.unlink()
            with self.assertRaisesRegex(ValueError, "^model-catalog$"):
                setup.model_catalog(home)


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="typelay-download-")
        self.addCleanup(self.directory.cleanup)
        self.cache = Path(self.directory.name)
        self.requests = []
        self.responses = []
        opener = self
        self.patch = patch.object(setup.urllib.request, "build_opener", return_value=opener)
        self.patch.start()
        self.addCleanup(self.patch.stop)

    def open(self, request, timeout):
        self.requests.append(request)
        self.assertEqual(timeout, 45)
        self.assertEqual(
            request.full_url, "https://argos-net.com/v1/translate-zh_en-1_9.argosmodel"
        )
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response

    def interrupted(self, **headers):
        self.responses.append(
            Response(
                b"first", headers={"Content-Length": 11, "ETag": '"v1"', **headers}, interrupt=True
            )
        )
        with self.assertRaises(OSError):
            setup.download(DIRECTIONS["zh_en"], self.cache)

    def test_interruption_resumes_only_the_same_representation(self):
        self.interrupted()
        self.responses.append(
            Response(
                b"second",
                206,
                {"Content-Length": 6, "Content-Range": "bytes 5-10/11", "ETag": '"v1"'},
            )
        )
        archive, _ = setup.download(DIRECTIONS["zh_en"], self.cache)
        self.assertEqual(self.requests[-1].get_header("Range"), "bytes=5-")
        self.assertEqual(self.requests[-1].get_header("If-range"), '"v1"')
        self.assertEqual(archive.read_bytes(), b"firstsecond")

    def test_ignored_range_overwrites_partial_instead_of_appending(self):
        self.interrupted()
        self.responses.append(Response(b"new model", headers={"Content-Length": 9, "ETag": '"v2"'}))
        archive, _ = setup.download(DIRECTIONS["zh_en"], self.cache)
        self.assertEqual(archive.read_bytes(), b"new model")

    def test_changed_etag_or_wrong_range_restarts_without_mixing_bytes(self):
        for headers in (
            {"Content-Length": 6, "Content-Range": "bytes 5-10/11", "ETag": '"v2"'},
            {"Content-Length": 6, "Content-Range": "bytes 4-9/11", "ETag": '"v1"'},
        ):
            with self.subTest(headers=headers):
                self.interrupted()
                self.responses.extend(
                    [
                        Response(b"unsafe", 206, headers),
                        Response(b"new model", headers={"Content-Length": 9, "ETag": '"v2"'}),
                    ]
                )
                archive, _ = setup.download(DIRECTIONS["zh_en"], self.cache)
                self.assertIsNone(self.requests[-1].get_header("Range"))
                self.assertEqual(archive.read_bytes(), b"new model")
                archive.unlink()

    def test_invalid_cached_prefix_is_not_resumed(self):
        self.interrupted()
        (self.cache / "zh_en-1.9.part").write_bytes(b"other")
        self.responses.append(Response(b"whole", headers={"Content-Length": 5}))
        archive, _ = setup.download(DIRECTIONS["zh_en"], self.cache)
        self.assertIsNone(self.requests[-1].get_header("Range"))
        self.assertEqual(archive.read_bytes(), b"whole")

    def test_malformed_checkpoint_headers_are_discarded(self):
        for headers in (None, []):
            with self.subTest(headers=headers):
                self.interrupted()
                checkpoint = self.cache / "zh_en-1.9.part.json"
                saved = json.loads(checkpoint.read_text())
                saved["validatorHeaders"] = headers
                checkpoint.write_text(json.dumps(saved))
                self.responses.append(Response(b"whole", headers={"Content-Length": 5}))
                archive, _ = setup.download(DIRECTIONS["zh_en"], self.cache)
                self.assertIsNone(self.requests[-1].get_header("Range"))
                self.assertEqual(archive.read_bytes(), b"whole")
                archive.unlink()

    def test_weak_etag_without_strong_date_cannot_resume(self):
        self.interrupted(ETag='W/"v1"')
        self.responses.append(Response(b"whole", headers={"Content-Length": 5}))
        archive, _ = setup.download(DIRECTIONS["zh_en"], self.cache)
        self.assertIsNone(self.requests[-1].get_header("Range"))
        self.assertEqual(archive.read_bytes(), b"whole")

    def test_weak_etag_cannot_fall_back_to_last_modified(self):
        self.interrupted(
            **{
                "ETag": 'W/"v1"',
                "Last-Modified": "Mon, 12 Oct 2026 16:00:00 GMT",
                "Date": "Mon, 12 Oct 2026 17:00:00 GMT",
            }
        )
        self.responses.append(Response(b"whole", headers={"Content-Length": 5}))
        archive, _ = setup.download(DIRECTIONS["zh_en"], self.cache)
        self.assertIsNone(self.requests[-1].get_header("If-range"))
        self.assertEqual(archive.read_bytes(), b"whole")

    def test_strong_last_modified_can_resume_without_an_etag(self):
        headers = {
            "ETag": "",
            "Last-Modified": "Mon, 12 Oct 2026 16:00:00 GMT",
            "Date": "Mon, 12 Oct 2026 17:00:00 GMT",
        }
        self.interrupted(**headers)
        self.responses.append(
            Response(
                b"second", 206, {**headers, "Content-Length": 6, "Content-Range": "bytes 5-10/11"}
            )
        )
        archive, _ = setup.download(DIRECTIONS["zh_en"], self.cache)
        self.assertEqual(self.requests[-1].get_header("If-range"), headers["Last-Modified"])
        self.assertEqual(archive.read_bytes(), b"firstsecond")

    def test_unsatisfied_range_restarts_once(self):
        self.interrupted()
        self.responses.extend(
            [
                urllib.error.HTTPError("https://argos-net.com/model", 416, "range", {}, None),
                Response(b"whole", headers={"Content-Length": 5}),
            ]
        )
        archive, _ = setup.download(DIRECTIONS["zh_en"], self.cache)
        self.assertEqual(archive.read_bytes(), b"whole")
        self.assertIsNone(self.requests[-1].get_header("Range"))

    def test_download_limit_does_not_publish_an_archive(self):
        self.responses.append(
            Response(b"too much", headers={"Content-Length": setup.MAX_ARCHIVE + 1})
        )
        with self.assertRaises(ValueError):
            setup.download(DIRECTIONS["zh_en"], self.cache)
        self.assertFalse((self.cache / "zh_en-1.9.argosmodel").exists())

    def test_pause_preserves_a_verified_prefix(self):
        class Pausing(Response):
            def read(self, size=-1):
                if self.tell():
                    raise setup.Paused()
                return super().read(size)

        self.responses.append(Pausing(b"first", headers={"Content-Length": 11, "ETag": '"v1"'}))
        with self.assertRaises(setup.Paused):
            setup.download(DIRECTIONS["zh_en"], self.cache)
        self.responses.append(
            Response(
                b"second",
                206,
                {"Content-Length": 6, "Content-Range": "bytes 5-10/11", "ETag": '"v1"'},
            )
        )
        archive, _ = setup.download(DIRECTIONS["zh_en"], self.cache)
        self.assertEqual(self.requests[-1].get_header("Range"), "bytes=5-")
        self.assertEqual(archive.read_bytes(), b"firstsecond")


class HTTPDownloadTests(unittest.TestCase):
    """Optional CI transport check, with a real loopback HTTP server, no model."""

    def test_socket_interruption_range_and_changed_resource(self):
        responses, requests = [], []

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                requests.append(dict(self.headers))
                status, headers, body = responses.pop(0)
                self.send_response(status)
                for name, value in headers.items():
                    self.send_header(name, str(value))
                self.end_headers()
                self.wfile.write(body)
                self.wfile.flush()
                self.close_connection = True

            def log_message(self, *_):
                pass

        server = HTTPServer(("127.0.0.1", 0), Handler)
        server.timeout = 2
        self.addCleanup(server.server_close)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(thread.join, 5)
        self.addCleanup(server.shutdown)
        transport = setup.urllib.request.build_opener(setup.urllib.request.ProxyHandler({}))

        class LocalTransport:
            def open(self, request, timeout):
                # Only this test's transport is relocated. Production still
                # constructs the fixed official HTTPS URL and origin policy.
                assert request.full_url == "https://argos-net.com/v1/translate-zh_en-1_9.argosmodel"
                local = setup.urllib.request.Request(
                    f"http://127.0.0.1:{server.server_port}/model",
                    headers=dict(request.header_items()),
                )
                return transport.open(local, timeout=timeout)

        with (
            tempfile.TemporaryDirectory(prefix="typelay-http-") as directory,
            patch.object(setup.urllib.request, "build_opener", return_value=LocalTransport()),
        ):
            cache = Path(directory)
            responses.append((200, {"Content-Length": 11, "ETag": '"v1"'}, b"first"))
            with self.assertRaises(OSError):
                setup.download(DIRECTIONS["zh_en"], cache)
            responses.append(
                (
                    206,
                    {"Content-Length": 6, "Content-Range": "bytes 5-10/11", "ETag": '"v1"'},
                    b"second",
                )
            )
            archive, _ = setup.download(DIRECTIONS["zh_en"], cache)
            self.assertEqual(archive.read_bytes(), b"firstsecond")
            self.assertEqual(requests[-1]["Range"], "bytes=5-")
            self.assertEqual(requests[-1]["If-Range"], '"v1"')
            archive.unlink()
            responses.append((200, {"Content-Length": 11, "ETag": '"v1"'}, b"first"))
            with self.assertRaises(OSError):
                setup.download(DIRECTIONS["zh_en"], cache)
            responses.append((200, {"Content-Length": 9, "ETag": '"v2"'}, b"new model"))
            archive, _ = setup.download(DIRECTIONS["zh_en"], cache)
            self.assertEqual(archive.read_bytes(), b"new model")
            self.assertEqual(requests[-1]["Range"], "bytes=5-")


class ApplicationSupportTests(unittest.TestCase):
    """Settings actions use the shipped installer against a real private HOME."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="typelay-support-中文 ")
        self.addCleanup(self.directory.cleanup)
        self.home = Path(self.directory.name)
        runtime = self.home / "runtime"
        runtime.mkdir(mode=0o700)
        environment = patch.dict(
            os.environ,
            {
                "HOME": str(self.home),
                "XDG_CONFIG_HOME": str(self.home / "config"),
                "XDG_DATA_HOME": str(self.home / "data"),
                "XDG_CACHE_HOME": str(self.home / "cache"),
                "XDG_RUNTIME_DIR": str(runtime),
            },
        )
        environment.start()
        self.addCleanup(environment.stop)
        self.package = self.home / ".local/lib/typelay/current"
        self.package.mkdir(parents=True)
        for name in ("install.py", "typelay-browser-host"):
            shutil.copy2(ROOT / "packaging/linux" / name, self.package / name)
        self.support = ApplicationSupport(Paths(self.package))

    def test_registered_application_contracts_in_an_isolated_gio_registry(self):
        # GIO caches XDG registry roots per process. Keep this suite's many HOME
        # fixtures separate from the real registered-entry/ELF contract process.
        subprocess.run(
            [
                sys.executable,
                str(Path(__file__).with_name("test_registered_applications.py")),
                "-v",
            ],
            check=True,
            timeout=30,
        )

    def test_browser_registration_is_reversible_and_not_a_live_connection(self):
        self.assertEqual(self.support.browser_status("chromium")["state"], "not-registered")
        result = asyncio.run(self.support.browser_action("chromium", "connect", "a" * 32))
        self.assertTrue(result["ok"], result)
        status = self.support.browser_status("chromium")
        self.assertEqual(status["state"], "registered")
        self.assertEqual(status["extensionId"], "a" * 32)
        manifest = self.home / "config/chromium/NativeMessagingHosts/org.typelay.browser.json"
        value = json.loads(manifest.read_text())
        self.assertEqual(value["allowed_origins"], ["chrome-extension://" + "a" * 32 + "/"])
        self.assertEqual(value["path"], str(self.package / "typelay-browser-host"))
        self.assertNotIn("connected", status)
        result = asyncio.run(self.support.browser_action("chromium", "remove"))
        self.assertTrue(result["ok"], result)
        self.assertFalse(manifest.exists())
        self.assertEqual(self.support.browser_status("chromium")["state"], "not-registered")

    def test_browser_launch_keeps_firefox_navigation_separate_from_chromium(self):
        commands = self.home / "commands"
        commands.mkdir()
        for browser, expected in (
            ("chromium", ["about:blank"]),
            ("google-chrome", ["about:blank"]),
            ("firefox", ["--new-tab", "about:debugging#/runtime/this-firefox"]),
        ):
            with self.subTest(browser=browser):
                output = self.home / (browser + "-arguments.json")
                launcher = commands / browser
                launcher.write_text(
                    "#!/usr/bin/python3\nimport json,sys\nfrom pathlib import Path\n"
                    + f"output=Path({str(output)!r})\n"
                    "temporary=output.with_suffix('.tmp')\n"
                    "temporary.write_text(json.dumps(sys.argv[1:]))\n"
                    "temporary.replace(output)\n"
                )
                launcher.chmod(0o700)
                subprocess.run(
                    [
                        "/usr/bin/python3",
                        "-I",
                        "-B",
                        "-c",
                        "import asyncio,sys; from pathlib import Path; "
                        "from types import SimpleNamespace; sys.path.insert(0, sys.argv[1]); "
                        "from application_support import ApplicationSupport; "
                        "support=ApplicationSupport(SimpleNamespace(home=Path(sys.argv[2]))); "
                        "asyncio.run(support.execute(sys.argv[3], 'OpenInstructions'))",
                        str(ROOT / "src/linux/desktop"),
                        str(self.package),
                        browser,
                    ],
                    env={**os.environ, "PATH": str(commands) + os.pathsep + os.environ["PATH"]},
                    check=True,
                    timeout=5,
                    capture_output=True,
                )
                deadline = time.monotonic() + 3
                while not output.exists():
                    self.assertLess(time.monotonic(), deadline, "Gio did not launch the recorder")
                    time.sleep(0.01)
                self.assertEqual(json.loads(output.read_text()), expected)
                self.assertEqual(self.support.browser_status(browser)["state"], "not-registered")

    def test_registration_refuses_foreign_files_and_preserves_browser_permissions(self):
        manifest = self.home / ".mozilla/native-messaging-hosts/org.typelay.browser.json"
        manifest.parent.mkdir(parents=True)
        original = '{"name":"org.typelay.browser","path":"/another/host","type":"stdio"}'
        manifest.write_text(original)
        for action in ("connect", "remove"):
            result = asyncio.run(self.support.browser_action("firefox", action))
            self.assertFalse(result["ok"], result)
            self.assertEqual(result["reason"], "foreign-browser-registration")
            self.assertEqual(manifest.read_text(), original)
        manifest.unlink()
        result = asyncio.run(self.support.browser_action("firefox", "connect"))
        self.assertTrue(result["ok"], result)
        self.assertEqual(
            json.loads(manifest.read_text())["allowed_extensions"], ["typelay@typelay.local"]
        )

    def test_invalid_extension_id_never_creates_a_registration(self):
        for extension_id in ("", "A" * 32, "z" * 32, "a" * 31, "../host"):
            result = asyncio.run(
                self.support.browser_action("google-chrome", "connect", extension_id)
            )
            self.assertFalse(result["ok"], result)
            self.assertEqual(result["reason"], "chromium-extension-id-required")
        self.assertEqual(self.support.browser_status("google-chrome")["state"], "not-registered")

    def test_remove_registration_still_works_after_host_was_lost(self):
        self.assertTrue(asyncio.run(self.support.browser_action("firefox", "connect"))["ok"])
        (self.package / "typelay-browser-host").unlink()
        result = asyncio.run(self.support.browser_action("firefox", "remove"))
        self.assertTrue(result["ok"], result)
        self.assertFalse(
            (self.home / ".mozilla/native-messaging-hosts/org.typelay.browser.json").exists()
        )

    def test_removed_registration_waits_for_the_existing_browser_connection_to_close(self):
        self.assertTrue(asyncio.run(self.support.execute("firefox", "Connect"))["ok"])
        peer = SimpleNamespace(
            lifetime="existing-browser-session",
            backend="browser",
            application="firefox",
            link=SimpleNamespace(channel=SimpleNamespace(alive=True)),
            support_observation={
                "atMs": now_ms(),
                "preview": True,
                "tabAccept": True,
                "directReplace": True,
            },
        )

        def row():
            return next(fact for fact in self.support.facts([peer]) if fact["id"] == "firefox")

        self.assertEqual(row()["state"], "Connected")
        result = asyncio.run(self.support.execute("firefox", "Remove"))
        self.assertTrue(result["ok"], result)
        self.assertEqual(self.support.browser_status("firefox")["state"], "not-registered")
        self.assertEqual(row()["state"], "RestartRequired")
        self.assertEqual(row()["reason"], "RestartRequired")
        self.assertEqual(row()["supportedActions"], ["Refresh", "OpenInstructions"])
        self.assertFalse(any(row()["capabilities"].values()))
        self.assertIsNone(row()["checkedAt"])
        asyncio.run(self.support.execute("firefox", "Refresh"))
        self.assertEqual(row()["state"], "RestartRequired")
        peer.link.channel.alive = False
        self.assertEqual(row()["state"], "NeedsInstall")
        self.assertEqual(row()["reason"], "RegistrationMissing")
        self.assertIn("Connect", row()["supportedActions"])

    def test_connect_uses_only_the_profile_extension_for_this_exact_package(self):
        folder = self.package / "browser/chromium"
        folder.mkdir(parents=True)
        shutil.copy2(ROOT / "src/linux/browser/manifest.chromium.json", folder / "manifest.json")
        profile = self.home / "config/chromium/Default"
        profile.mkdir(parents=True)
        preferences = profile / "Preferences"
        preferences.write_text(
            json.dumps(
                {
                    "extensions": {
                        "settings": {
                            "b" * 32: {"path": str(self.home / "unrelated-extension")},
                            "c" * 32: {"path": str(folder)},
                        }
                    }
                }
            )
        )
        result = asyncio.run(self.support.browser_action("chromium", "connect"))
        self.assertTrue(result["ok"], result)
        self.assertEqual(self.support.browser_status("chromium")["extensionId"], "c" * 32)
        asyncio.run(self.support.browser_action("chromium", "remove"))
        preferences.write_text(
            json.dumps(
                {
                    "extensions": {
                        "settings": {
                            "b" * 32: {"path": str(self.home / "unrelated-extension")},
                        }
                    }
                }
            )
        )
        result = asyncio.run(self.support.browser_action("chromium", "connect"))
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["reason"], "browser-extension-unconfirmed")
        self.assertEqual(self.support.browser_status("chromium")["state"], "not-registered")

    def test_browser_approval_is_the_next_step_until_one_shipped_extension_is_found(self):
        folder = self.package / "browser/chromium"
        folder.mkdir(parents=True)
        shutil.copy2(ROOT / "src/linux/browser/manifest.chromium.json", folder / "manifest.json")

        def row():
            return next(fact for fact in self.support.facts() if fact["id"] == "chromium")

        self.assertEqual(row()["state"], "AwaitingApproval")
        self.assertNotIn("Connect", row()["supportedActions"])
        profile = self.home / "config/chromium/Default"
        profile.mkdir(parents=True)
        preferences = profile / "Preferences"
        preferences.write_text('{"extensions":')
        self.assertEqual(row()["state"], "AwaitingApproval")
        preferences.write_text(
            json.dumps({"extensions": {"settings": {"c" * 32: {"path": str(folder)}}}})
        )
        self.assertEqual(row()["state"], "NeedsInstall")
        self.assertIn("Connect", row()["supportedActions"])
        preferences.write_text(
            json.dumps(
                {
                    "extensions": {
                        "settings": {
                            "c" * 32: {"path": str(folder)},
                            "d" * 32: {"path": str(folder)},
                        }
                    }
                }
            )
        )
        self.assertEqual(row()["state"], "AwaitingApproval")
        self.assertNotIn("Connect", row()["supportedActions"])
        result = asyncio.run(self.support.execute("chromium", "Connect"))
        self.assertFalse(result["ok"], result)
        self.assertEqual(self.support.browser_status("chromium")["state"], "not-registered")

    def test_recent_live_field_observation_is_separate_from_registration_and_pause(self):
        self.assertTrue(asyncio.run(self.support.browser_action("firefox", "connect"))["ok"])
        peer = SimpleNamespace(
            lifetime="owned-connection",
            backend="browser",
            application="firefox",
            link=SimpleNamespace(channel=SimpleNamespace(alive=True)),
            support_observation={
                "atMs": now_ms(),
                "preview": True,
                "tabAccept": True,
                "directReplace": True,
            },
        )

        def row(enabled=True):
            return next(
                fact
                for fact in self.support.facts([peer], enabled=enabled)
                if fact["id"] == "firefox"
            )

        observed = row()
        self.assertEqual(observed["state"], "Connected")
        self.assertEqual(observed["reason"], "LimitedFields")
        self.assertEqual(
            observed["capabilities"], {"preview": True, "tabAccept": True, "directReplace": True}
        )
        self.assertIsNotNone(observed["checkedAt"])
        paused = row(False)
        self.assertEqual(paused["state"], "Connected")
        self.assertEqual(paused["reason"], "Paused")
        self.assertFalse(any(paused["capabilities"].values()))
        self.assertIsNone(paused["checkedAt"])
        peer.link.channel.alive = False
        disconnected = row()
        self.assertEqual(disconnected["state"], "Registered")
        self.assertFalse(any(disconnected["capabilities"].values()))
        self.assertIsNone(disconnected["checkedAt"])
        peer.link.channel.alive = True
        peer.support_observation["atMs"] = now_ms() - 30001
        self.assertEqual(row()["reason"], "FieldUnobserved")
        self.assertFalse(any(row()["capabilities"].values()))
        peer.support_observation["atMs"] = now_ms() + 60000
        self.assertEqual(row()["reason"], "FieldUnobserved")
        peer.support_observation["atMs"] = now_ms()
        peer.support_observation["preview"] = "true"
        self.assertEqual(row()["reason"], "FieldUnobserved")

    def test_desktop_uses_latest_live_observation_without_merging_fields(self):
        def peer(stamp, tab, direct):
            return SimpleNamespace(
                lifetime="owned-connection",
                backend="gtk3",
                application="fixture",
                link=SimpleNamespace(channel=SimpleNamespace(alive=True)),
                support_observation={
                    "atMs": stamp,
                    "preview": True,
                    "tabAccept": tab,
                    "directReplace": direct,
                },
            )

        stamp = now_ms()
        older = peer(stamp - 1000, True, True)
        newer = peer(stamp, False, False)
        rows = self.support.facts([newer, older])
        result = next(fact for fact in rows if fact["id"] == "desktop-text")
        self.assertFalse(self.support.installed_application_view(rows)["canSelect"])
        self.assertEqual(result["reason"], "PreviewOnly")
        self.assertEqual(
            result["capabilities"], {"preview": True, "tabAccept": False, "directReplace": False}
        )
        newer.support_observation = None
        older.link.channel.alive = False
        result = next(
            fact for fact in self.support.facts([newer, older]) if fact["id"] == "desktop-text"
        )
        self.assertEqual(result["reason"], "FieldUnobserved")
        self.assertFalse(any(result["capabilities"].values()))

    def test_uninstalled_package_can_create_the_user_install_before_browser_connection(self):
        extracted = self.home / "extracted TypeLay"
        shutil.copytree(self.package, extracted, ignore=shutil.ignore_patterns("__pycache__"))
        shutil.copy2(ROOT / "packaging/linux/typelay", extracted / "typelay")
        lines = [
            f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n"
            for path in sorted(extracted.iterdir())
        ]
        (extracted / "MANIFEST.sha256").write_text("".join(lines))
        shutil.rmtree(self.home / ".local/lib/typelay")
        self.support = ApplicationSupport(Paths(extracted))
        self.assertEqual(self.support.browser_status("firefox")["reason"], "browser-host-missing")
        result = asyncio.run(self.support.execute("firefox", "Install"))
        self.assertTrue(result["ok"], result)
        self.assertTrue((self.home / ".local/bin/typelay").is_symlink())
        self.assertTrue((self.home / ".local/lib/typelay/current/typelay-browser-host").is_file())
        self.assertEqual(self.support.browser_status("firefox")["state"], "not-registered")
        self.assertTrue(asyncio.run(self.support.browser_action("firefox", "connect"))["ok"])
        self.assertEqual(self.support.browser_status("firefox")["state"], "registered")

    def test_owned_browser_registration_can_be_repaired_after_package_path_changes(self):
        self.assertTrue(
            asyncio.run(self.support.browser_action("chromium", "connect", "d" * 32))["ok"]
        )
        old_package = self.package
        release = self.home / ".local/lib/typelay/releases/relocated"
        release.parent.mkdir(parents=True)
        old_package.rename(release)
        old_package.symlink_to("releases/relocated", target_is_directory=True)
        reopened = ApplicationSupport(Paths(release))
        self.assertEqual(reopened.browser_extension_ids("chromium"), [])
        result = asyncio.run(reopened.browser_action("chromium", "connect"))
        self.assertTrue(result["ok"], result)
        self.assertEqual(reopened.browser_status("chromium")["extensionId"], "d" * 32)
        self.assertEqual(reopened.browser_status("chromium")["state"], "registered")

    def desktop_payload(self, *, extra_entries=(), version="5.1.22", content_overrides=None):
        support = self.package / "support/desktop"
        support.mkdir(parents=True, exist_ok=True)
        marker = self.home / "must-not-run-component"
        content = {
            "fcitx-cooperative.py": f"from pathlib import Path; Path({str(marker)!r}).touch()".encode(),
            "core/bin/fcitx5": b"not executable in preparation",
            "README.md": b"Explicit private session instructions",
            "THIRD_PARTY_NOTICES.md": b"Third party notices",
            "source/eng/linux/cooperative/fcitx.py": b"Corresponding rebuild recipe",
            "source/upstream/fcitx5/LICENSES/LGPL-2.1-or-later.txt": b"License text",
            "lib/libexample.so.1": b"Cooperative library",
        }
        content.update(content_overrides or {})
        manifest = {
            "schemaVersion": 1,
            "bundleName": "TypeLay-Fcitx-cooperative",
            "coreVersion": version,
            "versions": {
                "component_source_versions": {"fcitx5": version},
                "installed_private_versions": {"Fcitx5Core": version},
                "server_source_version": version,
                "server_runtime_version": version,
                "linked_core_development_version": version,
            },
            "qt6": None,
            "files": {
                key: {"sha256": hashlib.sha256(value).hexdigest()} for key, value in content.items()
            },
        }
        manifest["files"]["lib/libexample.so"] = {"symlink": "libexample.so.1"}
        encoded = json.dumps(manifest).encode()
        archive = support / "TypeLay-Fcitx-cooperative.tar.gz"
        with tarfile.open(archive, "w:gz") as output:
            for name, value in {**content, "manifest.json": encoded}.items():
                item = tarfile.TarInfo("TypeLay-Fcitx-cooperative/" + name)
                item.mode = 0o755 if name == "fcitx-cooperative.py" else 0o644
                item.size = len(value)
                output.addfile(item, io.BytesIO(value))
            link = tarfile.TarInfo("TypeLay-Fcitx-cooperative/lib/libexample.so")
            link.type = tarfile.SYMTYPE
            link.linkname = "libexample.so.1"
            output.addfile(link)
            for item, value in extra_entries:
                output.addfile(item, io.BytesIO(value) if value is not None else None)
        descriptor = {
            "schemaVersion": 1,
            "archive": archive.name,
            "archiveSha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
            "manifestSha256": hashlib.sha256(encoded).hexdigest(),
            "sourceVersions": {"fcitx5": "5.1.22"},
            "privateVersions": {"Fcitx5Core": "5.1.22"},
            "qtVersion": None,
            "sourceCommit": "1" * 40,
        }
        (support / "component.json").write_text(json.dumps(descriptor))
        self.write_package_manifest()
        return descriptor, marker

    def write_package_manifest(self):
        (self.package / "MANIFEST.sha256").write_text(
            "".join(
                f"{hashlib.sha256(path.read_bytes()).hexdigest()}  "
                f"{path.relative_to(self.package).as_posix()}\n"
                for path in sorted(self.package.rglob("*"))
                if path.is_file() and path.name != "MANIFEST.sha256"
            )
        )

    def test_bundled_component_prepares_only_after_explicit_install_and_survives_reopen(self):
        descriptor, marker = self.desktop_payload()
        managed = self.home / "data/typelay/components/desktop-text"
        fact = next(row for row in self.support.facts() if row["id"] == "desktop-text")
        self.assertEqual(fact["state"], "NeedsInstall")
        self.assertFalse(self.support.installed_application_view([fact])["canSelect"])
        self.assertIn("Install", fact["supportedActions"])
        self.assertNotIn("CheckComponent", fact["supportedActions"])
        self.assertFalse(managed.exists(), "discovery prepared the component")
        result = asyncio.run(self.support.execute("desktop-text", "Install"))
        self.assertTrue(result["ok"], result)
        prepared = self.support.component_folder("desktop-text")
        self.assertTrue((prepared / "source/eng/linux/cooperative/fcitx.py").is_file())
        self.assertTrue((prepared / "lib/libexample.so").is_symlink())
        self.assertEqual((prepared / "lib/libexample.so").read_bytes(), b"Cooperative library")
        self.assertFalse(marker.exists(), "preparation executed the packaged launcher")
        self.assertEqual(
            hashlib.sha256(
                (self.package / "support/desktop/TypeLay-Fcitx-cooperative.tar.gz").read_bytes()
            ).hexdigest(),
            descriptor["archiveSha256"],
        )
        self.assertFalse((prepared.parent / "TypeLay-Fcitx-cooperative.tar.gz").exists())
        reopened = ApplicationSupport(Paths(self.package))
        self.assertEqual(reopened.component_folder("desktop-text"), prepared)
        fact = next(row for row in reopened.facts() if row["id"] == "desktop-text")
        self.assertEqual(fact["state"], "AwaitingApproval")
        self.assertTrue(reopened.installed_application_view([fact])["canSelect"])
        self.assertFalse(fact["capabilities"])
        self.assertNotIn("Connect", fact["supportedActions"])
        self.assertNotIn("Remove", fact["supportedActions"])
        result = asyncio.run(reopened.execute("desktop-text", "Install"))
        self.assertTrue(result["ok"], result)
        self.assertEqual(reopened.component_folder("desktop-text"), prepared)
        self.assertEqual(
            os.readlink(managed / "current"), "releases/" + descriptor["archiveSha256"]
        )

    def test_detected_system_service_is_information_not_permission_to_connect(self):
        self.desktop_payload()
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])
        self.support.desktop_session_state = {
            "state": "conflict",
            "reason": "existing-fcitx-session",
            "source": {"source": "user-service", "reason": "dedicated-user-service"},
        }
        fact = self.support.desktop_fact()
        self.assertIn("专用用户服务", fact["details"])
        self.assertIn("不会自动", fact["details"])
        self.assertNotIn("Connect", fact["supportedActions"])
        self.assertFalse(fact["capabilities"])
        self.support.desktop_session_state["source"] = {
            "source": "unconfirmed",
            "reason": "shared-process-group",
        }
        fact = self.support.desktop_fact()
        self.assertIn("尚未确认", fact["details"])
        self.assertNotIn("Connect", fact["supportedActions"])
        self.assertIn("不表示系统不支持", fact["details"])

    def test_desktop_guidance_requires_original_method_confirmation_before_connect(self):
        self.desktop_payload()
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])
        self.support.desktop_session_state = {"state": "inactive"}
        guidance = self.support.desktop_guidance()
        self.assertFalse(guidance["originalMethodRecorded"])
        self.assertTrue(guidance["canRecordOriginalMethod"])
        for requirement in ("启动方式", "手动恢复", "手动退出"):
            self.assertIn(requirement, guidance["notice"])
        self.assertNotIn("Connect", self.support.desktop_fact()["supportedActions"])
        self.assertTrue(self.support.record_original_method())
        self.assertIn("Connect", self.support.desktop_fact()["supportedActions"])
        self.assertIn("由你确认", self.support.desktop_guidance()["steps"])
        self.assertNotIn("记下", self.support.desktop_guidance()["notice"])
        self.assertFalse(self.support.desktop_guidance()["canRecordOriginalMethod"])
        reopened = ApplicationSupport(Paths(self.package))
        reopened.desktop_session_state = {"state": "inactive"}
        self.assertFalse(reopened.desktop_guidance()["originalMethodRecorded"])
        self.assertNotIn("Connect", reopened.desktop_fact()["supportedActions"])

    def test_unchecked_input_method_stays_in_details_until_components_are_ready(self):
        guidance = self.support.desktop_guidance()
        self.assertEqual(guidance["notice"], "")
        self.assertIn("尚未确认", guidance["steps"])
        self.assertFalse(guidance["canRecordOriginalMethod"])
        self.assertFalse(guidance["canRecordNoOriginalMethod"])

    def test_active_session_shows_only_current_exit_requirement_above_details(self):
        self.support.desktop_session_state = {"state": "active"}
        self.assertTrue(self.support.record_original_method())
        guidance = self.support.desktop_guidance()
        self.assertIn("应用或练习", guidance["notice"])
        self.assertIn("手动恢复", guidance["notice"])
        self.assertIn("移除", guidance["notice"])
        self.assertNotIn("记下", guidance["notice"])
        self.assertNotIn("开始练习", guidance["notice"])
        self.assertIn("已由你确认", guidance["steps"])
        self.assertFalse(guidance["canRecordOriginalMethod"])
        self.assertFalse(guidance["canRecordNoOriginalMethod"])

    def test_restoration_guidance_never_treats_detection_as_original_restore_proof(self):
        self.support.desktop_session_state = {"state": "unknown", "detail": "unknown"}
        self.assertFalse(self.support.record_original_method())
        self.assertFalse(self.support.desktop_guidance()["canRecordOriginalMethod"])
        self.assertIn("尚未确认", self.support.desktop_guidance()["steps"])
        self.assertIn("尚未确认", self.support.desktop_guidance()["notice"])
        self.support.practice_component_removed = True
        for source in ("user-service", "unconfirmed"):
            self.support.desktop_session_state = {
                "state": "conflict",
                "reason": "existing-fcitx-session",
                "source": {"source": source},
            }
            guidance = self.support.desktop_guidance()
            self.assertIn("检测到 Fcitx 正在运行", guidance["steps"])
            self.assertIn("不能证明原启动方式已恢复", guidance["steps"])
            self.assertIn("不能证明原启动方式已恢复", guidance["notice"])
            self.assertNotIn("Connect", self.support.desktop_fact()["supportedActions"])
        self.support.desktop_session_state = {"state": "inactive"}
        self.assertIn("手动恢复", self.support.desktop_guidance()["steps"])
        self.assertFalse(self.support.desktop_guidance()["originalMethodRecorded"])

    def test_no_original_method_choice_requires_confirmed_inactive_state(self):
        self.support.desktop_session_state = {"state": "unknown"}
        self.assertFalse(self.support.record_original_method(no_original=True))
        self.support.desktop_session_state = {"state": "conflict"}
        self.assertFalse(self.support.record_original_method(no_original=True))
        self.support.desktop_session_state = {"state": "inactive"}
        self.assertTrue(self.support.record_original_method(no_original=True))
        self.assertEqual(self.support.desktop_guidance()["notice"], "")
        self.support.desktop_session_state = {"state": "active"}
        self.assertIn("应用或练习", self.support.desktop_guidance()["notice"])
        self.assertIn("没有原输入法需要恢复", self.support.desktop_guidance()["notice"])
        self.assertNotIn("手动恢复", self.support.desktop_guidance()["notice"])
        self.support.desktop_session_state = {"state": "inactive"}
        self.support.practice_component_removed = True
        self.assertIn("没有原输入法需要恢复", self.support.desktop_guidance()["steps"])
        self.support.desktop_session_state = {"state": "unknown"}
        self.assertIn("不能证明", self.support.desktop_guidance()["steps"])

    def test_restore_recheck_still_observes_session_after_component_files_are_gone(self):
        from unittest.mock import AsyncMock

        self.support.practice_component_removed = True
        self.assertFalse(self.support.desktop_component.root.exists())
        with patch.object(
            self.support.desktop_session,
            "refresh",
            new=AsyncMock(return_value={"state": "conflict", "reason": "existing-ibus-session"}),
        ):
            asyncio.run(self.support.refresh())
        self.assertIn("检测到 IBus 正在运行", self.support.desktop_guidance()["steps"])
        self.assertIn("不能证明原启动方式已恢复", self.support.desktop_guidance()["steps"])

    def test_late_session_refresh_cannot_replace_newer_guidance_observation(self):
        self.support.practice_component_removed = True

        async def scenario():
            old_started, old_release = asyncio.Event(), asyncio.Event()
            calls = 0

            async def observed():
                nonlocal calls
                calls += 1
                if calls == 1:
                    old_started.set()
                    await old_release.wait()
                    return {"state": "unknown", "detail": "earlier observation"}
                return {"state": "inactive"}

            with patch.object(self.support.desktop_session, "refresh", side_effect=observed):
                old = asyncio.create_task(self.support.refresh())
                await old_started.wait()
                await self.support.refresh()
                old_release.set()
                await old
            self.assertIn("当前未检测到 Fcitx/IBus 冲突", self.support.desktop_guidance()["steps"])

        asyncio.run(scenario())

    def test_new_input_method_conflict_retires_no_original_confirmation(self):
        from unittest.mock import AsyncMock

        self.desktop_payload()
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])
        with self.no_input_method_environment():
            self.assertTrue(
                asyncio.run(self.support.execute("desktop-text", "RecordNoOriginalMethod"))["ok"]
            )
        with patch.object(
            self.support.desktop_session,
            "refresh",
            new=AsyncMock(return_value={"state": "conflict", "reason": "existing-ibus-session"}),
        ):
            asyncio.run(self.support.refresh())
        guidance = self.support.desktop_guidance()
        self.assertFalse(guidance["noOriginalMethod"])
        self.assertFalse(guidance["originalMethodRecorded"])
        self.assertTrue(guidance["canRecordOriginalMethod"])
        self.assertFalse(guidance["canRecordNoOriginalMethod"])
        with patch.dict(os.environ, {"PATH": str(self.home / "input-status-tools")}):
            asyncio.run(self.support.refresh())
        self.assertNotIn("Connect", self.support.desktop_fact()["supportedActions"])

    def test_live_connection_removes_duplicate_success_but_keeps_failed_application_result(self):
        self.support.selected_application = {"id": "org.typelay.Sample.desktop", "name": "Sample"}
        self.support.application_result = "已请求打开应用，尚未确认连接。"
        fact = self.support.desktop_fact(connected=True)
        view = self.support.installed_application_view([fact])
        self.assertEqual(view["name"], "Sample")
        self.assertEqual(view["result"], "")
        self.assertFalse(view["canOpen"])
        self.assertEqual(fact["capabilities"], {})
        self.support.application_failed = True
        self.support.application_result = "应用启动失败，请正常退出旧窗口后重试。"
        self.assertEqual(
            self.support.installed_application_view([fact])["result"],
            "应用启动失败，请正常退出旧窗口后重试。",
        )

    def test_component_preparation_success_does_not_hide_later_package_damage(self):
        self.desktop_payload()
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])
        manifest = self.support.component_folder("desktop-text") / "manifest.json"
        manifest.write_text("changed")
        fact = next(row for row in self.support.facts() if row["id"] == "desktop-text")
        self.assertEqual(fact["state"], "Error")
        self.assertEqual(fact["reason"], "ComponentMissing")
        self.assertFalse(fact["capabilities"])
        self.support.application_result = "应用未能打开，请重新检查输入法与组件状态。"
        view = self.support.installed_application_view([fact])
        self.assertFalse(view["canSelect"])
        self.assertEqual(view["result"], "应用未能打开，请重新检查输入法与组件状态。")

    def test_failed_component_update_preserves_previous_release(self):
        self.desktop_payload()
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])
        original = self.support.component_folder("desktop-text")
        current = self.home / "data/typelay/components/desktop-text/current"
        previous = os.readlink(current)
        self.desktop_payload(version="5.1.99")
        result = asyncio.run(self.support.execute("desktop-text", "Install"))
        self.assertFalse(result["ok"], result)
        self.assertEqual(os.readlink(current), previous)
        self.assertTrue((original / "core/bin/fcitx5").is_file())
        self.assertEqual(list(current.parent.glob("releases/.preparing-*")), [])

    def test_component_preparation_refuses_changed_main_package_and_archive(self):
        for changed in ("component.json", "TypeLay-Fcitx-cooperative.tar.gz"):
            with self.subTest(changed=changed):
                self.desktop_payload()
                path = self.package / "support/desktop" / changed
                with path.open("ab") as stream:
                    stream.write(b"changed")
                result = asyncio.run(self.support.execute("desktop-text", "Install"))
                self.assertFalse(result["ok"], result)
                self.assertFalse(
                    (self.home / "data/typelay/components/desktop-text/current").exists()
                )

    def test_component_archive_cannot_write_through_links_or_escape_its_release(self):
        attacks = []
        for name in ("TypeLay-Fcitx-cooperative/../../escaped", "/tmp/typelay-escaped"):
            item = tarfile.TarInfo(name)
            item.size = 6
            attacks.append((item, b"escape"))
        for kind in (tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.FIFOTYPE):
            item = tarfile.TarInfo("TypeLay-Fcitx-cooperative/unsafe")
            item.type = kind
            item.linkname = str(self.home / "outside")
            attacks.append((item, None))
        duplicate = tarfile.TarInfo("TypeLay-Fcitx-cooperative/core/bin/fcitx5")
        duplicate.size = 6
        attacks.append((duplicate, b"escape"))
        for item, value in attacks:
            with self.subTest(path=item.name, kind=item.type):
                self.desktop_payload(extra_entries=[(item, value)])
                result = asyncio.run(self.support.execute("desktop-text", "Install"))
                self.assertFalse(result["ok"], result)
                self.assertFalse((self.home / "outside").exists())
                managed = self.home / "data/typelay/components/desktop-text"
                self.assertFalse((managed / "current").exists())
                self.assertEqual(list(managed.glob("releases/.preparing-*")), [])
                self.assertFalse((managed / "releases/escaped").exists())

    def test_component_preparation_refuses_symlinked_user_directory(self):
        self.desktop_payload()
        outside = self.home / "outside"
        outside.mkdir()
        components = self.home / "data/typelay/components"
        components.symlink_to(outside, target_is_directory=True)
        result = asyncio.run(self.support.execute("desktop-text", "Install"))
        self.assertFalse(result["ok"], result)
        self.assertEqual(list(outside.iterdir()), [])

    def test_component_preparation_respects_existing_install_lock(self):
        self.desktop_payload()
        components = self.home / "data/typelay/components"
        components.mkdir()
        process = subprocess.Popen(
            [
                sys.executable,
                "-I",
                "-c",
                "import fcntl,sys; f=open(sys.argv[1], 'w'); "
                "fcntl.flock(f, fcntl.LOCK_EX); print('locked',flush=True); sys.stdin.read()",
                str(components / "install.lock"),
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            text=True,
        )
        try:
            self.assertEqual(process.stdout.readline().strip(), "locked")
            result = asyncio.run(self.support.execute("desktop-text", "Install"))
            self.assertFalse(result["ok"], result)
            self.assertFalse((components / "desktop-text/current").exists())
        finally:
            process.communicate(timeout=5)
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])

    def test_component_refresh_discards_failed_attempt_without_starting_anything(self):
        self.desktop_payload()
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])
        directory = self.home / "incomplete-selected-bundle"
        directory.mkdir()
        result = asyncio.run(self.support.execute("desktop-text", "CheckComponent", str(directory)))
        self.assertFalse(result["ok"])
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Refresh"))["ok"])
        fact = next(row for row in self.support.facts() if row["id"] == "desktop-text")
        self.assertEqual(fact["state"], "AwaitingApproval")
        self.assertEqual(fact["reason"], "UserApprovalRequired")
        self.assertFalse(fact["capabilities"])

    def no_input_method_environment(self):
        commands = self.home / "input-status-tools"
        commands.mkdir()
        for name, body in (("gdbus", "print('(false,)')"), ("pgrep", "raise SystemExit(1)")):
            command = commands / name
            command.write_text("#!/usr/bin/python3\n" + body + "\n")
            command.chmod(0o700)
        return patch.dict(os.environ, {"PATH": str(commands)})

    def test_connect_startup_notification_belongs_only_to_the_new_practice_process(self):
        from desktop_session import receipts

        # Real preparation, guardian, action and typelay launch boundaries; the
        # private daemon/application are controlled executables, not a desktop test.
        daemon_pid = self.home / "daemon.pid"
        daemon_observation = self.home / "daemon.json"
        application_observation = self.home / "application.json"
        finish = self.home / "finish-practice"
        environment_keys = "['DESKTOP_STARTUP_ID','XDG_ACTIVATION_TOKEN']"
        launcher = (
            "import json,os,sys\nfrom pathlib import Path\n"
            "root=Path(__file__).parent\n"
            "if sys.argv[1] == 'daemon':\n"
            f" Path({str(daemon_observation)!r}).write_text(json.dumps("
            f"{{key:os.environ.get(key) for key in {environment_keys}}}))\n"
            f" Path({str(daemon_pid)!r}).write_text(str(os.getpid()))\n"
            " os.execv(root/'core/bin/fcitx5', [str(root/'core/bin/fcitx5'), '60'])\n"
            "os.execvpe(sys.argv[2], sys.argv[2:], os.environ)\n"
        )
        self.desktop_payload(
            content_overrides={
                "fcitx-cooperative.py": launcher.encode(),
                "core/bin/fcitx5": Path("/usr/bin/sleep").read_bytes(),
            }
        )
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])
        bundle = self.support.desktop_component.status()["directory"]
        (bundle / "core/bin/fcitx5").chmod(0o755)
        shutil.copy2(ROOT / "packaging/linux/typelay", self.package / "typelay")
        (self.package / "typelay").chmod(0o755)
        desktop = self.package / "desktop"
        desktop.mkdir()
        for name in ("main.py", "setup.py", "paths.py", "wire.py"):
            shutil.copy2(ROOT / "src/linux/desktop" / name, desktop / name)
        (desktop / "practice.py").write_text(
            "import json,os,sys,time\nfrom pathlib import Path\n"
            f"Path({str(application_observation)!r}).write_text(json.dumps("
            f"{{'environment':{{key:os.environ.get(key) for key in {environment_keys}}},"
            "'arguments':sys.argv[1:]}))\n"
            f"while not Path({str(finish)!r}).exists(): time.sleep(0.01)\n"
        )
        (self.package / "native").mkdir()
        (self.package / "native/libtypelay-gtk3.so").touch()
        with (
            self.no_input_method_environment(),
            patch.dict(
                os.environ,
                {"DESKTOP_STARTUP_ID": "old-startup", "XDG_ACTIVATION_TOKEN": "old-activation"},
            ),
        ):
            for tool in ("readlink", "dirname"):
                (self.home / "input-status-tools" / tool).symlink_to(Path("/usr/bin") / tool)
            command = self.home / "input-status-tools/gdbus"
            command.write_text(
                "#!/usr/bin/python3\nimport sys\nfrom pathlib import Path\n"
                f"marker=Path({str(daemon_pid)!r})\n"
                "pid=marker.read_text() if marker.exists() else None\n"
                "live=pid is not None and Path('/proc/'+pid+'/stat').exists()\n"
                "active=live and sys.argv[-1] == 'org.fcitx.Fcitx5'\n"
                "print(('(true,)' if active else '(false,)') "
                "if sys.argv[-2].endswith('NameHasOwner') else '(uint32 '+pid+',)')\n"
            )
            original_environment = dict(os.environ)

            def wait_for(predicate):
                deadline = time.monotonic() + 5
                while not predicate():
                    self.assertLess(time.monotonic(), deadline, "owned launch did not settle")
                    time.sleep(0.01)

            try:
                rejected = asyncio.run(
                    self.support.execute("desktop-text", "Connect", startup_id="unapproved")
                )
                self.assertFalse(rejected["ok"])
                self.assertFalse(daemon_pid.exists())
                self.assertTrue(
                    asyncio.run(self.support.execute("desktop-text", "RecordNoOriginalMethod"))[
                        "ok"
                    ]
                )
                selection = {
                    "id": "org.typelay.Fixture.desktop",
                    "name": "Fixture",
                    "toolkit": "gtk3",
                }
                for startup_id in ("synthetic-new-click", None):
                    self.support.selected_application = selection
                    self.support.application_result = "已选择，尚未打开或连接。"
                    result = asyncio.run(
                        self.support.execute("desktop-text", "Connect", startup_id=startup_id)
                    )
                    self.assertTrue(result["ok"], result)
                    self.assertIsNone(
                        self.support.installed_application_view(self.support.facts())["id"]
                    )
                    practice_peer = SimpleNamespace(
                        pid=self.support.practice_launch["receipt"]["child"]["pid"],
                        lifetime="live",
                        backend="gtk3",
                        application="practice",
                        link=SimpleNamespace(channel=SimpleNamespace(alive=True)),
                        support_observation={
                            "atMs": now_ms(),
                            "preview": True,
                            "tabAccept": False,
                            "directReplace": True,
                        },
                    )
                    practice_fact = next(
                        row
                        for row in self.support.facts([practice_peer])
                        if row["id"] == "desktop-text"
                    )
                    self.assertEqual(practice_fact["state"], "Connected")
                    self.assertTrue(practice_fact["capabilities"]["directReplace"])
                    self.assertIn("若窗口未在前台", result["detail"])
                    wait_for(application_observation.exists)
                    observed = json.loads(application_observation.read_text())
                    self.assertEqual(
                        observed,
                        {
                            "environment": {
                                "DESKTOP_STARTUP_ID": startup_id,
                                "XDG_ACTIVATION_TOKEN": None,
                            },
                            "arguments": [],
                        },
                    )
                    self.assertEqual(
                        json.loads(daemon_observation.read_text()),
                        {
                            "DESKTOP_STARTUP_ID": None,
                            "XDG_ACTIVATION_TOKEN": None,
                        },
                    )
                    self.assertNotIn(
                        "synthetic-new-click", json.dumps(receipts(self.support.paths.data))
                    )
                    self.assertNotIn("synthetic-new-click", json.dumps(result))
                    self.assertTrue(dict(os.environ) == original_environment)
                    finish.touch()
                    wait_for(
                        lambda: not any(
                            item["kind"] == "application"
                            for item in receipts(self.support.paths.data)
                        )
                    )
                    finish.unlink()
                    application_observation.unlink()
                    if startup_id is not None:
                        component = bundle / "lib/libexample.so.1"
                        original = component.read_bytes()
                        try:
                            component.write_bytes(b"changed after preparation")
                            self.support.selected_application = selection
                            failed = asyncio.run(
                                self.support.execute(
                                    "desktop-text", "Connect", startup_id="synthetic-failed-click"
                                )
                            )
                            self.assertFalse(failed["ok"])
                            self.assertEqual(
                                self.support.installed_application_view(self.support.facts())["id"],
                                selection["id"],
                            )
                            self.assertFalse(application_observation.exists())
                            self.assertNotIn("synthetic-failed-click", json.dumps(failed))
                        finally:
                            component.write_bytes(original)
                        before_refresh = receipts(self.support.paths.data)
                        asyncio.run(
                            self.support.execute(
                                "desktop-text", "Refresh", startup_id="synthetic-refresh"
                            )
                        )
                        self.assertEqual(receipts(self.support.paths.data), before_refresh)
            finally:
                finish.touch()
                wait_for(
                    lambda: not any(
                        item["kind"] == "application" for item in receipts(self.support.paths.data)
                    )
                )
                asyncio.run(self.support.desktop_session.stop_session())
                for process in self.support.desktop_session.guardians:
                    process.wait(timeout=5)

    def test_prepared_component_has_explicit_practice_action_and_safe_reversible_remove(self):
        self.desktop_payload()
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])
        with self.no_input_method_environment():
            asyncio.run(self.support.refresh())
            self.assertTrue(
                asyncio.run(self.support.execute("desktop-text", "RecordNoOriginalMethod"))["ok"]
            )
            fact = next(row for row in self.support.facts() if row["id"] == "desktop-text")
            self.assertEqual(fact["state"], "AwaitingApproval")
            self.assertIn("Connect", fact["supportedActions"])
            self.assertIn("Remove", fact["supportedActions"])
            self.assertNotIn("OpenInstructions", fact["supportedActions"])
            self.assertFalse(fact["capabilities"])
            result = asyncio.run(self.support.execute("desktop-text", "Remove"))
            self.assertTrue(result["ok"], result)
        fact = next(row for row in self.support.facts() if row["id"] == "desktop-text")
        self.assertEqual(fact["state"], "NeedsInstall")
        self.assertIn("Install", fact["supportedActions"])
        self.assertNotIn("Connect", fact["supportedActions"])
        self.assertTrue(
            (self.package / "support/desktop/TypeLay-Fcitx-cooperative.tar.gz").is_file()
        )
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])

    def test_lost_daemon_after_connect_start_keeps_application_support_readable(self):
        self.desktop_payload()
        self.assertTrue(asyncio.run(self.support.execute("desktop-text", "Install"))["ok"])
        # Start may have observed a live daemon that has exited by application launch.
        # Keep launch/status real, with the OS command boundary reporting no input method.
        with (
            self.no_input_method_environment(),
            patch.object(self.support.desktop_session, "start_session", return_value={"ok": True}),
        ):
            self.assertTrue(
                asyncio.run(self.support.execute("desktop-text", "RecordNoOriginalMethod"))["ok"]
            )
            result = asyncio.run(self.support.execute("desktop-text", "Connect"))
            self.assertFalse(result["ok"], result)
            self.assertEqual(result["state"], "inactive")
            fact = next(row for row in self.support.facts() if row["id"] == "desktop-text")
            self.assertEqual(fact["state"], "Error")
            self.assertEqual(fact["reason"], "ActionFailed")
            self.assertFalse(fact["capabilities"])
            self.assertIn("Refresh", fact["supportedActions"])
            asyncio.run(self.support.execute("desktop-text", "Refresh"))
            asyncio.run(self.support.refresh())
            fact = next(row for row in self.support.facts() if row["id"] == "desktop-text")
            self.assertEqual(fact["state"], "AwaitingApproval")
            self.assertIn("Connect", fact["supportedActions"])

    def test_component_inspection_checks_files_without_executing_package_code(self):
        bundle = self.home / "chosen bundle"
        bundle.mkdir()
        script = bundle / "fcitx-cooperative.py"
        marker = self.home / "must-not-exist"
        script.write_text(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        (bundle / "manifest.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "files": {
                        "fcitx-cooperative.py": {
                            "sha256": hashlib.sha256(script.read_bytes()).hexdigest()
                        }
                    },
                }
            )
        )
        result = asyncio.run(self.support.execute("desktop-text", "CheckComponent", str(bundle)))
        self.assertTrue(result["ok"], result)
        self.assertFalse(marker.exists(), "static inspection executed the selected script")
        with self.no_input_method_environment():
            result = asyncio.run(self.support.execute("desktop-text", "Connect"))
        self.assertFalse(result["ok"], result)
        self.assertFalse(marker.exists(), "selection became an executable launch source")
        # Re-reading selection remains a static integrity operation.
        result = asyncio.run(self.support.execute("desktop-text", "CheckComponent", str(bundle)))
        self.assertTrue(result["ok"], result)
        fact = next(row for row in self.support.facts() if row["id"] == "desktop-text")
        self.assertEqual(fact["state"], "AwaitingApproval")
        self.assertFalse(any(fact["capabilities"].values()))
        script.write_text("changed")
        result = asyncio.run(self.support.execute("desktop-text", "CheckComponent", str(bundle)))
        self.assertFalse(result["ok"], result)
        self.assertFalse(marker.exists())


class InstallationFailureTests(unittest.TestCase):
    """Exercise the real setup entry point with failing external boundaries."""

    def run_setup(
        self,
        failure=None,
        *,
        response=None,
        write_error=None,
        returncode=0,
        discarded_archive=False,
        worker_result=None,
        cleanup_error=None,
        expected_code=1,
        requested=None,
        committed=("zh_en", "en_zh"),
        expected_directions=("zh_en", "en_zh"),
        repair=False,
        activation=None,
        damaged_marker=False,
    ):
        with tempfile.TemporaryDirectory(prefix="typelay-install-failure-") as directory:
            base = Path(directory)
            shutil.copytree(ROOT / "src/shared/TypeLay.Worker", base / "home/worker")
            current = base / "data/components/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            current.mkdir(parents=True)
            (current / "retained").write_text("old usable version")
            if committed is not None:
                (current.parent / "current").symlink_to("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
                (current / "models").mkdir()
                (current / "models/installed.json").write_text(
                    json.dumps(
                        {"schema": 1 if len(committed) == 2 else 2, "directions": list(committed)}
                    )
                )
                if damaged_marker:
                    (current / "models/installed.json").write_text("private-token")
                record = activation or (
                    {"schema": 2, "previous": None, "directions": list(committed)}
                    if len(committed) == 1
                    else None
                )
                if record is not None:
                    (current / "activation.json").write_text(json.dumps(record))

            def process(arguments, **options):
                output = b""
                if arguments[0] != "uv":
                    self.assertIn("--self-check", arguments)
                    output = worker_result
                elif arguments[1:3] == ["python", "install"]:
                    python = Path(options["env"]["UV_PYTHON_INSTALL_DIR"])
                    interpreter = python / "cpython-3.13.16-linux-x86_64-gnu/bin/python3.13"
                    interpreter.parent.mkdir(parents=True)
                    interpreter.write_bytes(b"controlled interpreter")
                    (python / "cpython-3.13-linux-x86_64-gnu").symlink_to(
                        interpreter.parent.parent.name
                    )
                elif arguments[1] == "venv":
                    entry = Path(arguments[2]) / "bin/python"
                    entry.parent.mkdir(parents=True)
                    # uv preserves the supplied interpreter path in this link,
                    # including a managed Python alias in an ancestor directory.
                    entry.symlink_to(arguments[arguments.index("--python") + 1])
                elif arguments[1:3] == ["pip", "compile"]:
                    lock = Path(arguments[arguments.index("--output-file") + 1])
                    lock.parent.mkdir(parents=True, exist_ok=True)
                    lock.write_text("controlled dependency lock")
                if arguments[1:3] == ["python", "find"]:
                    output = (
                        options["env"]["UV_PYTHON_INSTALL_DIR"]
                        + "/cpython-3.13-linux-x86_64-gnu/bin/python3.13"
                    ).encode()
                return SimpleNamespace(
                    communicate=lambda **_: (output, None),
                    returncode=returncode,
                    poll=lambda: returncode,
                )

            output = io.StringIO()

            def open_response(request, **kwargs):
                if failure is not None:
                    raise failure
                if worker_result is not None:
                    # These inert archive bytes only exercise install state transitions;
                    # the external worker port is controlled, not real model inference.
                    source, target = ("zh", "en") if "zh_en" in request.full_url else ("en", "zh")
                    archive = io.BytesIO()
                    with zipfile.ZipFile(archive, "w") as package:
                        package.writestr(
                            "metadata.json",
                            json.dumps(
                                {
                                    "from_code": source,
                                    "to_code": target,
                                    "package_version": "1.9",
                                }
                            ),
                        )
                        for name in (
                            "sentencepiece.model",
                            "model/config.json",
                            "model/model.bin",
                            "model/shared_vocabulary.json",
                        ):
                            package.writestr(name, b"inert controlled archive")
                    return Response(archive.getvalue())
                return response

            opener = SimpleNamespace(open=open_response)
            with (
                patch.object(
                    sys,
                    "argv",
                    [
                        "setup.py",
                        "--supervised",
                        str(base / "home"),
                        str(base / "data"),
                        str(base / "cache"),
                        *(
                            []
                            if requested is None
                            else [json.dumps({"directions": requested, "repair": repair})]
                        ),
                    ],
                ),
                patch.object(subprocess, "Popen", side_effect=process),
                patch.object(setup.urllib.request, "build_opener", return_value=opener),
                patch.object(signal, "signal"),
                patch.object(os, "umask"),
                patch.object(os, "fsync", side_effect=write_error),
                patch.object(shutil, "rmtree", side_effect=cleanup_error)
                if cleanup_error
                else contextlib.nullcontext(),
                contextlib.redirect_stdout(output),
                contextlib.redirect_stderr(output),
                self.assertRaises(SystemExit) as result,
            ):
                runpy.run_path(str(ROOT / "src/linux/desktop/setup.py"), run_name="__main__")
            self.assertEqual(result.exception.code, expected_code)
            if committed is not None:
                self.assertEqual(
                    os.readlink(current.parent / "current"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                )
            else:
                self.assertFalse((current.parent / "current").exists())
            if not expected_code:
                prepared = next(
                    line[len(setup.PREPARED_PREFIX) :]
                    for line in output.getvalue().splitlines()
                    if line.startswith(setup.PREPARED_PREFIX)
                )
                candidate = current.parent / prepared
                self.assertEqual(
                    Path(os.readlink(candidate / "runtime/venv/bin/python")),
                    candidate / "runtime/python/cpython-3.13.16-linux-x86_64-gnu/bin/python3.13",
                )
                self.assertTrue((candidate / "self-check.json").is_file())
                activation = {
                    "schema": 1 if len(expected_directions) == 2 else 2,
                    "previous": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    if committed is not None
                    else None,
                }
                if activation["schema"] == 2:
                    activation["directions"] = list(expected_directions)
                self.assertEqual(
                    json.loads((candidate / "activation.json").read_text()), activation
                )
                inventory = json.loads((candidate / "models/installed.json").read_text())
                self.assertEqual(inventory["schema"], activation["schema"])
                self.assertEqual(inventory["directions"], list(expected_directions))
                self.assertEqual(
                    {entry["path"].split("/")[0] for entry in inventory["files"]},
                    set(expected_directions),
                )
            self.assertEqual((current / "retained").read_text(), "old usable version")
            if expected_code and cleanup_error is None:
                self.assertEqual(
                    sorted(path.name for path in current.parent.iterdir()),
                    ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "current", "install.lock"],
                )
            if discarded_archive:
                self.assertFalse((base / "cache/components/zh_en-1.9.argosmodel").exists())
            self.assertNotIn(directory, output.getvalue())
            self.assertNotIn("private-token", output.getvalue())
            self.assertNotIn("Traceback", output.getvalue())
            return output.getvalue()

    def test_network_failure_keeps_safe_diagnostics_when_presentation_is_unavailable(self):
        output = self.run_setup(
            urllib.error.URLError(
                ConnectionResetError(errno.ECONNRESET, "https://private-token.example/private-path")
            )
        )
        self.assertIn('"category":"network"', output)
        self.assertIn('"stage":"model-download"', output)
        self.assertIn(f'"errno":{errno.ECONNRESET}', output)
        self.assertIn("无法读取安装结果说明", output)

    def test_disk_space_failure_does_not_expose_the_path(self):
        output = self.run_setup(
            response=Response(b"model", headers={"Content-Length": 5}),
            write_error=OSError(errno.ENOSPC, "private-token", "/private-token/cache"),
        )
        self.assertIn('"category":"disk-space"', output)
        self.assertIn(f'"errno":{errno.ENOSPC}', output)
        diagnostic = json.loads(
            next(line for line in output.splitlines() if line.startswith(setup.FAILURE_PREFIX))[
                len(setup.FAILURE_PREFIX) :
            ]
        )
        self.assertGreaterEqual(diagnostic["dataFreeBytes"], 0)
        self.assertGreaterEqual(diagnostic["cacheFreeBytes"], 0)

    def test_invalid_archive_is_removed_and_reports_integrity_failure(self):
        output = self.run_setup(
            response=Response(b"private-token invalid zip"), discarded_archive=True
        )
        self.assertIn('"category":"integrity"', output)
        self.assertIn('"stage":"model-extract"', output)

    def test_permission_failure_has_a_separate_diagnostic(self):
        output = self.run_setup(
            response=Response(b"model"), write_error=PermissionError(errno.EACCES, "private-token")
        )
        self.assertIn('"category":"permission"', output)

    def test_failed_runtime_keeps_only_the_exit_code(self):
        output = self.run_setup(returncode=7)
        self.assertIn('"category":"runtime"', output)
        self.assertIn('"stage":"runtime"', output)
        self.assertIn('"exitCode":7', output)

    def test_timeouts_remain_failures_rather_than_user_pauses(self):
        for failure, category in (
            (TimeoutError("private-token"), "network"),
            (subprocess.TimeoutExpired("private-token", 1200), "runtime"),
        ):
            with self.subTest(category=category):
                output = self.run_setup(failure)
                self.assertIn(f'"category":"{category}"', output)
                self.assertNotIn("已暂停", output)

    def test_unknown_exception_cannot_leak_its_type_or_message(self):
        class PrivateTokenException(Exception):
            pass

        output = self.run_setup(PrivateTokenException("private-token source text"))
        self.assertIn('"category":"unknown"', output)
        self.assertNotIn("PrivateTokenException", output)
        self.assertIn("无法读取安装结果说明", output)

    def test_failed_self_check_reports_runtime_without_claiming_model_corruption(self):
        for result in (
            b'{"status":"failed","reason":"private-token"}',
            b'{"status":"passed"}',
            b"private-token invalid JSON",
        ):
            with self.subTest(result=result):
                output = self.run_setup(worker_result=result)
                self.assertIn('"category":"runtime"', output)
                self.assertIn('"stage":"self-check"', output)
                self.assertNotIn("重新获取损坏", output)

    def test_requested_single_direction_prepares_only_that_inventory_and_self_check(self):
        self.run_setup(
            requested=["zh_en"],
            committed=None,
            expected_directions=("zh_en",),
            expected_code=0,
            worker_result=json.dumps(
                {
                    "status": "passed",
                    "device": "cpu",
                    "results": [
                        {
                            "direction": "zh_en",
                            "status": "translated",
                            "nonempty": True,
                            "differs": True,
                        }
                    ],
                }
            ).encode(),
        )

    def test_supplement_preserves_committed_intent_and_rejects_an_inexact_check(self):
        single_check = json.dumps(
            {
                "status": "passed",
                "device": "cpu",
                "results": [
                    {
                        "direction": "en_zh",
                        "status": "translated",
                        "nonempty": True,
                        "differs": True,
                    }
                ],
            }
        ).encode()
        output = self.run_setup(
            requested=["en_zh"], committed=("zh_en",), worker_result=single_check
        )
        self.assertIn('"stage":"self-check"', output)
        check = json.dumps(
            {
                "status": "passed",
                "device": "cpu",
                "results": [
                    {
                        "direction": direction,
                        "status": "translated",
                        "nonempty": True,
                        "differs": True,
                    }
                    for direction in ("zh_en", "en_zh")
                ],
            }
        ).encode()
        self.run_setup(
            requested=["en_zh"], committed=("zh_en",), worker_result=check, expected_code=0
        )

    def test_bad_or_conflicting_committed_intent_requires_explicit_full_repair(self):
        check = json.dumps(
            {
                "status": "passed",
                "device": "cpu",
                "results": [
                    {
                        "direction": direction,
                        "status": "translated",
                        "nonempty": True,
                        "differs": True,
                    }
                    for direction in ("zh_en", "en_zh")
                ],
            }
        ).encode()
        for damage in ({"damaged_marker": True}, {"activation": {"schema": 1, "previous": None}}):
            with self.subTest(damage=damage):
                output = self.run_setup(
                    requested=["zh_en"], committed=("zh_en",), worker_result=check, **damage
                )
                self.assertIn('"stage":"model-verify"', output)
                self.assertNotIn("正在安装私有", output)
                self.run_setup(
                    requested=["zh_en", "en_zh"],
                    committed=("zh_en",),
                    repair=True,
                    worker_result=check,
                    expected_code=0,
                    **damage,
                )

    def test_preparation_returns_one_verified_candidate_without_switching_current(self):
        self.run_setup(
            worker_result=json.dumps(
                {
                    "status": "passed",
                    "device": "cpu",
                    "results": [
                        {
                            "direction": direction,
                            "status": "translated",
                            "nonempty": True,
                            "differs": True,
                        }
                        for direction in ("zh_en", "en_zh")
                    ],
                }
            ).encode(),
            expected_code=0,
        )

    def test_cleanup_failure_cannot_replace_the_first_failure_including_unknown(self):
        for original, expected in (
            (ConnectionError("private-token"), "network"),
            (RuntimeError("private-token"), "unknown"),
        ):
            with self.subTest(category=expected):
                output = self.run_setup(
                    original, cleanup_error=PermissionError(errno.EACCES, "private-token")
                )
                diagnostic = None
                for line in output.splitlines():
                    diagnostic = setup.failure_code(line, diagnostic)
                self.assertEqual(diagnostic, expected)

    def test_gui_diagnostics_accept_only_fixed_codes_and_numeric_metadata(self):
        self.assertEqual(
            setup.failure_code(
                'TYPELAY_SETUP_FAILURE={"category":"disk-space","stage":"model-download","errno":28}'
            ),
            "disk-space",
        )
        for payload in (
            {"category": "private-token", "stage": "runtime"},
            {"category": "network", "stage": "private-token"},
            {"category": "network", "stage": "runtime", "errno": True},
            {"category": "network", "stage": "runtime", "errno": "private-token"},
            {"category": "network", "stage": "runtime", "body": "private-token"},
            {"category": [], "stage": "runtime"},
        ):
            with self.subTest(payload=payload):
                self.assertIsNone(setup.failure_code(setup.FAILURE_PREFIX + json.dumps(payload)))
        self.assertIsNone(setup.failure_code("plain progress"))
        self.assertIsNone(setup.failure_code(setup.FAILURE_PREFIX + "private-token"))
        self.assertNotIn("private-token", setup.failure_message(None, "private-token"))


class InstallationProcessTests(unittest.TestCase):
    supervisor = None

    def test_pause_reaps_descendants_and_keeps_active_version(self):
        with tempfile.TemporaryDirectory(prefix="typelay-setup-stop-") as directory:
            base = Path(directory)
            home = base / "home"
            shutil.copytree(ROOT / "src/shared/TypeLay.Worker", home / "worker")
            (home / "native").mkdir(parents=True)
            (home / "native/typelay-supervise").symlink_to(self.supervisor)
            (base / "bin").mkdir()
            executable = base / "bin/uv"
            executable.write_text(
                f"#!{sys.executable}\n"
                "import subprocess, sys, time\n"
                "subprocess.Popen([sys.executable, '-c', "
                + repr(
                    "import os,signal,time; from pathlib import Path; "
                    "signal.signal(signal.SIGTERM, signal.SIG_IGN); "
                    "Path(os.environ['TYPELAY_TEST_CHILD_PID']).write_text(str(os.getpid())); "
                    "time.sleep(60)"
                )
                + "])\ntime.sleep(60)\n",
                encoding="utf-8",
            )
            executable.chmod(0o700)
            current = base / "data/components/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            current.mkdir(parents=True)
            (current / "retained").write_text("old usable version")
            (current.parent / "current").symlink_to("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
            process = subprocess.Popen(
                [
                    sys.executable,
                    "-I",
                    "-B",
                    str(ROOT / "src/linux/desktop/setup.py"),
                    str(home),
                    str(base / "data"),
                    str(base / "cache"),
                ],
                env={
                    **os.environ,
                    "PATH": str(base / "bin") + ":" + os.environ["PATH"],
                    "TYPELAY_TEST_CHILD_PID": str(base / "pid"),
                },
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            child = None
            try:
                deadline = time.monotonic() + 5
                while not (base / "pid").exists():
                    self.assertLess(time.monotonic(), deadline, "installer child did not start")
                    time.sleep(0.02)
                child = int((base / "pid").read_text())
                process.terminate()
                output, error = process.communicate(timeout=5)
                self.assertEqual(process.returncode, 128 + signal.SIGTERM, (output, error))
                self.assertFalse(Path(f"/proc/{child}").exists(), "installer leaked its descendant")
                child = None
                self.assertEqual(
                    os.readlink(current.parent / "current"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                )
                self.assertEqual((current / "retained").read_text(), "old usable version")
            finally:
                # Only process IDs created by this fixture are ever signalled.
                if child is not None:
                    try:
                        os.kill(child, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)


class PresentationProcessTests(unittest.TestCase):
    def test_pause_during_failure_guidance_keeps_the_first_failure_and_previous_version(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            shutil.copytree(ROOT / "src/shared/TypeLay.Worker", home / "worker")
            (home / "app").mkdir()
            pid = home / "formatter-pid"
            executable = home / "app/TypeLay.Linux"
            executable.write_text(
                f"#!{sys.executable}\n"
                "import os, time\n"
                "from pathlib import Path\n"
                f"Path({str(pid)!r}).write_text(str(os.getpid()))\n"
                "time.sleep(30)\n"
            )
            executable.chmod(0o700)
            current = home / "data/components/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            current.mkdir(parents=True)
            (current.parent / "current").symlink_to("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            process = subprocess.Popen(
                [
                    sys.executable,
                    "-I",
                    "-B",
                    str(ROOT / "src/linux/desktop/setup.py"),
                    "--supervised",
                    str(home),
                    str(home / "data"),
                    str(home / "cache"),
                ],
                env={**os.environ, "PATH": str(home / "no-uv")},
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            try:
                deadline = time.monotonic() + 3
                while not pid.exists():
                    self.assertLess(time.monotonic(), deadline, "failure formatter did not start")
                    time.sleep(0.02)
                process.terminate()
                output, error = process.communicate(timeout=5)
                self.assertEqual(process.returncode, 1, (output, error))
                diagnostic = None
                for line in output.decode().splitlines():
                    diagnostic = setup.failure_code(line, diagnostic)
                self.assertEqual(diagnostic, "runtime")
                self.assertNotIn("已暂停", output.decode())
                self.assertFalse(Path(f"/proc/{int(pid.read_text())}").exists())
                self.assertEqual(
                    os.readlink(current.parent / "current"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                )
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
                process.stdout.close()
                process.stderr.close()

    def test_bounded_presentation_failures_reap_the_formatter_without_leaking_output(self):
        for oversized in (False, True):
            with self.subTest(oversized=oversized), tempfile.TemporaryDirectory() as directory:
                home = Path(directory)
                (home / "app").mkdir()
                pid = home / "pid"
                executable = home / "app/TypeLay.Linux"
                executable.write_text(
                    f"#!{sys.executable}\n"
                    "import os, sys, time\n"
                    "from pathlib import Path\n"
                    f"Path({str(pid)!r}).write_text(str(os.getpid()))\n"
                    + (
                        "sys.stdout.write('private-token' * 2000); sys.stdout.flush()\n"
                        if oversized
                        else ""
                    )
                    + "time.sleep(30)\n"
                )
                executable.chmod(0o700)
                started = time.monotonic()
                message = setup.failure_message(home, "network")
                self.assertLess(time.monotonic() - started, 10, "formatter exceeded its deadline")
                self.assertIn("无法读取安装结果说明", message)
                self.assertNotIn("private-token", message)
                self.assertFalse(Path(f"/proc/{int(pid.read_text())}").exists())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(add_help=False)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--http", action="store_true")
    mode.add_argument("--supervisor", type=Path)
    options, remaining = parser.parse_known_args()
    suite = [
        "CatalogTests",
        "DownloadTests",
        "InstallationFailureTests",
        "ApplicationSupportTests",
        "PresentationProcessTests",
    ]
    if options.http:
        suite = "HTTPDownloadTests"
    elif options.supervisor:
        InstallationProcessTests.supervisor = options.supervisor.resolve(strict=True)
        suite = "InstallationProcessTests"
    unittest.main(defaultTest=suite, argv=[sys.argv[0], *remaining])
