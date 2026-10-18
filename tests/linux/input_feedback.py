"""Explicit native command feedback at the production desktop input boundary.

The compositor/listening socket are controlled system ports. These tests do not
claim native key interception, an IME, or a real desktop interaction.
"""

import asyncio
import contextlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from application_support import ApplicationSupport  # noqa: E402
from input import Input, Peer  # noqa: E402
from overlay import Overlay  # noqa: E402
from practice_session import PracticeSession  # noqa: E402
from wire import Decoder  # noqa: E402


class EventLoop:
    IO_IN, IO_OUT, IO_HUP, IO_ERR, IO_NVAL = 1, 2, 4, 8, 16
    PRIORITY_DEFAULT = 0

    def __init__(self):
        self.next_id = 0
        self.watches = {}

    def register(self, *_, **_options):
        self.next_id += 1
        return self.next_id

    timeout_add = idle_add = register

    def io_add_watch(self, descriptor, condition, callback):
        identity = self.register()
        self.watches[identity] = (descriptor, condition, callback)
        return identity

    def flush(self, descriptor):
        for identity, (fd, condition, callback) in list(self.watches.items()):
            if fd == descriptor and condition & self.IO_OUT and not callback(fd, self.IO_OUT):
                self.source_remove(identity)

    def source_remove(self, identity):
        self.watches.pop(identity, None)


class Listener:
    def bind(self, path):
        Path(path).touch()

    def fileno(self):
        return -1

    def listen(self, _):
        pass

    def setblocking(self, _):
        pass

    def close(self):
        pass


class Host:
    def __init__(self):
        self.events = []
        self.requests = []
        self.pause_allowed = True
        self.capture_allowed = False
        self.begin_edit = None

    def send(self, operation, data=None):
        self.events.append((operation, data))

    async def ask(self, operation, data=None):
        self.requests.append((operation, data))
        if operation == "beginEdit" and self.begin_edit:
            return await self.begin_edit(data)
        return (operation == "allowPauseField" and self.pause_allowed) or (
            operation == "allowField" and self.capture_allowed
        )

    @property
    def notices(self):
        return [value for operation, value in self.events if operation == "notice"]


class InputFeedbackTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="typelay-input-feedback-")
        self.addCleanup(directory.cleanup)
        self.home = Path(directory.name)
        self.host = Host()
        self.loop = EventLoop()
        self.connections = {}
        self.stamp = 100
        clock = patch("input.now_ms", return_value=1000)
        self.clock = clock.start()
        self.addCleanup(clock.stop)
        # Niri still owns its real lifecycle, but an absent socket cannot inspect
        # a user's compositor. No source text, model, or desktop is involved.
        environment = patch.dict(os.environ, {"NIRI_SOCKET": ""})
        environment.start()
        self.addCleanup(environment.stop)
        with patch("socket.socket", return_value=Listener()):
            self.input = Input(
                self.loop,
                SimpleNamespace(keyval_from_name=lambda name: {"F9": 0xFFC6, "F10": 0xFFC7}[name]),
                SimpleNamespace(native=Path(directory.name) / "native.sock"),
                self.host,
                None,
            )
        self.addCleanup(self.input.close)
        self.generation = str(uuid.uuid4())
        self.configure()
        self.peer = self.connect_peer()

    def configure(self, accept="Tab", exit_key="Esc"):
        self.input.notification(
            "shortcuts",
            {
                "enabled": True,
                "wake": "F9",
                "direct": "F10",
                "generation": self.generation,
                "sessionEnabled": True,
                "accept": accept,
                "exit": exit_key,
            },
        )

    def connect_peer(self, backend="qt6", hello=None):
        process = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])

        def stop():
            process.terminate()
            process.wait(timeout=5)

        self.addCleanup(stop)
        connection = tempfile.TemporaryFile()
        self.addCleanup(connection.close)
        peer = Peer(self.input, connection, process.pid)
        self.connections[peer] = connection
        self.input.peers.add(peer)
        peer.event(
            "hello",
            {
                "protocol": 2,
                "pid": process.pid,
                "backend": backend,
                "lifetime": str(uuid.uuid4()),
                **(hello or {}),
            },
        )
        return peer

    def connect_registered_browser(self):
        environment = patch.dict(
            os.environ, {"HOME": str(self.home), "XDG_CONFIG_HOME": str(self.home / "config")}
        )
        environment.start()
        self.addCleanup(environment.stop)
        root = self.home / ".local/lib/typelay"
        release = root / "releases/current-test"
        release.mkdir(parents=True)
        (root / "current").symlink_to(release, target_is_directory=True)
        shutil.copy2(ROOT / "packaging/linux/install.py", release / "install.py")
        host = release / "typelay-browser-host"
        host.write_text("#!/bin/sh\nexit 1\n")
        host.chmod(0o700)
        self.support = ApplicationSupport(SimpleNamespace(home=release))
        # Only the OS executable-name port is controlled. PID ancestry and start
        # times, current-user registration files and the production installer are real.
        identity = patch(
            "application_support.process_identity",
            side_effect=lambda pid: "chromium" if pid == os.getpid() else "python",
        )
        identity.start()
        self.addCleanup(identity.stop)
        self.extension_id = "a" * 32
        with contextlib.redirect_stdout(io.StringIO()):
            self.support.installer.register_browser(root, "chromium", self.extension_id)
        self.input.browser_registration = self.support.browser_registration
        self.peer = self.connect_peer(
            "browser", {"origin": f"chrome-extension://{self.extension_id}/", "home": str(release)}
        )
        self.route = {
            "extensionId": self.extension_id,
            "documentId": "browser-owned-document",
            "documentToken": str(uuid.uuid4()),
            "tabId": 1,
            "windowId": 2,
            "frameId": 0,
            "scheme": "https:",
        }
        self.input.niri._deliver(
            ("test", [{"id": 99, "pid": os.getpid(), "is_focused": True}], {}, {}, False, 1000)
        )
        self.peer.link.receive({"event": "document-focus", "data": True, "route": self.route})
        return self.native_frames(self.peer)

    async def capture_browser(
        self, *, bindings=None, description_route=None, capture_route=None, before_reply=None
    ):
        self.host.capture_allowed = True
        self.input.notification("settings", {"enabled": True})
        self.input.notification("revoke", None)
        self.native_frames(self.peer)
        operation = asyncio.create_task(self.input.request("capture", {}))
        description = self.description()
        target = self.route["documentToken"] + ":1"
        description["target"] = description["authority"]["target"] = target
        description["metadata"]["inDocument"] = True
        description["geometry"]["space"] = "browser-viewport"
        description["sessionBindings"] = {
            "target": target,
            "generation": self.generation,
            "accept": "Tab",
            "exit": "Esc",
            "sourceReady": True,
            "editorReady": True,
            "shortcutDispatch": "page",
            "sourceLocalAcceptanceVersion": 1,
            **(bindings or {}),
        }
        native = {
            **description,
            "text": "Original source",
            "selection": {"start": 0, "end": 0},
            "ime": "Clear",
            "observedAtMs": 1000,
            "writeSupported": True,
        }
        self.browser_native = native
        for expected, value, route in (
            ("describe", description, description_route),
            ("capture", native, capture_route),
        ):

            async def reply():
                while True:
                    requests = self.native_frames(self.peer)
                    if requests:
                        self.assertEqual([frame["op"] for frame in requests], [expected])
                        if before_reply:
                            before_reply(expected)
                        self.peer.link.receive(
                            {
                                "id": requests[0]["id"],
                                "data": value,
                                "route": self.route if route is None else route,
                            }
                        )
                        return
                    await asyncio.sleep(0)

            await asyncio.wait_for(reply(), 1)
        return await asyncio.wait_for(operation, 1)

    def publish_browser(self, capture):
        identity = str(uuid.uuid4())
        self.input.notification(
            "publish",
            {
                "lease": {
                    "source": capture["snapshot"],
                    "userEpoch": capture["userEpoch"],
                    "foregroundEpoch": capture["foregroundEpoch"],
                    "escapeAllowed": True,
                    "commandGeneration": self.generation,
                    "sessionRevision": 9,
                },
                "permit": {
                    "plan": {
                        "id": identity,
                        "expected": capture["snapshot"],
                        "replacement": "Translation",
                    },
                    "expiresAtMs": 1100,
                },
            },
        )
        self.assertEqual([frame["op"] for frame in self.native_frames(self.peer)], ["publish"])
        return identity

    def test_browser_default_capability_requires_current_registered_connection_and_exact_ack(self):
        frames = self.connect_registered_browser()
        configured = next(frame["data"] for frame in frames if frame["op"] == "configure-shortcuts")
        self.assertEqual(configured["shortcutDispatch"], "page")
        self.assertEqual(configured["sourceLocalAcceptanceVersion"], 1)
        capture = asyncio.run(self.capture_browser())
        self.assertEqual(capture["sessionShortcuts"]["sourceLocalAcceptanceVersion"], 1)
        for change in (
            {"target": "other"},
            {"generation": str(uuid.uuid4())},
            {"accept": "F7"},
            {"sourceReady": False},
            {"editorReady": False},
            {"sourceLocalAcceptanceVersion": True},
            {"sourceLocalAcceptanceVersion": 0},
            {"sourceLocalAcceptanceVersion": 2},
        ):
            with self.subTest(change=change):
                capture = asyncio.run(self.capture_browser(bindings=change))
                self.assertEqual(
                    (capture["sessionShortcuts"] or {}).get("sourceLocalAcceptanceVersion", 0), 0
                )
        self.support.installer.unregister_browser(self.support.root, "chromium")
        self.input.expire()
        capture = asyncio.run(self.capture_browser())
        self.assertEqual(capture["sessionShortcuts"]["sourceLocalAcceptanceVersion"], 0)

    def test_browser_route_mismatch_and_ancestor_focus_never_receive_default_acceptance(self):
        self.connect_registered_browser()
        for route in (
            {},
            {**self.route, "extensionId": "b" * 32},
            {**self.route, "documentId": None},
            {**self.route, "documentId": "different-document"},
            {**self.route, "frameId": 1},
        ):
            with self.subTest(route=route):
                capture = asyncio.run(self.capture_browser(capture_route=route))
                self.assertEqual(
                    (capture["sessionShortcuts"] or {}).get("sourceLocalAcceptanceVersion", 0), 0
                )
        # The existing general ancestry match is insufficient: the foreground
        # must be the specific browser ancestor established by this registration.
        self.focus(self.peer)
        self.assertIsNone(asyncio.run(self.input.request("capture", {})))

    def test_browser_retired_registration_and_document_keep_only_original_completed_receipt(self):
        self.connect_registered_browser()
        identity = self.publish_browser(asyncio.run(self.capture_browser()))
        self.support.installer.unregister_browser(self.support.root, "chromium")
        self.input.expire()
        self.peer.link.receive({"event": "document-closed", "data": None, "route": self.route})
        self.native_frames(self.peer)
        receipt = {
            "planId": identity,
            "acceptedAtMs": 1000,
            "receipt": {"status": "OutcomeUnknown", "reason": "UnknownOutcome"},
        }
        self.peer.link.receive(
            {"event": "accepted", "data": receipt, "route": {**self.route, "documentId": "other"}}
        )
        self.assertEqual(self.host.notices, [])
        self.native_frames(self.peer)
        self.peer.link.receive({"event": "accepted", "data": receipt, "route": self.route})
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["completedReceipt"], receipt["receipt"])
        self.assertEqual(self.native_frames(self.peer), [])
        self.peer.link.receive({"event": "accepted", "data": receipt, "route": self.route})
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual([frame["op"] for frame in self.native_frames(self.peer)], ["quarantine"])

    def test_browser_receiptless_acceptance_reports_uncertainty_without_another_write(self):
        self.connect_registered_browser()
        identity = self.publish_browser(asyncio.run(self.capture_browser()))
        self.peer.link.receive(
            {
                "event": "accepted",
                "data": {"planId": identity, "acceptedAtMs": 1000},
                "route": self.route,
            }
        )
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["completedReceipt"]["status"], "OutcomeUnknown")
        self.assertEqual([frame["op"] for frame in self.native_frames(self.peer)], ["quarantine"])

    def test_browser_registration_rejects_foreign_origin_and_stale_installation(self):
        self.connect_registered_browser()
        manifest = self.support.installer.browser_manifest("chromium")
        original = manifest.read_text()
        value = json.loads(original)
        for origins in (["chrome-extension://" + "b" * 32 + "/"], value["allowed_origins"] * 2):
            with self.subTest(origins=origins):
                manifest.write_text(json.dumps({**value, "allowed_origins": origins}))
                self.input.expire()
                self.assertIsNone(self.peer.browser_registration)
        manifest.write_text(original)
        self.input.expire()
        self.assertIsNotNone(self.peer.browser_registration)
        old = self.home / "old-release"
        old.mkdir()
        self.assertIsNone(
            self.support.browser_registration(
                self.peer.browser_origin, str(old), self.peer.ancestors
            )
        )
        current = self.support.paths.home
        self.support.paths.home = old
        self.input.expire()
        self.assertIsNone(self.peer.browser_registration)
        self.support.paths.home = current
        # Reusing either the host PID or its selected browser ancestor cannot
        # inherit the original process lifetime.
        for changed_pid in (self.peer.pid, self.peer.browser_identity["pid"]):
            lineage = {**self.peer.ancestors, self.peer.pid: self.peer.started}
            lineage[changed_pid] += 1
            with patch("input.process_lineage", return_value=lineage):
                self.input.expire()
                self.assertIsNone(self.peer.browser_registration)

    def test_explicit_browser_remove_blocks_future_grants_but_keeps_issued_receipt(self):
        self.connect_registered_browser()
        capture = asyncio.run(self.capture_browser())
        identity = self.publish_browser(capture)
        self.input.suspend_browser("chromium")
        self.support.installer.unregister_browser(self.support.root, "chromium")
        self.input.expire()
        self.native_frames(self.peer)
        self.assertIsNone(asyncio.run(self.input.request("capture", {})))
        for operation in ("publish", "publishShortcut"):
            self.input.notification(
                operation, {"lease": {"source": capture["snapshot"]}, "generation": self.generation}
            )
        self.peer.link.receive(
            {
                "event": "shortcut",
                "data": {
                    "kind": "direct",
                    "generation": self.generation,
                    "acceptedAtMs": 1000,
                    "native": self.browser_native,
                },
                "route": self.route,
            }
        )
        self.assertEqual(self.host.notices, [])
        self.assertEqual(self.native_frames(self.peer), [])
        with contextlib.redirect_stdout(io.StringIO()):
            self.support.installer.register_browser(
                self.support.root, "chromium", self.extension_id
            )
        self.input.suspend_browser("chromium", False)
        self.input.expire()
        self.assertIsNone(asyncio.run(self.input.request("capture", {})))
        self.assertEqual(self.native_frames(self.peer), [])
        self.peer.link.receive(
            {
                "event": "accepted",
                "data": {
                    "planId": identity,
                    "acceptedAtMs": 1000,
                    "receipt": {"status": "OutcomeUnknown", "reason": "UnknownOutcome"},
                },
                "route": self.route,
            }
        )
        self.assertEqual(len(self.host.notices), 1)

    def test_legacy_browser_without_complete_v1_ack_keeps_existing_remove_behavior(self):
        self.connect_registered_browser()
        capture = asyncio.run(self.capture_browser(bindings={"sourceLocalAcceptanceVersion": 0}))
        self.assertEqual(capture["sessionShortcuts"]["sourceLocalAcceptanceVersion"], 0)
        self.input.suspend_browser("chromium")
        capture = asyncio.run(self.capture_browser())
        self.assertEqual(capture["sessionShortcuts"]["sourceLocalAcceptanceVersion"], 0)

    def test_browser_remove_during_capture_cannot_bind_a_late_snapshot(self):
        self.connect_registered_browser()
        asyncio.run(self.capture_browser())
        capture = asyncio.run(
            self.capture_browser(
                before_reply=lambda operation: (
                    self.input.suspend_browser("chromium") if operation == "capture" else None
                )
            )
        )
        self.assertIsNone(capture)
        self.assertIsNone(self.input.bound_observation)

    def browser_direct_removal(self, *, sent):
        self.connect_registered_browser()
        capture = asyncio.run(self.capture_browser())
        self.input.notification(
            "publishShortcut",
            {"lease": {"source": capture["snapshot"]}, "generation": self.generation},
        )
        self.native_frames(self.peer)
        self.peer.link.receive(
            {
                "event": "shortcut",
                "data": {
                    "kind": "direct",
                    "generation": self.generation,
                    "acceptedAtMs": 1000,
                    "native": self.browser_native,
                },
                "route": self.route,
            }
        )
        snapshot = self.host.notices[-1]["source"]

        async def exercise():
            task = asyncio.create_task(self.input.request("direct", {"expected": snapshot}))
            await asyncio.sleep(0)
            describe = self.native_frames(self.peer)[0]
            self.assertEqual(describe["op"], "describe")
            if not sent:
                self.input.suspend_browser("chromium")
                self.native_frames(self.peer)
            self.peer.link.receive(
                {"id": describe["id"], "data": self.browser_native, "route": self.route}
            )
            if sent:
                await asyncio.sleep(0)
                await asyncio.sleep(0)
                direct = self.native_frames(self.peer)[0]
                self.assertEqual(direct["op"], "direct")
                self.input.suspend_browser("chromium")
                self.native_frames(self.peer)
                self.peer.link.receive(
                    {"id": direct["id"], "data": {"status": "Verified"}, "route": self.route}
                )
            result = await asyncio.wait_for(task, 1)
            self.assertEqual(result["status"], "Verified" if sent else "RejectedBeforeMutation")
            self.assertEqual(self.native_frames(self.peer), [])

        asyncio.run(exercise())

    def test_browser_remove_during_direct_describe_prevents_dispatch(self):
        self.browser_direct_removal(sent=False)

    def test_browser_remove_after_direct_dispatch_keeps_its_terminal_result(self):
        self.browser_direct_removal(sent=True)

    def native_frames(self, peer):
        connection = self.connections[peer]
        self.loop.flush(connection.fileno())
        connection.seek(0)
        frames = Decoder().feed(connection.read())
        connection.seek(0)
        connection.truncate()
        return frames

    def native_requests(self, peer):
        return [
            frame for frame in self.native_frames(peer) if frame.get("op") == "trigger-shortcut"
        ]

    def focus(self, peer):
        # Deliver a controlled compositor observation at its normal callback
        # boundary; the real process matching still rejects unrelated peers.
        self.input.niri._deliver(
            ("test", [{"id": peer.pid, "pid": peer.pid, "is_focused": True}], {}, {}, False, 1000)
        )

    def command(
        self, peer=None, command_id=1, kind="wake", generation=None, stamp=None, focus=True
    ):
        peer = peer or self.peer
        if focus:
            self.focus(peer)
        self.stamp += 1
        value = {
            "kind": kind,
            "generation": generation or self.generation,
            "commandId": command_id,
            "triggeredAtMs": self.stamp if stamp is None else stamp,
        }
        peer.event("shortcut-started", value)
        return value

    def description(self):
        return {
            "target": "ordinary-field",
            "metadata": {
                "process": "",
                "label": "Message",
                "automationId": "message",
                "help": "",
                "aria": "",
                "password": False,
                "enabled": True,
                "readOnly": False,
                "offscreen": False,
                "inDocument": False,
                "multiline": False,
                "hasValue": True,
                "hasTextEdit": True,
            },
            "authority": {
                "target": "ordinary-field",
                "metadataKey": "metadata",
                "inputRevision": 1,
                "contentRevision": 1,
                "focusRevision": 1,
                "observedAtMs": 1000,
            },
            "focused": True,
            "geometry": {
                "space": "qt-toplevel",
                "scale": 1,
                "frameWidth": 100,
                "frameHeight": 100,
                "bounds": {"x": 2, "y": 3, "width": 20, "height": 10},
            },
        }

    def test_declined_capture_keeps_null_and_only_fixed_failure_metadata(self):
        async def exercise():
            peer = self.connect_peer("gtk3")
            self.focus(peer)
            self.host.capture_allowed = True
            self.native_frames(peer)
            task = asyncio.create_task(self.input.request("capture", {}))
            await asyncio.sleep(0)
            describe = self.native_frames(peer)[0]
            peer.link.receive({"id": describe["id"], "data": self.description()})
            await asyncio.sleep(0)
            await asyncio.sleep(0)
            capture = self.native_frames(peer)[0]
            self.assertEqual(capture["op"], "capture")
            peer.link.receive(
                {
                    "id": capture["id"],
                    "data": None,
                    "captureTrace": {
                        "reason": "ime-unavailable",
                        "text": "private",
                        "target": "private",
                    },
                }
            )
            self.assertIsNone(await task)
            failure = self.input.diagnostics()["captureFailures"][-1]
            self.assertEqual(failure["reason"], "native-read-declined")
            self.assertEqual(failure["nativeFailure"], {"reason": "ime-unavailable"})
            self.assertNotIn("private", json.dumps(failure))

        with (
            patch.dict(os.environ, {"TYPELAY_TRACE": "1"}),
            contextlib.redirect_stderr(io.StringIO()),
        ):
            asyncio.run(exercise())

    def owned_context_advance(
        self,
        invalidation=None,
        *,
        resume=False,
        retire_during_bind=False,
        age_gap=False,
        foreground_gap=False,
        late_continuity=False,
    ):
        self.clock.return_value = 1000
        self.peer = self.connect_peer("gtk3")
        original = self.description

        def description():
            value = original()
            value["geometry"]["space"] = "gtk-toplevel"
            value["authority"]["observedAtMs"] = self.clock.return_value
            value["authority"]["contextStamp"] = {
                "context": 7,
                "epoch": 1,
                "input": 1,
                "server": 1,
                "applied": 1,
                "flags": 3,
            }
            return value

        self.description = description

        async def exercise():
            publication = (await self.publish_acceptance(pause=False))[0]["data"]
            binding = {
                "runId": str(uuid.uuid4()),
                "target": publication["lease"]["source"]["target"],
                "connectionId": self.peer.connection_id,
                "commandGeneration": self.generation,
                "nativeFocusRevision": 1,
                "contextGeneration": 7,
            }
            self.assertTrue(self.authorize_practice(binding, lambda _: True))
            publication["lease"].update(
                practiceRunId=binding["runId"], practiceConnectionId=binding["connectionId"]
            )
            self.native_frames(self.peer)
            if foreground_gap and not late_continuity:
                self.clock.return_value = 1151
                self.input.state()
                self.assertIsNone(self.input.practice_authorization)
                self.assertEqual(self.input.last_state["practiceForegroundSuspension"], binding)
                self.assertTrue(
                    self.input.practice_waiting(
                        binding["runId"], acknowledged=False, require_fresh=False
                    )
                )
                operation = asyncio.create_task(self.input.request("suspendOwnedSource", binding))
            else:
                operation = asyncio.create_task(
                    self.input.request("capture", {"practiceRead": binding})
                )

            async def request(name):
                while True:
                    frames = self.native_frames(self.peer)
                    matches = [
                        frame
                        for frame in frames
                        if frame.get("op") == name and frame.get("id", 0) > 0
                    ]
                    if matches:
                        return matches[0]
                    if operation.done():
                        self.fail("capture returned before the executor revoke acknowledgement")
                    await asyncio.sleep(0)

            late_reply = None
            if not foreground_gap or late_continuity:
                describe = await asyncio.wait_for(request("describe"), 1)
                self.peer.link.receive({"id": describe["id"], "data": description()})
                capture = await asyncio.wait_for(request("capture"), 1)
                reply = {
                    "id": capture["id"],
                    "data": None,
                    "captureContinuity": {
                        "target": binding["target"]["value"],
                        "nativeFocusRevision": 1,
                        "contextGeneration": 7,
                        "observedAtMs": self.clock.return_value,
                    },
                }
                if late_continuity:
                    self.clock.return_value = 1151
                    self.input.state()
                    old_capture = operation
                    operation = asyncio.create_task(
                        self.input.request("suspendOwnedSource", binding)
                    )
                    late_reply = reply
                else:
                    self.peer.link.receive(reply)
            revoke = await asyncio.wait_for(request("revoke"), 1)
            self.assertFalse(operation.done())
            if late_reply is not None:
                if invalidation in ("target", "nativeFocusRevision", "contextGeneration"):
                    late_reply["captureContinuity"][invalidation] = (
                        "another-field" if invalidation == "target" else 9
                    )
                elif invalidation == "native-null":
                    late_reply.pop("captureContinuity")
                elif invalidation == "close":
                    self.input.retire_practice(binding["runId"])
                elif invalidation == "settings":
                    self.input.notification("settings", {"enabled": True})
                elif invalidation == "unavailable":
                    self.input.niri._deliver(None)
                elif invalidation == "window-roundtrip":
                    self.input.window_epoch += 2
                elif invalidation == "foreground-roundtrip":
                    self.input.foreground_epoch += 2
                if late_continuity == "acknowledged":
                    self.peer.link.receive({"id": revoke["id"], "data": None})
                    self.assertTrue(await asyncio.wait_for(operation, 1))
                self.peer.link.receive(late_reply)
                fenced = await asyncio.wait_for(old_capture, 1)
                self.assertIsNone(self.input.bound_observation)
                self.assertIsNone(self.input.practice_authorization)
                if invalidation:
                    self.assertIsNone(fenced)
                    self.peer.link.receive({"id": revoke["id"], "data": None})
                    await asyncio.wait_for(operation, 1)
                    return
                self.assertEqual(fenced, {"snapshot": None, "fencedPracticeRead": binding})
            if foreground_gap:
                self.input.niri.observed_at = self.clock.return_value
                self.input.state()
                self.assertEqual(self.input.last_state["practiceForegroundSuspension"], binding)
                self.assertFalse(self.input.prepare_practice(binding, lambda _: True))
                self.assertEqual(
                    self.input.practice_waiting(binding["runId"]), late_continuity == "acknowledged"
                )
                if late_continuity != "acknowledged":
                    self.assertIsNone(
                        await self.input.practice_binding(self.peer.pid, binding["target"])
                    )
                self.assertEqual(self.native_frames(self.peer), [])
            self.input.notification("publish", publication)
            self.assertEqual(self.native_frames(self.peer), [])
            if invalidation == "focus":
                self.peer.event(
                    "input",
                    {
                        "target": binding["target"]["value"],
                        "focusChanged": True,
                        "focused": False,
                        "textChanged": False,
                    },
                )
            elif invalidation == "settings":
                self.input.notification("settings", {"enabled": True})
            elif invalidation == "close":
                self.input.retire_practice(binding["runId"])
            elif invalidation == "disconnect":
                self.input.lost(self.peer)
            if late_continuity != "acknowledged":
                self.peer.link.receive(
                    {"id": revoke["id"], "data": False if invalidation == "ack" else None}
                )
            result = await asyncio.wait_for(operation, 1)
            if invalidation:
                if foreground_gap:
                    self.assertFalse(result)
                else:
                    self.assertIsNone(result)
                self.assertIsNone(self.input.practice_authorization)
                self.assertIsNone(self.input.practice_continuity)
                return
            if foreground_gap:
                self.assertTrue(result)
            else:
                self.assertIsNone(result["snapshot"])
                self.assertEqual(result["ownedSource"]["binding"], binding)
                self.assertTrue(result["ownedSource"]["contextAdvanced"])
            self.assertIsNone(self.input.practice_authorization)
            self.input.notification("publish", publication)
            self.assertEqual(self.native_frames(self.peer), [])
            # The next independent refusal keeps only the already-ACKed intent.
            self.generation = str(uuid.uuid4())
            self.configure()
            self.native_frames(self.peer)
            second = {**binding, "commandGeneration": self.generation}
            operation = asyncio.create_task(self.input.request("capture", {"practiceRead": second}))
            describe = await asyncio.wait_for(request("describe"), 1)
            self.peer.link.receive({"id": describe["id"], "data": description()})
            capture = await asyncio.wait_for(request("capture"), 1)
            self.assertTrue(capture["data"]["ownedSource"])
            self.peer.link.receive(
                {
                    "id": capture["id"],
                    "data": None,
                    "captureContinuity": {
                        "target": binding["target"]["value"],
                        "nativeFocusRevision": 1,
                        "contextGeneration": 7,
                        "observedAtMs": self.clock.return_value,
                    },
                }
            )
            repeated = await asyncio.wait_for(operation, 1)
            self.assertEqual(repeated["ownedSource"]["binding"], second)
            self.assertEqual(
                self.native_frames(self.peer), [], "a repeated witness issued another revoke"
            )
            self.assertIsNone(self.input.practice_authorization)
            if resume:
                # A complete new capture with the new target-bound ACK is the
                # only route back to a newly constructed private authorization.
                async def complete_read(cross_age_gap=False):
                    nonlocal operation
                    operation = asyncio.create_task(
                        self.input.request("capture", {"practiceRead": second})
                    )
                    current = description()
                    current["sessionBindings"] = {
                        "generation": self.generation,
                        "accept": "Tab",
                        "exit": "Esc",
                        "sourceReady": True,
                        "editorReady": True,
                    }
                    describe = await asyncio.wait_for(request("describe"), 1)
                    self.peer.link.receive({"id": describe["id"], "data": current})
                    capture = await asyncio.wait_for(request("capture"), 1)
                    if cross_age_gap:
                        self.clock.return_value += 151
                        self.input.state()
                        self.input.niri._deliver(
                            (
                                "test",
                                [{"id": self.peer.pid, "pid": self.peer.pid, "is_focused": True}],
                                {},
                                {},
                                False,
                                self.clock.return_value,
                            )
                        )
                        self.input.state()
                    self.peer.link.receive(
                        {
                            "id": capture["id"],
                            "data": {
                                **current,
                                "text": "new source",
                                "selection": {"start": 10, "end": 10},
                                "ime": "Clear",
                                "observedAtMs": self.clock.return_value,
                                "writeSupported": True,
                            },
                        }
                    )
                    complete = await asyncio.wait_for(operation, 1)
                    if cross_age_gap:
                        self.assertTrue(complete["foregroundExpired"])
                        self.assertIsNone(self.input.bound_observation)
                        return
                    self.assertFalse(complete["ownedSource"]["contextAdvanced"])
                    self.assertEqual(complete["ownedSource"]["binding"], second)
                    return complete

                await complete_read()
                if retire_during_bind:
                    self.assertTrue(self.input.prepare_practice(second, lambda _: True))
                    # Runtime ends its still-awaiting G1 before it handles Bind G2:
                    # the new configure is sent before its old-binding retirement.
                    self.generation = str(uuid.uuid4())
                    self.configure()
                    self.native_frames(self.peer)
                    self.assertTrue(await self.input.request("retireAutomaticPractice", binding))
                    self.assertFalse(self.input.authorize_practice(second, lambda _: True))
                    self.assertIsNone(self.input.practice_authorization)
                    self.assertNotEqual(self.input.practice_retirement_ack, second["runId"])
                    return
                if age_gap:
                    self.clock.return_value = 1151
                    self.input.state()
                    self.assertIsNotNone(self.input.practice_continuity)
                    self.assertFalse(self.input.last_state["ready"])
                    self.assertEqual(self.input.last_state["focus"], "Other")
                    self.assertTrue(self.input.last_state["foregroundExpired"])
                    self.assertIsNone(self.input.bound_observation)
                    self.assertFalse(self.input.prepare_practice(second, lambda _: True))
                    refused = await self.input.request("capture", {"practiceRead": second})
                    self.assertTrue(refused["foregroundExpired"])
                    self.assertEqual(self.native_frames(self.peer), [])
                    self.input.niri._deliver(
                        (
                            "test",
                            [{"id": self.peer.pid, "pid": self.peer.pid, "is_focused": True}],
                            {},
                            {},
                            False,
                            1151,
                        )
                    )
                    self.input.state()
                    self.assertTrue(self.input.last_state["ready"])
                    self.assertFalse(self.input.prepare_practice(second, lambda _: True))
                    # Recovery does not re-use the completed pre-gap native read.
                    self.assertIsNone(self.input.bound_observation)
                    self.assertIsNone(self.input.practice_authorization)
                    self.assertEqual(self.generation, second["commandGeneration"])
                    await complete_read(cross_age_gap=True)
                    self.assertFalse(self.input.prepare_practice(second, lambda _: True))
                    await complete_read()
                self.assertTrue(self.authorize_practice(second, lambda _: True))
                self.input.notification("publish", publication)
                self.assertEqual(
                    self.native_frames(self.peer),
                    [],
                    "old generation revived after the new binding",
                )
                return
            # Close between this ACK and Runtime consuming the witness can join
            # exactly the old binding's real ACK, without granting a later run.
            self.assertTrue(await self.input.request("retireAutomaticPractice", binding))
            self.assertFalse(
                await self.input.request(
                    "retireAutomaticPractice", {**binding, "commandGeneration": str(uuid.uuid4())}
                )
            )

        asyncio.run(exercise())

    def test_age_fence_keeps_late_refusal_separate_from_fresh_evidence(self):
        self.owned_context_advance(foreground_gap=True, late_continuity="pending", resume=True)

    def test_acknowledged_age_fence_still_discards_the_old_native_read(self):
        self.owned_context_advance(foreground_gap=True, late_continuity="acknowledged", resume=True)

    def test_age_fence_does_not_classify_unknown_changed_or_closed_late_reads(self):
        for invalidation in (
            "target",
            "nativeFocusRevision",
            "contextGeneration",
            "native-null",
            "close",
            "settings",
            "unavailable",
            "window-roundtrip",
            "foreground-roundtrip",
        ):
            with self.subTest(invalidation=invalidation):
                self.owned_context_advance(
                    invalidation, foreground_gap=True, late_continuity="pending"
                )

    def test_active_foreground_expiry_requires_ack_even_when_freshness_recovers(self):
        self.owned_context_advance(foreground_gap=True, resume=True)

    def test_active_foreground_expiry_rejects_failed_ack(self):
        self.owned_context_advance("ack", foreground_gap=True)

    def test_owned_context_advance_waits_for_revoke_ack_and_keeps_old_publish_closed(self):
        self.owned_context_advance()

    def test_new_complete_owned_capture_rebinds_without_reviving_old_publication(self):
        self.owned_context_advance(resume=True)

    def test_age_expiry_keeps_only_acknowledged_intent_and_discards_cached_capture(self):
        self.owned_context_advance(resume=True, age_gap=True)

    def test_prepared_next_binding_does_not_hide_the_old_exact_retirement_ack(self):
        self.owned_context_advance(resume=True, retire_during_bind=True)

    def test_context_witness_cannot_survive_identity_or_intent_change_while_awaiting_ack(self):
        for invalidation in ("focus", "settings", "close", "disconnect"):
            with self.subTest(invalidation=invalidation):
                self.owned_context_advance(invalidation)

    async def observe_pause(self, description, before_reply=None):
        self.input.notification("settings", {"enabled": False})
        self.focus(self.peer)
        self.native_frames(self.peer)
        operation = asyncio.create_task(self.input.request("observePause", {"boundTarget": None}))
        await asyncio.sleep(0)
        requests = self.native_frames(self.peer)
        self.assertEqual([frame["op"] for frame in requests], ["describe"])
        self.assertEqual(requests[0]["data"], {})
        if before_reply:
            before_reply()
        self.peer.link.receive({"id": requests[0]["id"], "data": description})
        result = await asyncio.wait_for(operation, 1)
        self.assertFalse(any(frame["op"] == "capture" for frame in self.native_frames(self.peer)))
        return result

    async def describe_practice(self, peer, description, target=None, before_reply=None):
        self.focus(peer)
        self.native_frames(peer)
        operation = asyncio.create_task(self.input.practice_target(peer.pid, target))
        await asyncio.sleep(0)
        requests = self.native_frames(peer)
        self.assertEqual([frame["op"] for frame in requests], ["describe"])
        if before_reply:
            before_reply()
        peer.link.receive({"id": requests[0]["id"], "data": description})
        result = await asyncio.wait_for(operation, 1)
        self.assertTrue(
            all(frame["op"] == "revoke" for frame in self.native_frames(peer)),
            "practice identity requested body or grant",
        )
        return result

    def test_practice_identity_is_the_authenticated_process_exact_named_field(self):
        peer = self.connect_peer("gtk3")
        description = self.description()
        field = str(uuid.uuid4())
        description["target"] = description["authority"]["target"] = field
        description["metadata"].update(automationId="typelay-practice-source", aria="GtkEntry")
        target = asyncio.run(self.describe_practice(peer, description))
        self.assertEqual(
            target, {"backend": "linux-gtk3", "value": field, "lifetime": peer.lifetime}
        )
        self.assertEqual(self.host.requests, [], "identity lookup requested capture permissions")
        for change in ("name", "target", "focus", "peer"):
            with self.subTest(change=change):
                other = self.description()
                other["target"] = other["authority"]["target"] = field
                other["metadata"].update(automationId="typelay-practice-source", aria="GtkEntry")
                before = None
                if change == "name":
                    other["metadata"]["automationId"] = "another-field"
                elif change == "target":
                    other["authority"]["target"] = str(uuid.uuid4())
                elif change == "focus":
                    other["focused"] = False
                else:

                    def before():
                        self.focus(self.peer)

                self.assertIsNone(
                    asyncio.run(self.describe_practice(peer, other, before_reply=before))
                )
        for key in ("metadata", "authority"):
            for malformed in (None, [], ["unknown"], "unknown"):
                with self.subTest(key=key, malformed=malformed):
                    self.assertIsNone(
                        asyncio.run(self.describe_practice(peer, {**description, key: malformed}))
                    )
        another_field = str(uuid.uuid4())
        other = dict(
            description,
            target=another_field,
            authority={**description["authority"], "target": another_field},
        )
        self.assertIsNone(asyncio.run(self.describe_practice(peer, other, target)))
        self.assertIsNone(asyncio.run(self.input.practice_target(self.peer.pid)))
        self.assertIsNone(
            asyncio.run(
                self.input.practice_target(peer.pid, {**target, "lifetime": str(uuid.uuid4())})
            )
        )

    def authorize_practice(self, binding, owns_process):
        return self.input.prepare_practice(binding, owns_process) and self.input.authorize_practice(
            binding, owns_process
        )

    def test_automatic_authority_rejects_a_non_gtk_practice_peer(self):
        publication = asyncio.run(self.publish_acceptance(pause=False))[0]["data"]
        binding = {
            "runId": str(uuid.uuid4()),
            "target": publication["lease"]["source"]["target"],
            "connectionId": self.peer.connection_id,
            "commandGeneration": self.generation,
        }
        self.assertFalse(self.authorize_practice(binding, lambda _: True))

    async def bind_practice_with_held_reply(self, *, leave_source, bind_accepted=True):
        self.peer = self.connect_peer("gtk3")
        original_description = self.description
        field = str(uuid.uuid4())

        def description():
            result = original_description()
            result["target"] = result["authority"]["target"] = field
            result["metadata"].update(automationId="typelay-practice-source", aria="GtkEntry")
            result["geometry"]["space"] = "gtk-toplevel"
            return result

        self.description = description
        await self.publish_acceptance(pause=False)
        binding_started, release_binding = asyncio.Event(), asyncio.Event()
        progress, binding = {}, None

        async def ask(operation, data=None):
            nonlocal progress, binding
            if operation == "beginAutomaticPractice":
                progress = {"runId": data, "target": None, "completed": False}
            elif operation == "automaticPracticeProgress":
                return progress
            elif operation == "bindAutomaticPractice":
                binding = data
                binding_started.set()
                await release_binding.wait()
                if not bind_accepted:
                    return False
                progress = {**progress, **data}
            elif operation == "endAutomaticPractice":
                if not bind_accepted:
                    return True  # Runtime never registered the rejected binding.
                return await self.input.request("retireAutomaticPractice", binding)
            return True

        practice = PracticeSession(
            SimpleNamespace(ask=ask),
            self.input,
            SimpleNamespace(owns_practice=lambda pid: pid == self.peer.pid),
        )
        self.assertTrue(await practice.begin(self.peer.pid, True, lesson="automatic"))
        operation = asyncio.create_task(practice.progress(self.peer.pid, lesson="automatic"))
        await asyncio.sleep(0)
        requests = self.native_frames(self.peer)
        self.assertEqual([frame["op"] for frame in requests], ["describe"])
        self.peer.link.receive({"id": requests[0]["id"], "data": description()})
        await asyncio.wait_for(binding_started.wait(), 1)
        self.assertIsNone(self.input.practice_authorization)
        if leave_source:
            for focused in (False, True):
                self.peer.event(
                    "input",
                    {
                        "target": field,
                        "focusChanged": True,
                        "focused": focused,
                        "textChanged": False,
                    },
                )
            # Same peer/target and compositor focus do not prove uninterrupted
            # source focus while Runtime's binding reply was in flight.
            self.assertEqual(self.input.focus(), "Source")
        release_binding.set()

        async def finish():
            while not operation.done():
                for frame in self.native_frames(self.peer):
                    self.assertEqual(frame["op"], "revoke")
                    if frame["id"]:
                        self.peer.link.receive({"id": frame["id"], "data": None})
                await asyncio.sleep(0)
            return await operation

        result = await asyncio.wait_for(finish(), 1)
        # Keep teardown within the scheduler's lifetime, including the positive
        # control's still-active run. The tests assert the authorization first.
        authorized = self.input.practice_authorization is not None
        self.input.retire_practice(practice.run, notify=False)
        return practice, result, authorized

    def test_owned_practice_bind_reply_requires_continuous_source_focus(self):
        practice, progress, authorized = asyncio.run(
            self.bind_practice_with_held_reply(leave_source=True)
        )
        self.assertIsNone(progress)
        self.assertFalse(authorized)
        self.assertIsNone(self.input.practice_authorization)
        self.assertIsNone(practice.run)
        self.assertFalse(practice.end_unconfirmed)

    def test_rejected_runtime_binding_still_waits_for_its_pending_peer_ack(self):
        practice, progress, authorized = asyncio.run(
            self.bind_practice_with_held_reply(leave_source=False, bind_accepted=False)
        )
        self.assertIsNone(progress)
        self.assertFalse(authorized)
        self.assertIsNone(practice.run)
        self.assertFalse(practice.end_unconfirmed)
        self.assertIsNone(self.input.practice_retirement)
        self.assertIsNotNone(self.input.practice_retirement_ack)

    def test_owned_practice_bind_reply_keeps_unchanged_source_eligible(self):
        practice, progress, authorized = asyncio.run(
            self.bind_practice_with_held_reply(leave_source=False)
        )
        self.assertEqual(progress["runId"], practice.run)
        self.assertTrue(authorized)

    def test_owned_practice_revoke_waits_for_original_peer_ack_and_rejects_late_publish(self):
        self.peer = self.connect_peer("gtk3")
        original_description = self.description

        def description():
            result = original_description()
            result["geometry"]["space"] = "gtk-toplevel"
            return result

        self.description = description

        async def exercise():
            publication = (await self.publish_acceptance(pause=False))[0]["data"]
            binding = {
                "runId": str(uuid.uuid4()),
                "target": publication["lease"]["source"]["target"],
                "connectionId": self.peer.connection_id,
                "commandGeneration": self.generation,
            }
            self.assertTrue(self.authorize_practice(binding, lambda pid: pid == self.peer.pid))
            publication["lease"].update(
                practiceRunId=binding["runId"], practiceConnectionId=binding["connectionId"]
            )
            self.input.notification("publish", publication)
            self.assertEqual([frame["op"] for frame in self.native_frames(self.peer)], ["publish"])
            forged = {
                **publication,
                "lease": {**publication["lease"], "practiceConnectionId": str(uuid.uuid4())},
            }
            self.input.notification("publish", forged)
            self.assertEqual(self.native_frames(self.peer), [])
            retirement = asyncio.create_task(self.input.request("retireAutomaticPractice", binding))

            async def requested():
                while True:
                    requests = self.native_frames(self.peer)
                    if requests:
                        return requests
                    await asyncio.sleep(0)

            requests = await asyncio.wait_for(requested(), 1)
            self.assertEqual([frame["op"] for frame in requests], ["revoke"])
            self.assertGreater(requests[0]["id"], 0)
            self.assertFalse(
                retirement.done(), "queued revocation was reported as native completion"
            )
            self.input.notification("publish", publication)
            self.assertEqual(self.native_frames(self.peer), [])
            self.assertFalse(
                self.authorize_practice({**binding, "runId": str(uuid.uuid4())}, lambda _: True)
            )
            self.peer.link.receive({"id": requests[0]["id"], "data": None})
            self.assertTrue(await asyncio.wait_for(retirement, 1))
            self.assertTrue(await self.input.confirm_practice_retirement(binding["runId"]))
            self.assertFalse(await self.input.confirm_practice_retirement(str(uuid.uuid4())))
            self.input.notification("publish", publication)
            self.assertEqual(
                self.native_frames(self.peer), [], "retired publication returned after native ACK"
            )
            self.peer.event(
                "accepted",
                {
                    "planId": publication["permit"]["plan"]["id"],
                    "acceptedAtMs": 1001,
                    "receipt": {"status": "OutcomeUnknown", "reason": "UnknownOutcome"},
                },
            )
            notice = self.host.notices[-1]
            self.assertEqual(notice["practiceRunId"], binding["runId"])
            self.assertEqual(notice["practiceConnectionId"], binding["connectionId"])
            self.assertEqual(notice["completedReceipt"]["status"], "OutcomeUnknown")

        asyncio.run(exercise())

    def test_no_retirement_record_is_not_a_native_acknowledgement(self):
        self.assertFalse(asyncio.run(self.input.confirm_practice_retirement(str(uuid.uuid4()))))

    def test_owned_practice_native_revoke_timeout_blocks_a_new_authorization(self):
        self.peer = self.connect_peer("gtk3")
        original_description = self.description

        def description():
            result = original_description()
            result["geometry"]["space"] = "gtk-toplevel"
            return result

        self.description = description

        async def exercise():
            publication = (await self.publish_acceptance(pause=False))[0]["data"]
            binding = {
                "runId": str(uuid.uuid4()),
                "target": publication["lease"]["source"]["target"],
                "connectionId": self.peer.connection_id,
                "commandGeneration": self.generation,
            }
            self.assertTrue(self.authorize_practice(binding, lambda _: True))
            self.assertFalse(
                await asyncio.wait_for(self.input.request("retireAutomaticPractice", binding), 1)
            )
            self.assertFalse(
                self.authorize_practice({**binding, "runId": str(uuid.uuid4())}, lambda _: True)
            )

        asyncio.run(exercise())

    def test_owned_practice_disconnect_during_revoke_never_confirms_retirement(self):
        self.peer = self.connect_peer("gtk3")
        original_description = self.description

        def description():
            result = original_description()
            result["geometry"]["space"] = "gtk-toplevel"
            return result

        self.description = description

        async def exercise():
            publication = (await self.publish_acceptance(pause=False))[0]["data"]
            binding = {
                "runId": str(uuid.uuid4()),
                "target": publication["lease"]["source"]["target"],
                "connectionId": self.peer.connection_id,
                "commandGeneration": self.generation,
            }
            self.assertTrue(self.authorize_practice(binding, lambda _: True))
            retirement = asyncio.create_task(self.input.request("retireAutomaticPractice", binding))
            await asyncio.sleep(0)
            self.peer.link.close()
            self.assertFalse(await asyncio.wait_for(retirement, 1))
            self.assertFalse(await self.input.confirm_practice_retirement(binding["runId"]))
            self.assertFalse(self.authorize_practice(binding, lambda _: True))

        asyncio.run(exercise())

    def test_paused_field_observation_returns_only_metadata_anchor_without_capture(self):
        result = asyncio.run(self.observe_pause(self.description()))
        self.assertEqual(result["focus"], "Source")
        self.assertEqual(
            result["anchor"],
            {
                "target": {
                    "backend": "linux-qt6",
                    "value": "ordinary-field",
                    "lifetime": self.peer.lifetime,
                },
                "bounds": {"x": 2, "y": 3, "width": 20, "height": 10},
                "access": "Supported",
                "observedAtMs": 1000,
                "focusEpoch": 2,
            },
        )
        self.assertEqual([name for name, _ in self.host.requests], ["allowPauseField"])

    def test_paused_policy_refusal_never_requests_source_capture(self):
        self.host.pause_allowed = False
        self.assertIsNone(asyncio.run(self.observe_pause(self.description())))
        self.assertEqual([name for name, _ in self.host.requests], ["allowPauseField"])

    def test_paused_unknown_metadata_cannot_become_a_safe_empty_field(self):
        for key in self.description()["metadata"]:
            with self.subTest(key=key):
                description = self.description()
                del description["metadata"][key]
                self.assertIsNone(asyncio.run(self.observe_pause(description)))
        self.assertEqual(self.host.requests, [])

    def test_paused_late_description_after_resume_cannot_rebind_source(self):
        self.assertIsNone(
            asyncio.run(
                self.observe_pause(
                    self.description(),
                    lambda: self.input.notification("settings", {"enabled": True}),
                )
            )
        )
        states = [value for name, value in self.host.events if name == "state"]
        self.assertIsNone(states[-1]["target"])

    def test_paused_late_description_after_field_focus_change_is_rejected(self):
        def change_field():
            self.peer.event(
                "input",
                {
                    "target": "new-field",
                    "textChanged": False,
                    "focusChanged": True,
                    "focused": True,
                },
            )

        self.assertIsNone(asyncio.run(self.observe_pause(self.description(), change_field)))
        states = [value for name, value in self.host.events if name == "state"]
        self.assertIsNone(states[-1]["target"])

    def test_paused_stale_or_unfocused_description_cannot_display_entry(self):
        for replacement in ({"focused": False}, {"geometry": None}):
            with self.subTest(replacement=replacement):
                self.assertIsNone(
                    asyncio.run(self.observe_pause({**self.description(), **replacement}))
                )
        description = self.description()
        description["authority"]["observedAtMs"] = 849
        self.assertIsNone(asyncio.run(self.observe_pause(description)))

    def test_paused_anchor_expires_and_cannot_authorize_a_capture(self):
        asyncio.run(self.observe_pause(self.description()))
        self.clock.return_value = 1151
        self.input.expire()
        states = [value for name, value in self.host.events if name == "state"]
        self.assertIsNone(states[-1]["target"])
        self.assertEqual(states[-1]["focus"], "Other")
        self.assertIsNone(asyncio.run(self.input.request("capture", {})))
        self.assertFalse(
            any(frame["op"] in ("describe", "capture") for frame in self.native_frames(self.peer))
        )

    def test_paused_describe_timeout_does_not_fall_back_to_source_capture(self):
        async def timed_out():
            self.input.notification("settings", {"enabled": False})
            self.focus(self.peer)
            self.native_frames(self.peer)
            result = await asyncio.wait_for(self.input.request("observePause", {}), 1)
            requests = self.native_frames(self.peer)
            self.assertEqual([frame["op"] for frame in requests], ["describe"])
            return result

        self.assertIsNone(asyncio.run(timed_out()))
        self.assertEqual(self.host.requests, [])

    async def publish_acceptance(
        self,
        *,
        acknowledgement=True,
        pause=True,
        before_publish=None,
        authority_acknowledgement=None,
    ):
        self.host.capture_allowed = True
        self.input.notification("settings", {"enabled": True})
        self.focus(self.peer)
        self.native_frames(self.peer)
        operation = asyncio.create_task(self.input.request("capture", {}))

        async def reply_to(expected, result):
            while True:
                requests = self.native_frames(self.peer)
                if requests:
                    self.assertEqual([frame["op"] for frame in requests], [expected])
                    self.peer.link.receive({"id": requests[0]["id"], "data": result})
                    return
                await asyncio.sleep(0)

        description = self.description()
        if acknowledgement:
            description["sessionBindings"] = {
                "generation": self.generation,
                "accept": "Tab",
                "exit": "Esc",
                "sourceReady": True,
                "editorReady": True,
                **(acknowledgement if isinstance(acknowledgement, dict) else {}),
            }
        if authority_acknowledgement is not None:
            description["authority"]["sessionBindings"] = {
                **description["sessionBindings"],
                **authority_acknowledgement,
            }
        await asyncio.wait_for(reply_to("describe", description), 1)
        native = {
            **description,
            "text": "Original source",
            "selection": {"start": 0, "end": 0},
            "ime": "Clear",
            "observedAtMs": 1000,
            "writeSupported": True,
        }
        await asyncio.wait_for(reply_to("capture", native), 1)
        capture = await asyncio.wait_for(operation, 1)
        plan_id = str(uuid.uuid4())
        generation = self.generation
        if before_publish:
            before_publish()
            self.native_frames(self.peer)
        self.input.notification(
            "publish",
            {
                "lease": {
                    "source": capture["snapshot"],
                    "userEpoch": capture["userEpoch"],
                    "foregroundEpoch": capture["foregroundEpoch"],
                    "escapeAllowed": True,
                    "commandGeneration": generation,
                    "sessionRevision": 9,
                },
                "permit": {
                    "plan": {
                        "id": plan_id,
                        "expected": capture["snapshot"],
                        "replacement": "Translation",
                    },
                    "expiresAtMs": 1150,
                },
            },
        )
        frames = self.native_frames(self.peer)
        if (
            not acknowledgement
            or isinstance(acknowledgement, dict)
            or before_publish
            or authority_acknowledgement is not None
        ):
            return frames
        self.assertEqual([frame["op"] for frame in frames], ["publish"])
        if not pause:
            return frames
        self.input.notification("settings", {"enabled": False})
        self.assertEqual([frame["op"] for frame in self.native_frames(self.peer)], ["revoke"])
        return plan_id

    def test_peer_without_binding_acknowledgement_cannot_receive_acceptance_permit(self):
        frames = asyncio.run(self.publish_acceptance(acknowledgement=False))
        self.assertFalse(
            any(frame["op"] == "publish" and frame["data"].get("permit") for frame in frames)
        )

    def test_binding_acknowledgement_carries_the_generation_on_publication(self):
        frames = asyncio.run(self.publish_acceptance(pause=False))
        self.assertEqual(frames[0]["data"].get("generation"), self.generation)

    def test_conflicting_authority_binding_acknowledgement_cannot_authorize_publication(self):
        frames = asyncio.run(
            self.publish_acceptance(authority_acknowledgement={"generation": str(uuid.uuid4())})
        )
        self.assertFalse(any(frame["op"] == "publish" for frame in frames))

    def test_matching_optional_authority_acknowledgement_preserves_publication(self):
        frames = asyncio.run(self.publish_acceptance(authority_acknowledgement={}))
        self.assertEqual([frame["op"] for frame in frames], ["publish"])

    def test_wrong_or_incomplete_binding_acknowledgement_never_authorizes_publication(self):
        for change in (
            {"generation": str(uuid.uuid4())},
            {"accept": "F7"},
            {"exit": "F8"},
            {"sourceReady": False},
            {"sourceReady": 1},
            {"editorReady": None},
        ):
            with self.subTest(change=change):
                frames = asyncio.run(self.publish_acceptance(acknowledgement=change))
                self.assertFalse(any(frame["op"] == "publish" for frame in frames))

    def test_configuration_change_rejects_old_target_acknowledgement_and_publication(self):
        def reconfigure():
            self.generation = str(uuid.uuid4())
            self.configure("F7", "F8")

        frames = asyncio.run(self.publish_acceptance(before_publish=reconfigure))
        self.assertFalse(any(frame["op"] == "publish" for frame in frames))

    def test_delayed_source_exit_cannot_close_a_new_configuration(self):
        asyncio.run(self.publish_acceptance(pause=False))
        self.peer.event("escape", {"target": "ordinary-field", "generation": str(uuid.uuid4())})
        self.assertEqual(self.host.notices, [])

    def test_source_exit_is_bound_to_the_published_target_and_session_revision(self):
        asyncio.run(self.publish_acceptance(pause=False))
        for change in ({"target": "other-field"}, {"sessionRevision": 8}):
            self.peer.event(
                "escape",
                {
                    "target": "ordinary-field",
                    "generation": self.generation,
                    "sessionRevision": 9,
                    **change,
                },
            )
            self.assertEqual(self.host.notices, [])
        self.peer.event(
            "escape",
            {
                "target": "ordinary-field",
                "generation": self.generation,
                "sessionRevision": 9,
            },
        )
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["sessionRevision"], 9)
        self.assertEqual(self.host.notices[0]["target"]["value"], "ordinary-field")
        self.assertEqual(self.host.notices[0]["commandGeneration"], self.generation)

    def test_editor_return_requires_editor_acknowledgement(self):
        frames = asyncio.run(self.publish_acceptance(acknowledgement={"editorReady": False}))
        self.assertEqual([frame["op"] for frame in frames], ["publish"])
        context = {
            "boundTarget": frames[0]["data"]["lease"]["source"]["target"],
            "sessionShortcuts": {
                "generation": self.generation,
                "bindings": {"accept": "Tab", "exit": "Esc"},
            },
        }
        self.assertFalse(asyncio.run(self.input.request("return", context)))
        self.assertEqual(self.native_frames(self.peer), [])

    def test_configuration_change_during_return_description_cannot_move_focus(self):
        async def returning():
            frames = await self.publish_acceptance(pause=False)
            context = {
                "boundTarget": frames[0]["data"]["lease"]["source"]["target"],
                "sessionShortcuts": {
                    "generation": self.generation,
                    "bindings": {"accept": "Tab", "exit": "Esc"},
                },
            }
            operation = asyncio.create_task(self.input.request("return", context))
            await asyncio.sleep(0)
            requests = self.native_frames(self.peer)
            self.assertEqual([frame["op"] for frame in requests], ["describe"])
            self.generation = str(uuid.uuid4())
            self.configure("F7", "F8")
            self.native_frames(self.peer)
            self.peer.link.receive({"id": requests[0]["id"], "data": self.description()})
            self.assertFalse(await asyncio.wait_for(operation, 1))
            self.assertFalse(
                any(frame["op"] == "return" for frame in self.native_frames(self.peer))
            )

        asyncio.run(returning())

    async def overlay_scenario(self):
        publication = (await self.publish_acceptance(pause=False))[0]["data"]
        jobs = []
        overlay = Overlay(
            self.host,
            self.input,
            lambda coroutine: jobs.append(asyncio.create_task(coroutine)),
            lambda: None,
        )
        token = {
            "target": publication["lease"]["source"]["target"],
            "commandGeneration": self.generation,
            "revision": 9,
        }
        view = {
            "state": "ValidTranslation",
            "source": {"target": token["target"]},
            "viewContext": {
                "token": token,
                "bindings": {"accept": "Tab", "exit": "Esc"},
                "canEdit": True,
            },
            "canAccept": True,
        }
        overlay.render(view)
        await self.settle_overlay(jobs)
        return overlay, jobs, token, view

    async def settle_overlay(self, jobs):
        received = []
        async with asyncio.timeout(1):
            while any(not job.done() for job in jobs):
                await asyncio.sleep(0)
                for frame in self.native_frames(self.peer):
                    received.append(frame)
                    if frame["op"] == "overlay-render":
                        self.peer.link.receive(
                            {
                                "id": frame["id"],
                                "data": {
                                    "visible": True,
                                    "editing": frame["data"]["view"]["state"]
                                    == "EditingTranslation",
                                },
                            }
                        )
                for job in jobs:
                    if job.done():
                        job.result()
        received.extend(self.native_frames(self.peer))
        return received

    def test_overlay_return_echoes_displayed_token_and_rejects_old_generation(self):
        async def exercise():
            overlay, _jobs, token, _view = await self.overlay_scenario()
            event = {
                "operation": "return",
                "target": "ordinary-field",
                "revision": overlay.revision,
                "generation": self.generation,
            }
            self.peer.event("ui", {**event, "generation": str(uuid.uuid4())})
            self.assertFalse(any(name == "return" for name, _ in self.host.events))
            self.peer.event("ui", event)
            self.assertEqual(
                [value for name, value in self.host.events if name == "return"], [token]
            )

        asyncio.run(exercise())

    def test_begin_edit_uses_the_token_returned_after_the_shared_transition(self):
        async def exercise():
            overlay, jobs, token, view = await self.overlay_scenario()
            updated_token = {**token, "revision": 10}

            async def begin_edit(request):
                self.assertEqual(request, token)
                overlay.render(
                    {
                        **view,
                        "state": "EditingTranslation",
                        "viewContext": {
                            **view["viewContext"],
                            "token": updated_token,
                        },
                    }
                )
                return {"allowed": True, "token": updated_token}

            self.host.begin_edit = begin_edit
            self.peer.event(
                "ui",
                {
                    "operation": "beginEdit",
                    "target": "ordinary-field",
                    "revision": overlay.revision,
                    "generation": self.generation,
                },
            )
            received = await self.settle_overlay(jobs)
            edits = [frame["data"] for frame in received if frame["op"] == "overlay-edit"]
            self.assertEqual(
                edits,
                [
                    {
                        "target": "ordinary-field",
                        "allowed": True,
                        "generation": self.generation,
                        "revision": overlay.revision,
                    }
                ],
            )
            self.assertEqual(overlay.session_token(), updated_token)

        asyncio.run(exercise())

    def owned_edit_handoff(self, invalidation=None, returning=None):
        async def exercise():
            self.peer = self.connect_peer("gtk3")
            original = self.description

            def description():
                value = original()
                value["geometry"]["space"] = "gtk-toplevel"
                value["authority"]["contextStamp"] = {"context": 7}
                return value

            self.description = description
            overlay, jobs, token, view = await self.overlay_scenario()
            binding = {
                "runId": str(uuid.uuid4()),
                "target": token["target"],
                "connectionId": self.peer.connection_id,
                "commandGeneration": self.generation,
                "nativeFocusRevision": 1,
                "contextGeneration": 7,
            }
            self.assertTrue(self.authorize_practice(binding, lambda _: True))
            retired = []
            self.input.practice_retired = retired.append
            updated = {**token, "revision": 10}

            async def begin_edit(_request):
                overlay.render(
                    {
                        **view,
                        "state": "EditingTranslation",
                        "viewContext": {**view["viewContext"], "token": updated},
                    }
                )
                return {"allowed": True, "token": updated}

            self.host.begin_edit = begin_edit
            self.peer.event(
                "ui",
                {
                    "operation": "beginEdit",
                    "target": "ordinary-field",
                    "revision": overlay.revision,
                    "generation": self.generation,
                },
            )
            revoke = edit = None
            async with asyncio.timeout(1):
                while any(not task.done() for task in jobs):
                    await asyncio.sleep(0)
                    for frame in self.native_frames(self.peer):
                        if frame["op"] == "overlay-render":
                            self.peer.link.receive(
                                {"id": frame["id"], "data": {"visible": True, "editing": True}}
                            )
                        elif frame["op"] == "revoke" and frame["id"]:
                            revoke = frame
                            self.assertFalse(
                                self.input.practice_publication_allowed(
                                    {"source": {"target": token["target"]}}, self.peer
                                )
                            )
                            self.peer.link.receive(
                                {
                                    "id": frame["id"],
                                    "data": False if invalidation == "revoke" else None,
                                }
                            )
                        elif frame["op"] == "overlay-edit":
                            edit = frame
                            self.assertIsNotNone(revoke)
                            self.assertGreater(frame["id"], 0)
                            self.peer.event(
                                "input",
                                {
                                    "target": "ordinary-field",
                                    "textChanged": False,
                                    "focusChanged": True,
                                    "focused": False,
                                },
                            )
                            self.assertEqual(retired, [])
                            if invalidation == "other":
                                self.peer.event(
                                    "ui",
                                    {
                                        "operation": "focus",
                                        "target": "ordinary-field",
                                        "revision": overlay.revision,
                                        "generation": self.generation,
                                        "family": "Other",
                                        "value": False,
                                    },
                                )
                            elif invalidation == "close":
                                self.input.retire_practice(binding["runId"])
                            self.peer.link.receive(
                                {
                                    "id": frame["id"],
                                    "data": {"visible": True, "editing": True, "handoff": True},
                                }
                            )
                for task in jobs:
                    task.result()
            self.assertIsNotNone(revoke)
            if invalidation == "revoke":
                self.assertIsNone(edit)
            else:
                self.assertIsNotNone(edit)
            if invalidation:
                self.assertEqual(retired, [binding["runId"]])
                self.assertFalse(self.input.practice_editing(binding["runId"]))
                self.assertFalse(
                    self.input.practice_publication_allowed(
                        {
                            "source": {"target": token["target"]},
                            "practiceRunId": binding["runId"],
                            "practiceConnectionId": binding["connectionId"],
                            "commandGeneration": self.generation,
                        },
                        self.peer,
                    )
                )
                self.assertFalse(self.input.prepare_practice(binding, lambda _: True))
                return
            self.assertEqual(retired, [])
            self.assertEqual(self.input.focus(), "Overlay")
            self.peer.event(
                "ui",
                {
                    "operation": "draft",
                    "target": "ordinary-field",
                    "revision": overlay.revision,
                    "generation": self.generation,
                    "value": "actually changed draft",
                },
            )
            self.assertIn(("draft", "actually changed draft"), self.host.events)
            if returning is not None:
                await self.owned_return(overlay, jobs, view, binding, returning)
                return
            self.peer.event(
                "ui",
                {
                    "operation": "focus",
                    "target": "ordinary-field",
                    "revision": overlay.revision,
                    "generation": self.generation,
                    "value": False,
                    "family": "Source",
                },
            )
            self.assertFalse(
                self.input.practice_publication_allowed(
                    {"source": {"target": token["target"]}}, self.peer
                )
            )
            self.assertFalse(self.input.prepare_practice(binding, lambda _: True))
            # The editing ACK is not a whole-run retirement receipt.
            ending = asyncio.create_task(self.input.request("retireAutomaticPractice", binding))
            await asyncio.sleep(0)
            await asyncio.sleep(0)
            final_revoke = [
                frame
                for frame in self.native_frames(self.peer)
                if frame["op"] == "revoke" and frame["id"]
            ]
            self.assertEqual(len(final_revoke), 1)
            self.peer.link.receive({"id": final_revoke[0]["id"], "data": None})
            self.assertTrue(await ending)

        asyncio.run(exercise())

    async def owned_return(self, overlay, jobs, view, binding, invalidation):
        token = {**overlay.session_token(), "revision": 11}
        overlay.render(
            {
                **view,
                "state": "ReturningToSource",
                "viewContext": {**view["viewContext"], "token": token},
            }
        )
        await self.settle_overlay(jobs)
        old_generation = self.generation
        context = {
            "boundTarget": binding["target"],
            "sessionShortcuts": {
                "generation": self.generation,
                "bindings": {"accept": "Tab", "exit": "Esc"},
            },
            "practiceReturn": {"binding": binding, "token": token},
        }
        operation = asyncio.create_task(self.input.request("return", context))

        async def next_request(kind):
            async with asyncio.timeout(1):
                while True:
                    await asyncio.sleep(0)
                    for frame in self.native_frames(self.peer):
                        if frame["op"] == kind and frame["id"]:
                            return frame
                    if operation.done():
                        self.fail(f"return/capture finished before {kind}: {operation.result()}")

        current = self.description()
        current["sessionBindings"] = {
            "generation": self.generation,
            "accept": "Tab",
            "exit": "Esc",
            "sourceReady": True,
            "editorReady": True,
        }
        describe = await next_request("describe")
        self.peer.link.receive({"id": describe["id"], "data": current})
        request = await next_request("return")
        self.assertEqual(request["data"]["practiceReturn"]["token"], token)
        self.peer.event(
            "input",
            {
                "target": binding["target"]["value"],
                "focusChanged": True,
                "focused": True,
                "textChanged": False,
            },
        )
        self.peer.event(
            "ui",
            {
                "operation": "focus",
                "target": binding["target"]["value"],
                "revision": token["revision"],
                "generation": self.generation,
                "family": "Source",
                "value": False,
            },
        )
        proof = {
            "target": binding["target"]["value"],
            "token": token,
            "nativeFocusRevision": 3,
            "contextGeneration": 7,
            "observedAtMs": 1000,
        }
        self.peer.link.receive(
            {
                "id": request["id"],
                "data": {
                    "returned": True,
                    "practiceReturn": proof,
                    "returnTrace": {
                        "phase": "ready",
                        "same": True,
                        "ready": "not-a-boolean",
                        "text": "private-source",
                        "target": "private-target",
                    },
                },
            }
        )
        revoke = await next_request("revoke")
        self.assertFalse(operation.done())
        self.assertIsNone(self.input.practice_authorization)
        old_lease = {
            "source": {"target": binding["target"]},
            "commandGeneration": old_generation,
            "practiceRunId": binding["runId"],
            "practiceConnectionId": binding["connectionId"],
        }
        self.assertFalse(self.input.practice_publication_allowed(old_lease, self.peer))
        if invalidation == "close":
            self.input.retire_practice(binding["runId"])
        elif invalidation == "other":
            self.peer.event(
                "input",
                {
                    "target": binding["target"]["value"],
                    "focusChanged": True,
                    "focused": False,
                    "textChanged": False,
                },
            )
        self.peer.link.receive(
            {"id": revoke["id"], "data": False if invalidation == "ack" else None}
        )
        result = await asyncio.wait_for(operation, 1)
        if invalidation != "success":
            self.assertNotIn("practice", result)
            self.assertIsNone(self.input.practice_return)
            self.assertIsNone(self.input.practice_authorization)
            return
        self.assertEqual(result["practice"]["binding"], binding)
        self.assertEqual(result["practice"]["nativeFocusRevision"], 3)
        self.assertFalse(self.input.practice_editing(binding["runId"]))
        self.assertTrue(self.input.practice_waiting(binding["runId"]))
        self.assertIsNone(self.input.bound_observation)
        self.generation = str(uuid.uuid4())
        self.configure()
        self.native_frames(self.peer)
        new_binding = {**binding, "commandGeneration": self.generation, "nativeFocusRevision": 3}
        self.assertFalse(self.input.prepare_practice(new_binding, lambda _: True))
        operation = asyncio.create_task(
            self.input.request("capture", {"practiceRead": new_binding})
        )
        current["authority"]["focusRevision"] = 3
        current["sessionBindings"]["generation"] = self.generation
        describe = await next_request("describe")
        self.peer.link.receive({"id": describe["id"], "data": current})
        capture = await next_request("capture")
        self.assertNotIn("ownedSource", capture["data"])
        self.peer.link.receive(
            {
                "id": capture["id"],
                "data": {
                    **current,
                    "text": "source unchanged",
                    "selection": {"start": 16, "end": 16},
                    "ime": "Clear",
                    "observedAtMs": 1000,
                    "writeSupported": True,
                },
            }
        )
        captured = await asyncio.wait_for(operation, 1)
        self.assertEqual(captured["ownedSource"]["binding"], new_binding)
        self.assertTrue(self.input.prepare_practice(new_binding, lambda _: True))
        pending = self.input.practice_pending
        self.assertTrue(self.input.authorize_practice(new_binding, lambda _: True, pending))
        self.assertFalse(self.input.practice_publication_allowed(old_lease, self.peer))
        self.assertTrue(
            self.input.practice_publication_allowed(
                {**old_lease, "commandGeneration": self.generation}, self.peer
            )
        )
        # The old exact ACK cannot retire this new instance without a new ask.
        operation = asyncio.create_task(self.input.request("retireAutomaticPractice", new_binding))
        revoke = await next_request("revoke")
        self.assertFalse(operation.done())
        self.peer.link.receive({"id": revoke["id"], "data": None})
        self.assertTrue(await operation)

    def test_owned_return_needs_its_own_ack_and_fresh_new_generation_binding(self):
        self.owned_edit_handoff(returning="success")

    def test_owned_return_rejects_late_ack_after_close_or_source_focus_loss(self):
        for reason in ("close", "other"):
            with self.subTest(reason=reason):
                self.owned_edit_handoff(returning=reason)

    def test_owned_return_trace_keeps_the_result_and_only_known_fields(self):
        output = io.StringIO()
        with patch.dict(os.environ, {"TYPELAY_TRACE": "1"}), contextlib.redirect_stderr(output):
            self.owned_edit_handoff(returning="success")
        records = [
            json.loads(line.split(" ", 1)[1])
            for line in output.getvalue().splitlines()
            if line.startswith("typelay-return ")
        ]
        self.assertEqual(
            records,
            [
                {
                    "phase": "complete",
                    "nativeReturned": True,
                    "nativePhase": "ready",
                    "same": True,
                    "atMs": 1000,
                }
            ],
        )

    def test_owned_return_rejects_non_null_ack(self):
        self.owned_edit_handoff(returning="ack")

    def test_owned_edit_handoff_waits_for_revoke_and_never_restores_source_authority(self):
        self.owned_edit_handoff()

    def test_owned_edit_handoff_rejects_a_late_success_after_third_party_focus(self):
        self.owned_edit_handoff("other")

    def test_owned_edit_handoff_rejects_a_late_success_after_close(self):
        self.owned_edit_handoff("close")

    def test_owned_edit_handoff_requires_the_native_revoke_ack_shape(self):
        self.owned_edit_handoff("revoke")

    def test_old_begin_edit_reply_cannot_enter_a_new_same_target_session(self):
        async def exercise():
            overlay, jobs, token, view = await self.overlay_scenario()

            async def begin_edit(_request):
                overlay.render(
                    {
                        **view,
                        "state": "EditingTranslation",
                        "viewContext": {
                            **view["viewContext"],
                            "token": {**token, "revision": 12},
                        },
                    }
                )
                return {"allowed": True, "token": {**token, "revision": 10}}

            self.host.begin_edit = begin_edit
            self.peer.event(
                "ui",
                {
                    "operation": "beginEdit",
                    "target": "ordinary-field",
                    "revision": overlay.revision,
                    "generation": self.generation,
                },
            )
            received = await self.settle_overlay(jobs)
            self.assertFalse(any(frame["op"] == "overlay-edit" for frame in received))

        asyncio.run(exercise())

    def test_pause_preserves_only_once_the_receipt_of_previously_accepted_input(self):
        plan_id = asyncio.run(self.publish_acceptance())
        receipt = {
            "planId": plan_id,
            "acceptedAtMs": 1001,
            "receipt": {"status": "Verified", "reason": "None"},
        }
        self.peer.event("accepted", receipt)
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["completedReceipt"], receipt["receipt"])
        self.assertFalse(
            any(
                frame["op"] in ("publish", "capture", "direct")
                for frame in self.native_frames(self.peer)
            )
        )
        self.peer.event("accepted", receipt)
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual([frame["op"] for frame in self.native_frames(self.peer)], ["quarantine"])

    def test_reconfiguration_preserves_original_uncertain_receipt_without_reissuing_a_write(self):
        publication = asyncio.run(self.publish_acceptance(pause=False))[0]["data"]
        original_generation = self.generation
        self.generation = str(uuid.uuid4())
        self.configure("F7", "F8")
        self.native_frames(self.peer)
        receipt = {
            "planId": publication["permit"]["plan"]["id"],
            "acceptedAtMs": 1001,
            "receipt": {"status": "OutcomeUnknown", "reason": "UnknownOutcome"},
        }
        self.peer.event("accepted", receipt)
        self.assertEqual(len(self.host.notices), 1)
        notice = self.host.notices[0]
        self.assertEqual(notice["completedReceipt"], receipt["receipt"])
        self.assertEqual(notice["permit"], publication["permit"])
        self.assertEqual(notice["commandGeneration"], original_generation)
        self.assertEqual(notice["target"], publication["lease"]["source"]["target"])
        self.assertEqual(self.native_frames(self.peer), [])
        self.peer.event("accepted", receipt)
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual([frame["op"] for frame in self.native_frames(self.peer)], ["quarantine"])

    def test_another_peer_cannot_consume_the_original_accepted_receipt(self):
        plan_id = asyncio.run(self.publish_acceptance())
        other = self.connect_peer()
        self.native_frames(other)
        receipt = {
            "planId": plan_id,
            "acceptedAtMs": 1001,
            "receipt": {"status": "OutcomeUnknown", "reason": "UnknownOutcome"},
        }
        other.event("accepted", receipt)
        self.assertEqual(self.host.notices, [])
        self.assertEqual([frame["op"] for frame in self.native_frames(other)], ["quarantine"])
        self.peer.event("accepted", receipt)
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["completedReceipt"], receipt["receipt"])
        self.assertEqual(self.native_frames(self.peer), [])

    def test_paused_receipt_retention_expires_without_reissuing_the_old_permission(self):
        plan_id = asyncio.run(self.publish_acceptance())
        self.clock.return_value = 2001
        self.input.expire()
        self.peer.event(
            "accepted",
            {
                "planId": plan_id,
                "acceptedAtMs": 1001,
                "receipt": {"status": "Verified", "reason": "None"},
            },
        )
        self.assertEqual(self.host.notices, [])
        self.assertEqual([frame["op"] for frame in self.native_frames(self.peer)], ["quarantine"])
        self.assertIsNone(asyncio.run(self.input.request("capture", {})))
        self.assertFalse(
            any(
                frame["op"] in ("publish", "capture", "direct")
                for frame in self.native_frames(self.peer)
            )
        )

    def test_refusal_diagnostic_is_whitelisted_and_logged_after_the_real_notice(self):
        command = self.command()
        output = io.StringIO()
        refusal = {
            **command,
            "reason": "EpochChanged",
            "diagnostic": {"phase": "ime", "gate": "input-revision"},
        }
        with patch.dict(os.environ, {"TYPELAY_TRACE": "1"}), contextlib.redirect_stderr(output):
            self.peer.event("shortcut-rejected", refusal)
            self.peer.event("shortcut-rejected", refusal)
        lines = output.getvalue().splitlines()
        self.assertEqual(len(lines), 1)
        self.assertEqual(
            json.loads(lines[0].split(" ", 1)[1]),
            {
                "sequence": 1,
                "atMs": 1000,
                "phase": "ime",
                "gate": "input-revision",
                "reason": "EpochChanged",
            },
        )
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["rejection"], "EpochChanged")
        self.assertNotIn("diagnostic", self.host.notices[0])

    def test_invalid_or_missing_refusal_diagnostic_cannot_change_rejection_or_log_text(self):
        output = io.StringIO()
        invalid = [
            None,
            "PRIVATE",
            [],
            {"phase": [], "gate": "input-revision"},
            {"phase": "ime", "gate": "PRIVATE"},
            {"phase": "PRIVATE", "gate": "input-revision"},
            {"phase": "ime", "gate": "input-revision", "body": "PRIVATE"},
        ]
        with patch.dict(os.environ, {"TYPELAY_TRACE": "1"}), contextlib.redirect_stderr(output):
            for index, diagnostic in enumerate(invalid, 1):
                command = self.command(command_id=index)
                self.peer.event(
                    "shortcut-rejected",
                    {
                        **command,
                        "reason": "EpochChanged",
                        "diagnostic": diagnostic,
                    },
                )
        self.assertEqual(output.getvalue(), "")
        self.assertEqual(len(self.host.notices), len(invalid))
        self.assertTrue(all(n["rejection"] == "EpochChanged" for n in self.host.notices))

    def test_refusal_trace_is_bounded_and_stderr_failure_cannot_hide_the_notice(self):
        output = io.StringIO()
        with patch.dict(os.environ, {"TYPELAY_TRACE": "1"}), contextlib.redirect_stderr(output):
            for index in range(1, 98):
                command = self.command(command_id=index)
                self.peer.event(
                    "shortcut-rejected",
                    {
                        **command,
                        "reason": "EpochChanged",
                        "diagnostic": {"phase": "ime", "gate": "input-revision"},
                    },
                )
        self.assertEqual(len(output.getvalue().splitlines()), 96)
        self.assertEqual(len(self.host.notices), 97)
        # A fresh monitor tests the write failure before its diagnostic cap.
        self.input.shortcut_diagnostic_count = 0
        command = self.command(command_id=98)

        def unavailable(*_args, **_kwargs):
            self.assertEqual(len(self.host.notices), 98)
            raise OSError("closed diagnostic stream")

        with patch.dict(os.environ, {"TYPELAY_TRACE": "1"}), patch("builtins.print", unavailable):
            self.peer.event(
                "shortcut-rejected",
                {
                    **command,
                    "reason": "EpochChanged",
                    "diagnostic": {"phase": "ime", "gate": "input-revision"},
                },
            )
        self.assertEqual(self.host.notices[-1]["rejection"], "EpochChanged")

    def test_native_publication_trace_observes_queue_handoff_without_new_protocol_fields(self):
        output = io.StringIO()
        with patch.dict(os.environ, {"TYPELAY_TRACE": "1"}), contextlib.redirect_stderr(output):
            frames = asyncio.run(self.publish_acceptance(pause=False))
        self.assertEqual(output.getvalue(), "")
        records = self.input.diagnostics()["nativePublications"]
        self.assertEqual(len(records), 1)
        diagnostic = records[0]
        self.assertEqual(
            diagnostic,
            {
                "planId": frames[0]["data"]["permit"]["plan"]["id"],
                "receivedAtMs": 1000,
                "sendStartedAtMs": 1000,
                "sendReturnedAtMs": 1000,
                "permitUntilMs": 1150,
            },
        )
        self.assertEqual(frames[0]["id"], 0)
        self.assertNotIn("diagnostic", frames[0]["data"])
        self.assertNotIn("text", diagnostic)

    def test_native_publication_does_not_write_a_diagnostic_stream(self):
        with (
            patch.dict(os.environ, {"TYPELAY_TRACE": "1"}),
            patch("builtins.print", side_effect=AssertionError("publication must not log")),
        ):
            frames = asyncio.run(self.publish_acceptance(pause=False))
        self.assertEqual([frame["op"] for frame in frames], ["publish"])
        self.assertEqual(frames[0]["id"], 0)
        self.assertIsNotNone(frames[0]["data"]["permit"])

    def test_native_publication_metadata_is_bounded_and_status_returns_copies(self):
        with patch.dict(os.environ, {"TYPELAY_TRACE": "1"}):
            for _ in range(193):
                frames = asyncio.run(self.publish_acceptance(pause=False))
                self.assertIsNotNone(frames[0]["data"]["permit"])
        records = self.input.diagnostics()["nativePublications"]
        self.assertEqual(len(records), 192)
        first = dict(records[0])
        records[0]["planId"] = "changed by caller"
        records.clear()
        self.assertEqual(self.input.diagnostics()["nativePublications"][0], first)
        self.assertEqual(len(self.input.publish_diagnostics), 192)

    def test_cancelled_explicit_command_reaches_host_without_reading_source(self):
        command = self.command()
        self.peer.event("shortcut-rejected", {**command, "reason": "ImeUnclear"})
        self.assertEqual(len(self.host.notices), 1)
        notice = self.host.notices[0]
        self.assertEqual(notice["kind"], "wake")
        self.assertEqual(notice["rejection"], "ImeUnclear")
        self.assertEqual(notice["commandGeneration"], self.generation)
        self.assertEqual(notice["commandSequence"], 1)
        self.assertNotIn("source", notice)

    def test_tab_trace_retains_two_inflight_rounds_after_ring_eviction(self):
        peer = self.connect_peer("browser")
        rounds = [{"round": index, "startedMs": 900 + index} for index in range(1, 17)]
        self.input.browser_rounds[:] = rounds
        self.input.browser_round_owners = {id(item): peer for item in rounds}
        output = io.StringIO()
        with patch.dict(os.environ, {"TYPELAY_TRACE": "1"}), contextlib.redirect_stderr(output):
            peer.event(
                "tab-diagnostic",
                {
                    "reason": "StaleObservation",
                    "remainingMs": -5,
                    "publicationRound": 14,
                    "publication": True,
                    "source": "must not be logged",
                },
            )
        event = json.loads(output.getvalue().removeprefix("typelay-tab "))
        self.assertEqual(event["publicationRound"], 14)
        self.assertEqual(event["publicationTiming"], rounds[13])
        self.assertNotIn("source", event)
        rounds[-1]["capturedMs"] = 1020
        self.input.browser_rounds[:] = [{"round": 100}]
        retained = self.input.diagnostics()["tabCaptureRounds"]
        self.assertEqual([value["round"] for value in retained], [15, 16])
        self.assertEqual(retained[-1]["capturedMs"], 1020)
        with (
            patch.dict(os.environ, {"TYPELAY_TRACE": "1"}),
            contextlib.redirect_stderr(io.StringIO()),
        ):
            self.peer.backend = "browser"
            self.peer.event(
                "tab-diagnostic", {"reason": "StaleObservation", "publicationRound": 100}
            )
        self.assertEqual(self.input.diagnostics()["tabCaptureRounds"], [])

    def test_current_cli_without_an_approved_field_reports_a_source_free_refusal(self):
        self.focus(self.peer)
        self.clock.return_value = 1050
        accepted = asyncio.run(
            self.input.trigger("wake", {"windowId": self.peer.pid, "triggeredAtMs": 1040})
        )
        self.assertFalse(accepted)
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["rejection"], "Unsupported")
        self.assertEqual(self.host.notices[0]["kind"], "wake")
        self.assertEqual(self.host.notices[0]["commandGeneration"], self.generation)
        self.assertEqual(self.host.notices[0]["commandSequence"], 1)
        self.assertNotIn("source", self.host.notices[0])

    def test_gtk_refusals_share_ordering_and_discard_duplicate_results(self):
        for index, backend in enumerate(("gtk3", "gtk4"), start=1):
            with self.subTest(backend=backend):
                peer = self.connect_peer(backend)
                command = self.command(peer=peer, kind="direct")
                refusal = {**command, "reason": "ImeUnclear"}
                peer.event("shortcut-rejected", refusal)
                peer.event("shortcut-rejected", refusal)
                self.assertEqual(len(self.host.notices), index)

                notice = self.host.notices[-1]
                self.assertEqual(notice["commandSequence"], index)
                self.assertEqual(notice["rejection"], "ImeUnclear")
                self.assertNotIn("source", notice)
                # Once this peer speaks the ordered protocol, a result without
                # the start identity cannot resurrect an older command.
                peer.event(
                    "shortcut",
                    {
                        "kind": "direct",
                        "generation": self.generation,
                        "acceptedAtMs": self.stamp + 1,
                    },
                )
                self.assertEqual(len(self.host.notices), index)

    def test_disabled_shortcuts_send_no_publication_and_reenable_requires_current_generation(self):
        self.focus(self.peer)
        native = {
            "target": "field",
            "text": "ordinary test text",
            "selection": {"start": 0, "end": 0},
            "metadata": {"multiline": False},
            "ime": "Clear",
            "writeSupported": True,
            "observedAtMs": 1000,
            "authority": {"inputRevision": 1, "focusRevision": 1},
        }
        source, _ = self.input.remember(
            self.peer,
            native,
            {"id": self.peer.pid},
            {"bounds": {"x": 0, "y": 0, "width": 20, "height": 10}},
        )
        self.native_frames(self.peer)
        publication = {"lease": {"source": source}, "generation": self.generation}
        self.input.notification("publishShortcut", publication)
        frames = self.native_frames(self.peer)
        self.assertEqual([frame["op"] for frame in frames], ["publish-shortcut"])
        self.assertEqual(frames[0]["data"]["generation"], self.generation)
        self.assertIsNotNone(self.peer.shortcut)

        self.generation = str(uuid.uuid4())
        self.input.notification(
            "shortcuts",
            {"enabled": False, "wake": "F9", "direct": "F10", "generation": self.generation},
        )
        frames = self.native_frames(self.peer)
        self.assertEqual([frame["op"] for frame in frames], ["revoke", "configure-shortcuts"])
        self.assertFalse(frames[-1]["data"]["enabled"])
        self.assertIsNone(self.peer.shortcut)
        self.input.notification("publishShortcut", publication)
        self.input.notification("publishShortcut", {**publication, "generation": self.generation})
        self.assertEqual(self.native_frames(self.peer), [])
        self.assertIsNone(self.peer.shortcut)

        disabled_generation = self.generation
        self.generation = str(uuid.uuid4())
        self.configure()
        self.native_frames(self.peer)
        self.input.notification(
            "publishShortcut", {**publication, "generation": disabled_generation}
        )
        self.assertEqual(self.native_frames(self.peer), [])
        self.input.notification("publishShortcut", {**publication, "generation": self.generation})
        frames = self.native_frames(self.peer)
        self.assertEqual([frame["op"] for frame in frames], ["publish-shortcut"])
        self.assertEqual(frames[0]["data"]["generation"], self.generation)

    def test_current_cli_without_a_connected_peer_reports_one_refusal(self):
        self.focus(self.peer)
        self.peer.link.close()
        self.clock.return_value = 1050
        request = {"windowId": self.peer.pid, "triggeredAtMs": 1040}
        self.assertFalse(asyncio.run(self.input.trigger("direct", request)))
        self.assertFalse(asyncio.run(self.input.trigger("direct", request)))
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["kind"], "direct")
        self.assertEqual(self.host.notices[0]["rejection"], "Unsupported")
        self.assertNotIn("source", self.host.notices[0])

    def test_invalid_disabled_or_unproven_cli_does_not_invent_a_command(self):
        self.focus(self.peer)
        self.clock.return_value = 1050
        valid = {"windowId": self.peer.pid, "triggeredAtMs": 1040}
        requests = [
            ("wake", None),
            ("other", valid),
            ("wake", {**valid, "source": "untrusted"}),
            ("wake", {**valid, "windowId": True}),
            ("wake", {**valid, "windowId": self.peer.pid + 1}),
            ("wake", {**valid, "triggeredAtMs": True}),
            ("wake", {**valid, "triggeredAtMs": 1051}),
            ("wake", {**valid, "triggeredAtMs": 899}),
            # Without a generation or sub-millisecond time, ordering at the
            # configuration boundary is ambiguous and must remain a refusal.
            ("wake", {**valid, "triggeredAtMs": 1000}),
        ]
        for kind, request in requests:
            with self.subTest(kind=kind, request=request):
                self.assertFalse(asyncio.run(self.input.trigger(kind, request)))
        self.clock.return_value = 1200  # The compositor observation is now stale.
        self.assertFalse(asyncio.run(self.input.trigger("wake", {**valid, "triggeredAtMs": 1190})))
        self.clock.return_value = 1050
        self.input.notification(
            "shortcuts",
            {"enabled": False, "wake": "F9", "direct": "F10", "generation": self.generation},
        )
        self.assertFalse(asyncio.run(self.input.trigger("wake", valid)))
        self.assertEqual(self.host.notices, [])
        self.assertEqual(self.native_requests(self.peer), [])

    def test_old_cli_cannot_retire_a_newer_native_command(self):
        self.clock.return_value = 1050
        current = self.command(stamp=1045)
        for stamp in (1040, 1045):
            self.assertFalse(
                asyncio.run(
                    self.input.trigger(
                        "direct", {"windowId": self.peer.pid, "triggeredAtMs": stamp}
                    )
                )
            )
        self.peer.event("shortcut-rejected", {**current, "reason": "ImeUnclear"})
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["rejection"], "ImeUnclear")
        self.assertEqual(self.host.notices[0]["commandSequence"], 1)
        self.assertEqual(self.native_requests(self.peer), [])

    def test_cli_from_before_reconfiguration_cannot_become_a_current_command(self):
        self.focus(self.peer)
        self.clock.return_value = 1045
        self.generation = str(uuid.uuid4())
        self.configure()
        self.clock.return_value = 1050
        for stamp in (1040, 1045):
            self.assertFalse(
                asyncio.run(
                    self.input.trigger("wake", {"windowId": self.peer.pid, "triggeredAtMs": stamp})
                )
            )
        self.assertEqual(self.host.notices, [])

    def test_forwarded_qt_cli_keeps_its_original_time_and_emits_only_its_native_result(self):
        self.clock.return_value = 1050
        old = self.command(stamp=1020)
        self.peer.shortcut = {
            "target": {"value": "field"},
            "window": {"id": self.peer.pid},
            "authority": {"inputRevision": 1, "focusRevision": 1},
        }
        request = {"windowId": self.peer.pid, "triggeredAtMs": 1040}

        async def trigger():
            task = asyncio.create_task(self.input.trigger("direct", request))
            await asyncio.sleep(0)  # Let the real Link enqueue its native request.
            frames = self.native_requests(self.peer)
            self.assertEqual(len(frames), 1)
            self.assertEqual(frames[0]["data"]["triggeredAtMs"], 1040)
            self.assertEqual(frames[0]["data"]["generation"], self.generation)
            self.assertEqual(self.host.notices, [])
            self.assertFalse(await self.input.trigger("direct", request))
            self.assertEqual(self.native_requests(self.peer), [])
            self.peer.event("shortcut-rejected", {**old, "reason": "SourceChanged"})
            self.assertEqual(self.host.notices, [])
            command = self.command(command_id=2, kind="direct", stamp=1040)
            self.peer.link.receive(
                {
                    "event": "shortcut",
                    "data": {
                        **command,
                        "acceptedAtMs": 1050,
                        "native": {
                            "target": "field",
                            "text": "ordinary test text",
                            "selection": {"start": 0, "end": 0},
                            "metadata": {"multiline": False},
                            "ime": "Clear",
                            "writeSupported": True,
                            "observedAtMs": 1050,
                            "authority": {"inputRevision": 1, "focusRevision": 1},
                            "geometry": {
                                "space": "qt-toplevel",
                                "bounds": {"x": 0, "y": 0, "width": 20, "height": 10},
                                "frameWidth": 100,
                                "frameHeight": 100,
                                "scale": 1,
                            },
                        },
                    },
                }
            )
            self.peer.link.receive({"id": frames[0]["id"], "data": {"triggered": True}})
            self.assertTrue(await asyncio.wait_for(task, timeout=1))

        asyncio.run(trigger())
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["commandSequence"], 2)
        self.assertEqual(self.host.notices[0]["source"]["text"], "ordinary test text")
        self.assertNotIn("rejection", self.host.notices[0])

    def test_another_peer_cannot_claim_the_reserved_cli_millisecond(self):
        other = self.connect_peer()
        self.focus(self.peer)
        self.clock.return_value = 1050
        self.peer.shortcut = {
            "target": {"value": "field"},
            "window": {"id": self.peer.pid},
            "authority": {"inputRevision": 1, "focusRevision": 1},
        }

        async def trigger():
            task = asyncio.create_task(
                self.input.trigger("wake", {"windowId": self.peer.pid, "triggeredAtMs": 1040})
            )
            await asyncio.sleep(0)
            frames = self.native_requests(self.peer)
            self.assertEqual(len(frames), 1)
            command = self.command(peer=other, stamp=1040)
            other.event("shortcut-rejected", {**command, "reason": "SourceChanged"})
            self.peer.link.receive({"id": frames[0]["id"], "data": {"triggered": False}})
            self.assertFalse(await asyncio.wait_for(task, timeout=1))

        asyncio.run(trigger())
        self.assertEqual(self.host.notices, [])

    def test_late_cancel_cannot_replace_a_command_that_has_already_started(self):
        old = self.command(command_id=1)
        current = self.command(command_id=2, kind="direct")
        self.peer.event("shortcut-rejected", {**old, "reason": "SourceChanged"})
        self.assertEqual(self.host.notices, [])
        self.peer.event("shortcut-rejected", {**current, "reason": "ImeUnclear"})
        self.assertEqual([notice["commandSequence"] for notice in self.host.notices], [2])
        self.assertEqual(self.host.notices[0]["kind"], "direct")

    def test_duplicate_or_older_start_and_terminal_do_not_rewrite_feedback(self):
        current = self.command(command_id=4)
        self.peer.event("shortcut-rejected", {**current, "reason": "ImeUnclear"})
        self.command(command_id=3)
        self.command(command_id=4)
        self.peer.event("shortcut-rejected", {**current, "reason": "SourceChanged"})
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["rejection"], "ImeUnclear")

    def test_sequences_are_shared_across_peers_and_cancellation_keeps_its_owner(self):
        old = self.command()
        other = self.connect_peer()
        current = self.command(peer=other, kind="direct")
        self.peer.event("shortcut-rejected", {**old, "reason": "NotSourceFocus"})
        other.event("shortcut-rejected", {**current, "reason": "ReadOnly"})
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["commandSequence"], 2)
        self.assertEqual(self.host.notices[0]["rejection"], "ReadOnly")

    def test_configuration_change_retires_old_commands_and_restarts_sequence(self):
        old = self.command(command_id=30)
        self.generation = str(uuid.uuid4())
        self.configure()
        self.peer.event("shortcut-rejected", {**old, "reason": "SourceChanged"})
        current = self.command()
        self.peer.event("shortcut-rejected", {**current, "reason": "StaleObservation"})
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["commandGeneration"], self.generation)
        self.assertEqual(self.host.notices[0]["commandSequence"], 1)

    def test_disconnected_peer_cannot_report_a_late_command(self):
        old = self.command()
        self.peer.link.close()
        other = self.connect_peer()
        current = self.command(peer=other, kind="direct")
        other.event("shortcut-rejected", {**current, "reason": "ImeUnclear"})
        self.command(command_id=100)
        self.peer.event("shortcut-rejected", {**old, "reason": "SourceChanged"})
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["commandSequence"], 2)

    def test_rejection_never_forwards_peer_text_or_a_source_body(self):
        command = self.command()
        self.peer.event(
            "shortcut-rejected",
            {
                **command,
                "reason": "untrusted free text",
                "source": {"text": "private"},
                "native": {"text": "private"},
            },
        )
        self.assertEqual(
            self.host.notices,
            [
                {
                    "kind": "wake",
                    "commandGeneration": self.generation,
                    "commandSequence": 1,
                    "userEpoch": self.input.user_epoch,
                    "foregroundEpoch": self.input.foreground_epoch,
                    "rejection": "Unsupported",
                }
            ],
        )

    def test_rejection_needs_a_matching_real_start(self):
        for command_id in (0, -1, True, "1"):
            command = self.command(command_id=command_id)
            self.peer.event("shortcut-rejected", {**command, "reason": "ImeUnclear"})
        self.peer.event(
            "shortcut-rejected",
            {"kind": "wake", "generation": self.generation, "reason": "ImeUnclear"},
        )
        current = self.command()
        self.peer.event("shortcut-rejected", {**current, "kind": "direct", "reason": "ImeUnclear"})
        self.assertEqual(self.host.notices, [])
        self.peer.event("shortcut-rejected", {**current, "reason": "ImeUnclear"})
        self.assertEqual(len(self.host.notices), 1)

    def test_disabled_shortcuts_and_ordinary_input_stay_quiet(self):
        self.input.notification(
            "shortcuts",
            {"enabled": False, "wake": "F9", "direct": "F10", "generation": self.generation},
        )
        command = self.command()
        self.peer.event("shortcut-rejected", {**command, "reason": "ImeUnclear"})
        self.peer.event(
            "input",
            {"target": "field", "textChanged": True, "focusChanged": False, "focused": True},
        )
        self.assertEqual(self.host.notices, [])

    def test_legacy_qt_late_result_cannot_replace_a_newer_started_command(self):
        other = self.connect_peer()
        current = self.command(peer=other, kind="direct")
        self.peer.event("shortcut", {"kind": "wake", "generation": self.generation, "native": None})
        self.assertEqual(self.host.notices, [])
        other.event("shortcut-rejected", {**current, "reason": "ImeUnclear"})
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["commandSequence"], 1)

    def test_synchronous_gtk_command_uses_the_same_monitor_sequence(self):
        command = self.command()
        self.peer.event("shortcut-rejected", {**command, "reason": "ImeUnclear"})
        gtk = self.connect_peer("gtk3")
        self.focus(gtk)
        # This is the source approval record previously published to the native
        # adapter; the terminal event carries the captured synthetic test body.
        gtk.shortcut = {"target": {"value": "field"}, "window": {"id": 1}}
        gtk.event(
            "shortcut",
            {
                "kind": "wake",
                "generation": self.generation,
                "acceptedAtMs": 123,
                "native": {
                    "target": "field",
                    "text": "ordinary test text",
                    "selection": {"start": 0, "length": 0},
                    "metadata": {"multiline": False},
                    "ime": "Clear",
                    "writeSupported": True,
                    "observedAtMs": 123,
                    "authority": {"inputRevision": 1, "focusRevision": 1},
                    "geometry": {
                        "space": "gtk-toplevel",
                        "bounds": {"x": 0, "y": 0, "width": 20, "height": 10},
                        "frameWidth": 100,
                        "frameHeight": 100,
                        "scale": 1,
                    },
                },
            },
        )
        self.assertEqual([notice["commandSequence"] for notice in self.host.notices], [1, 2])
        self.assertEqual(self.host.notices[1]["source"]["text"], "ordinary test text")
        self.assertNotIn("rejection", self.host.notices[1])
        self.peer.event(
            "shortcut",
            {"kind": "wake", "generation": self.generation, "acceptedAtMs": 124, "native": None},
        )
        self.assertEqual(len(self.host.notices), 2)

    def test_qt_result_stays_bound_to_the_approval_at_command_start(self):
        self.focus(self.peer)
        self.peer.shortcut = {"target": {"value": "original"}, "window": {"id": 1}}
        command = self.command()
        self.peer.shortcut = {"target": {"value": "newer"}, "window": {"id": 2}}
        self.peer.event(
            "shortcut",
            {
                **command,
                "acceptedAtMs": 200,
                "native": {
                    "target": "original",
                    "text": "original test text",
                    "selection": {"start": 0, "length": 0},
                    "metadata": {"multiline": False},
                    "ime": "Clear",
                    "writeSupported": True,
                    "observedAtMs": 200,
                    "authority": {"inputRevision": 1, "focusRevision": 1},
                    "geometry": {
                        "space": "qt-toplevel",
                        "bounds": {"x": 0, "y": 0, "width": 20, "height": 10},
                        "frameWidth": 100,
                        "frameHeight": 100,
                        "scale": 1,
                    },
                },
            },
        )
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["source"]["text"], "original test text")
        self.assertEqual(self.host.notices[0]["source"]["target"]["value"], "original")
        self.assertEqual(self.host.notices[0]["commandSequence"], 1)

    def test_background_start_cannot_cancel_the_current_foreground_command(self):
        other = self.connect_peer()
        current = self.command(peer=other)
        old = self.command(command_id=100, focus=False)
        self.peer.event("shortcut-rejected", {**old, "reason": "SourceChanged"})
        other.event("shortcut-rejected", {**current, "reason": "ImeUnclear"})
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["commandSequence"], 1)
        self.assertEqual(self.host.notices[0]["rejection"], "ImeUnclear")

    def test_delayed_start_is_not_new_merely_because_its_packet_arrived_last(self):
        other = self.connect_peer()
        current = self.command(peer=other, stamp=300)
        for stamp in (299, 300):
            with self.subTest(stamp=stamp):
                old = self.command(command_id=stamp, stamp=stamp)
                self.peer.event("shortcut-rejected", {**old, "reason": "SourceChanged"})
        other.event("shortcut-rejected", {**current, "reason": "ImeUnclear"})
        self.assertEqual(len(self.host.notices), 1)
        self.assertEqual(self.host.notices[0]["commandSequence"], 1)
        self.assertEqual(self.host.notices[0]["rejection"], "ImeUnclear")


if __name__ == "__main__":
    unittest.main()
