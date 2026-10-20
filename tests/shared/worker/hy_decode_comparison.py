"""Explicit fixed Hy decoding comparison; no model download or production switches."""

# The official model card's transformers path requires >=5.6.0. Match that
# version's repetition -> temperature -> top_k -> top_p order, not CLI defaults:
# https://huggingface.co/tencent/Hy-MT2-1.8B
# https://github.com/huggingface/transformers/blob/v5.6.0/src/transformers/generation/utils.py
# This is four decode values under PRODUCT limits, not official max_tokens=4096.
import asyncio
import hashlib
import importlib.util
import json
import os
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import styled_quality as styled  # noqa: E402


def check_dispatch():
    value = os.environ.get("HY_DECODE_COMPARISON", "false")
    if value not in ("", "false", "true"):
        raise ValueError("hy_decode_comparison must be an exact boolean")
    if value == "true" and (
        os.environ.get("EVENT_NAME") != "workflow_dispatch"
        or os.environ.get("COLLECT_TRANSLATION_QUALITY") != "true"
    ):
        raise ValueError("hy_decode_comparison requires manual translation quality collection")


SEED = 20261020
RECIPE = {
    "sampling": "penalties-temperature-top_k-top_p-dist",
    "temperature": 0.7,
    "top_k": 20,
    "top_p": 0.6,
    "repetitionPenalty": 1.05,
    "penaltyHistory": "full-prompt-and-generated-prefix-context1024",
    "seed": SEED,
    "seedReset": "per-request",
    "orderingReference": "transformers-v5.6.0",
}
GREEDY = '  std::unique_ptr<llama_sampler,decltype(&llama_sampler_free)> sampler(llama_sampler_init_greedy(),llama_sampler_free);require(bool(sampler),HY_RESOURCE,"sampler allocation failed");'
SAMPLED = """  std::unique_ptr<llama_sampler,decltype(&llama_sampler_free)> sampler(llama_sampler_chain_init(llama_sampler_chain_default_params()),llama_sampler_free);require(bool(sampler),HY_RESOURCE,"sampler allocation failed");
  auto add_sampler=[&](llama_sampler *part){require(part!=nullptr,HY_RESOURCE,"sampler allocation failed");llama_sampler_chain_add(sampler.get(),part);};
  add_sampler(llama_sampler_init_penalties(llama_vocab_n_tokens(h->vocab),CONTEXT,1.05f,0.0f,0.0f));
  add_sampler(llama_sampler_init_temp(0.7f));
  add_sampler(llama_sampler_init_top_k(20));
  add_sampler(llama_sampler_init_top_p(0.6f,1));
  add_sampler(llama_sampler_init_dist(20261020u));
  for(auto token:prompt)llama_sampler_accept(sampler.get(),token);"""


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_new(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")


def runtime_probe(worker):
    # A fresh interpreter loads the REAL isolated ELF closure, not a mocked ABI.
    probe = subprocess.run(
        [
            sys.executable,
            "-I",
            "-B",
            "-c",
            "import ctypes,json,sys; sys.path.insert(0,sys.argv[1]); "
            "from typelay_worker.hy_assets import verify_native,EXPORTS; "
            "from typelay_worker.hy_cpu import LinuxRuntime; "
            "loader=LinuxRuntime(verify_native()); selected=loader.select_cpu_backend(); "
            "adapter=loader.load_adapter(); assert all(hasattr(adapter,n) for n in EXPORTS); "
            "cpu=ctypes.CDLL(str(selected)); print(json.dumps(loader.verify_loaded(selected))); loader.close()",
            str(worker),
        ],
        check=True,
        capture_output=True,
        text=True,
        timeout=30,
    )
    return {"exitCode": probe.returncode, "loaded": json.loads(probe.stdout)}


def build_variant(experiment, baseline):
    """Child-only build. No model access; parent owns the entire process group."""
    from typelay_worker.hy_assets import NATIVE_ROOT, read_json, verify_file

    # Check the original fixed source before deriving a deliberately different build.
    for record in read_json(NATIVE_ROOT / "assets.lock.json")["source_files"]:
        verify_file(NATIVE_ROOT / record["file"], record)
    baseline_probe = runtime_probe(baseline.parent)
    source = experiment / "source"
    shutil.copytree(NATIVE_ROOT, source)
    original = (source / "hy_adapter.cpp").read_text()
    if original.count(GREEDY) != 1:
        raise ValueError("greedy source changed; review experiment instead of guessing")
    (source / "hy_adapter.cpp").write_text(original.replace(GREEDY, SAMPLED))
    lock = read_json(source / "assets.lock.json")
    for record in lock["source_files"]:
        path = source / record["file"]
        record.update(bytes=path.stat().st_size, sha256=digest(path))
    # Derived build input only; no tracked or shipped production lock is changed.
    (source / "assets.lock.json").write_text(json.dumps(lock, indent=2) + "\n")
    worker = experiment / "worker"
    worker.mkdir()
    for name in ("run_worker.py", "requirements.txt", "model-catalog.json"):
        shutil.copyfile(baseline.parent / name, worker / name)
    shutil.copytree(baseline.parent / "typelay_worker", worker / "typelay_worker")
    spec = importlib.util.spec_from_file_location(
        "decode_variant_builder", ROOT / "eng/linux/hy/build.py"
    )
    builder = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(builder)
    builder.NATIVE_ROOT = source
    manifest = builder.build(baseline.parent / "native/hy/cpu-runtime", worker / "native/hy")
    probe = runtime_probe(worker)
    write_new(
        experiment / "build.json",
        {
            "recipe": RECIPE,
            "baselineNativeSha256": digest(baseline.parent / "native/hy/libtypelay_hy.so"),
            "sourceSha256": digest(source / "hy_adapter.cpp"),
            "sourceLockSha256": digest(source / "assets.lock.json"),
            "nativeSha256": manifest["adapter"]["sha256"],
            "manifestSha256": digest(worker / "native/hy/production-adapter.json"),
            "runtimeAbiProbe": probe,
            "baselineAbiProbe": baseline_probe,
            "workerSourceSha256": {name: digest(worker / name) for name in styled.source_hashes()},
        },
    )


def build_limits():
    styled.worker_limits()
    os.sched_setaffinity(0, sorted(os.sched_getaffinity(0))[:2])


async def prepare_variant(experiment, baseline, deadline, report):
    """One owned build/probe group. It cannot overlap either inference worker."""
    if time.monotonic() >= deadline:
        raise TimeoutError
    experiment.mkdir()  # Exclusive: failed or prior evidence is never reused.
    process = None
    loop = asyncio.get_running_loop()
    previous = {s: signal.getsignal(s) for s in (signal.SIGINT, signal.SIGTERM)}
    build_report = {"status": "not-run", "workerExitConfirmed": None}
    report["variantBuild"] = build_report
    for signum in previous:
        loop.add_signal_handler(signum, asyncio.current_task().cancel)
    try:
        with (experiment / "build.log").open("xb") as log:
            async with asyncio.timeout_at(deadline):
                spawn = asyncio.create_task(
                    asyncio.create_subprocess_exec(
                        sys.executable,
                        "-I",
                        "-B",
                        str(Path(__file__).resolve()),
                        "--build",
                        str(experiment),
                        str(baseline),
                        stdin=asyncio.subprocess.PIPE,
                        stdout=log,
                        stderr=log,
                        start_new_session=True,
                        preexec_fn=build_limits,
                    )
                )
                try:
                    process = await asyncio.shield(spawn)
                except asyncio.CancelledError:
                    for signum in previous:
                        loop.add_signal_handler(
                            signum, lambda: build_report.update(cleanupInterrupted=True)
                        )
                    process = await spawn
                    raise
                await process.wait()
                build_report["status"] = "completed" if process.returncode == 0 else "failed"
    finally:
        for signum in previous:
            loop.add_signal_handler(signum, lambda: build_report.update(cleanupInterrupted=True))
        try:
            if process is not None:
                await styled.cleanup(process, build_report)
        finally:
            for signum, handler in previous.items():
                loop.remove_signal_handler(signum)
                signal.signal(signum, handler)
    if (
        build_report.get("workerExitConfirmed") is not True
        or build_report.get("workerExitCode") != 0
        or build_report.get("cleanupInterrupted")
    ):
        raise styled.CollectionError("variant-build-or-runtime-abi-probe-failed")
    if time.monotonic() >= deadline:
        raise TimeoutError


def verify_variant(experiment, baseline_hash):
    record = json.loads((experiment / "build.json").read_text())
    native = experiment / "worker/native/hy"
    original = (ROOT / "src/shared/TypeLay.Worker/native/hy/hy_adapter.cpp").read_text()
    expected_source = hashlib.sha256(original.replace(GREEDY, SAMPLED).encode()).hexdigest()
    if (
        original.count(GREEDY) != 1
        or record["sourceSha256"] != expected_source
        or record["recipe"] != RECIPE
        or record["baselineNativeSha256"] != baseline_hash
        or record["nativeSha256"] == baseline_hash
        or digest(native / "libtypelay_hy.so") != record["nativeSha256"]
        or digest(native / "production-adapter.json") != record["manifestSha256"]
        or digest(experiment / "source/hy_adapter.cpp") != record["sourceSha256"]
        or digest(experiment / "source/assets.lock.json") != record["sourceLockSha256"]
        or record["workerSourceSha256"] != styled.source_hashes()
        or record["baselineAbiProbe"]["exitCode"] != 0
        or len(record["baselineAbiProbe"]["loaded"]) != 5
        or record["runtimeAbiProbe"]["exitCode"] != 0
        or len(record["runtimeAbiProbe"]["loaded"]) != 5
    ):
        raise styled.CollectionError("variant-build-identity-or-abi-evidence-mismatch")
    return record


def collect_main(*, variant, work_deadline, experiment):
    def prepare(*, include_holdout=False):
        if not include_holdout:
            raise styled.CollectionError("comparison-requires-all-sixteen-requests")
        report = styled.prepare_report(include_holdout=True)
        report["decodeComparison"] = {
            "variant": variant,
            "workDeadline": work_deadline,
            "recipeVerified": False,
        }
        return report

    async def collect(args, report):
        if variant == "A":
            # Same formal package identity gate used by the normal collector.
            styled.production_worker(args, report["workerSourceSha256"])
            inventory = styled.installation(args)
            baseline_hash = digest(args.worker.parent / "native/hy/libtypelay_hy.so")
            await prepare_variant(experiment, args.worker, work_deadline, report)
        else:
            baseline = json.loads((args.output.parent / "styled-quality.json").read_text())
            if (
                baseline["status"] != "completed"
                or baseline["workerExitConfirmed"] is not True
                or baseline["workerExitCode"] != 0
                or len(baseline["requests"]) != 17
            ):
                raise styled.CollectionError("baseline-incomplete-no-retry")
            inventory = styled.installation(args)
            if inventory != baseline["installation"]:
                raise styled.CollectionError("comparison-installed-generation-changed")
            baseline_hash = baseline["productionWorker"]["manifestAdapter"]["sha256"]
        record = verify_variant(experiment, baseline_hash)
        if (
            variant == "B"
            and args.worker.resolve() != (experiment / "worker/run_worker.py").resolve()
        ):
            raise styled.CollectionError("comparison-variant-worker-mismatch")
        report["decodeComparison"].update(
            recipeVerified=True,
            recipe=RECIPE if variant == "B" else {"sampling": "greedy"},
            nativeSha256=record["nativeSha256"] if variant == "B" else baseline_hash,
            sourceSha256=record["sourceSha256"]
            if variant == "B"
            else digest(ROOT / "src/shared/TypeLay.Worker/native/hy/hy_adapter.cpp"),
            buildRecordSha256=digest(experiment / "build.json"),
        )
        if variant == "B":
            report["configuredLimits"]["sampling"] = RECIPE["sampling"]
        await styled.collect(args, report, work_deadline=work_deadline)
        if report.get("installation") != inventory:
            report.update(status="failed", failureCode="comparison-installed-generation-changed")
        actual = report.get("productionWorker", {}).get("manifestAdapter", {}).get("sha256")
        if actual != report["decodeComparison"]["nativeSha256"]:
            report.update(status="failed", failureCode="comparison-loaded-native-identity-mismatch")

    return styled.main(prepare=prepare, collector=collect, allow_holdout=True)


if __name__ == "__main__":
    if sys.argv[1:] == ["--check-dispatch"]:
        check_dispatch()
    elif len(sys.argv) == 4 and sys.argv[1] == "--build":
        build_variant(Path(sys.argv[2]), Path(sys.argv[3]))
    else:
        raise SystemExit("requires --check-dispatch or private --build")
