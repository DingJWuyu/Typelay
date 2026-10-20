"""Opt-in real ELF packaging/loader checks, never open a GGUF or run inference.

Set TYPELAY_HY_CPU_RUNTIME to an existing fixed 17-file b11717 CPU directory.
No library downloads or llama.cpp builds are performed by this suite.
"""

import hashlib
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
sys.path.insert(0, str(ROOT))
from eng.linux.build import package_worker  # noqa: E402


class HyRuntimeBundleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        supplied = os.environ.get("TYPELAY_HY_CPU_RUNTIME")
        if not supplied:
            raise unittest.SkipTest("explicit fixed CPU runtime required; no models or downloads")
        cls.temporary = tempfile.TemporaryDirectory(prefix="typelay-hy-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.directory = Path(cls.temporary.name)
        initial = cls.directory / "initial"
        package_worker(initial, Path(supplied))
        cls.package = cls.directory / "搬迁 后的包"
        initial.rename(cls.package)
        cls.worker = cls.package / "worker"
        cls.native = cls.worker / "native/hy"

    def run_worker(self, code, *, env=None):
        return subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                "-c",
                "import sys; sys.path.insert(0,sys.argv[1]); "
                "from typelay_worker.hy_assets import verify_native; "
                "from typelay_worker.hy_cpu import LinuxRuntime; " + code,
                str(self.worker),
            ],
            env=env,
            capture_output=True,
            text=True,
            timeout=30,
        )

    def test_relocated_real_elf_closure_loads_without_source_or_audit(self):
        lock = json.loads((self.native / "cpu-runtime.lock.json").read_text())
        expected = {
            "model.lock.json",
            "cpu-runtime.lock.json",
            "production-adapter.json",
            "libtypelay_hy.so",
            "LICENSE.llama.cpp",
            *("cpu-runtime/" + item["file"] for item in lock["files"]),
        }
        self.assertEqual(
            {
                path.relative_to(self.native).as_posix()
                for path in self.native.rglob("*")
                if path.is_file()
            },
            expected,
        )
        # Explicitly map the chosen CPU to exercise the post-open closure check.
        # No hy_open_v5 call, model tokenization, translation or model readiness claim.
        result = self.run_worker(
            "import ctypes,json; loader=LinuxRuntime(verify_native()); "
            "selected=loader.select_cpu_backend(); adapter=loader.load_adapter(); "
            "assert hasattr(adapter,'hy_open_v5') and hasattr(adapter,'hy_translate_glossary_v5'); "
            "assert not any(hasattr(adapter,name) for name in ('hy_open_v2','hy_open_v3','hy_open_v4','hy_translate_styled_v4','hy_audit')); "
            "cpu=ctypes.CDLL(str(selected)); "
            "print(json.dumps(loader.verify_loaded(selected))); loader.close()"
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        symbols = subprocess.check_output(
            ["nm", "-D", "--defined-only", str(self.native / "libtypelay_hy.so")],
            text=True,
            timeout=30,
        )
        self.assertEqual(
            sorted(line.split()[-1] for line in symbols.splitlines() if line.strip()),
            [
                "hy_close",
                "hy_open_v5",
                "hy_translate",
                "hy_translate_glossary_v5",
                "hy_translate_styled_v5",
            ],
        )
        loaded = json.loads(result.stdout)
        self.assertEqual(len(loaded), 5)
        for item in loaded:
            self.assertTrue(Path(item["path"]).is_relative_to(self.native))

    def test_relabelled_old_open_export_is_rejected_without_calling_it(self):
        adapter = self.native / "libtypelay_hy.so"
        manifest = self.native / "production-adapter.json"
        original_adapter, original_manifest = adapter.read_bytes(), manifest.read_bytes()
        marker = self.directory / "old-open-was-called"
        source = self.directory / "old-adapter.c"
        source.write_text(
            "#include <stdio.h>\n#include <stdlib.h>\n"
            "int hy_open_v4(void *a,void *b,void *c,void *d,unsigned e) {"
            "(void)a;(void)b;(void)c;(void)d;(void)e;"
            'FILE *f=fopen(getenv("TYPELAY_TEST_OLD_OPEN"),"w"); if(f)fclose(f);return 8;}\n'
            "void hy_close(void *p){(void)p;}\n"
            "int hy_translate(void){return 1;}\n"
        )
        try:
            subprocess.run(
                [
                    "cc",
                    "-shared",
                    "-fPIC",
                    "-Wall",
                    "-Wextra",
                    "-Werror",
                    str(source),
                    "-o",
                    str(adapter),
                ],
                check=True,
                capture_output=True,
                timeout=30,
            )
            claimed = json.loads(original_manifest)
            self.assertEqual(claimed["abi"], 5)
            claimed["adapter"].update(
                bytes=adapter.stat().st_size,
                sha256=hashlib.sha256(adapter.read_bytes()).hexdigest(),
            )
            manifest.write_text(json.dumps(claimed))
            result = self.run_worker(
                "from typelay_worker.hy import HyModel; "
                "HyModel({**verify_native(),'model_path':'must-not-open-a-model'})",
                env={**os.environ, "TYPELAY_TEST_OLD_OPEN": str(marker)},
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("hy_open_v5", result.stderr)
            self.assertFalse(marker.exists())
        finally:
            adapter.write_bytes(original_adapter)
            manifest.write_bytes(original_manifest)

    def test_unknown_styles_fail_before_touching_a_model_handle(self):
        result = self.run_worker(
            "import ctypes; from typelay_worker.hy import Metrics,CANCEL; "
            "loader=LinuxRuntime(verify_native()); loader.select_cpu_backend(); adapter=loader.load_adapter(); "
            "call=adapter.hy_translate_styled_v5; "
            "call.argtypes=[ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,"
            "ctypes.c_void_p,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,"
            "ctypes.POINTER(ctypes.c_uint32),ctypes.POINTER(Metrics),CANCEL,"
            "ctypes.c_void_p,ctypes.c_void_p,ctypes.c_uint32]; "
            "call.restype=ctypes.c_int32; "
            "output=ctypes.create_string_buffer(8192); error=ctypes.create_string_buffer(512); "
            "length=ctypes.c_uint32(99); metrics=Metrics(); "
            "code=call(1,1,0,b'x',1,output,len(output),ctypes.byref(length),"
            "ctypes.byref(metrics),CANCEL(),None,error,len(error)); "
            "assert (code,length.value,metrics.decode_calls)==(1,0,0); "
            "assert error.value==b'unknown translation style'; loader.close()"
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_invalid_glossaries_fail_before_touching_a_model_handle(self):
        result = self.run_worker(
            textwrap.dedent(r"""
            import ctypes
            from typelay_worker.hy import Metrics, CANCEL
            class Term(ctypes.Structure):
                _fields_ = [("source_term", ctypes.c_void_p), ("source_length", ctypes.c_uint32),
                            ("target_term", ctypes.c_void_p), ("target_length", ctypes.c_uint32)]
            assert ctypes.sizeof(Term) == 32
            loader = LinuxRuntime(verify_native())
            loader.select_cpu_backend()
            adapter = loader.load_adapter()
            call = adapter.hy_translate_glossary_v5
            call.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(Term), ctypes.c_uint32,
                             ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32,
                             ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(Metrics), CANCEL,
                             ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32]
            call.restype = ctypes.c_int32
            def invoke(pairs, *, count=None, handle=1, source=b'x', abi=5, null_field=None):
                buffers = [ctypes.create_string_buffer(value) for pair in pairs for value in pair]
                terms = (Term * len(pairs))(*[
                    Term(ctypes.addressof(buffers[2*i]), len(a), ctypes.addressof(buffers[2*i+1]), len(b))
                    for i, (a, b) in enumerate(pairs)])
                if null_field is not None:
                    setattr(terms[0], null_field, None)
                output, error = ctypes.create_string_buffer(8192), ctypes.create_string_buffer(512)
                output.value = b'unchanged'
                length, metrics = ctypes.c_uint32(99), Metrics()
                metrics.abi_version = abi
                code = call(handle, 1, terms if pairs else None, len(pairs) if count is None else count,
                            source, len(source), output, len(output), ctypes.byref(length),
                            ctypes.byref(metrics), CANCEL(), None, error, len(error))
                assert (code, length.value, metrics.decode_calls, output.value) == (1, 0, 0, b'unchanged')
                return error.value
            assert invoke([]) == b'glossary must contain 1..64 term pairs'
            assert invoke([], count=1) == b'glossary must contain 1..64 term pairs'
            assert invoke([(b'a', b'b')], count=0) == b'glossary must contain 1..64 term pairs'
            assert invoke([(b'a', b'b')], count=65) == b'glossary must contain 1..64 term pairs'
            for field in ('source_term', 'target_term'):
                assert invoke([(b'a', b'b')], null_field=field) == b'term must be 1..128 UTF-8 bytes'
            for bad in (b'', b'a'*129, ('字'*43).encode()):
                for pair in ((bad, b'b'), (b'a', bad)):
                    assert invoke([pair]) == b'term must be 1..128 UTF-8 bytes'
            for bad in (b'\xff', b'\xc0\xaf', b'\xed\xa0\x80', b'\xf4\x90\x80\x80'):
                for pair in ((bad, b'b'), (b'a', bad)):
                    assert invoke([pair]) == b'term is not strict UTF-8'
            for value in (*range(0x20), *range(0x7f, 0xa0)):
                bad = ('a' + chr(value) + 'b').encode()
                for pair in ((bad, b'b'), (b'a', bad)):
                    assert invoke([pair]) == b'term contains a control character'
            for value in (0x20, 0xa0, 0x1680, *range(0x2000, 0x200b), 0x2028, 0x2029, 0x202f, 0x205f, 0x3000):
                whitespace = chr(value).encode()
                for bad in (whitespace+b'a', b'a'+whitespace):
                    for pair in ((bad, b'b'), (b'a', bad)):
                        assert invoke([pair]) == b'term has surrounding whitespace'
            assert invoke([(b'a'*128, b'b'*128)]*33) == b'glossary exceeds 8192 UTF-8 bytes'
            for source in (b'', b'x'*8193):
                assert invoke([(b'a', b'b')], source=source) == b'source must be 1..8192 UTF-8 bytes'
            assert invoke([(b'a', b'b')], source=b'\xff') == b'source is not strict UTF-8'
            assert invoke([(b'a', b'b')], abi=4) == b'metrics ABI mismatch'
            # Valid boundaries reach the missing-handle check, never a fake model.
            for pairs in ([(b'a'*128, b'b'*128)]*32, [(b'a'*64, b'b'*64)]*64,
                          [(('😀'*32).encode(), b'b')],
                          [('项目'.encode(), '译文'.encode())], [('a b'.encode(), 'a\u200bb'.encode())]):
                assert invoke(pairs, handle=None) == b'translate argument mismatch'
            loader.close()
        """)
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_public_abi_numbers_and_metrics_layout_match_existing_consumers(self):
        names = [
            "ZH_JA",
            "JA_ZH",
            "ZH_KO",
            "KO_ZH",
            "ZH_FR",
            "FR_ZH",
            "ZH_ES",
            "ES_ZH",
            "ZH_DE",
            "DE_ZH",
            "ZH_PT",
            "PT_ZH",
        ]
        source = (
            '#include "hy_adapter.h"\n#include <stddef.h>\n'
            + "\n".join(
                f'_Static_assert(HY_{name} == {number}, "fixed direction");'
                for number, name in enumerate(names, 1)
            )
            + '\n_Static_assert(HY_NATURAL == 1 && HY_FORMAL == 2, "fixed style values");'
            + '\n_Static_assert(sizeof(hy_metrics) == 56, "fixed metrics layout");'
            + '\n_Static_assert(sizeof(hy_term) == 32, "natural x64 term layout");'
            + '\n_Static_assert(offsetof(hy_term, source_term) == 0 && offsetof(hy_term, source_length) == 8, "source fields");'
            + '\n_Static_assert(offsetof(hy_term, target_term) == 16 && offsetof(hy_term, target_length) == 24, "target fields");\n'
            + "int32_t (*glossary)(hy_handle *, uint32_t, const hy_term *, uint32_t, const uint8_t *, uint32_t, "
            + "uint8_t *, uint32_t, uint32_t *, hy_metrics *, hy_cancel_fn, void *, char *, uint32_t) = hy_translate_glossary_v5;\n"
        )
        result = subprocess.run(
            [
                "cc",
                "-std=c11",
                "-Werror",
                "-fsyntax-only",
                "-x",
                "c",
                "-",
                "-I" + str(ROOT / "src/shared/TypeLay.Worker/native/hy"),
            ],
            input=source,
            text=True,
            capture_output=True,
            timeout=30,
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_foreign_preloaded_same_name_library_is_refused(self):
        foreign = self.directory / "foreign"
        foreign.mkdir(exist_ok=True)
        source = self.native / "cpu-runtime/libggml-base.so.0"
        shutil.copyfile(source, foreign / source.name)
        environment = os.environ.copy()
        environment["LD_PRELOAD"] = str(foreign / source.name)
        result = self.run_worker("LinuxRuntime(verify_native())", env=environment)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("loaded before the verified loader", result.stderr)

    def test_library_search_environment_cannot_replace_verified_private_closure(self):
        foreign = self.directory / "search"
        foreign.mkdir(exist_ok=True)
        for name in ("libggml-base.so.0", "libggml.so.0", "libllama.so.0"):
            shutil.copyfile(self.native / "cpu-runtime" / name, foreign / name)
        environment = os.environ.copy()
        environment["LD_LIBRARY_PATH"] = str(foreign)
        result = self.run_worker(
            "loader=LinuxRuntime(verify_native()); selected=loader.select_cpu_backend(); "
            "loader.load_adapter(); loader.close()",
            env=environment,
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_changed_runtime_file_is_rejected_before_native_load(self):
        target = self.native / "cpu-runtime/libggml.so.0"
        original = target.read_bytes()
        try:
            target.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
            result = self.run_worker("LinuxRuntime(verify_native())")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("fixed asset full hash mismatch", result.stderr)
        finally:
            target.write_bytes(original)


if __name__ == "__main__":
    unittest.main()
