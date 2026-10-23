"""No-model packaged identity contracts; synthetic bytes are never native-loaded."""

import copy
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/shared/TypeLay.Worker"))
from typelay_worker import hy_assets, inventory  # noqa: E402


class WindowsPayloadTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="typelay-hy-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "搬迁 包"
        self.root.mkdir()
        (self.root / "cpu-runtime").mkdir()
        source = json.loads((hy_assets.NATIVE_ROOT / "assets.lock.json").read_text())
        self.cpu = {
            "schema": 1,
            "platform": "win-x64",
            "upstream_tag": "b11717",
            "upstream_revision": source["runtime_archive"]["commit"],
            "runtime_archive_sha256": source["runtime_archive"]["sha256"],
            "files": [],
        }
        for original in source["runtime_files"]:
            path = self.root / "cpu-runtime" / original["file"]
            path.write_bytes(("identity fixture " + path.name).encode())
            self.cpu["files"].append({**self.record(path), "kind": original["kind"]})
        (self.root / "model.lock.json").write_bytes(
            (hy_assets.NATIVE_ROOT / "model.lock.json").read_bytes()
        )
        for name in ("typelay_hy.dll", "LICENSE.llama.cpp"):
            (self.root / name).write_bytes(("identity fixture " + name).encode())
        self.write_cpu()
        self.manifest = {
            "schema": 1,
            "platform": "win-x64",
            "abi": 5,
            "exports": hy_assets.EXPORTS,
            "upstream_revision": source["runtime_archive"]["commit"],
            "template_sha256": source["template_sha256"],
            "model_lock_sha256": hy_assets.digest(self.root / "model.lock.json"),
            "cpu_runtime_lock_sha256": hy_assets.digest(
                self.root / "cpu-runtime.win-x64.lock.json"
            ),
            "adapter": self.record(self.root / "typelay_hy.dll"),
            "license": self.record(self.root / "LICENSE.llama.cpp"),
        }
        self.write_manifest()
        for target, value in (
            ("NATIVE_ROOT", self.root),
            ("platform.system", lambda: "Windows"),
            ("platform.machine", lambda: "AMD64"),
        ):
            patcher = patch("typelay_worker.hy_assets." + target, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    @staticmethod
    def record(path):
        return {"file": path.name, "bytes": path.stat().st_size, "sha256": hy_assets.digest(path)}

    def write_cpu(self):
        (self.root / "cpu-runtime.win-x64.lock.json").write_text(json.dumps(self.cpu))

    def write_manifest(self):
        (self.root / "production-adapter.json").write_text(json.dumps(self.manifest))

    def test_only_exact_legacy_or_extended_closure_is_admitted(self):
        for exports, available in ((hy_assets.EXPORTS, False), (hy_assets.EXTENDED_EXPORTS, True)):
            self.manifest["exports"] = exports
            self.write_manifest()
            self.assertEqual(hy_assets.verify_native()["styled_glossary"], available)
        for exports in (hy_assets.EXTENDED_EXPORTS[:-1], [*hy_assets.EXTENDED_EXPORTS, "unknown"]):
            self.manifest["exports"] = exports
            self.write_manifest()
            with self.assertRaises(ValueError):
                hy_assets.verify_native()

    def test_english_capability_requires_exact_version_and_export_closure_without_changing_model_lock(
        self,
    ):
        original = (self.root / "model.lock.json").read_bytes()
        self.manifest["exports"] = hy_assets.ENGLISH_EXPORTS
        self.manifest["english_translation_version"] = 1
        self.write_manifest()
        verified = hy_assets.verify_native()
        self.assertEqual(verified["english_translation_version"], 1)
        self.assertTrue(verified["styled_glossary"])
        self.assertEqual((self.root / "model.lock.json").read_bytes(), original)
        for value in (None, True, 0, 2, "1"):
            with self.subTest(value=value):
                self.manifest["english_translation_version"] = value
                self.write_manifest()
                with self.assertRaises(ValueError):
                    hy_assets.verify_native()
        self.manifest.pop("english_translation_version")
        self.write_manifest()
        with self.assertRaises(ValueError):
            hy_assets.verify_native()
        self.manifest["exports"] = hy_assets.EXTENDED_EXPORTS
        self.manifest["english_translation_version"] = 1
        self.write_manifest()
        with self.assertRaises(ValueError):
            hy_assets.verify_native()
        self.manifest.pop("english_translation_version")
        self.write_manifest()
        self.assertIsNone(hy_assets.verify_native()["english_translation_version"])

    def test_relocated_manifest_and_cpu_lock_are_sufficient_without_source_or_model(self):
        result = hy_assets.verify_native()
        self.assertEqual(result["adapter_path"], str(self.root / "typelay_hy.dll"))
        self.assertEqual(len(result["cpu_runtime"]["variants"]), 14)
        self.assertEqual(len(result["cpu_runtime"]["files"]), 19)
        self.assertFalse(any(self.root.rglob("*.gguf")))

    def test_wrong_platform_abi_old_exports_and_template_never_validate(self):
        original = copy.deepcopy(self.manifest)
        changes = [
            {"platform": "linux-x86_64"},
            {"platform": "win-arm64"},
            {"abi": 4},
            {"exports": ["hy_open_v4"]},
            {"exports": sorted([*hy_assets.EXPORTS, "hy_audit"])},
            {"template_sha256": "0" * 64},
            {"model_lock_sha256": "0" * 64},
            {"cpu_runtime_lock_sha256": "0" * 64},
        ]
        for change in changes:
            with self.subTest(change=change):
                self.manifest = {**original, **change}
                self.write_manifest()
                with self.assertRaises(ValueError):
                    hy_assets.verify_native()

    def test_adapter_cpu_and_notices_are_full_hash_checked(self):
        for path in (
            self.root / "typelay_hy.dll",
            self.root / "LICENSE.llama.cpp",
            self.root / "cpu-runtime/ggml-cpu-x64.dll",
            self.root / "cpu-runtime/LICENSE-LLVM-OpenMP",
        ):
            with self.subTest(path=path.name):
                original = path.read_bytes()
                path.write_bytes(b"X" + original[1:])
                with self.assertRaisesRegex(ValueError, "full hash mismatch"):
                    hy_assets.verify_native()
                path.write_bytes(original)

    def test_missing_unknown_or_duplicate_cpu_members_are_rejected(self):
        original = copy.deepcopy(self.cpu)
        for change in ("missing", "duplicate", "case_alias", "gpu", "wrong_archive"):
            with self.subTest(change=change):
                self.cpu = copy.deepcopy(original)
                if change == "missing":
                    self.cpu["files"].pop()
                elif change == "duplicate":
                    self.cpu["files"].append(self.cpu["files"][-1])
                elif change == "case_alias":
                    self.cpu["files"].append({**self.cpu["files"][-1], "file": "LLAMA.DLL"})
                elif change == "gpu":
                    self.cpu["files"][-1]["file"] = "ggml-vulkan.dll"
                else:
                    self.cpu["runtime_archive_sha256"] = "0" * 64
                self.write_cpu()
                self.manifest["cpu_runtime_lock_sha256"] = hy_assets.digest(
                    self.root / "cpu-runtime.win-x64.lock.json"
                )
                self.write_manifest()
                with self.assertRaises(ValueError):
                    hy_assets.verify_native()

    def test_runtime_only_payload_rejects_extra_sources_archives_and_dlls(self):
        for path in (
            self.root / "assets.lock.json",
            self.root / "build-verification.json",
            self.root / "fixture.zip",
            self.root / "hy_adapter.cpp",
            self.root / "cpu-runtime/ggml-rpc.dll",
            self.root / "cpu-runtime/llama-cli.exe",
        ):
            with self.subTest(path=path.name):
                path.write_bytes(b"not part of production")
                with self.assertRaises(ValueError):
                    hy_assets.verify_native()
                path.unlink()

    def test_duplicate_manifest_keys_and_linked_files_are_rejected(self):
        path = self.root / "production-adapter.json"
        path.write_text('{"schema":1,"schema":1}')
        with self.assertRaisesRegex(ValueError, "duplicate"):
            hy_assets.verify_native()
        self.write_manifest()
        original = self.root / "typelay_hy.dll"
        alternate = self.root.parent / "other.dll"
        original.rename(alternate)
        os.link(alternate, original)
        with self.assertRaisesRegex(ValueError, "single-link regular"):
            hy_assets.verify_native()

    def installed_inventory(self):
        # Tiny identity fixtures exercise the unmodified production verifier;
        # no native library or actual GGUF is loaded by these tests.
        model = json.loads((self.root / "model.lock.json").read_text())
        models = self.root.parent / "models"
        package = models / "packages" / model["package_id"] / model["version"]
        package.mkdir(parents=True)
        files = []
        for key in ("model", "model_license"):
            path = package / model[key]["file"]
            path.write_bytes(("no-model identity fixture " + path.name).encode())
            model[key] = {**model[key], **self.record(path)}
            files.append({"path": path.name, "sha256": model[key]["sha256"]})
        (self.root / "model.lock.json").write_text(json.dumps(model))
        self.manifest["model_lock_sha256"] = hy_assets.digest(self.root / "model.lock.json")
        self.write_manifest()
        (models / "installed.json").write_text(
            json.dumps(
                {
                    "schema": 3,
                    "packages": [
                        {
                            "id": model["package_id"],
                            "version": model["version"],
                            "directions": model["directions"],
                            "files": files,
                        }
                    ],
                    "activePackages": [model["package_id"]],
                }
            )
        )
        return models, package

    def test_windows_inventory_admission_still_requires_full_production_verification(self):
        models, _ = self.installed_inventory()
        with patch("sys.platform", "win32"):
            result = inventory.verify_inventory(models)
        self.assertEqual(len(result.packages), 1)
        self.assertEqual(
            result.packages[0].native["adapter_path"], str(self.root / "typelay_hy.dll")
        )
        self.assertEqual(
            [direction.id for direction in result.packages[0].selection.directions],
            list(hy_assets.DIRECTIONS),
        )

    def test_windows_inventory_rejects_bad_model_and_native_hashes_without_fallback(self):
        models, package = self.installed_inventory()
        paths = [
            self.root / "typelay_hy.dll",
            self.root / "LICENSE.llama.cpp",
            self.root / "cpu-runtime/ggml-cpu-x64.dll",
            self.root / "cpu-runtime/LICENSE-LLVM-OpenMP",
            *package.iterdir(),
        ]
        with patch("sys.platform", "win32"):
            for path in paths:
                with self.subTest(path=path.name):
                    original = path.read_bytes()
                    path.write_bytes(b"X" + original[1:])
                    try:
                        with self.assertRaises(ValueError):
                            inventory.verify_inventory(models)
                    finally:
                        path.write_bytes(original)

    def test_windows_inventory_does_not_bypass_the_exact_native_platform(self):
        models, _ = self.installed_inventory()
        with patch("sys.platform", "win32"):
            for system, machine in (("Windows", "ARM64"), ("Linux", "x86_64")):
                with (
                    self.subTest(system=system, machine=machine),
                    patch("typelay_worker.hy_assets.platform.system", return_value=system),
                    patch("typelay_worker.hy_assets.platform.machine", return_value=machine),
                    self.assertRaises(ValueError),
                ):
                    inventory.verify_inventory(models)

    def test_installed_model_tree_cannot_supply_an_alternate_native_lock(self):
        package = self.root.parent / "installed-model"
        package.mkdir()
        (package / "production-adapter.json").write_text(json.dumps(self.manifest))
        with self.assertRaises(ValueError):
            hy_assets.verify_production(package)


if __name__ == "__main__":
    unittest.main()
