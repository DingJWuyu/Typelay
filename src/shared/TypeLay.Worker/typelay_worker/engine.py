"""Local CTranslate2 inference. Models are installed separately with explicit consent."""

from __future__ import annotations

import os
import re
import time
import unicodedata
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

from .catalog import load_package_catalog
from .glossary import parse_glossary, required_targets_present, segment_terms
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


def preserve_glossary_structure(text, translator, rules, source, target):
    spans, position = [], 0
    for part in PROTECTED.split(text):
        if not part:
            continue
        end = position + len(part)
        if PROTECTED.fullmatch(part) or not part.strip() or not any(c.isalpha() for c in part):
            spans.append((position, end, False))
        else:
            a = position + len(part) - len(part.lstrip())
            b = end - len(part) + len(part.rstrip())
            if a > position:
                spans.append((position, a, False))
            spans.append((a, b, True))
            if b < end:
                spans.append((b, end, False))
        position = end
    try:
        constraints = segment_terms(text, rules, source, target, spans)
    except ValueError as error:
        raise EngineError(str(error)) from None
    output = []
    for (a, b, translatable), terms in zip(spans, constraints):
        if not translatable:
            output.append(text[a:b])
            continue
        value = translator.translate_glossary(text[a:b], terms)
        if not valid_text(value):
            raise EngineError("invalid-output")
        if not required_targets_present(value, terms):
            raise EngineError("glossary-output-missing")
        output.append(value.strip())
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

    def translate_alternative(self, text: str, previous: str) -> Result:
        pieces = self._sp.encode(text, out_type=str)
        if not pieces or len(pieces) > 384:
            raise EngineError("segment-too-long")
        outputs = self._model.translate_batch(
            [pieces],
            beam_size=2,
            patience=1,
            num_hypotheses=2,
            max_input_length=0,
            max_decoding_length=512,
            repetition_penalty=1.05,
        )
        if len(outputs) != 1:
            raise EngineError("invalid-output")
        previous_key = alternative_key(previous)
        for tokens in outputs[0].hypotheses[:2]:
            if not tokens or len(tokens) >= 512:
                continue
            value = "".join(tokens).replace("\u2581", " ").strip()
            if not valid_text(value) or PROTECTED.search(value):
                continue
            if alternative_key(value) != previous_key:
                return Result("translated", value)
        return Result("no-alternative")


def alternative_key(text: str) -> str:
    # Comparison only. Never rewrite a selected hypothesis to manufacture a difference.
    return "".join(
        c
        for c in text.casefold()
        if not c.isspace() and not unicodedata.category(c).startswith("P")
    )


class LocalEngine:
    def __init__(self, models: Path):
        packages = load_package_catalog()
        self.catalog = tuple(
            direction for package in packages.packages for direction in package.directions
        )
        self._pairs = {}
        self._owned = []
        self._selection = ()
        self._identity_verified = False
        try:
            inventory = verify_inventory(models, packages)
            self.schema = inventory.schema
            self._identity_verified = inventory.identity_verified
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
        if not self._identity_verified:
            return ()
        return tuple(
            {
                "id": item.package.id,
                "version": item.package.version,
                "directions": [direction.id for direction in item.directions],
            }
            for item in self._selection
        )

    @property
    def styled_translation_version(self):
        # Successful loading establishes a protocol feature, not language quality.
        return (
            1
            if len(self._selection) == 1 and self._selection[0].package.backend == "hy-mt-llama-cpu"
            else None
        )

    @property
    def glossary_translation_version(self):
        return self.styled_translation_version

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

    def translate_alternative(
        self,
        text: str,
        source: str,
        target: str,
        previous: str,
        preserved: str | None = None,
    ) -> Result:
        if (
            not valid_text(text)
            or not valid_text(previous)
            or preserved is not None
            or not isinstance(source, str)
            or not isinstance(target, str)
            or source not in {"auto", "zh", "en"}
            or target not in {"zh", "en"}
        ):
            return Result("error", code="invalid-request")
        if (
            PROTECTED.search(text)
            or PROTECTED.search(previous)
            or self._selection[0].package.backend != "argos"
        ):
            return Result("error", code="alternative-unavailable")
        detected = self._detector.detect_language_of(text)
        detected_code = detected.iso_code_639_1.name.lower() if detected else ""
        if target == "en" and HAN.search(text) and not KANA.search(text):
            detected_code = "zh"
        if detected_code == target or source == target:
            return Result("no-alternative")
        actual = detected_code if source == "auto" else source
        pair = self._pairs.get((actual, target))
        if pair is None:
            return Result("error", code="language-unavailable")
        try:
            return pair.translate_alternative(text, previous)
        except EngineError as error:
            return Result("error", code=str(error))

    def translate_styled(
        self, text: str, source: str, target: str, style: str, preserved: str | None = None
    ) -> Result:
        if (
            type(style) is not str
            or style not in ("natural", "formal")
            or not isinstance(source, str)
            or not isinstance(target, str)
        ):
            return Result("error", code="invalid-request")
        if self._selection[0].package.backend != "hy-mt-llama-cpu":
            return Result("error", code="style-unavailable")
        return self._translate(text, source, target, preserved, style)

    def translate_glossary(self, text, source, target, glossary, preserved=None):
        try:
            rules = parse_glossary(glossary, {(d.source, d.target) for d in self.catalog})
        except ValueError as error:
            return Result("error", code=str(error))
        if not isinstance(source, str) or not isinstance(target, str):
            return Result("error", code="invalid-request")
        if self._selection[0].package.backend != "hy-mt-llama-cpu":
            return Result("error", code="glossary-unavailable")
        return self._translate(text, source, target, preserved, None, rules)

    def translate(
        self, text: str, source: str, target: str, preserved: str | None = None
    ) -> Result:
        return self._translate(text, source, target, preserved, None)

    def _translate(
        self,
        text: str,
        source: str,
        target: str,
        preserved: str | None,
        style: str | None,
        glossary=None,
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
                pair = pair.request(text, deadline, style)
            value = (
                preserve_structure(text, pair)
                if glossary is None
                else preserve_glossary_structure(text, pair, glossary, actual, target)
            )
            if preserved is not None:
                value = merge_manual(preserved, value)
            return Result("translated", value)
        except EngineError as error:
            return Result("error", code=str(error))
