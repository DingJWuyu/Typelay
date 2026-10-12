"""Real Tiptap transaction/undo/reentrancy experiment for the richtext adapter.

Run npm ci --ignore-scripts in a disposable copy of tests/fixtures/editor,
then pass that directory through --dependencies. No user browser is opened.
This is not extension bridge, physical IME, or complete desktop acceptance.
"""

import argparse
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

import browser_e2e
from browser_process import chromium_context
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tests/fixtures/editor"
ADAPTER = ROOT / "src/TypeLay.Linux/browser/editor-adapter.js"
BRIDGE = ROOT / "src/TypeLay.Linux/browser/editor-bridge.js"
CONTENT = ROOT / "src/TypeLay.Linux/browser/content.js"
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
        cls.bundle = cls.dependencies / "fixture.js"
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
        self.page.add_script_tag(content=ADAPTER.read_text())
        self.page.add_script_tag(content=self.bundle.read_text())
        self.cdp = context.new_cdp_session(self.page)
        frame = self.cdp.send("Page.getFrameTree")["frameTree"]["frame"]["id"]
        self.world = self.cdp.send(
            "Page.createIsolatedWorld", {"frameId": frame, "worldName": "typelay-editor-probe"}
        )["executionContextId"]
        self.isolated(BRIDGE.read_text())

    def isolated(self, expression):
        response = self.cdp.send(
            "Runtime.evaluate",
            {"expression": expression, "contextId": self.world, "returnByValue": True},
        )
        if response.get("exceptionDetails"):
            self.fail(str(response["exceptionDetails"]))
        return response["result"].get("value")

    def probe(self, kind, baseline=False, allowed=True, nested=False):
        before = self.page.evaluate("kind => window.editorProbeSetup(kind)", kind)
        if baseline:
            self.page.evaluate("window.editorProbeUnsafe()")
            receipt = {"status": "baseline"}
        else:
            guard = (
                "TypeLayEditorBridge.probe(root) === null && "
                "TypeLayEditorBridge.capture(root) === null && "
                "TypeLayEditorBridge.selection(root)?.start === 7"
                if nested
                else json.dumps(allowed)
            )
            receipt = self.isolated(
                "(() => { const root = document.querySelector('#editor-source');"
                "const capture = TypeLayEditorBridge.capture(root);"
                "if (!capture) return {status: 'Unavailable'};"
                "return TypeLayEditorBridge.replace(root, {version: capture.version,"
                "expectedText: capture.text, selection: capture.selection, start: 7, end: 12,"
                "replacement: 'bonjour', direct: false}, () => " + guard + "); })()"
            )
        result = self.page.evaluate("window.editorProbeInspect()")
        # Capture the DOM before undo/redo can flush a pending page-owned edit.
        history = self.page.evaluate("window.editorProbeHistory()")
        return {**before, **receipt, **result, **history}

    def test_production_capture_publish_and_single_tab_acceptance(self):
        self.isolated(browser_e2e.TRANSPORT + "\n" + CONTENT.read_text())
        self.page.evaluate("window.editorProbeSetup('guarded')")
        self.page.keyboard.press("Shift")  # Trusted non-composing state evidence.
        sequence = 0

        def command(operation, data=None):
            nonlocal sequence
            sequence += 1
            message = {"id": sequence, "op": operation, "data": data or {}}
            return self.isolated("fixtureCommand(" + json.dumps(message) + ")")

        description = command("describe")
        self.assertIsNotNone(description)
        snapshot = command(
            "capture", {"target": description["target"], "authority": description["authority"]}
        )
        self.assertIsNotNone(snapshot)
        plan = browser_e2e.BrowserContract.plan(snapshot, "bonjour", {"start": 7, "end": 12})
        plan["resultText"] = "Before bonjour world after untouched"
        plan["caret"] = 14
        command(
            "publish",
            {
                "target": snapshot["target"],
                "authority": snapshot["authority"],
                "permit": {"plan": plan},
                "lease": {"source": {"observedAtMs": snapshot["observedAtMs"]}},
            },
        )
        self.page.keyboard.press("Tab")
        result = self.page.evaluate("window.editorProbeInspect()")
        self.assertEqual(result["after"], TRANSLATED)
        self.assertTrue(result["preserved"])
        accepted = self.isolated("fixtureMessages.filter(message => message.event === 'accepted')")
        self.assertEqual(len(accepted), 1)
        self.assertEqual(accepted[0]["data"]["receipt"]["status"], "Verified")
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "editor-source")
        self.assertEqual(self.page.evaluate("window.editorProbeHistory()")["undone"], INITIAL)

    def test_transaction_preserves_rich_spans_selection_and_native_undo(self):
        result = self.probe("guarded")
        self.assertEqual(result["status"], "Applied")
        self.assertEqual(result["after"], TRANSLATED)
        self.assertTrue(result["preserved"])
        self.assertEqual(result["selection"], {"from": 15, "to": 15})
        self.assertEqual(result["undone"], INITIAL)
        self.assertEqual(result["undoSelection"], {"from": 8, "to": 13})
        self.assertEqual(result["redone"], TRANSLATED)

    def test_unguarded_tiptap_overwrites_reentrant_source_change(self):
        result = self.probe("reentrant-unguarded", baseline=True)
        self.assertTrue(result["reentered"])
        # This is an explicit unsafe counterexample, not an acceptance pass.
        self.assertEqual(result["after"], TRANSLATED)
        self.assertNotEqual(result["after"], NEW_SOURCE)

    def test_guard_preserves_reentrant_change_and_its_native_undo(self):
        result = self.probe("reentrant-guarded")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertTrue(result["reentered"])
        self.assertEqual(result["after"], NEW_SOURCE)
        self.assertEqual(result["undone"], INITIAL)
        self.assertEqual(result["redone"], NEW_SOURCE)

    def test_guard_survives_callback_listener_removal_and_addition(self):
        result = self.probe("listener-change")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertEqual(result["after"], NEW_SOURCE)
        self.assertEqual(result["undone"], INITIAL)

    def test_guard_rejects_dom_change_before_model_commit(self):
        result = self.probe("dom-change")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertEqual(result["domAfter"], "Before NEW world after untouched")

    def test_guard_rejects_dom_selection_change_before_model_commit(self):
        result = self.probe("dom-selection-change")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertEqual(result["after"], INITIAL)

    def test_guard_rechecks_after_page_observes_the_bridge_check(self):
        result = self.probe("bridge-check-change")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertTrue(result["reentered"])
        self.assertEqual(result["after"], NEW_SOURCE)
        self.assertEqual(result["undone"], INITIAL)

    def test_final_isolated_guard_can_veto_the_page_transaction(self):
        result = self.probe("guarded", allowed=False)
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertEqual(result["after"], INITIAL)

    def test_final_guard_keeps_dom_selection_but_rejects_nested_rpc(self):
        result = self.probe("guarded", nested=True)
        self.assertEqual(result["status"], "Applied")
        self.assertEqual(result["after"], TRANSLATED)

    def test_native_history_grouping_matches_the_unmodified_editor(self):
        baseline = self.probe("adjacent", baseline=True)
        adapted = self.probe("adjacent")
        self.assertEqual(adapted["status"], "Applied")
        for key in ["after", "undone", "undoSelection", "redone"]:
            self.assertEqual(adapted[key], baseline[key], key)
        self.assertEqual(adapted["undone"], INITIAL)
        self.assertIn("bonjour! world", adapted["redone"])

    def test_known_composition_is_refused_without_replacement(self):
        # Synthetic composition proves refusal only, not trusted IME readiness.
        result = self.probe("composing")
        self.assertEqual(result["status"], "Unavailable")
        self.assertEqual(result["after"], INITIAL)

    def test_guard_rejects_selection_change_before_outer_commit(self):
        result = self.probe("selection-change")
        self.assertEqual(result["status"], "OutcomeUnknown")
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
