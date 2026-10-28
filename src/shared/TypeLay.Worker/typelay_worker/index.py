"""One bounded Index model, one whole-source request, independent of Hy ABI5."""

from __future__ import annotations

import ctypes
import os
import sys
import time
from pathlib import Path

from .engine import EngineError
from .index_assets import ABI
from .index_assets import DIRECTIONS as DIRECTION_IDS
from .index_cpu import LinuxRuntime
from .protocol import valid_text

MAX_BYTES = 8192
CONTEXT = 4096
MAX_TOKENS = 1024
MEMORY_BYTES = 2684354560
DIRECTIONS = {identity: number for number, identity in enumerate(DIRECTION_IDS, 1)}
STYLES = {"natural": 1, "formal": 2}
CANCEL = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)


class Term(ctypes.Structure):
    _fields_ = [
        ("source_term", ctypes.c_char_p),
        ("source_length", ctypes.c_uint32),
        ("target_term", ctypes.c_char_p),
        ("target_length", ctypes.c_uint32),
    ]


class Metrics(ctypes.Structure):
    _fields_ = [
        (name, ctypes.c_uint32)
        for name in (
            "struct_size",
            "abi_version",
            "source_tokens",
            "prompt_tokens",
            "output_tokens",
            "split_count",
            "roundtrip_ok",
            "forbidden_source_tokens",
            "reserved_spelling_count",
            "user_defined_vocab_count",
            "decode_calls",
            "decode_callbacks",
        )
    ] + [("eog_token", ctypes.c_int32), ("last_decode_code", ctypes.c_int32)]

    def __init__(self):
        super().__init__()
        self.struct_size = ctypes.sizeof(self)
        self.abi_version = ABI
        self.eog_token = -1


def limit_linux_process():
    """Irreversibly bound only this owned worker, before any native load."""
    import resource

    # This worker has not loaded a threaded runtime yet. Refuse an unexpected
    # existing thread instead of claiming a process-wide limit from thread 0.
    if {p.name for p in Path("/proc/self/task").iterdir()} != {str(os.getpid())}:
        raise EngineError("index-resource-limit")
    for name in ("GOMP_CPU_AFFINITY", "OMP_PLACES", "OMP_PROC_BIND"):
        os.environ.pop(name, None)
    soft, hard = resource.getrlimit(resource.RLIMIT_AS)
    bounded_hard = MEMORY_BYTES if hard == resource.RLIM_INFINITY else min(hard, MEMORY_BYTES)
    bounded_soft = bounded_hard if soft == resource.RLIM_INFINITY else min(soft, bounded_hard)
    if bounded_soft <= 0:
        raise EngineError("index-resource-limit")
    resource.setrlimit(resource.RLIMIT_AS, (bounded_soft, bounded_hard))
    allowed = sorted(os.sched_getaffinity(0))
    if not allowed:
        raise EngineError("index-resource-limit")
    selected = set(allowed[:2])
    os.sched_setaffinity(0, selected)
    if (
        resource.getrlimit(resource.RLIMIT_AS) != (bounded_soft, bounded_hard)
        or os.sched_getaffinity(0) != selected
    ):
        raise EngineError("index-resource-limit")
    return selected


def verify_linux_affinity(selected):
    tasks = list(Path("/proc/self/task").iterdir())
    if not tasks or len(tasks) > 1024:
        raise EngineError("index-resource-limit")
    for task in tasks:
        try:
            affinity = os.sched_getaffinity(int(task.name))
        except ProcessLookupError:
            continue  # An already-retired owned thread consumes no CPU.
        if not affinity or not affinity <= selected:
            raise EngineError("index-resource-limit")


