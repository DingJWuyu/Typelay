"""Local CTranslate2 inference. Models are installed separately with explicit consent."""

from __future__ import annotations

import os
import re
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

from .catalog import load_package_catalog
from .inventory import verify_inventory
from .protocol import valid_text

# Keep original line separators, URLs, @mentions, issue references and emoji byte-for-byte.
# Protecting structure may reduce sentence-level fluency; this is deliberately explicit, not a
# claim that the first model solves arbitrary rich-text or glossary constraints.
PROTECTED = re.compile(
    r"(https?://[^\s<>]+|www\.[^\s<>]+|[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|"
    r"(?<!\w)@[\w.-]+|(?<!\w)#[0-9]+|\r\n|\r|\n|"
    r"[\U0001F1E6-\U0001FAFF\u2600-\u27BF][\U0001F1E6-\U0001FAFF\u2600-\u27BF\uFE0F\u200D]*)"
)
HAN = re.compile(r"[\u3400-\u9fff\U00020000-\U0003134F]")
KANA = re.compile(r"[\u3040-\u30ff]")


class EngineError(Exception):
    """Error codes only; source text is never put into exceptions exposed to the host."""


class SegmentTranslator(Protocol):
    def translate(self, text: str) -> str: ...


@dataclass(frozen=True)
class Result:
    status: str
    text: str = ""
    code: str = ""


def preserve_structure(text: str, translator: SegmentTranslator) -> str:
    output: list[str] = []
    for part in PROTECTED.split(text):
        if not part:
            continue
        if PROTECTED.fullmatch(part) or not part.strip() or not any(c.isalpha() for c in part):
            output.append(part)
            continue
        leading = part[: len(part) - len(part.lstrip())]
        trailing = part[len(part.rstrip()) :]
        translated = translator.translate(part.strip())
        if not valid_text(translated):
            raise EngineError("invalid-output")
        output.append(leading + translated.strip() + trailing)
    result = "".join(output)
    if not valid_text(result):
        raise EngineError("output-too-long")
    return result


def merge_manual(prefix: str, addition: str) -> str:
    if not valid_text(prefix) or not valid_text(addition):
        raise EngineError("invalid-manual-append")
    separator = "" if prefix[-1:].isspace() or addition[:1].isspace() else " "
    value = prefix + separator + addition
    if not valid_text(value):
        raise EngineError("output-too-long")
    return value


class Ct2Pair:
    def __init__(self, directory: Path, threads: int):
        import ctranslate2
        import sentencepiece as spm

        self._sp = spm.SentencePieceProcessor(model_file=str(directory / "sentencepiece.model"))
        self._model = ctranslate2.Translator(
            str(directory / "model"),
            device="cpu",
            compute_type="int8",
            inter_threads=1,
            intra_threads=threads,
        )

    def close(self):
        unload = getattr(self._model, "unload_model", None)
        if unload is not None:
            unload()
        self._model = None
        self._sp = None

    def translate(self, text: str) -> str:
        pieces = self._sp.encode(text, out_type=str)
        if not pieces:
            return text
        # CTranslate2's default input cap would silently truncate long inputs; reject instead.
        if len(pieces) > 384:
            raise EngineError("segment-too-long")
        outputs = self._model.translate_batch(
            [pieces],
            beam_size=2,
            max_input_length=0,
            max_decoding_length=512,
            repetition_penalty=1.05,
        )
        tokens = outputs[0].hypotheses[0]
        if not tokens or len(tokens) >= 512:
            raise EngineError("decoding-limit")
        # This package's SentencePiece vocabulary covers source tokens only. Decoding
        # generated target tokens through it leaves unknown word markers visible.
        return "".join(tokens).replace("\u2581", " ").strip()


class LocalEngine:
    def __init__(self, models: Path):
        packages = load_package_catalog()
        self.catalog = tuple(
            direction for package in packages.packages for direction in package.directions
        )
        self._pairs = {}
        self._owned = []
        self._selection = ()
        try:
            inventory = verify_inventory(models, packages)
            self.schema = inventory.schema
            if self.schema in (1, 2):
                self.catalog = packages.legacy_directions
            selected = inventory.packages
            from lingua import LanguageDetectorBuilder

            self._detector = (
                LanguageDetectorBuilder.from_all_languages().with_low_accuracy_mode().build()
            )
            threads = max(1, min(4, (os.cpu_count() or 2) // 2))
            for item in selected:
                if item.selection.package.backend == "argos":
                    for direction in item.selection.directions:
                        pair = Ct2Pair(item.root, threads)
                        self._owned.append(pair)
                        self._pairs[(direction.source, direction.target)] = pair
                else:
                    from .hy import HyModel

                    model = HyModel(item.native)
                    self._owned.append(model)
                    for direction in item.selection.directions:
                        self._pairs[(direction.source, direction.target)] = model.direction(
                            direction.id
                        )
            self._selection = tuple(item.selection for item in selected)
        except Exception as error:
            self.close()
            if isinstance(error, ValueError):
                raise EngineError(str(error)) from None
            raise

    def close(self):
        self._pairs.clear()
        self._selection = ()
        for model in reversed(self._owned):
            try:
                model.close()
            except Exception:
                # Continue releasing the other owned resources; process exit is
                # the final cleanup boundary, and native details stay private.
                pass
        self._owned.clear()

    @property
    def packages(self):
        return tuple(
            {
                "id": item.package.id,
                "version": item.package.version,
                "directions": [direction.id for direction in item.directions],
            }
            for item in self._selection
        )

    @property
    def self_check_directions(self):
        directions = tuple(direction for item in self._selection for direction in item.directions)
        return (
            directions[:1]
            if self._selection[0].package.backend == "hy-mt-llama-cpu"
            else directions
        )

    @property
    def directions(self) -> tuple[str, ...]:
        return tuple(direction.id for item in self._selection for direction in item.directions)

    def translate(
        self, text: str, source: str, target: str, preserved: str | None = None
    ) -> Result:
        deadline = time.monotonic() + 60
        sources = {direction.source for direction in self.catalog}
        targets = {direction.target for direction in self.catalog}
        if not valid_text(text) or source not in sources | {"auto"} or target not in targets:
            return Result("error", code="invalid-request")
        if preserved is not None and not valid_text(preserved):
            return Result("error", code="invalid-manual-append")
        detected = self._detector.detect_language_of(text)
        detected_code = detected.iso_code_639_1.name.lower() if detected else ""
        # For mixed Chinese/English going to English, retain the whole text as the translation unit.
        if target == "en" and HAN.search(text) and not KANA.search(text):
            detected_code = "zh"
        if detected_code == target or (source == target and source != "auto"):
            if preserved is not None:
                return Result("translated", merge_manual(preserved, text))
            return Result("no-translation")
        actual = detected_code if source == "auto" else source
        pair = self._pairs.get((actual, target))
        if pair is None:
            return Result("error", code="language-unavailable")
        try:
            if self._selection[0].package.backend == "hy-mt-llama-cpu":
                pair = pair.request(text, deadline)
            value = preserve_structure(text, pair)
            if preserved is not None:
                value = merge_manual(preserved, value)
            return Result("translated", value)
        except EngineError as error:
            return Result("error", code=str(error))
