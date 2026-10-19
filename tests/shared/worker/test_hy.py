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
from typelay_worker import catalog, hy, hy_assets
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
            raw = "訳文".encode()
            ctypes.memmove(output, raw, len(raw))
            ctypes.cast(length, ctypes.POINTER(ctypes.c_uint32))[0] = len(raw)
            return self.code

        self.library = SimpleNamespace(
            hy_open_v3=NativeFunction(opened),
            hy_close=NativeFunction(closed),
            hy_translate=NativeFunction(translated),
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

    def run_worker(self, requests=(), *, self_check=False, detected="zh"):
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
        stdout = io.StringIO() if self_check else SimpleNamespace(buffer=outgoing)
        arguments = ["run_worker.py", "--models", str(self.root)] + (
            ["--self-check"] if self_check else []
        )
        with (
            patch.dict(sys.modules, {"lingua": SimpleNamespace(LanguageDetectorBuilder=builder)}),
            patch.object(hy, "LinuxRuntime", self.native),
            patch("typelay_worker.hy_assets.verify_production", return_value=self.verified),
            patch.object(sys, "argv", arguments),
            patch.object(sys, "stdin", SimpleNamespace(buffer=incoming)),
            patch.object(sys, "stdout", stdout),
            patch("time.monotonic", side_effect=lambda: self.clock),
        ):
            status = run_worker.main()
        if self_check:
            return status, json.loads(stdout.getvalue())
        outgoing.seek(0)
        responses = []
        while outgoing.tell() < len(outgoing.getvalue()):
            responses.append(read_frame(outgoing))
        return status, responses

    def test_production_catalog_lock_and_binding_agree_on_twelve_fixed_directions(self):
        self.assertEqual([item.id for item in self.package.directions], EXPECTED_DIRECTIONS)
        model = json.loads((hy_assets.NATIVE_ROOT / "model.lock.json").read_text())
        self.assertEqual(model["directions"], EXPECTED_DIRECTIONS)
        self.assertEqual((model["abi"], hy_assets.ABI, hy.Metrics().abi_version), (3, 3, 3))
        self.assertEqual(hy.DIRECTIONS, dict(zip(EXPECTED_DIRECTIONS, range(1, 13))))
        self.assertEqual(hy_assets.EXPORTS, ["hy_close", "hy_open_v3", "hy_translate"])

    def test_old_open_symbol_is_rejected_before_open_even_after_asset_verification(self):
        self.library.hy_open = self.library.hy_open_v3
        del self.library.hy_open_v3
        status, replies = self.run_worker()
        self.assertEqual((status, replies), (2, []))
        self.assertEqual((self.opened, self.closed, self.loader_closed), (0, 0, 1))

    def test_windows_production_refuses_all_twelve_before_loading_native(self):
        self.inventory["packages"][0]["directions"] = EXPECTED_DIRECTIONS
        with patch.object(sys, "platform", "win32"):
            status, replies = self.run_worker()
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

    def test_whole_request_byte_cap_and_bare_cr_are_rejected_before_any_segment(self):
        for text, code in (
            ("甲" * 2731, "hy-input-too-long"),
            ("https://a/" + "甲" * 2731, "hy-input-too-long"),
            ("甲\r乙", "hy-literal-unsafe"),
        ):
            with self.subTest(code=code):
                _, replies = self.run_worker(
                    [{"op": "translate", "text": text, "source": "zh", "target": "ja"}]
                )
                self.assertEqual((replies[1]["code"], replies[1]["text"]), (code, ""))
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
                    [{"op": "translate", "text": "甲", "source": "zh", "target": "ja"}]
                )
                self.assertEqual((replies[1]["code"], replies[1]["text"]), (code, ""))

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
