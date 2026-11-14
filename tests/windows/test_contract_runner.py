"""Windows runner CLI contracts; a controlled dotnet command is not a timing test."""

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

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
if not isolated and mode == "manual":
    SubElement(results, "UnitTestResult", testName="Controlled.ExplicitManual", outcome="NotExecuted")
ElementTree(root).write(directory / "controlled.trx", encoding="utf-8", xml_declaration=True)
if not isolated and mode == "main-interrupt":
    os.kill(os.getppid(), signal.SIGINT)
    signal.pause()  # Keep the owned port active until subprocess.run cleans it up.
    sys.exit(0)
if not isolated and mode == "main-cancel": sys.exit(130)
sys.exit(7 if not isolated and mode == "main-fail" else 9 if isolated and mode == "isolated-fail" else 0)
"""


class WindowsContractRunnerTests(unittest.TestCase):
    def test_shard_cli_keeps_explicit_manual_cases_and_isolates_timeouts_once(self):
        filters = [
            self.run_case("manual", shard).split("&FullyQualifiedName!=", 1)[0]
            for shard in range(1, 5)
        ]
        # These classes have actual TRX evidence; the final class is new and
        # must be collected by the complement without changing a registry.
        expected = {
            "SetupRenderingTests": 1,
            "NativeSourceControllerTests": 2,
            "ModelInstallerTests": 2,
            "SettingsRenderingTests": 3,
            "SourceKeyboardBoundaryProbe": 4,
            "FutureRegressionTests": 4,
        }
        for name, shard in expected.items():
            token = f"TypeLay.App.Tests.{name}."
            selected = [
                i + 1
                for i, value in enumerate(filters)
                if (token in value if i < 3 else token not in value)
            ]
            self.assertEqual([shard], selected, name)

    def run_case(self, mode, shard=None):
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
            results.mkdir()
            (results / "current-contracts.json").write_text(
                json.dumps({"schemaVersion": 1, "runDirectory": "contracts-" + "f" * 32})
            )
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
            command = [
                sys.executable,
                "-B",
                str(ROOT / "eng/windows/test_contracts.py"),
                "--configuration",
                "Debug",
                "--results-directory",
                str(results),
                *(["--shard", str(shard)] if shard else []),
            ]
            if shard is None:
                execution = subprocess.run(
                    command, env=environment, cwd=ROOT, capture_output=True, text=True, timeout=20
                )
            else:
                spec = importlib.util.spec_from_file_location(
                    "contracts", ROOT / "eng/windows/test_contracts.py"
                )
                runner = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(runner)
                original_run = subprocess.run

                def dotnet_port(arguments, **kwargs):
                    # The real port is dotnet.exe. A .cmd stand-in would interpret
                    # VSTest's | and & as shell operators before Python sees them.
                    return original_run(
                        [sys.executable, str(implementation), *arguments[1:]],
                        env=environment,
                        **kwargs,
                    )

                with (
                    patch.dict(os.environ, environment),
                    patch.object(sys, "argv", command[2:]),
                    patch.object(runner.subprocess, "run", side_effect=dotnet_port),
                ):
                    execution = SimpleNamespace(returncode=runner.main(), stderr="")
            passed = mode in ("pass", "manual")
            self.assertEqual(passed, execution.returncode == 0, execution.stderr)
            invoked = [json.loads(line) for line in calls.read_text().splitlines()]
            canceled = mode in ("main-cancel", "main-interrupt")
            isolated_required = shard in (None, 2) and not canceled
            self.assertEqual(2 if isolated_required else 1, len(invoked))
            self.assertEqual("TypeLay.slnx", invoked[0][1])
            if isolated_required:
                self.assertEqual(
                    "tests/windows/TypeLay.App.Tests/TypeLay.App.Tests.csproj", invoked[1][1]
                )
            for arguments, comparison in zip(invoked, ("!=", "=")):
                if shard is None or comparison == "=":
                    self.assertEqual(
                        "FullyQualifiedName" + comparison + METHOD,
                        arguments[arguments.index("--filter") + 1],
                    )
                else:
                    self.assertTrue(
                        arguments[arguments.index("--filter") + 1].endswith(
                            "&FullyQualifiedName!=" + METHOD
                        )
                    )
                self.assertEqual("Debug", arguments[arguments.index("-c") + 1])
                self.assertIn("--no-build", arguments)
                self.assertIn("--no-restore", arguments)
            summaries = list(results.glob("contracts-*/result.json"))
            self.assertEqual(1, len(summaries))
            summary = json.loads(summaries[0].read_text(encoding="utf-8-sig"))
            current = json.loads((results / "current-contracts.json").read_text())
            self.assertEqual(
                {"schemaVersion": 1, "runDirectory": summaries[0].parent.name}, current
            )
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
                None if not isolated_required else 9 if mode == "isolated-fail" else 0,
                summary["isolatedExitCode"],
            )
            self.assertNotEqual(summary["mainDirectory"], summary["isolatedDirectory"])
            self.assertEqual(not passed, bool(summary["failures"]))
            if not canceled:
                expected_failed = (
                    ["Controlled.Other"]
                    if mode == "main-fail"
                    else sorted([METHOD + "(packages: False)", METHOD + "(packages: True)"])
                    if mode == "isolated-fail"
                    else []
                )
                self.assertEqual(
                    expected_failed, sorted(item["testName"] for item in summary["failedTests"])
                )
            self.assertEqual(2 if isolated_required else 1, len(list(results.rglob("*.trx"))))
            return invoked[0][invoked[0].index("--filter") + 1]

    def test_one_complete_partition_and_two_isolated_cases_succeed(self):
        self.run_case("pass")

    def test_main_failure_is_preserved_and_does_not_skip_the_independent_partition(self):
        self.run_case("main-fail")

    def test_each_shard_preserves_the_failed_test_summary(self):
        for shard in range(1, 5):
            with self.subTest(shard=shard):
                self.run_case("main-fail", shard)

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
