"""Bounded setup evidence in the lifecycle report, without installing a model."""

import asyncio
import json
import sys
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


class HostDiagnosticsTests(unittest.TestCase):
    def run_peer(self, start_result=True, activation="confirmed", current=True, status=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            native = root / "package/native"
            native.mkdir(parents=True)
            peer = native / "typelay-supervise"
            peer.write_text(
                f"#!{sys.executable}\n"
                "import json,sys\n"
                "def send(value):\n"
                " data=json.dumps(value).encode()\n"
                " sys.stdout.buffer.write(len(data).to_bytes(4,'little')+data)\n"
                " sys.stdout.buffer.flush()\n"
                "assert sys.stdin.buffer.read(1)==b'\\x01'\n"
                "while size:=sys.stdin.buffer.read(4):\n"
                " request=json.loads(sys.stdin.buffer.read(int.from_bytes(size,'little')))\n"
                f" if request['op']=='start': result={start_result!r}\n"
                " else:\n"
                f"  send({{'event':'status','data':{status!r}}})\n"
                f"  result={activation!r}\n"
                " send({'reply':request['id'],'data':result})\n"
            )
            peer.chmod(0o700)

            class Probe(lifecycle.Check):
                async def lifecycle(self):
                    self.begin("activate-index")
                    await self.start()
                    await self.activate("a" * 32, [lifecycle.INDEX])

            check = Probe(root / "package", root / "cache", root)
            if current:
                components = check.data / "components"
                components.mkdir(parents=True)
                (components / "current").symlink_to("a" * 32)
            self.assertEqual(1, asyncio.run(check.run()))
            return json.loads((root / "result.json").read_text())

    def test_start_refusal_is_distinct_from_activation(self):
        report = self.run_peer(start_result=False)
        self.assertEqual("host-start", report["failureContext"]["modelPhase"])
        self.assertIn("host-start-refused", report["failure"])

    def test_activation_refusal_records_only_fixed_status_fields(self):
        report = self.run_peer(
            activation="unchanged",
            status={
                "modelReady": False,
                "modelFailureCode": "model-start-failed",
                "text": "secret",
            },
        )
        self.assertEqual("activation", report["failureContext"]["modelPhase"])
        self.assertEqual("unchanged", report["failureContext"]["activationOutcome"])
        self.assertEqual("model-start-failed", report["failureContext"]["modelFailureCode"])
        self.assertNotIn("secret", json.dumps(report))

    def test_confirmed_activation_with_wrong_current_identifies_generation(self):
        report = self.run_peer(current=False)
        self.assertEqual("generation", report["failureContext"]["modelPhase"])
        self.assertIn("activated-generation-mismatch", report["failure"])

    def test_ready_with_wrong_packages_is_not_an_activation_failure(self):
        report = self.run_peer(status={"modelReady": True, "modelPackages": [{"id": "secret"}]})
        self.assertEqual("ready", report["failureContext"]["modelPhase"])
        self.assertIn("ready-packages-mismatch", report["failure"])
        self.assertNotIn("secret", json.dumps(report))

    def test_unknown_failure_code_and_activation_body_are_not_echoed(self):
        report = self.run_peer(
            activation={"private": "secret"},
            status={"modelReady": False, "modelFailureCode": "secret" * 1000},
        )
        context = report["failureContext"]
        self.assertEqual("unrecognized", context["activationOutcome"])
        self.assertEqual("unrecognized", context["modelFailureCode"])
        self.assertNotIn("secret", json.dumps(report))
        self.assertLess(len(json.dumps(context)), 1024)


if __name__ == "__main__":
    unittest.main()
