"""Exercise main-package finalization with real files and cooperation archives."""

import hashlib
import importlib.util
import io
import json
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
QT_TEST_INPUTS = (
    "tests/linux/qt_cooperative/run.py",
    "tests/linux/qt_cooperative/fixture.cpp",
    "tests/linux/qt_cooperative/qpa_ack.cpp",
    "tests/linux/fixtures/qt_native_fixture.cpp",
    "tests/linux/support/xtest.py",
)
sys.path.insert(0, str(ROOT))

from eng.linux import build  # noqa: E402
from eng.linux.cooperative import inputs  # noqa: E402


def digest(content):
    return hashlib.sha256(content).hexdigest()


class PackageReadmeTests(unittest.TestCase):
    def test_generated_readme_source_links_follow_this_build(self):
        for commit in ("a" * 40, "b" * 40):
            with self.subTest(commit=commit), tempfile.TemporaryDirectory() as directory:
                output = Path(directory)
                identity = {"schemaVersion": 1, "sourceCommit": commit, "sourceDirty": False}
                build.package_documentation(output, identity)
                readme = (output / "README.md").read_text()
                upstream = "https://github.com/DingJWuyu/Typelay/blob/"
                self.assertIn(
                    f"[Linux 开发说明]({upstream}{commit}/docs/platforms/linux/development.md#构建)",
                    readme,
                )
                for target in re.findall(r"\[[^\]]+\]\(([^)]+)\)", readme):
                    if target.startswith(upstream):
                        self.assertTrue(target.startswith(upstream + commit + "/"), target)

    def test_generated_readme_identifies_this_package_not_a_recommended_old_artifact(self):
        for dirty in (False, True):
            with self.subTest(dirty=dirty), tempfile.TemporaryDirectory() as directory:
                output = Path(directory)
                identity = {"schemaVersion": 1, "sourceCommit": "a" * 40, "sourceDirty": dirty}
                (output / "build-info.json").write_text(json.dumps(identity))
                build.package_documentation(output, identity)
                build.write_manifest(output)
                readme = (output / "README.md").read_text()
                for target in re.findall(r"\[[^\]]+\]\(([^)]+)\)", readme):
                    if not target.startswith("#") and ":" not in target:
                        self.assertTrue((output / target.partition("#")[0]).is_file(), target)
                self.assertIn("a" * 40, readme)
                self.assertIn("sourceDirty=" + str(dirty).lower(), readme)
                self.assertIn("[build-info.json](build-info.json)", readme)
                self.assertNotIn("/artifacts/", readme)
                self.assertNotIn("4be188b", readme)
                self.assertIn("## 安装、启动与首次配置", readme)
                self.assertIn("sha256sum", readme)
                self.assertIn("--install-models", readme)
                for name in ("browser.md", "fcitx.md", "qt.md", "vscode.md"):
                    self.assertIn("(" + name, readme)
                    self.assertEqual(
                        (output / name).read_bytes(),
                        (ROOT / "docs/platforms/linux" / name).read_bytes(),
                    )
                self.assertIn(
                    "[构建与检查记录](https://github.com/DingJWuyu/Typelay/actions/workflows/check.yml)",
                    readme,
                )
                self.assertNotIn("最新推荐与检查结果", readme)
                self.assertIn("/blob/" + "a" * 40 + "/docs/product/README.md", readme)
                self.assertNotIn("](..", readme)
                self.assertIn("不代表", readme)


class FcitxTestManifestTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location(
            "typelay_pack_manifest_test", ROOT / "packaging/linux/fcitx-cooperative/pack.py"
        )
        self.recipe = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.recipe)
        temporary = tempfile.TemporaryDirectory(prefix="typelay-fcitx-manifest-")
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.paths = {
            "fixture_sha256": "fixture",
            "gtk4_fixture_sha256": "gtk4-fixture",
            "build_inputs_sha256": "build-inputs.json",
            "build_metadata_sha256": "versions.json",
        }
        for relative in self.paths.values():
            (self.directory / relative).write_bytes(b"real inert fixture bytes\n")
        self.expected = inputs.FCITX_TEST_INPUTS
        self.record = {
            "files": inputs.hashes(self.expected),
            **{key: inputs.sha256(self.directory / path) for key, path in self.paths.items()},
        }

    def write_record(self):
        (self.directory / "test-inputs.json").write_text(json.dumps(self.record))

    def test_current_regression_manifest_is_accepted(self):
        self.write_record()
        self.assertEqual(self.recipe.verify_tests(self.directory), self.record)

    def test_old_partial_and_changed_regression_inputs_are_rejected(self):
        original = dict(self.record["files"])
        for files in (
            {key: original[key] for key in self.expected[:2]},
            {key: value for key, value in original.items() if key != self.expected[-1]},
            {**original, self.expected[2]: "0" * 64},
        ):
            with self.subTest(files=files):
                self.record["files"] = files
                self.write_record()
                with self.assertRaisesRegex(ValueError, "fixture inputs differ"):
                    self.recipe.verify_tests(self.directory)

    def test_each_compiled_fixture_and_build_record_remains_bound(self):
        self.write_record()
        for relative in self.paths.values():
            with self.subTest(relative=relative):
                path = self.directory / relative
                original = path.read_bytes()
                path.write_bytes(original + b"changed")
                with self.assertRaisesRegex(ValueError, "changed after fixture compilation"):
                    self.recipe.verify_tests(self.directory)
                path.write_bytes(original)

    def test_qt_probe_rejects_missing_stale_or_unbound_fcitx_inputs(self):
        from qt_cooperative.run import verify_fcitx_inputs

        versions = {
            **dict.fromkeys(
                (
                    "server_source_version",
                    "server_runtime_version",
                    "linked_core_development_version",
                ),
                inputs.CORE_VERSION,
            ),
            "component_source_versions": inputs.SOURCE_VERSIONS,
            "installed_private_versions": inputs.PRIVATE_VERSIONS,
            "server_source_commit": inputs.FCITX_PINS["fcitx5"][0],
        }
        (self.directory / "pins.json").write_text(json.dumps(inputs.FCITX_PINS))
        (self.directory / "versions.json").write_text(json.dumps(versions))
        self.record["build_metadata_sha256"] = inputs.sha256(self.directory / "versions.json")
        with self.assertRaisesRegex(ValueError, "requires compiled Fcitx regression inputs"):
            verify_fcitx_inputs(self.directory)
        self.record["files"][self.expected[0]] = "0" * 64
        self.write_record()
        with self.assertRaisesRegex(ValueError, "fixture inputs differ"):
            verify_fcitx_inputs(self.directory)
        self.record["files"] = inputs.hashes(self.expected)
        (self.directory / "build-inputs.json").write_text(json.dumps({"files": {}}))
        self.record["build_inputs_sha256"] = inputs.sha256(self.directory / "build-inputs.json")
        self.write_record()
        with self.assertRaisesRegex(ValueError, "identify every cooperation build input"):
            verify_fcitx_inputs(self.directory)
        versions["server_runtime_version"] = "unknown"
        (self.directory / "versions.json").write_text(json.dumps(versions))
        with self.assertRaisesRegex(ValueError, "server_runtime_version must be"):
            verify_fcitx_inputs(self.directory)


