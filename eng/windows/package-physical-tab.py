"""Package physical Tab after original ordered negatives and relocated missing-eligibility runtime smoke."""

import base64
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

CLASS = "TypeLay.App.Tests.NativeSourceControllerTests"
MANUAL = "ManualPhysicalAcceptanceKeyUsesOrderedSourceOnceAndSettlesItsOriginalTails"
NEGATIVE = "OrderedSourceRecordsTheRealInjectedEdgeButDoesNotConsumeOrWrite"
SMOKE = "RuntimeRejectsMissingSourceEligibilityRegardlessOfAutomationOrBrokerInjection"
OUTPUTS = {
    "tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows": "TypeLay.App.Tests",
    "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0": "TypeLay.NativeFixture",
    "src/windows/TypeLay.Broker/bin/Release/net10.0-windows": "TypeLay.Broker",
}
APP_OUTPUT = next(iter(OUTPUTS))
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
# These exact commits come from the locked NuGet packages' repository metadata.
UPSTREAM = {
    "728c1dce012cd82193035dddfeaba184baaa88c6": ("xunit/LICENSE", "Apache-2.0.txt"),
    "50e68bbb8b9ddd4b1bbb95d062b62010caf99909": (
        "xunit-visualstudio/License.txt",
        "Apache-2.0.txt",
    ),
    "a2260df3e96395e6b513e5c7485bd6c53806871e": ("xunit-analyzers/LICENSE", "Apache-2.0.txt"),
    "490850ae3fdc1b470e3804ceab4f6a41cf89ae51": (
        "vstest/LICENSE",
        "vstest/THIRD-PARTY-NOTICES.txt",
    ),
    "cb5afc3bb9bb01ebd75b57f89e8358b914ee2a49": (
        "testfx/LICENSE",
        "testfx/THIRD-PARTY-NOTICES.TXT",
    ),
    "2faa7e8b157a431daa2e71785d68abd5fa817b53": ("applicationinsights/LICENSE",),
}


def copy_tree(source, destination):
    """Copy an output tree, never following a symlink or Windows junction."""
    if source.is_symlink() or source.is_junction():
        raise ValueError(f"Linked package input: {source}")
    destination.mkdir(parents=True)
    for path in source.iterdir():
        if path.is_symlink() or path.is_junction():
            raise ValueError(f"Linked package input: {path}")
        target = destination / path.name
        if path.is_dir():
            copy_tree(path, target)
        elif path.is_file():
            shutil.copyfile(path, target)
        else:
            raise ValueError(f"Not a regular package input: {path}")


def method_results(path, expected_method):
    root = ET.parse(path).getroot()
    definitions = {}
    for test in root.findall("t:TestDefinitions/t:UnitTest", NS):
        method = test.find("t:TestMethod", NS)
        if (
            method is not None
            and method.get("className") == CLASS
            and method.get("name") == expected_method
        ):
            if (
                Path(method.get("codeBase", "").replace("\\", "/")).name.lower()
                != "typelay.app.tests.dll"
            ):
                raise ValueError("Selected test came from a different assembly")
            identifier = test.get("id")
            if not identifier or identifier in definitions:
                raise ValueError("Missing or duplicate TRX test definition")
            definitions[identifier] = method
    results = [
        r for r in root.findall("t:Results/t:UnitTestResult", NS) if r.get("testId") in definitions
    ]
    if len(results) != len(definitions) or len({r.get("testId") for r in results}) != len(results):
        raise ValueError("Missing or duplicate selected result")
    return root, results


def current_main_results(directory):
    """Read only the explicitly selected, completed contract run's main partition."""
    directory = directory.resolve()
    current = directory / "current-contracts.json"
    try:
        if current.is_symlink():
            raise ValueError("Current contract pointer must not be a link")
        pointer = json.loads(current.read_text(encoding="utf-8"))
        if (
            not isinstance(pointer, dict)
            or set(pointer) != {"schemaVersion", "runDirectory"}
            or type(pointer["schemaVersion"]) is not int
            or pointer["schemaVersion"] != 1
            or not isinstance(pointer["runDirectory"], str)
            or re.fullmatch(r"contracts-[0-9a-f]{32}", pointer["runDirectory"]) is None
        ):
            raise ValueError("Invalid current contract pointer")
        run = directory / pointer["runDirectory"]
        report_path = run / "result.json"
        primary = run / "main"
        if (
            run.is_symlink()
            or run.resolve() != run
            or report_path.is_symlink()
            or primary.is_symlink()
            or primary.resolve() != run.resolve() / "main"
            or not primary.is_dir()
        ):
            raise ValueError("Contract results must stay in their exact run directory")
        report = json.loads(report_path.read_text(encoding="utf-8"))
        if (
            not isinstance(report, dict)
            or not isinstance(report.get("mainDirectory"), str)
            or Path(report["mainDirectory"]).resolve() != primary.resolve()
            or not isinstance(report.get("failures"), list)
            or any(
                failure in ("interrupted", "main-canceled", "isolated-canceled")
                for failure in report["failures"]
            )
            or any(
                type(report.get(key)) is not int
                or report[key] in (-2, -15, 130, -1073741510, 0xC000013A)
                for key in ("mainExitCode", "isolatedExitCode")
            )
        ):
            raise ValueError("Current contract run is incomplete, canceled, or mismatched")
        return primary
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError("Current contract run evidence is unavailable") from error


