"""Fixture-only Windows CPU selection and verified private DLL loading.

Use b11717's maximum-positive score rule without PATH/cwd search or fallback.
The Windows production worker does not import or load this fixture runtime.
"""

import _ctypes
import ctypes
import os
import time
from pathlib import Path

from verify_assets import verify_file

SEARCH_SYSTEM32 = 0x00000800
SEARCH = SEARCH_SYSTEM32


class WindowsRuntime:
    def __init__(self, verified, adapter):
        if os.name != "nt" or ctypes.sizeof(ctypes.c_void_p) != 8:
            raise RuntimeError("Windows x64 required")
        self.runtime = Path(verified["cpu_runtime"]["directory"])
        self.records = {item["file"]: item for item in verified["cpu_runtime"]["files"]}
        self.adapter = Path(adapter)
        self.adapter_record = {
            "bytes": self.adapter.stat().st_size,
            "sha256": verified["adapter_sha256"],
        }
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
        self.dependencies = []
        for name in ("libomp.dll", "ggml-base.dll", "ggml.dll", "llama.dll"):
            self.dependencies.append(self.load(self.runtime / name))
        self._verify_loaded({"ggml-base.dll", "libomp.dll", "ggml.dll", "llama.dll"})

    @staticmethod
    def load(path):
        return ctypes.CDLL(str(path), winmode=SEARCH)

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
            if name.startswith(("ggml", "llama")) or name in ("libomp.dll", "typelay_hy.dll"):
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

    def select_cpu_backend(self, verified):
        start = time.monotonic()
        scores, selected, best_score = [], None, 0
        baseline = {"ggml-base.dll", "libomp.dll", "ggml.dll", "llama.dll"}
        for record in verified["cpu_runtime"]["variants"]:
            library = self.load(self.runtime / record["file"])
            try:
                score_function = library.ggml_backend_score
                score_function.argtypes = []
                score_function.restype = ctypes.c_int
                score = score_function()
                del score_function
                self._verify_loaded(baseline | {record["file"]})
                scores.append(
                    {
                        "library": record["file"],
                        "sha256": record["sha256"],
                        "bytes": record["bytes"],
                        "score": score,
                    }
                )
                if score > best_score:
                    selected, best_score = record["file"], score
            finally:
                # CDLL does not automatically FreeLibrary. No function survives.
                library.__dict__.pop("ggml_backend_score", None)
                _ctypes.FreeLibrary(library._handle)
                library._handle = 0
            self._verify_loaded(baseline)
        if selected is None:
            raise RuntimeError("no positive official CPU backend score")
        return self.runtime / selected, {
            "policy": "b11717-official-score-max-positive-cpu-only-v1",
            "scores": scores,
            "selected": selected,
            "selected_score": best_score,
            "selection_seconds": round(time.monotonic() - start, 6),
            "runtime_lock_sha256": verified["cpu_runtime"]["lock_sha256"],
        }

    def loaded_identity(self, selected):
        return self._verify_loaded(
            {"ggml-base.dll", "libomp.dll", "ggml.dll", "llama.dll", "typelay_hy.dll", selected}
        )

    def close(self):
        # Release only this loader's own preload references after hy_close.
        # Native dependency/registry references remain owned by their DLLs.
        for library in reversed(self.dependencies):
            _ctypes.FreeLibrary(library._handle)
            library._handle = 0
        self.dependencies.clear()
