"""Explicit real-model preparation and GTK package lifecycle acceptance.

Preparation downloads only the fixed model and license into a dedicated test
cache. The desktop phase consumes that cache and the existing formal Argos
installation. It neither fabricates installation markers nor launches a second
inference worker. Windows availability and default installation are unchanged.
"""

import argparse
import gettext
import json
import math
import os
import shutil
import signal
import subprocess
import sys
import time
import uuid
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from main import control  # noqa: E402
from paths import Paths  # noqa: E402
from setup import (  # noqa: E402
    download_resource,
    hy_assets,
    package_catalog,
)
from support.translation_quality_capture import (  # noqa: E402
    CLEANUP_SECONDS,
    Capture,
    exclusive_inventory,
    fixture_home,
    process_identity,
    work_seconds,
)

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


def valid_rectangle(area):
    return (
        area is not None
        and len(area) == 4
        and all(type(value) is int and -(2**31) < value < 2**31 for value in area)
        and area[2] > 0
        and area[3] > 0
    )


def rectangle_contains(outer, inner):
    return (
        valid_rectangle(outer)
        and valid_rectangle(inner)
        and outer[0] <= inner[0] <= outer[0] + outer[2] - inner[2]
        and outer[1] <= inner[1] <= outer[1] + outer[3] - inner[3]
    )


def package_scroll_value(current, lower, upper, card, viewport):
    """Compute one public scrollbar move, never an actionable button geometry."""
    assert all(
        type(value) in (int, float) and math.isfinite(value) for value in (current, lower, upper)
    ), "package scrollbar has unknown values"
    assert lower <= current <= upper, "package scrollbar has an invalid range"
    assert valid_rectangle(card) and valid_rectangle(viewport), "unknown package scroll geometry"
    x, y, width, height = card
    left, top, viewport_width, viewport_height = viewport
    assert left <= x <= left + viewport_width - width and height <= viewport_height, (
        "package card cannot fit the observed viewport"
    )
    delta = y - top if y < top else max(0, y + height - top - viewport_height)
    return max(lower, min(upper, current + delta))


def model_cache(package, cache):
    assets = hy_assets(package)
    fixed = assets.read_json(package / "worker/native/hy/model.lock.json")
    assert fixed["package_id"] == HY and fixed["directions"] == DIRECTIONS
    return assets, fixed, cache / "typelay/components" / HY / fixed["version"]


def prepare_cache(package, cache, *, cache_only=False):
    assets, fixed, folder = model_cache(package, cache)
    assets.verify_native()
    item = next(item for item in package_catalog(package).packages if item.id == HY)
    if not cache_only:
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
        elif cache_only:
            raise FileNotFoundError("fixed diagnostics requires the verified existing Hy cache")
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
    """Reserve the complete five-transaction lifecycle, including combination capture."""
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


def worker_generation(pid, package, components):
    process = Path("/proc") / str(pid)
    args = (process / "cmdline").read_bytes().split(b"\0")
    if (
        len(args) < 4
        or args[-4] != os.fsencode(package / "worker/run_worker.py")
        or args[-3] != b"--models"
        or args[-1] != b""
    ):
        return None
    models = Path(os.fsdecode(args[-2]))
    generation = models.parent
    if (
        models.name != "models"
        or generation.parent != components
        or len(generation.name) != 32
        or any(character not in "0123456789abcdef" for character in generation.name)
    ):
        return None
    # Bind to the immutable --models generation, including an unpublished
    # candidate. Runtime copies materialize venv/bin/python as a regular file.
    if (process / "exe").samefile(generation / "runtime/venv/bin/python"):
        return generation.name
    return None