def verify_original_suite(directory):
    results = []
    for path in directory.glob("*.trx"):
        _, selected = method_results(path, NEGATIVE)
        results.extend(selected)
    if (
        len(results) != 2
        or {r.get("testName") for r in results}
        != {
            f'{CLASS}.{NEGATIVE}(accept: "{accept}", key: {key})'
            for accept, key in (("Tab", 9), ("F7", 118))
        }
        or any(r.get("outcome") != "Passed" for r in results)
    ):
        raise ValueError(
            "Original suite must already contain Passed ordered Tab/9 and F7/118 once each"
        )


def verify_smoke(path):
    root, selected = method_results(path, SMOKE)
    counters = root.find("t:ResultSummary/t:Counters", NS)
    if (
        len(selected) != 2
        or {r.get("testName") for r in selected}
        != {f"{CLASS}.{SMOKE}(automationTest: {case})" for case in (False, True)}
        or len(root.findall("t:Results/t:UnitTestResult", NS)) != 2
        or any(r.get("outcome") != "Passed" for r in selected)
        or counters is None
        or any(counters.get(k) != "2" for k in ("total", "executed", "passed"))
        or any(counters.get(k) != "0" for k in ("failed", "error", "notExecuted", "notRunnable"))
        or any(
            v != "0" for k, v in counters.attrib.items() if k not in ("total", "executed", "passed")
        )
        or root.find("t:ResultSummary", NS).get("outcome") != "Completed"
    ):
        raise ValueError(
            "Relocated missing-eligibility runtime smoke needs False/True each executed/Passed, no skips or errors"
        )


def verify_discovery(path, assembly):
    rows = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(rows, list) or len(rows) != 1:
        raise ValueError("Physical Tab discovery must contain exactly one row")
    row = rows[0]
    if (
        Path(row.get("Assembly", "")).resolve() != assembly.resolve()
        or row.get("Class") != CLASS
        or row.get("Method") != MANUAL
        or "Skip" in row
        or row.get("Traits", {}).get("ManualPhysicalKey") != ["Tab"]
    ):
        raise ValueError("Physical Tab discovery metadata does not match the exact unskipped row")


def copy_notices(root, stage):
    project = root / "tests/windows/TypeLay.App.Tests"
    locked = json.loads((project / "packages.lock.json").read_text(encoding="utf-8"))
    dependencies = next(iter(locked["dependencies"].values()))
    assets = json.loads((project / "obj/project.assets.json").read_text(encoding="utf-8"))
    folders = [Path(p) for p in assets["packageFolders"]]
    # Retain the complete locked test-dependency notices, including build-only
    # metadata. No binaries outside the three built output trees are copied.
    for name, dependency in dependencies.items():
        if dependency["type"] == "Project":
            continue
        version = dependency["resolved"]
        relative = Path(name.lower()) / version
        found = [folder / relative for folder in folders if (folder / relative).is_dir()]
        if len(found) != 1:
            raise ValueError(f"Expected one restored package: {name}/{version}")
        source = found[0]
        nuspec = source / f"{name.lower()}.nuspec"
        with (source / f"{name.lower()}.{version}.nupkg").open("rb") as package:
            archive_hash = base64.b64encode(
                hashlib.file_digest(package, "sha512").digest()
            ).decode()
        # NuGet's content hash is not the SHA512 of a repository-signed archive.
        # Keep locked-restore metadata and the cached archive checksum distinct.
        restored = json.loads((source / ".nupkg.metadata").read_text(encoding="utf-8"))
        cached_hash = (source / f"{name.lower()}.{version}.nupkg.sha512").read_text().strip()
        if restored["contentHash"] != dependency["contentHash"] or archive_hash != cached_hash:
            raise ValueError(f"Restored package does not match the source lock: {name}/{version}")
        metadata = ET.parse(nuspec).getroot()
        repository = metadata.find(".//{*}repository")
        license_node = metadata.find(".//{*}license")
        if (
            license_node is None
            or license_node.get("type") != "expression"
            or license_node.text not in ("MIT", "Apache-2.0")
        ):
            raise ValueError(f"Review changed package license before shipping {name}/{version}")
        destination = stage / "licenses/packages" / relative
        destination.mkdir(parents=True)
        shutil.copyfile(nuspec, destination / nuspec.name)
        notices = [
            p
            for p in source.rglob("*")
            if p.is_file()
            and re.search(
                r"(^|[._-])(LICENSE|LICENCE|COPYING|NOTICE|THIRD[-_]?PARTY)", p.name, re.I
            )
        ]
        for notice in notices:
            if notice.is_symlink() or notice.is_junction():
                raise ValueError(f"Linked package notice: {notice}")
            target = destination / notice.relative_to(source)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(notice, target)
        commit = repository.get("commit") if repository is not None else None
        for filename in UPSTREAM.get(commit, ()):
            if not (root / "packaging/windows/physical-tab/licenses" / filename).is_file():
                raise ValueError(f"Missing required upstream legal text: {filename}")
        if commit not in UPSTREAM and not any(
            p.name.lower().startswith("license") for p in notices
        ):
            raise ValueError(f"Missing original license for {name}/{version}; review required")
    copy_tree(root / "packaging/windows/physical-tab/licenses", stage / "licenses/upstream")
    shutil.copyfile(project / "packages.lock.json", stage / "licenses/packages.lock.json")


