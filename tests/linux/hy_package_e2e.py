"""Explicit real-model preparation and GTK package lifecycle acceptance.

Preparation downloads only the fixed model and license into a dedicated test
cache. The desktop phase consumes that cache and the existing formal Argos
installation. It neither fabricates installation markers nor launches a second
inference worker. Windows availability and default installation are unchanged.
"""

import argparse
import gettext
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import uuid
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from main import control  # noqa: E402
from paths import Paths  # noqa: E402
from setup import download_resource, hy_assets, package_catalog  # noqa: E402

HY = "hy-mt2-1-8b-q4-k-m"
ARGOS = ["argos-zh-en", "argos-en-zh"]
DIRECTIONS = [
    "zh_ja",
    "ja_zh",
    "zh_ko",
    "ko_zh",
    "zh_fr",
    "fr_zh",
    "zh_es",
    "es_zh",
    "zh_de",
    "de_zh",
    "zh_pt",
    "pt_zh",
]


def model_cache(package, cache):
    assets = hy_assets(package)
    fixed = assets.read_json(package / "worker/native/hy/model.lock.json")
    assert fixed["package_id"] == HY and fixed["directions"] == DIRECTIONS
    return assets, fixed, cache / "typelay/components" / HY / fixed["version"]


def prepare_cache(package, cache):
    assets, fixed, folder = model_cache(package, cache)
    assets.verify_native()
    item = next(item for item in package_catalog(package).packages if item.id == HY)
    folder.mkdir(parents=True, exist_ok=True, mode=0o700)
    assets.plain_path(folder, directory=True)
    expected = {fixed[key]["file"] for key in ("model", "model_license")}
    if {path.name for path in folder.iterdir()} - expected:
        raise ValueError("Hy test cache contains unexpected files")
    for kind in ("model", "model_license"):
        record = fixed[kind]
        target = folder / record["file"]
        if target.exists() or target.is_symlink():
            assets.verify_file(target, record)
        else:
            url = item.url if kind == "model" else item.url.rsplit("/", 1)[0] + "/LICENSE.txt"
            with redirect_stdout(sys.stderr):
                download_resource(
                    HY,
                    url,
                    target,
                    record["bytes"] + 65536,
                    expected=record,
                    hosts=(
                        "huggingface.co",
                        "cdn-lfs.huggingface.co",
                        "cdn-lfs.hf.co",
                        "cdn-lfs-us-1.hf.co",
                        "cdn-lfs-eu-1.hf.co",
                        "cas-bridge.xethub.hf.co",
                        "us.aws.cdn.hf.co",
                        "eu.aws.cdn.hf.co",
                    ),
                )
        assets.verify_file(target, record)
    return {
        "status": "verified",
        "modelSha256": fixed["model"]["sha256"],
        "licenseSha256": fixed["model_license"]["sha256"],
        "bytes": sum(fixed[kind]["bytes"] for kind in ("model", "model_license")),
    }


def disk_budget(package, data):
    """Reserve all five transaction generations until Remove retires history."""
    assets = hy_assets(package)
    fixed = assets.read_json(package / "worker/native/hy/model.lock.json")
    generation = (data / "typelay/components/current").resolve(strict=True)
    original = sum(path.stat().st_size for path in generation.rglob("*") if path.is_file())
    model = sum(fixed[key]["bytes"] for key in ("model", "model_license"))
    # Install, return, select, return retain four GGUF copies. Remove adds a
    # fifth Argos/runtime generation before cleanup. Reserve runtime fallback
    # and package relocation separately, rather than claiming a fixed peak.
    desktop = 5 * original + 4 * model + 2 * 1024**3
    return {
        "currentGenerationBytes": original,
        "modelBytes": model,
        "requiredDesktopFreeBytes": desktop,
        "requiredCacheAndDesktopFreeBytes": desktop + model,
    }


