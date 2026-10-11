"""Bounded little-endian frames, matching TypeLay.Protocol.Wire."""

from __future__ import annotations

import json
import struct
from typing import Any, BinaryIO

MAX_FRAME = 256 * 1024
MAX_TEXT_UNITS = 8192


def read_exact(stream: BinaryIO, count: int) -> bytes:
    result = bytearray()
    while len(result) < count:
        chunk = stream.read(count - len(result))
        if not chunk:
            raise EOFError("pipe-closed")
        result.extend(chunk)
    return bytes(result)


def read_frame(stream: BinaryIO) -> dict[str, Any]:
    count = struct.unpack("<i", read_exact(stream, 4))[0]
    if not 0 < count <= MAX_FRAME:
        raise ValueError("frame-size")
    value = json.loads(read_exact(stream, count).decode("utf-8"))
    if not isinstance(value, dict):
        raise ValueError("frame-object")
    return value


def write_frame(stream: BinaryIO, value: dict[str, Any]) -> None:
    data = json.dumps(value, ensure_ascii=True, separators=(",", ":"), allow_nan=False).encode(
        "utf-8"
    )
    if not 0 < len(data) <= MAX_FRAME:
        raise ValueError("frame-size")
    stream.write(struct.pack("<i", len(data)))
    stream.write(data)
    stream.flush()


def valid_text(text: object, *, empty: bool = False) -> bool:
    if not isinstance(text, str) or "\0" in text or (not empty and not text.strip()):
        return False
    try:
        return len(text.encode("utf-16-le")) // 2 <= MAX_TEXT_UNITS
    except UnicodeError:
        return False
