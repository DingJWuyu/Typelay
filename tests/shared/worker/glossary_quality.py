"""Collect five fixed glossary cases through one installed ABI5 Hy worker on Linux.

Reuse the styled collector's production identity, inventory/hello gates and fixed
budgets. Expected refusals are evidence to review, never an automatic quality pass.
No model downloads, installation, retries, fallback or output replacement.
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import time
from pathlib import Path

from styled_quality import (
    REQUEST_TIMEOUT,
    CollectionError,
    collect,
    collection_report,
    exchange,
    main,
)
from typelay_worker.catalog import load_package_catalog
from typelay_worker.glossary import parse_glossary
from typelay_worker.protocol import valid_text

INPUTS = Path(__file__).with_name("glossary_quality_inputs.json")
INPUT_SHA256 = "4330b0d550f16080b577561ef871077253d9d3b9a661ae78faceb36482a3e6b9"
DIRECTIONS = ("zh_fr", "fr_zh")
CATEGORIES = (
    "directed-over-global",
    "numbers-and-negation",
    "protected-preserved",
    "protected-refusal",
    "style-glossary-refusal",
)


def prepare_report():
    raw = INPUTS.read_bytes()
    if hashlib.sha256(raw).hexdigest() != INPUT_SHA256:
        raise CollectionError("fixed-input-sha256")
    inputs = json.loads(raw)
    samples = inputs["samples"]
    if (
        set(inputs) != {"schema", "samples"}
        or type(inputs["schema"]) is not int
        or inputs["schema"] != 1
        or len(samples) != 5
        or tuple(sample["category"] for sample in samples) != CATEGORIES
        or len({sample["id"] for sample in samples}) != 5
    ):
        raise CollectionError("fixed-input-schema-count-or-order")
    directions = {
        (direction.source, direction.target)
        for package in load_package_catalog().packages
        for direction in package.directions
    }
    for sample in samples:
        payload = sample["request"]
        combined = sample["category"] == "style-glossary-refusal"
        expected_fields = {"op", "text", "source", "target", "glossary"}
        if combined:
            expected_fields.add("style")
        if (
            set(sample)
            != {
                "id",
                "category",
                "request",
                "expectedResponse",
                "semantic_anchors",
                "protected_literals",
            }
            or set(payload) != expected_fields
            or payload["op"] != "translate-glossary-v1"
            or f"{payload['source']}_{payload['target']}" not in DIRECTIONS
            or not valid_text(payload["text"])
            or (combined and payload["style"] != "formal")
            or not sample["semantic_anchors"]
            or any(not valid_text(anchor) for anchor in sample["semantic_anchors"])
            or any(
                not literal or literal not in payload["text"]
                for literal in sample["protected_literals"]
            )
        ):
            raise CollectionError("fixed-input-case-schema")
        parse_glossary(payload["glossary"], directions)
        expected = {"status": "translated", "code": ""}
        if sample["category"] == "protected-refusal":
            expected = {"status": "error", "code": "glossary-protected-conflict"}
        elif combined:
            expected = {"status": "error", "code": "invalid-request"}
        if sample["expectedResponse"] != expected:
            raise CollectionError("fixed-input-response-schema")
    return {
        **collection_report(INPUT_SHA256, len(samples)),
        "method": "production-translate-glossary-v1-fixed-cases-including-explicit-refusals",
        "requiredGlossaryTranslationVersion": 1,
        "cases": [
            {
                **sample,
                "response": None,
                "manualReview": dict.fromkeys(
                    (
                        "terminology",
                        "directedOverGlobal",
                        "protectedStructure",
                        "refusalAppropriate",
                        "negation",
                        "numbers",
                        "subject",
                        "state",
                        "tenseCondition",
                        "addedMeaning",
                        "omittedMeaning",
                        "naturalness",
                        "seriousMeaningError",
                        "notes",
                    )
                ),
            }
            for sample in samples
        ],
    }


async def collect_cases(process, report):
    for case in report["cases"]:
        report["stage"] = case["id"]
        payload = {"id": len(report["requests"]) + 1, **case["request"]}
        record = {
            "id": payload["id"],
            "caseId": case["id"],
            "request": payload,
            "response": None,
            "status": "pending",
            "code": None,
            "workerElapsedMs": None,
        }
        report["requests"].append(record)
        started = time.monotonic()
        try:
            response = await asyncio.wait_for(exchange(process, payload), REQUEST_TIMEOUT)
            # Keep the entire response, even unexpected refusals or partial/invalid output.
            # Do not replace terms, normalize text, select alternatives or retry.
            record.update(
                response=response,
                status=response.get("status"),
                code=response.get("code"),
                workerElapsedMs=response["elapsedMs"],
            )
            case["response"] = response
            report["quality"] = "not-reviewed"
            expected = case["expectedResponse"]
            if any(response.get(key) != value for key, value in expected.items()):
                raise CollectionError("unexpected-glossary-response-no-fallback")
            if expected["status"] == "translated":
                if not valid_text(response.get("text")):
                    raise CollectionError("invalid-glossary-output-no-fallback")
            elif response.get("text") != "":
                raise CollectionError("glossary-refusal-must-not-publish-text")
        except TimeoutError:
            record.update(status="timeout", code="request-transport-timeout")
            raise
        except asyncio.CancelledError:
            record.update(status="interrupted", code="total-timeout-or-cancelled")
            raise
        except Exception as error:
            record["failureCode"] = (
                str(error) if isinstance(error, CollectionError) else type(error).__name__
            )
            if record["status"] == "pending":
                record.update(status="failed", code="transport-failed")
            raise
        finally:
            record["wallElapsedMs"] = round((time.monotonic() - started) * 1000, 2)


async def collect_glossary(args, report):
    await collect(
        args,
        report,
        collect_cases=collect_cases,
        required_directions=DIRECTIONS,
        capability="glossaryTranslationVersion",
    )


if __name__ == "__main__":
    raise SystemExit(main(prepare_report, collect_glossary, __doc__))
