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
from .hy_assets import DIRECTIONS as HY_ADVANCED_DIRECTIONS
from .inventory import verify_inventory
from .protocol import valid_text

# Keep original line separators, URLs, @mentions, issue references and emoji byte-for-byte.
# Protecting structure may reduce sentence-level fluency; this is deliberately explicit, not a
# claim that the first model solves arbitrary rich-text or glossary constraints.
PROTECTED = re.compile(
    r"(https?://[^\s<>]+|www\.[^\s<>]+|[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|"
    r"[#*0-9]\uFE0F?\u20E3|[\u00A9\u00AE]\uFE0F|"
    # A mention/issue may start before a keycap; keep its combining suffix in the same span.
    r"(?:(?<!\w)@[\w.-]+|(?<!\w)#[0-9]+)(?:(?<=[0-9])\uFE0F?\u20E3)?|"
    r"\r\n|\r|\n|"
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


def translate_whole_source(text, direction, deadline, style, rules, source, target):
    """One Index inference; inspect structure without splitting or repairing inference."""
    parts = [part for part in PROTECTED.split(text) if part]
    spans, translatable, position = [], [], 0
    literals, preserve_whitespace = [], False
    for part in parts:
        translate = (
            not PROTECTED.fullmatch(part) and bool(part.strip()) and any(c.isalpha() for c in part)
        )
        translatable.append(bool(translate))
        spans.append((position, position + len(part), bool(translate)))
        position += len(part)
        if translate:
            leading = part[: len(part) - len(part.lstrip())]
            trailing = part[len(part.rstrip()) :]
            preserve_whitespace |= bool(leading or trailing)
        elif part not in ("\r\n", "\r", "\n"):
            literals.append(part)
    terms = []
    groups = [() for _ in spans]
    if rules is not None:
        try:
            groups = segment_terms(text, rules, source, target, spans)
            for group in groups:
                for pair in group:
                    if pair not in terms:
                        terms.append(pair)
        except ValueError as error:
            raise EngineError(str(error)) from None
    if not any(translatable):
        return text
    linebreaks = tuple(re.findall(r"\r\n|\r|\n", text))
    request = direction.request(
        text,
        deadline,
        style,
        preserved_literals=tuple(literals),
        preserve_linebreaks=bool(linebreaks),
        preserve_whitespace=preserve_whitespace,
    )
    result = request.translate_glossary(text, tuple(terms)) if terms else request.translate(text)
    if not valid_text(result):
        raise EngineError("invalid-output")

    # Validate logical topology before source-owned layout serialization. Only
    # whole-request SP/TAB padding and the spelling of existing line breaks may
    # differ; internal whitespace, literals and per-slot terminology stay exact.
    def logical(value):
        return value.replace("\r\n", "\n").replace("\r", "\n")

    source_parts = []
    for i, (part, translate, group) in enumerate(zip(parts, translatable, groups)):
        if i == 0:
            part = part.lstrip(" \t")
        if i == len(parts) - 1:
            part = part.rstrip(" \t")
        if part:
            source_parts.append((logical(part), translate, group))
    body = logical(result.strip(" \t"))
    output_parts = [part for part in PROTECTED.split(body) if part]
    if (
        len(output_parts) != len(source_parts)
        or tuple(PROTECTED.findall(body)) != tuple(PROTECTED.findall(logical(text)))
        or body.count("\n") != len(linebreaks)
    ):
        raise EngineError("structure-not-preserved")
    for (original, translate, group), output in zip(source_parts, output_parts):
        if translate and not output.strip():
            raise EngineError("structure-not-preserved")
        if not translate:
            preserved = original == output
        else:
            preserved = (
                original[: len(original) - len(original.lstrip())]
                == output[: len(output) - len(output.lstrip())]
                and original[len(original.rstrip()) :] == output[len(output.rstrip()) :]
            )
        if not preserved:
            raise EngineError("structure-not-preserved")
        if not required_targets_present(output, group):
            raise EngineError("glossary-output-missing")
    separators = iter(linebreaks)
    rendered = (
        text[: len(text) - len(text.lstrip(" \t"))]
        + re.sub("\n", lambda _: next(separators), body)
        + text[len(text.rstrip(" \t")) :]
    )
    # The native 8192-byte output bound also applies after layout serialization,
    # in addition to the shared UTF-16/frame contract. Never truncate to fit.
    if not valid_text(rendered) or len(rendered.encode("utf-8", "strict")) > 8192:
        raise EngineError("output-too-long")
    return rendered


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
                    if item.selection.package.backend == "index-translate-llama-cpu":
                        from .index import IndexModel

                        model = IndexModel(item.native)
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
    def advanced_translation_directions(self):
        if len(self._selection) != 1:
            return ()
        if self._selection[0].package.backend == "index-translate-llama-cpu":
            return tuple(direction.id for direction in self._selection[0].directions)
        if self._selection[0].package.backend != "hy-mt-llama-cpu":
            return ()
        return tuple(
            direction.id
            for direction in self._selection[0].directions
            if direction.id in HY_ADVANCED_DIRECTIONS
        )

    def _supports_advanced_direction(self, source, target):
        possible = tuple(
            direction.id
            for item in self._selection
            for direction in item.directions
            if direction.target == target and (source == "auto" or direction.source == source)
        )
        return bool(possible) and all(
            direction in self.advanced_translation_directions for direction in possible
        )

    @property
    def styled_translation_version(self):
        # Successful loading establishes a protocol feature, not language quality.
        if self._selection and self._selection[0].package.backend == "index-translate-llama-cpu":
            return (
                1
                if self.advanced_translation_directions and self._owned[0].styled_available
                else None
            )
        return 1 if self.advanced_translation_directions else None

    @property
    def glossary_translation_version(self):
        if self._selection and self._selection[0].package.backend == "index-translate-llama-cpu":
            return (
                1
                if self.advanced_translation_directions and self._owned[0].glossary_available
                else None
            )
        return self.styled_translation_version

    @property
    def styled_glossary_translation_version(self):
        return (
            1
            if self.styled_translation_version is not None
            and self._owned[0].styled_glossary_available
            else None
        )

    @property
    def alternative_translation_version(self):
        return (
            1
            if self._selection
            and self._selection[0].package.backend == "index-translate-llama-cpu"
            and self._owned[0].alternative_available
            else None
        )

    @property
    def self_check_directions(self):
        directions = tuple(direction for item in self._selection for direction in item.directions)
        return (
            directions[:1]
            if self._selection[0].package.backend
            in ("hy-mt-llama-cpu", "index-translate-llama-cpu")
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
        backend = self._selection[0].package.backend
        languages = (
            {"zh", "en"}
            if backend == "argos"
            else {
                language
                for direction in self.catalog
                for language in (direction.source, direction.target)
            }
        )
        if (
            not valid_text(text)
            or not valid_text(previous)
            or preserved is not None
            or not isinstance(source, str)
            or not isinstance(target, str)
            or source not in languages | {"auto"}
            or target not in languages
        ):
            return Result("error", code="invalid-request")
        if (
            PROTECTED.search(text)
            or PROTECTED.search(previous)
            or backend not in ("argos", "index-translate-llama-cpu")
            or backend == "index-translate-llama-cpu"
            and self.alternative_translation_version != 1
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
            if backend == "index-translate-llama-cpu":
                return pair.translate_alternative(text, previous, deadline=time.monotonic() + 60)
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
        if not self._supports_advanced_direction(source, target):
            return Result("error", code="style-unavailable")
        return self._translate(text, source, target, preserved, style)

    def translate_styled_glossary(self, text, source, target, style, glossary, preserved=None):
        if (
            type(style) is not str
            or style not in ("natural", "formal")
            or not isinstance(source, str)
            or not isinstance(target, str)
        ):
            return Result("error", code="invalid-request")
        if (
            self.styled_glossary_translation_version is None
            or not self._supports_advanced_direction(source, target)
        ):
            return Result("error", code="glossary-combination-unavailable")
        return self._translate_glossary(text, source, target, glossary, preserved, style)

    def translate_glossary(self, text, source, target, glossary, preserved=None):
        return self._translate_glossary(text, source, target, glossary, preserved, None)

    def _translate_glossary(self, text, source, target, glossary, preserved, style):
        try:
            rules = parse_glossary(glossary, {(d.source, d.target) for d in self.catalog})
        except ValueError as error:
            return Result("error", code=str(error))
        if not isinstance(source, str) or not isinstance(target, str):
            return Result("error", code="invalid-request")
        if not self._supports_advanced_direction(source, target):
            return Result("error", code="glossary-unavailable")
        return self._translate(text, source, target, preserved, style, rules)

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
                if (actual, target) in (("zh", "en"), ("en", "zh")):
                    if glossary is not None:
                        raise EngineError("glossary-unavailable")
                    if style is not None:
                        raise EngineError("style-unavailable")
                pair = pair.request(text, deadline, style)
            if self._selection[0].package.backend == "index-translate-llama-cpu":
                value = translate_whole_source(
                    text, pair, deadline, style, glossary, actual, target
                )
            else:
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
