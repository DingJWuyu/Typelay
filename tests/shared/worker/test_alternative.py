"""Alternative protocol and bounded inference contracts, not model-quality evidence."""

import hashlib
import io
import json
import sys
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import run_worker
from typelay_worker.engine import LocalEngine
from typelay_worker.protocol import read_frame, write_frame


@contextmanager
def controlled_engine(hypotheses=None, detected="zh", direction="zh_en"):
    """Load a verified synthetic inventory against controlled external model ports."""
    calls = []
    hypotheses = hypotheses if hypotheses is not None else [["▁Hello"], ["▁Greetings"]]

    class Model:
        def __init__(self, *args, **kwargs):
            pass

        def translate_batch(self, inputs, **kwargs):
            calls.append((inputs, kwargs))
            return [SimpleNamespace(hypotheses=hypotheses)]

        def unload_model(self):
            pass

    class Tokenizer:
        def __init__(self, **kwargs):
            pass

        def encode(self, text, **kwargs):
            return text.split()

    class Detector:
        @classmethod
        def from_all_languages(cls):
            return cls()

        def with_low_accuracy_mode(self):
            return self

        def build(self):
            return self

        def detect_language_of(self, text):
            return SimpleNamespace(iso_code_639_1=SimpleNamespace(name=detected.upper()))

    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        files = []
        for name in ("model/model.bin", "sentencepiece.model"):
            path = root / direction / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"controlled model port, not model weights")
            files.append(
                {
                    "path": path.relative_to(root).as_posix(),
                    "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                }
            )
        (root / "installed.json").write_text(
            json.dumps({"schema": 2, "directions": [direction], "files": files}), encoding="utf8"
        )
        with patch.dict(
            sys.modules,
            {
                "ctranslate2": SimpleNamespace(Translator=Model),
                "sentencepiece": SimpleNamespace(SentencePieceProcessor=Tokenizer),
                "lingua": SimpleNamespace(LanguageDetectorBuilder=Detector),
            },
        ):
            engine = LocalEngine(root)
            try:
                yield engine, calls
            finally:
                engine.close()


def exchange(engine, **fields):
    incoming, outgoing = io.BytesIO(), io.BytesIO()
    request = {
        "id": 1,
        "op": "translate-alternative",
        "text": "你好",
        "source": "zh",
        "target": "en",
        "previousCandidate": "Hello",
        **fields,
    }
    if request["op"] == "translate-styled-v1":
        request.pop("previousCandidate", None)
    write_frame(incoming, request)
    incoming.seek(0)
    with (
        patch.object(sys, "stdin", SimpleNamespace(buffer=incoming)),
        patch.object(sys, "stdout", SimpleNamespace(buffer=outgoing)),
    ):
        code = run_worker.serve(engine, False)
    outgoing.seek(0)
    return code, read_frame(outgoing)


class AlternativeTests(unittest.TestCase):
    def test_alternative_uses_original_source_and_second_complete_hypothesis(self):
        with controlled_engine() as (engine, calls):
            code, response = exchange(engine)
            self.assertEqual(code, 0)
            self.assertEqual((response["status"], response["text"]), ("translated", "Greetings"))
            self.assertEqual(
                calls,
                [
                    (
                        [["你好"]],
                        {
                            "beam_size": 2,
                            "patience": 1,
                            "num_hypotheses": 2,
                            "max_input_length": 0,
                            "max_decoding_length": 512,
                            "repetition_penalty": 1.05,
                        },
                    )
                ],
            )

    def test_ranked_hypotheses_are_only_filtered_not_rewritten(self):
        cases = [
            ([["▁HELLO!"]], "no-alternative", ""),
            ([["▁H e l l o..."]], "no-alternative", ""),
            ([["▁Greetings!"], ["▁Welcome"]], "translated", "Greetings!"),
            ([[], ["▁Greetings"]], "translated", "Greetings"),
            ([["▁x"] * 512, ["▁Hello"]], "no-alternative", ""),
            ([["▁https://example.test"], ["▁Hello"]], "no-alternative", ""),
            ([["▁Line\nTwo"], ["▁Hello"]], "no-alternative", ""),
            ([["▁Hi🙂"], ["▁Hello"]], "no-alternative", ""),
            ([["\ud800"], ["▁Hello"]], "no-alternative", ""),
        ]
        for hypotheses, status, text in cases:
            with (
                self.subTest(hypotheses=hypotheses),
                controlled_engine(hypotheses) as (engine, calls),
            ):
                code, response = exchange(engine)
                self.assertEqual((code, response["status"], response["text"]), (0, status, text))
                self.assertEqual(len(calls), 1)

    def test_unsupported_or_invalid_requests_never_run_inference(self):
        fields = [
            {"previousCandidate": None},
            {"previousCandidate": ""},
            {"previousCandidate": "\ud800"},
            {"previousCandidate": "x" * 8193},
            {"preserved": "manual prefix"},
            {"preserved": ""},
            {"text": "你好\r\n世界"},
            {"previousCandidate": "Hello\nWorld"},
            {"text": "你好 https://example.test"},
            {"text": "你好 @Alice"},
            {"text": "你好 a@example.test"},
            {"text": "你好 #12"},
            {"text": "你好🙂"},
            {"text": "x" * 8193},
            {"source": []},
            {"target": "ja"},
        ]
        for fields in fields:
            with self.subTest(fields=fields), controlled_engine() as (engine, calls):
                code, response = exchange(engine, **fields)
                self.assertEqual((code, response["status"], response["text"]), (0, "error", ""))
                self.assertEqual(calls, [])

    def test_auto_and_loaded_direction_keep_the_ordinary_detection_order(self):
        for direction, detected, source, target, status, code in [
            ("zh_en", "zh", "auto", "en", "translated", ""),
            ("en_zh", "zh", "auto", "en", "error", "language-unavailable"),
            ("zh_en", "en", "auto", "en", "no-alternative", ""),
            ("zh_en", "zh", "zh", "zh", "no-alternative", ""),
        ]:
            with self.subTest(direction=direction, source=source, target=target, detected=detected):
                with controlled_engine(detected=detected, direction=direction) as (engine, calls):
                    _, response = exchange(engine, text="source", source=source, target=target)
                    self.assertEqual((response["status"], response["code"]), (status, code))
                    self.assertEqual(len(calls), int(status == "translated"))

    def test_long_tokenized_input_is_rejected_without_truncation(self):
        with controlled_engine() as (engine, calls):
            _, response = exchange(engine, text="word " * 385)
            self.assertEqual((response["status"], response["code"]), ("error", "segment-too-long"))
            self.assertEqual(calls, [])

    def test_argos_rejects_explicit_styles_before_same_language_without_inference(self):
        for source in ("auto", "en"):
            with self.subTest(source=source), controlled_engine(detected="en") as (engine, calls):
                code, response = exchange(
                    engine, op="translate-styled-v1", source=source, target="en", style="formal"
                )
                self.assertEqual(
                    (code, response["status"], response["code"], response["text"]),
                    (0, "error", "style-unavailable", ""),
                )
                self.assertEqual(calls, [])

    def test_normal_translation_still_uses_its_original_single_hypothesis_contract(self):
        with controlled_engine() as (engine, calls):
            result = engine.translate("你好", "zh", "en")
            self.assertEqual((result.status, result.text), ("translated", "Hello"))
            self.assertEqual(
                calls,
                [
                    (
                        [["你好"]],
                        {
                            "beam_size": 2,
                            "max_input_length": 0,
                            "max_decoding_length": 512,
                            "repetition_penalty": 1.05,
                        },
                    )
                ],
            )
