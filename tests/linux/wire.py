"""Production nonblocking transport over real pipes and headless GLib sources."""

import asyncio
import fcntl
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import warnings
from pathlib import Path
from unittest.mock import patch

from gi.repository import GLib

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from link import Link  # noqa: E402
from niri import Niri  # noqa: E402
from paths import Paths  # noqa: E402
from wire import MAX_FRAME, Channel, Decoder, encode, now_ms, receive_sync  # noqa: E402


class CompositorDispatchTests(unittest.TestCase):
    def test_completed_snapshot_is_applied_while_default_io_remains_ready(self):
        # Exercise the production worker and GLib delivery, without a compositor
        # or any GTK objects. A ready pipe represents sustained native input.
        completed = threading.Event()
        replies, applied, io_dispatches, timer_dispatches = [], [], [], []
        original_stamp = now_ms() - 200

        def reply(_commands):
            replies.append(time.monotonic())
            completed.set()
            return [
                {"Windows": [{"id": 1, "pid": 123, "is_focused": True}]},
                {"Outputs": {}},
                {"Workspaces": []},
                {"OverviewState": {"is_open": False}},
            ]

        read_fd, write_fd = os.pipe()
        os.write(write_fd, b"input")
        watch = GLib.io_add_watch(
            read_fd,
            GLib.PRIORITY_DEFAULT,
            GLib.IO_IN,
            lambda *_: (io_dispatches.append(time.monotonic()), True)[1],
        )
        timer = GLib.timeout_add(10, lambda: (timer_dispatches.append(time.monotonic()), True)[1])
        context = GLib.MainContext.default()
        with (
            warnings.catch_warnings(),
            patch("niri.request", return_value={"Version": "test"}),
            patch("niri.request_many", side_effect=reply),
            # Advance the clock after the first request. Applying its snapshot
            # must not turn that old observation into a fresh one.
            patch(
                "niri.now_ms",
                side_effect=lambda: now_ms() if completed.is_set() else original_stamp,
            ),
        ):
            # PyGObject repeatedly enters catch_warnings while querying this
            # deprecated policy, invalidating the default once-per-site cache.
            warnings.filterwarnings(
                "ignore",
                message=r"^'asyncio\.get_event_loop_policy' is deprecated and slated for removal in Python 3\.16$",
                category=DeprecationWarning,
                module=r"^gi\.events$",
            )
            observer = Niri(GLib, lambda: applied.append((time.monotonic(), observer.observed_at)))
            try:
                self.assertTrue(completed.wait(2), "compositor request did not complete")
                deadline = time.monotonic() + 0.2
                while time.monotonic() < deadline:
                    context.iteration(False)
                delivered_while_busy = observer.ready
                observation_age = now_ms() - observer.observed_at
                GLib.source_remove(watch)
                watch = 0
                # Also record the old implementation's delivery after IO stops.
                deadline = time.monotonic() + 2
                while not applied and time.monotonic() < deadline:
                    context.iteration(False)
                self.assertTrue(applied, "completed compositor snapshot was never applied")
                applied_at, observed_at = applied[0]
                delay = (applied_at - replies[0]) * 1000
                print(f"compositor reply-to-apply: {delay:.1f} ms", file=sys.stderr)
                self.assertTrue(
                    delivered_while_busy,
                    f"snapshot starved behind ready IO for {delay:.1f} ms",
                )
                self.assertEqual(observed_at, original_stamp, "delivery refreshed observation")
                self.assertLessEqual(observation_age, 150)
                self.assertTrue(any(stamp > applied_at for stamp in io_dispatches))
                self.assertTrue(any(stamp > applied_at for stamp in timer_dispatches))
            finally:
                observer.close()
                if watch:
                    GLib.source_remove(watch)
                GLib.source_remove(timer)
                os.close(read_fd)
                os.close(write_fd)
                while context.pending():
                    context.iteration(False)


