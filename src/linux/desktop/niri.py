"""niri session and foreground authority. Window origins are not inferred."""

import json
import os
import socket
import threading
import time
from pathlib import Path

from wire import now_ms


def request(command, timeout=0.25):
    return request_many((command,), timeout)[0]


def request_many(commands, timeout=0.25):
    """Pipeline newline requests on one niri connection, preserving reply order."""
    path = os.environ.get("NIRI_SOCKET", "")
    if not Path(path).is_absolute():
        raise OSError("niri-socket-unavailable")
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        deadline = time.monotonic() + timeout
        connection.settimeout(timeout)
        connection.connect(path)
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("niri-response-timeout")
        connection.settimeout(remaining)
        connection.sendall(
            "".join(
                json.dumps(command, separators=(",", ":")) + "\n" for command in commands
            ).encode()
        )
        data = bytearray()
        responses = []
        while len(responses) < len(commands):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("niri-response-timeout")
            connection.settimeout(remaining)
            part = connection.recv(16384)
            if not part:
                raise EOFError("niri-closed")
            data.extend(part)
            while b"\n" in data:
                line, _, rest = data.partition(b"\n")
                if len(line) > 2 * 1024 * 1024:
                    raise ValueError("niri-response-size")
                response = json.loads(line)
                if not isinstance(response, dict) or "Ok" not in response:
                    raise OSError("niri-operation-rejected")
                responses.append(response["Ok"])
                data = rest
            if len(data) > 2 * 1024 * 1024 or len(responses) > len(commands):
                raise ValueError("niri-response-size")
        return responses


def contents(response, name):
    if not isinstance(response, dict) or name not in response:
        raise ValueError("niri-response-type")
    return response[name]


class Niri:
    """One bounded worker for IPC; callbacks return to GTK, never mutate shared state."""

    def __init__(self, glib, changed):
        self.glib = glib
        self.changed = changed
        self.windows = []
        self.outputs = {}
        self.workspaces = []
        self.focused = None
        self.ready = False
        self.version = ""
        self.observed_at = 0
        self.stopping = threading.Event()
        self.thread = threading.Thread(target=self._run, name="typelay-niri", daemon=True)
        self.thread.start()

    def _deliver(self, snapshot):
        if self.stopping.is_set():
            return False
        old = (self.ready, self.focused, self.windows, self.outputs, self.workspaces)
        if snapshot is None:
            self.ready = False
            self.focused = None
            self.windows = []
        else:
            (
                self.version,
                self.windows,
                self.outputs,
                self.workspaces,
                overview,
                self.observed_at,
            ) = snapshot
            self.ready = not overview
            self.focused = next(
                (window for window in self.windows if window.get("is_focused")), None
            )
        if old != (self.ready, self.focused, self.windows, self.outputs, self.workspaces):
            self.changed()
        return False

    def _run(self):
        version = ""
        while not self.stopping.is_set():
            start = time.monotonic()
            try:
                if not version:
                    version = contents(request("Version"), "Version")
                # niri handles multiple newline requests per connection. Sending the
                # batch together avoids four connection/scheduling round trips. Keep
                # the pre-request timestamp: receiving a reply cannot renew its age.
                observed = now_ms()
                replies = request_many(("Windows", "Outputs", "Workspaces", "OverviewState"))
                windows = contents(replies[0], "Windows")
                outputs = contents(replies[1], "Outputs")
                workspaces = contents(replies[2], "Workspaces")
                overview = contents(replies[3], "OverviewState")["is_open"]
                snapshot = version, windows, outputs, workspaces, overview, observed
            except (OSError, EOFError, ValueError, KeyError, TypeError):
                snapshot = None
            self.glib.idle_add(self._deliver, snapshot)
            # Full geometry must follow scrolling/animation, not only focus events.
            self.stopping.wait(max(0.04, 0.075 - (time.monotonic() - start)) if snapshot else 1.0)

    def by_id(self, window_id):
        return next((window for window in self.windows if window.get("id") == window_id), None)

    def focus(self, window_id, finished):
        def execute():
            try:
                response = request({"Action": {"FocusWindow": {"id": window_id}}}, 0.5)
                ok = response == "Handled" or response == {"Handled": None}
            except (OSError, EOFError, ValueError, TypeError):
                ok = False
            self.glib.idle_add(lambda: (finished(ok), False)[1])

        threading.Thread(target=execute, name="typelay-return-focus", daemon=True).start()

    def close(self):
        self.stopping.set()
        self.thread.join(timeout=1.5)