def run(arguments):
    from support.gtk_accessibility import Atspi, named, select_settings_page

    sys.stdout = sys.stderr = (arguments.output / "inside.log").open("w", buffering=1)
    started, process = time.monotonic(), None
    deadline = started + 240
    report = {"scope": "formal-linux-hy-package-lifecycle", "status": "failed", "stages": []}
    environment = dict(os.environ)
    package = arguments.package
    assets, fixed, cache = model_cache(package, Path(environment["XDG_CACHE_HOME"]))
    for kind in ("model", "model_license"):
        assets.verify_file(cache / fixed[kind]["file"], fixed[kind])
    cache_identity = {
        path.name: (path.stat().st_size, path.stat().st_mtime_ns) for path in cache.iterdir()
    }
    budget = disk_budget(package, Path(environment["XDG_DATA_HOME"]))
    report["diskBudget"] = budget
    if (
        shutil.disk_usage(Path(environment["XDG_DATA_HOME"])).free
        < budget["requiredDesktopFreeBytes"]
    ):
        raise RuntimeError("Insufficient disk for the complete Hy lifecycle generation budget")

    def remaining():
        value = deadline - time.monotonic()
        assert value > 0, "Hy package lifecycle exhausted its 240-second budget"
        return value

    def wait(predicate, description, maximum=30):
        end = time.monotonic() + min(remaining(), maximum)
        while time.monotonic() < end:
            value = predicate()
            if value:
                return value
            time.sleep(0.05)
        raise AssertionError(description)

    # User installation and relocation are part of this acceptance. The package
    # is immutable; only this test's HOME/config and prepared model data are used.
    with tempfile.TemporaryDirectory(prefix="hy-package-home-", dir=arguments.output) as home:
        environment.update(HOME=home, XDG_CONFIG_HOME=str(Path(home) / "config"))
        subprocess.run(
            [sys.executable, str(package / "install.py"), "install"],
            env=environment,
            check=True,
            timeout=min(30, remaining()),
        )
        package = (Path(home) / ".local/lib/typelay/current").resolve(strict=True)
        with patch.dict(os.environ, environment):
            paths = Paths(package)
        initial = json.loads((paths.models / "installed.json").read_text())
        assert initial["schema"] in (1, 2, 3)
        assert (
            subprocess.check_output(
                [
                    str(paths.python),
                    "-I",
                    "-B",
                    "-c",
                    "import platform;print(platform.python_version())",
                ],
                env=environment,
                text=True,
                timeout=10,
            ).strip()
            == "3.13.16"
        )
        paths.settings.write_text(
            json.dumps(
                {
                    "Schema": 1,
                    "Enabled": True,
                    "Source": "zh",
                    "Target": "fr",
                    "Mode": 1,
                    "OnboardingComplete": True,
                }
            )
        )
        saved_settings = paths.settings.read_bytes()
        previous_worker = None
        previous_generation = None
        seen_workers = {}
        initial_free = shutil.disk_usage(paths.data).free
        report.update(minimumDataFreeBytes=initial_free, peakWorkerRssBytes=0)

        def state():
            assert process.poll() is None, "formal desktop exited during package lifecycle"
            try:
                return control(paths, "status") or {}
            except (OSError, EOFError, ValueError):
                return {}

        def row():
            return next(
                (
                    item
                    for item in state()
                    .get("setup", {})
                    .get("preparation", {})
                    .get("packageRows", [])
                    if item["id"] == HY
                ),
                None,
            )

        def workers(maximum=1):
            # Only inspect descendants of this owned synthetic desktop. Worker
            # argv holds paths, never the text carried by the private pipe.
            parents = {}
            for directory in Path("/proc").iterdir():
                if directory.name.isdigit():
                    try:
                        fields = (directory / "stat").read_text().rsplit(")", 1)[1].split()
                        parents[int(directory.name)] = int(fields[1])
                    except (OSError, ValueError):
                        pass
            owned = {process.pid}
            for _ in range(16):
                more = {pid for pid, parent in parents.items() if parent in owned}
                if more <= owned:
                    break
                owned |= more
            found = []
            for pid in owned:
                try:
                    args = (Path("/proc") / str(pid) / "cmdline").read_bytes().split(b"\0")
                    if (
                        os.fsencode(package / "worker/run_worker.py") in args
                        and Path(os.readlink(f"/proc/{pid}/exe")).name == "python3.13"
                    ):
                        found.append(pid)
                        info = (
                            (Path("/proc") / str(pid) / "stat")
                            .read_text()
                            .rsplit(")", 1)[1]
                            .split()
                        )
                        seen_workers[pid] = info[19]
                        usage = dict(
                            line.split(":", 1)
                            for line in (Path("/proc") / str(pid) / "status")
                            .read_text()
                            .splitlines()
                            if ":" in line
                        )
                        report["peakWorkerRssBytes"] = max(
                            report["peakWorkerRssBytes"],
                            int(usage.get("VmHWM", "0").split()[0]) * 1024,
                        )
                except OSError:
                    pass
            report["minimumDataFreeBytes"] = min(
                report["minimumDataFreeBytes"], shutil.disk_usage(paths.data).free
            )
            assert len(found) <= maximum, "package transition overlapped serving workers"
            return found

        def ready(active, directions, stage):
            nonlocal previous_worker, previous_generation

            def observed():
                current_workers = workers(2 if stage == "remove-inactive-hy" else 1)
                value = state()
                assert value.get("setup", {}).get("installation") != "failed", (
                    "real package transaction failed: " + stage
                )
                return (
                    value
                    if value.get("modelReady")
                    and value.get("modelDirections") == directions
                    and [item["id"] for item in value.get("modelPackages", [])] == active
                    and paths.models.parent.resolve(strict=True).name != previous_generation
                    and len(current_workers) == 1
                    and (
                        previous_worker is None
                        or not (Path("/proc") / str(previous_worker)).exists()
                    )
                    and (
                        previous_generation is None
                        or value.get("setup", {}).get("installation") == "ready"
                    )
                    else None
                )

            value = wait(
                observed, "expected package hello did not become ready: " + stage, remaining()
            )
            current_workers = workers()
            previous_generation = paths.models.parent.resolve(strict=True).name
            if stage != "remove-inactive-hy":
                assert {
                    path.name: (path.stat().st_size, path.stat().st_mtime_ns)
                    for path in cache.iterdir()
                } == cache_identity, "desktop phase changed prepared download assets"
            assert len(current_workers) == 1
            if previous_worker is not None:
                assert previous_worker not in current_workers
                assert not (Path("/proc") / str(previous_worker)).exists(), "old worker still alive"
            previous_worker = current_workers[0]
            assert paths.settings.read_bytes() == saved_settings, "package action changed languages"
            if active == [HY]:
                assert value["modelPackages"] == [
                    {"id": HY, "version": fixed["version"], "directions": DIRECTIONS}
                ]
            report["stages"].append(
                {
                    "stage": stage,
                    "activePackages": active,
                    "directions": directions,
                    "workerCount": 1,
                }
            )

        def activate(name):
            node = wait(lambda: named(process.pid, name), "GTK package control missing: " + name)

            def enabled():
                node.clear_cache()
                states = node.get_state_set()
                return all(
                    states.contains(value)
                    for value in (
                        Atspi.StateType.SENSITIVE,
                        Atspi.StateType.ENABLED,
                    )
                )

            wait(enabled, "GTK package control not enabled: " + name)
            assert Atspi.Component.grab_focus(node.get_component_iface()), name

            def showing():
                node.clear_cache()
                return node.get_state_set().contains(Atspi.StateType.SHOWING)

            wait(showing, "GTK package control not visible after focus: " + name)
            assert Atspi.Action.do_action(node.get_action_iface(), 0), name

        def action(title, label):
            select_settings_page(process.pid, "应用支持", timeout=min(15, remaining()))
            manager = wait(
                lambda: named(process.pid, "本地翻译组件管理"), "package management missing"
            )
            if not manager.get_state_set().contains(Atspi.StateType.EXPANDED):
                activate("本地翻译组件管理")
            activate(title + "：" + label)
            wait(
                lambda: named(process.pid, label.rstrip("…") + "？"), "confirmation dialog missing"
            )
            activate(gettext.dgettext("gtk30", "_OK").replace("_", ""))

        try:
            with (arguments.output / "desktop.log").open("w") as log:
                process = subprocess.Popen(
                    [str(paths.launcher), "--background"], env=environment, stdout=log, stderr=log
                )
                ready(ARGOS, ["zh_en", "en_zh"], "original-argos")
                assert control(paths, "settings") is True
                wait(
                    lambda: row()
                    and any(
                        a["request"]["operation"] == "Install" and a["enabled"]
                        for a in row()["actions"]
                    ),
                    "shipped Hy runtime unavailable",
                )
                action("Hy-MT2 1.8B · 中文多语", "下载并使用…")
                ready([HY], DIRECTIONS, "install-and-use-hy")
                request = {
                    "id": 1,
                    "operation": "translateEditorSnapshot",
                    "data": {
                        "documentId": str(uuid.uuid4()),
                        "version": 1,
                        "selection": {"start": 0, "length": 11},
                        "text": "我没有同意删除备份。",
                        "languageId": "plaintext",
                    },
                }
                request["data"]["selection"]["length"] = (
                    len(request["data"]["text"].encode("utf-16-le")) // 2
                )
                response = control(
                    paths, "translateEditorSnapshot", request, timeout=min(70, remaining())
                )
                assert (
                    response["id"] == 1
                    and response["data"]["documentId"] == request["data"]["documentId"]
                )
                translated = response["data"]["translation"]
                assert translated.strip() and translated != request["data"]["text"]
                assert workers() == [previous_worker], "explicit preview spawned another worker"
                report["singleZhFrInference"] = "translated-nonempty-different"
                subprocess.run(
                    ["grim", str(arguments.output / "hy-installed.png")],
                    check=True,
                    timeout=min(3, remaining()),
                )
                action("Argos · 中文 → English", "使用此模型")
                ready(ARGOS, ["zh_en", "en_zh"], "switch-back-to-argos")
                action("Hy-MT2 1.8B · 中文多语", "使用此模型")
                ready([HY], DIRECTIONS, "select-installed-hy")
                action("Argos · 中文 → English", "使用此模型")
                ready(ARGOS, ["zh_en", "en_zh"], "return-to-argos-before-remove")
                action("Hy-MT2 1.8B · 中文多语", "移除…")
                ready(ARGOS, ["zh_en", "en_zh"], "remove-inactive-hy")
                marker = json.loads((paths.models / "installed.json").read_text())
                assert marker["schema"] == 3 and marker["activePackages"] == ARGOS
                assert [item["id"] for item in marker["packages"]] == ARGOS
                # Real cleanup must release model copies, including recovery generations.
                wait(
                    lambda: not list((paths.data / "components").rglob("*.gguf")),
                    "removed Hy model copies were retained without release",
                    remaining(),
                )
                wait(lambda: not cache.exists(), "removed Hy download cache was not cleaned")
                assert control(paths, "quit") is True
                assert process.wait(timeout=7) == 0
                assert not (Path("/proc") / str(previous_worker)).exists()
                report.update(
                    status="passed",
                    python="3.13.16",
                    verifiedModelCacheUnchangedUntilRemove=True,
                    defaultArgosPreserved=True,
                    languageSettingsPreserved=True,
                    physicalDesktopAcceptance=False,
                    twelveDirectionQualityAcceptance=False,
                )
        finally:
            cleanup_deadline = time.monotonic() + 18
            if process is not None and process.poll() is None:
                try:
                    control(paths, "quit", timeout=3)
                    process.wait(timeout=5)
                except (OSError, EOFError, ValueError, subprocess.TimeoutExpired):
                    process.terminate()
                    try:
                        process.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=2)

            # Only previously observed owned workers may be cleaned on failure.
            # A successful product path must already have released every one.
            def surviving_workers():
                alive = []
                for pid, identity in seen_workers.items():
                    try:
                        info = (
                            (Path("/proc") / str(pid) / "stat")
                            .read_text()
                            .rsplit(")", 1)[1]
                            .split()
                        )
                        if info[19] == identity:
                            alive.append(pid)
                    except (OSError, ValueError):
                        pass
                return alive

            survivors = surviving_workers()
            report["forcedWorkerCleanup"] = bool(survivors)
            if survivors:
                report["status"] = "failed"
                for pid in survivors:
                    try:
                        os.kill(pid, signal.SIGTERM)
                    except ProcessLookupError:
                        pass
                grace = min(cleanup_deadline, time.monotonic() + 2)
                while surviving_workers() and time.monotonic() < grace:
                    time.sleep(0.05)
                for pid in surviving_workers():
                    try:
                        os.kill(pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                while surviving_workers() and time.monotonic() < cleanup_deadline:
                    time.sleep(0.05)
            report["workerExitConfirmed"] = not surviving_workers()
            report["peakAdditionalDiskBytes"] = initial_free - report["minimumDataFreeBytes"]
            report["elapsedSeconds"] = round(time.monotonic() - started, 3)
            (arguments.output / "hy-package-result.json").write_text(json.dumps(report, indent=2))
        assert report["status"] == "passed", "owned model process cleanup was not confirmed"
        (arguments.output / "desktop-result.json").write_text(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--prepare-cache", action="store_true")
    action.add_argument("--check-budget", action="store_true")
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--cache", type=Path)
    parser.add_argument("--data", type=Path)
    parser.add_argument("--remaining-seconds", type=int)
    args = parser.parse_args()
    if args.prepare_cache:
        if args.cache is None:
            parser.error("--prepare-cache requires --cache")
        print(json.dumps(prepare_cache(args.package.resolve(), args.cache.resolve())))
    else:
        if args.data is None or args.remaining_seconds is None:
            parser.error("--check-budget requires --data and --remaining-seconds")
        report = {
            **disk_budget(args.package.resolve(), args.data.resolve()),
            "remainingSeconds": args.remaining_seconds,
            "requiredSeconds": 12 * 60,
            "freeBytes": shutil.disk_usage(args.data).free,
        }
        report["admitted"] = (
            report["remainingSeconds"] >= report["requiredSeconds"]
            and report["freeBytes"] >= report["requiredCacheAndDesktopFreeBytes"]
        )
        print(json.dumps(report, indent=2))
        if not report["admitted"]:
            raise SystemExit(
                "Insufficient bounded Hy lifecycle time or disk; no model download started."
            )
