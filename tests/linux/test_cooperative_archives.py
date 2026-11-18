"""Raw archive supply contracts; compilation is verified by the offline build recipe."""

import hashlib
import io
import random
import subprocess
import sys
import tarfile
import tempfile
import unittest
from contextlib import ExitStack
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from eng.linux.cooperative import fcitx, qt, third_party, third_party_recipe  # noqa: E402
from eng.linux.cooperative.archives import download  # noqa: E402

BUILDERS = (
    (
        fcitx.build,
        (third_party_recipe.CORE_VERSION,),
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
            for layout in ("same", "child", "parent", "symlink", "source-alias"):
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
                            "source-alias": cache.parent / "out",
                        }[layout]
                        if layout == "symlink":
                            out.symlink_to(cache, target_is_directory=True)
                        if layout == "source-alias":
                            out.mkdir()
                            (out / "sources").symlink_to(cache, target_is_directory=True)
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
                    third_party_recipe.CORE_VERSION,
                    reuse_third_party=True,
                    archive_cache=Path(tmp) / "cache",
                )


class ArchiveSupplyTests(unittest.TestCase):
    DIGEST = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

    def test_copy_failure_preserves_cache_and_never_downloads(self):
        with tempfile.TemporaryDirectory() as tmp:
            cache, out = Path(tmp) / "cache", Path(tmp) / "out"
            cache.mkdir()
            out.mkdir()
            source = cache / "source.tar.gz"
            source.write_bytes(b"abc")
            with (
                patch("subprocess.run", side_effect=subprocess.CalledProcessError(1, "cp")),
                patch("urllib.request.urlopen", side_effect=AssertionError("network")),
            ):
                with self.assertRaises(subprocess.CalledProcessError):
                    download("https://invalid.test/source", out / source.name, self.DIGEST, cache)
            self.assertEqual(source.read_bytes(), b"abc")
            self.assertFalse((out / source.name).exists())

    def test_generated_archive_bytes_are_preserved_and_writes_are_isolated(self):
        random_bytes = random.Random(71)
        for length in (0, 1, 2, 255, 4096, 65537):
            with self.subTest(length=length), tempfile.TemporaryDirectory() as tmp:
                cache, out = Path(tmp) / "cache", Path(tmp) / "out"
                cache.mkdir()
                out.mkdir()
                content = random_bytes.randbytes(length)
                source, destination = cache / "source.tar.gz", out / "source.tar.gz"
                source.write_bytes(content)
                with patch("urllib.request.urlopen", side_effect=AssertionError("network")):
                    download(
                        "https://invalid.test/source",
                        destination,
                        hashlib.sha256(content).hexdigest(),
                        cache,
                    )
                self.assertEqual(destination.read_bytes(), content)
                destination.write_bytes(b"changed output")
                self.assertEqual(source.read_bytes(), content)

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


class CompilerReached(Exception):
    """Stop at the external compiler, after real archive extraction and patching."""