def require_combination_inventory(inventory, version, *, original=None):
    """Bind the installed mid-lifecycle Argos selection before and after capture."""
    if (
        type(inventory.get("schema")) is not int
        or inventory["schema"] != 3
        or inventory.get("activePackages") != ARGOS
        or len(inventory.get("packages", [])) != len(ARGOS) + 1
        or {item.get("id") for item in inventory.get("packages", [])} != {*ARGOS, HY}
    ):
        raise ValueError("combination-only-requires-installed-hy-and-active-argos")
    hy = next(item for item in inventory["packages"] if item["id"] == HY)
    if hy.get("version") != version or hy.get("directions") != DIRECTIONS:
        raise ValueError("combination-only-installed-hy-identity")
    if original is not None and inventory != original:
        raise ValueError("combination-only-restored-inventory-changed")


def exercise_model_lifecycle(
    action,
    ready,
    prepare_install,
    collect,
    preview,
    screenshot,
    inventory,
    version,
    *,
    capture=None,
    combination=False,
):
    """One existing five-transaction lifecycle; combination replaces all other inference."""
    if combination and capture is None:
        raise ValueError("combination-only-requires-capture")
    argos = ready(ARGOS, ["zh_en", "en_zh"], "original-argos")
    if capture is not None and not combination and not capture.comparison:
        collect(("alternative",), argos)
    prepare_install()
    action("Hy-MT2 1.8B · 中文多语", "下载并使用…")
    hy = ready([HY], DIRECTIONS, "install-and-use-hy")
    if capture is not None and not combination:
        collect(
            ("ordinary",) if capture.diagnostics or capture.comparison else ("styled", "glossary"),
            hy,
        )
    if not combination and not (capture is not None and capture.comparison):
        preview()
    screenshot()
    action("Argos · 中文 → English", "使用此模型")
    ready(ARGOS, ["zh_en", "en_zh"], "switch-back-to-argos")
    if combination:
        original = inventory()
        require_combination_inventory(original, version)
    action("Hy-MT2 1.8B · 中文多语", "使用此模型")
    hy = ready([HY], DIRECTIONS, "select-installed-hy")
    if combination:
        collect(("styled_glossary",), hy)
    action("Argos · 中文 → English", "使用此模型")
    ready(ARGOS, ["zh_en", "en_zh"], "return-to-argos-before-remove")
    if combination:
        require_combination_inventory(inventory(), version, original=original)
    action("Hy-MT2 1.8B · 中文多语", "移除…")
    ready(ARGOS, ["zh_en", "en_zh"], "remove-inactive-hy")


