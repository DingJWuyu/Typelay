"""Model download HTTP boundary with actual private cache files, without model downloads."""

import argparse
import io
import json
import os
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
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
import setup  # noqa: E402


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
    suite = "DownloadTests"
    if options.http:
        suite = "HTTPDownloadTests"
    elif options.supervisor:
        InstallationProcessTests.supervisor = options.supervisor.resolve(strict=True)
        suite = "InstallationProcessTests"
    unittest.main(defaultTest=suite, argv=[sys.argv[0], *remaining])