class PreparedSourcesTests(unittest.TestCase):
    """Synthetic upstream catalog, real supply/extraction/patching, no compiled-byte claim."""

    def setUp(self):
        self.context = ExitStack()
        self.addCleanup(self.context.close)
        self.root = Path(self.context.enter_context(tempfile.TemporaryDirectory()))
        self.cache = self.root / "cache"
        self.cache.mkdir()
        patches = self.root / "patches"
        patches.mkdir()
        self.expected = {}
        pins = {}
        for project, (commit, _) in third_party_recipe.FCITX_PINS.items():
            directories = [
                directory.removeprefix(project + "/")
                for directory, _, _ in (*third_party_recipe.DATA, third_party_recipe.SPELL)
                if directory.startswith(project + "/")
            ]
            if project == "libime":
                directories.append("src/libime/core/kenlm")
            name = f"{project}-{commit}.tar.gz"
            pins[project] = (commit, self.archive(name, f"{project}-{commit}", directories))
            self.expected[Path(name)] = (self.cache / name).read_bytes()
        data = []
        for directory, name, _ in (*third_party_recipe.DATA, third_party_recipe.SPELL):
            content = name.encode()
            (self.cache / name).write_bytes(content)
            data.append((directory, name, hashlib.sha256(content).hexdigest()))
            self.expected[Path("sources") / directory / name] = content
        for project in (*third_party_recipe.PATCH_PROJECTS, "fcitx5-qt"):
            (patches / f"{project}.patch").write_text(
                "--- a/marker\n+++ b/marker\n@@ -1 +1 @@\n-before\n+after\n"
            )
        qt_digest = self.archive("fcitx5-qt.tar.gz", f"fcitx5-qt-{qt.QT_PIN}", ["common"])
        self.context.enter_context(patch.dict(third_party_recipe.FCITX_PINS, pins, clear=True))
        self.context.enter_context(patch.object(third_party_recipe, "DATA", tuple(data[:-1])))
        self.context.enter_context(patch.object(third_party_recipe, "SPELL", data[-1]))
        self.context.enter_context(patch.object(third_party_recipe, "PATCHES", patches))
        self.context.enter_context(patch.object(qt, "PATCHES", patches))
        self.context.enter_context(patch.object(qt, "QT_SHA256", qt_digest))
        self.context.enter_context(
            patch("urllib.request.urlopen", side_effect=AssertionError("network fallback"))
        )
        real_run = subprocess.run

        def run(command, *args, **kwargs):
            if str(command[0]) == "cmake":
                raise CompilerReached()
            return real_run(command, *args, **kwargs)

        self.context.enter_context(patch("subprocess.run", side_effect=run))

    def archive(self, name, source_root, directories):
        path = self.cache / name
        with tarfile.open(path, "w:gz") as archive:
            for directory in ("", *directories):
                entry = tarfile.TarInfo(f"{source_root}/{directory}")
                entry.type = tarfile.DIRTYPE
                entry.mode = 0o755
                archive.addfile(entry)
            entry = tarfile.TarInfo(f"{source_root}/marker")
            entry.size = len(b"before\n")
            archive.addfile(entry, io.BytesIO(b"before\n"))
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def test_fcitx_and_third_party_prepare_all_sources_data_and_spell_from_cache(self):
        (self.cache / "fcitx5-qt.tar.gz").unlink()  # These builders do not need Qt.
        before = {p.name: p.read_bytes() for p in self.cache.iterdir()}
        for builder, args, _ in BUILDERS[:2]:
            with self.subTest(builder=builder.__module__):
                out = self.root / builder.__module__.rsplit(".", 1)[-1]
                with self.assertRaises(CompilerReached):
                    builder(out, *args, archive_cache=self.cache)
                for path, content in self.expected.items():
                    self.assertEqual((out / path).read_bytes(), content)
                for project in third_party_recipe.PATCH_PROJECTS:
                    self.assertEqual((out / "sources" / project / "marker").read_text(), "after\n")
                self.assertEqual({p.name: p.read_bytes() for p in self.cache.iterdir()}, before)

    def test_qt_prepares_its_only_archive_and_applies_patch_and_abi(self):
        for path in self.cache.iterdir():
            if path.name != "fcitx5-qt.tar.gz":
                path.unlink()
        before = (self.cache / "fcitx5-qt.tar.gz").read_bytes()
        out = self.root / "qt"
        with self.assertRaises(CompilerReached):
            qt.build(out, archive_cache=self.cache)
        self.assertEqual((out / "fcitx5-qt.tar.gz").read_bytes(), before)
        source = out / f"fcitx5-qt-{qt.QT_PIN}"
        self.assertEqual((source / "marker").read_text(), "after\n")
        self.assertEqual((source / "common" / qt.ABI.name).read_bytes(), qt.ABI.read_bytes())
        self.assertEqual((self.cache / "fcitx5-qt.tar.gz").read_bytes(), before)

    def test_every_later_fcitx_archive_is_required_and_checked_before_compilation(self):
        for name in sorted(p.name for p in self.cache.iterdir() if p.name != "fcitx5-qt.tar.gz"):
            path = self.cache / name
            original = path.read_bytes()
            for corrupt in (False, True):
                with self.subTest(name=name, corrupt=corrupt):
                    if corrupt:
                        path.write_bytes(b"bad archive")
                    else:
                        path.unlink()
                    out = self.root / f"{name}-{corrupt}"
                    error_type = RuntimeError if corrupt else FileNotFoundError
                    with self.assertRaises(error_type) as error:
                        fcitx.build(out, third_party_recipe.CORE_VERSION, archive_cache=self.cache)
                    self.assertIn(str(path), str(error.exception))
                    path.write_bytes(original)


if __name__ == "__main__":
    unittest.main()
