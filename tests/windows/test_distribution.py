"""Test the shipped ZIP after relocation. Model inference and physical GUI input are separate gates."""

import ctypes
import json
import os
import queue
import struct
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid
import zipfile
from pathlib import Path, PurePosixPath

TOKEN = "1" * 64
MAX_FRAME = 256 * 1024


def frame(value: dict) -> bytes:
    payload = json.dumps(value, ensure_ascii=False).encode("utf-8")
    return struct.pack("<i", len(payload)) + payload


def responses(data: bytes) -> list[dict]:
    result = []
    while data:
        if len(data) < 4:
            raise AssertionError("Truncated response header")
        length = struct.unpack("<i", data[:4])[0]
        if not 0 < length <= MAX_FRAME or len(data) < length + 4:
            raise AssertionError("Invalid response frame")
        result.append(json.loads(data[4 : 4 + length]))
        data = data[4 + length :]
    return result


def request(identifier: int, operation: str, **changes) -> dict:
    return {"version": 3, "id": identifier, "op": operation, "token": TOKEN, **changes}


class DistributionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        archive = os.environ.get("TYPELAY_PACKAGE_ZIP")
        if not archive or not Path(archive).is_file():
            raise RuntimeError(
                "TYPELAY_PACKAGE_ZIP must point at the built archive. Run ./dev.ps1 pack."
            )
        cls.temporary = tempfile.TemporaryDirectory(prefix="typelay-e2e-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.home = Path(cls.temporary.name) / "relocated TypeLay 中"
        cls.home.mkdir()
        with zipfile.ZipFile(archive) as zipped:
            names = [PurePosixPath(info.filename.replace("\\", "/")) for info in zipped.infolist()]
            roots = {path.parts[0] for path in names if path.parts}
            if len(roots) != 1:
                raise AssertionError("Expected one distribution root directory")
            for info, path in zip(zipped.infolist(), names):
                relative = PurePosixPath(*path.parts[1:])
                if path.is_absolute() or ".." in path.parts:
                    raise AssertionError("Unsafe archive path")
                target = cls.home.joinpath(*relative.parts)
                if info.filename.replace("\\", "/").endswith("/"):
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(zipped.read(info))

    def test_distribution_contains_only_the_runtime_surface(self):
        expected = {
            "TypeLay.exe",
            "Start-TypeLay.cmd",
            "README.md",
            "THIRD_PARTY_NOTICES.md",
            "app",
            "worker",
            "bootstrap",
            "browser",
        }
        self.assertEqual({p.name for p in self.home.iterdir()}, expected)
        forbidden = {
            "src",
            "tests",
            "archive",
            "evidence",
            ".github",
            ".git",
            "__pycache__",
            "bin",
            "obj",
        }
        for path in self.home.rglob("*"):
            self.assertFalse(forbidden.intersection(path.relative_to(self.home).parts), str(path))

    def test_relocated_worker_reads_its_shared_model_catalog(self):
        result = subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                "-c",
                "import sys,json; sys.path.insert(0, sys.argv[1]); "
                "from typelay_worker.catalog import load_catalog; "
                "print(json.dumps([d.id for d in load_catalog()]))",
                str(self.home / "worker"),
            ],
            cwd=self.home,
            capture_output=True,
            text=True,
            timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(json.loads(result.stdout), ["zh_en", "en_zh"])

    def test_runtime_dependencies_survive_relocation(self):
        for relative in (
            "TypeLay.exe",
            "bootstrap/start.ps1",
            "app/TypeLay.App.exe",
            "app/TypeLay.Broker.exe",
            "app/TypeLay.Practice.exe",
            "app/TypeLay.BrowserHost.exe",
            "app/TypeLay.Core.dll",
            "app/TypeLay.Protocol.dll",
            "app/TypeLay.Runtime.dll",
            "app/TypeLay.Windows.Contracts.dll",
            "app/TypeLay.NativeInput.dll",
            "app/coreclr.dll",
            "worker/run_worker.py",
            "worker/typelay_worker/engine.py",
            "worker/typelay_worker/catalog.py",
            "worker/model-catalog.json",
            "browser/chromium/manifest.json",
            "browser/chromium/connection.json",
            "browser/chromium/background.js",
            "browser/chromium/content.js",
        ):
            with self.subTest(path=relative):
                self.assertTrue((self.home / relative).is_file(), relative)
        self.assertEqual(
            json.loads(
                (self.home / "browser/chromium/connection.json").read_text(encoding="utf-8")
            ),
            {"version": 1, "extensionId": None, "storeUrl": None},
        )
        config = json.loads(
            (self.home / "app/TypeLay.App.runtimeconfig.json").read_text(encoding="utf-8")
        )
        self.assertNotIn("frameworks", config["runtimeOptions"])
        self.assertNotIn("framework", config["runtimeOptions"])

    def test_browser_host_rejects_invalid_entry_without_protocol_output(self):
        host = self.home / "app/TypeLay.BrowserHost.exe"
        for arguments in (
            ["chrome-extension://invalid/", "--parent-window=0"],
            ["chrome-extension://" + "a" * 32 + "/", "--parent-window=-1"],
            ["chrome-extension://" + "a" * 32 + "/", "--parent-window=0", "unexpected"],
            ["register", "--extension-id", "a" * 31],
            ["register", "--extension-id", "q" * 32],
        ):
            with self.subTest(arguments=arguments):
                result = subprocess.run(
                    [str(host), *arguments],
                    cwd=self.temporary.name,
                    capture_output=True,
                    timeout=10,
                )
                self.assertEqual(result.returncode, 2)
                self.assertEqual(result.stdout, b"")
                self.assertNotIn(b"unexpected", result.stderr)

    @unittest.skipUnless(sys.platform == "win32", "Packaged Windows executable")
    def test_browser_host_help_accepts_the_same_executable_through_its_short_path(self):
        host = (self.home / "app/TypeLay.BrowserHost.exe").resolve()
        normal = subprocess.run(
            [str(host), "--help"], cwd=self.temporary.name, capture_output=True, timeout=10
        )
        self.assertEqual(normal.returncode, 0, normal.stderr.decode(errors="replace"))
        self.assertIn(b"register --extension-id", normal.stdout)
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.GetShortPathNameW.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_ulong]
        kernel32.GetShortPathNameW.restype = ctypes.c_ulong
        buffer = ctypes.create_unicode_buffer(32768)
        length = kernel32.GetShortPathNameW(str(host), buffer, len(buffer))
        self.assertGreater(length, 0, "Windows could not resolve the existing executable path.")
        self.assertLess(length, len(buffer))
        alias = buffer.value
        if alias.casefold() == str(host).casefold():
            self.skipTest("This Windows volume provides no distinct 8.3 alias for the host.")
        self.assertTrue(os.path.samefile(alias, host))
        result = subprocess.run(
            [alias, "--help"], cwd=self.temporary.name, capture_output=True, timeout=10
        )
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        self.assertEqual(result.stdout, normal.stdout)
        self.assertEqual(result.stderr, b"")

    @unittest.skipUnless(sys.platform == "win32", "Packaged Windows executable")
    def test_browser_host_rejects_a_different_executable_at_the_same_installation(self):
        host = self.home / "app/TypeLay.BrowserHost.exe"
        other = host.with_name("Other.TypeLay.BrowserHost.exe")
        with other.open("xb") as output:
            self.addCleanup(other.unlink)
            output.write(host.read_bytes())
        self.assertFalse(os.path.samefile(host, other))
        result = subprocess.run(
            [str(other), "--help"], cwd=self.temporary.name, capture_output=True, timeout=10
        )
        self.assertEqual(result.returncode, 2)
        self.assertEqual(result.stdout, b"")

    @unittest.skipUnless(
        os.environ.get("TYPELAY_BROWSER_REGISTRATION_E2E") == "1",
        "Native-host registration requires an explicitly isolated Windows account.",
    )
    def test_browser_registration_preserves_conflicts_and_removes_only_its_installation(self):
        import winreg

        host = self.home / "app/TypeLay.BrowserHost.exe"
        data_root = Path(os.environ["LOCALAPPDATA"]) / "TypeLay"
        manifest = data_root / "browser/org.typelay.browser.json"
        registry = r"Software\Google\Chrome\NativeMessagingHosts\org.typelay.browser"
        views = (winreg.KEY_WOW64_32KEY, winreg.KEY_WOW64_64KEY)
        self.assertFalse(
            data_root.exists(), "Registration E2E requires an unused TypeLay data root."
        )
        self.assertFalse(manifest.exists(), "Registration E2E requires an unused manifest path.")
        for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
            for view in views:
                with self.assertRaises(FileNotFoundError):
                    with winreg.OpenKey(hive, registry, 0, winreg.KEY_READ | view):
                        self.fail("Registration E2E requires an unused host name in both views.")

        def command(*arguments, expected=0):
            result = subprocess.run(
                [str(host), *arguments], cwd=self.temporary.name, capture_output=True, timeout=10
            )
            self.assertEqual(result.returncode, expected, result.stderr.decode(errors="replace"))
            return result

        def cleanup():
            command("unregister")
            if not data_root.exists():
                return
            # Only paths created after the proven-empty preflight are ours. Unexpected
            # files make cleanup fail rather than deleting another process's state.
            browser = data_root / "browser"
            lock = browser / "registration.lock"
            self.assertEqual(set(data_root.iterdir()), {browser})
            self.assertEqual(set(browser.iterdir()), {lock})
            self.assertFalse(lock.is_symlink())
            self.assertEqual(lock.read_bytes(), b"")
            lock.unlink()
            browser.rmdir()
            data_root.rmdir()

        # The explicit gate and empty-name preflight precede every registry mutation.
        # The production unregister command itself checks ownership during cleanup.
        self.addCleanup(cleanup)
        self.assertEqual(
            json.loads(command("status").stdout), {"registered": False, "origin": None}
        )
        extension = "abcdefghijklmnopabcdefghijklmnop"
        origin = "chrome-extension://" + extension + "/"
        command("register", "--extension-id", extension)
        original = manifest.read_bytes()
        metadata = json.loads(original)
        self.assertTrue(Path(metadata["path"]).is_absolute())
        self.assertTrue(os.path.samefile(metadata["path"], host))
        self.assertEqual(metadata["allowed_origins"], [origin])
        self.assertEqual(
            json.loads(command("status").stdout), {"registered": True, "origin": origin}
        )
        command("register", "--extension-id", extension)
        self.assertEqual(manifest.read_bytes(), original)
        command("register", "--extension-id", "p" * 32, expected=2)
        self.assertEqual(manifest.read_bytes(), original)
        command("unregister", "--extension-id", "p" * 32, expected=2)
        self.assertEqual(manifest.read_bytes(), original)
        self.assertEqual(
            command("chrome-extension://" + "p" * 32 + "/", "--parent-window=0", expected=2).stdout,
            b"",
        )

        # A conflicting view may alias HKCU's first view. Restore only our own injected value.
        foreign = str(self.home / "foreign-test-manifest.json")
        with winreg.CreateKeyEx(
            winreg.HKEY_CURRENT_USER, registry, 0, winreg.KEY_ALL_ACCESS | winreg.KEY_WOW64_64KEY
        ) as key:
            previous = winreg.QueryValueEx(key, "")[0] if winreg.QueryInfoKey(key)[1] else None
            winreg.SetValueEx(key, "", 0, winreg.REG_SZ, foreign)
        try:
            command("register", "--extension-id", extension, expected=2)
            command("unregister", expected=2)
            self.assertEqual(manifest.read_bytes(), original)
            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER, registry, 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY
            ) as key:
                self.assertEqual(winreg.QueryValueEx(key, ""), (foreign, winreg.REG_SZ))
        finally:
            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                registry,
                0,
                winreg.KEY_ALL_ACCESS | winreg.KEY_WOW64_64KEY,
            ) as key:
                self.assertEqual(winreg.QueryValueEx(key, ""), (foreign, winreg.REG_SZ))
                if previous is not None:
                    winreg.SetValueEx(key, "", 0, winreg.REG_SZ, previous)
                else:
                    winreg.DeleteValue(key, "")
            if previous is None:
                winreg.DeleteKeyEx(winreg.HKEY_CURRENT_USER, registry, winreg.KEY_WOW64_64KEY)

        # A manifest belonging to another installation is never replaced or removed.
        altered = dict(metadata, path=str(self.home / "foreign TypeLay/TypeLay.BrowserHost.exe"))
        foreign_bytes = json.dumps(altered).encode()
        manifest.write_bytes(foreign_bytes)
        try:
            command("register", "--extension-id", extension, expected=2)
            command("unregister", expected=2)
            self.assertEqual(manifest.read_bytes(), foreign_bytes)
        finally:
            self.assertEqual(manifest.read_bytes(), foreign_bytes)
            manifest.write_bytes(original)

        command("unregister", "--extension-id", extension)
        self.assertFalse(manifest.exists())
        self.assertEqual(
            json.loads(command("status").stdout), {"registered": False, "origin": None}
        )
        command("unregister")

    def test_relocated_practice_opens_a_real_stock_edit_without_changing_settings(self):
        settings = Path(self.temporary.name) / "练习 settings.json"
        original = json.dumps(
            {
                "Mode": 1,
                "OnboardingComplete": False,
                "ExcludedApplications": ["应用" * 120 + str(i) for i in range(256)],
            }
        ).encode()
        settings.write_bytes(original)
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
        user32.GetDlgItem.argtypes = [ctypes.c_void_p, ctypes.c_int]
        user32.GetDlgItem.restype = ctypes.c_void_p
        user32.IsWindowVisible.argtypes = [ctypes.c_void_p]
        user32.IsWindowVisible.restype = ctypes.c_bool
        user32.GetClassNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
        user32.PostMessageW.argtypes = [
            ctypes.c_void_p,
            ctypes.c_uint,
            ctypes.c_size_t,
            ctypes.c_ssize_t,
        ]
        callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
        user32.EnumWindows.argtypes = [callback_type, ctypes.c_void_p]
        connection, run = uuid.uuid4(), uuid.uuid4()
        process = subprocess.Popen(
            [
                str(self.home / "app/TypeLay.Practice.exe"),
                "--settings",
                str(settings),
                "--progress",
                connection.hex,
            ],
            cwd=self.temporary.name,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        root = None
        try:
            deadline = time.monotonic() + 6
            while root is None and time.monotonic() < deadline:
                self.assertIsNone(process.poll(), "Practice exited before opening its input window")

                @callback_type
                def owned_window(window, _):
                    nonlocal root
                    pid = ctypes.c_ulong()
                    user32.GetWindowThreadProcessId(window, ctypes.byref(pid))
                    if pid.value == process.pid:
                        name = ctypes.create_unicode_buffer(128)
                        user32.GetClassNameW(window, name, len(name))
                        if (
                            name.value == "TypeLayPracticeWindow"
                            and user32.IsWindowVisible(window)
                            and user32.GetDlgItem(window, 101)
                        ):
                            root = window
                            return False
                    return True

                user32.EnumWindows(owned_window, None)
                if root is None:
                    time.sleep(0.02)
            self.assertIsNotNone(root, "The relocated practice must create its own window")
            field = user32.GetDlgItem(root, 101)
            self.assertTrue(field, "The production practice must expose an ordinary input field")
            name = ctypes.create_unicode_buffer(128)
            user32.GetClassNameW(field, name, len(name))
            self.assertEqual(name.value, "Edit")
            self.assertEqual(settings.read_bytes(), original)
            pid = ctypes.c_ulong()
            user32.GetWindowThreadProcessId(root, ctypes.byref(pid))
            self.assertIsNone(process.poll())
            self.assertEqual(pid.value, process.pid)
            user32.GetPropW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
            user32.GetPropW.restype = ctypes.c_void_p

            def exchange(kind, sequence):
                command = {
                    "Kind": kind,
                    "Connection": str(connection),
                    "Run": str(run),
                    "Sequence": sequence,
                    "Progress": None,
                    "Acceptance": None,
                    "AutomaticProgress": None,
                    "InputCompletion": None,
                    "Editing": None,
                    "Returning": None,
                    "MouseReturn": None,
                    "Completion": None,
                    "Exit": None,
                    "ExitResult": None,
                    "PrefixResult": None,
                }
                process.stdin.write(frame(command))
                process.stdin.flush()
                result = queue.Queue()

                def read_ack():
                    try:
                        prefix = process.stdout.read(4)
                        if len(prefix) != 4:
                            raise AssertionError("Missing practice ACK")
                        length = struct.unpack("<i", prefix)[0]
                        if not 0 < length <= 4096:
                            raise AssertionError("Invalid practice ACK length")
                        result.put(responses(prefix + process.stdout.read(length))[0])
                    except Exception as error:
                        result.put(error)

                # A timeout still reaches process cleanup; no blocked executor
                # shutdown can hide a missing outer-loop acknowledgement.
                threading.Thread(target=read_ack, daemon=True).start()
                reply = result.get(timeout=5)
                if isinstance(reply, Exception):
                    raise reply
                self.assertEqual(
                    reply, command | {"Kind": "started" if kind == "begin" else "retired"}
                )

            exchange("begin", 1)
            first_cookie = user32.GetPropW(field, "TypeLay.NativeInput.Lifetime.v2")
            self.assertTrue(first_cookie)
            exchange("retire", 2)
            self.assertIsNone(user32.GetPropW(field, "TypeLay.NativeInput.Lifetime.v2"))
            run = uuid.uuid4()
            exchange("begin", 3)
            self.assertNotEqual(
                first_cookie, user32.GetPropW(field, "TypeLay.NativeInput.Lifetime.v2")
            )
            self.assertTrue(user32.GetPropW(field, "TypeLay.NativeInput.Lifetime.v2"))
            self.assertTrue(user32.PostMessageW(root, 0x0010, 0, 0))
            # Keep stdin open until close has retired the field and acknowledged.
            process.wait(timeout=5)
            stdout, stderr = process.communicate(timeout=5)
            self.assertEqual(process.returncode, 0)
            self.assertEqual(stderr, b"")
            self.assertEqual(
                responses(stdout),
                [
                    {
                        "Kind": "closed",
                        "Connection": str(connection),
                        "Run": str(run),
                        "Sequence": 3,
                        "Progress": None,
                        "Acceptance": None,
                        "AutomaticProgress": None,
                        "InputCompletion": None,
                        "Editing": None,
                        "Returning": None,
                        "MouseReturn": None,
                        "Completion": None,
                        "Exit": None,
                        "ExitResult": None,
                        "PrefixResult": None,
                    }
                ],
            )
            self.assertEqual(settings.read_bytes(), original)
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            process.stdin.close()
            process.stdout.close()
            process.stderr.close()
            settings.unlink(missing_ok=True)

    def test_redistributed_frameworks_keep_their_exact_version_notices(self):
        config = json.loads(
            (self.home / "app/TypeLay.App.runtimeconfig.json").read_text(encoding="utf-8")
        )
        for framework in config["runtimeOptions"]["includedFrameworks"]:
            package = (framework["name"] + ".Runtime.win-x64").lower()
            folder = self.home / "app/licenses" / package / framework["version"]
            with self.subTest(framework=framework["name"]):
                self.assertTrue(folder.is_dir(), str(folder))
                names = {path.name.upper(): path for path in folder.rglob("*") if path.is_file()}
                licenses = [path for name, path in names.items() if name.startswith("LICENSE")]
                notices = [path for name, path in names.items() if "NOTICE" in name]
                self.assertTrue(licenses, "The upstream license must accompany the runtime")
                # WindowsDesktop's NuGet pack ships LICENSE but no separate notice file.
                # Core's pack does ship third-party notices, which must not be dropped.
                if framework["name"] == "Microsoft.NETCore.App":
                    self.assertTrue(
                        notices, "The Core runtime's third-party notices must be retained"
                    )
                for path in licenses + notices:
                    self.assertGreater(path.stat().st_size, 0, str(path))

    def test_packaged_worker_starts_in_isolated_mode_from_an_unrelated_directory(self):
        result = subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                str(self.home / "worker/run_worker.py"),
                "--models",
                str(self.home / "no-models"),
                "--self-check",
            ],
            cwd=self.temporary.name,
            capture_output=True,
            timeout=15,
        )
        self.assertEqual(result.returncode, 2)
        self.assertEqual(
            json.loads(result.stdout), {"status": "failed", "code": "dependency-or-model-load"}
        )
        self.assertEqual(result.stderr, b"")

    @unittest.skipUnless(sys.platform == "win32", "Packaged Windows executable")
    def test_application_entry_point_rejects_unknown_arguments_without_opening_ui(self):
        result = subprocess.run(
            [str(self.home / "app/TypeLay.App.exe"), "--not-a-valid-option"],
            cwd=self.temporary.name,
            capture_output=True,
            timeout=20,
        )
        self.assertEqual(result.returncode, 2)
        self.assertEqual(result.stdout, b"")
        self.assertEqual(result.stderr, b"")

    def broker(self, data: bytes, token: str | None = TOKEN, trace: Path | None = None):
        environment = os.environ.copy()
        environment.pop("TYPELAY_TRACE", None)
        if trace is not None:
            environment["TYPELAY_TRACE"] = str(trace)
        if token is None:
            environment.pop("TYPELAY_PIPE_TOKEN", None)
        else:
            environment["TYPELAY_PIPE_TOKEN"] = token
        return subprocess.run(
            [str(self.home / "app/TypeLay.Broker.exe")],
            input=data,
            env=environment,
            cwd=self.temporary.name,
            capture_output=True,
            timeout=20,
        )

    @unittest.skipUnless(sys.platform == "win32", "Production Windows broker")
    def test_broker_handshake_and_shutdown_use_real_process_pipes(self):
        trace = Path(self.temporary.name) / "return-trace.jsonl"
        result = self.broker(
            frame(request(1, "hello")) + frame(request(2, "return")) + frame(request(3, "quit")),
            trace=trace,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            [(r["id"], r["status"]) for r in responses(result.stdout)],
            [(1, "ready"), (2, "denied"), (3, "bye")],
        )
        self.assertEqual(result.stderr, b"")
        events = [
            json.loads(line) for line in Path(str(trace) + ".broker.jsonl").read_text().splitlines()
        ]
        self.assertEqual(len(events), 1)
        self.assertGreater(events[0].pop("atMs"), 0)
        self.assertEqual(
            events[0],
            {
                "operation": "return",
                "status": "denied",
                "reason": "target-missing",
                "errorType": None,
            },
        )

    @unittest.skipUnless(sys.platform == "win32", "Production Windows broker")
    def test_direct_write_is_rejected_without_falling_back_to_foreground_mutation(self):
        # A request without a bound plan must not fall back to the foreground.
        # Qualified stock Edit targets have a separate coordinated native writer.
        result = self.broker(frame(request(1, "direct-write")) + frame(request(2, "quit")))
        self.assertEqual(result.returncode, 0, result.stderr)
        replies = responses(result.stdout)
        self.assertEqual([reply["status"] for reply in replies], ["receipt", "bye"])
        self.assertEqual(replies[0]["receipt"]["status"], 1)  # RejectedBeforeMutation
        self.assertEqual(replies[0]["receipt"]["reason"], 6)  # Unsupported
        self.assertIsNone(replies[0]["receipt"]["verifiedSnapshot"])
        self.assertEqual(result.stderr, b"")

    @unittest.skipUnless(sys.platform == "win32", "Production Windows broker")
    def test_broker_unknown_operation_does_not_corrupt_the_next_frame(self):
        result = self.broker(frame(request(1, "PRIVATE_SENTINEL")) + frame(request(2, "quit")))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([r["status"] for r in responses(result.stdout)], ["unsupported", "bye"])
        self.assertNotIn(b"PRIVATE_SENTINEL", result.stdout + result.stderr)

    @unittest.skipUnless(sys.platform == "win32", "Production Windows broker")
    def test_broker_rejects_missing_launch_authentication(self):
        result = self.broker(b"PRIVATE_SENTINEL", token=None)
        self.assertEqual(result.returncode, 2)
        self.assertEqual(result.stdout + result.stderr, b"")

    @unittest.skipUnless(sys.platform == "win32", "Production Windows broker")
    def test_broker_rejects_wrong_authentication_and_version(self):
        for change in ({"token": "2" * 64}, {"version": 1}, {"version": 2}, {"version": 4}):
            with self.subTest(change=change):
                result = self.broker(frame(request(1, "hello", **change)))
                self.assertEqual(result.returncode, 3)
                self.assertEqual(result.stdout + result.stderr, b"")

    @unittest.skipUnless(sys.platform == "win32", "Production Windows broker")
    def test_broker_rejects_replayed_request_ids(self):
        result = self.broker(frame(request(1, "hello")) + frame(request(1, "hello")))
        self.assertEqual(result.returncode, 3)
        self.assertEqual([r["status"] for r in responses(result.stdout)], ["ready"])
        self.assertEqual(result.stderr, b"")

    @unittest.skipUnless(sys.platform == "win32", "Production Windows broker")
    def test_broker_rejects_null_authentication_without_a_runtime_crash(self):
        result = self.broker(frame(request(1, "hello", token=None)))
        self.assertEqual(result.returncode, 3)
        self.assertEqual(result.stdout + result.stderr, b"")

    @unittest.skipUnless(sys.platform == "win32", "Production Windows broker")
    def test_broker_rejects_malformed_frames_without_a_trace(self):
        for data in (
            struct.pack("<i", -1),
            struct.pack("<i", 0),
            struct.pack("<i", MAX_FRAME + 1),
            b"abc",
            struct.pack("<i", 8) + b"{}",
            struct.pack("<i", 1) + b"{",
        ):
            with self.subTest(data=data):
                result = self.broker(data)
                self.assertEqual(result.returncode, 4)
                self.assertEqual(result.stdout + result.stderr, b"")
