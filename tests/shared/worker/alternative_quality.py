"""Collect the frozen Argos quality cases through one real worker on Linux.

No download, installation, self-check, mock engine, or semantic pass verdict.
The production protocol exposes a selected alternative, not the raw n-best list;
each case therefore needs one ordinary and one alternative request (at most 32).
"""

from __future__ import annotations

import argparse
import asyncio
import hashlib
import io
import json
import math
import os
import signal
import struct
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
WORKER = ROOT / "src/shared/TypeLay.Worker/run_worker.py"
INPUTS = Path(__file__).with_name("alternative_quality_inputs.json")
INPUT_SHA256 = "87452b28c414bc964afbd84542480dd46a65c0b4e15c42ad36422270413bcbe5"
sys.path.insert(0, str(WORKER.parent))
from typelay_worker.catalog import load_package_catalog, read_active_selection  # noqa: E402
from typelay_worker.engine import alternative_key  # noqa: E402
from typelay_worker.protocol import MAX_FRAME, read_frame, valid_text, write_frame  # noqa: E402


def positive_seconds(value):
    seconds = float(value)
    if not math.isfinite(seconds) or seconds <= 0:
        raise argparse.ArgumentTypeError("timeout must be finite and positive")
    return seconds


def arguments():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--python", type=Path, help="existing official installation's venv Python")
    parser.add_argument("--models", type=Path, help="same installation's Argos model directory")
    parser.add_argument("--output", type=Path, required=True, help="new JSON file under artifacts/")
    parser.add_argument("--request-timeout", type=positive_seconds, default=90)
    parser.add_argument("--total-timeout", type=positive_seconds, default=600)
    parser.add_argument(
        "--validate-only", action="store_true", help="no worker startup or native model imports"
    )
    args = parser.parse_args()
    args.output = args.output.resolve()
    if not args.output.is_relative_to(ROOT / "artifacts") or args.output.suffix != ".json":
        parser.error("--output must be a JSON file under this checkout's artifacts/")
    if args.output.exists():
        parser.error("--output already exists; preserve earlier results")
    if not args.validate_only and (args.python is None or args.models is None):
        parser.error("--python and --models are required unless --validate-only")
    if not args.validate_only and sys.platform != "linux":
        parser.error("this runner's owned process-group cleanup requires Linux")
    return args


def prepare_report(args):
    raw = INPUTS.read_bytes()
    if hashlib.sha256(raw).hexdigest() != INPUT_SHA256:
        raise ValueError("frozen-input-sha256")
    samples = json.loads(raw)["samples"]
    if len(samples) != 16 or [s["direction"] for s in samples] != ["zh_en"] * 8 + ["en_zh"] * 8:
        raise ValueError("frozen-input-count-or-order")
    return {
        "schema": 1,
        "status": "not-run",
        "quality": "not-run",
        "stage": "input-validation",
        "inputSha256": INPUT_SHA256,
        "workerSourceSha256": {
            name: hashlib.sha256((WORKER.parent / name).read_bytes()).hexdigest()
            for name in ("run_worker.py", "typelay_worker/engine.py", "typelay_worker/inventory.py")
        },
        "method": "production-translate-then-translate-alternative",
        "rawNBestExposed": False,
        "maximumTranslationRequests": 32,
        "requestTimeoutSeconds": args.request_timeout,
        "totalTimeoutSeconds": args.total_timeout,
        "cleanupAllowanceSeconds": 7,
        "workerStarts": 0,
        "workerExitCode": None,
        "workerExitConfirmed": None,
        "requests": [],
        "cases": [
            {
                **sample,
                "ordinary": None,
                "alternative": None,
                "comparison": None,
                "manualReview": {
                    candidate: dict.fromkeys(
                        (
                            "negation",
                            "numbers",
                            "subject",
                            "tenseCondition",
                            "tone",
                            "addedMeaning",
                            "omittedMeaning",
                            "naturalness",
                            "seriousMeaningError",
                            "notes",
                        )
                    )
                    for candidate in ("ordinary", "alternative")
                },
            }
            for sample in samples
        ],
    }


