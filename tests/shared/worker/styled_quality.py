"""Collect fixed Natural/Formal pairs through one installed Hy worker on Linux.

Only production translate-styled-v1 is used. No download, installation, self-check,
model substitution, retry, fallback, or automated language-quality verdict.
Four original pairs are always kept; --include-holdout appends four frozen pairs.
"""

from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import os
import signal
import sys
import time
from pathlib import Path

# These helpers have no startup side effects; alternative_quality.main is guarded.
from alternative_quality import MAX_FRAME, ROOT, WORKER, cleanup, exchange
from typelay_worker.catalog import load_package_catalog, read_active_selection
from typelay_worker.protocol import valid_text

INPUTS = Path(__file__).with_name("styled_quality_inputs.json")
INPUT_SHA256 = "6d5a77c5369be23cec23e526cb89f102e7c2726e3d84221e48d6572c9b62576c"
HOLDOUT_INPUTS = Path(__file__).with_name("styled_quality_holdout_inputs.json")
HOLDOUT_SHA256 = "f837e7a5983bc2303c3c279052cc05166ba2cfe05d6dccd0f364fe15896d3616"
DIRECTIONS = ("zh_ja", "ja_zh", "zh_fr", "fr_zh")
STYLES = ("natural", "formal")
ADDRESS_SPACE_BYTES = 5 * 1024**3 // 2
REQUEST_TIMEOUT = 65  # The production request still owns its original 60-second deadline.
HELLO_TIMEOUT = 90
TOTAL_TIMEOUT = 600


class CollectionError(ValueError):
    """Only fixed, non-private failure codes are exposed in the report."""


def arguments(description=__doc__, *, allow_holdout=False):
    parser = argparse.ArgumentParser(description=description, allow_abbrev=False)
    parser.add_argument("--python", type=Path, help="existing official installation's venv Python")
    parser.add_argument(
        "--models", type=Path, help="same installation's schema3 Hy model directory"
    )
    parser.add_argument(
        "--worker", type=Path, help="matching production bundle's worker/run_worker.py"
    )
    parser.add_argument("--output", type=Path, required=True, help="new JSON file under artifacts/")
    parser.add_argument(
        "--validate-only", action="store_true", help="inputs only; no worker startup"
    )
    if allow_holdout:
        parser.add_argument(
            "--include-holdout",
            action="store_true",
            help="append four frozen pairs; at most 16 requests within the same 600 seconds",
        )
    args = parser.parse_args()
    args.output = args.output.resolve()
    if not args.output.is_relative_to(ROOT / "artifacts") or args.output.suffix != ".json":
        parser.error("--output must be a JSON file under this checkout's artifacts/")
    if args.output.exists():
        parser.error("--output already exists; preserve earlier results")
    if not args.validate_only and any(
        value is None for value in (args.python, args.models, args.worker)
    ):
        parser.error("--python, --models and --worker are required unless --validate-only")
    if not args.validate_only and sys.platform != "linux":
        parser.error("this runner's resource limit and owned process-group cleanup require Linux")
    return args


