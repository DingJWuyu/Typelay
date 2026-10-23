"""Local ZIP/build-input safety contracts. No downloads, compiler or models."""

import hashlib
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from eng.windows.hy.build import build, production_exports, verify_sources  # noqa: E402
from eng.windows.hy.generate_locks import generate  # noqa: E402
from eng.windows.hy.prepare_assets import Preparation  # noqa: E402
from eng.windows.hy.prepare_runtime import extract_runtime, prepare  # noqa: E402


class WindowsPreparationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.archive = self.root / "runtime.zip"
        self.output = self.root / "搬迁 cpu"
        self.payload = b"bounded CPU ZIP fixture bytes"
        self.records = [
            {
                "file": "llama.dll",
                "bytes": len(self.payload),
                "sha256": hashlib.sha256(self.payload).hexdigest(),
            }
        ]
        network = patch("urllib.request.urlopen", side_effect=AssertionError("network prohibited"))
        network.start()
        self.addCleanup(network.stop)

    def archive_with(self, extra=(), *, payload=None):
        with zipfile.ZipFile(self.archive, "w") as archive:
            archive.writestr("llama.dll", self.payload if payload is None else payload)
            for entry in extra:
                archive.writestr(entry, b"unselected content")

    def test_only_allowlisted_files_are_copied_without_cli_gpu_rpc_or_model(self):
        self.archive_with(("llama-cli.exe", "ggml-rpc.dll", "ggml-vulkan.dll", "model.gguf"))
        extract_runtime(self.archive, self.output, self.records)
        self.assertEqual([p.name for p in self.output.iterdir()], ["llama.dll"])
        self.assertEqual((self.output / "llama.dll").read_bytes(), self.payload)

    def test_ambiguous_unsafe_and_link_entries_do_not_publish_runtime(self):
        link = zipfile.ZipInfo("elsewhere")
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        for entry in ("LLAMA.DLL", "../escape", "sub/file", "C:drive", "CON", link):
            with self.subTest(entry=entry):
                self.archive_with((entry,))
                with self.assertRaises(ValueError):
                    extract_runtime(self.archive, self.output, self.records)
                self.assertFalse(self.output.exists())
                self.assertFalse((self.root / "escape").exists())

    def test_corrupt_or_missing_selected_member_does_not_publish_runtime(self):
        for payload in (b"changed", b"X" * len(self.payload)):
            self.archive_with(payload=payload)
            with self.assertRaises(ValueError):
                extract_runtime(self.archive, self.output, self.records)
            self.assertFalse(self.output.exists())
        with zipfile.ZipFile(self.archive, "w") as archive:
            archive.writestr("other.dll", self.payload)
        with self.assertRaises(ValueError):
            extract_runtime(self.archive, self.output, self.records)
        self.assertFalse(self.output.exists())

    def test_existing_output_is_never_overwritten(self):
        self.archive_with()
        self.output.mkdir()
        marker = self.output / "owned"
        marker.write_bytes(b"keep")
        with self.assertRaises(ValueError):
            extract_runtime(self.archive, self.output, self.records)
        self.assertEqual(marker.read_bytes(), b"keep")

    def test_offline_cache_miss_does_not_fetch_or_publish(self):
        with self.assertRaises(FileNotFoundError):
            prepare(self.root / "cache", self.output, offline=True)
        self.assertFalse(self.output.exists())

    def test_corrupt_cache_and_model_cache_are_not_silently_repaired(self):
        cache = self.root / "cache"
        cache.mkdir()
        archive = cache / "llama-b11717-bin-win-cpu-x64.zip"
        archive.write_bytes(b"invalid cached archive")
        with self.assertRaisesRegex(ValueError, "full hash mismatch"):
            prepare(cache, self.output)
        self.assertEqual(archive.read_bytes(), b"invalid cached archive")
        (cache / "Hy-MT2-1.8B-Q4_K_M.gguf").write_bytes(b"never open a model")
        with self.assertRaisesRegex(ValueError, "unexpected"):
            prepare(cache, self.output)
        self.assertFalse(self.output.exists())

    def test_isolated_fixture_resolves_the_shared_loader_before_native_preparation(self):
        result = subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                str(ROOT / "tests/windows/fixtures/TypeLay.NativeFixture/HyAdapter/hy_worker.py"),
                "--help",
            ],
            cwd=self.root,
            capture_output=True,
            text=True,
            timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        for option in ("--assets", "--runtime", "--adapter"):
            self.assertIn(option, result.stdout)

    def test_product_asset_preparation_rejects_unexpected_cache_without_sdk_or_network(self):
        artifacts = self.root / "artifacts"
        artifacts.mkdir()
        cache = artifacts / "hy-download"
        cache.mkdir()
        (cache / "unexpected.partial").write_bytes(b"keep")
        with self.assertRaisesRegex(ValueError, "unexpected file"):
            Preparation(artifacts, 60).run(assets_only=True)
        self.assertEqual((cache / "unexpected.partial").read_bytes(), b"keep")
        self.assertFalse((artifacts / "hy-build/typelay_hy.dll").exists())

    def test_source_pins_and_generated_locks_are_current(self):
        generate(check=True)
        verify_sources()

    def test_exact_production_exports_reject_audit_old_missing_duplicate_and_extra(self):
        expected = [
            "hy_close",
            "hy_open_v5",
            "hy_translate",
            "hy_translate_english_v1",
            "hy_translate_glossary_v5",
            "hy_translate_styled_glossary_v5",
            "hy_translate_styled_v5",
        ]

        def listing(names):
            return "Dump of file C:/搬迁 包/typelay_hy.dll\r\n".encode() + b"\r\n".join(
                f" {index} {index:X} 00001000 {name}".encode()
                for index, name in enumerate(names, 1)
            )

        self.assertEqual(production_exports(listing(expected)), expected)
        with self.assertRaises(ValueError):
            production_exports(listing(expected) + b"\r\n 6 5 hy_audit = foreign.hy_audit")
        for names in (
            [*expected, "hy_audit"],
            [n.replace("open_v5", "open_v4") for n in expected],
            expected[:-1],
            [*expected, "hy_close"],
            [*expected, "foreign"],
        ):
            with self.assertRaises(ValueError):
                production_exports(listing(names))

    def test_non_windows_build_fails_before_creating_output(self):
        with patch("platform.system", return_value="Linux"):
            with self.assertRaisesRegex(RuntimeError, "requires Windows x64"):
                build(self.root / "absent", self.output)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
