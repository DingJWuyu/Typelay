"""Build provenance contracts only. No model or compiler quality claim."""

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))
from eng.index_profile import (  # noqa: E402
    LINK_INPUTS,
    confirm_native_sources,
    cpu_material,
    native_sources,
)


class IndexBuildProvenanceTests(unittest.TestCase):
    def test_changed_compiler_input_cannot_be_labeled_with_the_original_fingerprint(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "include").mkdir()
            (root / "headers.lock.json").write_text(json.dumps({"files": [{"file": "llama.h"}]}))
            for name in (
                "index_adapter.cpp",
                "index_adapter.h",
                "chat_template.jinja",
                "include/llama.h",
            ):
                (root / name).write_text("original input " + name)
            for name in LINK_INPUTS:
                (root / name).parent.mkdir(parents=True, exist_ok=True)
                (root / name).write_text("original link input " + name)
            expected = native_sources(root, root)
            confirm_native_sources(expected, root, root)
            for name in (
                "index_adapter.cpp",
                "index_adapter.h",
                "chat_template.jinja",
                "include/llama.h",
                *LINK_INPUTS,
            ):
                with self.subTest(name=name):
                    before = (root / name).read_bytes()
                    (root / name).write_bytes(before + b"changed")
                    with self.assertRaisesRegex(ValueError, "changed during compilation"):
                        confirm_native_sources(expected, root, root)
                    (root / name).write_bytes(before)

    def test_cpu_record_order_is_not_behavior_but_every_constraint_remains_material(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "cpu.json"
            value = {
                "platform": "linux-x86_64",
                "files": [{"file": "b", "sha256": "two"}, {"file": "a", "sha256": "one"}],
            }
            path.write_text(json.dumps(value))
            expected = cpu_material(path)
            value["files"].reverse()
            path.write_text(json.dumps(value))
            self.assertEqual(expected, cpu_material(path))
            value["files"][0]["sha256"] = "changed"
            path.write_text(json.dumps(value))
            self.assertNotEqual(expected, cpu_material(path))
