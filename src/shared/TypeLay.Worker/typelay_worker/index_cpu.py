"""Select and load a verified CPU-only Linux Index runtime.

Use b11913's maximum-positive score rule, then explicitly load one backend.
No load_all, CPU-name inference, GPU/RPC backend or unverified fallback.
Verify actual ELF mappings before the native handle becomes available.
"""

import _ctypes
import ctypes
import mmap
import os
import re
import sys
from pathlib import Path

from .index_assets import DEPENDENCIES, plain_path, verify_file


class LinuxRuntime:
    """Own explicit CPU-only loads and verify the actual mapped runtime closure."""

    def __init__(self, verified):
        if not sys.platform.startswith("linux") or ctypes.sizeof(ctypes.c_void_p) != 8:
            raise RuntimeError("Linux x64 required")
        self.runtime = Path(verified["cpu_runtime"]["directory"])
        self.records = {item["file"]: item for item in verified["cpu_runtime"]["files"]}
        self.variants = verified["cpu_runtime"]["variants"]
        self.adapter = Path(verified["adapter_path"])
        self.adapter_record = verified["adapter_record"]
        self.dependencies = []
        self.adapter_library = None
        self.selected = None
        if self._mapped_modules():
            raise RuntimeError("runtime library was loaded before the verified loader")
        try:
            expected = set()
            for name in DEPENDENCIES:
                self.dependencies.append(self._load(self.runtime / name))
                expected.add(name)
                self._verify_loaded(expected)
        except BaseException:
            self.close()
            raise

    def _load(self, path):
        record = self.adapter_record if path == self.adapter else self.records[path.name]
        verify_file(path, record)
        return ctypes.CDLL(str(path), mode=os.RTLD_NOW | os.RTLD_LOCAL)

    @staticmethod
    def _unload(library):
        if library._handle:
            _ctypes.dlclose(library._handle)
            library._handle = 0

    @staticmethod
    def _maps():
        with Path("/proc/self/maps").open(encoding="utf-8", errors="surrogateescape") as stream:
            data = stream.read(8 * 1024 * 1024 + 1)
        if len(data) > 8 * 1024 * 1024:
            raise RuntimeError("loaded library map bound")
        return [line.split(None, 5) for line in data.splitlines()]

    @classmethod
    def _mapped_modules(cls):
        # /proc records actual mappings, independent of loader search paths.
        result = {}
        for fields in cls._maps():
            if len(fields) != 6:
                continue
            raw = fields[5]
            path_text = re.sub(r"\\([0-7]{3})", lambda m: chr(int(m[1], 8)), raw)
            name = Path(path_text.removesuffix(" (deleted)")).name
            if not name.startswith(("libggml", "libllama", "libtypelay_")):
                continue
            if path_text.endswith(" (deleted)") or not path_text.startswith("/"):
                raise RuntimeError("deleted or anonymous runtime library mapping")
            path = Path(path_text)
            device = tuple(int(part, 16) for part in fields[3].split(":"))
            identity = (path, device, int(fields[4]))
            if name in result and result[name] != identity:
                raise RuntimeError("duplicate runtime library basename")
            result[name] = identity
        return result

    @classmethod
    def _file_mapping_identity(cls, path):
        # Btrfs stat reports a subvolume device, while maps reports the superblock
        # device. Compare maps with maps through a private, non-executable view of
        # the verified file, rather than weakening the device check on that FS.
        with plain_path(path).open("rb") as stream:
            info = os.fstat(stream.fileno())
            with mmap.mmap(stream.fileno(), 1, access=mmap.ACCESS_COPY) as view:
                address = ctypes.addressof(ctypes.c_char.from_buffer(view))
                for fields in cls._maps():
                    start, end = (int(value, 16) for value in fields[0].split("-"))
                    if start <= address < end and len(fields) == 6:
                        current = plain_path(path).stat()
                        if (info.st_dev, info.st_ino) != (current.st_dev, current.st_ino):
                            raise RuntimeError("runtime library file changed during verification")
                        return tuple(int(part, 16) for part in fields[3].split(":")), int(fields[4])
        raise RuntimeError("runtime library verification mapping missing")

    def _verify_loaded(self, expected):
        loaded = self._mapped_modules()
        if set(loaded) != expected:
            raise RuntimeError("unexpected or missing loaded runtime library")
        result = []
        for name, (path, device, inode) in sorted(loaded.items()):
            target = self.adapter if name == self.adapter.name else self.runtime / name
            if path != target:
                raise RuntimeError("loaded runtime library path mismatch")
            if (device, inode) != self._file_mapping_identity(path):
                raise RuntimeError("loaded runtime library file identity changed")
            record = self.adapter_record if name == self.adapter.name else self.records[name]
            verify_file(path, record)
            result.append({"library": name, "path": str(path), "sha256": record["sha256"]})
        return result

    def select_cpu_backend(self):
        if self.selected is not None or self.adapter_library is not None:
            raise RuntimeError("CPU selection already completed")
        baseline = set(DEPENDENCIES)
        best_score = 0
        selected = None
        for record in self.variants:
            library = self._load(self.runtime / record["file"])
            try:
                # Verify before invoking code in a probe, including its dependencies.
                self._verify_loaded(baseline | {record["file"]})
                score_function = library.ggml_backend_score
                score_function.argtypes = []
                score_function.restype = ctypes.c_int
                score = score_function()
                del score_function
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
            self._verify_loaded(set(DEPENDENCIES) | {self.adapter.name})
        except BaseException:
            self._unload(self.adapter_library)
            self.adapter_library = None
            raise
        return self.adapter_library

    def verify_loaded(self, selected):
        if selected != self.selected or self.adapter_library is None:
            raise RuntimeError("selected CPU identity mismatch")
        return self._verify_loaded(set(DEPENDENCIES) | {self.adapter.name, selected.name})

    def close(self):
        # Caller must finish index_close before releasing these owned dlopen refs.
        if self.adapter_library is not None:
            self._unload(self.adapter_library)
            self.adapter_library = None
        for library in reversed(self.dependencies):
            self._unload(library)
        self.dependencies.clear()
