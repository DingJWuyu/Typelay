"""Run Windows contracts once, with the two wall-clock body tests isolated."""

import argparse
import json
import shutil
import subprocess
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
METHOD = (
    "TypeLay.App.Tests.ModelInstallerTests."
    "DownloadTimeoutAlsoBoundsAnArchiveBodyAfterHeadersAndPartialBytes"
)
CANCELED = {-2, -15, 130, -1073741510, 0xC000013A}


def results(directory):
    return [
        node.attrib
        for path in sorted(directory.glob("*.trx"))
        for node in ET.parse(path).getroot().iter()
        if node.tag.rsplit("}", 1)[-1] == "UnitTestResult"
    ]


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    parser.add_argument("--results-directory", type=Path, default=Path("artifacts/test-results"))
    args = parser.parse_args()
    results_root = (ROOT / args.results_directory).resolve()
    results_root.mkdir(parents=True, exist_ok=True)
    current = results_root / "current-contracts.json"
    # Invalidate old admission even if this invocation fails before dotnet starts.
    current.unlink(missing_ok=True)
    directory = results_root / ("contracts-" + uuid.uuid4().hex)
    primary, isolated = directory / "main", directory / "archive-body-timeout"
    primary.mkdir(parents=True)
    isolated.mkdir()
    pointer = directory / "current.tmp"
    pointer.write_text(
        json.dumps({"schemaVersion": 1, "runDirectory": directory.name}) + "\n",
        encoding="utf-8",
    )
    pointer.replace(current)
    report = {"mainExitCode": None, "isolatedExitCode": None, "failures": []}
    failures = report["failures"]
    try:
        dotnet = shutil.which("dotnet")
        if dotnet is None:
            raise FileNotFoundError("dotnet")
        common = ["-c", args.configuration, "--no-build", "--no-restore", "--logger", "trx"]
        first = subprocess.run(
            [
                dotnet,
                "test",
                "TypeLay.slnx",
                *common,
                "--filter",
                f"FullyQualifiedName!={METHOD}",
                "--results-directory",
                str(primary),
            ],
            cwd=ROOT,
            check=False,
        ).returncode
        report["mainExitCode"] = first
        if first in CANCELED:
            failures.append("main-canceled")
            return 130
        if first != 0:
            failures.append("main-exit")
        # This is the only execution of these two cases, not a failed-test retry.
        second = subprocess.run(
            [
                dotnet,
                "test",
                "tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj",
                *common,
                "--filter",
                f"FullyQualifiedName={METHOD}",
                "--results-directory",
                str(isolated),
            ],
            cwd=ROOT,
            check=False,
        ).returncode
        report["isolatedExitCode"] = second
        if second in CANCELED:
            failures.append("isolated-canceled")
            return 130
        if second != 0:
            failures.append("isolated-exit")
        ordinary_results, timed_results = results(primary), results(isolated)
        report.update(mainResults=len(ordinary_results), isolatedResults=len(timed_results))
        if not ordinary_results:
            failures.append("main-results-missing")
        if any(r.get("outcome") == "Failed" for r in ordinary_results):
            failures.append("main-result-failed")
        if any(
            r.get("testName", "") == METHOD or r.get("testName", "").startswith(METHOD + "(")
            for r in ordinary_results
        ):
            failures.append("isolated-cases-also-ran-in-main")
        expected = {METHOD + "(packages: False)", METHOD + "(packages: True)"}
        if len(timed_results) != 2 or {r.get("testName") for r in timed_results} != expected:
            failures.append("isolated-case-set")
        if any(r.get("outcome") != "Passed" for r in timed_results):
            failures.append("isolated-case-not-passed")
        return 1 if failures else 0
    except KeyboardInterrupt:
        failures.append("interrupted")
        return 130
    except (OSError, ET.ParseError) as error:
        failures.append(type(error).__name__)
        return 1
    finally:
        report.update(mainDirectory=str(primary), isolatedDirectory=str(isolated))
        completed = directory / "result.tmp"
        completed.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        completed.replace(directory / "result.json")
        print(json.dumps({"results": str(directory), "failures": failures}), flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
