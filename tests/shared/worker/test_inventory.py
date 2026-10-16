"""Deterministic protocol/structure tests. Does NOT substitute for actual model inference."""

import hashlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import run_worker
from typelay_worker.engine import EngineError, LocalEngine
from typelay_worker.protocol import read_frame, write_frame


class InventoryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.entries = []
        for pair in ("zh_en", "en_zh"):
            for name in ("model/model.bin", "sentencepiece.model"):
                rel = f"{pair}/{name}"
                path = self.root / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"inventory-test-bytes-not-a-model")
                self.entries.append(
                    {"path": rel, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                )
        self.save()

    def tearDown(self):
        self.tmp.cleanup()

    def save(self, **fields):
        (self.root / "installed.json").write_text(
            json.dumps({"schema": 1, "files": self.entries, **fields}), encoding="utf8"
        )

    def test_tampered_file(self):
        (self.root / self.entries[0]["path"]).write_bytes(b"changed")
        with self.assertRaisesRegex(EngineError, "^model-(inventory|integrity)$"):
            LocalEngine(self.root)

    def test_path_escape(self):
        self.entries[0]["path"] = "../escape"
        self.save()
        with self.assertRaisesRegex(EngineError, "^model-(inventory|integrity)$"):
            LocalEngine(self.root)

    def test_duplicate(self):
        self.entries[0] = self.entries[1]
        self.save()
        with self.assertRaisesRegex(EngineError, "^model-(inventory|integrity)$"):
            LocalEngine(self.root)

    def test_missing_required(self):
        self.entries = self.entries[:3]
        self.save()
        with self.assertRaisesRegex(EngineError, "^model-(inventory|integrity)$"):
            LocalEngine(self.root)

    def test_no_inventory(self):
        (self.root / "installed.json").unlink()
        with self.assertRaisesRegex(EngineError, "^model-(inventory|integrity)$"):
            LocalEngine(self.root)

    def test_incomplete_or_invalid_direction_claim_is_refused_before_model_loading(self):
        for directions in (
            None,
            [],
            ["zh_en"],
            ["en_zh"],
            ["zh_en", "fr_en"],
            ["zh_en", "en_zh", "zh_en"],
            ["zh_en", "zh_en"],
            "zh_en",
            [{"id": "zh_en"}],
        ):
            with self.subTest(directions=directions):
                self.save(directions=directions)
                with self.assertRaisesRegex(EngineError, "^model-inventory$"):
                    LocalEngine(self.root)

    def test_missing_or_unknown_legacy_schema_never_loads_or_advertises_directions(self):
        for fields in ({}, {"schema": 2}):
            with self.subTest(fields=fields):
                (self.root / "installed.json").write_text(
                    json.dumps({"files": self.entries, **fields}), encoding="utf8"
                )
                result, response, loaded = self.start_worker()
                self.assertEqual(result, 2)
                self.assertEqual(loaded, [])
                self.assertIsNone(response)

    def test_schema1_hello_advertises_the_complete_bundle_only_after_both_native_loads(self):
        for fields in ({}, {"directions": ["en_zh", "zh_en"]}):
            with self.subTest(fields=fields):
                self.save(**fields)
                result, response, loaded = self.start_worker()
                self.assertEqual(result, 0)
                self.assertEqual(loaded, ["zh_en", "en_zh"])
                self.assertEqual(response["status"], "ready")
                self.assertEqual(response["directions"], ["zh_en", "en_zh"])

    def test_second_native_model_load_failure_never_advertises_any_direction(self):
        result, response, loaded = self.start_worker(fail_load="en_zh")
        self.assertEqual(result, 2)
        self.assertEqual(loaded, ["zh_en", "en_zh"])
        self.assertIsNone(response)

    def test_legacy_tampered_inventory_never_loads_a_model_or_advertises_directions(self):
        (self.root / self.entries[0]["path"]).write_bytes(b"changed")
        result, response, loaded = self.start_worker()
        self.assertEqual(result, 2)
        self.assertEqual(loaded, [])
        self.assertIsNone(response)

    def start_worker(self, fail_load=None):
        # Control native dependency constructors at their external API, never replace
        # the engine. This proves readiness ordering and protocol, not real inference.
        loaded = []

        def load_model(path, **options):
            pair = Path(path).parent.name
            loaded.append(pair)
            self.assertEqual(options["device"], "cpu")
            self.assertEqual(options["compute_type"], "int8")
            if pair == fail_load:
                raise RuntimeError("private native failure")
            return object()

        builder = SimpleNamespace()
        builder.from_all_languages = lambda: builder
        builder.with_low_accuracy_mode = lambda: builder
        builder.build = lambda: object()
        dependencies = {
            "lingua": SimpleNamespace(LanguageDetectorBuilder=builder),
            "sentencepiece": SimpleNamespace(SentencePieceProcessor=lambda **_: object()),
            "ctranslate2": SimpleNamespace(Translator=load_model),
        }
        incoming, outgoing = io.BytesIO(), io.BytesIO()
        write_frame(incoming, {"id": 1, "op": "hello"})
        incoming.seek(0)
        with (
            patch.dict(sys.modules, dependencies),
            patch.object(sys, "argv", ["run_worker.py", "--models", str(self.root)]),
            patch.object(sys, "stdin", SimpleNamespace(buffer=incoming)),
            patch.object(sys, "stdout", SimpleNamespace(buffer=outgoing)),
        ):
            result = run_worker.main()
        outgoing.seek(0)
        return result, read_frame(outgoing) if outgoing.getvalue() else None, loaded
