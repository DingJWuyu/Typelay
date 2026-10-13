"""Model download HTTP boundary with actual private cache files, without model downloads."""

import argparse
import asyncio
import hashlib
import io
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
from email.message import Message
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
import setup  # noqa: E402
from application_support import ApplicationSupport  # noqa: E402
from paths import Paths  # noqa: E402
from wire import now_ms  # noqa: E402


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
            setup.download("zh_en", self.cache)

    def test_interruption_resumes_only_the_same_representation(self):
        self.interrupted()
        self.responses.append(
            Response(
                b"second",
                206,
                {"Content-Length": 6, "Content-Range": "bytes 5-10/11", "ETag": '"v1"'},
            )
        )
        archive, _ = setup.download("zh_en", self.cache)
        self.assertEqual(self.requests[-1].get_header("Range"), "bytes=5-")
        self.assertEqual(self.requests[-1].get_header("If-range"), '"v1"')
        self.assertEqual(archive.read_bytes(), b"firstsecond")

    def test_ignored_range_overwrites_partial_instead_of_appending(self):
        self.interrupted()
        self.responses.append(Response(b"new model", headers={"Content-Length": 9, "ETag": '"v2"'}))
        archive, _ = setup.download("zh_en", self.cache)
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
                archive, _ = setup.download("zh_en", self.cache)
                self.assertIsNone(self.requests[-1].get_header("Range"))
                self.assertEqual(archive.read_bytes(), b"new model")
                archive.unlink()

    def test_invalid_cached_prefix_is_not_resumed(self):
        self.interrupted()
        (self.cache / "zh_en-1.9.part").write_bytes(b"other")
        self.responses.append(Response(b"whole", headers={"Content-Length": 5}))
        archive, _ = setup.download("zh_en", self.cache)
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
                archive, _ = setup.download("zh_en", self.cache)
                self.assertIsNone(self.requests[-1].get_header("Range"))
                self.assertEqual(archive.read_bytes(), b"whole")
                archive.unlink()

    def test_weak_etag_without_strong_date_cannot_resume(self):
        self.interrupted(ETag='W/"v1"')
        self.responses.append(Response(b"whole", headers={"Content-Length": 5}))
        archive, _ = setup.download("zh_en", self.cache)
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
        archive, _ = setup.download("zh_en", self.cache)
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
        archive, _ = setup.download("zh_en", self.cache)
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
        archive, _ = setup.download("zh_en", self.cache)
        self.assertEqual(archive.read_bytes(), b"whole")
        self.assertIsNone(self.requests[-1].get_header("Range"))

    def test_download_limit_does_not_publish_an_archive(self):
        self.responses.append(
            Response(b"too much", headers={"Content-Length": setup.MAX_ARCHIVE + 1})
        )
        with self.assertRaises(ValueError):
            setup.download("zh_en", self.cache)
        self.assertFalse((self.cache / "zh_en-1.9.argosmodel").exists())

    def test_pause_preserves_a_verified_prefix(self):
        class Pausing(Response):
            def read(self, size=-1):
                if self.tell():
                    raise setup.Paused()
                return super().read(size)

        self.responses.append(Pausing(b"first", headers={"Content-Length": 11, "ETag": '"v1"'}))
        with self.assertRaises(setup.Paused):
            setup.download("zh_en", self.cache)
        self.responses.append(
            Response(
                b"second",
                206,
                {"Content-Length": 6, "Content-Range": "bytes 5-10/11", "ETag": '"v1"'},
            )
        )
        archive, _ = setup.download("zh_en", self.cache)
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
                setup.download("zh_en", cache)
            responses.append(
                (
                    206,
                    {"Content-Length": 6, "Content-Range": "bytes 5-10/11", "ETag": '"v1"'},
                    b"second",
                )
            )
            archive, _ = setup.download("zh_en", cache)
            self.assertEqual(archive.read_bytes(), b"firstsecond")
            self.assertEqual(requests[-1]["Range"], "bytes=5-")
            self.assertEqual(requests[-1]["If-Range"], '"v1"')
            archive.unlink()
            responses.append((200, {"Content-Length": 11, "ETag": '"v1"'}, b"first"))
            with self.assertRaises(OSError):
                setup.download("zh_en", cache)
            responses.append((200, {"Content-Length": 9, "ETag": '"v2"'}, b"new model"))
            archive, _ = setup.download("zh_en", cache)
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
        fact = next(row for row in self.support.facts() if row["id"] == "desktop-text")
        self.assertEqual(fact["state"], "AwaitingApproval")
        self.assertFalse(any(fact["capabilities"].values()))
        script.write_text("changed")
        result = asyncio.run(self.support.execute("desktop-text", "CheckComponent", str(bundle)))
        self.assertFalse(result["ok"], result)
        self.assertFalse(marker.exists())


class InstallationProcessTests(unittest.TestCase):
    supervisor = None

    def test_pause_reaps_descendants_and_keeps_active_version(self):
        with tempfile.TemporaryDirectory(prefix="typelay-setup-stop-") as directory:
            base = Path(directory)
            home = base / "home"
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
            current = base / "data/components/old"
            current.mkdir(parents=True)
            (current / "retained").write_text("old usable version")
            (current.parent / "current").symlink_to("old")
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
                self.assertEqual(os.readlink(current.parent / "current"), "old")
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


if __name__ == "__main__":
    parser = argparse.ArgumentParser(add_help=False)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--http", action="store_true")
    mode.add_argument("--supervisor", type=Path)
    options, remaining = parser.parse_known_args()
    suite = ["DownloadTests", "ApplicationSupportTests"]
    if options.http:
        suite = "HTTPDownloadTests"
    elif options.supervisor:
        InstallationProcessTests.supervisor = options.supervisor.resolve(strict=True)
        suite = "InstallationProcessTests"
    unittest.main(defaultTest=suite, argv=[sys.argv[0], *remaining])
