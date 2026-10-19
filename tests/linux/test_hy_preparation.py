"""Archive preparation safety; small local tar files, no network or native loads."""

import hashlib
import io
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from hy_package_e2e import disk_budget  # noqa: E402

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
