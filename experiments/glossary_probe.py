"""Temporary synthetic-sentence feasibility probe; never shipped with TypeLay."""

import argparse
import dataclasses
import hashlib
import json
import re
import time
from pathlib import Path

CASES = [
    ("项目方确认了预算。", "zh", "en", None),
    ("我们已经把预算发给项目方。", "zh", "en", None),
    ("请不要把这个 PR 发给项目方。", "zh", "en", None),
    ("项目方没有批准这个 PR。", "zh", "en", None),
    ("项目方说 TypeLay 不需要修改。", "zh", "en", None),
    ("明天由项目方确认，而不是我们。", "zh", "en", None),
    ("请让项目方和开发团队一起检查 TypeLay。", "zh", "en", None),
    (
        "请项目方查看 https://example.test/TypeLay/PR ，联系 @TypeLay，并检查 #123。\n不要修改 PR。",
        "zh",
        "en",
        None,
    ),
    ("Please ask the client team to review this PR in TypeLay.", "en", "zh", None),
    ("Do not send this PR to the client team.", "en", "zh", None),
    ("TypeLay and TypeLayer differ; this PR is APPROVED.", "en", "zh", None),
    ("然后请项目方检查 TypeLay。", "zh", "en", "My deliberate wording: 项目方 / TypeLay / PR."),
]
ASCII_WORD = r"A-Za-z0-9_"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    import sys

    sys.path.insert(0, str(args.worker.resolve(strict=True)))
    from typelay_worker.engine import PROTECTED, LocalEngine, merge_manual
    from typelay_worker.protocol import valid_text

    engine = LocalEngine(args.models)
    report = {
        "packageCommit": "52faf423fa6e055e41af0238fbd8e67e8cc10b8a",
        "packageArtifact": 11970206852,
        "inventorySha256": hashlib.sha256(
            (args.models / "installed.json").read_bytes()
        ).hexdigest(),
        "engineSha256": hashlib.sha256(
            (args.worker / "typelay_worker/engine.py").read_bytes()
        ).hexdigest(),
        "scope": "Synthetic explicit zh/en directions only; no Auto, settings, learning, or product integration.",
        "matching": "Case-sensitive; protected structure first; leftmost longest; ASCII word boundaries for Latin terms.",
        "rules": {"global": {"TypeLay": "TypeLay", "PR": "PR"}, "zh-en": {"项目方": "client team"}},
        "qualityVerdict": "Requires human sentence-by-sentence assessment; exact term preservation is not a quality pass.",
        "cases": [],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)

    def save():
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    for index, (text, source, target, preserved) in enumerate(CASES, 1):
        started = time.monotonic()
        baseline = engine.translate(text, source, target, preserved)
        baseline_ms = round((time.monotonic() - started) * 1000)
        if baseline.status != "translated":
            report["cases"].append(
                {
                    "id": f"{index:02}",
                    "source": source,
                    "target": target,
                    "input": text,
                    "preserved": preserved,
                    "baseline": dataclasses.asdict(baseline),
                    "baselineMs": baseline_ms,
                    "comparison": "not-entered: production baseline did not translate",
                }
            )
            save()
            continue
        span_started = time.monotonic()
        rules = {"TypeLay": "TypeLay", "PR": "PR"}
        if (source, target) == ("zh", "en"):
            rules["项目方"] = "client team"
        terms = sorted(rules, key=lambda term: (-len(term), term))
        patterns = [
            rf"(?<![{ASCII_WORD}]){re.escape(term)}(?![{ASCII_WORD}])"
            if term.isascii()
            else re.escape(term)
            for term in terms
        ]
        matcher = re.compile("|".join(patterns))
        pair = engine._pairs[(source, target)]
        segments = []

        def append(kind, value, start):
            if not value:
                return
            if kind == "term":
                translated = rules[value]
            elif kind == "structure" or not value.strip() or not any(c.isalpha() for c in value):
                translated = value
            else:
                leading = value[: len(value) - len(value.lstrip())]
                trailing = value[len(value.rstrip()) :]
                translated = leading + pair.translate(value.strip()).strip() + trailing
            segments.append(
                {
                    "kind": kind,
                    "start": start,
                    "end": start + len(value),
                    "source": value,
                    "output": translated,
                }
            )

        def ordinary(value, offset):
            cursor = 0
            for match in matcher.finditer(value):
                append("translated", value[cursor : match.start()], offset + cursor)
                append("term", match.group(), offset + match.start())
                cursor = match.end()
            append("translated", value[cursor:], offset + cursor)

        cursor = 0
        for protected in PROTECTED.finditer(text):
            ordinary(text[cursor : protected.start()], cursor)
            append("structure", protected.group(), protected.start())
            cursor = protected.end()
        ordinary(text[cursor:], cursor)
        raw = "".join(segment["output"] for segment in segments)
        spaced = ""
        previous = None
        for segment in segments:
            value = segment["output"]
            if (
                previous
                and "structure" not in (previous["kind"], segment["kind"])
                and "term" in (previous["kind"], segment["kind"])
                and spaced[-1:].isascii()
                and spaced[-1:].isalnum()
                and value[:1].isascii()
                and value[:1].isalnum()
            ):
                spaced += " "
            spaced += value
            previous = segment
        if not valid_text(raw) or not valid_text(spaced):
            raise ValueError("invalid-candidate-output")
        if preserved is not None:
            raw = merge_manual(preserved, raw)
            spaced = merge_manual(preserved, spaced)
        report["cases"].append(
            {
                "id": f"{index:02}",
                "source": source,
                "target": target,
                "input": text,
                "preserved": preserved,
                "baseline": dataclasses.asdict(baseline),
                "rawSpan": raw,
                "spacedSpan": spaced,
                "segments": segments,
                "baselineMs": baseline_ms,
                "spanInferenceAndJoinMs": round((time.monotonic() - span_started) * 1000),
                "manualAssessment": "pending",
                "elapsedMs": round((time.monotonic() - started) * 1000),
            }
        )
        save()
    report["completedCases"] = len(report["cases"])
    save()


if __name__ == "__main__":
    main()
