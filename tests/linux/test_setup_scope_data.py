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
from desktop_e2e import copy_setup_model_data  # noqa: E402


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


if __name__ == "__main__":
    unittest.main()
