"""Explicit native command feedback at the production desktop input boundary.

The compositor/listening socket are controlled system ports. These tests do not
claim native key interception, an IME, or a real desktop interaction.
"""

import asyncio
import contextlib
import io
import json
import os
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
from input import Input, Peer  # noqa: E402
from wire import Decoder  # noqa: E402


class EventLoop:
    IO_IN, IO_OUT, IO_HUP, IO_ERR, IO_NVAL = 1, 2, 4, 8, 16

    def __init__(self):
        self.next_id = 0
        self.watches = {}

    def register(self, *_):
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

    def send(self, operation, data):
        self.events.append((operation, data))

    async def ask(self, operation, data):
        self.requests.append((operation, data))
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

    def configure(self):
        self.input.notification(
            "shortcuts",
            {"enabled": True, "wake": "F9", "direct": "F10", "generation": self.generation},
        )

    def connect_peer(self, backend="qt6"):
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
            {"protocol": 2, "pid": process.pid, "backend": backend, "lifetime": str(uuid.uuid4())},
        )
        return peer

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

    async def publish_acceptance(self):
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
        self.input.notification(
            "publish",
            {
                "lease": {
                    "source": capture["snapshot"],
                    "userEpoch": capture["userEpoch"],
                    "foregroundEpoch": capture["foregroundEpoch"],
                    "escapeAllowed": True,
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
        self.assertEqual([frame["op"] for frame in self.native_frames(self.peer)], ["publish"])
        self.input.notification("settings", {"enabled": False})
        self.assertEqual([frame["op"] for frame in self.native_frames(self.peer)], ["revoke"])
        return plan_id

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