def run(arguments):
    from support.gtk_accessibility import Atspi, named, select_settings_page

    sys.stdout = sys.stderr = (arguments.output / "inside.log").open("w", buffering=1)
    started, process = time.monotonic(), None
    deadline = started + 240
    quality = getattr(arguments, "collect_translation_quality", False)
    combination = getattr(arguments, "combination_only", False)
    capture = (
        Capture(
            arguments.output,
            arguments.quality_work_deadline,
            comparison=getattr(arguments, "hy_decode_comparison", False),
            diagnostics=getattr(arguments, "fixed_model_diagnostics", False),
            combination=combination,
        )
        if quality
        else None
    )
    if quality:
        assert arguments.quality_work_deadline is not None
        assert 0 < arguments.quality_work_deadline - started <= work_seconds(combination)
        (arguments.output / "quality-inside-owner.json").write_text(
            json.dumps(
                {
                    "pid": os.getpid(),
                    "startTime": process_identity(os.getpid()),
                }
            )
        )

        def interrupted(signum, frame):
            raise KeyboardInterrupt("quality lifecycle interrupted")

        signal.signal(signal.SIGTERM, interrupted)
    report = {"scope": "formal-linux-hy-package-lifecycle", "status": "failed", "stages": []}
    if capture is not None:
        report["translationQuality"] = capture.report
    environment = dict(os.environ)
    package = arguments.package
    if capture is not None:
        for name in ("XDG_DATA_HOME", "XDG_CACHE_HOME"):
            folder = Path(environment[name]).resolve(strict=True)
            assert (
                folder.is_dir()
                and folder.is_relative_to(ROOT / "artifacts")
                and folder != ROOT / "artifacts"
            ), "quality capture requires existing isolated artifacts data and cache"
            assert folder.stat().st_uid == os.getuid()
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
        if capture is not None:
            value = min(value + capture.elapsed, capture.work_deadline - time.monotonic())
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
    with fixture_home(arguments.output, capture, report) as home:
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
        seen_hosts = {}
        initial_free = shutil.disk_usage(paths.data).free
        report.update(minimumDataFreeBytes=initial_free, peakWorkerRssBytes=0)

        def state():
            assert process.poll() is None, "formal desktop exited during package lifecycle"
            try:
                value = control(paths, "status") or {}
                if value:
                    # Keep only the last already-read model facts. Full status
                    # can include editor state and must not enter the artifact.
                    report["lastObservedStatus"] = {
                        "modelReady": value.get("modelReady"),
                        "modelDirections": value.get("modelDirections"),
                        "modelPackages": [
                            {key: item.get(key) for key in ("id", "version", "directions")}
                            for item in value.get("modelPackages") or []
                        ],
                        "modelFailureCode": value.get("modelFailureCode"),
                        "modelCleanupPending": value.get("modelCleanupPending"),
                        "installation": value.get("setup", {}).get("installation"),
                    }
                return value
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
                    if capture is not None and (Path("/proc") / str(pid) / "exe").samefile(
                        package / "app/TypeLay.Linux"
                    ):
                        seen_hosts[pid] = process_identity(pid)
                    if worker_generation(pid, package, paths.data / "components") is not None:
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
            return value

        def collect_and_restart(names, expected, log):
            nonlocal process, previous_worker
            remaining()
            assert workers() == [previous_worker]
            generation = paths.models.parent.resolve(strict=True)
            assert generation.name == previous_generation
            inventory = (generation / "models/installed.json").read_bytes()
            settings = paths.settings.read_bytes()
            original = {
                pid: identity
                for pid, identity in seen_hosts.items()
                if process_identity(pid) == identity
            }
            assert len(original) == 1, "expected one precisely owned host before collection"
            original[previous_worker] = seen_workers[previous_worker]
            original[process.pid] = process_identity(process.pid)
            assert all(identity is not None for identity in original.values())
            assert control(paths, "quit", timeout=min(3, remaining())) is True
            assert process.wait(timeout=min(7, remaining())) == 0
            wait(
                lambda: all(
                    process_identity(pid) != identity for pid, identity in original.items()
                ),
                "original host or worker exit not confirmed",
                maximum=7,
            )
            assert not any(os.path.lexists(path) for path in (paths.control, paths.native)), (
                "original desktop sockets were not retired"
            )

            def unchanged():
                assert paths.models.parent.resolve(strict=True) == generation
                assert (generation / "models/installed.json").read_bytes() == inventory
                assert paths.settings.read_bytes() == settings == saved_settings

            with exclusive_inventory(paths, generation), patch.dict(os.environ, environment):
                unchanged()
                for name in names:
                    remaining()
                    if name == "ordinary" and capture.comparison:
                        capture.run_comparison(generation, package / "worker/run_worker.py")
                    else:
                        capture.run(name, generation, package / "worker/run_worker.py")
                    unchanged()
            remaining()
            process = subprocess.Popen(
                [str(paths.launcher), "--background"], env=environment, stdout=log, stderr=log
            )

            def restarted():
                unchanged()
                current_workers = workers()
                value = state()
                assert value.get("setup", {}).get("installation") != "failed"
                return (
                    value
                    if (
                        value.get("modelReady") is True
                        and value.get("modelDirections") == expected["modelDirections"]
                        and value.get("modelPackages") == expected["modelPackages"]
                        and not value.get("modelCleanupPending")
                        and value.get("setup", {}).get("installation") == "idle"
                        and len(current_workers) == 1
                        and worker_generation(
                            current_workers[0], package, paths.data / "components"
                        )
                        == generation.name
                        and all(
                            process_identity(pid) != identity for pid, identity in original.items()
                        )
                    )
                    else None
                )

            wait(restarted, "same-generation real hello did not become ready", remaining())
            previous_worker = workers()[0]
            unchanged()
            report["stages"].append(
                {
                    "stage": "restart-after-" + "-and-".join(names),
                    "generation": generation.name,
                    "inventoryUnchanged": True,
                    "settingsUnchanged": True,
                    "originalProcessesExited": True,
                    "socketsRetired": True,
                    "workerCount": 1,
                    "activePackages": expected["modelPackages"],
                }
            )
            assert control(paths, "settings") is True

        def activate(name, *, package_control=False):
            node = wait(lambda: named(process.pid, name), "GTK package control missing: " + name)
            scroll_observation = None

            def observe():
                observed = []
                ancestor = node
                for _ in range(16):
                    if ancestor is None:
                        break
                    ancestor.clear_cache()
                    states = ancestor.get_state_set()
                    value = {
                        "role": ancestor.get_role_name(),
                        "owned": ancestor.get_process_id() == process.pid,
                        "states": {
                            state: states.contains(getattr(Atspi.StateType, state))
                            for state in (
                                "VISIBLE",
                                "SHOWING",
                                "FOCUSED",
                                "EXPANDED",
                                "SENSITIVE",
                                "ENABLED",
                                "DEFUNCT",
                            )
                        },
                    }
                    component = ancestor.get_component_iface()
                    if component is not None:
                        area = Atspi.Component.get_extents(component, Atspi.CoordType.SCREEN)
                        value["bounds"] = [area.x, area.y, area.width, area.height]
                    observed.append((ancestor, value))
                    if ancestor.get_role() == Atspi.Role.FRAME:
                        break
                    ancestor = ancestor.get_parent()
                return observed

            def package_path():
                assert named(process.pid, name) == node, "GTK package control was replaced: " + name
                assert node.get_role() == Atspi.Role.PUSH_BUTTON, (
                    "current GTK package control is not a button: " + name
                )
                observed = observe()
                assert all(
                    value["owned"] and not value["states"]["DEFUNCT"] for _, value in observed
                ), "GTK package control has a retired or foreign ancestor"
                viewports = [
                    index
                    for index, (ancestor, _) in enumerate(observed)
                    if ancestor.get_role() == Atspi.Role.VIEWPORT
                ]
                assert len(viewports) == 1, "GTK package control has no unique viewport"
                index = viewports[0]
                assert index + 1 < len(observed)
                pane, pane_value = observed[index + 1]
                frame, frame_value = observed[-1]
                assert (
                    pane.get_role() == Atspi.Role.SCROLL_PANE
                    and pane.get_name() == "设置页：应用支持"
                    and frame.get_role() == Atspi.Role.FRAME
                    and frame.get_name() == "TypeLay 设置"
                    and not any(
                        ancestor.get_role() in (Atspi.Role.SCROLL_PANE, Atspi.Role.FRAME)
                        for ancestor, _ in observed[:index] + observed[index + 2 : -1]
                    )
                ), "GTK package control is outside its exact settings scroll pane"
                clips = (observed[index][1], pane_value, frame_value)
                assert all(
                    value["states"]["SHOWING"] and valid_rectangle(value.get("bounds"))
                    for value in clips
                ), "GTK package control has unknown clipping geometry"
                assert rectangle_contains(pane_value["bounds"], clips[0]["bounds"])
                assert rectangle_contains(frame_value["bounds"], pane_value["bounds"])
                return observed, index, clips

            def scroll_package():
                nonlocal scroll_observation
                observed, index, clips = package_path()
                target = observed[0][1]
                if target["states"]["SHOWING"] and all(
                    rectangle_contains(value["bounds"], target.get("bounds")) for value in clips
                ):
                    return
                card = next(
                    (
                        value["bounds"]
                        for _, value in observed[1:index]
                        if value["states"]["SHOWING"] and valid_rectangle(value.get("bounds"))
                    ),
                    None,
                )
                assert card is not None, "GTK package control has no observed scroll anchor"
                pane = observed[index + 1][0]
                count = pane.get_child_count()
                assert 0 < count <= 8, "GTK settings scroll pane has unexpected direct children"
                scrollbars = []
                for child_index in range(count):
                    child = pane.get_child_at_index(child_index)
                    assert child is not None, "GTK settings scroll pane child disappeared"
                    child.clear_cache()
                    assert child.get_process_id() == process.pid and child.get_parent() == pane
                    if child.get_role() == Atspi.Role.SCROLL_BAR and child.get_state_set().contains(
                        Atspi.StateType.VERTICAL
                    ):
                        scrollbars.append(child)
                assert len(scrollbars) == 1, "GTK settings has no unique vertical scrollbar"
                scrollbar = scrollbars[0]
                states = scrollbar.get_state_set()
                assert not states.contains(Atspi.StateType.DEFUNCT) and all(
                    states.contains(value)
                    for value in (
                        Atspi.StateType.SHOWING,
                        Atspi.StateType.SENSITIVE,
                        Atspi.StateType.ENABLED,
                    )
                ), "GTK settings vertical scrollbar is not interactive"
                value = scrollbar.get_value_iface()
                assert value is not None, "GTK settings vertical scrollbar has no public Value"
                current = Atspi.Value.get_current_value(value)
                lower = Atspi.Value.get_minimum_value(value)
                upper = Atspi.Value.get_maximum_value(value)
                requested = package_scroll_value(current, lower, upper, card, clips[0]["bounds"])
                assert requested != current, "GTK package control cannot be revealed by scrolling"
                scroll_observation = {
                    "before": current,
                    "requested": requested,
                    "minimum": lower,
                    "maximum": upper,
                }
                # GTK3 buttons do not implement Component.scroll_to. Drive only
                # their actual parent pane's public scrollbar, once.
                assert time.monotonic() < visibility_deadline, (
                    "GTK package control exhausted its visibility budget before scrolling"
                )
                assert Atspi.Value.set_current_value(value, requested), (
                    "GTK settings public scrollbar rejected its position"
                )
                scroll_observation["actual"] = Atspi.Value.get_current_value(value)
                assert scroll_observation["actual"] == requested, (
                    "GTK settings public scrollbar did not retain its requested position"
                )

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
            visibility_deadline = time.monotonic() + min(30, remaining())
            if package_control:
                # The ready view can collapse this section while publishing
                # the new action. Observe that action before revealing it.
                manager = wait(
                    lambda: named(process.pid, "本地翻译组件管理"), "package management missing"
                )
                manager.clear_cache()
                if not manager.get_state_set().contains(Atspi.StateType.EXPANDED):
                    activate("本地翻译组件管理")

                def expanded():
                    manager.clear_cache()
                    return manager.get_state_set().contains(Atspi.StateType.EXPANDED)

                wait(
                    expanded,
                    "GTK package management did not finish expanding",
                    visibility_deadline - time.monotonic(),
                )

            def showing():
                if package_control:
                    observed, _, clips = package_path()
                    target = observed[0][1]
                    return all(
                        target["states"][state]
                        for state in ("SHOWING", "FOCUSED", "SENSITIVE", "ENABLED")
                    ) and all(
                        rectangle_contains(value["bounds"], target.get("bounds")) for value in clips
                    )
                node.clear_cache()
                states = node.get_state_set()
                return not states.contains(Atspi.StateType.DEFUNCT) and all(
                    states.contains(getattr(Atspi.StateType, state))
                    for state in ("SHOWING", "FOCUSED", "SENSITIVE", "ENABLED")
                )

            def focused():
                node.clear_cache()
                return node.get_state_set().contains(Atspi.StateType.FOCUSED)

            try:
                if package_control:
                    # Expansion can publish a replacement action. Resolve it
                    # before the single focus/scroll/action sequence.
                    node = wait(
                        lambda: named(process.pid, name),
                        "current GTK package control missing: " + name,
                        visibility_deadline - time.monotonic(),
                    )
                    assert enabled(), "current GTK package control is disabled: " + name
                assert Atspi.Component.grab_focus(node.get_component_iface()), name
                if package_control:
                    wait(
                        focused,
                        "GTK package control did not receive focus: " + name,
                        visibility_deadline - time.monotonic(),
                    )
                    scroll_package()
                wait(
                    showing,
                    "GTK package control not visible and focused: " + name,
                    visibility_deadline - time.monotonic(),
                )
                assert time.monotonic() < visibility_deadline and showing(), (
                    "GTK package control changed before its single action: " + name
                )
                assert Atspi.Action.do_action(node.get_action_iface(), 0), name
            except Exception:
                try:
                    report["failedControlObservation"] = {
                        "control": name,
                        "ancestry": [value for _, value in observe()],
                        "scroll": scroll_observation,
                    }
                except Exception:
                    pass  # Diagnostics must not replace the original visibility failure.
                raise

        def action(title, label):
            select_settings_page(process.pid, "应用支持", timeout=min(15, remaining()))
            activate(title + "：" + label, package_control=True)
            wait(
                lambda: named(process.pid, label.rstrip("…") + "？"), "confirmation dialog missing"
            )
            activate(gettext.dgettext("gtk30", "_OK").replace("_", ""))

        def prepare_install():
            assert control(paths, "settings") is True
            wait(
                lambda: row()
                and any(
                    a["request"]["operation"] == "Install" and a["enabled"]
                    for a in row()["actions"]
                ),
                "shipped Hy runtime unavailable",
            )

        def preview():
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

        try:
            with (arguments.output / "desktop.log").open("w") as log:
                if capture is not None:
                    remaining()
                process = subprocess.Popen(
                    [str(paths.launcher), "--background"], env=environment, stdout=log, stderr=log
                )
                exercise_model_lifecycle(
                    action,
                    ready,
                    prepare_install,
                    lambda names, expected: collect_and_restart(names, expected, log),
                    preview,
                    lambda: subprocess.run(
                        ["grim", str(arguments.output / "hy-installed.png")],
                        check=True,
                        timeout=min(3, remaining()),
                    ),
                    lambda: json.loads((paths.models / "installed.json").read_bytes()),
                    fixed["version"],
                    capture=capture,
                    combination=combination,
                )
                if combination:
                    report["singleZhFrInference"] = "not-run-combination-only-eight-request-budget"
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
            if capture is not None:
                # A repeated outer TERM must not interrupt owned-process cleanup.
                signal.signal(signal.SIGTERM, lambda signum, frame: None)
                signal.signal(signal.SIGINT, lambda signum, frame: None)
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
                    if capture is not None:
                        try:
                            if process_identity(pid) == identity:
                                alive.append(pid)
                        except (OSError, ValueError, IndexError):
                            alive.append(pid)
                        continue
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
                        if capture is None or process_identity(pid) == seen_workers[pid]:
                            os.kill(pid, signal.SIGTERM)
                    except ProcessLookupError:
                        pass
                    except (OSError, ValueError, IndexError):
                        if capture is None:
                            raise
                grace = min(cleanup_deadline, time.monotonic() + 2)
                while surviving_workers() and time.monotonic() < grace:
                    time.sleep(0.05)
                for pid in surviving_workers():
                    try:
                        if capture is None or process_identity(pid) == seen_workers[pid]:
                            os.kill(pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                    except (OSError, ValueError, IndexError):
                        if capture is None:
                            raise
                while surviving_workers() and time.monotonic() < cleanup_deadline:
                    time.sleep(0.05)
            report["workerExitConfirmed"] = not surviving_workers()
            if capture is not None:
                try:
                    report["hostExitConfirmed"] = (
                        process is not None
                        and process.poll() is not None
                        and all(
                            process_identity(pid) != identity
                            for pid, identity in seen_hosts.items()
                        )
                    )
                except (OSError, ValueError, IndexError):
                    report["hostExitConfirmed"] = False
                if not report["hostExitConfirmed"] or not capture.report["workerExitConfirmed"]:
                    report["status"] = "failed"
                report["collectionWallSeconds"] = round(capture.elapsed, 3)
                report["nonCollectionSeconds"] = round(
                    time.monotonic() - started - capture.elapsed, 3
                )
                report["wallBudgetSeconds"] = work_seconds(combination) + 30
            report["peakAdditionalDiskBytes"] = initial_free - report["minimumDataFreeBytes"]
            report["elapsedSeconds"] = round(time.monotonic() - started, 3)
            (arguments.output / "hy-package-result.json").write_text(json.dumps(report, indent=2))
        assert report["status"] == "passed", "owned model process cleanup was not confirmed"
        if capture is None:
            (arguments.output / "desktop-result.json").write_text(json.dumps(report, indent=2))
    if capture is not None:
        (arguments.output / "hy-package-result.json").write_text(json.dumps(report, indent=2))
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
    parser.add_argument("--collect-translation-quality", action="store_true")
    parser.add_argument("--cache-only", action="store_true")
    parser.add_argument(
        "--combination-only",
        action="store_true",
        help="Collect only eight paired requests during the cached install/select/remove lifecycle",
    )
    args = parser.parse_args()
    if args.combination_only and not (args.check_budget and args.collect_translation_quality):
        parser.error("--combination-only requires --check-budget --collect-translation-quality")
    if args.cache_only and not args.prepare_cache:
        parser.error("--cache-only requires --prepare-cache")
    if args.collect_translation_quality and not args.check_budget:
        parser.error("--collect-translation-quality is only valid with --check-budget")
    if args.prepare_cache:
        if args.cache is None:
            parser.error("--prepare-cache requires --cache")
        try:
            report = prepare_cache(
                args.package.resolve(), args.cache.resolve(), cache_only=args.cache_only
            )
        except (OSError, ValueError) as error:
            print(
                json.dumps(
                    {
                        "status": "failed",
                        "quality": "not-run",
                        "workerStarts": 0,
                        "cacheOnly": args.cache_only,
                        "failureCode": type(error).__name__,
                        "reason": "existing-cache-or-native-verification-failed",
                    }
                )
            )
            raise
        print(json.dumps(report))
    else:
        if args.data is None or args.remaining_seconds is None:
            parser.error("--check-budget requires --data and --remaining-seconds")
        report = {
            **disk_budget(args.package.resolve(), args.data.resolve()),
            "remainingSeconds": args.remaining_seconds,
            "requiredSeconds": 12 * 60,
            "freeBytes": shutil.disk_usage(args.data).free,
        }
        if args.collect_translation_quality:
            # Replace the ordinary five-minute lifecycle allowance, not append it.
            # Cache restore, preparation and cache save keep their original bounds.
            capture = work_seconds(args.combination_only) + CLEANUP_SECONDS
            report.update(
                preparationSeconds=7 * 60,
                captureSeconds=capture,
                captureStepOverheadSeconds=0 if args.combination_only else 36 * 60 - capture,
                artifactSeconds=3 * 60,
                requiredSeconds=7 * 60
                + capture
                + (0 if args.combination_only else 36 * 60 - capture)
                + 3 * 60,
            )
        report["admitted"] = (
            report["remainingSeconds"] >= report["requiredSeconds"]
            and report["freeBytes"] >= report["requiredCacheAndDesktopFreeBytes"]
        )
        print(json.dumps(report, indent=2))
        if not report["admitted"]:
            raise SystemExit(
                "Insufficient bounded Hy lifecycle time or disk; no model download started."
            )