class WorkerBundleTests(unittest.TestCase):
    def test_argos_only_worker_has_runtime_python_and_locks_without_native_build_inputs(self):
        with tempfile.TemporaryDirectory(prefix="typelay-worker-") as directory:
            package = Path(directory) / "发行 包"
            build.package_worker(package)
            worker = package / "worker"
            for name in (
                "run_worker.py",
                "model-catalog.json",
                "requirements.txt",
                "typelay_worker/hy_assets.py",
                "typelay_worker/hy_cpu.py",
                "typelay_worker/engine.py",
                "typelay_worker/catalog.py",
            ):
                self.assertTrue((worker / name).is_file(), name)
            native = worker / "native/hy"
            self.assertEqual(
                {item.name for item in native.iterdir()},
                {"model.lock.json", "cpu-runtime.lock.json", "LICENSE.llama.cpp"},
            )
            self.assertEqual(
                {item.relative_to(worker).as_posix() for item in worker.rglob("*.cpp")},
                {"native/hy/LICENSE.llama.cpp", "native/index/LICENSE.llama.cpp"},
            )
            index = worker / "native/index"
            self.assertEqual(
                {item.name for item in index.iterdir()},
                {
                    "model.lock.json",
                    "cpu-runtime.lock.json",
                    "LICENSE.llama.cpp",
                    "LICENSE.Index-Translate",
                },
            )
            self.assertFalse(any(worker.rglob("*.h")))
            self.assertFalse(any(worker.rglob("assets.lock.json")))
            self.assertFalse(any(worker.rglob("headers.lock.json")))
            self.assertFalse(any(worker.rglob("__pycache__")))
            self.assertFalse((native / "production-adapter.json").exists())
            # Import the relocated runtime, not the checkout's module. Missing
            # optional native payload fails before any library/model loading.
            result = subprocess.run(
                [
                    sys.executable,
                    "-I",
                    "-B",
                    "-c",
                    "import sys; sys.path.insert(0,sys.argv[1]); "
                    "from typelay_worker.hy_assets import verify_native; verify_native()",
                    str(worker),
                ],
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("production-adapter.json", result.stderr)


class BrowserBundleTests(unittest.TestCase):
    def test_browser_payloads_include_the_declared_icons_and_exact_xpi(self):
        with tempfile.TemporaryDirectory(prefix="typelay-browser-") as directory:
            package = Path(directory) / "发行 包"
            build.package_browsers(package)
            expected = {
                "manifest.json",
                "background.js",
                "content.js",
                "editor-bridge.js",
                "editor-adapter.js",
                "icons/typelay-16.png",
                "icons/typelay-48.png",
                "icons/typelay-128.png",
            }
            expected |= {
                "fonts/" + path.relative_to(ROOT / "src/shared/ui/fonts").as_posix()
                for path in (ROOT / "src/shared/ui/fonts").rglob("*")
                if path.is_file()
            }
            for browser in ("chromium", "firefox"):
                with self.subTest(browser=browser):
                    extension = package / "browser" / browser
                    self.assertEqual(
                        {
                            p.relative_to(extension).as_posix()
                            for p in extension.rglob("*")
                            if p.is_file()
                        },
                        expected,
                    )
                    manifest = json.loads((extension / "manifest.json").read_text())
                    self.assertEqual(set(manifest["icons"]), {"16", "48", "128"})
                    for resource in manifest["web_accessible_resources"][0]["resources"]:
                        self.assertTrue(list(extension.glob(resource)), resource)
                    self.assertTrue((extension / "fonts/inter/LICENSE.txt").is_file())
                    self.assertTrue((extension / "fonts/source-serif/LICENSE.md").is_file())
                    for size, name in manifest["icons"].items():
                        png = (extension / name).read_bytes()
                        self.assertEqual(png, (ROOT / "src/shared/browser" / name).read_bytes())
                        self.assertEqual(png[:16], b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR")
                        self.assertEqual(struct.unpack(">II", png[16:24]), (int(size), int(size)))
            with zipfile.ZipFile(package / "browser/typelay-firefox-unsigned.xpi") as addon:
                self.assertEqual(set(addon.namelist()), expected)
                for name in expected:
                    self.assertEqual(
                        addon.read(name), (package / "browser/firefox" / name).read_bytes()
                    )


class MainBundleTests(unittest.TestCase):
    def setUp(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="bundle-test-", dir=ROOT / "artifacts")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.repository = self.directory / "checkout"
        self.repository.mkdir()
        for relative in (
            *inputs.FCITX_BUILD_INPUTS,
            *inputs.FCITX_TEST_INPUTS,
            *inputs.QT_BUILD_INPUTS,
            *QT_TEST_INPUTS,
            "eng/linux/build.py",
            "packaging/linux/fcitx-cooperative/pack.py",
            "packaging/linux/fcitx-cooperative/fcitx-cooperative.py",
            "docs/platforms/linux/fcitx.md",
            "docs/platforms/linux/qt.md",
            "THIRD_PARTY_NOTICES.md",
        ):
            destination = self.repository / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, destination)
        (self.repository / ".gitignore").write_text("artifacts/\n__pycache__/\n")
        # This is an owned disposable Git repository, never the working checkout.
        for arguments in (
            ("init", "--quiet"),
            ("add", "."),
            (
                "-c",
                "user.name=Fixture",
                "-c",
                "user.email=fixture@example.invalid",
                "-c",
                "commit.gpgsign=false",
                "commit",
                "--quiet",
                "-m",
                "Fixture",
            ),
        ):
            subprocess.run(
                ["git", *arguments], cwd=self.repository, check=True, capture_output=True
            )
        self.package = self.repository / "artifacts/package"
        self.package.mkdir(parents=True)
        self.commit = subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=self.repository, text=True
        ).strip()
        self.base = {
            "typelay": b"main application remains unchanged\n",
            "build-info.json": json.dumps(
                {"schemaVersion": 1, "sourceCommit": self.commit, "sourceDirty": False}
            ).encode(),
        }
        for name, content in self.base.items():
            (self.package / name).write_bytes(content)
        (self.package / "MANIFEST.sha256").write_text(
            "".join(f"{digest(content)}  {name}\n" for name, content in sorted(self.base.items()))
        )
        self.archive = self.repository / "artifacts/cooperation.tar.gz"
        self.output = self.repository / "artifacts/TypeLay-Linux-preview-x86_64.tar.gz"
        self.files = {
            "fcitx-cooperative.py": (
                ROOT / "packaging/linux/fcitx-cooperative/fcitx-cooperative.py"
            ).read_bytes(),
            "core/bin/fcitx5": b"must never execute this fixture\n",
            "lib/im-fcitx5.so": b"fixture GTK library\n",
            "source/upstream/fcitx5/LICENSES/LGPL-2.1-or-later.txt": b"retained license\n",
        }
        for name in (
            "THIRD_PARTY_NOTICES.md",
            "lib/libFcitx5GClient.so",
            "lib/libtypelay-gtk4.so",
            "lib/gtk-4.0/4.0.0/immodules/libim-fcitx5.so",
            "core/lib/libFcitx5Core.so",
            "core/lib/libFcitx5Config.so",
            "core/lib/libFcitx5Utils.so",
            "core/lib/libIMECore.so",
            "core/lib/libIMEPinyin.so",
            "core/lib/libIMETable.so",
            "core/share/libime/sc.dict",
            "core/share/libime/extb.dict",
            "core/lib/libime/zh_CN.lm",
            "core/lib/libime/zh_CN.lm.predict",
            *(
                f"core/share/libime/{name}.main.dict"
                for name in ("db", "erbi", "qxm", "wanfeng", "wbpy", "wbx", "zrm", "cj")
            ),
            *(
                f"core/lib/fcitx5/lib{name}.so"
                for name in (
                    "pinyin",
                    "table",
                    "punctuation",
                    "pinyinhelper",
                    "chttrans",
                    "fullwidth",
                    "cloudpinyin",
                )
            ),
            *(
                f"core/share/fcitx5/addon/{name}.conf"
                for name in (
                    "pinyin",
                    "table",
                    "punctuation",
                    "pinyinhelper",
                    "chttrans",
                    "fullwidth",
                    "cloudpinyin",
                )
            ),
            *(
                f"source/upstream/{name}/LICENSES/LGPL-2.1-or-later.txt"
                for name in inputs.SOURCE_VERSIONS
            ),
        ):
            self.files[name] = b"inert packaged fixture\n"
        for relative in (
            *inputs.FCITX_BUILD_INPUTS,
            *inputs.FCITX_TEST_INPUTS,
            "packaging/linux/fcitx-cooperative/pack.py",
            "packaging/linux/fcitx-cooperative/fcitx-cooperative.py",
            "docs/platforms/linux/fcitx.md",
            "docs/platforms/linux/qt.md",
            "THIRD_PARTY_NOTICES.md",
        ):
            self.files[f"source/{relative}"] = (ROOT / relative).read_bytes()
        self.metadata = {
            "schemaVersion": 1,
            "bundleName": "TypeLay-Fcitx-cooperative",
            "coreVersion": inputs.CORE_VERSION,
            "versions": {
                "component_source_versions": inputs.SOURCE_VERSIONS,
                "installed_private_versions": inputs.PRIVATE_VERSIONS,
                "server_source_version": inputs.CORE_VERSION,
                "server_runtime_version": inputs.CORE_VERSION,
                "linked_core_development_version": inputs.CORE_VERSION,
                "server_source_commit": inputs.FCITX_PINS["fcitx5"][0],
                "binary_sha256": {
                    "fcitx5": digest(self.files["core/bin/fcitx5"]),
                    **{
                        name: digest(self.files[name])
                        for name in (
                            "lib/im-fcitx5.so",
                            "lib/libFcitx5GClient.so",
                            "lib/libtypelay-gtk4.so",
                            "lib/gtk-4.0/4.0.0/immodules/libim-fcitx5.so",
                        )
                    },
                },
            },
            "pins": {name: list(pin) for name, pin in inputs.FCITX_PINS.items()},
            "buildInputs": {"files": inputs.hashes(inputs.FCITX_BUILD_INPUTS)},
            "testInputs": None,
            "libimeDataSources": inputs.DATA_ARCHIVES,
            "chineseDataSources": inputs.CHINESE_DATA_ARCHIVES,
            "qt6": None,
        }
        self.links = {"lib/im-fcitx5.so.1": "im-fcitx5.so"}

    def write_archive(self, extra=()):
        self.metadata["files"] = {
            **{name: {"sha256": digest(data)} for name, data in self.files.items()},
            **{name: {"symlink": target} for name, target in self.links.items()},
        }
        self.manifest = json.dumps(self.metadata).encode()
        with tarfile.open(self.archive, "w:gz") as package:
            for name, data in {**self.files, "manifest.json": self.manifest}.items():
                entry = tarfile.TarInfo(f"TypeLay-Fcitx-cooperative/{name}")
                entry.size = len(data)
                package.addfile(entry, io.BytesIO(data))
            for name, target in self.links.items():
                entry = tarfile.TarInfo(f"TypeLay-Fcitx-cooperative/{name}")
                entry.type = tarfile.SYMTYPE
                entry.linkname = target
                package.addfile(entry)
            for name, data in extra:
                entry = tarfile.TarInfo(name)
                entry.size = len(data)
                package.addfile(entry, io.BytesIO(data))

    def finalize(self):
        return subprocess.run(
            [
                sys.executable,
                str(self.repository / "eng/linux/build.py"),
                "--finalize",
                "--output",
                str(self.package),
                "--cooperation-package",
                str(self.archive),
            ],
            cwd=self.repository,
            text=True,
            capture_output=True,
            timeout=30,
        )

    def test_current_regression_inputs_survive_finalization(self):
        self.metadata["testInputs"] = {"files": inputs.hashes(inputs.FCITX_TEST_INPUTS)}
        self.write_archive()
        result = self.finalize()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_partial_regression_inputs_are_rejected_by_finalizer(self):
        self.metadata["testInputs"] = {"files": inputs.hashes(inputs.FCITX_TEST_INPUTS[:2])}
        self.write_archive()
        self.assert_rejected("Cooperation test inputs differ")

    def test_changed_regression_source_is_rejected_by_finalizer(self):
        self.metadata["testInputs"] = {"files": inputs.hashes(inputs.FCITX_TEST_INPUTS)}
        self.files["source/tests/linux/fixtures/gtk4_fixture.c"] += b"\n/* drift */\n"
        self.write_archive()
        self.assert_rejected("Packaged cooperation source differs")

    def test_matching_payload_is_bound_to_main_manifest_without_rebuilding(self):
        self.write_archive()
        result = self.finalize()
        self.assertEqual(result.returncode, 0, result.stderr)
        with tarfile.open(self.output) as package:
            names = {entry.name for entry in package}
            root = "TypeLay-Linux-preview/"
            descriptor = json.load(package.extractfile(root + "support/desktop/component.json"))
            self.assertEqual(descriptor["sourceCommit"], self.commit)
            self.assertEqual(descriptor["sourceVersions"], inputs.SOURCE_VERSIONS)
            self.assertEqual(descriptor["privateVersions"], inputs.PRIVATE_VERSIONS)
            self.assertIsNone(descriptor["qtVersion"])
            self.assertEqual(descriptor["manifestSha256"], digest(self.manifest))
            archive_name = "support/desktop/TypeLay-Fcitx-cooperative.tar.gz"
            self.assertEqual(descriptor["archiveSha256"], digest(self.archive.read_bytes()))
            self.assertEqual(
                package.extractfile(root + archive_name).read(), self.archive.read_bytes()
            )
            manifest = package.extractfile(root + "MANIFEST.sha256").read().decode()
            self.assertIn(f"{descriptor['archiveSha256']}  {archive_name}\n", manifest)
            self.assertIn("  support/desktop/component.json\n", manifest)
            self.assertNotIn("  MANIFEST.sha256\n", manifest)
            self.assertIn(root + "build-info.json", names)
            self.assertEqual(package.extractfile(root + "typelay").read(), self.base["typelay"])
        self.assertEqual(
            self.output.with_suffix(".gz.sha256").read_text(),
            f"{digest(self.output.read_bytes())}  {self.output.name}\n",
        )

    def add_qt(self):
        binaries = {
            "plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so": "qt6/plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so",
            "native/qt6/generic/libtypelay-qt6.so": "qt6/plugins/generic/libtypelay-qt6.so",
            "qt-runtime-probe": "qt6/bin/qt-runtime-probe",
        }
        for path in binaries.values():
            self.files[path] = b"inert Qt fixture\n"
        for path in (*inputs.QT_BUILD_INPUTS, *QT_TEST_INPUTS):
            self.files["source/" + path] = (ROOT / path).read_bytes()
        for license_name in ("LGPL-2.1-or-later.txt", "BSD-3-Clause.txt"):
            self.files["source/upstream/fcitx5-qt/LICENSES/" + license_name] = b"license fixture"
        self.metadata["qt6"] = {
            "qtVersion": inputs.QT_VERSION,
            "fcitxQtVersion": inputs.QT_SOURCE_VERSION,
            "sourceCommit": inputs.QT_PIN,
            "sourceSha256": inputs.QT_SHA256,
            "buildInputs": inputs.hashes(inputs.QT_BUILD_INPUTS),
            "testInputs": inputs.hashes(QT_TEST_INPUTS),
            "binarySha256": {name: digest(self.files[path]) for name, path in binaries.items()},
        }

    def test_matching_qt_sources_and_binaries_are_bundled(self):
        self.add_qt()
        self.write_archive()
        result = self.finalize()
        self.assertEqual(result.returncode, 0, result.stderr)
        descriptor = json.loads((self.package / "support/desktop/component.json").read_text())
        self.assertEqual(descriptor["qtVersion"], inputs.QT_VERSION)

    def test_qt_input_revision_mismatch_is_rejected(self):
        self.add_qt()
        self.metadata["qt6"]["sourceCommit"] = "0" * 40
        self.write_archive()
        self.assert_rejected("Cooperation Qt inputs differ")

    def test_changed_qt_binary_is_rejected(self):
        self.add_qt()
        self.files["qt6/bin/qt-runtime-probe"] = b"changed since the recorded build"
        self.write_archive()
        self.assert_rejected("Cooperation Qt binary differs")

    def test_finalize_requires_an_explicit_payload(self):
        result = subprocess.run(
            [
                sys.executable,
                str(self.repository / "eng/linux/build.py"),
                "--finalize",
                "--output",
                str(self.package),
            ],
            cwd=self.repository,
            capture_output=True,
            text=True,
            timeout=10,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("--finalize requires --cooperation-package", result.stderr)
        self.assertFalse(self.output.exists())

    def assert_rejected(self, message):
        original = (self.package / "MANIFEST.sha256").read_bytes()
        result = self.finalize()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(message, result.stderr)
        self.assertFalse(self.output.exists())
        self.assertFalse((self.package / "support/desktop/component.json").exists())
        self.assertEqual((self.package / "MANIFEST.sha256").read_bytes(), original)

    def test_missing_requested_payload_does_not_claim_complete_support(self):
        self.assert_rejected("No such file")

    def test_changed_main_package_is_not_republished(self):
        self.write_archive()
        (self.package / "typelay").write_text("unrecorded replacement")
        self.assert_rejected("main-package-files-changed-before-finalization")

    def test_different_main_source_commit_is_rejected(self):
        self.write_archive()
        path = self.package / "build-info.json"
        record = json.loads(path.read_text())
        record["sourceCommit"] = "0" * 40
        path.write_text(json.dumps(record))
        self.base["build-info.json"] = path.read_bytes()
        (self.package / "MANIFEST.sha256").write_text(
            "".join(f"{digest(data)}  {name}\n" for name, data in sorted(self.base.items()))
        )
        self.assert_rejected("main-package-source-identity-mismatch")

    def test_dirty_current_sources_are_not_labeled_as_the_commit(self):
        self.write_archive()
        (self.repository / inputs.FCITX_BUILD_INPUTS[1]).write_text("changed after build\n")
        self.assert_rejected("full-bundle-requires-clean-source-checkout")

    def test_current_source_hash_mismatch_is_rejected_even_with_valid_archive_manifest(self):
        relative = inputs.FCITX_BUILD_INPUTS[1]
        self.files["source/" + relative] += b"\n# an older source revision\n"
        self.metadata["buildInputs"]["files"][relative] = digest(self.files["source/" + relative])
        self.write_archive()
        self.assert_rejected("build inputs differ from the current checkout")

    def test_embedded_source_must_match_recorded_current_source(self):
        self.files["source/" + inputs.FCITX_BUILD_INPUTS[1]] += b"\n# drift\n"
        self.write_archive()
        self.assert_rejected("Packaged cooperation source differs")

    def test_missing_runtime_is_not_published_as_a_full_component(self):
        self.files.pop("core/share/libime/sc.dict")
        self.write_archive()
        self.assert_rejected("Incomplete cooperation payload")

    def test_missing_license_is_not_published_as_a_full_component(self):
        self.files.pop("source/upstream/fcitx5/LICENSES/LGPL-2.1-or-later.txt")
        self.write_archive()
        self.assert_rejected("Incomplete cooperation payload")

    def test_recorded_binary_hash_must_match_the_actual_archive_bytes(self):
        self.files["core/bin/fcitx5"] = b"changed after the cooperation build\n"
        self.write_archive()
        self.assert_rejected("Cooperation binary differs from its build record")

    def test_pinned_component_version_mismatch_is_rejected(self):
        self.metadata["versions"]["server_runtime_version"] = "0.0.0"
        self.write_archive()
        self.assert_rejected("component versions mismatch")

    def test_extra_payload_is_rejected_even_when_it_is_inside_the_archive_root(self):
        self.write_archive(extra=(("TypeLay-Fcitx-cooperative/unrecorded.py", b"unlisted"),))
        self.assert_rejected("manifest, pins or archive files mismatch")

    def test_traversal_and_duplicate_archive_members_are_rejected(self):
        for name in (
            "TypeLay-Fcitx-cooperative/../escape",
            "/TypeLay-Fcitx-cooperative/absolute",
            "TypeLay-Fcitx-cooperative/core/bin/fcitx5",
        ):
            with self.subTest(name=name):
                self.write_archive(extra=((name, b"invalid"),))
                self.assert_rejected("Unsafe cooperation archive entry")
        self.assertFalse((self.repository / "escape").exists())

    def test_external_cyclic_and_parent_symlinks_are_rejected(self):
        for links in (
            {"lib/escape": "../../outside"},
            {"lib/a": "b", "lib/b": "a"},
            {"core": "lib/im-fcitx5.so"},
        ):
            with self.subTest(links=links):
                self.links = links
                self.write_archive()
                self.assert_rejected("cooperation")

    def test_repeated_finalization_keeps_a_verifiable_main_manifest(self):
        self.write_archive()
        for _ in range(2):
            result = self.finalize()
            self.assertEqual(result.returncode, 0, result.stderr)
        lines = (self.package / "MANIFEST.sha256").read_text().splitlines()
        names = [line.split("  ", 1)[1] for line in lines]
        self.assertEqual(len(names), len(set(names)))
        self.assertNotIn("MANIFEST.sha256", names)
        for line in lines:
            checksum, name = line.split("  ", 1)
            self.assertEqual(checksum, digest((self.package / name).read_bytes()))


if __name__ == "__main__":
    unittest.main()
