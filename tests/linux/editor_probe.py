"""Real Tiptap transaction/undo/reentrancy experiment for the richtext adapter.

Run npm ci --ignore-scripts in a disposable copy of tests/fixtures/editor,
then pass that directory through --dependencies. No user browser is opened.
This is not extension bridge, physical IME, or complete desktop acceptance.
"""

import argparse
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from browser_process import chromium_context
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tests/fixtures/editor"
INITIAL = "<p>Before <strong>hello world</strong> after <em>untouched</em></p>"
TRANSLATED = "<p>Before <strong>bonjour world</strong> after <em>untouched</em></p>"
NEW_SOURCE = "<p>NEW Before <strong>hello world</strong> after <em>untouched</em></p>"


class EditorProbe(unittest.TestCase):
    executable = "/usr/bin/chromium"
    dependencies = ROOT / "artifacts/prototypes/editor"

    @classmethod
    def setUpClass(cls):
        cls.playwright = sync_playwright().start()
        cls.temporary = tempfile.TemporaryDirectory(prefix="typelay-editor-probe-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.addClassCleanup(cls.playwright.stop)
        cls.home = Path(cls.temporary.name)
        # Resolve imports relative to the copied fixture, not repository or user
        # node_modules. The lockfile and npm ci own dependency verification.
        entry = cls.dependencies / "entry.js"
        shutil.copy2(FIXTURE / "entry.js", entry)
        cls.bundle = cls.home / "editor.js"
        subprocess.run(
            [
                "node",
                str(cls.dependencies / "node_modules/esbuild/bin/esbuild"),
                str(entry),
                "--bundle",
                "--format=iife",
                f"--outfile={cls.bundle}",
            ],
            check=True,
            timeout=30,
        )

    def setUp(self):
        home = self.home / self._testMethodName
        home.mkdir()
        env = {
            **os.environ,
            "HOME": str(home),
            "XDG_CONFIG_HOME": str(home / "config"),
            "XDG_CACHE_HOME": str(home / "cache"),
        }
        scope = chromium_context(self.playwright, self.executable, home / "profile", env)
        context = scope.__enter__()
        self.addCleanup(scope.__exit__, None, None, None)
        self.page = context.new_page()
        self.page.route(
            "http://127.0.0.1/editor-probe",
            lambda route: route.fulfill(
                content_type="text/html",
                body="<!doctype html><meta charset=utf-8><div id=mount></div>",
            ),
        )
        self.page.goto("http://127.0.0.1/editor-probe")
        self.page.add_script_tag(content=self.bundle.read_text())

    def probe(self, kind):
        return self.page.evaluate("kind => window.editorProbe(kind)", kind)

    def test_transaction_preserves_rich_spans_selection_and_native_undo(self):
        result = self.probe("guarded")
        self.assertEqual(result["status"], "written")
        self.assertEqual(result["after"], TRANSLATED)
        self.assertTrue(result["preserved"])
        self.assertEqual(result["selection"], {"from": 15, "to": 15})
        self.assertEqual(result["undone"], INITIAL)
        self.assertEqual(result["undoSelection"], {"from": 8, "to": 13})
        self.assertEqual(result["redone"], TRANSLATED)

    def test_unguarded_tiptap_overwrites_reentrant_source_change(self):
        result = self.probe("reentrant-unguarded")
        self.assertTrue(result["reentered"])
        # This is an explicit unsafe counterexample, not an acceptance pass.
        self.assertEqual(result["after"], TRANSLATED)
        self.assertNotEqual(result["after"], NEW_SOURCE)

    def test_guard_preserves_reentrant_change_and_its_native_undo(self):
        result = self.probe("reentrant-guarded")
        self.assertEqual(result["status"], "source-changed")
        self.assertTrue(result["reentered"])
        self.assertEqual(result["after"], NEW_SOURCE)
        self.assertEqual(result["undone"], INITIAL)
        self.assertEqual(result["redone"], NEW_SOURCE)

    def test_guard_survives_callback_listener_removal_and_addition(self):
        result = self.probe("listener-change")
        self.assertEqual(result["status"], "source-changed")
        self.assertEqual(result["after"], NEW_SOURCE)
        self.assertEqual(result["undone"], INITIAL)

    def test_guard_rejects_dom_change_before_model_commit(self):
        result = self.probe("dom-change")
        self.assertEqual(result["status"], "source-changed")
        self.assertEqual(result["domAfter"], "Before NEW world after untouched")

    def test_guard_rejects_dom_selection_change_before_model_commit(self):
        result = self.probe("dom-selection-change")
        self.assertEqual(result["status"], "source-changed")
        self.assertEqual(result["after"], INITIAL)

    def test_known_composition_is_refused_without_replacement(self):
        # Synthetic composition proves refusal only, not trusted IME readiness.
        result = self.probe("composing")
        self.assertEqual(result["status"], "unsupported")
        self.assertEqual(result["after"], INITIAL)

    def test_guard_rejects_selection_change_before_outer_commit(self):
        result = self.probe("selection-change")
        self.assertEqual(result["status"], "source-changed")
        self.assertEqual(result["after"], INITIAL)
        self.assertEqual(result["selection"], {"from": 1, "to": 1})


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--chromium", default=EditorProbe.executable)
    parser.add_argument("--dependencies", type=Path, required=True)
    args, remaining = parser.parse_known_args()
    EditorProbe.executable = args.chromium
    EditorProbe.dependencies = args.dependencies.resolve()
    unittest.main(argv=[__file__, *remaining])
