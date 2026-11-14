"""Native capture metadata projection, not desktop or input-method evidence."""

import copy
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from input import Input  # noqa: E402


class SourceVersionProjectionTests(unittest.TestCase):
    def capture(self):
        return {
            "target": "field-a",
            "focused": True,
            "ime": "Clear",
            "metadata": {
                "aria": "QLineEdit",
                "documentGeneration": 0,
                "password": False,
                "readOnly": False,
                "enabled": True,
            },
            "authority": {
                "target": "field-a",
                "contentRevision": 5,
                "selectionRevision": 9,
                "focusRevision": 3,
                "contextStamp": {"context": 2, "field": 4, "flags": 3, "server": 8, "applied": 8},
            },
            "sourceVersion": {
                "version": 1,
                "content": 5,
                "selection": 9,
                "document": 1,
                "focus": 3,
                "context": 2,
                "field": 4,
                "integrity": 7,
            },
        }

    def test_exact_native_read_projects_only_the_body_free_version(self):
        native = self.capture()
        native["text"] = "Must not enter the evidence projection"
        self.assertEqual(
            Input.source_observation_version(SimpleNamespace(backend="qt6"), native),
            native["sourceVersion"],
        )

    def test_missing_malformed_or_foreign_evidence_is_not_a_capability(self):
        original = self.capture()
        variants = []
        for key in original["sourceVersion"]:
            for invalid in (None, True, -1, 0, 2**63 - 1):
                native = copy.deepcopy(original)
                native["sourceVersion"][key] = invalid
                variants.append(native)
            native = copy.deepcopy(original)
            del native["sourceVersion"][key]
            variants.append(native)
        for key, value in (("focused", False), ("ime", "Unknown"), ("target", "different")):
            native = copy.deepcopy(original)
            native[key] = value
            variants.append(native)
        for key in ("content", "selection", "document", "focus", "context", "field"):
            native = copy.deepcopy(original)
            native["sourceVersion"][key] += 1
            variants.append(native)
        for native in variants:
            with self.subTest(native=native):
                self.assertIsNone(
                    Input.source_observation_version(SimpleNamespace(backend="qt6"), native)
                )
        for backend in ("browser", "gtk3", "gtk4", "unknown"):
            self.assertIsNone(
                Input.source_observation_version(SimpleNamespace(backend=backend), original)
            )


if __name__ == "__main__":
    unittest.main()
