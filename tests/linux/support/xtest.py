"""Preconnected XTest input for owned isolated displays, not physical-key evidence."""

import ctypes

from wire import now_ms


def prepare_xtest(stack):
    """Own one existing-display connection for an ordered real input sequence."""
    x11 = ctypes.CDLL("libX11.so.6")
    xtst = ctypes.CDLL("libXtst.so.6")
    display_type = ctypes.c_void_p
    x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
    x11.XOpenDisplay.restype = display_type
    x11.XCloseDisplay.argtypes = [display_type]
    x11.XCloseDisplay.restype = ctypes.c_int
    x11.XStringToKeysym.argtypes = [ctypes.c_char_p]
    x11.XStringToKeysym.restype = ctypes.c_ulong
    x11.XKeysymToKeycode.argtypes = [display_type, ctypes.c_ulong]
    x11.XKeysymToKeycode.restype = ctypes.c_ubyte
    x11.XSync.argtypes = [display_type, ctypes.c_int]
    x11.XSync.restype = ctypes.c_int
    xtst.XTestQueryExtension.argtypes = [display_type] + [ctypes.POINTER(ctypes.c_int)] * 4
    xtst.XTestQueryExtension.restype = ctypes.c_int
    xtst.XTestFakeKeyEvent.argtypes = [display_type, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
    xtst.XTestFakeKeyEvent.restype = ctypes.c_int
    display = x11.XOpenDisplay(None)
    assert display, "Cannot open existing XTest DISPLAY"
    stack.callback(x11.XCloseDisplay, display)
    extension = [ctypes.c_int() for _ in range(4)]
    assert xtst.XTestQueryExtension(display, *(ctypes.byref(value) for value in extension))
    return x11, xtst, display


def prepare_key(stack, name, *, connection=None):
    """Preconnect to XTest; each call delivers one real down or up event."""
    x11, xtst, display = connection or prepare_xtest(stack)
    keycode = x11.XKeysymToKeycode(display, x11.XStringToKeysym(name.encode("ascii")))
    assert keycode, f"XTest DISPLAY has no {name} keycode"

    def send_key(down, sync=True, *, timing=None):
        if timing is not None:
            timing["downStartedAtMs" if down else "upStartedAtMs"] = now_ms()
        sent = xtst.XTestFakeKeyEvent(display, keycode, int(down), 0)
        if timing is not None:
            timing["downReturnedAtMs" if down else "upReturnedAtMs"] = now_ms()
        # Flush and wait only for X server processing, not Wayland delivery.
        if sync:
            if timing is not None:
                timing["xSyncStartedAtMs"] = now_ms()
            x11.XSync(display, 0)
            if timing is not None:
                timing["xSyncReturnedAtMs"] = now_ms()
        assert sent, f"XTest refused {name} key event"

    return send_key


def prepare_pointer(stack, window):
    """Preconnect one real move/press/release to an owned nested client."""
    x11, xtst, display = prepare_xtest(stack)
    window = int(window, 0)
    assert window > 0
    x11.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
    x11.XDefaultRootWindow.restype = ctypes.c_ulong
    x11.XTranslateCoordinates.argtypes = [
        ctypes.c_void_p,
        ctypes.c_ulong,
        ctypes.c_ulong,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.POINTER(ctypes.c_int),
        ctypes.POINTER(ctypes.c_int),
        ctypes.POINTER(ctypes.c_ulong),
    ]
    x11.XTranslateCoordinates.restype = ctypes.c_int
    xtst.XTestFakeMotionEvent.argtypes = [
        ctypes.c_void_p,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_ulong,
    ]
    xtst.XTestFakeMotionEvent.restype = ctypes.c_int
    xtst.XTestFakeButtonEvent.argtypes = [
        ctypes.c_void_p,
        ctypes.c_uint,
        ctypes.c_int,
        ctypes.c_ulong,
    ]
    xtst.XTestFakeButtonEvent.restype = ctypes.c_int
    root = x11.XDefaultRootWindow(display)

    def click(x, y, *, timing):
        timing["startedAtMs"] = now_ms()
        root_x, root_y, child = ctypes.c_int(), ctypes.c_int(), ctypes.c_ulong()
        assert x11.XTranslateCoordinates(
            display,
            window,
            root,
            x,
            y,
            ctypes.byref(root_x),
            ctypes.byref(root_y),
            ctypes.byref(child),
        ), "Cannot resolve the owned nested window coordinates"
        assert xtst.XTestFakeMotionEvent(display, -1, root_x.value, root_y.value, 0)
        assert xtst.XTestFakeButtonEvent(display, 1, 1, 0)
        assert xtst.XTestFakeButtonEvent(display, 1, 0, 0)
        timing["queuedAtMs"] = now_ms()
        x11.XSync(display, 0)
        timing["xSyncReturnedAtMs"] = now_ms()
        # XSync acknowledges X server processing, not Qt hit testing or click.

    return click
