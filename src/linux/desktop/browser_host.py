#!/usr/bin/env python3
"""Browser-native-messaging bridge to the same-session TypeLay socket.

Browser startup owns this process and its pipes. It never starts the desktop,
opens a network listener, reads page data from disk or logs message contents.
The desktop independently authenticates our same-user browser ancestry.
"""

import os
import selectors
import sys
import time
import uuid
from collections import deque
from pathlib import Path

from paths import Paths, connect_private
from wire import MAX_FRAME, Decoder, encode, now_ms


def main():
    connection = connect_private(Paths(home=Path(__file__).resolve().parent.parent).native)
    connection.setblocking(False)
    input_fd, output_fd = sys.stdin.fileno(), sys.stdout.fileno()
    os.set_blocking(input_fd, False)
    os.set_blocking(output_fd, False)
    native_fd = connection.fileno()
    queues = {native_fd: deque(), output_fd: deque()}
    sizes = {native_fd: 0, output_fd: 0}
    decoders = {input_fd: Decoder(), native_fd: Decoder()}
    with connection, selectors.DefaultSelector() as selector:
        selector.register(input_fd, selectors.EVENT_READ)
        selector.register(native_fd, selectors.EVENT_READ)

        def queue(fd, value):
            packet = encode(value)
            if sizes[fd] + len(packet) > 4 * MAX_FRAME:
                raise OSError("browser-backpressure")
            queues[fd].append(memoryview(packet))
            sizes[fd] += len(packet)
            if fd == native_fd:
                selector.modify(fd, selectors.EVENT_READ | selectors.EVENT_WRITE)
            elif len(queues[fd]) == 1:
                selector.register(fd, selectors.EVENT_WRITE)

        queue(
            native_fd,
            {
                "event": "hello",
                "data": {
                    "protocol": 2,
                    "backend": "browser",
                    "pid": os.getpid(),
                    "lifetime": str(uuid.uuid4()),
                },
            },
        )
        while True:
            for key, flags in selector.select():
                fd = key.fd
                if flags & selectors.EVENT_READ:
                    packet = os.read(fd, 8192)
                    if not packet:
                        return
                    for message in decoders[fd].feed(packet):
                        if fd == native_fd:
                            message["clock"] = {
                                "boottimeMs": now_ms(),
                                "wallTimeMs": time.time_ns() // 1_000_000,
                            }
                            queue(output_fd, message)
                        else:
                            # A webpage cannot choose peer identity or reset its lifetime.
                            if message.get("event") == "hello":
                                raise ValueError("browser-handshake")
                            queue(native_fd, message)
                if flags & selectors.EVENT_WRITE:
                    queue_items = queues[fd]
                    while queue_items:
                        try:
                            count = os.write(fd, queue_items[0])
                        except BlockingIOError:
                            break
                        if not count:
                            return
                        sizes[fd] -= count
                        if count == len(queue_items[0]):
                            queue_items.popleft()
                        else:
                            queue_items[0] = queue_items[0][count:]
                    if not queue_items:
                        if fd == native_fd:
                            selector.modify(fd, selectors.EVENT_READ)
                        else:
                            selector.unregister(fd)


if __name__ == "__main__":
    try:
        main()
    except (OSError, EOFError, ValueError, KeyError, TypeError):
        # Native messaging stdout is a protocol channel, never diagnostics.
        sys.exit(1)
