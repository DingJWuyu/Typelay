"""Collect two frozen sources across four production operations, once each.

Formal-only, greedy, one installed worker, unchanged 600-second total budget.
Run only after the existing owner has exited and its inventory is exclusively
held by the approved capture owner. This CLI does not acquire that ownership.
No downloads, retries, output replacement, automatic quality verdict or CI hook.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import glossary_quality
import styled_quality
from typelay_worker.catalog import load_package_catalog
from typelay_worker.glossary import parse_glossary
from typelay_worker.protocol import valid_text

INPUTS = Path(__file__).with_name("styled_glossary_quality_inputs.json")
INPUT_SHA256 = "a12ac32993d521956e57f4d807dca7b7fd8d342d5473bc1bd9a1347907ad287a"
DIRECTIONS = ("zh_fr", "fr_zh")
OPERATIONS = (
    ("ordinary", "translate"),
    ("style", "translate-styled-v1"),
    ("glossary", "translate-glossary-v1"),
    ("combined", "translate-styled-glossary-v1"),
)
CAPABILITIES = (
    "styledTranslationVersion",
    "glossaryTranslationVersion",
    "styledGlossaryTranslationVersion",
)


def prepare_report():
    raw = INPUTS.read_bytes()
    if hashlib.sha256(raw).hexdigest() != INPUT_SHA256:
        raise styled_quality.CollectionError("fixed-input-sha256")
    inputs = json.loads(raw)
    if (
        set(inputs) != {"schema", "samples"}
        or type(inputs["schema"]) is not int
        or inputs["schema"] != 1
        or len(inputs["samples"]) != 2
    ):
        raise styled_quality.CollectionError("fixed-input-schema")
    directions = {
        (direction.source, direction.target)
        for package in load_package_catalog().packages
        for direction in package.directions
    }
    cases = []
    for sample, identity, direction in zip(
        inputs["samples"],
        ("zh-fr-payment-attempt", "fr-zh-proposal-condition"),
        DIRECTIONS,
        strict=True,
    ):
        if (
            set(sample) != {"id", "direction", "text", "semantic_anchors", "style", "glossary"}
            or sample["id"] != identity
            or sample["direction"] != direction
            or not valid_text(sample["text"])
            or sample["style"] != "formal"
            or not sample["semantic_anchors"]
            or any(not valid_text(anchor) for anchor in sample["semantic_anchors"])
        ):
            raise styled_quality.CollectionError("fixed-input-case-schema")
        parse_glossary(sample["glossary"], directions)
        source, target = direction.split("_")
        for variant, operation in OPERATIONS:
            payload = dict(op=operation, text=sample["text"], source=source, target=target)
            if variant in ("style", "combined"):
                payload["style"] = sample["style"]
            if variant in ("glossary", "combined"):
                payload["glossary"] = sample["glossary"]
            cases.append(
                dict(
                    id=identity + ":" + variant,
                    sourceCaseId=identity,
                    variant=variant,
                    request=payload,
                    expectedResponse={"status": "translated", "code": ""},
                    semantic_anchors=sample["semantic_anchors"],
                    response=None,
                    manualReview=dict.fromkeys(
                        (
                            "negation",
                            "numbers",
                            "subject",
                            "state",
                            "tenseCondition",
                            "commitmentStrength",
                            "properNames",
                            "terminology",
                            "styleSuitability",
                            "addedMeaning",
                            "omittedMeaning",
                            "naturalness",
                            "seriousMeaningError",
                            "notes",
                        )
                    ),
                )
            )
    return {
        **styled_quality.collection_report(INPUT_SHA256, 8),
        "method": "production-four-operations-formal-only-paired-capture",
        "requiredCapabilities": dict.fromkeys(CAPABILITIES, 1),
        "comparisonLimit": "operation templates differ; not isolated prompt-term causality",
        "cases": cases,
    }


async def collect_cases(process, report):
    # The sole hello is already verified against the installed package identity.
    # Check all three capabilities before ordinary control can consume any work.
    hello = report["hello"]
    if any(type(hello.get(key)) is not int or hello[key] != 1 for key in CAPABILITIES):
        raise styled_quality.CollectionError("paired-capabilities-required-no-fallback")
    if len(report["requests"]) != 1 or report["requests"][0]["op"] != "hello":
        raise styled_quality.CollectionError("paired-capture-must-start-once-after-hello")
    await glossary_quality.collect_cases(process, report)


async def collect(args, report):
    await styled_quality.collect(
        args,
        report,
        collect_cases=collect_cases,
        required_directions=DIRECTIONS,
        capability="styledGlossaryTranslationVersion",
    )


if __name__ == "__main__":
    raise SystemExit(styled_quality.main(prepare_report, collect, __doc__))
