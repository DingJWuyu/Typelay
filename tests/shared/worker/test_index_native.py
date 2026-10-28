"""Index asset and request refusal contracts; no model quality claims."""

import tempfile
import unittest
from pathlib import Path

from typelay_worker import index_assets


class IndexAssetTests(unittest.TestCase):
    def test_duplicate_manifest_keys_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "manifest.json"
            path.write_text('{"abi":1,"abi":5}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "duplicate"):
                index_assets.read_json(path)

    def test_missing_native_payload_cannot_offer_a_backend(self):
        # The source tree deliberately contains no production binary or manifest.
        with self.assertRaises((ValueError, FileNotFoundError)):
            index_assets.verify_native()


class IndexWholeSourceTests(unittest.TestCase):
    def test_fragmented_source_is_refused_before_model_access(self):
        from typelay_worker.engine import EngineError
        from typelay_worker.index import IndexDirection

        request = IndexDirection(None, 1).request("第一句。\n第二句。", float("inf"))
        with self.assertRaisesRegex(EngineError, "index-whole-source-required"):
            request.translate("第一句。")

    def test_original_utf8_byte_limit_is_checked_before_model_access(self):
        from typelay_worker.engine import EngineError
        from typelay_worker.index import IndexDirection

        with self.assertRaisesRegex(EngineError, "segment-too-long"):
            IndexDirection(None, 1).request("界" * 2731, float("inf"))

    def test_unknown_style_cannot_become_plain_translation(self):
        from typelay_worker.engine import EngineError
        from typelay_worker.index import IndexDirection

        with self.assertRaisesRegex(EngineError, "invalid-request"):
            IndexDirection(None, 1).request("原文", float("inf"), "persuasive")


if __name__ == "__main__":
    unittest.main()
