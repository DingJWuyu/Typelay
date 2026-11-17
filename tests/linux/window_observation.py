"""Observe niri and direct Xvfb windows without polling window snapshots."""

import ctypes
import json
import os
import select
import socket
import sys
import time


class XEvent(ctypes.Union):
    class Map(ctypes.Structure):
        _fields_ = [
            ("type", ctypes.c_int),
            ("serial", ctypes.c_ulong),
            ("send_event", ctypes.c_int),
            ("display", ctypes.c_void_p),
            ("event", ctypes.c_ulong),
            ("window", ctypes.c_ulong),
            ("override_redirect", ctypes.c_int),
        ]

    _fields_ = [("type", ctypes.c_int), ("map", Map), ("pad", ctypes.c_long * 24)]


def emit(kind, **data):
    print(json.dumps({"kind": kind, "monotonic": time.monotonic(), **data}), flush=True)


def observe():
    """One process owns both subscriptions; unexpected EOF always fails closed."""
    x11 = ctypes.CDLL("libX11.so.6")
    for name, arguments, result in (
        ("XOpenDisplay", [ctypes.c_char_p], ctypes.c_void_p),
        ("XDefaultRootWindow", [ctypes.c_void_p], ctypes.c_ulong),
        ("XSelectInput", [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_long], ctypes.c_int),
        ("XSync", [ctypes.c_void_p, ctypes.c_int], ctypes.c_int),
        ("XConnectionNumber", [ctypes.c_void_p], ctypes.c_int),
        ("XPending", [ctypes.c_void_p], ctypes.c_int),
        ("XNextEvent", [ctypes.c_void_p, ctypes.POINTER(XEvent)], ctypes.c_int),
        ("XCloseDisplay", [ctypes.c_void_p], ctypes.c_int),
    ):
        function = getattr(x11, name)
        function.argtypes = arguments
        function.restype = result
    display = x11.XOpenDisplay(os.environ["TYPELAY_VERIFY_XDISPLAY"].encode())
    if not display:
        raise RuntimeError("cannot observe the recorded private Xvfb display")
    try:
        root = x11.XDefaultRootWindow(display)
        x11.XSelectInput(
            display, root, 1 << 19
        )  # SubstructureNotifyMask, includes override-redirect.
        x11.XSync(display, 0)  # Subscription is installed before acknowledging readiness.
        with socket.socket(socket.AF_UNIX) as niri:
            niri.connect(os.environ["NIRI_SOCKET"])
            niri.sendall(b'"EventStream"\n')
            buffer = b""
            ready = False
            stopping = False
            event = XEvent()
            while True:
                while x11.XPending(display):
                    x11.XNextEvent(display, ctypes.byref(event))
                    if event.type == 19:  # MapNotify, creation alone does not display a window.
                        emit(
                            "x11-window",
                            id=event.map.window,
                            overrideRedirect=bool(event.map.override_redirect),
                        )
                readers = [niri, x11.XConnectionNumber(display)]
                if not stopping:
                    readers.append(sys.stdin)
                readable, _, _ = select.select(readers, [], [], 0.1 if stopping else None)
                if not readable and stopping:
                    emit("stopped")
                    return
                if niri in readable:
                    chunk = niri.recv(65536)
                    if not chunk:
                        raise RuntimeError("niri event stream exited before observation finished")
                    buffer += chunk
                    while b"\n" in buffer:
                        line, buffer = buffer.split(b"\n", 1)
                        message = json.loads(line)
                        emit("niri-raw", event=message)
                        if "Err" in message:
                            raise RuntimeError(f"niri rejected event stream: {message}")
                        if "WindowsChanged" in message:
                            windows = message["WindowsChanged"]["windows"]
                            if not ready:
                                if windows:
                                    raise RuntimeError(
                                        "private niri must start without client windows"
                                    )
                                ready = True
                                emit(
                                    "ready",
                                    xdisplay=os.environ["TYPELAY_VERIFY_XDISPLAY"],
                                    niri=os.environ["NIRI_SOCKET"],
                                )
                            else:
                                for window in windows:
                                    emit("wayland-window", **window)
                        if "WindowOpenedOrChanged" in message:
                            emit("wayland-window", **message["WindowOpenedOrChanged"]["window"])
                if sys.stdin in readable:
                    if sys.stdin.readline() != "stop\n":
                        raise RuntimeError("observer controller closed without a stop request")
                    if not ready:
                        raise RuntimeError("observation stopped before niri readiness")
                    x11.XSync(display, 0)
                    stopping = True
    finally:
        x11.XCloseDisplay(display)