def stage_bundle(root, stage):
    stage.mkdir()
    for relative, name in OUTPUTS.items():
        source = root / relative
        if source.is_symlink() or source.is_junction():
            raise ValueError(f"Linked build output: {source}")
        target = stage / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        # The earlier product publish creates a separate RID output below the
        # Broker build directory. It is not this framework-dependent test build.
        target.mkdir()
        for path in source.iterdir():
            if path.name == "win-x64" and path.is_dir():
                continue
            if path.is_dir():
                copy_tree(path, target / path.name)
            elif path.is_file() and not path.is_symlink():
                shutil.copyfile(path, target / path.name)
            else:
                raise ValueError(f"Unexpected build output: {path}")
        for filename in (
            f"{name}.dll",
            f"{name}.deps.json",
            f"{name}.runtimeconfig.json",
            "TypeLay.NativeInput.dll",
        ):
            if not (target / filename).is_file():
                raise ValueError(f"Missing built dependency: {target / filename}")
        options = json.loads((target / f"{name}.runtimeconfig.json").read_text(encoding="utf-8"))[
            "runtimeOptions"
        ]
        frameworks = options.get("frameworks", [options.get("framework", {})])
        if (
            "includedFrameworks" in options
            or not frameworks
            or any(not f.get("version", "").startswith("10.") for f in frameworks)
        ):
            raise ValueError(f"Expected framework-dependent .NET 10 output: {name}")
    native = [(stage / relative / "TypeLay.NativeInput.dll").read_bytes() for relative in OUTPUTS]
    if not native[0] or any(value != native[0] for value in native[1:]):
        raise ValueError("NativeInput must be the same built DLL in all three output trees")
    if not (stage / APP_OUTPUT / "TypeLay.App.Tests.exe").is_file():
        raise ValueError("Missing existing xUnit executable")
    if any(
        p.name.lower() in ("coreclr.dll", "hostfxr.dll", "hostpolicy.dll") for p in stage.rglob("*")
    ):
        raise ValueError("Do not embed a self-contained runtime in the physical validation package")
    shutil.copyfile(root / "TypeLay.slnx", stage / "TypeLay.slnx")
    for filename in ("README.md", "THIRD_PARTY_NOTICES.md"):
        shutil.copyfile(root / "packaging/windows/physical-tab" / filename, stage / filename)
    copy_notices(root, stage)


