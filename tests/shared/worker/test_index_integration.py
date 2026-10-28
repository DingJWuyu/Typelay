"""Index package/worker contracts; controlled inference is not model quality evidence."""

import unittest

from typelay_worker.catalog import load_package_catalog, read_active_selection


class IndexSelectionTests(unittest.TestCase):
    def test_default_is_index_but_legacy_inventory_keeps_argos(self):
        catalog = load_package_catalog()
        self.assertEqual("index-translate-2b-q4-k-m", catalog.default_package.id)
        self.assertEqual(14, len(catalog.default_directions))
        selected = read_active_selection({"schema": 1}, catalog)
        self.assertEqual(["argos-zh-en", "argos-en-zh"], [x.package.id for x in selected])
        self.assertEqual(["zh_en", "en_zh"], [x.id for x in catalog.legacy_directions])

    def test_index_active_does_not_claim_inactive_hy_or_argos_directions(self):
        catalog = load_package_catalog()
        index = next(p for p in catalog.packages if p.id == "index-translate-2b-q4-k-m")
        argos = next(p for p in catalog.packages if p.id == "argos-en-zh")
        inventory = {
            "schema": 3,
            "packages": [
                {
                    "id": p.id,
                    "version": p.version,
                    "directions": [direction],
                    "files": [{"path": "test.fixture", "sha256": "a" * 64}],
                }
                for p, direction in ((index, "zh_en"), (argos, "en_zh"))
            ],
            "activePackages": [index.id],
        }
        selected = read_active_selection(inventory, catalog)
        self.assertEqual([index.id], [p.package.id for p in selected])
        self.assertEqual(["zh_en"], [d.id for p in selected for d in p.directions])
        inventory["activePackages"].append(argos.id)
        with self.assertRaisesRegex(ValueError, "model-inventory"):
            read_active_selection(inventory, catalog)


