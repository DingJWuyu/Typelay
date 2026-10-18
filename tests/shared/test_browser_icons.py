"""Check extension manifests against the shared, ready-to-package PNG resources."""

import json
import struct
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class BrowserIconTests(unittest.TestCase):
    def test_platform_manifests_declare_existing_pngs_at_the_requested_sizes(self):
        expected = {
            "16": "icons/typelay-16.png",
            "48": "icons/typelay-48.png",
            "128": "icons/typelay-128.png",
        }
        for relative in (
            "src/windows/browser/manifest.chromium.json",
            "src/linux/browser/manifest.chromium.json",
            "src/linux/browser/manifest.firefox.json",
        ):
            with self.subTest(manifest=relative):
                manifest = json.loads((ROOT / relative).read_text(encoding="utf-8"))
                self.assertEqual(manifest.get("icons"), expected)
                for size, name in manifest["icons"].items():
                    png = (ROOT / "src/shared/browser" / name).read_bytes()
                    self.assertEqual(png[:16], b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR")
                    self.assertEqual(struct.unpack(">II", png[16:24]), (int(size), int(size)))
                    self.assertEqual(png[24:26], b"\x08\x06", "Expected 8-bit RGBA PNG")


if __name__ == "__main__":
    unittest.main()
