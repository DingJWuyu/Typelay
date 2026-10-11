"""niri IPC and validated window-local to output geometry. No title matching."""

import json
import math
import os
import socket
import threading
import time
from pathlib import Path

from wire import now_ms


def request(command, timeout=0.25):
    path = os.environ.get("NIRI_SOCKET", "")
    if not Path(path).is_absolute():
        raise OSError("niri-socket-unavailable")
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        connection.settimeout(timeout)
        connection.connect(path)
        connection.sendall((json.dumps(command, separators=(",", ":")) + "\n").encode())
        data = bytearray()
        while b"\n" not in data:
            part = connection.recv(16384)
            if not part:
                raise EOFError("niri-closed")
            data.extend(part)
            if len(data) > 2 * 1024 * 1024:
                raise ValueError("niri-response-size")
        response = json.loads(data.split(b"\n", 1)[0])
        if not isinstance(response, dict) or "Ok" not in response:
            raise OSError("niri-operation-rejected")
        return response["Ok"]


def contents(response, name):
    if not isinstance(response, dict) or name not in response:
        raise ValueError("niri-response-type")
    return response[name]


def finite_pair(value):
    return (
        isinstance(value, (list, tuple))
        and len(value) == 2
        and all(isinstance(x, (int, float)) and math.isfinite(x) for x in value)
    )


def place_geometry(window: dict, outputs: dict, workspaces: list, native: dict) -> dict | None:
    """Require agreement on the xdg window geometry before mapping a source field.

    niri's positions are logical; Core receives physical pixels in one output's
    coordinate frame. Never combine outputs with different scales in one placement.
    """
    try:
        layout = window["layout"]
        position = layout["tile_pos_in_workspace_view"]
        offset = layout["window_offset_in_tile"]
        size = layout["window_size"]
        if not all(finite_pair(value) for value in (position, offset, size)):
            return None
        if any(abs(size[i] - native[["frameWidth", "frameHeight"][i]]) > 1 for i in range(2)):
            return None
        workspace = next(w for w in workspaces if w["id"] == window["workspace_id"])
        output_name = workspace["output"]
        output = outputs[output_name]["logical"]
        scale = output["scale"]
        if not 0.5 <= scale <= 8 or not math.isfinite(scale):
            return None
        # Hidden workspaces may retain layout information, but must not get an overlay.
        if not workspace.get("is_active", False):
            return None
        origin_x, origin_y = output["x"], output["y"]
        rectangle = native["bounds"]
        x, y, width, height = (rectangle[key] for key in ("x", "y", "width", "height"))
        if not all(isinstance(v, (int, float)) and math.isfinite(v) for v in (x, y, width, height)):
            return None
        if (
            x < -1
            or y < -1
            or width <= 0
            or height <= 0
            or x + width > size[0] + 1
            or y + height > size[1] + 1
        ):
            return None
        bounds = {
            "x": (origin_x + position[0] + offset[0] + x) * scale,
            "y": (origin_y + position[1] + offset[1] + y) * scale,
            "width": width * scale,
            "height": height * scale,
        }
        work = {
            "x": origin_x * scale,
            "y": origin_y * scale,
            "width": output["width"] * scale,
            "height": output["height"] * scale,
        }
        if bounds["x"] + bounds["width"] <= work["x"] or bounds["x"] >= work["x"] + work["width"]:
            return None
        if bounds["y"] + bounds["height"] <= work["y"] or bounds["y"] >= work["y"] + work["height"]:
            return None
        return {
            "bounds": bounds,
            "geometry": {
                "workArea": work,
                "output": output_name,
                "scale": scale,
                "originX": origin_x,
                "originY": origin_y,
            },
        }
    except (KeyError, TypeError, ValueError, StopIteration):
        return None


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
            observed = now_ms()
            try:
                if not version:
                    version = contents(request("Version"), "Version")
                windows = contents(request("Windows"), "Windows")
                outputs = contents(request("Outputs"), "Outputs")
                workspaces = contents(request("Workspaces"), "Workspaces")
                overview = contents(request("OverviewState"), "OverviewState")["is_open"]
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