def main():
    if sys.platform != "win32":
        raise SystemExit("Packaging smoke requires the existing Windows x64 build and desktop")
    root = Path(__file__).resolve().parents[2]
    artifacts = root / "artifacts"
    archive = artifacts / "TypeLay-Windows-physical-Tab-validation.zip"
    checksum = archive.with_suffix(".zip.sha256")
    # Never leave a previous run's usable ZIP under the final upload glob.
    archive.unlink(missing_ok=True)
    checksum.unlink(missing_ok=True)
    evidence = artifacts / "physical-tab-package"
    evidence.mkdir()
    deadline = time.monotonic() + 110  # Work shares one limit; cleanup gets at most 5s.
    start = int(os.environ["TYPELAY_WINDOWS_CHECK_START"])
    if not 0 <= time.time() - start <= 80 * 60 - 180:
        raise ValueError("Need the existing 2-minute tail slot plus 1 minute for artifact upload")
    verify_original_suite(current_main_results(artifacts / "test-results"))
    with tempfile.TemporaryDirectory(
        prefix="TypeLay physical Tab 中 ", dir=os.environ["RUNNER_TEMP"]
    ) as temporary:
        temporary = Path(temporary).resolve()
        if temporary.is_relative_to(root):
            raise ValueError("Relocation must be outside the checkout")
        stage = temporary / "stage"
        stage_bundle(root, stage)
        head = subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=root, text=True, timeout=5
        ).strip()
        if head != os.environ["GITHUB_SHA"]:
            raise ValueError("Build checkout does not match this workflow head")
        identity = f"source={head}\nrun={os.environ['GITHUB_RUN_ID']}\nattempt={os.environ['GITHUB_RUN_ATTEMPT']}\nconfiguration=Release\n"
        (stage / "BUILD.txt").write_text(identity, encoding="utf-8")
        (evidence / "BUILD.txt").write_text(identity, encoding="utf-8")
        candidate = temporary / "candidate.zip"
        with zipfile.ZipFile(candidate, "w", zipfile.ZIP_DEFLATED) as zipped:
            for path in sorted(stage.rglob("*")):
                if path.is_file():
                    zipped.write(path, Path("TypeLay-Physical-Tab") / path.relative_to(stage))
        relocated = temporary / "relocated"
        with zipfile.ZipFile(candidate) as zipped:
            zipped.extractall(relocated)
        bundle = relocated / "TypeLay-Physical-Tab"
        exe = bundle / APP_OUTPUT / "TypeLay.App.Tests.exe"
        common = ["-preEnumerateTheories", "-noAutoReporters", "-noColor", "-noLogo"]
        commands = [
            (
                "discovery",
                [
                    *common,
                    "-explicit",
                    "only",
                    "-method",
                    f"{CLASS}.{MANUAL}",
                    "-trait",
                    "ManualPhysicalKey=Tab",
                    "-list",
                    "full/json",
                ],
            ),
            (
                "smoke",
                [
                    *common,
                    "-explicit",
                    "off",
                    "-method",
                    f"{CLASS}.{SMOKE}",
                    "-trx",
                    str(evidence / "smoke.trx"),
                ],
            ),
        ]
        for phase, arguments in commands:
            output = evidence / ("discovery.json" if phase == "discovery" else "runner.log")
            with output.open("wb") as stdout, (evidence / f"{phase}.stderr").open("wb") as stderr:
                process = subprocess.Popen(
                    [str(exe), *arguments], cwd=bundle, stdout=stdout, stderr=stderr
                )
                try:
                    code = process.wait(timeout=max(0, deadline - time.monotonic()))
                except subprocess.TimeoutExpired:
                    # Only the exact runner started above and its descendants.
                    # Timeout/forced cleanup can never authorize a downloadable ZIP.
                    cleanup = time.monotonic() + 5
                    try:
                        subprocess.run(
                            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                            stdout=stderr,
                            stderr=stderr,
                            timeout=5,
                            check=False,
                        )
                        process.wait(timeout=max(0, cleanup - time.monotonic()))
                    finally:
                        (evidence / f"{phase}.exit-code.txt").write_text(
                            f"timeout; runnerExit={process.poll()}; cleanupNotAcceptance=true\n",
                            encoding="ascii",
                        )
                    raise
            (evidence / f"{phase}.exit-code.txt").write_text(f"{code}\n", encoding="ascii")
            if code != 0:
                raise ValueError(f"{phase} failed with exit {code}; no validation ZIP")
            if phase == "discovery":
                verify_discovery(output, exe.with_suffix(".dll"))
            else:
                verify_smoke(evidence / "smoke.trx")
        if time.monotonic() > deadline:
            raise TimeoutError("Package validation exceeded its shared work limit")
        # Do not publish until the relocated directory has also been removed.
        pending = evidence / "candidate.zip.pending"
        shutil.copyfile(candidate, pending)
    with pending.open("rb") as package:
        digest = hashlib.file_digest(package, "sha256").hexdigest()
    checksum.write_text(f"{digest}  {archive.name}\n", encoding="ascii")
    pending.replace(archive)
    print(
        "Relocated missing-eligibility runtime smoke passed; physical Tab was discovered, not executed."
    )


if __name__ == "__main__":
    main()
