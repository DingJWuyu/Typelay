"""Match actual Pango faces against the offline font bundle, without a display.

Requires the real verified bundle prepared by eng/fonts/prepare.py. These tests
verify selected font bytes and regional glyph coverage, not desktop geometry.
"""

import hashlib
import json
import os
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))

import design  # noqa: E402
from gi.repository import Pango, PangoCairo  # noqa: E402


class BundledUiFonts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = Path(os.environ.get("TYPELAY_UI_FONT_ROOT", ROOT / "artifacts/ui-fonts"))
        cls.records = [
            item
            for item in json.loads((cls.root / "manifest.json").read_text())["files"]
            if "family" in item
        ]
        design.register_fonts()
        cls.font_map = PangoCairo.FontMap.get_default()
        cls.context = cls.font_map.create_context()

    def selected(self, font):
        face = font.get_face()
        self.assertIsNotNone(face)
        self.assertFalse(face.is_synthesized(), font.describe().to_string())
        selected = json.loads(font.serialize().get_data())
        matches = [item for item in self.records if item["sha256"] == selected["checksum"]]
        self.assertEqual(len(matches), 1, selected)
        path = self.root / matches[0]["path"]
        self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), selected["checksum"])
        return matches[0], face.describe().get_weight()

    def test_registered_cjk_roles_resolve_exact_locked_face_bytes_and_weight(self):
        for item in self.records:
            if not item["family"].startswith("Noto "):
                continue
            with self.subTest(family=item["family"], weight=item["weight"]):
                description = Pango.FontDescription()
                description.set_family(item["family"])
                description.set_weight(item["weight"])
                description.set_absolute_size(15 * Pango.SCALE)
                selected, weight = self.selected(self.font_map.load_font(self.context, description))
                self.assertEqual(selected["path"], item["path"])
                self.assertEqual(weight, item["weight"])

    def test_regional_content_runs_and_wrapping_use_real_packaged_glyphs(self):
        samples = {
            "zh": "语言设置：你好，我们明天下午见。",
            "ja": "言語設定：こんにちは、また明日会いましょう。",
            "ko": "언어 설정: 안녕하세요, 내일 다시 만나요.",
        }
        for choice in ("claude", "sans"):
            for language, sample in samples.items():
                with self.subTest(font=choice, language=language):
                    layout = Pango.Layout.new(self.context)
                    families = design.font_families({"font": choice, "target": language}, "content")
                    description = Pango.FontDescription()
                    description.set_family(", ".join(families))
                    description.set_absolute_size(15 * Pango.SCALE)
                    layout.set_font_description(description)
                    layout.set_text(sample, -1)
                    layout.set_width(120 * Pango.SCALE)
                    layout.set_wrap(Pango.WrapMode.WORD_CHAR)
                    self.assertEqual(layout.get_unknown_glyphs_count(), 0)
                    self.assertGreater(layout.get_line_count(), 1)
                    selected_families = set()
                    runs = layout.get_iter()
                    while True:
                        run = runs.get_run_readonly()
                        if run is not None:
                            item = run.item
                            analysis = item.analysis
                            selected, weight = self.selected(analysis.font)
                            selected_families.add(selected["family"])
                            self.assertEqual(weight, 400)
                        if not runs.next_run():
                            break
                    self.assertIn(families[1], selected_families)

    def test_system_preference_does_not_force_a_bundled_content_family(self):
        for role in ("display", "content"):
            self.assertEqual(
                design.font_families({"font": "system", "target": "ja"}, role), ("sans-serif",)
            )


if __name__ == "__main__":
    unittest.main()
