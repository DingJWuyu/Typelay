"""Bounded handoff to the existing quality runners, never another model owner."""

import fcntl
import hashlib
import json
import os
import runpy
import shutil
import stat
import sys
import tempfile
import time
from contextlib import ExitStack, contextmanager
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
RUNNERS = ROOT / "tests/shared/worker"
RUNNER_SECONDS = 600 + 7
WORK_SECONDS = 240 + 3 * RUNNER_SECONDS
CLEANUP_SECONDS = 30
COLLECTIONS = {"alternative": 32, "styled": 16, "glossary": 5}


def collections(comparison=False):
    return (
        {"alternative": 32, "styled": 16, "styled-b": 16, "glossary": 5}
        if comparison
        else COLLECTIONS
    )


def process_status(pid):
    try:
        process = Path("/proc") / str(pid)
        if process.stat().st_uid != os.getuid():
            raise PermissionError("process owner changed")
        fields = (process / "stat").read_text().rsplit(")", 1)[1].split()
        value = int(fields[19])
        if value <= 0:
            raise ValueError("invalid process start time")
        return str(value), fields[0]
    except FileNotFoundError:
        return None


def process_identity(pid):
    status = process_status(pid)
    return status[0] if status is not None else None


@contextmanager
def exclusive_inventory(paths, generation):
    """Hold the existing production locks for this exact, already-served generation."""
    with ExitStack() as stack:
        locks = [
            paths.session / "desktop.lock",
            paths.data / "components/install.lock",
            generation / "use.lock",
        ]
        for path in locks:
            fd = os.open(path, os.O_RDWR | os.O_NOFOLLOW)
            stack.callback(os.close, fd)
            info = os.fstat(fd)
            if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or info.st_nlink != 1:
                raise RuntimeError("unsafe capture lock")
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield


def invoke_runner(name, arguments, *, comparison=None):
    """The runner keeps its own SIG/asyncio cleanup and sole worker process group."""

    def related(key):
        return (
            key == "typelay_worker"
            or key.startswith("typelay_worker.")
            or key
            in {"alternative_quality", "styled_quality", "glossary_quality", "hy_decode_comparison"}
        )

    saved_modules = {key: value for key, value in sys.modules.items() if related(key)}
    saved_path, saved_argv = sys.path[:], sys.argv
    try:
        for key in saved_modules:
            del sys.modules[key]
        sys.path.insert(0, str(RUNNERS))
        source_name = "styled" if name == "styled-b" else name
        sys.argv = [str(RUNNERS / f"{source_name}_quality.py"), *arguments]
        try:
            if comparison is not None:
                import hy_decode_comparison

                return hy_decode_comparison.collect_main(**comparison)
            runpy.run_path(sys.argv[0], run_name="__main__")
        except SystemExit as error:
            return error.code
        raise RuntimeError("quality runner did not return an exit status")
    finally:
        for key in list(sys.modules):
            if related(key):
                del sys.modules[key]
        sys.modules.update(saved_modules)
        sys.path[:] = saved_path
        sys.argv = saved_argv


def inspect_report(name, output):
    """Read without rewriting the original full-response quality artifact."""
    report = json.loads(output.read_text())
    requests = report["requests"]
    if not isinstance(requests, list):
        raise ValueError("quality request records missing")
    operations = []
    for item in requests:
        if not isinstance(item, dict):
            raise ValueError("quality request record invalid")
        payload = item.get("request", {})
        if not isinstance(payload, dict):
            raise ValueError("quality request payload invalid")
        operation = item.get("op", payload.get("op"))
        if operation not in {
            "hello",
            "translate",
            "translate-alternative",
            "translate-styled-v1",
            "translate-glossary-v1",
        }:
            raise ValueError("quality request operation invalid")
        operations.append(operation)
    count = sum(operation != "hello" for operation in operations)
    return report, {
        "runner": name,
        "output": str(output),
        "status": report.get("status"),
        "quality": report.get("quality", "not-run"),
        "maximumTranslationRequests": collections(name == "styled-b")[name],
        "actualTranslationRequests": count,
        "workerStarts": report.get("workerStarts"),
        "workerExitConfirmed": report.get("workerExitConfirmed"),
        "workerExitCode": report.get("workerExitCode"),
        "inputSha256": report.get("inputSha256"),
        "holdoutInputSha256": report.get("holdoutInputSha256"),
    }


