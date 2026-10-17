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
        for fields in ({}, {"schema": 2}, {"schema": 3}, {"schema": True}):
            with self.subTest(fields=fields):
                (self.root / "installed.json").write_text(
                    json.dumps({"files": self.entries, **fields}), encoding="utf8"
                )
                result, response, loaded = self.start_worker()
                self.assertEqual(result, 2)
                self.assertEqual(loaded, [])
                self.assertIsNone(response)

    def test_schema2_invalid_selections_never_load_or_advertise_directions(self):
        for selected in (None, [], ["zh_en", "zh_en"], ["fr_en"], ["zh_en", None], "zh_en"):
            with self.subTest(selected=selected):
                self.save(schema=2, directions=selected)
                result, response, loaded = self.start_worker()
                self.assertEqual((result, response, loaded), (2, None, []))

    def test_schema2_cannot_claim_files_from_an_unselected_direction(self):
        self.save(schema=2, directions=["zh_en"])
        result, response, loaded = self.start_worker()
        self.assertEqual((result, response, loaded), (2, None, []))

    def test_schema2_cannot_load_an_uninventoried_model_file(self):
        (self.root / "zh_en/model/config.json").write_text("{}", encoding="utf8")
        self.save(schema=2, directions=["zh_en", "en_zh"])
        result, response, loaded = self.start_worker()
        self.assertEqual((result, response, loaded), (2, None, []))

    def test_schema2_rejects_a_noncanonical_alias_even_when_its_hash_matches(self):
        self.entries[0]["path"] = "zh_en/model/../model/model.bin"
        self.save(schema=2, directions=["zh_en", "en_zh"])
        result, response, loaded = self.start_worker()
        self.assertEqual((result, response, loaded), (2, None, []))

    def test_schema1_hello_advertises_the_complete_bundle_only_after_both_native_loads(self):
        for fields in ({}, {"directions": ["en_zh", "zh_en"]}):
            with self.subTest(fields=fields):
                self.save(**fields)
                result, response, loaded = self.start_worker()
                self.assertEqual(result, 0)
                self.assertEqual(loaded, ["zh_en", "en_zh"])
                self.assertEqual(response["status"], "ready")
                self.assertEqual(response["directions"], ["zh_en", "en_zh"])

    def test_schema2_loads_and_advertises_only_its_nonempty_selection(self):
        entries = self.entries
        for selected in (["zh_en"], ["en_zh"], ["en_zh", "zh_en"]):
            with self.subTest(selected=selected):
                self.entries = [item for item in entries if item["path"].split("/")[0] in selected]
                self.save(schema=2, directions=selected)
                result, response, loaded = self.start_worker()
                self.assertEqual(result, 0)
                expected = ["zh_en", "en_zh"] if len(selected) == 2 else selected
                self.assertEqual(loaded, expected)
                self.assertEqual(response["status"], "ready")
                self.assertEqual(response["directions"], expected)

    def test_second_native_model_load_failure_never_advertises_any_direction(self):
        for schema in (1, 2):
            with self.subTest(schema=schema):
                self.save(schema=schema, directions=["zh_en", "en_zh"])
                result, response, loaded = self.start_worker(fail_load="en_zh")
                self.assertEqual(result, 2)
                self.assertEqual(loaded, ["zh_en", "en_zh"])
                self.assertIsNone(response)

    def test_tampered_inventory_never_loads_a_model_or_advertises_directions(self):
        (self.root / self.entries[0]["path"]).write_bytes(b"changed")
        for fields in ({}, {"schema": 2, "directions": ["zh_en", "en_zh"]}):
            with self.subTest(fields=fields):
                self.save(**fields)
                result, response, loaded = self.start_worker()
                self.assertEqual(result, 2)
                self.assertEqual(loaded, [])
                self.assertIsNone(response)

    def test_schema2_selfcheck_reports_only_the_loaded_selection(self):
        self.entries = [item for item in self.entries if item["path"].startswith("en_zh/")]
        self.save(schema=2, directions=["en_zh"])
        result, response, loaded = self.start_worker(self_check=True)
        self.assertEqual(result, 0)
        self.assertEqual(loaded, ["en_zh"])
        self.assertEqual(response["status"], "passed")
        self.assertEqual([item["direction"] for item in response["results"]], ["en_zh"])

    def test_subset_requests_keep_detection_same_language_and_manual_append_before_missing_pair(
        self,
    ):
        self.entries = [item for item in self.entries if item["path"].startswith("en_zh/")]
        self.save(schema=2, directions=["en_zh"])
        cases = (
            ("English text", "auto", "en", None, "en", "no-translation", "", ""),
            ("English text", "zh", "en", None, "en", "no-translation", "", ""),
            (
                "English text",
                "auto",
                "en",
                "Manual.",
                "en",
                "translated",
                "Manual. English text",
                "",
            ),
            ("中文", "auto", "en", None, "en", "error", "", "language-unavailable"),
            ("English text", "auto", "zh", None, "en", "translated", "translated", ""),
            ("Bonjour", "auto", "en", None, "fr", "error", "", "language-unavailable"),
            ("???", "auto", "en", None, None, "error", "", "language-unavailable"),
            ("Bonjour", "fr", "en", None, "fr", "error", "", "invalid-request"),
        )
        for text, source, target, preserved, detected, status, output, code in cases:
            with self.subTest(source=source, target=target, detected=detected, preserved=preserved):
                request = dict(id=2, op="translate", text=text, source=source, target=target)
                if preserved is not None:
                    request["preserved"] = preserved
                result, responses, loaded = self.start_worker(request=request, detected=detected)
                self.assertEqual(result, 0)
                self.assertEqual(loaded, ["en_zh"])
                self.assertEqual(responses[0]["directions"], ["en_zh"])
                self.assertEqual(
                    (responses[1]["status"], responses[1]["text"], responses[1]["code"]),
                    (status, output, code),
                )

    def start_worker(self, fail_load=None, *, request=None, detected="en", self_check=False):
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
            return SimpleNamespace(
                translate_batch=lambda *_args, **_options: [
                    SimpleNamespace(hypotheses=[["translated"]])
                ]
            )

        builder = SimpleNamespace()
        builder.from_all_languages = lambda: builder
        builder.with_low_accuracy_mode = lambda: builder
        builder.build = lambda: SimpleNamespace(
            detect_language_of=lambda _: SimpleNamespace(
                iso_code_639_1=SimpleNamespace(name=detected)
            )
            if detected
            else None
        )
        dependencies = {
            "lingua": SimpleNamespace(LanguageDetectorBuilder=builder),
            "sentencepiece": SimpleNamespace(
                SentencePieceProcessor=lambda **_: SimpleNamespace(
                    encode=lambda text, **_: text.split()
                )
            ),
            "ctranslate2": SimpleNamespace(Translator=load_model),
        }
        incoming, outgoing = io.BytesIO(), io.BytesIO()
        write_frame(incoming, {"id": 1, "op": "hello"})
        if request is not None:
            write_frame(incoming, request)
        incoming.seek(0)
        stdout = io.StringIO() if self_check else SimpleNamespace(buffer=outgoing)
        arguments = ["run_worker.py", "--models", str(self.root)]
        if self_check:
            arguments.append("--self-check")
        with (
            patch.dict(sys.modules, dependencies),
            patch.object(sys, "argv", arguments),
            patch.object(sys, "stdin", SimpleNamespace(buffer=incoming)),
            patch.object(sys, "stdout", stdout),
        ):
            result = run_worker.main()
        if self_check:
            return result, json.loads(stdout.getvalue()), loaded
        outgoing.seek(0)
        if request is not None:
            responses = [read_frame(outgoing), read_frame(outgoing)] if outgoing.getvalue() else []
            return result, responses, loaded
        return result, read_frame(outgoing) if outgoing.getvalue() else None, loaded