def installation(args):
    # Resolve current to a fixed generation, but preserve the venv Python filename.
    args.models = args.models.resolve(strict=True)
    runtime = args.models.parent / "runtime"
    args.python = args.python.parent.resolve(strict=True) / args.python.name
    if (
        args.models.name != "models"
        or args.python != runtime / "venv/bin/python"
        or not args.python.is_file()
        or not os.access(args.python, os.X_OK)
        or not args.python.resolve(strict=True).is_relative_to(runtime)
        or not (runtime / "venv/pyvenv.cfg").is_file()
        or not (runtime / "requirements.lock").is_file()
    ):
        raise ValueError("requires-same-installed-runtime-and-models")
    with (args.models / "installed.json").open("rb") as stream:
        raw = stream.read(8 * 1024 * 1024 + 1)
    if len(raw) > 8 * 1024 * 1024:
        raise ValueError("model-inventory")
    inventory = json.loads(raw)
    selected = read_active_selection(inventory, load_package_catalog())
    if {item.package.id for item in selected} != {
        "argos-zh-en",
        "argos-en-zh",
    }:
        raise ValueError("requires-installed-argos-pair")
    # Intent is not Ready. The real worker must verify all files and load both models.
    return {
        "inventorySha256": hashlib.sha256(raw).hexdigest(),
        "schema": inventory["schema"],
        "models": str(args.models),
        "python": str(args.python),
        "expectedPackages": [
            {
                "id": item.package.id,
                "version": item.package.version,
                "directions": [direction.id for direction in item.directions],
            }
            for item in selected
        ],
    }


async def exchange(process, request):
    frame = io.BytesIO()
    write_frame(frame, request)
    process.stdin.write(frame.getvalue())
    await process.stdin.drain()
    header = await process.stdout.readexactly(4)
    size = struct.unpack("<i", header)[0]
    if not 0 < size <= MAX_FRAME:
        raise ValueError("frame-size")
    response = read_frame(io.BytesIO(header + await process.stdout.readexactly(size)))
    elapsed = response.get("elapsedMs")
    if (
        response.get("id") != request["id"]
        or type(elapsed) not in (int, float)
        or (not math.isfinite(elapsed) or elapsed < 0)
    ):
        raise ValueError("response-sequence-or-time")
    return response


async def request(process, report, args, operation, case=None, previous=None):
    payload = {"id": len(report["requests"]) + 1, "op": operation}
    if case is not None:
        source, target = case["direction"].split("_")
        payload.update(text=case["text"], source=source, target=target)
    if previous is not None:
        payload["previousCandidate"] = previous
    record = {
        "id": payload["id"],
        "op": operation,
        "caseId": case["id"] if case else None,
        "direction": case["direction"] if case else None,
        "status": "pending",
        "code": None,
        "text": None,
        "workerElapsedMs": None,
    }
    report["requests"].append(record)
    started = time.monotonic()
    try:
        response = await asyncio.wait_for(exchange(process, payload), args.request_timeout)
        record.update(
            status=response.get("status"),
            code=response.get("code", ""),
            text=response.get("text"),
            workerElapsedMs=response["elapsedMs"],
        )
        if operation == "hello":
            expected = report["installation"]
            if (
                response.get("status") != "ready"
                or (
                    (expected["schema"] == 3 or response.get("packages") is not None)
                    and response.get("packages") != expected["expectedPackages"]
                )
                or sorted(response.get("directions", [])) != ["en_zh", "zh_en"]
            ):
                raise ValueError("hello-packages-or-directions")
        elif response.get("status") == "translated":
            if not valid_text(response.get("text")) or response.get("code") != "":
                raise ValueError("translation-response")
            report["quality"] = "not-reviewed"
        elif (
            operation != "translate-alternative"
            or response.get("status") != "no-alternative"
            or (response.get("text") != "" or response.get("code") != "")
        ):
            raise ValueError("translation-not-completed")
        return response
    except TimeoutError:
        record.update(status="timeout", code="request-timeout")
        raise
    except asyncio.CancelledError:
        record.update(status="interrupted", code="total-timeout-or-cancelled")
        raise
    except Exception as error:
        record["failureCode"] = type(error).__name__
        if record["status"] == "pending":
            record.update(status="failed", code="transport-failed")
        raise
    finally:
        record["wallElapsedMs"] = round((time.monotonic() - started) * 1000, 2)


async def cleanup(process, report):
    process.stdin.close()
    # One owned process group, bounded EOF/TERM/KILL; no restart or retry of cases.
    loop = asyncio.get_running_loop()
    deadline = loop.time() + 7
    for signum, seconds in ((None, 3), (signal.SIGTERM, 2), (signal.SIGKILL, 2)):
        if signum is not None:
            try:
                os.killpg(process.pid, signum)
            except ProcessLookupError:
                break
        until = min(deadline, loop.time() + seconds)
        group_gone = False
        while loop.time() < until:
            try:
                os.killpg(process.pid, 0)
            except ProcessLookupError:
                group_gone = True
            if process.returncode is not None and group_gone:
                break
            await asyncio.sleep(min(0.05, max(0, until - loop.time())))
        if process.returncode is not None and group_gone:
            break
    report["workerExitCode"] = process.returncode
    try:
        os.killpg(process.pid, 0)
        group_gone = False
    except ProcessLookupError:
        group_gone = True
    report["workerExitConfirmed"] = process.returncode is not None and group_gone
    if not report["workerExitConfirmed"] or process.returncode != 0:
        report.update(status="failed", cleanupCode="worker-exit-unconfirmed-or-nonzero")


