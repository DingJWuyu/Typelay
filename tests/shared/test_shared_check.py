"""Exercise the shared-check CLI with failures at its external command boundary."""

import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SUITES = (
    "tests/shared/TypeLay.Core.Tests",
    "tests/shared/TypeLay.Runtime.Tests",
    "tests/shared/TypeLay.Protocol.Tests",
    "tests/windows/TypeLay.Windows.Contracts.Tests",
    "tests/shared",
    "tests/shared/worker",
)


class SharedCheckTests(unittest.TestCase):
    def run_check(self, failing_suite):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for relative in ("dev.ps1", "eng/shared/check.ps1", "eng/shared/commands.ps1"):
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / relative, target)
            log = root / "commands.jsonl"
            driver = root / "drive.ps1"
            driver.write_text(
                """
function global:dotnet {
    Add-Content $env:CHECK_COMMAND_LOG (ConvertTo-Json -Compress -InputObject @($args))
    $global:LASTEXITCODE = if ($env:CHECK_FAIL_SUITE -in $args) { 23 } else { 0 }
}
function global:python {
    Add-Content $env:CHECK_COMMAND_LOG (ConvertTo-Json -Compress -InputObject @($args))
    $global:LASTEXITCODE = if ($env:CHECK_FAIL_SUITE -in $args) { 23 } else { 0 }
}
& "$PSScriptRoot/dev.ps1" shared-check
""",
                encoding="utf-8",
            )
            result = subprocess.run(
                ["pwsh", "-NoProfile", "-File", str(driver)],
                cwd=root,
                env={
                    **os.environ,
                    "CHECK_COMMAND_LOG": str(log),
                    "CHECK_FAIL_SUITE": failing_suite,
                },
                capture_output=True,
                text=True,
                timeout=30,
            )
            calls = [json.loads(line) for line in log.read_text().splitlines()]
            return result, calls

    def test_suite_failure_keeps_independent_suites_running_and_command_fails(self):
        for failing in SUITES:
            with self.subTest(failing=failing):
                result, calls = self.run_check(failing)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(calls), len(SUITES), result.stdout + result.stderr)
                for suite in SUITES:
                    self.assertTrue(any(suite in call for call in calls), suite)

    def test_all_suites_passing_keeps_the_cli_successful(self):
        result, calls = self.run_check("")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(calls), len(SUITES))


if __name__ == "__main__":
    unittest.main()
