"""Filesystem isolation only; no fake model readiness or desktop acceptance."""

import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
sys.path.insert(0, str(ROOT / "src/shared/TypeLay.Worker"))
from desktop_e2e import copy_setup_model_data, record_direction_archives  # noqa: E402
from typelay_worker.catalog import load_catalog  # noqa: E402


class SetupScopeDataTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        # Runtime relocation has its own production tests; these synthetic
        # bytes only exercise fixture storage isolation, never Python readiness.
        self.runtime_copy = self.enterContext(
            patch("desktop_e2e.copy_runtime", side_effect=self.copy_runtime_bytes)
        )
        self.source = self.root / "source"
        self.components = self.source / "typelay/components"
        self.generation = "a" * 32
        self.model = self.components / self.generation
        (self.model / "models").mkdir(parents=True)
        (self.model / "runtime").mkdir()
        (self.model / "activation.json").write_text(json.dumps({"schema": 1, "previous": None}))
        (self.model / "models/installed.json").write_text("synthetic-copy-payload")
        (self.model / "runtime/python").write_bytes(b"synthetic-runtime-payload")
        (self.components / "current").symlink_to(self.generation)
        (self.components / "desktop-text").mkdir()
        (self.components / "desktop-text/prepared").write_text("must-not-copy")

    @staticmethod
    def copy_runtime_bytes(source, destination, plan):
        assert {relative.as_posix() for _, relative, _ in plan} == {"python"}
        shutil.copytree(source, destination)

    def test_scopes_copy_bytes_but_do_not_share_component_state_or_model_files(self):
        settings, tutorial = self.root / "settings", self.root / "tutorial"
        for destination in (settings, tutorial):
            copy_setup_model_data(self.source, destination)
            components = destination / "typelay/components"
            self.assertEqual(self.generation, (components / "current").readlink().as_posix())
            self.assertEqual({self.generation, "current"}, {p.name for p in components.iterdir()})
            for relative in ("models/installed.json", "runtime/python", "activation.json"):
                self.assertEqual(
                    (self.model / relative).read_bytes(),
                    (components / self.generation / relative).read_bytes(),
                )
        self.assertEqual(2, self.runtime_copy.call_count)
        (settings / "typelay/components" / self.generation / "models/installed.json").write_text(
            "changed"
        )
        self.assertEqual(
            "synthetic-copy-payload", (self.model / "models/installed.json").read_text()
        )
        self.assertEqual(
            "synthetic-copy-payload",
            (
                tutorial / "typelay/components" / self.generation / "models/installed.json"
            ).read_text(),
        )
        with self.assertRaises(FileExistsError):
            copy_setup_model_data(self.source, settings)

    def test_unknown_or_recovery_installation_is_not_partially_copied(self):
        for activation in (
            {"schema": 1},
            {"previous": "b" * 32},
            {"previous": None, "recoveryPrevious": "b" * 32},
        ):
            with self.subTest(activation=activation):
                (self.model / "activation.json").write_text(json.dumps(activation))
                with self.assertRaisesRegex(ValueError, "fresh-model-installation"):
                    copy_setup_model_data(self.source, self.root / "destination")
                self.assertFalse((self.root / "destination").exists())

    def test_external_or_missing_generation_is_rejected(self):
        (self.components / "current").unlink()
        with self.assertRaisesRegex(ValueError, "generation-missing"):
            copy_setup_model_data(self.source, self.root / "destination")
        (self.components / "current").symlink_to(self.model.absolute())
        with self.assertRaisesRegex(ValueError, "model-current"):
            copy_setup_model_data(self.source, self.root / "destination")

    def test_generation_directory_cannot_alias_an_external_copy(self):
        external = self.root / "external"
        self.model.rename(external)
        self.model.symlink_to(external, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "model-current"):
            copy_setup_model_data(self.source, self.root / "destination")
        self.assertFalse((self.root / "destination").exists())

    def test_external_runtime_link_and_failed_relocation_never_publish_current(self):
        python = self.model / "runtime/python"
        python.unlink()
        external = self.root / "external-python"
        external.write_text("outside")
        python.symlink_to(external)
        destination = self.root / "external-destination"
        with self.assertRaisesRegex(ValueError, "python-runtime-path"):
            copy_setup_model_data(self.source, destination)
        self.assertFalse((destination / "typelay/components/current").is_symlink())
        python.unlink()
        python.write_text("synthetic-runtime-payload")
        self.runtime_copy.side_effect = ValueError("python-runtime-path")
        destination = self.root / "failed-destination"
        with self.assertRaisesRegex(ValueError, "python-runtime-path"):
            copy_setup_model_data(self.source, destination)
        self.assertFalse((destination / "typelay/components/current").is_symlink())


