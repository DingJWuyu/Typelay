"""Bounded length-prefix transport on existing local pipes and sockets."""

import json
import os
import socket
import struct
import time
from collections import deque

MAX_FRAME = 256 * 1024


def now_ms() -> int:
    return time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1_000_000


def encode(value: dict) -> bytes:
    body = json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode(
        "utf-8"
    )
    if not 0 < len(body) <= MAX_FRAME:
        raise ValueError("frame-size")
    return struct.pack("<I", len(body)) + body


class Decoder:
    def __init__(self):
        self.buffer = bytearray()

    def feed(self, data: bytes) -> list[dict]:
        self.buffer.extend(data)
        if len(self.buffer) > 2 * MAX_FRAME:
            raise ValueError("frame-buffer-size")
        frames = []
        while len(self.buffer) >= 4:
            length = struct.unpack_from("<I", self.buffer)[0]
            if not 0 < length <= MAX_FRAME:
                raise ValueError("frame-size")
            if len(self.buffer) < length + 4:
                break
            value = json.loads(bytes(self.buffer[4 : length + 4]).decode("utf-8"))
            del self.buffer[: length + 4]
            if not isinstance(value, dict):
                raise ValueError("frame-type")
            frames.append(value)
        return frames


def receive_sync(connection: socket.socket) -> dict:
    def exact(length: int) -> bytes:
        data = bytearray()
        while len(data) < length:
            part = connection.recv(length - len(data))
            if not part:
                raise EOFError("connection-closed")
            data.extend(part)
        return bytes(data)

    length = struct.unpack("<I", exact(4))[0]
    if not 0 < length <= MAX_FRAME:
        raise ValueError("frame-size")
    value = json.loads(exact(length).decode("utf-8"))
    if not isinstance(value, dict):
        raise ValueError("frame-type")
    return value


class Channel:
    """Nonblocking GLib stream, bounded in both directions, one GUI-thread owner."""

    def __init__(self, glib, read_fd, write_fd, received, closed, owner=None):
        self.glib = glib
        self.read_fd = read_fd
        self.write_fd = write_fd
        self.received = received
        self.closed = closed
        self.owner = owner
        self.decoder = Decoder()
        self.queue = deque()
        self.queued = 0
        self.alive = True
        self.output_watch = 0
        os.set_blocking(read_fd, False)
        os.set_blocking(write_fd, False)
        self.input_watch = glib.io_add_watch(
            read_fd, glib.IO_IN | glib.IO_HUP | glib.IO_ERR | glib.IO_NVAL, self._read
        )

    def send(self, value: dict) -> None:
        if not self.alive:
            raise EOFError("connection-closed")
        data = encode(value)
        if self.queued + len(data) > 4 * MAX_FRAME:
            self.close()
            raise OSError("connection-backpressure")
        self.queue.append(memoryview(data))
        self.queued += len(data)
        if not self.output_watch:
            # A writable nonblocking pipe needs no extra GLib dispatch. Keep the
            # existing close notification on failure; send is not a delivery ACK.
            if not self._write(self.write_fd, 0):
                return
            self.output_watch = self.glib.io_add_watch(
                self.write_fd,
                self.glib.IO_OUT | self.glib.IO_HUP | self.glib.IO_ERR | self.glib.IO_NVAL,
                self._write,
            )

    def _read(self, _fd, condition):
        if condition & (self.glib.IO_ERR | self.glib.IO_NVAL):
            self.input_watch = 0
            self.close()
            return False
        try:
            while self.alive:
                try:
                    data = os.read(self.read_fd, 8192)
                except BlockingIOError:
                    break
                if not data:
                    self.input_watch = 0
                    self.close()
                    return False
                for value in self.decoder.feed(data):
                    self.received(value)
        except (OSError, ValueError, KeyError, TypeError, UnicodeError):
            self.input_watch = 0
            self.close()
            return False
        return self.alive

    def _write(self, _fd, condition):
        if condition & (self.glib.IO_HUP | self.glib.IO_ERR | self.glib.IO_NVAL):
            self.output_watch = 0
            self.close()
            return False
        try:
            while self.queue:
                try:
                    count = os.write(self.write_fd, self.queue[0])
                except BlockingIOError:
                    return True
                if count == 0:
                    raise EOFError("connection-closed")
                self.queued -= count
                if count == len(self.queue[0]):
                    self.queue.popleft()
                else:
                    self.queue[0] = self.queue[0][count:]
        except OSError:
            self.output_watch = 0
            self.close()
            return False
        self.output_watch = 0
        return False

    def close(self):
        if not self.alive:
            return
        self.alive = False
        for watch in (self.input_watch, self.output_watch):
            if watch:
                self.glib.source_remove(watch)
        self.input_watch = self.output_watch = 0
        self.queue.clear()
        self.queued = 0
        self.decoder.buffer.clear()
        if self.owner is not None:
            self.owner.close()
        self.closed()
