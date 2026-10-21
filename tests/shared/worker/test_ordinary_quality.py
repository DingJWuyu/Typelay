"""Diagnostic transport coverage, not real inference or language-quality evidence."""

import asyncio
import io
import unittest
from types import SimpleNamespace

import ordinary_quality
import styled_quality
from typelay_worker.protocol import read_frame, write_frame


class OrdinaryDiagnosticTests(unittest.IsolatedAsyncioTestCase):
    async def test_frozen_eight_sources_are_sent_once_without_style(self):
        report = ordinary_quality.prepare_report()
        self.assertEqual(len(report["cases"]), 8)
        self.assertEqual(report["maximumTranslationRequests"], 8)
        self.assertEqual(report["configuredLimits"]["sampling"], "greedy")
        self.assertEqual(report["quality"], "not-run")
        wire = io.BytesIO()
        for i in range(1, 9):
            write_frame(
                wire,
                dict(id=i, status="translated", text="transport fixture", code="", elapsedMs=0),
            )
        reader = asyncio.StreamReader()
        reader.feed_data(wire.getvalue())
        reader.feed_eof()

        class Input(io.BytesIO):
            async def drain(self):
                pass

        writer = Input()
        await ordinary_quality.collect_cases(SimpleNamespace(stdin=writer, stdout=reader), report)
        writer.seek(0)
        for i, case in enumerate(report["cases"], 1):
            source, target = case["direction"].split("_")
            self.assertEqual(
                read_frame(writer),
                dict(id=i, op="translate", text=case["text"], source=source, target=target),
            )
            self.assertEqual(case["ordinary"]["text"], "transport fixture")
            self.assertTrue(all(v is None for v in case["manualReview"]["ordinary"].values()))
        self.assertEqual(writer.read(), b"")
        self.assertEqual(report["quality"], "not-reviewed")

    async def test_existing_styled_collection_keeps_sixteen_styled_requests(self):
        report = styled_quality.prepare_report(include_holdout=True)
        wire = io.BytesIO()
        for i in range(1, 17):
            write_frame(
                wire,
                dict(id=i, status="translated", text="transport fixture", code="", elapsedMs=0),
            )
        reader = asyncio.StreamReader()
        reader.feed_data(wire.getvalue())
        reader.feed_eof()

        class Input(io.BytesIO):
            async def drain(self):
                pass

        writer = Input()
        await styled_quality.collect_styled_cases(
            SimpleNamespace(stdin=writer, stdout=reader), report
        )
        writer.seek(0)
        for case in report["cases"]:
            for style in ("natural", "formal"):
                payload = read_frame(writer)
                self.assertEqual(payload["op"], "translate-styled-v1")
                self.assertEqual(payload["style"], style)
                self.assertEqual(payload["text"], case["text"])
        self.assertEqual(writer.read(), b"")
        self.assertEqual(len(report["requests"]), 16)

    async def test_refusal_is_retained_and_stops_without_retry(self):
        report = ordinary_quality.prepare_report()
        wire = io.BytesIO()
        write_frame(wire, dict(id=1, status="error", code="decoding-limit", elapsedMs=0))
        reader = asyncio.StreamReader()
        reader.feed_data(wire.getvalue())
        reader.feed_eof()

        class Input(io.BytesIO):
            async def drain(self):
                pass

        writer = Input()
        with self.assertRaises(ValueError):
            await ordinary_quality.collect_cases(
                SimpleNamespace(stdin=writer, stdout=reader), report
            )
        self.assertEqual(len(report["requests"]), 1)
        self.assertEqual(report["cases"][0]["ordinary"]["code"], "decoding-limit")
        self.assertIsNone(report["cases"][1]["ordinary"])