class PipeTransportTests(unittest.TestCase):
    def setUp(self):
        self.input_read, self.input_write = os.pipe()
        self.output_read, self.output_write = os.pipe()
        self.descriptors = {self.input_read, self.input_write, self.output_read, self.output_write}
        self.addCleanup(self.close_descriptors)
        os.set_blocking(self.output_read, False)
        self.closed = 0
        self.channel = Channel(
            GLib, self.input_read, self.output_write, lambda _: None, self.on_closed
        )
        self.addCleanup(self.channel.close)

    def close_descriptors(self):
        for descriptor in self.descriptors:
            os.close(descriptor)

    def on_closed(self):
        self.closed += 1
        # Closing from the owner callback must be harmless, including when the
        # failure is detected synchronously inside send().
        self.channel.close()

    def test_capture_failure_trace_is_private_to_its_pending_null_reply(self):
        self.channel.close()
        link = Link(
            GLib,
            self.input_read,
            self.output_write,
            lambda *_: None,
            None,
            lambda: None,
            response_key="id",
        )
        self.addCleanup(link.close)
        trace = {"reason": "stamp-before", "ageBeforeMs": 4, "targetMatchesBefore": True}
        unsafe = {**trace, "text": "private", "target": "private", "beforeInputRevision": True}

        async def exercise():
            context = GLib.MainContext.default()

            async def begin(operation, diagnostics, timeout=1):
                task = asyncio.create_task(
                    link.ask(operation, {}, timeout=timeout, capture_trace=diagnostics)
                )
                await asyncio.sleep(0)
                request = Decoder().feed(os.read(self.output_read, MAX_FRAME))[0]
                return task, request["id"]

            def respond(identity, data, diagnostic):
                os.write(
                    self.input_write,
                    encode({"id": identity, "data": data, "captureTrace": diagnostic}),
                )
                context.iteration(False)

            for operation, data, diagnostic, expected in (
                ("capture", None, unsafe, trace),
                ("describe", None, unsafe, {}),
                ("capture", {"text": "normal result"}, unsafe, {}),
                ("capture", None, {"reason": "private exception"}, {}),
                (
                    "capture",
                    None,
                    {"reason": "stamp-before", "ageBeforeMs": 2**60},
                    {"reason": "stamp-before"},
                ),
            ):
                with self.subTest(operation=operation, data=data, diagnostic=diagnostic):
                    diagnostics = {}
                    task, identity = await begin(operation, diagnostics)
                    respond(identity, data, diagnostic)
                    self.assertEqual(await task, data)
                    self.assertEqual(diagnostics, expected)
                    self.assertTrue(link.channel.alive)

            diagnostics = {}
            task, identity = await begin("capture", diagnostics, timeout=0.001)
            with self.assertRaises(TimeoutError):
                await task
            respond(identity, None, trace)
            self.assertEqual(diagnostics, {}, "late trace changed a finished request")
            self.assertTrue(link.channel.alive)

        asyncio.run(exercise())

    def test_writable_pipe_receives_frame_without_event_dispatch(self):
        message = {"event": "publish", "data": {"generation": 7}}
        self.channel.send(message)
        self.assertEqual(os.read(self.output_read, MAX_FRAME), encode(message))
        self.assertEqual(self.closed, 0)

    def test_capture_reply_dispatch_stamps_local_time_without_changing_result(self):
        received = []
        self.channel.received = received.append
        frame = {
            "id": 7,
            "data": {
                "text": "synthetic source",
                "captureTrace": {
                    "round": 12,
                    "pageCaptureMs": 2.5,
                    "nativeReplyReceivedMs": 10,
                    "desktopReplyDispatchedMs": -1,
                },
            },
        }
        before = now_ms()
        os.write(self.input_write, encode(frame))
        loop = GLib.MainLoop()
        self.channel.received = lambda value: (received.append(value), loop.quit())
        timer = GLib.timeout_add(2000, lambda: (loop.quit(), True)[1])
        try:
            loop.run()
        finally:
            GLib.source_remove(timer)
        self.assertEqual(len(received), 1)
        result = received[0]["data"]
        stamp = result["captureTrace"].pop("desktopReplyDispatchedMs")
        self.assertLessEqual(before, stamp)
        self.assertLessEqual(stamp, now_ms())
        del frame["data"]["captureTrace"]["desktopReplyDispatchedMs"]
        self.assertEqual(result, frame["data"])

    def test_invalid_capture_trace_cannot_forward_claimed_timestamps(self):
        received = []
        traces = [
            {"round": 12, "pageCaptureMs": -1},
            {"round": True, "pageCaptureMs": 2},
            {"round": 12, "pageCaptureMs": "2"},
        ]
        for trace in traces:
            trace.update(nativeReplyReceivedMs=123, desktopReplyDispatchedMs=456)
            os.write(
                self.input_write,
                encode({"id": 7, "data": {"text": "test body", "captureTrace": trace}}),
            )
        loop = GLib.MainLoop()

        def receive(value):
            received.append(value)
            if len(received) == len(traces):
                loop.quit()

        self.channel.received = receive
        timer = GLib.timeout_add(2000, lambda: (loop.quit(), True)[1])
        try:
            loop.run()
        finally:
            GLib.source_remove(timer)
        self.assertEqual(received, [{"id": 7, "data": {"text": "test body"}}] * 3)

    def test_diagnostic_clock_failure_keeps_body_without_peer_dispatch_time(self):
        received = []
        os.write(
            self.input_write,
            encode(
                {
                    "id": 7,
                    "data": {
                        "text": "test body",
                        "captureTrace": {
                            "round": 12,
                            "pageCaptureMs": 2,
                            "nativeReplyReceivedMs": 10,
                            "desktopReplyDispatchedMs": 456,
                        },
                    },
                }
            ),
        )
        loop = GLib.MainLoop()
        self.channel.received = lambda value: (received.append(value), loop.quit())
        timer = GLib.timeout_add(2000, lambda: (loop.quit(), True)[1])
        try:
            with patch("wire.now_ms", side_effect=OSError("clock unavailable")):
                loop.run()
        finally:
            GLib.source_remove(timer)
        self.assertEqual(
            received,
            [
                {
                    "id": 7,
                    "data": {
                        "text": "test body",
                        "captureTrace": {
                            "round": 12,
                            "pageCaptureMs": 2,
                            "nativeReplyReceivedMs": 10,
                        },
                    },
                }
            ],
        )
        self.assertEqual(self.closed, 0)

    def test_partial_write_and_full_pipe_resume_in_fifo_order(self):
        capacity = fcntl.fcntl(self.output_write, fcntl.F_GETPIPE_SZ)
        first = {"event": "first", "data": "x" * (capacity * 2 + 97)}
        second = {"event": "second", "data": "tail"}
        self.channel.send(first)
        # The first frame exceeds the pipe capacity. The second send encounters
        # an already queued suffix and must neither overtake nor discard it.
        self.channel.send(second)
        decoder = Decoder()
        self.assertEqual(decoder.feed(os.read(self.output_read, capacity)), [])
        messages = []
        loop = GLib.MainLoop()

        def readable(descriptor, _condition):
            while True:
                try:
                    data = os.read(descriptor, MAX_FRAME)
                except BlockingIOError:
                    break
                if not data:
                    loop.quit()
                    return True
                messages.extend(decoder.feed(data))
            if len(messages) >= 2:
                loop.quit()
            return True

        def timeout():
            loop.quit()
            return True

        watch = GLib.io_add_watch(self.output_read, GLib.IO_IN, readable)
        timer = GLib.timeout_add(2000, timeout)
        try:
            # Only the headless transport sources run; no GTK application or
            # GUI pump is involved, and there are no timing sleeps or retries.
            loop.run()
        finally:
            GLib.source_remove(watch)
            GLib.source_remove(timer)
        self.assertEqual(messages, [first, second])
        self.assertEqual(self.closed, 0)

    def test_native_host_stamps_reply_and_drops_untrusted_trace_fields(self):
        with tempfile.TemporaryDirectory(prefix="typelay-wire-") as directory:
            root = Path(directory)
            environment = {
                **os.environ,
                "HOME": directory,
                "XDG_RUNTIME_DIR": directory,
                "XDG_CONFIG_HOME": str(root / "config"),
                "XDG_DATA_HOME": str(root / "data"),
                "XDG_CACHE_HOME": str(root / "cache"),
                "WAYLAND_DISPLAY": "wire-test",
                "NIRI_SOCKET": str(root / "niri"),
            }
            with patch.dict(os.environ, environment):
                paths = Paths(ROOT / "src/linux")
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
                listener.bind(str(paths.native))
                paths.native.chmod(0o600)
                listener.listen(1)
                listener.settimeout(3)
                process = subprocess.Popen(
                    [sys.executable, str(ROOT / "src/linux/desktop/browser_host.py")],
                    env=environment,
                    stdin=subprocess.PIPE,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                )
                try:
                    connection, _ = listener.accept()
                    with connection:
                        connection.settimeout(3)
                        self.assertEqual(receive_sync(connection)["event"], "hello")
                        before = now_ms()
                        process.stdin.write(
                            encode(
                                {
                                    "id": 7,
                                    "data": {
                                        "text": "test body",
                                        "captureTrace": {
                                            "round": 53,
                                            "pageCaptureMs": 2,
                                            "nativeReplyReceivedMs": -1,
                                            "desktopReplyDispatchedMs": -2,
                                            "untrusted": "not a diagnostic",
                                        },
                                    },
                                }
                            )
                        )
                        process.stdin.flush()
                        result = receive_sync(connection)["data"]
                        self.assertEqual(result["text"], "test body")
                        trace = result["captureTrace"]
                        self.assertEqual(
                            set(trace), {"round", "pageCaptureMs", "nativeReplyReceivedMs"}
                        )
                        self.assertLessEqual(before, trace["nativeReplyReceivedMs"])
                        self.assertLessEqual(trace["nativeReplyReceivedMs"], now_ms())
                        process.stdin.write(encode({"id": 8, "data": None}))
                        process.stdin.flush()
                        self.assertEqual(receive_sync(connection), {"id": 8, "data": None})
                        process.stdin.write(
                            encode(
                                {
                                    "id": 9,
                                    "data": {
                                        "text": "test body",
                                        "captureTrace": {
                                            "round": 53,
                                            "pageCaptureMs": -1,
                                            "nativeReplyReceivedMs": 123,
                                            "desktopReplyDispatchedMs": 456,
                                        },
                                    },
                                }
                            )
                        )
                        process.stdin.flush()
                        self.assertEqual(
                            receive_sync(connection), {"id": 9, "data": {"text": "test body"}}
                        )
                finally:
                    failed = sys.exc_info()[0] is not None
                    try:
                        process.terminate()
                        try:
                            process.wait(timeout=3)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait(timeout=3)
                    except (OSError, subprocess.TimeoutExpired):
                        if not failed:
                            raise
                    finally:
                        try:
                            process.stdin.close()
                        except OSError:
                            pass

    def test_backpressure_closes_once_and_rejects_further_sends(self):
        # Fill the actual nonblocking pipe before any channel frame is sent.
        while True:
            try:
                os.write(self.output_write, b"x" * 4096)
            except BlockingIOError:
                break
        message = {"data": "x" * (MAX_FRAME - 1024)}
        for _ in range(4):
            self.channel.send(message)
        with self.assertRaisesRegex(OSError, "connection-backpressure"):
            self.channel.send(message)
        self.assertEqual(self.closed, 1)
        self.channel.close()
        self.assertEqual(self.closed, 1)
        with self.assertRaises(EOFError):
            self.channel.send({"data": "late"})

    def test_broken_pipe_notifies_owner_once_during_send(self):
        os.close(self.output_read)
        self.descriptors.remove(self.output_read)
        # Admission is not a delivery promise: write failure notifies the owner
        # through the same close path as a later GLib IO_OUT callback.
        self.assertIsNone(self.channel.send({"data": "closed reader"}))
        self.assertEqual(self.closed, 1)
        self.channel.close()
        self.assertEqual(self.closed, 1)
        with self.assertRaises(EOFError):
            self.channel.send({"data": "late"})


if __name__ == "__main__":
    unittest.main()
