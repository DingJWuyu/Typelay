"""Deterministic protocol/structure tests. Does NOT substitute for actual model inference."""

import unittest

from typelay_worker.engine import EngineError, merge_manual, preserve_structure


class UppercaseSegment:
    """Test double for structure-helper tests only; never imported by the runtime."""

    def translate(self, text):
        return text.upper()


class StructureTests(unittest.TestCase):
    def test_whitespace(self):
        self.assertEqual(preserve_structure("  hello  ", UppercaseSegment()), "  HELLO  ")

    def test_line_endings(self):
        self.assertEqual(preserve_structure("a\r\nb\nc\rd", UppercaseSegment()), "A\r\nB\nC\rD")

    def test_url(self):
        self.assertEqual(
            preserve_structure("go https://Example.com/a?b=1 now", UppercaseSegment()),
            "GO https://Example.com/a?b=1 NOW",
        )

    def test_mention(self):
        self.assertEqual(
            preserve_structure("hello @Alice thanks", UppercaseSegment()), "HELLO @Alice THANKS"
        )

    def test_email(self):
        self.assertEqual(
            preserve_structure("mail Me@Example.com please", UppercaseSegment()),
            "MAIL Me@Example.com PLEASE",
        )

    def test_issue(self):
        self.assertEqual(
            preserve_structure("issue #123 ready", UppercaseSegment()), "ISSUE #123 READY"
        )

    def test_emoji(self):
        self.assertEqual(
            preserve_structure("yes 👩‍💻 thanks 🙂", UppercaseSegment()), "YES 👩‍💻 THANKS 🙂"
        )

    def test_keycaps_and_emoji_presentation_symbols_are_not_sent_to_model(self):
        class ReplaceSegment:
            def __init__(self):
                self.inputs = []

            def translate(self, text):
                self.inputs.append(text)
                return "translated"

        for emoji in (
            *(key + selector + "\u20e3" for key in "0123456789#*" for selector in ("", "\ufe0f")),
            "©️",
            "®️",
        ):
            with self.subTest(emoji=emoji):
                translator = ReplaceSegment()
                self.assertEqual(
                    preserve_structure("按" + emoji + "继续", translator),
                    "translated" + emoji + "translated",
                )
                self.assertEqual(translator.inputs, ["按", "继续"])

    def test_keycap_is_not_split_by_overlapping_issue_or_mention(self):
        class ReplaceSegment:
            def translate(self, text):
                return "translated"

        for prefix in ("#", "@", "#12", "@Alice"):
            for selector in ("", "\ufe0f"):
                with self.subTest(prefix=prefix, selector=selector):
                    protected = prefix + "1" + selector + "\u20e3"
                    self.assertEqual(
                        preserve_structure("按 " + protected + "继续", ReplaceSegment()),
                        "translated " + protected + "translated",
                    )

    def test_plain_keycap_bases_stay_in_the_translatable_sentence(self):
        class ReplaceSegment:
            def translate(self, text):
                self.asserted_input = text
                return "translated"

        translator = ReplaceSegment()
        self.assertEqual(preserve_structure("第1次*尝试", translator), "translated")
        self.assertEqual(translator.asserted_input, "第1次*尝试")

    def test_numeric(self):
        self.assertEqual(preserve_structure("14:30 12345", UppercaseSegment()), "14:30 12345")

    def test_bad_output(self):
        class Broken:
            def translate(self, text):
                return "\ud800"

        with self.assertRaises(EngineError):
            preserve_structure("hello", Broken())

    def test_manual_exact_prefix(self):
        self.assertEqual(merge_manual(" My choice. ", "Added."), " My choice. Added.")

    def test_manual_space(self):
        self.assertEqual(merge_manual("Hello", "World"), "Hello World")

    def test_manual_limit(self):
        with self.assertRaises(EngineError):
            merge_manual("x" * 8191, "xx")

    def test_manual_invalid(self):
        with self.assertRaises(EngineError):
            merge_manual("", "Hello")
