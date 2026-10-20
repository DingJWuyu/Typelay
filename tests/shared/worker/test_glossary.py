"""Bounded glossary matching and real public frames, without model-quality claims."""

import unittest

from test_alternative import controlled_engine, exchange


class GlossaryProtocolTests(unittest.TestCase):
    def test_old_operations_reject_glossary_fields_even_when_null(self):
        with controlled_engine() as (engine, calls):
            for op in ("translate", "translate-styled-v1", "translate-alternative"):
                for field in ("glossary", "terms"):
                    with self.subTest(op=op, field=field):
                        status, reply = exchange(engine, op=op, **{field: None})
                        self.assertEqual(
                            (status, reply["status"], reply["code"]),
                            (0, "error", "invalid-request"),
                        )
            self.assertEqual(calls, [])


class GlossaryMatchingTests(unittest.TestCase):
    def test_direction_priority_case_boundaries_and_longest_overlap(self):
        from typelay_worker.glossary import parse_glossary, select_matches

        raw = {
            "version": 1,
            "rules": [
                {"sourceTerm": "PR", "targetTerm": "global"},
                {
                    "sourceTerm": "PR",
                    "targetTerm": "专属",
                    "sourceLanguage": "fr",
                    "targetLanguage": "zh",
                },
                {"sourceTerm": "项目", "targetTerm": "short"},
                {"sourceTerm": "项目方", "targetTerm": "long"},
            ],
        }
        rules = parse_glossary(raw, {("fr", "zh")})
        text = "PR APR PRé PR\u0301 _PR PR_ pr 项目方项目"
        hits = select_matches(text, rules, "fr", "zh")
        self.assertEqual(
            [(text[h.start : h.end], h.target) for h in hits],
            [("PR", "专属"), ("项目方", "long"), ("项目", "short")],
        )
        self.assertEqual(select_matches("PR", rules, "zh", "fr")[0].target, "global")

    def test_conflicting_rules_limits_and_duplicate_canonicalization(self):
        from typelay_worker.glossary import parse_glossary

        pair = {"sourceTerm": "alpha", "targetTerm": "beta"}
        self.assertEqual(len(parse_glossary({"version": 1, "rules": [pair, pair]}, set())), 1)
        for rules in (
            [pair, {**pair, "targetTerm": "other"}],
            [{**pair, "sourceTerm": "a" * 129}],
            [{**pair, "sourceTerm": "bad\nterm"}],
            [{**pair, "targetTerm": "\ud800"}],
            [pair] * 65,
        ):
            with self.subTest(rules=repr(rules)[:60]), self.assertRaises(ValueError):
                parse_glossary({"version": 1, "rules": rules}, set())

    def test_equal_length_overlap_is_leftmost_and_independent_of_rule_order(self):
        from typelay_worker.glossary import parse_glossary, select_matches

        rules = [
            {"sourceTerm": "甲乙", "targetTerm": "first"},
            {"sourceTerm": "乙丙", "targetTerm": "second"},
        ]
        for ordered in (rules, list(reversed(rules))):
            parsed = parse_glossary({"version": 1, "rules": ordered}, set())
            self.assertEqual(
                [(h.source, h.target) for h in select_matches("甲乙丙", parsed, "zh", "ja")],
                [("甲乙", "first")],
            )
