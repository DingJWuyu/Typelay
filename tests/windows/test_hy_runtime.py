"""Opt-in actual Windows DLL build/relocation tests, without model loading.

Set TYPELAY_HY_WINDOWS_CPU_RUNTIME to an existing fixed CPU-only directory on
Windows x64 with MSVC installed. Never acquires assets or runs Hy inference.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from eng.windows.hy.build import build  # noqa: E402


class WindowsNativeRuntimeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        supplied = os.environ.get("TYPELAY_HY_WINDOWS_CPU_RUNTIME")
        if sys.platform != "win32" or not supplied:
            raise unittest.SkipTest("explicit fixed Windows CPU runtime and Windows x64 required")
        cls.temporary = tempfile.TemporaryDirectory(prefix="typelay-hy-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.root = Path(cls.temporary.name)
        initial = cls.root / "initial/worker"
        initial.mkdir(parents=True)
        shutil.copytree(
            ROOT / "src/shared/TypeLay.Worker/typelay_worker",
            initial / "typelay_worker",
            ignore=shutil.ignore_patterns("__pycache__", "*.pyc"),
        )
        build(Path(supplied), initial / "native/hy")
        cls.worker = cls.root / "搬迁 包/worker"
        cls.worker.parent.mkdir()
        initial.rename(cls.worker)
        cls.native = cls.worker / "native/hy"

    def run_native(self, code):
        return subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                "-c",
                "import sys; sys.path.insert(0,sys.argv[1]); "
                "from typelay_worker.hy_assets import verify_native; "
                "from typelay_worker.hy_cpu_windows import WindowsRuntime; " + code,
                str(self.worker),
            ],
            capture_output=True,
            text=True,
            timeout=60,
        )

    def test_relocated_dll_closure_loads_with_five_production_exports_and_no_model(self):
        result = self.run_native(
            "import ctypes,json,_ctypes; loader=WindowsRuntime(verify_native()); "
            "selected=loader.select_cpu_backend(); adapter=loader.load_adapter(); "
            "assert all(hasattr(adapter,n) for n in "
            "('hy_close','hy_open_v5','hy_translate','hy_translate_styled_v5','hy_translate_glossary_v5','hy_translate_styled_glossary_v5')); "
            "assert not any(hasattr(adapter,n) for n in "
            "('hy_audit','hy_open_v2','hy_open_v3','hy_open_v4','hy_translate_styled_v4')); "
            "cpu=ctypes.CDLL(str(selected),winmode=0x800); "
            "print(json.dumps(loader.verify_loaded(selected))); "
            "_ctypes.FreeLibrary(cpu._handle); cpu._handle=0; loader.close()"
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(json.loads(result.stdout)), 6)
        lock = json.loads((self.native / "cpu-runtime.win-x64.lock.json").read_text())
        expected = {
            "model.lock.json",
            "cpu-runtime.win-x64.lock.json",
            "production-adapter.json",
            "typelay_hy.dll",
            "LICENSE.llama.cpp",
            *("cpu-runtime/" + r["file"] for r in lock["files"]),
        }
        self.assertEqual(
            {p.relative_to(self.native).as_posix() for p in self.native.rglob("*") if p.is_file()},
            expected,
        )

    def test_foreign_preloaded_runtime_is_rejected(self):
        foreign = self.root / "foreign"
        foreign.mkdir(exist_ok=True)
        target = foreign / "ggml-base.dll"
        shutil.copyfile(self.native / "cpu-runtime/ggml-base.dll", target)
        result = self.run_native(
            f"import ctypes; foreign=ctypes.CDLL({str(target)!r},winmode=0x800); "
            "WindowsRuntime(verify_native())"
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("loaded before", result.stderr)

    def test_changed_cpu_file_is_rejected_without_loading(self):
        path = self.native / "cpu-runtime/ggml.dll"
        original = path.read_bytes()
        try:
            path.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
            result = self.run_native("WindowsRuntime(verify_native())")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("full hash mismatch", result.stderr)
        finally:
            path.write_bytes(original)


if __name__ == "__main__":
    unittest.main()
