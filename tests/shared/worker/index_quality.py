"""Collect eight frozen sources through one installed, exact Index worker.

Six operations per source, plus three explicit refusals. One model, fixed greedy
4096/1024 and 600 seconds total; no retries, prompt search, fallback or quality verdict.
Only the explicit Index entry selects Index; existing Hy collectors keep their gates.
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import time
from pathlib import Path

import styled_quality
from typelay_worker.engine import PROTECTED, alternative_key
from typelay_worker.glossary import parse_glossary
from typelay_worker.index_assets import PACKAGE_ID, VERSION
from typelay_worker.protocol import valid_text

INPUTS = Path(__file__).with_name("index_quality_inputs.json")
INPUT_SHA256 = "0fdcac8ce378ca2fb19304cc2565485224ed60b54cc3c7ec16738edf41f444cd"
DIRECTIONS = ("zh_en", "en_zh")
VARIANTS = ("ordinary", "natural", "formal", "glossary", "combined", "alternative")
CAPABILITIES = (
    "styledTranslationVersion",
    "glossaryTranslationVersion",
    "styledGlossaryTranslationVersion",
    "alternativeTranslationVersion",
)


def prepare_report():
    return prepare_fixed_inputs(INPUTS, INPUT_SHA256, 8, include_refusal_checks=True)


def prepare_fixed_inputs(path, expected_sha256, count, *, include_refusal_checks):
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise styled_quality.CollectionError("fixed-input-sha256")
    document = json.loads(raw)
    samples = document.get("samples", [])
    if set(document) != {"schema", "samples"} or document["schema"] != 1 or len(samples) != count:
        raise styled_quality.CollectionError("fixed-input-schema-count")
    for index, sample in enumerate(samples):
        if (
            set(sample) != {"id", "direction", "text", "term", "semantic_anchors"}
            or sample["direction"] != DIRECTIONS[index % 2]
            or not valid_text(sample["text"])
            or len(sample["term"]) != 2
            or not all(valid_text(term) for term in sample["term"])
            or sample["term"][0] not in sample["text"]
            or not sample["semantic_anchors"]
            or not all(valid_text(anchor) for anchor in sample["semantic_anchors"])
        ):
            raise styled_quality.CollectionError("fixed-input-case-schema")
        parse_glossary(glossary(sample), {("zh", "en"), ("en", "zh")})
    return {
        **styled_quality.collection_report(
            expected_sha256,
            count * len(VARIANTS) + (3 if include_refusal_checks else 0),
            native="index",
        ),
        "method": "production-index-six-operations-fixed-zh-en-capture",
        "includeRefusalChecks": include_refusal_checks,
        "expectedPackage": {"id": PACKAGE_ID, "version": VERSION},
        "requiredCapabilities": dict.fromkeys(CAPABILITIES, 1),
        "reviewScope": f"{count} frozen sources, not a broad language-quality or speed benchmark.",
        "cases": [
            {
                **sample,
                "responses": dict.fromkeys(VARIANTS),
                "manualReview": dict.fromkeys(VARIANTS),
                "pairReview": dict.fromkeys(
                    ("meaningEquivalent", "styleDistinction", "alternativeUseful", "notes")
                ),
            }
            for sample in samples
        ],
        "refusals": [],
    }


def glossary(case):
    source, target = case["direction"].split("_")
    return {
        "version": 1,
        "rules": [
            {
                "sourceTerm": case["term"][0],
                "targetTerm": case["term"][1],
                "sourceLanguage": source,
                "targetLanguage": target,
            }
        ],
    }


async def exchange_case(process, report, identity, payload, *, expected_error=None):
    report["stage"] = identity
    payload = {"id": len(report["requests"]) + 1, **payload}
    record = {
        "id": payload["id"],
        "caseId": identity,
        "request": payload,
        "response": None,
        "status": "pending",
        "code": None,
        "workerElapsedMs": None,
    }
    report["requests"].append(record)
    started = time.monotonic()
    try:
        response = await asyncio.wait_for(
            styled_quality.exchange(process, payload), styled_quality.REQUEST_TIMEOUT
        )
        record.update(
            response=response,
            status=response.get("status"),
            code=response.get("code"),
            workerElapsedMs=response["elapsedMs"],
        )
        report["quality"] = "not-reviewed"
        if expected_error is not None:
            if (
                response.get("status") != "error"
                or response.get("code") != expected_error
                or response.get("text") != ""
            ):
                raise styled_quality.CollectionError("unexpected-index-refusal-response")
        elif (
            payload["op"] == "translate-alternative" and response.get("status") == "no-alternative"
        ):
            if response.get("text") != "" or response.get("code") != "":
                raise styled_quality.CollectionError("invalid-index-no-alternative-response")
        elif (
            response.get("status") != "translated"
            or response.get("code") != ""
            or not valid_text(response.get("text"))
        ):
            raise styled_quality.CollectionError("index-translation-not-completed-no-fallback")
        return response
    except TimeoutError:
        record.update(status="timeout", code="request-transport-timeout")
        raise
    except asyncio.CancelledError:
        record.update(status="interrupted", code="total-timeout-or-cancelled")
        raise
    except Exception as error:
        record["failureCode"] = (
            str(error)
            if isinstance(error, styled_quality.CollectionError)
            else type(error).__name__
        )
        if record["status"] == "pending":
            record.update(status="failed", code="transport-failed")
        raise
    finally:
        record["wallElapsedMs"] = round((time.monotonic() - started) * 1000, 2)


async def collect_cases(process, report):
    hello = report["hello"]
    if (
        any(type(hello.get(key)) is not int or hello[key] != 1 for key in CAPABILITIES)
        or len(hello["packages"]) != 1
        or {key: hello["packages"][0][key] for key in ("id", "version")}
        != report["expectedPackage"]
        or len(report["requests"]) != 1
    ):
        raise styled_quality.CollectionError("exact-index-capabilities-required-no-fallback")
    for case in report["cases"]:
        source, target = case["direction"].split("_")
        for variant in VARIANTS:
            payload = dict(op="translate", text=case["text"], source=source, target=target)
            if variant in ("natural", "formal", "combined"):
                payload.update(
                    op="translate-styled-v1", style="natural" if variant == "natural" else "formal"
                )
            if variant in ("glossary", "combined"):
                payload.update(
                    op="translate-styled-glossary-v1"
                    if variant == "combined"
                    else "translate-glossary-v1",
                    glossary=glossary(case),
                )
            if variant == "alternative":
                payload.update(
                    op="translate-alternative",
                    previousCandidate=case["responses"]["ordinary"]["text"],
                )
            protected_alternative = variant == "alternative" and (
                PROTECTED.search(case["text"]) or PROTECTED.search(payload["previousCandidate"])
            )
            response = await exchange_case(
                process,
                report,
                case["id"] + ":" + variant,
                payload,
                expected_error="alternative-unavailable" if protected_alternative else None,
            )
            case["responses"][variant] = response
            if variant == "alternative":
                case["alternativeComparison"] = (
                    "protected-source-or-candidate-unavailable"
                    if protected_alternative
                    else "no-alternative"
                    if response["status"] == "no-alternative"
                    else "same-normalized-text"
                    if alternative_key(response["text"])
                    == alternative_key(payload["previousCandidate"])
                    else "different-text-not-a-meaning-verdict"
                )
    if not report["includeRefusalChecks"]:
        return
    base = dict(
        op="translate-alternative",
        text="请稍后回复。",
        source="zh",
        target="en",
        previousCandidate="Please reply later.",
    )
    refusals = (
        (
            "protected-source",
            {**base, "text": "请打开 https://example.test"},
            "alternative-unavailable",
        ),
        ("combined-style", {**base, "style": "formal"}, "invalid-request"),
        ("preserved-draft", {**base, "preserved": "manual draft"}, "invalid-request"),
    )
    for identity, payload, expected in refusals:
        response = await exchange_case(process, report, identity, payload, expected_error=expected)
        report["refusals"].append({"id": identity, "response": response, "manualReview": None})


async def collect(args, report):
    await styled_quality.collect(
        args,
        report,
        collect_cases=collect_cases,
        required_directions=DIRECTIONS,
        capability="alternativeTranslationVersion",
        native="index",
    )


if __name__ == "__main__":
    raise SystemExit(styled_quality.main(prepare_report, collect, __doc__, native="index"))
