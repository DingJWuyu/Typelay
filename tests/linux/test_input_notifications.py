"""Exercise the production notification ledger, without claiming native GUI/IME coverage."""

import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class InputNotificationTests(unittest.TestCase):
    def test_completed_notifications_only_reconcile_the_exact_stable_provider_epoch(self):
        with tempfile.TemporaryDirectory(prefix="typelay-input-notifications-") as directory:
            executable = Path(directory) / "notifications"
            subprocess.run(
                [
                    "cc",
                    "-std=c17",
                    "-Wall",
                    "-Wextra",
                    "-Werror",
                    str(ROOT / "tests/linux/fixtures/input_notifications.c"),
                    "-o",
                    str(executable),
                ],
                check=True,
                timeout=30,
            )
            subprocess.run([str(executable)], check=True, timeout=5)


if __name__ == "__main__":
    unittest.main()
