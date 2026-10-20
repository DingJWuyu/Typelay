#!/usr/bin/env python3
"""Isolated stage-1 NDJSON fixture. Not TypeLay's production wire protocol.
No server, network, prompt argv, prompt files, queues or concurrent requests.
Cancellation belongs to the owning supervisor: terminate and reap this process.
"""

import argparse
import ctypes
import hashlib
import json
import os
import re
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
# -I excludes script/PYTHONPATH entries; add only the two checked-in source roots.
sys.dont_write_bytecode = True
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT.parents[4] / "src/shared/TypeLay.Worker"))
from typelay_worker.hy_cpu_windows import WindowsRuntime  # noqa: E402
from verify_assets import verify  # noqa: E402

MAX_WIRE = 16384
MAX_TEXT = 8192


class Metrics(ctypes.Structure):
    _fields_ = [
        (k, ctypes.c_uint32)
        for k in (
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
        self.abi_version = 5

    def data(self):
        return {name: getattr(self, name) for name, _ in self._fields_}


CANCEL = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)


def emit(obj):
    data = (
        json.dumps(obj, ensure_ascii=False, separators=(",", ":")).encode("utf-8", "strict") + b"\n"
    )
    if len(data) > MAX_WIRE:
        raise ValueError("output wire bound")
    sys.stdout.buffer.write(data)
    sys.stdout.buffer.flush()


def run():
    startup_start = time.monotonic()
    if os.name == "nt":
        import msvcrt

        msvcrt.setmode(sys.stdin.fileno(), os.O_BINARY)
        msvcrt.setmode(sys.stdout.fileno(), os.O_BINARY)
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    for name in ("assets", "runtime", "adapter"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    if any(not path.is_absolute() for path in (args.assets, args.runtime, args.adapter)):
        raise ValueError("absolute fixture paths required")
    if os.name != "nt":
        raise RuntimeError("Windows x64 required")
    _, verified = verify(args.assets, args.runtime, args.adapter)
    verification_seconds = round(time.monotonic() - startup_start, 6)
    loader = WindowsRuntime(verified)
    scores = []
    selection_start = time.monotonic()
    cpu_path = loader.select_cpu_backend(on_score=scores.append)
    cpu_selection = {
        "policy": "b11717-official-score-max-positive-cpu-only-v1",
        "scores": [
            {"library": r["file"], "bytes": r["bytes"], "sha256": r["sha256"], "score": r["score"]}
            for r in scores
        ],
        "selected": cpu_path.name,
        "selected_score": next(r["score"] for r in scores if r["file"] == cpu_path.name),
        "selection_seconds": round(time.monotonic() - selection_start, 6),
        "runtime_lock_sha256": verified["cpu_runtime"]["lock_sha256"],
    }
    lib = loader.load_adapter()
    lib.hy_open_v5.argtypes = [
        ctypes.c_char_p,
        ctypes.c_char_p,
        ctypes.POINTER(ctypes.c_void_p),
        ctypes.c_void_p,
        ctypes.c_uint32,
    ]
    lib.hy_open_v5.restype = ctypes.c_int32
    lib.hy_close.argtypes = [ctypes.c_void_p]
    lib.hy_close.restype = None
    lib.hy_translate.argtypes = [
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
    lib.hy_translate.restype = ctypes.c_int32
    lib.hy_audit.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_uint32,
        ctypes.POINTER(Metrics),
        ctypes.c_void_p,
        ctypes.c_uint32,
    ]
    lib.hy_audit.restype = ctypes.c_int32
    handle = ctypes.c_void_p()
    error = ctypes.create_string_buffer(512)
    open_start = time.monotonic()
    rc = lib.hy_open_v5(
        os.fsencode(cpu_path),
        os.fsencode(verified["model_path"]),
        ctypes.byref(handle),
        error,
        len(error),
    )
    if rc:
        emit({"event": "open_error", "code": rc, "error": error.value.decode("utf-8", "strict")})
        loader.close()
        return 2
    open_seconds = round(time.monotonic() - open_start, 6)
    try:
        emit(
            {
                "event": "ready",
                "abi": 5,
                "asset_verification_seconds": verification_seconds,
                "model_open_seconds": open_seconds,
                "adapter_sha256": verified["adapter_sha256"],
                "loaded_runtime": loader.verify_loaded(cpu_path),
                "model_sha256": verified["model_full_hash"],
                "runtime_sha256": verified["runtime_archive_full_hash"],
                "template_sha256": verified["template_sha256"],
                "runtime_members_verified": verified["runtime_regular_members_verified"],
                "cpu_backend": cpu_selection["selected"],
                "cpu_backend_score": cpu_selection["selected_score"],
                "cpu_selection": cpu_selection,
                "startup_seconds": round(time.monotonic() - startup_start, 6),
                "ctx": 1024,
                "threads": 2,
                "max_new_tokens": 192,
                "sampler": "greedy",
                "pid": os.getpid(),
            }
        )
        while True:
            line = sys.stdin.buffer.readline(MAX_WIRE + 1)
            if not line:
                return 0
            if len(line) > MAX_WIRE or not line.endswith(b"\n"):
                emit({"event": "protocol_error", "error": "request wire bound or missing newline"})
                return 2
            rid = None
            try:
                request = json.loads(line.decode("utf-8", "strict"))
                if not isinstance(request, dict):
                    raise ValueError("object required")
                rid = request.get("id")
                if not isinstance(rid, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", rid):
                    raise ValueError("invalid id")
                op = request.get("op")
                expected = (
                    {"id", "op", "text", "direction"} if op == "translate" else {"id", "op", "text"}
                )
                if op not in ("translate", "audit") or set(request) != expected:
                    raise ValueError("unsupported operation or fields")
                if not isinstance(request["text"], str):
                    raise ValueError("text string required")
                raw = request["text"].encode("utf-8", "strict")
                if not 1 <= len(raw) <= MAX_TEXT:
                    raise ValueError("source byte bound")
                direction = {
                    "zh-ja": 1,
                    "ja-zh": 2,
                    "zh-ko": 3,
                    "ko-zh": 4,
                    "zh-fr": 5,
                    "fr-zh": 6,
                    "zh-es": 7,
                    "es-zh": 8,
                    "zh-de": 9,
                    "de-zh": 10,
                    "zh-pt": 11,
                    "pt-zh": 12,
                }.get(request.get("direction"))
                if op == "translate" and direction is None:
                    raise ValueError("unsupported direction")
            except (ValueError, TypeError, UnicodeError):
                emit(
                    {
                        "event": "error",
                        "id": rid
                        if isinstance(rid, str) and re.fullmatch(r"[A-Za-z0-9_-]{1,64}", rid)
                        else None,
                        "code": 1,
                        "error": "invalid bounded request",
                    }
                )
                continue
            original_hash = hashlib.sha256(raw).hexdigest()
            canonical = raw.replace(b"\r\n", b"\n")
            source_audit = {
                "policy": "typelay-crlf-to-lf-only-v1",
                "original_bytes": len(raw),
                "original_sha256": original_hash,
                "canonical_bytes": len(canonical),
                "canonical_sha256": hashlib.sha256(canonical).hexdigest(),
                "crlf_count": raw.count(b"\r\n"),
                "lf_count": raw.count(b"\n"),
                "standalone_cr_count": canonical.count(b"\r"),
            }
            metrics = Metrics()
            error.value = b""
            start = time.monotonic()
            deadline = start + 60
            notified = False

            @CANCEL
            def check(_):
                nonlocal notified
                try:
                    if metrics.decode_callbacks and not notified:
                        emit(
                            {
                                "event": "decode_entered",
                                "id": rid,
                                "decode_callbacks": metrics.decode_callbacks,
                            }
                        )
                        notified = True
                    return int(time.monotonic() >= deadline)
                except Exception:
                    return 1

            if op == "audit":
                rc = lib.hy_audit(handle, raw, len(raw), ctypes.byref(metrics), error, len(error))
                length = 0
                text = None
            else:
                output = ctypes.create_string_buffer(MAX_TEXT)
                outlen = ctypes.c_uint32(0)
                rc = lib.hy_translate(
                    handle,
                    direction,
                    raw,
                    len(raw),
                    output,
                    MAX_TEXT,
                    ctypes.byref(outlen),
                    ctypes.byref(metrics),
                    check,
                    None,
                    error,
                    len(error),
                )
                length = outlen.value
                if length > MAX_TEXT or (rc and length):
                    raise RuntimeError("ABI output byte bound or uncleared length")
                if time.monotonic() > deadline:
                    rc = 6
                    length = 0
                    error.value = b"cancelled or deadline reached"
                text = output.raw[:length].decode("utf-8", "strict") if not rc else None
            if hashlib.sha256(raw).hexdigest() != original_hash:
                raise RuntimeError("original source mutated")
            source_audit["token_roundtrip_sha256"] = (
                source_audit["canonical_sha256"] if metrics.roundtrip_ok else None
            )
            response = {
                "source_audit": source_audit,
                "event": "result",
                "id": rid,
                "operation": op,
                "code": rc,
                "metrics": metrics.data(),
                "elapsed_seconds": round(time.monotonic() - start, 6),
            }
            if rc:
                response["error"] = error.value.decode("utf-8", "strict")
            elif op == "translate":
                response["text"] = text
                response["complete"] = True
                response["completion_reason"] = "eog"
            else:
                response["validated_only"] = True
            try:
                emit(response)
            except ValueError:
                emit(
                    {
                        "event": "error",
                        "id": rid,
                        "code": 5,
                        "error": "serialized output wire bound",
                    }
                )
    finally:
        lib.hy_close(handle)
        loader.close()


if __name__ == "__main__":
    try:
        sys.exit(run())
    except Exception as exc:
        emit(
            {
                "event": "fatal",
                "error": "worker startup or protocol failure",
                "exception_type": type(exc).__name__,
            }
        )
        sys.exit(2)