class DirectionArchiveEvidenceTests(unittest.TestCase):
    def test_each_completed_inventory_is_retained_after_source_inventory_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            directions = load_catalog(ROOT / "src/shared/TypeLay.Worker/model-catalog.json")
            observed = {
                "packageId": "argos-zh-en",
                "version": "1.9",
                "catalogUrl": directions[0].url,
                "provenance": "observed-local-archive-not-publisher-verified",
                "observedSha256": "a" * 64,
                "compressedBytes": 100,
                "declaredUnpackedBytes": 300,
                "retainedBytes": 200,
                "localPath": str(root / "private-model"),
            }
            result = {"outcome": "running"}
            marker = root / "installed.json"
            for inventory, selected in (
                ({"schema": 2, "dependencies": [{"name": "zh_en", **observed}]}, directions[:1]),
                (
                    {
                        "schema": 3,
                        "packages": [
                            {"id": "argos-zh-en", "archiveObservation": observed},
                            {"id": "argos-en-zh", "files": [{"path": "private-file"}]},
                        ],
                    },
                    directions,
                ),
            ):
                marker.write_text(json.dumps(inventory), encoding="utf8")
                record_direction_archives(
                    result, json.loads(marker.read_text(encoding="utf8")), selected
                )
                marker.unlink()
            # Persist the accumulated summary after the source inventories are gone.
            output = root / "direction-installation.json"
            output.write_text(json.dumps(result), encoding="utf8")
            captured = json.loads(output.read_text(encoding="utf8"))
            self.assertEqual(captured["outcome"], "running")
            first, second = captured["archiveObservations"]
            self.assertEqual(first["schema"], 2)
            self.assertEqual(first["directions"], ["zh_en"])
            self.assertEqual(second["schema"], 3)
            self.assertEqual(second["directions"], ["zh_en", "en_zh"])
            expected = {key: value for key, value in observed.items() if key != "localPath"}
            self.assertEqual(first["packages"], [{"status": "recorded", **expected}])
            self.assertEqual(
                second["packages"],
                [
                    {"status": "recorded", **expected},
                    {"packageId": "argos-en-zh", "version": "1.9", "status": "missing"},
                ],
            )
            self.assertNotIn(str(root), output.read_text(encoding="utf8"))
            self.assertNotIn("files", output.read_text(encoding="utf8"))

    def test_unusable_optional_observations_remain_missing_without_invented_sizes(self):
        directions = load_catalog(ROOT / "src/shared/TypeLay.Worker/model-catalog.json")[:1]
        for value in (None, "invalid", {"observedSha256": "bad", "compressedBytes": True}):
            with self.subTest(value=value):
                result = {}
                record_direction_archives(
                    result,
                    {
                        "schema": 3,
                        "packages": [
                            {"id": "argos-zh-en", "archiveObservation": value},
                        ],
                    },
                    directions,
                )
                self.assertEqual(
                    result["archiveObservations"][0]["packages"],
                    [
                        {"packageId": "argos-zh-en", "version": "1.9", "status": "missing"},
                    ],
                )


if __name__ == "__main__":
    unittest.main()