class WholeSourceTests(unittest.TestCase):
    def run_translation(self, source, output):
        from typelay_worker.engine import translate_whole_source

        class NativeBoundary:
            def request(boundary, original, deadline, style, **requirements):
                self.assertEqual(source, original)
                self.requirements = requirements
                return boundary

            def translate(boundary, text):
                self.assertEqual(source, text)
                self.calls += 1
                return output

        self.calls = 0
        return translate_whole_source(source, NativeBoundary(), 60, None, None, "en", "zh")

    def test_plain_source_has_no_artificial_requirements(self):
        self.assertEqual("明天见。", self.run_translation("See you tomorrow.", "明天见。"))
        self.assertEqual(1, self.calls)
        self.assertEqual(
            {"preserved_literals": (), "preserve_linebreaks": False, "preserve_whitespace": False},
            self.requirements,
        )

    def test_complete_source_including_crlf_literals_and_spaces_is_one_request(self):
        source = "  Send https://example.com to @sam.\r\nThanks 😀  "
        output = "  把 https://example.com 发给 @sam.\r\n谢谢 😀  "
        self.assertEqual(output, self.run_translation(source, output))
        self.assertEqual(1, self.calls)
        self.assertTrue(self.requirements["preserve_linebreaks"])
        self.assertTrue(self.requirements["preserve_whitespace"])

    def test_mutated_structure_is_refused_without_repair_or_retry(self):
        from typelay_worker.engine import EngineError

        for result in (
            "见 https://evil.example\n谢谢。",
            "见 https://example.com\n\n谢谢。",
            "见 https://example.com\r\n谢谢 😀。",
        ):
            with self.subTest(result=result):
                with self.assertRaisesRegex(EngineError, "structure-not-preserved"):
                    self.run_translation("See https://example.com\r\nThanks.", result)
                self.assertEqual(1, self.calls)

    def test_all_nontranslatable_fragments_keep_original_without_inference(self):
        source = "123 https://a.test 456"
        self.assertEqual(source, self.run_translation(source, "999 https://a.test 000"))
        self.assertEqual(0, self.calls)

    def test_numeric_fragment_and_protected_edge_whitespace_are_not_repaired(self):
        from typelay_worker.engine import EngineError

        for source, output in (
            ("123 https://a.test send it.", "999 https://a.test 发过去。"),
            ("Send  https://a.test  please.", "发送 https://a.test 请。"),
            ("Send 😀  thanks.\r\nDone.", "发送 😀 谢谢。\r\n完成。"),
        ):
            with self.subTest(source=source):
                with self.assertRaisesRegex(EngineError, "structure-not-preserved"):
                    self.run_translation(source, output)
                self.assertEqual(1, self.calls)

    def test_each_original_glossary_segment_must_keep_its_target(self):
        from typelay_worker.engine import EngineError, translate_whole_source
        from typelay_worker.glossary import Rule

        class NativeBoundary:
            calls = 0

            def request(boundary, original, deadline, style, **requirements):
                self.assertEqual("battery.\nThe battery works.", original)
                return boundary

            def translate_glossary(boundary, original, terms):
                boundary.calls += 1
                return "电池。\n设备工作。"

        boundary = NativeBoundary()
        with self.assertRaisesRegex(EngineError, "glossary-output-missing"):
            translate_whole_source(
                "battery.\nThe battery works.",
                boundary,
                60,
                None,
                (Rule("battery", "电池", None, None),),
                "en",
                "zh",
            )
        self.assertEqual(1, boundary.calls)

    def test_source_owned_outer_padding_and_line_endings_are_serialized(self):
        source = " \tPlease keep https://example.com\r\nThank you. \t"
        self.assertEqual(
            " \t请保留 https://example.com\r\n谢谢。 \t",
            self.run_translation(source, "  请保留 https://example.com\n谢谢。"),
        )
        self.assertEqual(1, self.calls)
        self.assertEqual(
            "甲\r乙\n丙\r\n丁", self.run_translation("A\rB\nC\r\nD", "甲\n乙\r\n丙\r丁")
        )

    def test_layout_serialization_never_repairs_topology_or_internal_content(self):
        from typelay_worker.engine import EngineError

        for source, output in (
            (" A\r\nB ", "甲乙"),
            (" A\r\nB ", "\n甲\n乙"),
            (" A\r\nB ", "甲\n乙\n"),
            ("\u00a0A B\u3000", "甲乙"),
            (" A\r\nB ", "甲\n\n乙"),
            ("A https://a.test\r\nB", "甲\nhttps://a.test乙"),
            ("123 https://a.test send it.", "999 https://a.test 发过去。"),
            (" A\r\n  B ", "甲\n乙"),
            (" A  https://a.test  B ", "甲 https://a.test 乙"),
        ):
            with self.subTest(source=source):
                with self.assertRaisesRegex(EngineError, "structure-not-preserved"):
                    self.run_translation(source, output)
                self.assertEqual(1, self.calls)

    def test_only_whitespace_never_invokes_model(self):
        source = " \t\r\n \r\n\t"
        self.assertEqual(source, self.run_translation(source, "invented"))
        self.assertEqual(0, self.calls)

    def test_serialized_output_rechecks_utf8_and_text_limits(self):
        from typelay_worker.engine import EngineError

        with self.assertRaisesRegex(EngineError, "output-too-long"):
            self.run_translation("  A ", "界" * 2730)
        with self.assertRaisesRegex(EngineError, "output-too-long"):
            self.run_translation("  A ", "x" * 8191)

    def test_entire_translatable_slot_cannot_disappear_into_its_padding(self):
        from typelay_worker.engine import EngineError

        with self.assertRaisesRegex(EngineError, "structure-not-preserved"):
            self.run_translation(
                "Please https://a.test battery https://b.test thanks.",
                "请 https://a.test https://b.test 谢谢。",
            )
        self.assertEqual(1, self.calls)
