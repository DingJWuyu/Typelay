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
        # No hy_open_v3 call, model tokenization, translation or model readiness claim.
        result = self.run_worker(
            "import ctypes,json; loader=LinuxRuntime(verify_native()); "
            "selected=loader.select_cpu_backend(); adapter=loader.load_adapter(); "
            "assert hasattr(adapter,'hy_open_v3'); "
            "assert not hasattr(adapter,'hy_open') and not hasattr(adapter,'hy_audit'); "
            "cpu=ctypes.CDLL(str(selected)); "
            "print(json.dumps(loader.verify_loaded(selected))); loader.close()"
        )
        self.assertEqual(result.returncode, 0, result.stderr)
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
            "int hy_open(void *a,void *b,void *c,void *d,unsigned e) {"
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
            self.assertEqual(claimed["abi"], 3)
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
            self.assertIn("hy_open_v3", result.stderr)
            self.assertFalse(marker.exists())
        finally:
            adapter.write_bytes(original_adapter)
            manifest.write_bytes(original_manifest)

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
            '#include "hy_adapter.h"\n'
            + "\n".join(
                f'_Static_assert(HY_{name} == {number}, "fixed direction");'
                for number, name in enumerate(names, 1)
            )
            + '\n_Static_assert(sizeof(hy_metrics) == 56, "fixed metrics layout");\n'
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
