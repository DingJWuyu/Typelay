"""Archive preparation safety; small local tar files, no network or native loads."""

import hashlib
import io
import json
import runpy
import sys
import tarfile
import tempfile
import types
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from hy_package_e2e import disk_budget, hy_assets, prepare_cache  # noqa: E402

from eng.linux.build import package_worker  # noqa: E402
from eng.linux.hy.prepare_runtime import extract_runtime, prepare  # noqa: E402


class RuntimePreparationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.archive = self.root / "runtime.tar.gz"
        self.output = self.root / "cpu"
        self.payload = b"fixed runtime fixture bytes"
        self.records = [
            {
                "file": "libllama.so.0",
                "bytes": len(self.payload),
                "sha256": hashlib.sha256(self.payload).hexdigest(),
            }
        ]

    def archive_with(self, *, target="libllama.so.0.0.0", payload=None, duplicate=False):
        with tarfile.open(self.archive, "w:gz") as archive:
            entry = tarfile.TarInfo("llama-b11717/libllama.so.0.0.0")
            content = self.payload if payload is None else payload
            entry.size = len(content)
            archive.addfile(entry, io.BytesIO(content))
            link = tarfile.TarInfo("llama-b11717/libllama.so.0")
            link.type, link.linkname = tarfile.SYMTYPE, target
            archive.addfile(link)
            if duplicate:
                archive.addfile(link)

    def test_cache_only_verifies_real_files_and_never_downloads_missing_or_corrupt_assets(self):
        # Synthetic hashed files exercise the real disk verifier, not native loading
        # or model quality. No production lock in the checkout is modified.
        package = self.root / "package"
        package_worker(package)
        assets = hy_assets(package)
        native = package / "worker/native/hy"
        native.mkdir(parents=True, exist_ok=True)

        def record(path, content=b"synthetic verification bytes"):
            path.write_bytes(content)
            return {
                "file": path.name,
                "bytes": len(content),
                "sha256": hashlib.sha256(content).hexdigest(),
            }

        fixed = json.loads((native / "model.lock.json").read_text())
        cache = self.root / "cache"
        folder = cache / "typelay/components" / fixed["package_id"] / fixed["version"]
        folder.mkdir(parents=True)
        for kind in ("model", "model_license"):
            fixed[kind].update(record(folder / fixed[kind]["file"]))
        (native / "model.lock.json").write_text(json.dumps(fixed))
        cpu = json.loads((native / "cpu-runtime.lock.json").read_text())
        (native / "cpu-runtime").mkdir()
        for item in cpu["files"]:
            item.update(record(native / "cpu-runtime" / item["file"]))
        (native / "cpu-runtime.lock.json").write_text(json.dumps(cpu))
        (native / "production-adapter.json").write_text(
            json.dumps(
                {
                    "schema": 1,
                    "abi": assets.ABI,
                    "platform": "linux-x86_64",
                    "exports": assets.EXPORTS,
                    "upstream_revision": assets.UPSTREAM,
                    "model_lock_sha256": assets.digest(native / "model.lock.json"),
                    "cpu_runtime_lock_sha256": assets.digest(native / "cpu-runtime.lock.json"),
                    "adapter": record(native / "libtypelay_hy.so"),
                    "license": record(native / "LICENSE.llama.cpp"),
                }
            )
        )
        model = folder / fixed["model"]["file"]
        original = model.read_bytes()
        with patch(
            "hy_package_e2e.download_resource", side_effect=AssertionError("network forbidden")
        ) as download:
            self.assertEqual(prepare_cache(package, cache, cache_only=True)["status"], "verified")
            model.unlink()
            with self.assertRaisesRegex(FileNotFoundError, "existing Hy cache"):
                prepare_cache(package, cache, cache_only=True)
            self.assertFalse(model.exists())
            model.write_bytes(b"bad")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                prepare_cache(package, cache, cache_only=True)
            model.unlink()
            target = self.root / "other-model"
            target.write_bytes(original)
            model.symlink_to(target)
            with self.assertRaisesRegex(ValueError, "linked"):
                prepare_cache(package, cache, cache_only=True)
            download.assert_not_called()

    def test_lifecycle_disk_budget_reserves_all_generations_and_cache(self):
        package = self.root / "package"
        package_worker(package)
        data = self.root / "data"
        generation = data / "typelay/components/original"
        (generation / "runtime").mkdir(parents=True)
        (generation / "runtime/python").write_bytes(b"abc")
        (generation / "models").mkdir()
        (generation / "models/data").write_bytes(b"de")
        (generation.parent / "current").symlink_to("original")
        self.assertEqual(
            disk_budget(package, data),
            {
                "currentGenerationBytes": 5,
                "modelBytes": 1133092087,
                "requiredDesktopFreeBytes": 6679852021,
                "requiredCacheAndDesktopFreeBytes": 7812944108,
            },
        )

    def test_explicit_quality_admission_reserves_capture_cleanup_and_workflow_tail(self):
        package = self.root / "package"
        package_worker(package)
        data = self.root / "data"
        generation = data / "typelay/components/original"
        generation.mkdir(parents=True)
        (generation / "runtime").write_bytes(b"abc")
        (generation.parent / "current").symlink_to("original")
        for quality, remaining, free, admitted in (
            (False, 719, 16 * 1024**3, False),
            (False, 720, 16 * 1024**3, True),
            (True, 2759, 16 * 1024**3, False),
            (True, 2760, 16 * 1024**3, True),
            (True, 2760, 0, False),
        ):
            with self.subTest(quality=quality, remaining=remaining, free=free):
                command = [
                    str(ROOT / "tests/linux/hy_package_e2e.py"),
                    "--check-budget",
                    "--package",
                    str(package),
                    "--data",
                    str(data),
                    "--remaining-seconds",
                    str(remaining),
                ]
                if quality:
                    command.append("--collect-translation-quality")
                output = io.StringIO()
                exit_code = 0
                with (
                    patch.object(sys, "argv", command),
                    patch("shutil.disk_usage", return_value=types.SimpleNamespace(free=free)),
                    redirect_stdout(output),
                ):
                    try:
                        runpy.run_path(command[0], run_name="__main__")
                    except SystemExit as error:
                        exit_code = error.code
                report = json.loads(output.getvalue())
                self.assertEqual(report["admitted"], admitted)
                self.assertEqual(report["requiredSeconds"], 2760 if quality else 720)
                self.assertEqual(exit_code == 0, admitted)
                if quality:
                    self.assertEqual(report["captureSeconds"], 2091)
                    self.assertEqual(report["captureStepOverheadSeconds"], 69)
                    self.assertEqual(report["preparationSeconds"], 420)
                    self.assertEqual(report["artifactSeconds"], 180)

    def test_quality_budget_flag_cannot_prepare_or_download_assets(self):
        command = [
            str(ROOT / "tests/linux/hy_package_e2e.py"),
            "--prepare-cache",
            "--collect-translation-quality",
            "--package",
            str(self.root / "absent-package"),
            "--cache",
            str(self.root / "absent-cache"),
        ]
        error = io.StringIO()
        with (
            patch.object(sys, "argv", command),
            redirect_stderr(error),
            self.assertRaises(SystemExit) as stopped,
        ):
            runpy.run_path(command[0], run_name="__main__")
        self.assertEqual(stopped.exception.code, 2)
        self.assertIn("only valid with --check-budget", error.getvalue())
        self.assertEqual(list(self.root.iterdir()), [])

    def test_offline_cache_miss_does_not_create_runtime(self):
        with self.assertRaises(FileNotFoundError):
            prepare(self.root / "cache", self.output, offline=True)
        self.assertFalse(self.output.exists())

    def test_corrupt_fixed_cache_fails_without_network_replacement(self):
        cache = self.root / "cache"
        cache.mkdir()
        archive = cache / "llama-b11717-bin-ubuntu-x64.tar.gz"
        archive.write_bytes(b"invalid cached archive")
        with self.assertRaisesRegex(ValueError, "full hash mismatch"):
            prepare(cache, self.output)
        self.assertEqual(archive.read_bytes(), b"invalid cached archive")
        self.assertFalse(self.output.exists())

    def test_verified_target_is_copied_as_regular_file_without_tools(self):
        self.archive_with()
        extract_runtime(self.archive, self.output, self.records)
        self.assertEqual(list(self.output.iterdir()), [self.output / "libllama.so.0"])
        self.assertFalse((self.output / "libllama.so.0").is_symlink())
        self.assertEqual((self.output / "libllama.so.0").read_bytes(), self.payload)

    def test_escape_link_does_not_write_outside_or_publish_runtime(self):
        self.archive_with(target="../../outside")
        with self.assertRaises(ValueError):
            extract_runtime(self.archive, self.output, self.records)
        self.assertFalse(self.output.exists())
        self.assertFalse((self.root / "outside").exists())

    def test_bad_bytes_and_duplicate_names_do_not_publish_runtime(self):
        for arguments in ({"payload": b"changed"}, {"duplicate": True}):
            with self.subTest(arguments=arguments):
                self.archive_with(**arguments)
                with self.assertRaises(ValueError):
                    extract_runtime(self.archive, self.output, self.records)
                self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
