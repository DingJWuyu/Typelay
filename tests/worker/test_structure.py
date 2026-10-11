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
