"""Deterministic protocol/structure tests. Does NOT substitute for actual model inference."""

import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from typelay_worker.engine import EngineError, LocalEngine


class InventoryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.entries = []
        for pair in ("zh_en", "en_zh"):
            for name in ("model/model.bin", "sentencepiece.model"):
                rel = f"{pair}/{name}"
                path = self.root / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"inventory-test-bytes-not-a-model")
                self.entries.append(
                    {"path": rel, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                )
        self.save()

    def tearDown(self):
        self.tmp.cleanup()

    def save(self):
        (self.root / "installed.json").write_text(
            json.dumps({"schema": 1, "files": self.entries}), encoding="utf8"
        )

    def test_tampered_file(self):
        (self.root / self.entries[0]["path"]).write_bytes(b"changed")
        with self.assertRaisesRegex(EngineError, "^model-(inventory|integrity)$"):
            LocalEngine(self.root)

    def test_path_escape(self):
        self.entries[0]["path"] = "../escape"
        self.save()
        with self.assertRaisesRegex(EngineError, "^model-(inventory|integrity)$"):
            LocalEngine(self.root)

    def test_duplicate(self):
        self.entries[0] = self.entries[1]
        self.save()
        with self.assertRaisesRegex(EngineError, "^model-(inventory|integrity)$"):
            LocalEngine(self.root)

    def test_missing_required(self):
        self.entries = self.entries[:3]
        self.save()
        with self.assertRaisesRegex(EngineError, "^model-(inventory|integrity)$"):
            LocalEngine(self.root)

    def test_no_inventory(self):
        (self.root / "installed.json").unlink()
        with self.assertRaisesRegex(EngineError, "^model-(inventory|integrity)$"):
            LocalEngine(self.root)
