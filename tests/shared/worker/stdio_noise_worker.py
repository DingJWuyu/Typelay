"""Controlled engine for real stdio isolation tests, never a model or shipped entry point."""

import atexit
import ctypes
import importlib.machinery
import io
import os
import runpy
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace


def noise(stage):
    print(f"python-{stage}")  # Intentionally buffered, including interpreter shutdown.
    os.write(1, f"fd-{stage}\n".encode())
    if sys.platform == "win32":
        from ctypes import wintypes

        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.GetStdHandle.argtypes = [wintypes.DWORD]
        kernel.GetStdHandle.restype = wintypes.HANDLE
        kernel.WriteFile.argtypes = [
            wintypes.HANDLE,
            ctypes.c_void_p,
            wintypes.DWORD,
            ctypes.POINTER(wintypes.DWORD),
            ctypes.c_void_p,
        ]
        kernel.WriteFile.restype = wintypes.BOOL
        data = f"win32-{stage}\n".encode()
        written = wintypes.DWORD()
        if not kernel.WriteFile(
            kernel.GetStdHandle(-11), data, len(data), ctypes.byref(written), None
        ) or written.value != len(data):
            raise ctypes.WinError(ctypes.get_last_error())


worker, mode = Path(sys.argv[1]), sys.argv[2]
sys.path.insert(0, str(worker.parent))
from typelay_worker import engine  # noqa: E402


class NoisyEngine:
    schema = 3
    directions = ["en_zh"]
    packages = [{"id": "controlled-stdio-only", "version": "1", "directions": directions}]
    styled_translation_version = None
    glossary_translation_version = None
    styled_glossary_translation_version = None
    alternative_translation_version = None
    self_check_directions = [
        SimpleNamespace(id="en_zh", source="en", target="zh", self_check_text="Synthetic input")
    ]

    def __init__(self, models):
        global descendant
        noise("load")
        if mode in ("load-failed", "self-check-load-failed"):
            raise RuntimeError("PRIVATE_EXCEPTION_SENTINEL")
        if mode == "close-output":
            # Any inherited private protocol handle would keep the parent's read open.
            descendant = subprocess.Popen(
                [sys.executable, "-I", "-B", "-c", "import sys; sys.stdin.buffer.read(1)"],
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                close_fds=False,
            )

    def translate(self, *_):
        noise("translate")
        if mode == "translate-failed":
            raise RuntimeError("PRIVATE_EXCEPTION_SENTINEL")
        return engine.Result("translated", "Synthetic response")

    def close(self):
        noise("close")


engine.LocalEngine = NoisyEngine
descendant = None
if mode not in ("isolation-failed", "missing-stderr"):
    atexit.register(noise, "shutdown")
sys.argv = [str(worker), "--models", "unused-controlled-stdio"]
if mode.startswith("self-check"):
    sys.argv.append("--self-check")
if mode == "diagnostic":

    class NoisyDependency:
        def find_spec(self, name, path=None, target=None):
            if name == "ctranslate2":
                return importlib.machinery.ModuleSpec(name, self)
            return None

        def create_module(self, spec):
            return None

        def exec_module(self, module):
            noise("import")
            module.Translator = None  # No translator is needed for hello.

    sys.meta_path.insert(0, NoisyDependency())
    diagnostic = Path(__file__).with_name("alternative_boundary_worker.py")
    sys.argv = [str(diagnostic), "--fixed-fixture", "--models", "unused-controlled-stdio"]
    runpy.run_path(str(diagnostic), run_name="__main__")
if mode in ("close-output", "isolation-failed", "missing-stderr"):
    import run_worker

    sys.stdin = SimpleNamespace(buffer=io.BytesIO())
    if mode == "missing-stderr":
        os.close(2)
    if mode == "isolation-failed":
        from unittest.mock import patch

        with patch("os.dup2", side_effect=OSError("PRIVATE_EXCEPTION_SENTINEL")):
            result = run_worker.main()
    else:
        result = run_worker.main()
    if mode in ("isolation-failed", "missing-stderr"):
        # The failed redirect leaves the original stdout open, but not its private copy.
        os.close(1)
    # Keep this process alive until the parent has observed protocol EOF.
    sys.__stdin__.buffer.read(1)
    if descendant is not None:
        descendant.communicate(b"x", timeout=10)
        if descendant.returncode != 0:
            raise SystemExit(6)
    raise SystemExit(result)
runpy.run_path(str(worker), run_name="__main__")
