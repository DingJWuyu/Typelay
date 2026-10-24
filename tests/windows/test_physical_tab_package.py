"""File/result boundaries only; the relocated Windows smoke runs in the package step."""

import importlib.util
import json
import shutil
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "physical_tab_package", ROOT / "eng/windows/package-physical-tab.py"
)
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)


class PhysicalTabPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.home = Path(self.temporary.name)

    def test_copy_preserves_nested_runtime_files_and_refuses_links(self):
        source = self.home / "build"
        (source / "zh-Hans").mkdir(parents=True)
        (source / "zh-Hans/resources.dll").write_bytes(b"runtime resource")
        package.copy_tree(source, self.home / "relocated 中")
        self.assertEqual(
            (self.home / "relocated 中/zh-Hans/resources.dll").read_bytes(), b"runtime resource"
        )
        try:
            (source / "linked.dll").symlink_to(source / "zh-Hans/resources.dll")
        except OSError:
            self.skipTest("Creating a test symlink is unavailable; ordinary copy already checked")
        with self.assertRaises(ValueError):
            package.copy_tree(source, self.home / "refused")

    def test_manual_discovery_accepts_only_one_exact_unskipped_tab_trait(self):
        assembly = self.home / "TypeLay.App.Tests.dll"
        row = dict(
            Assembly=str(assembly),
            Class=package.CLASS,
            Method=package.MANUAL,
            Traits={"ManualPhysicalKey": ["Tab"]},
            DisplayName="not parsed",
            ID="not selected",
        )
        path = self.home / "discovery.json"
        path.write_text(json.dumps([row]), encoding="utf-8")
        package.verify_discovery(path, assembly)
        for rows in (
            [],
            [row, row],
            [dict(row, Skip="unavailable")],
            [dict(row, Traits={"ManualPhysicalKey": ["F24"]})],
            [dict(row, Method="Other")],
            [dict(row, Assembly="elsewhere.dll")],
        ):
            path.write_text(json.dumps(rows), encoding="utf-8")
            with self.subTest(rows=rows), self.assertRaises(ValueError):
                package.verify_discovery(path, assembly)

    def stage_input(self):
        root = self.home / "checkout"
        for relative, name in package.OUTPUTS.items():
            output = root / relative
            output.mkdir(parents=True)
            (output / f"{name}.dll").write_bytes(b"test output, not executable")
            (output / f"{name}.deps.json").write_text("{}", encoding="utf-8")
            (output / "TypeLay.NativeInput.dll").write_bytes(b"same native bytes")
            (output / f"{name}.runtimeconfig.json").write_text(
                json.dumps(
                    {
                        "runtimeOptions": {
                            "framework": {
                                "name": "Microsoft.WindowsDesktop.App",
                                "version": "10.0.0",
                            }
                        }
                    }
                ),
                encoding="utf-8",
            )
        (root / package.APP_OUTPUT / "TypeLay.App.Tests.exe").write_bytes(b"test exe placeholder")
        (root / "TypeLay.slnx").write_text("<Solution />", encoding="utf-8")
        (root / "private-user-data.txt").write_text("not distributable", encoding="utf-8")
        project = root / "tests/windows/TypeLay.App.Tests"
        (project / "obj").mkdir()
        (project / "obj/project.assets.json").write_text('{"packageFolders": {}}', encoding="utf-8")
        (project / "packages.lock.json").write_text(
            '{"dependencies": {"net10.0-windows7.0": {}}}', encoding="utf-8"
        )
        shutil.copytree(
            ROOT / "packaging/windows/physical-tab", root / "packaging/windows/physical-tab"
        )
        return root

    def test_stage_keeps_original_three_paths_but_not_rid_publish_or_user_data(self):
        root = self.stage_input()
        rid = root / "src/windows/TypeLay.Broker/bin/Release/net10.0-windows/win-x64"
        rid.mkdir()
        (rid / "coreclr.dll").write_bytes(b"self-contained product runtime")
        stage = self.home / "isolated bundle 中"
        package.stage_bundle(root, stage)
        self.assertEqual((stage / "TypeLay.slnx").read_bytes(), b"<Solution />")
        self.assertFalse((stage / "private-user-data.txt").exists())
        self.assertFalse((stage / rid.relative_to(root)).exists())
        for relative in package.OUTPUTS:
            self.assertEqual(
                (stage / relative / "TypeLay.NativeInput.dll").read_bytes(), b"same native bytes"
            )
        self.assertTrue((stage / "licenses/upstream/xunit/LICENSE").is_file())

    def test_stage_rejects_mixed_native_build_and_embedded_runtime(self):
        root = self.stage_input()
        native = root / package.APP_OUTPUT / "TypeLay.NativeInput.dll"
        native.write_bytes(b"different build")
        with self.assertRaises(ValueError):
            package.stage_bundle(root, self.home / "mixed")
        native.write_bytes(b"same native bytes")
        (native.parent / "coreclr.dll").write_bytes(b"unwanted runtime")
        with self.assertRaises(ValueError):
            package.stage_bundle(root, self.home / "embedded")

    def write_trx(self, outcome="Passed", executed=2, method=None):
        method = method or package.NEGATIVE
        path = self.home / "result.trx"
        path.write_text(
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            "<TestDefinitions>"
            + "".join(
                f'<UnitTest id="{i}"><TestMethod className="{package.CLASS}" '
                f'name="{method}" codeBase="C:\\bundle\\TypeLay.App.Tests.dll"/>'
                "</UnitTest>"
                for i in (1, 2)
            )
            + "</TestDefinitions><Results>"
            + "".join(f'<UnitTestResult testId="{i}" outcome="{outcome}"/>' for i in (1, 2))
            + '</Results><ResultSummary outcome="Completed"><Counters '
            f'total="2" executed="{executed}" passed="2" failed="0" error="0" '
            'notExecuted="0" notRunnable="0"/></ResultSummary></TestRun>',
            encoding="utf-8",
        )
        return path

    def test_trx_requires_exact_method_rows_and_execution_not_just_exit_zero(self):
        package.verify_smoke(self.write_trx())
        for changes in (dict(outcome="NotExecuted"), dict(executed=0), dict(method="Other")):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                package.verify_smoke(self.write_trx(**changes))

    def test_original_suite_admission_does_not_ignore_failed_or_missing_rows(self):
        self.write_trx()
        package.verify_original_suite(self.home)
        self.write_trx(outcome="Failed")
        with self.assertRaises(ValueError):
            package.verify_original_suite(self.home)
        (self.home / "result.trx").unlink()
        with self.assertRaises(ValueError):
            package.verify_original_suite(self.home)


if __name__ == "__main__":
    unittest.main()
