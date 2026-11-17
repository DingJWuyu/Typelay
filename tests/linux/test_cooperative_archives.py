"""Raw archive supply contracts; compilation is verified by the offline build recipe."""

import io
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from eng.linux.cooperative import fcitx, qt, third_party  # noqa: E402
from eng.linux.cooperative.archives import download  # noqa: E402

BUILDERS = (
    (
        fcitx.build,
        (third_party.CORE_VERSION,),
        "fcitx5-c7ecdb931d8b378ccdcd87382a3ef7ff0bd10def.tar.gz",
    ),
    (third_party.build, (), "fcitx5-c7ecdb931d8b378ccdcd87382a3ef7ff0bd10def.tar.gz"),
    (qt.build, (), "fcitx5-qt.tar.gz"),
)


class LocalBuildTests(unittest.TestCase):
    def test_missing_archive_reports_cache_path_without_network(self):
        for builder, args, name in BUILDERS:
            with self.subTest(builder=builder.__module__), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                cache = root / "cache"
                cache.mkdir()
                with patch("urllib.request.urlopen", side_effect=AssertionError("network")):
                    with self.assertRaises(FileNotFoundError) as error:
                        builder(root / "output", *args, archive_cache=cache)
                self.assertIn(str(cache / name), str(error.exception))
                self.assertEqual(list(cache.iterdir()), [])

    def test_cache_cannot_overlap_output_even_through_symlinks(self):
        for builder, args, _ in BUILDERS:
            for layout in ("same", "child", "parent", "symlink"):
                with self.subTest(builder=builder.__module__, layout=layout):
                    with tempfile.TemporaryDirectory() as tmp:
                        cache = Path(tmp) / "cache"
                        cache.mkdir()
                        sentinel = cache / "build-inputs.json"
                        sentinel.write_text("must survive")
                        out = {
                            "same": cache,
                            "child": cache / "output",
                            "parent": cache.parent,
                            "symlink": cache.parent / "alias",
                        }[layout]
                        if layout == "symlink":
                            out.symlink_to(cache, target_is_directory=True)
                        with self.assertRaisesRegex(ValueError, "separate"):
                            builder(out, *args, archive_cache=cache)
                        self.assertEqual(sentinel.read_text(), "must survive")

    def test_corrupt_cache_does_not_use_existing_output_or_network(self):
        for builder, args, name in BUILDERS:
            with self.subTest(builder=builder.__module__), tempfile.TemporaryDirectory() as tmp:
                cache, out = Path(tmp) / "cache", Path(tmp) / "output"
                cache.mkdir()
                out.mkdir()
                (cache / name).write_bytes(b"corrupt cache")
                (out / name).write_bytes(b"existing output")
                with patch("urllib.request.urlopen", side_effect=AssertionError("network")):
                    with self.assertRaisesRegex(RuntimeError, "checksum mismatch") as error:
                        builder(out, *args, archive_cache=cache)
                self.assertIn(str(cache / name), str(error.exception))
                self.assertEqual((cache / name).read_bytes(), b"corrupt cache")
                self.assertEqual((out / name).read_bytes(), b"existing output")

    def test_local_archives_cannot_be_combined_with_binary_reuse(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(ValueError, "reuse"):
                fcitx.build(
                    Path(tmp) / "output",
                    third_party.CORE_VERSION,
                    reuse_third_party=True,
                    archive_cache=Path(tmp) / "cache",
                )


class ArchiveSupplyTests(unittest.TestCase):
    DIGEST = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

    def test_local_hit_copies_verified_bytes_and_leaves_cache_unchanged(self):
        with tempfile.TemporaryDirectory() as tmp:
            cache, out = Path(tmp) / "cache", Path(tmp) / "out"
            cache.mkdir()
            out.mkdir()
            source, destination = cache / "sample.tar.gz", out / "sample.tar.gz"
            source.write_bytes(b"abc")
            source.chmod(0o444)
            destination.write_bytes(b"stale output")
            before = source.stat()
            with patch("urllib.request.urlopen", side_effect=AssertionError("network")):
                download("https://invalid.test/unused", destination, self.DIGEST, cache)
            self.assertEqual(destination.read_bytes(), b"abc")
            self.assertEqual(source.read_bytes(), b"abc")
            self.assertEqual(source.stat().st_mtime_ns, before.st_mtime_ns)
            destination.chmod(0o644)
            destination.write_bytes(b"output can change independently")
            self.assertEqual(source.read_bytes(), b"abc")

    def test_missing_or_unknown_archive_never_uses_existing_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            cache, out = Path(tmp) / "cache", Path(tmp) / "out"
            cache.mkdir()
            out.mkdir()
            destination = out / "unknown.tar.gz"
            destination.write_bytes(b"abc")
            with patch("urllib.request.urlopen", side_effect=AssertionError("network")):
                with self.assertRaises(FileNotFoundError):
                    download("https://invalid.test/unknown", destination, self.DIGEST, cache)

    def test_default_mode_still_reuses_and_checks_output_archive(self):
        with tempfile.TemporaryDirectory() as tmp:
            destination = Path(tmp) / "source.tar.gz"
            destination.write_bytes(b"abc")
            with patch("urllib.request.urlopen", side_effect=AssertionError("network")):
                download("https://invalid.test/source", destination, self.DIGEST)
                destination.write_bytes(b"corrupt")
                with self.assertRaisesRegex(RuntimeError, "checksum mismatch"):
                    download("https://invalid.test/source", destination, self.DIGEST)

    def test_default_mode_downloads_and_rejects_untrusted_bytes(self):
        for content in (b"abc", b"bad response"):
            with self.subTest(content=content), tempfile.TemporaryDirectory() as tmp:
                destination = Path(tmp) / "source.tar.gz"
                with patch("urllib.request.urlopen", return_value=io.BytesIO(content)) as request:
                    if content == b"abc":
                        download("https://upstream.test/source", destination, self.DIGEST)
                        self.assertEqual(destination.read_bytes(), b"abc")
                    else:
                        with self.assertRaisesRegex(RuntimeError, "checksum mismatch"):
                            download("https://upstream.test/source", destination, self.DIGEST)
                    request.assert_called_once_with("https://upstream.test/source", timeout=180)


class CommandLineTests(unittest.TestCase):
    def test_each_command_accepts_same_cache_option_and_reports_missing_or_corrupt_file(self):
        for module, output_flag, name in (
            ("fcitx", "--output", BUILDERS[0][2]),
            ("third_party", "--output", BUILDERS[1][2]),
            ("qt", "--out", BUILDERS[2][2]),
        ):
            for corrupt in (False, True):
                with (
                    self.subTest(module=module, corrupt=corrupt),
                    tempfile.TemporaryDirectory() as tmp,
                ):
                    cache = Path(tmp) / "cache"
                    cache.mkdir()
                    if corrupt:
                        (cache / name).write_bytes(b"bad archive")
                    result = subprocess.run(
                        [
                            sys.executable,
                            str(ROOT / f"eng/linux/cooperative/{module}.py"),
                            output_flag,
                            str(Path(tmp) / "out"),
                            "--archive-cache",
                            str(cache),
                        ],
                        text=True,
                        capture_output=True,
                        timeout=15,
                    )
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn(str(cache / name), result.stderr)
                    self.assertIn(
                        "checksum mismatch" if corrupt else "archive missing", result.stderr
                    )


if __name__ == "__main__":
    unittest.main()
