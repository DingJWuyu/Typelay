"""Bounded setup evidence in the lifecycle report, without installing a model."""

import asyncio
import json
import tempfile
import unittest
from pathlib import Path

import index_package_e2e as lifecycle


class PreparationDiagnosticsTests(unittest.TestCase):
    def test_only_the_first_valid_fixed_diagnostic_is_kept(self):
        first = {"category": "unknown", "stage": "prepare"}
        later = {"category": "network", "stage": "model-download"}
        output = (
            b"private unstructured output\xff\n"
            + b"TYPELAY_SETUP_FAILURE="
            + json.dumps(first).encode()
            + b"\nTYPELAY_SETUP_FAILURE="
            + json.dumps(later).encode()
        )
        self.assertEqual(first, lifecycle.preparation_failure(output))

    def test_private_unknown_or_oversized_fields_are_not_reported(self):
        for value in (
            {"category": "network", "stage": "prepare", "message": "/private/example"},
            {"category": "network", "stage": "prepare", "resource": "secret-name"},
            {"category": "network", "stage": "prepare", "errno": True},
            {"category": "network", "stage": "prepare", "padding": "x" * 1024},
        ):
            with self.subTest(value=value):
                output = b"TYPELAY_SETUP_FAILURE=" + json.dumps(value).encode()
                self.assertIsNone(lifecycle.preparation_failure(output))

    def test_prepare_still_fails_but_reports_only_the_fixed_failure(self):
        diagnostic = {
            "category": "network",
            "stage": "model-download",
            "resource": "argos-zh-en",
            "networkReason": "http",
            "httpStatus": 503,
        }
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            package, output = root / "package", root / "output"
            (package / "desktop").mkdir(parents=True)
            output.mkdir()
            marker = "TYPELAY_SETUP_FAILURE=" + json.dumps(diagnostic)
            (package / "desktop/setup.py").write_text(
                f"print('unstructured private details')\nprint({marker!r})\nraise SystemExit(7)\n"
            )
            check = lifecycle.Check(package, root / "cache", output)
            check.begin("install-argos")
            with self.assertRaisesRegex(AssertionError, "production-prepare-failed"):
                asyncio.run(check.prepare())
            self.assertEqual(
                {"exitCode": 7, "diagnostic": diagnostic},
                check.report["preparationFailures"]["install-argos"],
            )
            self.assertNotIn("private", json.dumps(check.report))
            self.assertTrue(all(child.returncode == 7 for child in check.children))


if __name__ == "__main__":
    unittest.main()
