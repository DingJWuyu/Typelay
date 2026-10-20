"""Explicit case-sensitive terminology. Matching is owned only by the worker."""

import re
import unicodedata
from dataclasses import dataclass

MAX_RULES = 64
MAX_TERM_BYTES = 128
MAX_TOTAL_BYTES = 8192
CJK = re.compile(
    r"[\u3400-\u9fff\U00020000-\U0003134F\u3040-\u30ff\u1100-\u11ff\u3130-\u318f\uac00-\ud7af]"
)


@dataclass(frozen=True)
class Rule:
    source: str
    target: str
    source_language: str | None
    target_language: str | None


@dataclass(frozen=True)
class Match:
    start: int
    end: int
    source: str
    target: str


def term_bytes(value):
    if (
        type(value) is not str
        or not value
        or value.strip() != value
        or any(unicodedata.category(c) == "Cc" for c in value)
    ):
        raise ValueError("invalid-glossary")
    try:
        size = len(value.encode("utf-8", "strict"))
    except UnicodeError:
        raise ValueError("invalid-glossary") from None
    if size > MAX_TERM_BYTES:
        raise ValueError("glossary-too-large")
    return size


def parse_glossary(value, directions):
    if (
        type(value) is not dict
        or set(value) != {"version", "rules"}
        or type(value["version"]) is not int
        or not 0 < value["version"] <= 9223372036854775807
        or type(value["rules"]) is not list
        or not 1 <= len(value["rules"]) <= MAX_RULES
    ):
        raise ValueError("invalid-glossary")
    result, seen, total = [], {}, 0
    for item in value["rules"]:
        if (
            type(item) is not dict
            or set(item) - {"sourceTerm", "targetTerm", "sourceLanguage", "targetLanguage"}
            or not {"sourceTerm", "targetTerm"} <= set(item)
        ):
            raise ValueError("invalid-glossary")
        source, target = item["sourceTerm"], item["targetTerm"]
        total += term_bytes(source) + term_bytes(target)
        a, b = item.get("sourceLanguage"), item.get("targetLanguage")
        if (a is not None or b is not None) and (
            type(a) is not str or type(b) is not str or (a, b) not in directions
        ):
            raise ValueError("invalid-glossary")
        key = (a, b, source)
        if key in seen:
            if seen[key] != target:
                raise ValueError("glossary-conflict")
            continue
        seen[key] = target
        result.append(Rule(source, target, a, b))
    if total > MAX_TOTAL_BYTES:
        raise ValueError("glossary-too-large")
    return tuple(result)


def word_character(c):
    return c == "_" or unicodedata.category(c)[0] in "LNM"


def occurrences(text, term):
    substring = CJK.search(term) is not None
    start = 0
    while (start := text.find(term, start)) >= 0:
        end = start + len(term)
        if substring or (
            (start == 0 or not word_character(text[start - 1]))
            and (end == len(text) or not word_character(text[end]))
        ):
            yield start, end
        start += 1


def select_matches(text, rules, source, target):
    selected = {r.source: r for r in rules if r.source_language is None}
    selected.update(
        (r.source, r) for r in rules if (r.source_language, r.target_language) == (source, target)
    )
    possible = [
        Match(a, b, r.source, r.target)
        for r in selected.values()
        for a, b in occurrences(text, r.source)
    ]
    # Length means Unicode scalar count. Longest first, then leftmost, then
    # scalar ordinal strings; this is independent of persisted list ordering.
    possible.sort(key=lambda h: (-(h.end - h.start), h.start, h.source, h.target))
    hits = []
    for hit in possible:
        if not any(hit.start < old.end and old.start < hit.end for old in hits):
            hits.append(hit)
    return tuple(sorted(hits, key=lambda h: h.start))


def segment_terms(text, rules, source, target, segments):
    """Resolve before inference; every nontrivial match belongs to one actual segment."""
    result = [[] for _ in segments]
    for hit in select_matches(text, rules, source, target):
        owner = next(
            (
                i
                for i, (a, b, translatable) in enumerate(segments)
                if a <= hit.start and hit.end <= b
            ),
            None,
        )
        if owner is None:
            raise ValueError("glossary-protected-conflict")
        if not segments[owner][2]:
            if hit.source != hit.target:
                raise ValueError("glossary-protected-conflict")
            continue
        pair = (hit.source, hit.target)
        if pair not in result[owner]:
            result[owner].append(pair)
    return tuple(tuple(pairs) for pairs in result)


def required_targets_present(text, terms):
    return all(next(occurrences(text, target), None) is not None for _, target in terms)
