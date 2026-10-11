"""Deterministic protocol/structure tests. Does NOT substitute for actual model inference."""

import io
import struct
import unittest

from typelay_worker.protocol import MAX_FRAME, read_exact, read_frame, valid_text, write_frame


class ProtocolTests(unittest.TestCase):
    def test_round_trip_unicode(self):
        value = {"id": 1, "op": "translate", "text": "你好🙂\r\nline"}
        stream = io.BytesIO()
        write_frame(stream, value)
        stream.seek(0)
        self.assertEqual(read_frame(stream), value)

    def test_byte_order(self):
        stream = io.BytesIO()
        write_frame(stream, {"id": 1})
        self.assertEqual(struct.unpack("<i", stream.getvalue()[:4])[0], len(stream.getvalue()) - 4)

    def test_short_reads(self):
        class Partial(io.BytesIO):
            def read(self, n=-1):
                return super().read(min(n, 1))

        self.assertEqual(read_exact(Partial(b"abc"), 3), b"abc")

    def test_truncated(self):
        for data in (b"", b"abc", struct.pack("<i", 8) + b"{}"):
            with self.subTest(data=data), self.assertRaises(EOFError):
                read_frame(io.BytesIO(data))

    def test_bad_sizes(self):
        for length in (-1, 0, MAX_FRAME + 1, 2**31 - 1):
            with self.subTest(length=length), self.assertRaises(ValueError):
                read_frame(io.BytesIO(struct.pack("<i", length)))

    def test_nonobject(self):
        with self.assertRaises(ValueError):
            read_frame(io.BytesIO(struct.pack("<i", 2) + b"[]"))

    def test_invalid_utf8(self):
        with self.assertRaises(UnicodeError):
            read_frame(io.BytesIO(struct.pack("<i", 1) + b"\xff"))

    def test_invalid_json(self):
        with self.assertRaises(ValueError):
            read_frame(io.BytesIO(struct.pack("<i", 1) + b"{"))

    def test_oversized_write(self):
        with self.assertRaises(ValueError):
            write_frame(io.BytesIO(), {"text": "x" * MAX_FRAME})

    def test_no_nan_write(self):
        with self.assertRaises(ValueError):
            write_frame(io.BytesIO(), {"value": float("nan")})

    def test_null_and_nontext(self):
        for value in (None, 1, True, [], {}, "a\0b"):
            with self.subTest(value=value):
                self.assertFalse(valid_text(value))

    def test_empty_policy(self):
        self.assertFalse(valid_text(" "))
        self.assertTrue(valid_text("", empty=True))

    def test_utf16_limit(self):
        self.assertTrue(valid_text("🙂" * 4096))
        self.assertFalse(valid_text("🙂" * 4097))

    def test_unpaired_surrogates(self):
        self.assertFalse(valid_text("\ud800"))
        self.assertFalse(valid_text("\udfff"))

    def test_two_frames(self):
        stream = io.BytesIO()
        write_frame(stream, {"id": 1})
        write_frame(stream, {"id": 2})
        stream.seek(0)
        self.assertEqual(read_frame(stream)["id"], 1)
        self.assertEqual(read_frame(stream)["id"], 2)
