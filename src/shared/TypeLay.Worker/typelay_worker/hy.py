"""One fixed Hy ABI5 CPU model; English requires its separate versioned capability."""

from __future__ import annotations

import ctypes
import os
import sys
import time

from .engine import EngineError
from .hy_assets import ABI, ENGLISH_DIRECTIONS, ENGLISH_EXPORT, STYLED_GLOSSARY_EXPORT
from .hy_assets import DIRECTIONS as DIRECTION_IDS
from .hy_cpu import LinuxRuntime
from .protocol import valid_text

MAX_BYTES = 8192
DIRECTIONS = {identity: number for number, identity in enumerate(DIRECTION_IDS, 1)}
ENGLISH_DIRECTION_IDS = {identity: number for number, identity in enumerate(ENGLISH_DIRECTIONS, 13)}
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


class HyModel:
    def __init__(self, verified: dict):
        self._loader = None
        self._lib = None
        self.styled_glossary_available = False
        self.english_available = False
        self._handle = ctypes.c_void_p()
        try:
            if sys.platform == "win32":
                from .hy_cpu_windows import WindowsRuntime

                self._loader = WindowsRuntime(verified)
            else:
                self._loader = LinuxRuntime(verified)
            selected = self._loader.select_cpu_backend()
            self._lib = self._loader.load_adapter()
            self._lib.hy_open_v5.argtypes = [
                ctypes.c_char_p,
                ctypes.c_char_p,
                ctypes.POINTER(ctypes.c_void_p),
                ctypes.c_void_p,
                ctypes.c_uint32,
            ]
            self._lib.hy_open_v5.restype = ctypes.c_int32
            self._lib.hy_close.argtypes = [ctypes.c_void_p]
            self._lib.hy_close.restype = None
            self._lib.hy_translate.argtypes = [
                ctypes.c_void_p,
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
            self._lib.hy_translate.restype = ctypes.c_int32
            self._lib.hy_translate_styled_v5.argtypes = [
                *self._lib.hy_translate.argtypes[:2],
                ctypes.c_uint32,
                *self._lib.hy_translate.argtypes[2:],
            ]
            self._lib.hy_translate_styled_v5.restype = ctypes.c_int32
            self._lib.hy_translate_glossary_v5.argtypes = [
                *self._lib.hy_translate.argtypes[:2],
                ctypes.POINTER(Term),
                ctypes.c_uint32,
                *self._lib.hy_translate.argtypes[2:],
            ]
            self._lib.hy_translate_glossary_v5.restype = ctypes.c_int32
            if verified.get("styled_glossary") is True:
                # The verified manifest is not proof that the actual symbol exists.
                combined = getattr(self._lib, STYLED_GLOSSARY_EXPORT)
                combined.argtypes = [
                    *self._lib.hy_translate.argtypes[:2],
                    ctypes.c_uint32,
                    ctypes.POINTER(Term),
                    ctypes.c_uint32,
                    *self._lib.hy_translate.argtypes[2:],
                ]
                combined.restype = ctypes.c_int32
                self.styled_glossary_available = True
            if verified.get("english_translation_version") == 1:
                english = getattr(self._lib, ENGLISH_EXPORT)
                english.argtypes = self._lib.hy_translate.argtypes
                english.restype = ctypes.c_int32
                self.english_available = True
            error = ctypes.create_string_buffer(512)
            code = self._lib.hy_open_v5(
                os.fsencode(selected),
                os.fsencode(verified["model_path"]),
                ctypes.byref(self._handle),
                error,
                len(error),
            )
            if code or not self._handle.value:
                raise EngineError("hy-model-load")
            self._loader.verify_loaded(selected)
        except Exception:
            self.close()
            raise

    def direction(self, identity: str):
        if identity in ENGLISH_DIRECTION_IDS:
            if not self.english_available:
                raise EngineError("hy-english-unavailable")
            return HyDirection(self, ENGLISH_DIRECTION_IDS[identity])
        if identity not in DIRECTIONS:
            raise EngineError("language-unavailable")
        return HyDirection(self, DIRECTIONS[identity])

    def close(self):
        try:
            if self._handle.value and self._lib is not None:
                self._lib.hy_close(self._handle)
                self._handle = ctypes.c_void_p()
        finally:
            self._lib = None
            if self._loader is not None:
                self._loader.close()
                self._loader = None

    def translate(
        self, text: str, direction: int, deadline: float, style: int | None = None, terms=()
    ) -> str:
        if time.monotonic() >= deadline:
            raise EngineError("request-timeout")
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

        english = direction in ENGLISH_DIRECTION_IDS.values()
        if english and (not self.english_available or style is not None or terms):
            raise EngineError("hy-english-operation-unavailable")
        translate = self._lib.hy_translate if style is None else self._lib.hy_translate_styled_v5
        if english:
            translate = getattr(self._lib, ENGLISH_EXPORT)
        prefix = (self._handle, direction) if style is None else (self._handle, direction, style)
        if terms:
            if style is not None and not self.styled_glossary_available:
                raise EngineError("glossary-combination-unavailable")
            encoded = [(a.encode("utf-8", "strict"), b.encode("utf-8", "strict")) for a, b in terms]
            pairs = (Term * len(encoded))(*(Term(a, len(a), b, len(b)) for a, b in encoded))
            translate = self._lib.hy_translate_glossary_v5
            prefix = (self._handle, direction, pairs, len(pairs))
            if style is not None:
                translate = getattr(self._lib, STYLED_GLOSSARY_EXPORT)
                prefix = (self._handle, direction, style, pairs, len(pairs))
        code = translate(
            *prefix,
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
        if time.monotonic() >= deadline:
            raise EngineError("request-timeout")
        if code:
            raise EngineError(
                {
                    3: "hy-literal-unsafe",
                    4: "segment-too-long",
                    5: "decoding-limit",
                    6: "request-timeout",
                }.get(code, "hy-inference-failed")
            )
        if (
            not 0 < length.value <= MAX_BYTES
            or metrics.eog_token < 0
            or not metrics.roundtrip_ok
            or metrics.forbidden_source_tokens
            or metrics.output_tokens >= 192
            or metrics.prompt_tokens + 192 > 1024
        ):
            raise EngineError("hy-incomplete-output")
        try:
            result = output.raw[: length.value].decode("utf-8", "strict")
        except UnicodeError:
            raise EngineError("invalid-output") from None
        if not valid_text(result):
            raise EngineError("invalid-output")
        return result


class HyDirection:
    def __init__(self, model: HyModel, direction: int):
        self._model, self._direction = model, direction

    def request(self, original: str, deadline: float, style: str | None = None):
        if self._direction in ENGLISH_DIRECTION_IDS.values() and style is not None:
            raise EngineError("style-unavailable")
        if style is not None and (type(style) is not str or style not in STYLES):
            raise EngineError("invalid-request")
        # Bound the entire input before protected fragments are split. Derive no
        # replacement source here: the ABI applies CRLF -> LF only per segment.
        if len(original.encode("utf-8", "strict")) > MAX_BYTES:
            raise EngineError("segment-too-long")
        if "\r" in original.replace("\r\n", "\n"):
            raise EngineError("hy-literal-unsafe")
        return HyRequest(
            self._model, self._direction, deadline, STYLES[style] if style is not None else None
        )


class HyRequest:
    def __init__(self, model: HyModel, direction: int, deadline: float, style: int | None):
        self._model, self._direction, self._deadline, self._style = (
            model,
            direction,
            deadline,
            style,
        )

    def translate(self, text: str) -> str:
        return self._model.translate(text, self._direction, self._deadline, self._style)

    def translate_glossary(self, text: str, terms) -> str:
        return self._model.translate(text, self._direction, self._deadline, self._style, terms)
