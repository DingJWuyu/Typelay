"""Fixed ordinary Argos A/B, same installed models; no download or production edit."""

from __future__ import annotations

import argparse
import asyncio
import copy
import hashlib
import json
import os
import platform
import sys
import time
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent))
import alternative_quality as quality  # noqa: E402

HOLDOUT = Path(__file__).with_name("argos_repetition_holdout.json")
HOLDOUT_SHA256 = "7fb5abd79757e67142a3e3be78f4457ea474550eda317f9bdaea7a379b0a6752"
TOTAL_SECONDS = 600


def holdout_samples():
    raw = HOLDOUT.read_bytes()
    if hashlib.sha256(raw).hexdigest() != HOLDOUT_SHA256:
        raise ValueError("frozen-holdout-sha256")
    samples = json.loads(raw)["samples"]
    if len(samples) != 8 or [s["direction"] for s in samples] != ["zh_en"] * 4 + ["en_zh"] * 4:
        raise ValueError("frozen-holdout-count-or-order")
    return samples


def prepare_report(args):
    variants = []
    for penalty in (1.05, 1.0):
        variant = quality.prepare_report(args)
        template = copy.deepcopy(variant["cases"][0])
        variant["cases"] += [{**copy.deepcopy(template), **sample} for sample in holdout_samples()]
        for case in variant["cases"]:
            case.pop("alternative")
            case.pop("comparison")
            case["manualReview"].pop("alternative")
        variant.update(
            repetitionPenalty=penalty,
            holdoutSha256=HOLDOUT_SHA256,
            method="instrumented-production-ordinary-repetition-comparison",
            maximumTranslationRequests=24,
        )
        variants.append(variant)
    return {
        "schema": 1,
        "status": "not-run",
        "quality": "not-run",
        "maximumTranslationRequests": 48,
        "totalTimeoutSeconds": TOTAL_SECONDS,
        "cleanupAllowanceSecondsPerWorker": 7,
        "configurationCount": 2,
        "cpu": {
            "machine": platform.machine(),
            "processor": platform.processor(),
            "count": os.cpu_count(),
        },
        "experimentSourceSha256": {
            name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
            for name in (
                "argos_repetition_comparison.py",
                "alternative_quality.py",
                "alternative_boundary_worker.py",
            )
        },
        "variants": variants,
    }


async def collect(args, report):
    started = time.monotonic()
    deadline = started + TOTAL_SECONDS
    try:
        for variant in report["variants"]:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                report.update(status="failed", failureCode="comparison-budget-exhausted")
                return
            options = SimpleNamespace(**vars(args))
            options.total_timeout = remaining
            options.ordinary_repetition = variant["repetitionPenalty"]
            variant["totalTimeoutSeconds"] = remaining
            first = report["variants"][0]
            if variant is not first and quality.installation(options) != first["installation"]:
                report.update(status="failed", failureCode="installation-changed-before-variant")
                return
            await quality.collect(options, variant)
            if variant["quality"] == "not-reviewed":
                report["quality"] = "not-reviewed"
            if variant["status"] != "completed" or not variant.get("workerExitConfirmed"):
                report.update(status="failed", failureCode="variant-incomplete-or-exit-unconfirmed")
                return
            if len(variant["requests"]) != 25 or any(
                request["op"] != "translate" for request in variant["requests"][1:]
            ):
                report.update(status="failed", failureCode="ordinary-request-count")
                return
            first = report["variants"][0]
            if (
                variant["installation"] != first["installation"]
                or variant["requests"][0]["decodingBoundary"]["packages"]
                != first["requests"][0]["decodingBoundary"]["packages"]
            ):
                report.update(status="failed", failureCode="installation-changed-between-variants")
                return
        report.update(status="completed", quality="not-reviewed")
    finally:
        report["elapsedSeconds"] = round(time.monotonic() - started, 3)


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--python", type=Path)
    parser.add_argument("--models", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--validate-only", action="store_true")
    args = parser.parse_args()
    args.output = args.output.resolve()
    if not args.output.is_relative_to(quality.ROOT / "artifacts") or args.output.suffix != ".json":
        parser.error("--output must be a new JSON under this checkout's artifacts/")
    if not args.validate_only and (sys.platform != "linux" or not args.python or not args.models):
        parser.error("Linux and same-installation --python and --models required")
    args.capture_decoding_boundary = True
    args.request_timeout = 90
    args.total_timeout = TOTAL_SECONDS
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = {"schema": 1, "status": "not-run", "quality": "not-run", "variants": []}
    with args.output.open("x", encoding="utf8") as stream:
        try:
            report = prepare_report(args)
            if not args.validate_only:
                asyncio.run(collect(args, report))
        except (KeyboardInterrupt, asyncio.CancelledError):
            report.update(status="interrupted", failureCode="cancelled")
        except Exception as error:
            report.update(status="failed", failureCode=type(error).__name__)
        finally:
            json.dump(report, stream, ensure_ascii=False, indent=2, allow_nan=False)
            stream.write("\n")
    print(json.dumps({"status": report["status"], "quality": report["quality"]}))
    return 0 if report["status"] == "completed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
