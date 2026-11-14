"""Verification consumes private cache copies and cannot reach the owner's network."""

import importlib.util
import json
import os
import socket
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".agents/skills/verify-typelay/scripts/private-session.py"
SPEC = importlib.util.spec_from_file_location("verification_session", SCRIPT)
session = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(session)


class VerificationResourcesTests(unittest.TestCase):
    def test_cache_copy_keeps_bytes_and_relative_links_but_isolates_writes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, target = root / "source", root / "scratch/cache"
            (source / "uv/archive").mkdir(parents=True)
            (source / "uv/archive/wheel").write_bytes(b"cached wheel")
            (source / "uv/wheel").symlink_to("archive/wheel")
            (source / "model").write_bytes(b"cached model")
            session.copy_cache(source, target)
            self.assertEqual((target / "uv/wheel").read_bytes(), b"cached wheel")
            self.assertEqual(os.readlink(target / "uv/wheel"), "archive/wheel")
            (target / "uv/wheel").write_bytes(b"private mutation")
            (target / "model").unlink()
            self.assertEqual((source / "uv/wheel").read_bytes(), b"cached wheel")
            self.assertEqual((source / "model").read_bytes(), b"cached model")
            with self.assertRaises(FileNotFoundError):
                session.copy_cache(root / "missing", root / "unexpected")
            self.assertFalse((root / "unexpected").exists())

    def test_cleanup_preserves_evidence_and_respects_explicit_retention_and_live_children(self):
        for keep, remaining in ((False, []), (True, []), (False, [123])):
            with (
                self.subTest(keep=keep, remaining=remaining),
                tempfile.TemporaryDirectory() as directory,
            ):
                output = Path(directory)
                scratch, runtime = output / "scratch", output / "runtime"
                scratch.mkdir()
                runtime.mkdir()
                (scratch / "installation").write_bytes(b"installed environment")
                (output / "journey.log").write_text("failure evidence")
                session.remove_scratch(scratch, runtime, keep=keep, remaining=remaining)
                self.assertEqual(scratch.exists(), keep or bool(remaining))
                self.assertEqual(runtime.exists(), keep or bool(remaining))
                self.assertEqual((output / "journey.log").read_text(), "failure evidence")

    def test_network_namespace_blocks_host_and_external_routes_but_allows_local_fixtures(self):
        with tempfile.TemporaryDirectory() as directory, socket.socket() as owner:
            owner.bind(("127.0.0.1", 0))
            owner.listen()
            port = owner.getsockname()[1]
            probe = f"""
import socket
for address in [('127.0.0.1', {port}), ('192.0.2.1', 443)]:
    try:
        socket.create_connection(address, timeout=1)
    except OSError:
        pass
    else:
        raise AssertionError('escaped isolated network')
with socket.socket() as fixture:
    fixture.bind(('127.0.0.1', 0))
    fixture.listen()
    with socket.create_connection(fixture.getsockname(), timeout=1):
        pass
"""
            result = subprocess.run(
                [
                    "unshare",
                    "--user",
                    "--map-current-user",
                    "--keep-caps",
                    "--net",
                    sys.executable,
                    str(SCRIPT),
                    "--network-child",
                    "--output",
                    directory,
                    "--",
                    sys.executable,
                    "-c",
                    probe,
                ],
                env={**os.environ, "TYPELAY_VERIFY_PARENT_NETNS": os.readlink("/proc/self/ns/net")},
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            evidence = json.loads((Path(directory) / "network.json").read_text())
            self.assertEqual(evidence["interfaces"], ["lo"])
            self.assertNotEqual(evidence["namespace"], evidence["parentNamespace"])

    def test_network_child_refuses_host_namespace_before_running_command(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(
                [
                    sys.executable,
                    str(SCRIPT),
                    "--network-child",
                    "--output",
                    directory,
                    "--",
                    "/usr/bin/touch",
                    str(Path(directory) / "should-not-run"),
                ],
                env={**os.environ, "TYPELAY_VERIFY_PARENT_NETNS": os.readlink("/proc/self/ns/net")},
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse((Path(directory) / "should-not-run").exists())


if __name__ == "__main__":
    unittest.main()
