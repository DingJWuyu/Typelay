"""Opt-in real Index ELF/relocation/refusal tests, without GGUF or inference."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class IndexRuntimeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        supplied = os.environ.get("TYPELAY_INDEX_CPU_RUNTIME")
        if not supplied:
            raise unittest.SkipTest("explicit fixed Index CPU runtime required; no downloads")
        # Exercise the normal package filesystem, including Btrfs subvolumes,
        # rather than silently moving the actual ELF boundary onto /tmp tmpfs.
        (ROOT / "artifacts").mkdir(exist_ok=True)
        cls.temporary = tempfile.TemporaryDirectory(prefix="typelay-index-", dir=ROOT / "artifacts")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.directory = Path(cls.temporary.name)
        cls.worker = cls.directory / "搬迁 后的 worker"
        shutil.copytree(
            ROOT / "src/shared/TypeLay.Worker/typelay_worker",
            cls.worker / "typelay_worker",
            ignore=shutil.ignore_patterns("__pycache__"),
        )
        subprocess.run(
            [
                sys.executable,
                str(ROOT / "eng/linux/index/build.py"),
                "--runtime",
                supplied,
                "--output",
                str(cls.worker / "native/index"),
            ],
            check=True,
            capture_output=True,
            timeout=120,
        )

    def run_isolated(self, code):
        return subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                "-c",
                "import sys; sys.path.insert(0,sys.argv[1]); " + textwrap.dedent(code),
                str(self.worker),
            ],
            capture_output=True,
            text=True,
            timeout=30,
        )

    def test_private_relocated_cpu_closure_has_only_index_abi(self):
        result = self.run_isolated(
            "import ctypes,json; from typelay_worker.index_assets import verify_native; "
            "from typelay_worker.index_cpu import LinuxRuntime; "
            "loader=LinuxRuntime(verify_native()); selected=loader.select_cpu_backend(); "
            "adapter=loader.load_adapter(); "
            "assert hasattr(adapter,'index_open_v1') and hasattr(adapter,'index_translate_v1'); "
            "assert not hasattr(adapter,'hy_open_v5'); cpu=ctypes.CDLL(str(selected)); "
            "print(json.dumps(loader.verify_loaded(selected))); loader.close()"
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(json.loads(result.stdout)), 5)

    def test_invalid_native_requirements_do_not_publish_partial_output(self):
        result = self.run_isolated("""
            import ctypes
            from typelay_worker.index_assets import verify_native
            from typelay_worker.index_cpu import LinuxRuntime
            from typelay_worker.index import Metrics, CANCEL, Term
            loader = LinuxRuntime(verify_native())
            loader.select_cpu_backend()
            lib = loader.load_adapter()
            call = lib.index_translate_v1
            call.argtypes = [
                ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32,
                ctypes.POINTER(Term), ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32,
                ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32),
                ctypes.POINTER(Metrics), CANCEL, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32,
            ]
            call.restype = ctypes.c_int32
            out, error = ctypes.create_string_buffer(8192), ctypes.create_string_buffer(512)
            cancel = CANCEL(lambda _: 0)
            for direction, style, flags in [(0,0,0), (15,0,0), (1,3,0), (1,0,8)]:
                m, n = Metrics(), ctypes.c_uint32(123)
                code = call(None, direction, style, flags, None, 0, b"source", 6, out, 8192,
                            ctypes.byref(n), ctypes.byref(m), cancel, None, error, 512)
                assert code == 1 and n.value == 0
            loader.close()
        """)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_foreign_preloaded_same_name_library_is_refused(self):
        result = self.run_isolated("""
            import ctypes, shutil
            from pathlib import Path
            from typelay_worker.index_assets import verify_native
            from typelay_worker.index_cpu import LinuxRuntime
            verified = verify_native()
            source = Path(verified['cpu_runtime']['directory']) / 'libggml-base.so.0'
            foreign = Path(sys.argv[1]) / 'foreign' / source.name
            foreign.parent.mkdir(exist_ok=True)
            shutil.copyfile(source, foreign)
            library = ctypes.CDLL(str(foreign))
            LinuxRuntime(verified)
        """)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("loaded before the verified loader", result.stderr)

    def test_changed_digest_is_refused_even_after_manifest_verification(self):
        result = self.run_isolated("""
            from pathlib import Path
            from typelay_worker.index_assets import verify_native
            from typelay_worker.index_cpu import LinuxRuntime
            verified = verify_native()
            target = Path(verified['cpu_runtime']['directory']) / 'libggml-base.so.0'
            original = target.read_bytes()
            try:
                target.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
                LinuxRuntime(verified)
            finally:
                target.write_bytes(original)
        """)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("fixed asset full hash mismatch", result.stderr)

    def test_same_bytes_replacing_a_mapped_file_are_refused(self):
        result = self.run_isolated("""
            import ctypes
            from pathlib import Path
            from typelay_worker.index_assets import verify_native
            from typelay_worker.index_cpu import LinuxRuntime
            loader = LinuxRuntime(verify_native())
            selected = loader.select_cpu_backend()
            loader.load_adapter()
            library = ctypes.CDLL(str(selected))
            original = selected.read_bytes()
            selected.unlink()
            selected.write_bytes(original)
            try:
                loader.verify_loaded(selected)
            finally:
                loader.close()
        """)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("deleted or anonymous runtime library mapping", result.stderr)

    def test_identity_probe_failure_releases_temporary_mapping_and_descriptors(self):
        result = self.run_isolated("""
            from pathlib import Path
            from unittest.mock import patch
            from typelay_worker.index_assets import verify_native
            from typelay_worker.index_cpu import LinuxRuntime
            verified = verify_native()
            before = len(list(Path('/proc/self/fd').iterdir()))
            original_open = Path.open
            def has_probe(data):
                return any('rw-p 00000000' in row and 'libggml-base' in row
                           for row in data.splitlines())
            def read_maps(path, *args, **kwargs):
                stream = original_open(path, *args, **kwargs)
                if path == Path('/proc/self/maps'):
                    data = stream.read()
                    stream.seek(0)
                    if has_probe(data):
                        stream.close()
                        raise PermissionError('denied with the verification mapping open')
                return stream
            with patch.object(Path, 'open', read_maps):
                try:
                    LinuxRuntime(verified)
                except PermissionError:
                    pass
                else:
                    raise AssertionError('mapping failure must reject the runtime')
            assert not has_probe(Path('/proc/self/maps').read_text())
            assert len(list(Path('/proc/self/fd').iterdir())) == before
        """)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_successful_identity_checks_release_temporary_mapping_and_descriptors(self):
        result = self.run_isolated("""
            from pathlib import Path
            from typelay_worker.index_assets import verify_native
            from typelay_worker.index_cpu import LinuxRuntime
            before = len(list(Path('/proc/self/fd').iterdir()))
            loader = LinuxRuntime(verify_native())
            loader.select_cpu_backend()
            loader.load_adapter()
            loader.close()
            assert not any('rw-p 00000000' in row and 'libggml' in row
                           for row in Path('/proc/self/maps').read_text().splitlines())
            assert len(list(Path('/proc/self/fd').iterdir())) == before
        """)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_alternative_export_refuses_missing_previous_without_partial_output(self):
        result = self.run_isolated("""
            import ctypes
            from typelay_worker.index_assets import verify_native
            from typelay_worker.index_cpu import LinuxRuntime
            from typelay_worker.index import Metrics, CANCEL
            loader = LinuxRuntime(verify_native())
            loader.select_cpu_backend()
            call = loader.load_adapter().index_translate_alternative_v1
            call.argtypes = [ctypes.c_void_p, ctypes.c_uint32,
                ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32,
                ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32),
                ctypes.POINTER(Metrics), CANCEL, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32]
            call.restype = ctypes.c_int32
            out, error = ctypes.create_string_buffer(8192), ctypes.create_string_buffer(512)
            cancel = CANCEL(lambda _: 1)
            for previous, length in [(None, 0), (b"", 0), (None, 1)]:
                n, metrics = ctypes.c_uint32(123), Metrics()
                code = call(None, 13, b"source", 6, previous, length, out, 8192,
                    ctypes.byref(n), ctypes.byref(metrics), cancel, None, error, 512)
                assert code == 1 and n.value == 0
                assert error.value == b"previous candidate required"
            loader.close()
        """)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_explicit_legacy_production_payload_keeps_only_original_capabilities(self):
        legacy = os.environ.get("TYPELAY_INDEX_LEGACY_NATIVE")
        if not legacy:
            self.skipTest("explicit original three-export production payload required")
        legacy_worker = self.directory / "legacy worker"
        shutil.copytree(self.worker / "typelay_worker", legacy_worker / "typelay_worker")
        shutil.copytree(legacy, legacy_worker / "native/index")
        result = subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                "-c",
                textwrap.dedent("""
            import sys
            sys.path.insert(0, sys.argv[1])
            from typelay_worker.index_assets import verify_native
            from typelay_worker.index_cpu import LinuxRuntime
            manifest = verify_native()
            assert not manifest.get("alternative", False)
            loader = LinuxRuntime(manifest)
            loader.select_cpu_backend()
            lib = loader.load_adapter()
            assert hasattr(lib, "index_translate_v1")
            assert not hasattr(lib, "index_translate_alternative_v1")
            loader.close()
        """),
                str(legacy_worker),
            ],
            capture_output=True,
            text=True,
            timeout=30,
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_resource_limits_apply_only_inside_the_owned_process(self):
        result = self.run_isolated(
            "import os,resource,json; from typelay_worker.index import limit_linux_process; "
            "before=sorted(os.sched_getaffinity(0)); limit_linux_process(); "
            "after=sorted(os.sched_getaffinity(0)); limits=resource.getrlimit(resource.RLIMIT_AS); "
            "assert after==before[:2] and 0<limits[0]<=limits[1]<=2684354560; "
            "print(json.dumps({'affinity':after,'addressSpace':limits}))"
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertLessEqual(len(json.loads(result.stdout)["affinity"]), 2)


if __name__ == "__main__":
    unittest.main()