class IndexModel:
    def __init__(self, verified: dict):
        self._loader = None
        self._lib = None
        self._handle = ctypes.c_void_p()
        self._allowed_cpus = None
        self.styles = ()
        self.styled_available = False
        self.glossary_available = False
        self.styled_glossary_available = False
        try:
            if sys.platform == "win32":
                from .index_cpu_windows import WindowsRuntime

                self._loader = WindowsRuntime(verified)
            else:
                self._allowed_cpus = limit_linux_process()
                self._loader = LinuxRuntime(verified)
            selected = self._loader.select_cpu_backend()
            self._lib = self._loader.load_adapter()
            # Resolve the complete actual ABI before invoking open. Manifest
            # capabilities alone cannot grant features missing from the binary.
            self._lib.index_open_v1.argtypes = [
                ctypes.c_char_p,
                ctypes.c_char_p,
                ctypes.POINTER(ctypes.c_void_p),
                ctypes.c_void_p,
                ctypes.c_uint32,
            ]
            self._lib.index_open_v1.restype = ctypes.c_int32
            self._lib.index_close.argtypes = [ctypes.c_void_p]
            self._lib.index_close.restype = None
            self._lib.index_translate_v1.argtypes = [
                ctypes.c_void_p,
                ctypes.c_uint32,
                ctypes.c_uint32,
                ctypes.c_uint32,
                ctypes.POINTER(Term),
                ctypes.c_uint32,
                ctypes.c_void_p,
                ctypes.c_uint32,
                ctypes.c_void_p,
                ctypes.c_uint32,
                ctypes.POINTER(ctypes.c_uint32),
                ctypes.POINTER(Metrics),
                CANCEL,
                ctypes.c_void_p,
                ctypes.c_void_p,
                ctypes.c_uint32,
            ]
            self._lib.index_translate_v1.restype = ctypes.c_int32
            if (
                verified.get("styles") != list(STYLES)
                or verified.get("glossary") is not True
                or verified.get("styled_glossary") is not True
            ):
                raise EngineError("index-capability-unavailable")
            error = ctypes.create_string_buffer(512)
            code = self._lib.index_open_v1(
                os.fsencode(selected),
                os.fsencode(verified["model_path"]),
                ctypes.byref(self._handle),
                error,
                len(error),
            )
            if code or not self._handle.value:
                raise EngineError("index-model-load")
            self._loader.verify_loaded(selected)
            if self._allowed_cpus is not None:
                verify_linux_affinity(self._allowed_cpus)
            self.styles = tuple(STYLES)
            self.styled_available = True
            self.glossary_available = True
            self.styled_glossary_available = True
        except BaseException:
            self.close()
            raise

    def direction(self, identity: str):
        if identity not in DIRECTIONS:
            raise EngineError("language-unavailable")
        return IndexDirection(self, DIRECTIONS[identity])

    def close(self):
        try:
            if self._handle.value and self._lib is not None:
                self._lib.index_close(self._handle)
                self._handle = ctypes.c_void_p()
        finally:
            self._lib = None
            if self._loader is not None:
                self._loader.close()
                self._loader = None

    def translate(self, text, direction, deadline, style=0, flags=0, terms=()):
        if time.monotonic() >= deadline:
            raise EngineError("request-timeout")
        if not self._handle.value or self._lib is None:
            raise EngineError("index-model-closed")
        if not valid_text(text):
            raise EngineError("invalid-request")
        source = text.encode("utf-8", "strict")
        if not 1 <= len(source) <= MAX_BYTES:
            raise EngineError("segment-too-long")
        metrics, length = Metrics(), ctypes.c_uint32()
        output, error = ctypes.create_string_buffer(MAX_BYTES), ctypes.create_string_buffer(512)

        @CANCEL
        def cancelled(_):
            try:
                return int(time.monotonic() >= deadline)
            except Exception:
                return 1

        if len(terms) > 64:
            raise EngineError("invalid-request")
        encoded = [(a.encode("utf-8", "strict"), b.encode("utf-8", "strict")) for a, b in terms]
        pairs = (Term * len(encoded))(*(Term(a, len(a), b, len(b)) for a, b in encoded))
        code = self._lib.index_translate_v1(
            self._handle,
            direction,
            style,
            flags,
            pairs,
            len(pairs),
            source,
            len(source),
            output,
            MAX_BYTES,
            ctypes.byref(length),
            ctypes.byref(metrics),
            cancelled,
            None,
            error,
            len(error),
        )
        if self._allowed_cpus is not None:
            verify_linux_affinity(self._allowed_cpus)
        if time.monotonic() >= deadline:
            raise EngineError("request-timeout")
        if code:
            raise EngineError(
                {
                    3: "index-literal-unsafe",
                    4: "segment-too-long",
                    5: "decoding-limit",
                    6: "request-timeout",
                }.get(code, "index-inference-failed")
            )
        if (
            not 0 < length.value <= MAX_BYTES
            or metrics.eog_token not in (248044, 248046)
            or not metrics.roundtrip_ok
            or metrics.forbidden_source_tokens
            or metrics.output_tokens >= MAX_TOKENS
            or metrics.prompt_tokens + MAX_TOKENS > CONTEXT
        ):
            raise EngineError("index-incomplete-output")
        try:
            result = output.raw[: length.value].decode("utf-8", "strict")
        except UnicodeError:
            raise EngineError("invalid-output") from None
        if not valid_text(result):
            raise EngineError("invalid-output")
        return result


class IndexDirection:
    def __init__(self, model: IndexModel, direction: int):
        self._model, self._direction = model, direction

    def request(
        self,
        original,
        deadline,
        style=None,
        *,
        preserved_literals=(),
        preserve_linebreaks=False,
        preserve_whitespace=False,
    ):
        if not valid_text(original) or (
            style is not None and (type(style) is not str or style not in STYLES)
        ):
            raise EngineError("invalid-request")
        if len(original.encode("utf-8", "strict")) > MAX_BYTES:
            raise EngineError("segment-too-long")
        flags = (
            int(bool(preserved_literals))
            | (int(preserve_linebreaks) << 1)
            | (int(preserve_whitespace) << 2)
        )
        return IndexRequest(
            self._model, self._direction, original, deadline, STYLES.get(style, 0), flags
        )


class IndexRequest:
    def __init__(self, model, direction, original, deadline, style, flags):
        self._model, self._direction, self._original = model, direction, original
        self._deadline, self._style, self._flags = deadline, style, flags

    def translate(self, text):
        return self.translate_glossary(text, ())

    def translate_glossary(self, text, terms):
        # This backend never silently turns a full request into independently
        # translated fragments or accepts replacement source text.
        if text != self._original:
            raise EngineError("index-whole-source-required")
        return self._model.translate(
            text, self._direction, self._deadline, self._style, self._flags, terms
        )