async def collect(args, report):
    process = None
    started = time.monotonic()
    loop = asyncio.get_running_loop()
    previous_handlers = {
        signum: signal.getsignal(signum) for signum in (signal.SIGINT, signal.SIGTERM)
    }
    for signum in previous_handlers:
        loop.add_signal_handler(signum, asyncio.current_task().cancel)
    try:
        async with asyncio.timeout(args.total_timeout):
            report["stage"] = "installation-intent"
            report["installation"] = installation(args)
            report["stage"] = "load-and-hello"
            spawn = asyncio.create_task(
                asyncio.create_subprocess_exec(
                    str(args.python),
                    "-I",
                    "-B",
                    str(WORKER),
                    "--models",
                    str(args.models),
                    stdin=asyncio.subprocess.PIPE,
                    stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.DEVNULL,
                    start_new_session=True,
                    limit=MAX_FRAME + 4,
                )
            )
            try:
                process = await asyncio.shield(spawn)
            except asyncio.CancelledError:
                # Acquire the one process owner before cancellation reaches cleanup.
                for signum in previous_handlers:
                    loop.add_signal_handler(signum, lambda: report.update(cleanupInterrupted=True))
                process = await spawn
                report["workerStarts"] = 1
                raise
            report["workerStarts"] = 1
            await request(process, report, args, "hello")
            for case in report["cases"]:
                report["stage"] = case["id"] + ":ordinary"
                case["ordinary"] = await request(process, report, args, "translate", case)
                report["stage"] = case["id"] + ":alternative"
                case["alternative"] = await request(
                    process, report, args, "translate-alternative", case, case["ordinary"]["text"]
                )
                first, second = case["ordinary"]["text"], case["alternative"]["text"]
                case["comparison"] = {
                    "exactDuplicate": first == second if second else None,
                    "caseWhitespacePunctuationOnly": (
                        first != second and alternative_key(first) == alternative_key(second)
                        if second
                        else None
                    ),
                    "differentText": first != second if second else None,
                }
            report.update(status="completed", stage="completed-unreviewed")
    except TimeoutError:
        report.update(status="failed", failureCode="timeout")
    except asyncio.CancelledError:
        report.update(status="interrupted", failureCode="cancelled")
    except Exception as error:
        # Keep fixed stage and exception type, never arbitrary exception/user text.
        report.update(status="failed", failureCode=type(error).__name__)
    finally:
        # Delay further Ctrl-C/TERM until the bounded cleanup finishes.
        for signum in previous_handlers:
            loop.add_signal_handler(signum, lambda: report.update(cleanupInterrupted=True))
        try:
            if process is not None:
                await cleanup(process, report)
            if report.get("cleanupInterrupted") and report["status"] == "completed":
                report.update(status="interrupted", failureCode="cancelled-during-cleanup")
        finally:
            report["elapsedSeconds"] = round(time.monotonic() - started, 3)
            for signum, handler in previous_handlers.items():
                loop.remove_signal_handler(signum)
                signal.signal(signum, handler)


def main():
    args = arguments()
    report = {"schema": 1, "status": "not-run", "quality": "not-run", "stage": "input-validation"}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # Reserve output before a model starts, so an unwritable destination cannot lose a run.
    with args.output.open("x", encoding="utf8") as output:
        try:
            report.update(prepare_report(args))
            if not args.validate_only:
                asyncio.run(collect(args, report))
        except (KeyboardInterrupt, asyncio.CancelledError):
            report.update(status="interrupted", failureCode="cancelled-during-cleanup")
        except Exception as error:
            report.update(status="failed", failureCode=type(error).__name__)
        finally:
            if report.get("workerStarts") and not report.get("workerExitConfirmed"):
                report.update(status="failed", cleanupCode="worker-exit-unconfirmed")
            output.write(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + "\n")
    print(
        json.dumps(
            {"status": report["status"], "quality": report["quality"], "output": str(args.output)},
            ensure_ascii=False,
        )
    )
    return 0 if report["status"] == "completed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
