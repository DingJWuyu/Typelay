"""Verified private Windows CPU loading, shared by production and the audit fixture.

Only construction changes DLL search policy, inside an owned Hy worker process.
Use b11717's maximum-positive official score with no PATH/cwd search or fallback.
"""

import _ctypes
import ctypes
import os
import sys
from pathlib import Path

from .hy_assets import WINDOWS_DEPENDENCIES, plain_path, verify_file

SEARCH_SYSTEM32 = 0x00000800
SEARCH = SEARCH_SYSTEM32


class WindowsRuntime:
    def __init__(self, verified):
        if sys.platform != "win32" or ctypes.sizeof(ctypes.c_void_p) != 8:
            raise RuntimeError("Windows x64 required")
        self.runtime = plain_path(verified["cpu_runtime"]["directory"], directory=True)
        self.records = {item["file"]: item for item in verified["cpu_runtime"]["files"]}
        self.variants = verified["cpu_runtime"]["variants"]
        self.adapter = plain_path(verified["adapter_path"])
        self.adapter_record = verified["adapter_record"]
        self.dependencies = []
        self.adapter_library = None
        self.selected = None
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True, winmode=SEARCH_SYSTEM32)
        self.kernel.SetDefaultDllDirectories.argtypes = [ctypes.c_uint32]
        self.kernel.SetDefaultDllDirectories.restype = ctypes.c_int
        self.kernel.SetDllDirectoryW.argtypes = [ctypes.c_wchar_p]
        self.kernel.SetDllDirectoryW.restype = ctypes.c_int
        self.kernel.GetCurrentProcess.argtypes = []
        self.kernel.GetCurrentProcess.restype = ctypes.c_void_p
        self.kernel.K32EnumProcessModules.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(ctypes.c_void_p),
            ctypes.c_uint32,
            ctypes.POINTER(ctypes.c_uint32),
        ]
        self.kernel.K32EnumProcessModules.restype = ctypes.c_int
        self.kernel.GetModuleFileNameW.argtypes = [
            ctypes.c_void_p,
            ctypes.c_wchar_p,
            ctypes.c_uint32,
        ]
        self.kernel.GetModuleFileNameW.restype = ctypes.c_uint32
        # Exclude both the executable directory and current directory from implicit
        # loads, including the LoadLibrary call made inside official ggml code.
        if not self.kernel.SetDllDirectoryW("") or not self.kernel.SetDefaultDllDirectories(SEARCH):
            raise ctypes.WinError(ctypes.get_last_error())
        if self._relevant_modules():
            raise RuntimeError("runtime DLL was loaded before the verified loader")
        try:
            expected = set()
            for name in WINDOWS_DEPENDENCIES:
                self.dependencies.append(self._load(self.runtime / name))
                expected.add(name)
                self._verify_loaded(expected)
        except BaseException:
            self.close()
            raise

    def _load(self, path):
        record = self.adapter_record if path == self.adapter else self.records[path.name]
        verify_file(path, record)
        return ctypes.CDLL(str(path), winmode=SEARCH)

    @staticmethod
    def _unload(library):
        if library._handle:
            _ctypes.FreeLibrary(library._handle)
            library._handle = 0

    def _relevant_modules(self):
        modules = (ctypes.c_void_p * 1024)()
        needed = ctypes.c_uint32()
        if not self.kernel.K32EnumProcessModules(
            self.kernel.GetCurrentProcess(), modules, ctypes.sizeof(modules), ctypes.byref(needed)
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        if needed.value > ctypes.sizeof(modules) or needed.value % ctypes.sizeof(ctypes.c_void_p):
            raise RuntimeError("loaded module enumeration bound")
        found = {}
        for module in modules[: needed.value // ctypes.sizeof(ctypes.c_void_p)]:
            value = ctypes.create_unicode_buffer(32768)
            count = self.kernel.GetModuleFileNameW(module, value, len(value))
            if not count or count >= len(value):
                raise RuntimeError("loaded module path bound")
            path = Path(value.value)
            name = path.name.lower()
            if name.startswith(("ggml", "llama", "typelay_hy")) or name == "libomp.dll":
                if name in found:
                    raise RuntimeError("duplicate runtime DLL basename")
                found[name] = path
        return found

    def _verify_loaded(self, expected):
        loaded = self._relevant_modules()
        if set(loaded) != expected:
            raise RuntimeError("unexpected or missing loaded runtime DLL")
        result = []
        for name, path in sorted(loaded.items()):
            target = self.adapter if name == "typelay_hy.dll" else self.runtime / name
            if os.path.normcase(str(path)) != os.path.normcase(str(target)):
                raise RuntimeError("loaded runtime DLL path mismatch")
            record = self.adapter_record if name == "typelay_hy.dll" else self.records[name]
            verify_file(path, record)
            result.append(
                {
                    "library": name,
                    "path": str(path),
                    "bytes": record["bytes"],
                    "sha256": record["sha256"],
                }
            )
        return result

    def select_cpu_backend(self, *, on_score=None):
        """Return one CPU path. The fixture may observe scores without owning loading."""
        if self.selected is not None or self.adapter_library is not None:
            raise RuntimeError("CPU selection already completed")
        selected, best_score = None, 0
        baseline = set(WINDOWS_DEPENDENCIES)
        for record in self.variants:
            library = self._load(self.runtime / record["file"])
            try:
                # Verify mapped dependencies before invoking any probe function.
                self._verify_loaded(baseline | {record["file"]})
                score_function = library.ggml_backend_score
                score_function.argtypes = []
                score_function.restype = ctypes.c_int
                score = score_function()
                del score_function
                if on_score is not None:
                    on_score({**record, "score": score})
                if score > best_score:
                    selected, best_score = record["file"], score
            finally:
                library.__dict__.pop("ggml_backend_score", None)
                self._unload(library)
            self._verify_loaded(baseline)
        if selected is None:
            raise RuntimeError("no positive official CPU backend score")
        self.selected = self.runtime / selected
        return self.selected

    def load_adapter(self):
        if self.selected is None or self.adapter_library is not None:
            raise RuntimeError("select CPU once before loading the adapter")
        self.adapter_library = self._load(self.adapter)
        try:
            self._verify_loaded(set(WINDOWS_DEPENDENCIES) | {self.adapter.name})
        except BaseException:
            self._unload(self.adapter_library)
            self.adapter_library = None
            raise
        return self.adapter_library

    def verify_loaded(self, selected):
        if selected != self.selected or self.adapter_library is None:
            raise RuntimeError("selected CPU identity mismatch")
        return self._verify_loaded(set(WINDOWS_DEPENDENCIES) | {self.adapter.name, selected.name})

    def close(self):
        # Caller must finish hy_close before releasing these owned DLL references.
        if self.adapter_library is not None:
            self._unload(self.adapter_library)
            self.adapter_library = None
        for library in reversed(self.dependencies):
            self._unload(library)
        self.dependencies.clear()
