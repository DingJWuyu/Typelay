"""DLL-system boundary simulation, not Windows execution or model evidence."""

import ctypes
import hashlib
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/shared/TypeLay.Worker"))
from typelay_worker.hy_cpu_windows import WindowsRuntime  # noqa: E402


class Function:
    def __init__(self, call):
        self.call = call

    def __call__(self, *args):
        return self.call(*args)


class DllSystem:
    """Model only the public ctypes/Win32 loader APIs; file checks remain real."""

    def __init__(self):
        self.mapped = {}
        self.next_handle = 1
        self.loads = []
        self.scores = {}
        self.scored = []
        self.search = []
        self.extra_on_load = {}
        self.kernel = SimpleNamespace(
            SetDefaultDllDirectories=Function(lambda flags: self.search.append(flags) or 1),
            SetDllDirectoryW=Function(lambda path: self.search.append(path) or 1),
            GetCurrentProcess=Function(lambda: 1),
            K32EnumProcessModules=Function(self.enumerate),
            GetModuleFileNameW=Function(self.module_name),
        )

    def enumerate(self, process, modules, capacity, needed):
        ctypes.cast(needed, ctypes.POINTER(ctypes.c_uint32))[0] = len(self.mapped) * ctypes.sizeof(
            ctypes.c_void_p
        )
        for index, handle in enumerate(self.mapped):
            modules[index] = handle
        return 1

    def module_name(self, handle, output, capacity):
        output.value = str(self.mapped[handle])
        return len(output.value)

    def map(self, path):
        handle = self.next_handle
        self.next_handle += 1
        self.mapped[handle] = Path(path)
        return handle

    def load(self, path, *, winmode):
        if not Path(path).is_absolute() or winmode != 0x800:
            raise AssertionError("private absolute loads with SYSTEM32 only required")
        self.loads.append(Path(path).name)
        handle = self.map(path)
        if Path(path).name in self.extra_on_load:
            self.map(self.extra_on_load[Path(path).name])
        library = SimpleNamespace(_handle=handle)
        if Path(path).name.startswith("ggml-cpu-"):

            def score():
                self.scored.append(Path(path).name)
                return self.scores.get(Path(path).name, 0)

            library.ggml_backend_score = Function(score)
        return library

    def unload(self, handle):
        self.mapped.pop(handle)


class WindowsLoaderTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.runtime = self.root / "cpu"
        self.runtime.mkdir()
        names = [
            "libomp.dll",
            "ggml-base.dll",
            "ggml.dll",
            "llama.dll",
            "ggml-cpu-alderlake.dll",
            "ggml-cpu-x64.dll",
            "ggml-cpu-zen4.dll",
        ]
        records = []
        for name in names:
            path = self.runtime / name
            path.write_bytes(("DLL boundary bytes " + name).encode())
            records.append(self.record(path))
        adapter = self.root / "typelay_hy.dll"
        adapter.write_bytes(b"adapter boundary bytes")
        self.verified = {
            "cpu_runtime": {
                "directory": str(self.runtime),
                "files": records,
                "variants": [r for r in records if r["file"].startswith("ggml-cpu-")],
            },
            "adapter_path": str(adapter),
            "adapter_record": self.record(adapter),
        }
        self.system = DllSystem()
        for target, value in (
            ("sys.platform", "win32"),
            ("ctypes.WinDLL", lambda *args, **kwargs: self.system.kernel),
            ("ctypes.CDLL", self.system.load),
            ("_ctypes.FreeLibrary", self.system.unload),
        ):
            patcher = patch(target, value, create=True)
            patcher.start()
            self.addCleanup(patcher.stop)

    @staticmethod
    def record(path):
        return {
            "file": path.name,
            "bytes": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        }

    def loader(self):
        loader = WindowsRuntime(self.verified)
        self.addCleanup(loader.close)
        return loader

    def test_best_positive_score_and_tie_order_use_only_private_paths(self):
        self.system.scores = {
            "ggml-cpu-alderlake.dll": 7,
            "ggml-cpu-x64.dll": 12,
            "ggml-cpu-zen4.dll": 12,
        }
        observed = []
        loader = self.loader()
        selected = loader.select_cpu_backend(on_score=observed.append)
        self.assertEqual(selected, self.runtime / "ggml-cpu-x64.dll")
        self.assertEqual([r["score"] for r in observed], [7, 12, 12])
        self.assertEqual(self.system.search, ["", 0x800])
        loader.load_adapter()
        cpu_handle = self.system.map(selected)
        identity = loader.verify_loaded(selected)
        self.assertEqual(len(identity), 6)
        self.assertTrue(all(Path(r["path"]).is_relative_to(self.root) for r in identity))
        self.system.unload(cpu_handle)
        loader.close()
        loader.close()
        self.assertEqual(self.system.mapped, {})

    def test_no_positive_score_does_not_load_adapter_or_choose_a_fallback(self):
        self.system.scores = {"ggml-cpu-alderlake.dll": -1}
        loader = self.loader()
        with self.assertRaisesRegex(RuntimeError, "no positive"):
            loader.select_cpu_backend()
        self.assertNotIn("typelay_hy.dll", self.system.loads)
        self.assertEqual(len(self.system.mapped), 4)

    def test_foreign_preloaded_unknown_or_same_name_dll_is_rejected_before_loading(self):
        for name in ("ggml-base.dll", "ggml-vulkan.dll", "llama-common.dll", "libomp.dll"):
            with self.subTest(name=name):
                handle = self.system.map(self.root / "foreign" / name)
                with self.assertRaisesRegex(RuntimeError, "loaded before"):
                    WindowsRuntime(self.verified)
                self.assertEqual(self.system.loads, [])
                self.system.unload(handle)

    def test_constructor_failure_releases_only_owned_dlls(self):
        self.system.extra_on_load["ggml.dll"] = self.root / "foreign/ggml-rpc.dll"
        with self.assertRaisesRegex(RuntimeError, "unexpected or missing"):
            WindowsRuntime(self.verified)
        self.assertEqual(list(self.system.mapped.values()), [self.root / "foreign/ggml-rpc.dll"])

    def test_foreign_probe_dependency_is_refused_before_score_execution(self):
        loader = self.loader()
        self.system.extra_on_load["ggml-cpu-alderlake.dll"] = self.root / "foreign/libomp.dll"
        with self.assertRaisesRegex(RuntimeError, "duplicate"):
            loader.select_cpu_backend()
        self.assertEqual(self.system.scored, [])
        self.assertNotIn(self.runtime / "ggml-cpu-alderlake.dll", self.system.mapped.values())

    def test_changed_probe_or_adapter_is_refused_before_loading(self):
        loader = self.loader()
        probe = self.runtime / "ggml-cpu-alderlake.dll"
        original = probe.read_bytes()
        probe.write_bytes(b"X" + original[1:])
        with self.assertRaisesRegex(ValueError, "full hash mismatch"):
            loader.select_cpu_backend()
        self.assertEqual(self.system.scored, [])
        probe.write_bytes(original)
        self.system.scores[probe.name] = 1
        loader.select_cpu_backend()
        Path(self.verified["adapter_path"]).write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "full hash mismatch"):
            loader.load_adapter()
        self.assertNotIn("typelay_hy.dll", self.system.loads)

    def test_adapter_order_repeat_selection_and_wrong_selected_identity_are_rejected(self):
        loader = self.loader()
        with self.assertRaises(RuntimeError):
            loader.load_adapter()
        self.system.scores["ggml-cpu-x64.dll"] = 1
        loader.select_cpu_backend()
        with self.assertRaises(RuntimeError):
            loader.select_cpu_backend()
        loader.load_adapter()
        with self.assertRaises(RuntimeError):
            loader.load_adapter()
        with self.assertRaises(RuntimeError):
            loader.verify_loaded(self.runtime / "ggml-cpu-zen4.dll")


if __name__ == "__main__":
    unittest.main()
