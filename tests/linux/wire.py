"""Production nonblocking transport over real pipes and headless GLib sources."""

import fcntl
import os
import sys
import unittest
from pathlib import Path

from gi.repository import GLib

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from wire import MAX_FRAME, Channel, Decoder, encode  # noqa: E402


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

    def test_writable_pipe_receives_frame_without_event_dispatch(self):
        message = {"event": "publish", "data": {"generation": 7}}
        self.channel.send(message)
        self.assertEqual(os.read(self.output_read, MAX_FRAME), encode(message))
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
