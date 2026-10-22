"""Production framing/ownership contracts with a native boundary double, not model quality."""

import ctypes
import hashlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import run_worker
from typelay_worker import catalog, hy, hy_assets, hy_cpu_windows
from typelay_worker.protocol import read_frame, write_frame

EXPECTED_DIRECTIONS = [
    "zh_ja",
    "ja_zh",
    "zh_ko",
    "ko_zh",
    "zh_fr",
    "fr_zh",
    "zh_es",
    "es_zh",
    "zh_de",
    "de_zh",
    "zh_pt",
    "pt_zh",
]


class NativeFunction:
    def __init__(self, call):
        self.call = call

    def __call__(self, *arguments):
        return self.call(*arguments)


class HyContractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = catalog.load_package_catalog().packages[2]
        directory = self.root / "packages" / self.package.id / self.package.version
        directory.mkdir(parents=True)
        files = []
        for name in ("Hy-MT2-1.8B-Q4_K_M.gguf", "model_LICENSE.txt"):
            (directory / name).write_bytes(b"native-boundary-fixture-not-a-model")
            files.append(
                {
                    "path": name,
                    "sha256": hashlib.sha256((directory / name).read_bytes()).hexdigest(),
                }
            )
        self.inventory = {
            "schema": 3,
            "packages": [
                {
                    "id": self.package.id,
                    "version": self.package.version,
                    "directions": ["zh_ja", "ja_zh"],
                    "files": files,
                }
            ],
            "activePackages": [self.package.id],
        }
        self.opened, self.closed, self.calls, self.loader_closed = 0, 0, [], 0
        self.styles = []
        self.terms = []
        self.output_text = "訳文"
        self.code, self.eog, self.after_decode = 0, 120020, None
        self.fail_open = self.fail_loaded = False
        self.clock = 0

        def opened(_cpu, _model, output, _error, _capacity):
            self.opened += 1
            if self.fail_open:
                return 8
            ctypes.cast(output, ctypes.POINTER(ctypes.c_void_p))[0] = 1
            return 0

        def closed(_handle):
            self.closed += 1

        def translated(
            _handle,
            direction,
            source,
            count,
            output,
            _capacity,
            length,
            metrics,
            cancel,
            _data,
            _error,
            _error_capacity,
        ):
            self.calls.append((direction, source[:count].decode("utf8")))
            if self.after_decode:
                self.after_decode()
            if cancel(None):
                return 6
            metric = ctypes.cast(metrics, ctypes.POINTER(hy.Metrics)).contents
            metric.eog_token = self.eog
            metric.roundtrip_ok, metric.prompt_tokens, metric.output_tokens = 1, 30, 2
            raw = (
                self.output_text(len(self.calls))
                if callable(self.output_text)
                else self.output_text
            ).encode()
            ctypes.memmove(output, raw, len(raw))
            ctypes.cast(length, ctypes.POINTER(ctypes.c_uint32))[0] = len(raw)
            return self.code

        def styled(handle, direction, style, *arguments):
            self.styles.append(style)
            return translated(handle, direction, *arguments)

        def glossary(handle, direction, terms, count, *arguments):
            self.terms.append(
                [
                    (terms[i].source_term.decode(), terms[i].target_term.decode())
                    for i in range(count)
                ]
            )
            return translated(handle, direction, *arguments)

        def combined(handle, direction, style, terms, count, *arguments):
            self.styles.append(style)
            return glossary(handle, direction, terms, count, *arguments)

        self.library = SimpleNamespace(
            hy_open_v5=NativeFunction(opened),
            hy_close=NativeFunction(closed),
            hy_translate=NativeFunction(translated),
            hy_translate_styled_v5=NativeFunction(styled),
            hy_translate_glossary_v5=NativeFunction(glossary),
            hy_translate_styled_glossary_v5=NativeFunction(combined),
        )
        owner = self

        class NativeRuntime:
            def __init__(self, _verified):
                pass

            def select_cpu_backend(self):
                return Path("fixture-cpu")

            def load_adapter(self):
                return owner.library

            def verify_loaded(self, _selected):
                if owner.fail_loaded:
                    raise RuntimeError("fixture loaded-closure mismatch")

            def close(self):
                owner.loader_closed += 1

        self.native = NativeRuntime
        self.verified = {"model_path": str(directory / files[0]["path"])}

    def run_worker(self, requests=(), *, self_check=False, detected="zh", platform="linux"):
        (self.root / "installed.json").write_text(json.dumps(self.inventory), encoding="utf8")
        incoming, outgoing = io.BytesIO(), io.BytesIO()
        for number, request in enumerate(({"op": "hello"}, *requests), 1):
            write_frame(incoming, {"id": number, **request})
        incoming.seek(0)
        builder = SimpleNamespace()
        builder.from_all_languages = lambda: builder
        builder.with_low_accuracy_mode = lambda: builder
        builder.build = lambda: SimpleNamespace(
            detect_language_of=lambda text: SimpleNamespace(
                iso_code_639_1=SimpleNamespace(
                    name=detected(text) if callable(detected) else detected
                )
            )
        )
        arguments = ["run_worker.py", "--models", str(self.root)] + (
            ["--self-check"] if self_check else []
        )
        with (
            patch.dict(sys.modules, {"lingua": SimpleNamespace(LanguageDetectorBuilder=builder)}),
            patch.object(hy, "LinuxRuntime", self.native),
            patch.object(hy_cpu_windows, "WindowsRuntime", self.native),
            patch.object(sys, "platform", platform),
            patch("typelay_worker.hy_assets.verify_production", return_value=self.verified),
            patch.object(sys, "argv", arguments),
            patch.object(sys, "stdin", SimpleNamespace(buffer=incoming)),
            patch("time.monotonic", side_effect=lambda: self.clock),
        ):
            status = run_worker.main(outgoing)
        if self_check:
            return status, json.loads(outgoing.getvalue())
        outgoing.seek(0)
        responses = []
        while outgoing.tell() < len(outgoing.getvalue()):
            responses.append(read_frame(outgoing))
        return status, responses

    def combined_request(self, **changes):
        return {
            "op": "translate-styled-glossary-v1",
            "text": "原文",
            "source": "zh",
            "target": "ja",
            "style": "formal",
            "glossary": {"version": 1, "rules": [{"sourceTerm": "原文", "targetTerm": "固定"}]},
            **changes,
        }

    def test_old_native_keeps_single_operations_and_rejects_combination(self):
        del self.library.hy_translate_styled_glossary_v5
        self.output_text = "固定"
        status, replies = self.run_worker(
            [
                self.combined_request(),
                {
                    "op": "translate-styled-v1",
                    "text": "原文",
                    "source": "zh",
                    "target": "ja",
                    "style": "formal",
                },
                {
                    k: v
                    for k, v in self.combined_request(op="translate-glossary-v1").items()
                    if k != "style"
                },
            ]
        )
        self.assertEqual(status, 0)
        self.assertNotIn("styledGlossaryTranslationVersion", replies[0])
        self.assertEqual(replies[1]["code"], "glossary-combination-unavailable")
        self.assertEqual([r["status"] for r in replies[2:]], ["translated", "translated"])
        self.assertEqual(len(self.calls), 2)

    def test_combined_frame_passes_style_terms_and_original_in_one_native_call(self):
        self.verified["styled_glossary"] = True
        self.output_text = "固定"
        status, replies = self.run_worker([self.combined_request()])
        self.assertEqual(status, 0)
        self.assertEqual(replies[0]["styledGlossaryTranslationVersion"], 1)
        self.assertEqual(replies[1]["text"], "固定")
        self.assertEqual(
            (self.calls, self.styles, self.terms), ([(1, "原文")], [2], [[("原文", "固定")]])
        )

    def test_combined_no_term_match_still_applies_style_and_protected_conflicts_refuse(self):
        self.verified["styled_glossary"] = True
        self.output_text = "译文"
        conflict = {
            "version": 1,
            "rules": [{"sourceTerm": "https://example.com", "targetTerm": "replacement"}],
        }
        status, replies = self.run_worker(
            [
                self.combined_request(text="另一句"),
                self.combined_request(text="原文 https://example.com", glossary=conflict),
            ]
        )
        self.assertEqual(status, 0)
        self.assertEqual(replies[1]["text"], "译文")
        self.assertEqual(replies[2]["code"], "glossary-protected-conflict")
        self.assertEqual((self.calls, self.styles, self.terms), ([(1, "另一句")], [2], []))

    def test_claimed_extension_without_actual_symbol_fails_before_model_open(self):
        self.verified["styled_glossary"] = True
        del self.library.hy_translate_styled_glossary_v5
        self.assertEqual(self.run_worker(), (2, []))
        self.assertEqual((self.opened, self.calls), (0, []))

    def test_combined_request_rejects_unknown_fields_and_missing_constraints(self):
        self.verified["styled_glossary"] = True
        requests = [
            self.combined_request(**change)
            for change in (
                {"style": None},
                {"style": "invented"},
                {"glossary": None},
                {"previousCandidate": "old"},
                {"terms": []},
            )
        ]
        status, replies = self.run_worker(requests)
        self.assertEqual(status, 0)
        self.assertTrue(all(r["status"] == "error" for r in replies[1:]))
        self.assertEqual(self.calls, [])

    def test_combined_translation_keeps_structure_and_rejects_missing_target(self):
        self.verified["styled_glossary"] = True
        self.output_text = "固定"
        status, replies = self.run_worker(
            [self.combined_request(text="原文\r\nhttps://example.com")]
        )
        self.assertEqual((status, replies[1]["text"]), (0, "固定\r\nhttps://example.com"))
        self.output_text = "不含指定译法"
        _, replies = self.run_worker([self.combined_request()])
        self.assertEqual(replies[1]["status"], "error")
        self.assertEqual(replies[1]["text"], "")

    def test_production_catalog_lock_and_binding_agree_on_twelve_fixed_directions(self):
        self.assertEqual([item.id for item in self.package.directions], EXPECTED_DIRECTIONS)
        model = json.loads((hy_assets.NATIVE_ROOT / "model.lock.json").read_text())
        self.assertEqual(model["directions"], EXPECTED_DIRECTIONS)
        self.assertEqual((model["abi"], hy_assets.ABI, hy.Metrics().abi_version), (5, 5, 5))
        self.assertEqual(hy.DIRECTIONS, dict(zip(EXPECTED_DIRECTIONS, range(1, 13))))
        self.assertEqual(
            hy_assets.EXPORTS,
            [
                "hy_close",
                "hy_open_v5",
                "hy_translate",
                "hy_translate_glossary_v5",
                "hy_translate_styled_v5",
            ],
        )

    def test_old_open_symbol_is_rejected_before_open_even_after_asset_verification(self):
        self.library.hy_open_v4 = self.library.hy_open_v5
        del self.library.hy_open_v5
        status, replies = self.run_worker()
        self.assertEqual((status, replies), (2, []))
        self.assertEqual((self.opened, self.closed, self.loader_closed), (0, 0, 1))

    def test_missing_styled_export_is_rejected_before_open(self):
        del self.library.hy_translate_styled_v5
        status, replies = self.run_worker()
        self.assertEqual((status, replies), (2, []))
        self.assertEqual((self.opened, self.closed, self.loader_closed), (0, 0, 1))

    def glossary_request(self, text, rules, **fields):
        return {
            "op": "translate-glossary-v1",
            "text": text,
            "source": "zh",
            "target": "ja",
            "glossary": {"version": 1, "rules": rules},
            **fields,
        }

    def test_glossary_auto_uses_actual_direction_and_only_whole_segment_matches(self):
        self.output_text = "訳文"
        rules = [
            {"sourceTerm": "甲", "targetTerm": "global"},
            {
                "sourceTerm": "甲",
                "targetTerm": "訳文",
                "sourceLanguage": "zh",
                "targetLanguage": "ja",
            },
            {"sourceTerm": "不存在", "targetTerm": "unused"},
        ]
        status, replies = self.run_worker([self.glossary_request("甲\n乙", rules, source="auto")])
        self.assertEqual(status, 0)
        self.assertEqual(replies[0]["glossaryTranslationVersion"], 1)
        self.assertEqual(replies[1]["text"], "訳文\n訳文")
        self.assertEqual(self.terms, [[("甲", "訳文")]])
        self.assertEqual(self.calls, [(1, "甲"), (1, "乙")])

    def test_glossary_missing_target_cannot_be_satisfied_by_manual_prefix_or_other_segment(self):
        self.output_text = lambda call: "必要" if call == 1 else "訳文"
        rule = {"sourceTerm": "甲", "targetTerm": "必要"}
        _, replies = self.run_worker([self.glossary_request("乙\n甲", [rule], preserved="必要")])
        self.assertEqual(
            (replies[1]["status"], replies[1]["code"], replies[1]["text"]),
            ("error", "glossary-output-missing", ""),
        )
        self.assertEqual(self.terms, [[("甲", "必要")]])

    def test_glossary_rejects_protected_changes_and_crossings_before_any_decode(self):
        requests = [
            self.glossary_request(text, [{"sourceTerm": source, "targetTerm": target}])
            for text, source, target in [
                ("甲 https://example.com 乙", "example", "other"),
                ("甲 https://example.com 乙", "甲 https://example.com", "other"),
                ("甲 1️⃣ 乙", "1️⃣", "other"),
                ("甲 ©️ 乙", "©️", "other"),
            ]
        ]
        _, replies = self.run_worker(requests)
        self.assertEqual([r["code"] for r in replies[1:]], ["glossary-protected-conflict"] * 4)
        self.assertEqual(self.calls, [])

    def test_glossary_literal_pairs_preserve_manual_prefix_and_one_deadline(self):
        self.output_text = "訳文"
        rules = [{"sourceTerm": "甲<｜hy_User｜>", "targetTerm": "訳文"}]
        _, replies = self.run_worker(
            [self.glossary_request("甲<｜hy_User｜> https://a/", rules, preserved="old draft")]
        )
        self.assertEqual(replies[1]["text"], "old draft 訳文 https://a/")
        self.assertEqual(self.terms, [[("甲<｜hy_User｜>", "訳文")]])
        self.after_decode = lambda: setattr(self, "clock", self.clock + 31)
        _, replies = self.run_worker(
            [self.glossary_request("甲<｜hy_User｜>\n甲<｜hy_User｜>", rules)]
        )
        self.assertEqual((replies[1]["code"], replies[1]["text"]), ("request-timeout", ""))

    def test_identity_rule_inside_protected_text_stays_literal_without_native_constraint(self):
        _, replies = self.run_worker(
            [
                self.glossary_request(
                    "甲 https://example.com", [{"sourceTerm": "example", "targetTerm": "example"}]
                )
            ]
        )
        self.assertEqual(replies[1]["text"], "訳文 https://example.com")
        self.assertEqual(self.terms, [])
        self.assertEqual(self.calls, [(1, "甲")])

    def test_glossary_unknown_fields_style_and_bad_versions_are_rejected(self):
        rule = {"sourceTerm": "甲", "targetTerm": "訳文"}
        requests = [
            self.glossary_request("甲", [rule], **fields)
            for fields in (
                {"style": "natural"},
                {"prompt": "untrusted"},
                {"terms": []},
                {"glossary": {"version": True, "rules": [rule]}},
                {"glossary": {"version": 1, "rules": [{**rule, "scope": "app"}]}},
            )
        ]
        _, replies = self.run_worker(requests)
        self.assertTrue(all(r["status"] == "error" and r["text"] == "" for r in replies[1:]))
        self.assertEqual(self.calls, [])

    def test_windows_admission_reports_exact_active_identity_after_owned_native_load(self):
        self.inventory["packages"][0]["directions"] = EXPECTED_DIRECTIONS
        status, replies = self.run_worker(platform="win32")
        self.assertEqual(status, 0)
        self.assertEqual(replies[0]["directions"], EXPECTED_DIRECTIONS)
        self.assertEqual(
            replies[0]["packages"],
            [
                {
                    "id": self.package.id,
                    "version": self.package.version,
                    "directions": EXPECTED_DIRECTIONS,
                }
            ],
        )
        self.assertEqual((self.opened, self.closed, self.loader_closed, self.calls), (1, 1, 1, []))

    def test_unknown_platform_refuses_all_twelve_before_loading_native(self):
        self.inventory["packages"][0]["directions"] = EXPECTED_DIRECTIONS
        status, replies = self.run_worker(platform="darwin")
        self.assertEqual((status, replies), (2, []))
        self.assertEqual((self.opened, self.closed, self.loader_closed, self.calls), (0, 0, 0, []))

    def test_twelve_directions_share_one_handle_and_use_the_fixed_native_numbers(self):
        self.inventory["packages"][0]["directions"] = EXPECTED_DIRECTIONS
        requests = [
            {
                "op": "translate",
                "text": identity[:2] + " 原始内容",
                "source": identity[:2],
                "target": identity[3:],
            }
            for identity in EXPECTED_DIRECTIONS
        ]
        status, replies = self.run_worker(requests, detected=lambda text: text[:2])
        self.assertEqual(status, 0)
        self.assertEqual(replies[0]["directions"], EXPECTED_DIRECTIONS)
        self.assertEqual([reply["status"] for reply in replies[1:]], ["translated"] * 12)
        self.assertEqual(
            self.calls,
            [
                (number, identity[:2] + " 原始内容")
                for number, identity in enumerate(EXPECTED_DIRECTIONS, 1)
            ],
        )
        self.assertEqual((self.opened, self.closed, self.loader_closed), (1, 1, 1))

    def test_mixed_old_and_new_subset_advertises_and_executes_only_its_exact_selection(self):
        self.inventory["packages"][0]["directions"] = ["pt_zh", "zh_ja"]
        status, replies = self.run_worker(
            [
                {"op": "translate", "text": "texto", "source": "pt", "target": "zh"},
                {"op": "translate", "text": "原文", "source": "zh", "target": "ko"},
            ],
            detected="pt",
        )
        self.assertEqual(status, 0)
        self.assertEqual(replies[0]["directions"], ["zh_ja", "pt_zh"])
        self.assertEqual(replies[0]["packages"][0]["directions"], ["zh_ja", "pt_zh"])
        self.assertEqual(replies[2]["code"], "language-unavailable")
        self.assertEqual(self.calls, [(12, "texto")])

    def test_hello_loads_one_handle_for_both_directions_and_performs_no_inference(self):
        status, replies = self.run_worker()
        self.assertEqual(status, 0)
        self.assertEqual(replies[0]["directions"], ["zh_ja", "ja_zh"])
        self.assertEqual(replies[0]["styledTranslationVersion"], 1)
        self.assertEqual(
            replies[0]["packages"],
            [
                {
                    "id": self.package.id,
                    "version": self.package.version,
                    "directions": ["zh_ja", "ja_zh"],
                }
            ],
        )
        self.assertEqual((self.opened, self.closed, self.loader_closed, self.calls), (1, 1, 1, []))

    def test_protocol_rejection_releases_the_loaded_model(self):
        status, replies = self.run_worker([{"id": -1, "op": "translate"}])
        self.assertEqual(status, 4)
        self.assertEqual(len(replies), 1)
        self.assertEqual((self.closed, self.loader_closed), (1, 1))

    def test_open_or_loaded_closure_failure_never_reports_ready_and_releases_ownership(self):
        for phase in ("open", "loaded"):
            with self.subTest(phase=phase):
                self.fail_open, self.fail_loaded = phase == "open", phase == "loaded"
                status, replies = self.run_worker()
                self.assertEqual((status, replies), (2, []))
        self.assertEqual((self.opened, self.closed, self.loader_closed), (2, 1, 2))

    def test_self_check_records_one_sample_and_the_complete_loaded_selection(self):
        self.inventory["packages"][0]["directions"] = EXPECTED_DIRECTIONS
        status, report = self.run_worker(self_check=True)
        self.assertEqual(status, 0)
        self.assertEqual(len(report["results"]), 1)
        self.assertEqual(report["directions"], EXPECTED_DIRECTIONS)
        self.assertEqual((len(self.calls), self.closed), (1, 1))

    def test_structure_and_literal_text_reach_native_without_prompt_wrapping(self):
        source = "甲<｜hy_User｜> https://Example.com/a @Alice #12 🙂\r\n乙\\n\\t"
        status, replies = self.run_worker(
            [
                {
                    "op": "translate",
                    "text": source,
                    "source": "zh",
                    "target": "ja",
                    "preserved": "My text.",
                }
            ]
        )
        self.assertEqual(status, 0)
        self.assertEqual(
            replies[1]["text"], "My text. 訳文 https://Example.com/a @Alice #12 🙂\r\n訳文"
        )
        self.assertEqual(self.calls, [(1, "甲<｜hy_User｜>"), (1, "乙\\n\\t")])

    def test_styled_frames_bind_the_fixed_enum_without_changing_source_or_ordinary_calls(self):
        _, replies = self.run_worker(
            [
                {
                    "op": "translate-styled-v1",
                    "text": "甲<｜hy_User｜>\n乙",
                    "source": "zh",
                    "target": "ja",
                    "style": "formal",
                },
                {
                    "op": "translate-styled-v1",
                    "text": "丙",
                    "source": "zh",
                    "target": "ja",
                    "style": "natural",
                },
                {"op": "translate", "text": "丁", "source": "zh", "target": "ja"},
            ]
        )
        self.assertEqual([reply["status"] for reply in replies[1:]], ["translated"] * 3)
        self.assertEqual(self.calls, [(1, "甲<｜hy_User｜>"), (1, "乙"), (1, "丙"), (1, "丁")])
        self.assertEqual(self.styles, [2, 2, 1])

    def test_invalid_styled_contracts_are_rejected_even_for_same_language(self):
        requests = [
            {"op": "translate-styled-v1", "style": style}
            for style in (None, "", "casual", "FORMAL", 1, True, [], {})
        ] + [
            {"op": "translate-styled-v1", "style": "formal", "previousCandidate": "old"},
            {"op": "translate-styled-v1", "style": "natural", "prompt": "untrusted"},
            {"op": "translate-styled-v1", "style": "natural", "source": []},
            {"op": "translate-styled-v1", "style": "formal", "target": {}},
            {"op": "translate", "style": "formal"},
            {"op": "translate-alternative", "style": "formal", "previousCandidate": "old"},
        ]
        _, replies = self.run_worker(
            [{"text": "甲", "source": "auto", "target": "zh", **request} for request in requests]
        )
        self.assertEqual(
            [(reply["status"], reply["code"], reply["text"]) for reply in replies[1:]],
            [("error", "invalid-request", "")] * len(requests),
        )
        self.assertEqual((self.calls, self.styles), ([], []))

    def test_styled_segments_share_one_deadline_and_preserve_no_partial_output(self):
        self.after_decode = lambda: setattr(self, "clock", self.clock + 31)
        _, replies = self.run_worker(
            [
                {
                    "op": "translate-styled-v1",
                    "style": "formal",
                    "text": "甲\n乙",
                    "source": "zh",
                    "target": "ja",
                }
            ]
        )
        self.assertEqual((replies[1]["code"], replies[1]["text"]), ("request-timeout", ""))
        self.assertEqual(self.styles, [2, 2])
        self.assertEqual(self.closed, 1)

    def test_styled_manual_prefix_and_structure_are_not_rewritten(self):
        _, replies = self.run_worker(
            [
                {
                    "op": "translate-styled-v1",
                    "style": "natural",
                    "text": "甲 https://Example.com/a\r\n乙",
                    "source": "zh",
                    "target": "ja",
                    "preserved": "My draft.",
                }
            ]
        )
        self.assertEqual(replies[1]["text"], "My draft. 訳文 https://Example.com/a\r\n訳文")
        self.assertEqual(self.calls, [(1, "甲"), (1, "乙")])
        self.assertEqual(self.styles, [1, 1])

    def test_whole_request_byte_cap_and_bare_cr_are_rejected_before_any_segment(self):
        for text, code in (
            ("甲" * 2731, "hy-input-too-long"),
            ("https://a/" + "甲" * 2731, "hy-input-too-long"),
            ("甲\r乙", "hy-literal-unsafe"),
        ):
            with self.subTest(code=code):
                _, replies = self.run_worker(
                    [
                        {"op": "translate", "text": text, "source": "zh", "target": "ja"},
                        {
                            "op": "translate-styled-v1",
                            "style": "formal",
                            "text": text,
                            "source": "zh",
                            "target": "ja",
                        },
                    ]
                )
                self.assertEqual(
                    [(reply["code"], reply["text"]) for reply in replies[1:]], [(code, "")] * 2
                )
        self.assertEqual(self.calls, [])

    def test_context_output_failure_or_missing_eog_never_returns_partial_text(self):
        for status, eog, code in (
            (4, 120020, "segment-too-long"),
            (5, 120020, "decoding-limit"),
            (0, -1, "hy-incomplete-output"),
        ):
            with self.subTest(code=code):
                self.code, self.eog = status, eog
                _, replies = self.run_worker(
                    [
                        {"op": "translate", "text": "甲", "source": "zh", "target": "ja"},
                        {
                            "op": "translate-styled-v1",
                            "style": "natural",
                            "text": "甲",
                            "source": "zh",
                            "target": "ja",
                        },
                    ]
                )
                self.assertEqual(
                    [(reply["code"], reply["text"]) for reply in replies[1:]], [(code, "")] * 2
                )

    def test_combined_segments_share_deadline_and_never_return_partial_output(self):
        self.verified["styled_glossary"] = True
        self.output_text = "固定"
        self.after_decode = lambda: setattr(self, "clock", self.clock + 31)
        _, replies = self.run_worker([self.combined_request(text="原文\n原文")])
        self.assertEqual((replies[1]["code"], replies[1]["text"]), ("request-timeout", ""))
        self.assertEqual((len(self.calls), self.styles, len(self.terms)), (2, [2, 2], 2))

    def test_all_segments_share_one_deadline_and_discard_earlier_segment_output(self):
        self.after_decode = lambda: setattr(self, "clock", self.clock + 31)
        _, replies = self.run_worker(
            [{"op": "translate", "text": "甲\n乙", "source": "zh", "target": "ja"}]
        )
        self.assertEqual((replies[1]["code"], replies[1]["text"]), ("request-timeout", ""))
        self.assertEqual(len(self.calls), 2)
        self.assertEqual(self.closed, 1)

    def test_single_direction_keeps_same_language_manual_append_before_capability_rejection(self):
        self.inventory["packages"][0]["directions"] = ["ja_zh"]
        _, replies = self.run_worker(
            [
                {
                    "op": "translate",
                    "text": "甲",
                    "source": "auto",
                    "target": "zh",
                    "preserved": "手写",
                },
                {"op": "translate", "text": "甲", "source": "zh", "target": "ja"},
            ]
        )
        self.assertEqual(replies[1]["text"], "手写 甲")
        self.assertEqual(replies[2]["code"], "language-unavailable")
        self.assertEqual(self.calls, [])