def source_hashes():
    paths = [
        WORKER,
        *sorted((WORKER.parent / "typelay_worker").glob("*.py")),
        *(WORKER.parent / name for name in ("requirements.txt", "model-catalog.json")),
        *(
            WORKER.parent / "native/hy" / name
            for name in ("model.lock.json", "cpu-runtime.lock.json")
        ),
    ]
    return {
        str(path.relative_to(WORKER.parent)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in paths
    }


def collection_report(input_sha256, maximum_requests):
    """The styled and glossary collectors share the same production limits and evidence."""
    return {
        "schema": 1,
        "status": "not-run",
        "quality": "not-run",
        "stage": "input-validation",
        "inputSha256": input_sha256,
        "workerSourceSha256": source_hashes(),
        "requiredNativeAbi": 5,
        "maximumTranslationRequests": maximum_requests,
        "configuredLimits": {
            "workerRequestSeconds": 60,
            "requestTransportTimeoutSeconds": REQUEST_TIMEOUT,
            "loadAndHelloTimeoutSeconds": HELLO_TIMEOUT,
            "totalTimeoutSeconds": TOTAL_TIMEOUT,
            "cleanupAllowanceSeconds": 7,
            "workerAddressSpaceBytesMaximum": ADDRESS_SPACE_BYTES,
            "modelHandles": 1,
            "parallelRequests": 1,
            "nativeThreads": 2,
            "contextTokens": 1024,
            "maximumNewTokens": 192,
            "sampling": "greedy",
            "quantization": "Q4_K_M",
        },
        "nativeTokenMetricsExposed": False,
        "workerStarts": 0,
        "workerExitCode": None,
        "workerExitConfirmed": None,
        "assetsValidatedByWorker": False,
        "hello": None,
        "requests": [],
    }


def prepare_report(*, include_holdout=False):
    sources = [(INPUTS, INPUT_SHA256)]
    if include_holdout:
        sources.append((HOLDOUT_INPUTS, HOLDOUT_SHA256))
    samples = []
    for path, expected_sha256 in sources:
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != expected_sha256:
            raise CollectionError("fixed-input-sha256")
        inputs = json.loads(raw)
        selected = inputs["samples"]
        if (
            inputs["schema"] != 1
            or len(selected) != 4
            or tuple(sample["direction"] for sample in selected) != DIRECTIONS
            or any(not valid_text(sample["text"]) for sample in selected)
        ):
            raise CollectionError("fixed-input-schema-count-or-order")
        samples.extend(selected)
    if len({sample["id"] for sample in samples}) != len(samples):
        raise CollectionError("fixed-input-schema-count-or-order")
    return {
        **collection_report(INPUT_SHA256, len(samples) * len(STYLES)),
        **({"holdoutInputSha256": HOLDOUT_SHA256} if include_holdout else {}),
        "method": "production-translate-styled-v1-natural-then-formal",
        "requiredStyledTranslationVersion": 1,
        "cases": [
            {
                **sample,
                "natural": None,
                "formal": None,
                "manualReview": {
                    style: dict.fromkeys(
                        (
                            "negation",
                            "numbers",
                            "subject",
                            "state",
                            "tenseCondition",
                            "commitmentStrength",
                            "addedMeaning",
                            "omittedMeaning",
                            "naturalness",
                            "styleSuitability",
                            "seriousMeaningError",
                            "notes",
                        )
                    )
                    for style in STYLES
                },
                "pairReview": dict.fromkeys(("meaningEquivalent", "styleDistinction", "notes")),
            }
            for sample in samples
        ],
    }


def installation(args, required_directions=DIRECTIONS):
    # Pin current to one generation without following the venv Python leaf alias.
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
        raise CollectionError("requires-same-installed-runtime-and-models")
    with (args.models / "installed.json").open("rb") as stream:
        raw = stream.read(8 * 1024 * 1024 + 1)
    if len(raw) > 8 * 1024 * 1024:
        raise CollectionError("model-inventory-size")
    inventory = json.loads(raw)
    if type(inventory.get("schema")) is not int or inventory["schema"] != 3:
        raise CollectionError("requires-schema3-hy")
    selected = read_active_selection(inventory, load_package_catalog())
    if len(selected) != 1 or selected[0].package.backend != "hy-mt-llama-cpu":
        raise CollectionError("requires-one-active-hy-package")
    directions = [direction.id for direction in selected[0].directions]
    if not set(required_directions).issubset(directions):
        raise CollectionError("installed-active-directions-do-not-cover-fixed-inputs")
    # This reads intent only. Production verification and real hello establish capability.
    return {
        "inventorySha256": hashlib.sha256(raw).hexdigest(),
        "schema": 3,
        "models": str(args.models),
        "python": str(args.python),
        "expectedPackages": [
            {
                "id": selected[0].package.id,
                "version": selected[0].package.version,
                "directions": directions,
            }
        ],
        "expectedDirections": directions,
    }


def production_worker(args, expected_hashes):
    args.worker = args.worker.resolve(strict=True)
    if args.worker.name != "run_worker.py":
        raise CollectionError("requires-production-worker-entry")
    package = args.worker.parent
    if {path.name for path in package.iterdir()} != {
        "run_worker.py",
        "requirements.txt",
        "model-catalog.json",
        "typelay_worker",
        "native",
    }:
        raise CollectionError("production-worker-layout-mismatch")
    # Extra packages or bytecode could shadow the source that was actually compared.
    modules = {str(path.relative_to(package)) for path in (package / "typelay_worker").iterdir()}
    if modules != {name for name in expected_hashes if name.startswith("typelay_worker/")}:
        raise CollectionError("production-python-module-set-mismatch")
    for name, expected in expected_hashes.items():
        # Bound reads to the known source length; do not hash arbitrary installed files.
        with (package / name).open("rb") as stream:
            actual = stream.read((WORKER.parent / name).stat().st_size + 1)
        if hashlib.sha256(actual).hexdigest() != expected:
            raise CollectionError("production-worker-source-or-lock-mismatch")
    with (package / "native/hy/production-adapter.json").open("rb") as stream:
        raw = stream.read(128 * 1024 + 1)
    if len(raw) > 128 * 1024:
        raise CollectionError("production-adapter-manifest-size")
    manifest = json.loads(raw)
    if type(manifest.get("abi")) is not int or manifest["abi"] != 5:
        raise CollectionError("requires-abi5-production-adapter")
    return {
        "entry": str(args.worker),
        "sourceMatchesCheckout": True,
        "productionAdapterManifestSha256": hashlib.sha256(raw).hexdigest(),
        "manifestAbi": manifest["abi"],
        "manifestAdapter": manifest["adapter"],
        # Actual native closure, hashes, exports and model identity remain worker-owned.
        "assetValidation": "pending-real-worker-hello",
    }


def worker_limits():
    import resource

    soft, hard = resource.getrlimit(resource.RLIMIT_AS)
    bound = min(
        value for value in (ADDRESS_SPACE_BYTES, soft, hard) if value != resource.RLIM_INFINITY
    )
    resource.setrlimit(resource.RLIMIT_AS, (bound, bound))


async def request(process, report, case=None, style=None, *, capability="styledTranslationVersion"):
    payload = {"id": len(report["requests"]) + 1, "op": "hello"}
    if case is not None:
        source, target = case["direction"].split("_")
        payload.update(
            op="translate-styled-v1", text=case["text"], source=source, target=target, style=style
        )
    record = {
        "id": payload["id"],
        "op": payload["op"],
        "caseId": case["id"] if case else None,
        "direction": case["direction"] if case else None,
        "style": style,
        "status": "pending",
        "code": None,
        "workerElapsedMs": None,
    }
    report["requests"].append(record)
    started = time.monotonic()
    try:
        timeout = HELLO_TIMEOUT if case is None else REQUEST_TIMEOUT
        response = await asyncio.wait_for(exchange(process, payload), timeout)
        record.update(
            status=response.get("status"),
            code=response.get("code"),
            workerElapsedMs=response["elapsedMs"],
        )
        if case is None:
            report["hello"] = response
            expected = report["installation"]
            if (
                response.get("status") != "ready"
                or response.get("packages") != expected["expectedPackages"]
                or response.get("directions") != expected["expectedDirections"]
            ):
                raise CollectionError("hello-packages-or-directions")
            if type(response.get(capability)) is not int or response[capability] != 1:
                name = capability.removesuffix("TranslationVersion")
                raise CollectionError(f"hello-{name}-translation-v1-required-no-fallback")
            report["assetsValidatedByWorker"] = True
            report["productionWorker"]["assetValidation"] = "real-worker-hello-verified"
        else:
            case[style] = response  # Preserve even a refusal before stopping, with no fallback.
            if (
                response.get("status") != "translated"
                or response.get("code") != ""
                or not valid_text(response.get("text"))
            ):
                raise CollectionError("styled-translation-not-completed-no-fallback")
            report["quality"] = "not-reviewed"
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


async def collect_styled_cases(process, report):
    for case in report["cases"]:
        for style in STYLES:
            report["stage"] = case["id"] + ":" + style
            await request(process, report, case, style)


async def collect(
    args,
    report,
    *,
    collect_cases=collect_styled_cases,
    required_directions=DIRECTIONS,
    capability="styledTranslationVersion",
):
    process = None
    started = time.monotonic()
    loop = asyncio.get_running_loop()
    previous_handlers = {
        signum: signal.getsignal(signum) for signum in (signal.SIGINT, signal.SIGTERM)
    }
    for signum in previous_handlers:
        loop.add_signal_handler(signum, asyncio.current_task().cancel)
    try:
        async with asyncio.timeout(TOTAL_TIMEOUT):
            report["stage"] = "installation-intent"
            report["installation"] = installation(args, required_directions)
            report["stage"] = "production-worker-identity"
            report["productionWorker"] = production_worker(args, report["workerSourceSha256"])
            report["stage"] = "load-and-hello"
            spawn = asyncio.create_task(
                asyncio.create_subprocess_exec(
                    str(args.python),
                    "-I",
                    "-B",
                    str(args.worker),
                    "--models",
                    str(args.models),
                    stdin=asyncio.subprocess.PIPE,
                    stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.DEVNULL,
                    start_new_session=True,
                    preexec_fn=worker_limits,
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
            await request(process, report, capability=capability)
            await collect_cases(process, report)
            report.update(status="completed", stage="completed-unreviewed")
    except TimeoutError:
        report.update(status="failed", failureCode="timeout")
    except asyncio.CancelledError:
        report.update(status="interrupted", failureCode="cancelled")
    except Exception as error:
        report.update(
            status="failed",
            failureCode=str(error) if isinstance(error, CollectionError) else type(error).__name__,
        )
    finally:
        # Delay further Ctrl-C/TERM until the existing bounded cleanup finishes.
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


def main(prepare=prepare_report, collector=collect, description=__doc__, *, allow_holdout=False):
    args = arguments(description, allow_holdout=allow_holdout)
    report = {
        "schema": 1,
        "status": "not-run",
        "quality": "not-run",
        "stage": "input-validation",
        "workerStarts": 0,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf8") as output:
        try:
            report.update(
                prepare(include_holdout=args.include_holdout) if allow_holdout else prepare()
            )
            if not args.validate_only:
                asyncio.run(collector(args, report))
        except (KeyboardInterrupt, asyncio.CancelledError):
            report.update(status="interrupted", failureCode="cancelled-during-cleanup")
        except Exception as error:
            report.update(
                status="failed",
                failureCode=str(error)
                if isinstance(error, CollectionError)
                else type(error).__name__,
            )
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
    raise SystemExit(main(allow_holdout=True))
