"""Capture evidence boundaries, not GTK rendering or real compositor acceptance."""

import asyncio
import hashlib
import os
import stat
import subprocess
import sys
import tempfile
import time
import unittest
from contextlib import ExitStack
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import patch

import gi
from settings_ui import capture_owned_niri, pixbuf_observation

# Load the real image library before the controlled niri module scope restores
# sys.modules. Native GTypes cannot be safely unloaded and imported again.
gi.require_version("GdkPixbuf", "2.0")
from gi.repository import GdkPixbuf  # noqa: E402


def image(width, height, channels, stride, pixels):
    return NS(
        get_width=lambda: width,
        get_height=lambda: height,
        get_n_channels=lambda: channels,
        get_rowstride=lambda: stride,
        get_bits_per_sample=lambda: 8,
        get_has_alpha=lambda: channels == 4,
        get_pixels=lambda: pixels,
    )


def png(alpha=255):
    """A single synthetic RGBA pixel, never evidence of a rendered desktop."""
    pixel = GdkPixbuf.Pixbuf.new(GdkPixbuf.Colorspace.RGB, True, 8, 1, 1)
    pixel.fill((40 << 24) | (60 << 16) | (80 << 8) | alpha)
    saved, data = pixel.save_to_bufferv("png", [], [])
    assert saved
    return data


class PixbufObservationTests(unittest.TestCase):
    def test_summary_ignores_padding_and_preserves_transparent_pixel_evidence(self):
        pixels = b"\0\0\0\0\xff\0\0\xff" + b"junk" + b"\0\xff\0\x80\0\0\xff\xff"
        observed = pixbuf_observation(image(2, 2, 4, 12, pixels))
        expected = b"\0\0\0\0\xff\0\0\xff\0\xff\0\x80\0\0\xff\xff"
        self.assertEqual(hashlib.sha256(expected).hexdigest(), observed["rgbaSha256"])
        self.assertEqual(
            (0, 255, 3),
            (
                observed["alphaMin"],
                observed["alphaMax"],
                observed["nontransparentPixels"],
            ),
        )

    def test_rgb_and_rgba_have_the_same_effective_pixel_digest(self):
        rgb = pixbuf_observation(image(1, 1, 3, 3, b"\x01\x02\x03"))
        rgba = pixbuf_observation(image(1, 1, 4, 4, b"\x01\x02\x03\xff"))
        self.assertEqual(rgb["rgbaSha256"], rgba["rgbaSha256"])
        self.assertEqual(255, rgb["alphaMin"])

    def test_zero_alpha_is_not_reported_as_an_opaque_black_image(self):
        observed = pixbuf_observation(image(1, 1, 4, 4, b"\0" * 4))
        self.assertEqual(0, observed["alphaMax"])
        self.assertEqual(0, observed["nontransparentPixels"])
        self.assertFalse(observed["rgbHasNonzero"])


class Child:
    def __init__(self, *, timeout=False):
        self.pid = 12345
        self.code = None if timeout else 0
        self.kills = 0

    def poll(self):
        return self.code

    def wait(self, timeout):
        if self.code is None:
            raise subprocess.TimeoutExpired("controlled-grim", timeout)
        return self.code

    def kill(self):
        self.kills += 1
        self.code = -9


class OwnedCaptureTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.output = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.runtime = self.output / "runtime"
        self.runtime.mkdir(mode=0o700)
        self.niri = self.runtime / "niri.wayland-1.test.sock"
        self.wayland = self.runtime / "wayland-1"
        # No socket/display is created. Only external stat/IPC observations are
        # controlled; the cancellation test below starts one real owned process.
        self.niri.touch()
        self.wayland.touch()
        self.windows = [
            {"pid": os.getpid(), "title": "TypeLay Settings", "id": 7, "is_focused": True}
        ]
        self.outputs = {"winit": {"name": "winit"}}
        self.real_stat = Path.stat
        self.real_popen = subprocess.Popen

    def endpoints(self):
        def observed_stat(path, *args, **kwargs):
            if path in (self.niri, self.wayland):
                return NS(st_uid=os.getuid(), st_mode=stat.S_IFSOCK | 0o600)
            return self.real_stat(path, *args, **kwargs)

        def request_many(commands, timeout):
            self.assertEqual(("Windows", "Outputs"), commands)
            self.assertGreater(timeout, 0)
            return [{"Windows": self.windows}, {"Outputs": self.outputs}]

        scope = ExitStack()
        scope.enter_context(
            patch.dict(
                os.environ,
                {
                    "XDG_RUNTIME_DIR": str(self.runtime),
                    "NIRI_SOCKET": str(self.niri),
                    "WAYLAND_DISPLAY": "wayland-1",
                    "GDK_BACKEND": "wayland",
                },
            )
        )
        # This controlled endpoint owns its environment; patch.dict restores it.
        os.environ.pop("WAYLAND_SOCKET", None)
        scope.enter_context(patch.object(Path, "stat", autospec=True, side_effect=observed_stat))
        scope.enter_context(
            patch.dict(
                sys.modules,
                {
                    "niri": NS(
                        request_many=request_many, contents=lambda response, key: response[key]
                    ),
                },
            )
        )
        return scope

    async def capture(self, observation, *, name="shortcuts-en-light-default", deadline=None):
        await capture_owned_niri(
            self.output,
            "TypeLay Settings",
            name=name,
            deadline=time.monotonic() + 30 if deadline is None else deadline,
            observation=observation,
        )

    async def test_only_the_validated_owned_output_is_passed_to_one_command(self):
        def spawn(args, **kwargs):
            self.assertEqual(["grim", "-o", "winit"], args[:3])
            self.assertNotIn("start_new_session", kwargs)
            self.assertEqual(subprocess.DEVNULL, kwargs["stderr"])
            # A real PNG decoder may inspect this fixture, not a real desktop.
            Path(args[3]).write_bytes(png())
            return Child()

        observed = {}
        with self.endpoints(), patch("settings_ui.subprocess.Popen", side_effect=spawn) as command:
            await self.capture(observed)
        self.assertEqual(1, command.call_count)
        self.assertEqual("written-not-reviewed", observed["status"])
        self.assertEqual(
            (os.getpid(), 7, "TypeLay Settings"),
            (
                observed["pid"],
                observed["windowId"],
                observed["title"],
            ),
        )
        self.assertTrue(observed["childReaped"])
        self.assertEqual(1, observed["pixels"]["nontransparentPixels"])

    async def test_transparent_or_invalid_output_is_not_recorded_as_a_captured_image(self):
        for data in (png(alpha=0), b"not-a-png"):
            with self.subTest(data=data):
                destination = self.output / "shortcuts-en-light-default.png"
                destination.unlink(missing_ok=True)

                def spawn(args, **_kwargs):
                    Path(args[3]).write_bytes(data)
                    return Child()

                observed = {}
                with self.endpoints(), patch("settings_ui.subprocess.Popen", side_effect=spawn):
                    await self.capture(observed)
                self.assertEqual("unavailable", observed["status"])
                self.assertTrue(observed["failure"])
                self.assertTrue(observed["childReaped"])

    async def test_nine_state_names_keep_separate_images_without_overwriting_or_renewing(self):
        names = (
            "en-light-default",
            "en-light-recording",
            "en-light-save-failed",
            "en-light-duplicate",
            "en-light-conflict",
            "en-light-default-final",
            "en-dark-default-final",
            "zh-light-default-final",
            "zh-dark-default-final",
        )
        deadline = time.monotonic() + 30

        def spawn(args, **_kwargs):
            Path(args[3]).write_bytes(png())
            return Child()

        with self.endpoints(), patch("settings_ui.subprocess.Popen", side_effect=spawn) as command:
            for name in names:
                observed = {}
                await self.capture(observed, name="shortcuts-" + name, deadline=deadline)
                self.assertEqual("written-not-reviewed", observed["status"])
                self.assertEqual("shortcuts-" + name + ".png", observed["file"])
                self.assertTrue(observed["childReaped"])
            original = (self.output / "shortcuts-en-light-default.png").read_bytes()
            repeated = {}
            await self.capture(repeated, deadline=deadline)
            self.assertEqual("unavailable", repeated["status"])
            self.assertEqual(
                original, (self.output / "shortcuts-en-light-default.png").read_bytes()
            )
            expired = {}
            await self.capture(expired, name="not-started", deadline=time.monotonic() - 1)
            self.assertEqual("unavailable", expired["status"])
        self.assertEqual(9, command.call_count)
        self.assertEqual(9, len(list(self.output.glob("shortcuts-*.png"))))

    async def test_foreign_or_ambiguous_windows_and_unknown_outputs_never_capture(self):
        for change in (
            lambda: self.windows.append(dict(self.windows[0], pid=os.getpid() + 1)),
            lambda: self.windows.append(dict(self.windows[0], id=8)),
            lambda: self.windows[0].update(is_focused=False),
            lambda: self.outputs.update({"other": {"name": "other"}}),
        ):
            with self.subTest(change=change):
                self.windows = [
                    {"pid": os.getpid(), "title": "TypeLay Settings", "id": 7, "is_focused": True}
                ]
                self.outputs = {"winit": {"name": "winit"}}
                change()
                observed = {}
                with self.endpoints(), patch("settings_ui.subprocess.Popen") as command:
                    await self.capture(observed)
                command.assert_not_called()
                self.assertEqual("unavailable", observed["status"])

    async def test_a_different_runtime_or_inherited_wayland_fd_never_captures(self):
        for environment in (
            {"XDG_RUNTIME_DIR": str(self.output)},
            {"WAYLAND_SOCKET": "0"},
        ):
            with self.subTest(environment=environment):
                observed = {}
                with (
                    self.endpoints(),
                    patch.dict(os.environ, environment),
                    patch("settings_ui.subprocess.Popen") as command,
                ):
                    await self.capture(observed)
                command.assert_not_called()
                self.assertEqual("owned-runtime", observed["stage"])

    async def test_command_timeout_kills_and_reaps_without_becoming_a_business_failure(self):
        observed, child = {}, Child(timeout=True)
        with self.endpoints(), patch("settings_ui.subprocess.Popen", return_value=child) as command:
            await self.capture(observed)
        self.assertEqual(1, command.call_count)
        self.assertEqual("TimeoutExpired", observed["failure"])
        self.assertEqual(1, child.kills)
        self.assertTrue(observed["childReaped"])

    async def test_cancellation_reaps_the_real_owned_child_and_stays_cancelled(self):
        entered, children = asyncio.Event(), []

        def spawn(_args, **kwargs):
            child = self.real_popen(
                [sys.executable, "-c", "import signal; signal.pause()"], **kwargs
            )
            children.append(child)
            entered.set()
            return child

        observed = {}
        with self.endpoints(), patch("settings_ui.subprocess.Popen", side_effect=spawn):
            task = asyncio.create_task(self.capture(observed))
            try:
                await asyncio.wait_for(entered.wait(), 3)
                task.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await task
            finally:
                for child in children:
                    if child.poll() is None:
                        child.kill()
                    child.wait(timeout=2)
        self.assertEqual(1, len(children))
        self.assertIsNotNone(children[0].returncode)
        self.assertTrue(observed["childReaped"])
        self.assertEqual("cancelled", observed["status"])


if __name__ == "__main__":
    unittest.main()
