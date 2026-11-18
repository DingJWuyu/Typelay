"""A fixed release changes only when one of its own archive inputs changes."""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
KINDS = ("linux-tools", "fonts", "fcitx", "hy", "index")
ACTION = ".github/actions/linux-tools/action.yml"


class FixedBuildKeyTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        for name in ("eng", ".github/actions/linux-tools", "src/linux/native"):
            shutil.copytree(
                ROOT / name, self.root / name, ignore=shutil.ignore_patterns("__pycache__")
            )
        for kind, lock in (("hy", "assets.lock.json"), ("index", "model.lock.json")):
            source = Path("src/shared/TypeLay.Worker/native") / kind / lock
            (self.root / source.parent).mkdir(parents=True)
            shutil.copyfile(ROOT / source, self.root / source)
        self.baseline = self.keys()

    def keys(self):
        return {
            kind: subprocess.check_output(
                [sys.executable, str(self.root / "eng/ci/fixed.py"), "key", kind],
                text=True,
                timeout=10,
                env=os.environ | {"PYTHONDONTWRITEBYTECODE": "1"},
            ).strip()
            for kind in KINDS
        }

    def check_edit(self, name, old, new, *changed, label=None):
        with self.subTest(file=name, edit=label or new):
            path = self.root / name
            original = path.read_text(encoding="utf-8")
            self.assertIn(old, original)
            path.write_bytes(original.replace(old, new, 1).encode("utf-8"))
            try:
                keys = self.keys()
                self.assertEqual(
                    {kind for kind in KINDS if keys[kind] != self.baseline[kind]}, set(changed)
                )
            finally:
                path.write_bytes(original.encode("utf-8"))

    def test_action_observations_do_not_invalidate_any_archive(self):
        self.check_edit(ACTION, "name: Linux tools", "# workflow note\nname: Linux tools")
        self.check_edit(ACTION, "Outcome of the fixed package restore", "Archive restore outcome")
        self.check_edit(
            ACTION, 'if [ "$PRODUCER"', 'echo "Restoring package cache"\n      if [ "$PRODUCER"'
        )
        self.check_edit("eng/ci/fixed_summary.py", "本轮冷建", "本轮首次构建")

    def test_pacman_transactions_and_producer_collection_change_the_tools_archive(self):
        recipe = "eng/ci/linux-tools.sh"
        for old, new in (
            ("python github-cli", "python github-cli wget"),
            ("git cmake ninja", "git cmake ninja make"),
            ("gimp at-spi2-core", "gimp inkscape at-spi2-core"),
            ('"$PRODUCER" == true', '"$PRODUCER" == false'),
        ):
            self.check_edit(recipe, old, new, "linux-tools")
        snapshot = "eng/ci/arch-snapshot"
        self.check_edit(
            snapshot,
            (self.root / snapshot).read_text().strip(),
            "2027/01/01",
            "linux-tools",
            "fcitx",
        )

    def test_raw_downloads_ignore_descriptions_but_track_each_download_identity(self):
        for kind, filename in (("hy", "assets.lock.json"), ("index", "model.lock.json")):
            name = f"src/shared/TypeLay.Worker/native/{kind}/{filename}"
            text = (self.root / name).read_text(encoding="utf-8")
            lock = json.loads(text)
            for record in (
                "model",
                "model_license",
                *(("runtime_archive",) if kind == "hy" else ()),
            ):
                self.check_edit(
                    name, f'"{record}": {{', f'"{record}": {{"description": "reader only",'
                )
                for field, value in lock[record].items():
                    if field not in {"file", "bytes", "sha256", "url", "revision", "tag", "commit"}:
                        continue
                    changed = value + 1 if isinstance(value, int) else value + "-changed"
                    edit = json.loads(text)
                    edit[record][field] = changed
                    self.check_edit(
                        name,
                        text,
                        json.dumps(edit, indent=2) + "\n",
                        kind,
                        label=f"{record}.{field}",
                    )
            self.check_edit(name, lock["template_sha256"], "0" * 64)
            script = (
                "eng/windows/hy/prepare_assets.py"
                if kind == "hy"
                else "eng/shared/prepare_index_model.py"
            )
            message = '"prepare_failure"' if kind == "hy" else '"index_asset_stage"'
            self.check_edit(script, message, '"diagnostic"')

    def test_fcitx_progress_messages_do_not_invalidate_the_third_party_archive(self):
        self.check_edit(
            "eng/linux/cooperative/third_party.py",
            "Configuring complete Fcitx",
            "Preparing complete Fcitx",
        )

    def test_font_observations_and_lint_dependencies_leave_the_archive_reusable(self):
        self.check_edit(
            "eng/fonts/prepare.py", "Verified offline UI font bundle", "Checked offline fonts"
        )
        self.check_edit("eng/requirements.txt", "ruff==", "ruff==99.")

    def test_fonts_track_the_lock_fonttools_and_output_recipe(self):
        self.check_edit(
            "eng/fonts/fonts.lock.json",
            "src/shared/ui/fonts/inter/Inter-Medium.ttf",
            "src/shared/ui/fonts/inter/Inter-Other.ttf",
            "fonts",
        )
        self.check_edit("eng/fonts/requirements.txt", "fonttools==", "fonttools==99.", "fonts")
        self.check_edit("eng/fonts/bundle.py", "optimize=True", "optimize=False", "fonts")

    def test_fcitx_tracks_its_sources_data_patches_and_build_options(self):
        recipe = "eng/linux/cooperative/third_party_recipe.py"
        text = (self.root / recipe).read_text(encoding="utf-8")
        source_pin = re.search(r'"([a-f0-9]{40})"', text)[1]
        data_pin = re.search(r'"[^"]+\.tar\.zst",\s*"([a-f0-9]{64})"', text)[1]
        self.check_edit(recipe, source_pin, "1" * 40, "fcitx")
        self.check_edit(recipe, data_pin, "1" * 64, "fcitx")
        self.check_edit(
            "eng/linux/cooperative/archives.py", '"--remove-destination"', '"--no-clobber"', "fcitx"
        )
        self.check_edit(recipe, "-DENABLE_ENCHANT=ON", "-DENABLE_ENCHANT=OFF", "fcitx")
        self.check_edit(
            "eng/linux/cooperative/patches/fcitx5.patch",
            "TypeLay development-only transaction cooperation changes",
            "TypeLay third-party cooperation changes",
            "fcitx",
        )
        for name in (
            "src/linux/native/gtk4/CMakeLists.txt",
            "src/linux/native/qt6/CMakeLists.txt",
            "src/linux/native/common/overlay-design.h",
            "eng/linux/cooperative/fcitx.py",
            "eng/linux/cooperative/qt.py",
        ):
            self.check_edit(
                name,
                "\n",
                "\n/* TypeLay native edit */\n"
                if name.endswith(".h")
                else "\n# TypeLay native edit\n",
            )

    def test_archive_member_selection_invalidates_only_its_product(self):
        self.check_edit(
            "eng/ci/fixed.py",
            '"artifacts/linux-tools-cache"',
            '"artifacts/new-packages"',
            "linux-tools",
        )
        self.check_edit(
            "eng/ci/fixed.py", '"--exclude=core-build"', '"--exclude=other-build"', "fcitx"
        )
