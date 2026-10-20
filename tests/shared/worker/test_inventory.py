"""Deterministic protocol/structure tests. Does NOT substitute for actual model inference."""

import copy
import hashlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import run_worker
from typelay_worker import catalog
from typelay_worker.engine import EngineError, LocalEngine
from typelay_worker.protocol import read_frame, write_frame


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

    def save(self, **fields):
        (self.root / "installed.json").write_text(
            json.dumps({"schema": 1, "files": self.entries, **fields}), encoding="utf8"
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

    def test_incomplete_or_invalid_direction_claim_is_refused_before_model_loading(self):
        for directions in (
            None,
            [],
            ["zh_en"],
            ["en_zh"],
            ["zh_en", "fr_en"],
            ["zh_en", "en_zh", "zh_en"],
            ["zh_en", "zh_en"],
            "zh_en",
            [{"id": "zh_en"}],
        ):
            with self.subTest(directions=directions):
                self.save(directions=directions)
                with self.assertRaisesRegex(EngineError, "^model-inventory$"):
                    LocalEngine(self.root)

    def test_missing_or_unknown_legacy_schema_never_loads_or_advertises_directions(self):
        for fields in ({}, {"schema": 2}, {"schema": 3}, {"schema": True}):
            with self.subTest(fields=fields):
                (self.root / "installed.json").write_text(
                    json.dumps({"files": self.entries, **fields}), encoding="utf8"
                )
                result, response, loaded = self.start_worker()
                self.assertEqual(result, 2)
                self.assertEqual(loaded, [])
                self.assertIsNone(response)

    def test_complete_schema3_marker_cannot_load_or_advertise_legacy_directions(self):
        self.save(
            schema=3,
            packages=[
                {
                    "id": "argos-zh-en",
                    "version": "1.9",
                    "directions": ["zh_en"],
                    "files": manifest(),
                },
                {
                    "id": "argos-en-zh",
                    "version": "1.9",
                    "directions": ["en_zh"],
                    "files": manifest(),
                },
            ],
        )
        self.assertEqual(self.start_worker(), (2, None, []))

    def schema3(self, active=("argos-zh-en",)):
        packages = []
        for identity, direction in (("argos-zh-en", "zh_en"), ("argos-en-zh", "en_zh")):
            root = self.root / "packages" / identity / "1.9"
            shutil.copytree(self.root / direction, root, dirs_exist_ok=True)
            for name, content in (
                (
                    "metadata.json",
                    json.dumps(
                        {
                            "from_code": direction[:2],
                            "to_code": direction[3:],
                            "package_version": "1.9",
                        }
                    ),
                ),
                ("model/config.json", "{}"),
                ("model/shared_vocabulary.json", "[]"),
            ):
                (root / name).write_text(content, encoding="utf8")
            packages.append(
                {
                    "id": identity,
                    "version": "1.9",
                    "directions": [direction],
                    "files": [
                        {
                            "path": path.relative_to(root).as_posix(),
                            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                        }
                        for path in root.rglob("*")
                        if path.is_file()
                    ],
                }
            )
        value = {"schema": 3, "packages": packages, "activePackages": list(active)}
        (self.root / "installed.json").write_text(json.dumps(value), encoding="utf8")
        return value

    def test_schema3_loads_only_active_inventory_and_reports_its_package(self):
        self.schema3()
        shutil.rmtree(self.root / "packages/argos-en-zh")
        result, response, loaded = self.start_worker()
        self.assertEqual((result, loaded), (0, ["zh_en"]))
        self.assertEqual(response["directions"], ["zh_en"])
        self.assertNotIn("styledTranslationVersion", response)
        self.assertEqual(
            response["packages"], [{"id": "argos-zh-en", "version": "1.9", "directions": ["zh_en"]}]
        )

    def test_schema3_bad_active_or_inactive_manifest_never_loads_any_model(self):
        for active in (None, [], ["argos-zh-en", "argos-zh-en"], ["unknown"], [1]):
            with self.subTest(active=active):
                value = self.schema3()
                if active is None:
                    del value["activePackages"]
                else:
                    value["activePackages"] = active
                (self.root / "installed.json").write_text(json.dumps(value), encoding="utf8")
                self.assertEqual(self.start_worker(), (2, None, []))
        for path in (
            "../escape",
            "model/../model.bin",
            "/absolute",
            "C:/file",
            "model\\file",
            "model/file.",
        ):
            with self.subTest(path=path):
                value = self.schema3()
                value["packages"][1]["files"][0]["path"] = path
                (self.root / "installed.json").write_text(json.dumps(value), encoding="utf8")
                self.assertEqual(self.start_worker(), (2, None, []))

    def test_schema3_refuses_linked_package_files_even_with_matching_hashes(self):
        for link in ("hard", "symbolic"):
            with self.subTest(link=link):
                self.schema3()
                source = self.root / "zh_en/model/model.bin"
                target = self.root / "packages/argos-zh-en/1.9/model/model.bin"
                target.unlink()
                if link == "hard":
                    os.link(source, target)
                else:
                    target.symlink_to(source)
                try:
                    self.assertEqual(self.start_worker(), (2, None, []))
                finally:
                    shutil.rmtree(self.root / "packages")

    def test_schema3_inactive_hy_is_not_scanned_or_verified_until_activated(self):
        value = self.schema3()
        package = catalog.load_package_catalog().packages[2]
        value["packages"].append(
            {
                "id": package.id,
                "version": package.version,
                "directions": ["zh_ja"],
                "files": [{"path": "missing.gguf", "sha256": "0" * 64}],
            }
        )
        (self.root / "installed.json").write_text(json.dumps(value), encoding="utf8")
        with patch(
            "typelay_worker.hy_assets.verify_production",
            side_effect=AssertionError("inactive Hy was touched"),
        ):
            result, response, loaded = self.start_worker()
        self.assertEqual((result, loaded, response["directions"]), (0, ["zh_en"], ["zh_en"]))
        value["activePackages"] = [package.id]
        (self.root / "installed.json").write_text(json.dumps(value), encoding="utf8")
        self.assertEqual(self.start_worker(), (2, None, []))
        value["activePackages"] = ["argos-zh-en", package.id]
        (self.root / "installed.json").write_text(json.dumps(value), encoding="utf8")
        self.assertEqual(self.start_worker(), (2, None, []))

    def test_schema3_checks_every_active_package_before_the_first_load(self):
        for fault in ("hash", "extra", "metadata", "missing"):
            with self.subTest(fault=fault):
                value = self.schema3(("argos-zh-en", "argos-en-zh"))
                root = self.root / "packages/argos-en-zh/1.9"
                if fault == "hash":
                    (root / "model/model.bin").write_bytes(b"tampered")
                elif fault == "extra":
                    (root / "unexpected").write_bytes(b"extra")
                elif fault == "missing":
                    (root / "model/shared_vocabulary.json").unlink()
                    value["packages"][1]["files"] = [
                        entry
                        for entry in value["packages"][1]["files"]
                        if entry["path"] != "model/shared_vocabulary.json"
                    ]
                else:
                    path = root / "metadata.json"
                    path.write_text(
                        json.dumps({"from_code": "zh", "to_code": "en", "package_version": "1.9"})
                    )
                    next(
                        entry
                        for entry in value["packages"][1]["files"]
                        if entry["path"] == "metadata.json"
                    )["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
                (self.root / "installed.json").write_text(json.dumps(value), encoding="utf8")
                self.assertEqual(self.start_worker(), (2, None, []))
                shutil.rmtree(self.root / "packages")

    def test_schema2_invalid_selections_never_load_or_advertise_directions(self):
        for selected in (None, [], ["zh_en", "zh_en"], ["fr_en"], ["zh_en", None], "zh_en"):
            with self.subTest(selected=selected):
                self.save(schema=2, directions=selected)
                result, response, loaded = self.start_worker()
                self.assertEqual((result, response, loaded), (2, None, []))

    def test_schema2_cannot_claim_files_from_an_unselected_direction(self):
        self.save(schema=2, directions=["zh_en"])
        result, response, loaded = self.start_worker()
        self.assertEqual((result, response, loaded), (2, None, []))

    def test_schema2_cannot_load_an_uninventoried_model_file(self):
        (self.root / "zh_en/model/config.json").write_text("{}", encoding="utf8")
        self.save(schema=2, directions=["zh_en", "en_zh"])
        result, response, loaded = self.start_worker()
        self.assertEqual((result, response, loaded), (2, None, []))

    def test_schema2_rejects_a_noncanonical_alias_even_when_its_hash_matches(self):
        self.entries[0]["path"] = "zh_en/model/../model/model.bin"
        self.save(schema=2, directions=["zh_en", "en_zh"])
        result, response, loaded = self.start_worker()
        self.assertEqual((result, response, loaded), (2, None, []))

    def test_schema1_hello_advertises_the_complete_bundle_only_after_both_native_loads(self):
        for fields in ({}, {"directions": ["en_zh", "zh_en"]}):
            with self.subTest(fields=fields):
                self.save(**fields)
                result, response, loaded = self.start_worker()
                self.assertEqual(result, 0)
                self.assertEqual(loaded, ["zh_en", "en_zh"])
                self.assertEqual(response["status"], "ready")
                self.assertNotIn("packages", response)
                self.assertNotIn("styledTranslationVersion", response)
                self.assertEqual(response["directions"], ["zh_en", "en_zh"])

    def test_schema2_loads_and_advertises_only_its_nonempty_selection(self):
        entries = self.entries
        for selected in (["zh_en"], ["en_zh"], ["en_zh", "zh_en"]):
            with self.subTest(selected=selected):
                self.entries = [item for item in entries if item["path"].split("/")[0] in selected]
                self.save(schema=2, directions=selected)
                result, response, loaded = self.start_worker()
                self.assertEqual(result, 0)
                expected = ["zh_en", "en_zh"] if len(selected) == 2 else selected
                self.assertEqual(loaded, expected)
                self.assertEqual(response["status"], "ready")
                self.assertEqual(response["directions"], expected)
                self.assertNotIn("styledTranslationVersion", response)

    def test_second_native_model_load_failure_never_advertises_any_direction(self):
        for schema in (1, 2):
            with self.subTest(schema=schema):
                self.save(schema=schema, directions=["zh_en", "en_zh"])
                result, response, loaded = self.start_worker(fail_load="en_zh")
                self.assertEqual(result, 2)
                self.assertEqual(loaded, ["zh_en", "en_zh"])
                self.assertIsNone(response)

    def test_tampered_inventory_never_loads_a_model_or_advertises_directions(self):
        (self.root / self.entries[0]["path"]).write_bytes(b"changed")
        for fields in ({}, {"schema": 2, "directions": ["zh_en", "en_zh"]}):
            with self.subTest(fields=fields):
                self.save(**fields)
                result, response, loaded = self.start_worker()
                self.assertEqual(result, 2)
                self.assertEqual(loaded, [])
                self.assertIsNone(response)

    def test_schema2_selfcheck_reports_only_the_loaded_selection(self):
        self.entries = [item for item in self.entries if item["path"].startswith("en_zh/")]
        self.save(schema=2, directions=["en_zh"])
        result, response, loaded = self.start_worker(self_check=True)
        self.assertEqual(result, 0)
        self.assertEqual(loaded, ["en_zh"])
        self.assertEqual(response["status"], "passed")
        self.assertEqual([item["direction"] for item in response["results"]], ["en_zh"])

    def test_subset_requests_keep_detection_same_language_and_manual_append_before_missing_pair(
        self,
    ):
        self.entries = [item for item in self.entries if item["path"].startswith("en_zh/")]
        self.save(schema=2, directions=["en_zh"])
        cases = (
            ("English text", "auto", "en", None, "en", "no-translation", "", ""),
            ("English text", "zh", "en", None, "en", "no-translation", "", ""),
            (
                "English text",
                "auto",
                "en",
                "Manual.",
                "en",
                "translated",
                "Manual. English text",
                "",
            ),
            ("中文", "auto", "en", None, "en", "error", "", "language-unavailable"),
            ("English text", "auto", "zh", None, "en", "translated", "translated", ""),
            ("Bonjour", "auto", "en", None, "fr", "error", "", "language-unavailable"),
            ("???", "auto", "en", None, None, "error", "", "language-unavailable"),
            ("Bonjour", "fr", "en", None, "fr", "error", "", "invalid-request"),
        )
        for text, source, target, preserved, detected, status, output, code in cases:
            with self.subTest(source=source, target=target, detected=detected, preserved=preserved):
                request = dict(id=2, op="translate", text=text, source=source, target=target)
                if preserved is not None:
                    request["preserved"] = preserved
                result, responses, loaded = self.start_worker(request=request, detected=detected)
                self.assertEqual(result, 0)
                self.assertEqual(loaded, ["en_zh"])
                self.assertEqual(responses[0]["directions"], ["en_zh"])
                self.assertEqual(
                    (responses[1]["status"], responses[1]["text"], responses[1]["code"]),
                    (status, output, code),
                )

    def start_worker(self, fail_load=None, *, request=None, detected="en", self_check=False):
        # Control native dependency constructors at their external API, never replace
        # the engine. This proves readiness ordering and protocol, not real inference.
        loaded = []

        def load_model(path, **options):
            pair = Path(path).parent.name
            if pair == "1.9":
                pair = {"argos-zh-en": "zh_en", "argos-en-zh": "en_zh"}[
                    Path(path).parent.parent.name
                ]
            loaded.append(pair)
            self.assertEqual(options["device"], "cpu")
            self.assertEqual(options["compute_type"], "int8")
            if pair == fail_load:
                raise RuntimeError("private native failure")
            return SimpleNamespace(
                translate_batch=lambda *_args, **_options: [
                    SimpleNamespace(hypotheses=[["translated"]])
                ]
            )

        builder = SimpleNamespace()
        builder.from_all_languages = lambda: builder
        builder.with_low_accuracy_mode = lambda: builder
        builder.build = lambda: SimpleNamespace(
            detect_language_of=lambda _: SimpleNamespace(
                iso_code_639_1=SimpleNamespace(name=detected)
            )
            if detected
            else None
        )
        dependencies = {
            "lingua": SimpleNamespace(LanguageDetectorBuilder=builder),
            "sentencepiece": SimpleNamespace(
                SentencePieceProcessor=lambda **_: SimpleNamespace(
                    encode=lambda text, **_: text.split()
                )
            ),
            "ctranslate2": SimpleNamespace(Translator=load_model),
        }
        incoming, outgoing = io.BytesIO(), io.BytesIO()
        write_frame(incoming, {"id": 1, "op": "hello"})
        if request is not None:
            write_frame(incoming, request)
        incoming.seek(0)
        stdout = io.StringIO() if self_check else SimpleNamespace(buffer=outgoing)
        arguments = ["run_worker.py", "--models", str(self.root)]
        if self_check:
            arguments.append("--self-check")
        with (
            patch.dict(sys.modules, dependencies),
            patch.object(sys, "argv", arguments),
            patch.object(sys, "stdin", SimpleNamespace(buffer=incoming)),
            patch.object(sys, "stdout", stdout),
        ):
            result = run_worker.main()
        if self_check:
            return result, json.loads(stdout.getvalue()), loaded
        outgoing.seek(0)
        if request is not None:
            responses = [read_frame(outgoing), read_frame(outgoing)] if outgoing.getvalue() else []
            return result, responses, loaded
        return result, read_frame(outgoing) if outgoing.getvalue() else None, loaded


def manifest():
    return [{"path": "fixture.bin", "sha256": "0" * 64}]


class PackageCatalogTests(unittest.TestCase):
    def test_bundled_packages_preserve_legacy_fields_and_order(self):
        packages = catalog.load_package_catalog()
        self.assertEqual(
            [package.id for package in packages.packages[:2]], ["argos-zh-en", "argos-en-zh"]
        )
        self.assertEqual([package.backend for package in packages.packages[:2]], ["argos", "argos"])
        self.assertEqual([package.preview for package in packages.packages[:2]], [False, False])
        self.assertEqual(
            [direction.id for direction in packages.legacy_directions], ["zh_en", "en_zh"]
        )
        self.assertEqual(packages.default_directions, packages.legacy_directions)
        self.assertEqual(catalog.load_catalog(), packages.legacy_directions)
        self.assertEqual(
            packages.legacy_directions[0],
            catalog.ModelDirection(
                "zh_en",
                "zh",
                "en",
                "中文",
                "English",
                "1.9",
                "https://argos-net.com/v1/translate-zh_en-1_9.argosmodel",
                "你好，我想确认一下明天下午的会议时间。",
            ),
        )

        self.assertEqual(
            packages.legacy_directions[1],
            catalog.ModelDirection(
                "en_zh",
                "en",
                "zh",
                "English",
                "中文",
                "1.9",
                "https://argos-net.com/v1/translate-en_zh-1_9.argosmodel",
                "Please send me the final version.",
            ),
        )

    def test_installation_selection_normalizes_only_the_requested_legacy_packages(self):
        for inventory, expected in (
            ({"schema": 1}, ["argos-zh-en", "argos-en-zh"]),
            ({"schema": 1, "directions": ["en_zh", "zh_en"]}, ["argos-zh-en", "argos-en-zh"]),
            ({"schema": 2, "directions": ["en_zh"], "files": manifest()}, ["argos-en-zh"]),
            (
                {
                    "schema": 3,
                    "packages": [
                        {
                            "id": "argos-en-zh",
                            "version": "1.9",
                            "directions": ["en_zh"],
                            "files": manifest(),
                        }
                    ],
                },
                ["argos-en-zh"],
            ),
        ):
            with self.subTest(inventory=inventory):
                selection = catalog.read_installation_selection(inventory)
                self.assertEqual([item.package.id for item in selection], expected)

    def preview_catalog(self):
        value = json.loads(
            (Path(run_worker.__file__).parent / "model-catalog.json").read_text(encoding="utf8")
        )
        value["packages"] = value["packages"][:2]
        pairs = (
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
        )
        value["packages"].append(
            {
                "id": "hy-mt-preview",
                "version": "1.5",
                "backend": "hy-mt-llama-cpu",
                "preview": True,
                "url": "https://example.invalid/hy-test-only.gguf",
                "directions": [
                    {
                        "id": pair,
                        "source": pair[:2],
                        "target": pair[3:],
                        "sourceName": pair[:2],
                        "targetName": pair[3:],
                        "selfCheckText": "Test only.",
                    }
                    for pair in pairs
                ],
            }
        )
        return value

    def load(self, value):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "model-catalog.json"
            path.write_text(json.dumps(value), encoding="utf8")
            return catalog.load_package_catalog(path), catalog.load_catalog(path)

    def test_preview_package_has_twelve_direct_mappings_without_expanding_legacy_defaults(self):
        packages, legacy = self.load(self.preview_catalog())
        preview = packages.packages[2]
        self.assertTrue(preview.preview)
        self.assertEqual(preview.backend, "hy-mt-llama-cpu")
        self.assertEqual(len(preview.directions), 12)
        self.assertEqual([item.id for item in legacy], ["zh_en", "en_zh"])
        self.assertEqual(packages.default_directions, legacy)
        selected = catalog.read_installation_selection(
            {
                "schema": 3,
                "packages": [
                    {
                        "id": "hy-mt-preview",
                        "version": "1.5",
                        "directions": [item.id for item in reversed(preview.directions)],
                        "files": manifest(),
                    }
                ],
            },
            packages,
        )
        self.assertEqual(len(selected), 1)
        self.assertEqual(selected[0].package, preview)
        self.assertEqual(selected[0].directions, preview.directions)
        selected = catalog.read_installation_selection(
            {
                "schema": 3,
                "packages": [
                    {
                        "id": "hy-mt-preview",
                        "version": "1.5",
                        "directions": ["pt_zh", "zh_ja"],
                        "files": manifest(),
                    },
                    {
                        "id": "argos-en-zh",
                        "version": "1.9",
                        "directions": ["en_zh"],
                        "files": manifest(),
                    },
                ],
            },
            packages,
        )
        self.assertEqual([item.package.id for item in selected], ["argos-en-zh", "hy-mt-preview"])
        self.assertEqual([item.id for item in selected[1].directions], ["zh_ja", "pt_zh"])
        selected = catalog.read_installation_selection({"schema": 1}, packages)
        self.assertEqual([item.package.id for item in selected], ["argos-zh-en", "argos-en-zh"])
        for schema in (1, 2):
            with self.assertRaisesRegex(ValueError, "^model-inventory$"):
                catalog.read_installation_selection(
                    {"schema": schema, "directions": ["zh_ja"]}, packages
                )

    def test_invalid_package_catalogs_are_rejected(self):
        valid = self.preview_catalog()
        invalid = [
            None,
            [],
            {},
            {"schema": 1, "packages": valid["packages"]},
            {"schema": True, "packages": valid["packages"]},
            {"schema": 2},
            {"schema": 2, "packages": []},
            {"schema": 2, "packages": {}},
            {**valid, "directions": []},
            {**valid, "packages": [None]},
            {**valid, "packages": [valid["packages"][0], valid["packages"][0]]},
        ]
        for field in ("id", "version", "backend", "preview", "url", "directions"):
            value = copy.deepcopy(valid)
            del value["packages"][0][field]
            invalid.append(value)
            for replacement in (None, 5, {}):
                value = copy.deepcopy(valid)
                value["packages"][0][field] = replacement
                invalid.append(value)
        for field, replacement in (
            ("id", "bad/id"),
            ("backend", "bad.module"),
            ("preview", "false"),
            ("version", "1"),
            ("url", "http://argos-net.com/model"),
            ("url", "https://other.invalid/model"),
            ("url", "https://user@argos-net.com/model"),
            ("directions", []),
        ):
            value = copy.deepcopy(valid)
            value["packages"][0][field] = replacement
            invalid.append(value)
        for field, replacement in (
            ("id", "1package"),
            ("id", "a" * 65),
            ("backend", "unknown"),
            ("version", " "),
            ("version", "bad/version"),
            ("version", "a" * 129),
            ("url", "https://example.invalid:444/model"),
            ("url", "https://example.invalid/model?key=value"),
            ("url", "https://example.invalid/model#fragment"),
        ):
            value = copy.deepcopy(valid)
            value["packages"][2][field] = replacement
            invalid.append(value)
        direction = valid["packages"][0]["directions"][0]
        for field in direction:
            value = copy.deepcopy(valid)
            del value["packages"][0]["directions"][0][field]
            invalid.append(value)
        for field, replacement in (
            ("source", "ZH"),
            ("target", "zh"),
            ("id", "en_zh"),
            ("sourceName", ""),
            ("targetName", 1),
            ("selfCheckText", None),
            ("version", "9.9"),
            ("url", "https://example.invalid"),
        ):
            value = copy.deepcopy(valid)
            value["packages"][0]["directions"][0][field] = replacement
            invalid.append(value)
        value = copy.deepcopy(valid)
        value["packages"][0]["directions"].append(direction)
        invalid.append(value)
        for value in invalid:
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "^model-catalog$"):
                self.load(value)

    def test_invalid_installation_intent_never_drops_package_identity(self):
        value = self.preview_catalog()
        value["packages"][2]["directions"].append(
            copy.deepcopy(value["packages"][0]["directions"][0])
        )
        packages, legacy = self.load(value)
        self.assertEqual(legacy[0].version, "1.9")
        argos = {
            "id": "argos-zh-en",
            "version": "1.9",
            "directions": ["zh_en"],
            "files": manifest(),
        }
        invalid = [
            None,
            [],
            {},
            {"schema": True},
            {"schema": 0},
            {"schema": 3},
            {"schema": 1, "directions": ["zh_en"], "files": manifest()},
            {"schema": 1, "directions": None},
            {"schema": 1, "packages": [argos]},
            {"schema": 2},
            {"schema": 3, "packages": [argos], "directions": ["zh_en"], "files": manifest()},
            {"schema": 3, "packages": [argos, argos]},
        ]
        for value in (None, [], {}, "packages", [None], ["argos-zh-en"]):
            invalid.append({"schema": 3, "packages": value})
        for field in ("id", "version", "directions", "files"):
            entry = dict(argos)
            del entry[field]
            invalid.append({"schema": 3, "packages": [entry]})
            for value in (None, 1, {}):
                invalid.append({"schema": 3, "packages": [{**argos, field: value}]})
        for field, value in (
            ("id", "unknown"),
            ("version", "1.8"),
            ("version", "1.9 "),
            ("directions", []),
            ("directions", "zh_en"),
            ("directions", [None]),
            ("directions", ["zh_en", "zh_en"]),
            ("directions", ["en_zh"]),
            ("directions", ["zh_ja"]),
            ("directions", ["fr_en"]),
        ):
            invalid.append({"schema": 3, "packages": [{**argos, field: value}]})
        for schema in (1, 2):
            for value in (None, [], "zh_en", [1], ["zh_en", "zh_en"], ["fr_en"]):
                invalid.append({"schema": schema, "directions": value})
        for value in invalid:
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "^model-inventory$"):
                catalog.read_installation_selection(value, packages)

    def test_active_selection_rejects_two_hy_packages_but_inventory_keeps_alternatives(self):
        value = self.preview_catalog()
        second = copy.deepcopy(value["packages"][2])
        second["id"] = "hy-second"
        value["packages"].append(second)
        packages, _ = self.load(value)
        inventory = {
            "schema": 3,
            "packages": [
                {"id": identity, "version": "1.5", "directions": ["zh_ja"], "files": manifest()}
                for identity in ("hy-mt-preview", "hy-second")
            ],
            "activePackages": ["hy-mt-preview", "hy-second"],
        }
        self.assertEqual(len(catalog.read_installation_selection(inventory, packages)), 2)
        with self.assertRaisesRegex(ValueError, "^model-inventory$"):
            catalog.read_active_selection(inventory, packages)
        inventory["activePackages"] = ["hy-second"]
        selected = catalog.read_active_selection(inventory, packages)
        self.assertEqual([item.package.id for item in selected], ["hy-second"])

    def test_overlapping_direction_keeps_the_explicit_provider_when_selected_alone(self):
        value = self.preview_catalog()
        value["packages"][2]["directions"].append(
            copy.deepcopy(value["packages"][0]["directions"][0])
        )
        packages, _ = self.load(value)
        selected = catalog.read_installation_selection(
            {
                "schema": 3,
                "packages": [
                    {
                        "id": "hy-mt-preview",
                        "version": "1.5",
                        "directions": ["zh_en"],
                        "files": manifest(),
                    }
                ],
            },
            packages,
        )
        self.assertEqual(selected[0].package.id, "hy-mt-preview")
        self.assertEqual(selected[0].directions[0].version, "1.5")