class Capture:
    def __init__(self, output, work_deadline, *, comparison=False):
        self.comparison = comparison
        self.collections = collections(comparison)
        self.output = output
        self.work_deadline = work_deadline
        self.elapsed = 0.0
        self.blocked = False
        self.report = {
            "quality": "not-run",
            "maximumTranslationRequests": sum(self.collections.values()),
            "actualTranslationRequests": 0,
            "requestCountComplete": True,
            "workerExitConfirmed": True,
            "collections": [],
        }

    def run_comparison(self, generation, worker):
        if not self.comparison:
            raise RuntimeError("comparison was not explicitly selected")
        if self.blocked or any(
            item["runner"] in ("styled", "styled-b") for item in self.report["collections"]
        ):
            raise RuntimeError("failed quality collection cannot be retried")
        if self.work_deadline - time.monotonic() < RUNNER_SECONDS:
            raise TimeoutError("insufficient remaining quality work budget")
        started = time.monotonic()
        deadline = started + 600
        # Build and runtime-only ABI probe finish before A loads a model.
        # Helpers are isolated from any previously imported installed modules.
        options = {
            "variant": "A",
            "work_deadline": deadline,
            "experiment": self.output / "hy-decode",
        }
        self.run("styled", generation, worker, comparison=options)
        if time.monotonic() >= deadline:
            self.blocked = True
            raise TimeoutError("shared styled deadline exhausted before B")
        options["variant"] = "B"
        self.run(
            "styled-b",
            generation,
            self.output / "hy-decode/worker/run_worker.py",
            comparison=options,
        )
        if time.monotonic() - started > RUNNER_SECONDS:
            self.blocked = True
            raise TimeoutError("shared styled comparison budget exceeded")

    def run(self, name, generation, worker, *, comparison=None):
        if name not in self.collections or (name == "styled-b" and comparison is None):
            raise ValueError("collection not selected for this capture")
        if self.blocked:
            raise RuntimeError("failed quality collection cannot be retried or continued")
        if name in [item["runner"] for item in self.report["collections"]]:
            raise RuntimeError("quality collection cannot be retried")
        if comparison is None and self.work_deadline - time.monotonic() < RUNNER_SECONDS:
            raise TimeoutError("insufficient remaining quality work budget")
        output = self.output / f"{name}-quality.json"
        if output.exists():
            raise FileExistsError("preserve the existing quality report")
        models = generation / "models"
        python = generation / "runtime/venv/bin/python"
        inventory = (models / "installed.json").read_bytes()
        arguments = ["--python", str(python), "--models", str(models), "--output", str(output)]
        if name != "alternative":
            arguments.extend(["--worker", str(worker)])
        if name in ("styled", "styled-b"):
            arguments.append("--include-holdout")
        entry = {
            "runner": name,
            "output": str(output),
            "status": "unknown",
            "quality": "not-run",
            "workerExitConfirmed": None,
            "actualTranslationRequests": None,
        }
        self.report["collections"].append(entry)
        self.blocked = True
        started = time.monotonic()
        code = None
        try:
            code = (
                invoke_runner(name, arguments, comparison=comparison)
                if comparison is not None
                else invoke_runner(name, arguments)
            )
        finally:
            elapsed = time.monotonic() - started
            self.elapsed += elapsed
            try:
                report, summary = inspect_report(name, output)
                entry.update(summary)
            except (OSError, ValueError, KeyError, TypeError):
                report = None
            entry.update(runnerExitCode=code, wallSeconds=round(elapsed, 3))
            self.report["actualTranslationRequests"] = sum(
                item["actualTranslationRequests"] or 0 for item in self.report["collections"]
            )
            self.report["requestCountComplete"] = all(
                item["actualTranslationRequests"] is not None for item in self.report["collections"]
            )
            self.report["workerExitConfirmed"] = all(
                item["workerExitConfirmed"] is True for item in self.report["collections"]
            )
            self.report["quality"] = (
                "not-reviewed"
                if any(item["quality"] == "not-reviewed" for item in self.report["collections"])
                else "not-run"
            )
        if report is None:
            raise RuntimeError("quality report missing or unreadable; preserve all data")
        source_name = "styled" if name == "styled-b" else name
        inputs = (RUNNERS / f"{source_name}_quality_inputs.json").read_bytes()
        expected = json.loads(inputs)["samples"]
        holdout_matches = True
        if name in ("styled", "styled-b"):
            holdout = (RUNNERS / "styled_quality_holdout_inputs.json").read_bytes()
            expected.extend(json.loads(holdout)["samples"])
            holdout_matches = (
                report.get("holdoutInputSha256") == hashlib.sha256(holdout).hexdigest()
            )
        if not (
            code == 0
            and report.get("status") == "completed"
            and report.get("quality") == "not-reviewed"
            and type(report.get("workerStarts")) is int
            and report["workerStarts"] == 1
            and report.get("workerExitConfirmed") is True
            and type(report.get("workerExitCode")) is int
            and report["workerExitCode"] == 0
            and report.get("maximumTranslationRequests") == self.collections[name]
            and entry["actualTranslationRequests"] == self.collections[name]
            and len(report["requests"]) == self.collections[name] + 1
            and report["requests"][0].get("op") == "hello"
            and report.get("inputSha256") == hashlib.sha256(inputs).hexdigest()
            and holdout_matches
            and len(report.get("cases", [])) == len(expected)
            and all(
                {key: case.get(key) for key in sample} == sample
                for case, sample in zip(report["cases"], expected, strict=True)
            )
            and report.get("installation", {}).get("models") == str(models)
            and report.get("installation", {}).get("python") == str(python)
            and report.get("installation", {}).get("inventorySha256")
            == hashlib.sha256(inventory).hexdigest()
            and (models / "installed.json").read_bytes() == inventory
            and elapsed <= RUNNER_SECONDS
            and time.monotonic() < self.work_deadline
        ):
            raise RuntimeError("quality collection incomplete or identity/exit evidence invalid")
        self.blocked = False


@contextmanager
def fixture_home(output, capture, report):
    if capture is None:
        with tempfile.TemporaryDirectory(prefix="hy-package-home-", dir=output) as home:
            yield home
        return
    home = tempfile.mkdtemp(prefix="hy-package-home-", dir=output)
    report["retainedHome"] = home
    try:
        yield home
    finally:
        if (
            report["status"] == "passed"
            and report.get("hostExitConfirmed") is True
            and report.get("workerExitConfirmed") is True
            and capture.report["workerExitConfirmed"] is True
        ):
            shutil.rmtree(home)
            report["retainedHome"] = None
