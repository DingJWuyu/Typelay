"""Windows runner CLI contracts; a controlled dotnet command is not a timing test."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
METHOD = (
    "TypeLay.App.Tests.ModelInstallerTests."
    "DownloadTimeoutAlsoBoundsAnArchiveBodyAfterHeadersAndPartialBytes"
)

SHIM = r"""
import json, os, signal, sys
from pathlib import Path
from xml.etree.ElementTree import Element, SubElement, ElementTree
args = sys.argv[1:]
mode = os.environ["TYPELAY_RUNNER_CASE"]
method = os.environ["TYPELAY_RUNNER_METHOD"]
isolated = args[args.index("--filter") + 1] == "FullyQualifiedName=" + method
with Path(os.environ["TYPELAY_RUNNER_CALLS"]).open("a") as stream:
    stream.write(json.dumps(args) + "\n")
directory = Path(args[args.index("--results-directory") + 1])
root = Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
results = SubElement(root, "Results")
names = [method + "(packages: False)", method + "(packages: True)"] if isolated else ["Controlled.Other"]
if isolated and mode == "missing": names = names[:1]
if isolated and mode == "duplicate": names = [names[0], names[0]]
if not isolated and mode == "leak": names.append(method + "(packages: False)")
for name in names:
    outcome = "NotExecuted" if isolated and mode == "skip" else "Passed"
    if (isolated and mode == "isolated-fail") or (not isolated and mode == "main-fail"):
        outcome = "Failed"
    SubElement(results, "UnitTestResult", testName=name, outcome=outcome)
ElementTree(root).write(directory / "controlled.trx", encoding="utf-8", xml_declaration=True)
if not isolated and mode == "main-interrupt":
    os.kill(os.getppid(), signal.SIGINT)
    signal.pause()  # Keep the owned port active until subprocess.run cleans it up.
    sys.exit(0)
if not isolated and mode == "main-cancel": sys.exit(130)
sys.exit(7 if not isolated and mode == "main-fail" else 9 if isolated and mode == "isolated-fail" else 0)
"""


class WindowsContractRunnerTests(unittest.TestCase):
    def run_case(self, mode):
        with tempfile.TemporaryDirectory(prefix="TypeLay contract runner ") as directory:
            root = Path(directory)
            shim = root / ("dotnet.cmd" if sys.platform == "win32" else "dotnet")
            implementation = root / "dotnet-port.py"
            implementation.write_text(SHIM, encoding="utf-8")
            if sys.platform == "win32":
                shim.write_text(
                    "@echo off\nsetlocal DisableDelayedExpansion\n"
                    f'"{sys.executable}" "{implementation}" %*\nexit /b %errorlevel%\n',
                    encoding="utf-8",
                )
            else:
                shim.write_text(f"#!{sys.executable}\n" + SHIM, encoding="utf-8")
                shim.chmod(0o700)
            calls = root / "calls.jsonl"
            results = root / "results"
            environment = dict(
                os.environ,
                PATH=str(root) + os.pathsep + os.environ["PATH"],
                TYPELAY_RUNNER_CASE=mode,
                TYPELAY_RUNNER_METHOD=METHOD,
                TYPELAY_RUNNER_CALLS=str(calls),
            )
            # Never let the controlled test accidentally invoke the actual SDK.
            self.assertEqual(
                shim.resolve(), Path(shutil.which("dotnet", path=environment["PATH"])).resolve()
            )
            execution = subprocess.run(
                [
                    sys.executable,
                    "-B",
                    str(ROOT / "eng/windows/test_contracts.py"),
                    "--configuration",
                    "Debug",
                    "--results-directory",
                    str(results),
                ],
                env=environment,
                cwd=ROOT,
                capture_output=True,
                text=True,
                timeout=20,
            )
            self.assertEqual(mode == "pass", execution.returncode == 0, execution.stderr)
            invoked = [json.loads(line) for line in calls.read_text().splitlines()]
            canceled = mode in ("main-cancel", "main-interrupt")
            self.assertEqual(1 if canceled else 2, len(invoked))
            self.assertEqual("TypeLay.slnx", invoked[0][1])
            if not canceled:
                self.assertEqual(
                    "tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj", invoked[1][1]
                )
            for arguments, comparison in zip(invoked, ("!=", "=")):
                self.assertEqual(
                    "FullyQualifiedName" + comparison + METHOD,
                    arguments[arguments.index("--filter") + 1],
                )
                self.assertEqual("Debug", arguments[arguments.index("-c") + 1])
                self.assertIn("--no-build", arguments)
                self.assertIn("--no-restore", arguments)
            summaries = list(results.glob("contracts-*/result.json"))
            self.assertEqual(1, len(summaries))
            summary = json.loads(summaries[0].read_text(encoding="utf-8-sig"))
            expected_main = (
                None
                if mode == "main-interrupt"
                else 130
                if mode == "main-cancel"
                else 7
                if mode == "main-fail"
                else 0
            )
            self.assertEqual(expected_main, summary["mainExitCode"])
            self.assertEqual(
                None if canceled else 9 if mode == "isolated-fail" else 0,
                summary["isolatedExitCode"],
            )
            self.assertNotEqual(summary["mainDirectory"], summary["isolatedDirectory"])
            self.assertEqual(mode != "pass", bool(summary["failures"]))
            self.assertEqual(1 if canceled else 2, len(list(results.rglob("*.trx"))))

    def test_one_complete_partition_and_two_isolated_cases_succeed(self):
        self.run_case("pass")

    def test_main_failure_is_preserved_and_does_not_skip_the_independent_partition(self):
        self.run_case("main-fail")

    def test_isolated_failure_cannot_be_hidden_by_the_main_success(self):
        self.run_case("isolated-fail")

    def test_main_cancellation_does_not_launch_the_next_partition(self):
        self.run_case("main-cancel")

    @unittest.skipIf(
        sys.platform == "win32",
        "POSIX signal delivery; Windows cancellation code checked separately",
    )
    def test_interrupt_of_the_owned_runner_does_not_launch_the_next_partition(self):
        self.run_case("main-interrupt")

    def test_missing_duplicate_skipped_or_leaked_cases_fail(self):
        for mode in ("missing", "duplicate", "skip", "leak"):
            with self.subTest(mode=mode):
                self.run_case(mode)
