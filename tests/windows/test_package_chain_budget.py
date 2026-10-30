"""Exercise the actual admission CLI and its persisted decision, without models."""

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ADMISSION = Path(__file__).resolve().parents[2] / "eng/windows/admit-package-chain.py"


class PackageChainBudgetTests(unittest.TestCase):
    def test_bounded_chain_admission(self):
        for elapsed, admitted in (
            (425, True),
            (979, True),
            (1307, True),
            (1500, True),
            (1501, False),
            (5101, False),
            (-1, False),
        ):
            with self.subTest(elapsed=elapsed), tempfile.TemporaryDirectory() as directory:
                report = Path(directory) / "hy-results/admission.json"
                result = subprocess.run(
                    [
                        sys.executable,
                        str(ADMISSION),
                        "--elapsed-seconds",
                        str(elapsed),
                        "--report",
                        str(report),
                    ],
                    capture_output=True,
                    text=True,
                    timeout=10,
                    check=False,
                )
                self.assertEqual(result.returncode == 0, admitted, result.stderr)
                self.assertEqual(
                    json.loads(report.read_text(encoding="utf-8")),
                    dict(
                        admitted=admitted,
                        elapsedSeconds=elapsed,
                        remainingSeconds=5100 - elapsed,
                        requiredSeconds=3600,
                    ),
                )
                if not admitted:
                    self.assertIn("budget", result.stderr)


if __name__ == "__main__":
    unittest.main()
