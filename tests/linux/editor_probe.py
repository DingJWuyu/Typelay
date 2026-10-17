"""Real Tiptap transaction/undo/reentrancy experiment for the richtext adapter.

Run npm ci --ignore-scripts in a disposable copy of tests/linux/fixtures/editor,
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
from playwright.sync_api import sync_playwright
from support.browser_process import chromium_context

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tests/linux/fixtures/editor"
ADAPTER = ROOT / "src/linux/browser/editor-adapter.js"
BRIDGE = ROOT / "src/linux/browser/editor-bridge.js"
CONTENT = ROOT / "src/linux/browser/content.js"
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
                "const deadline = performance.now() + 240; root.dataset.probeDeadline = String(deadline);"
                "return TypeLayEditorBridge.replace(root, {version: capture.version,"
                "expectedText: capture.text, selection: capture.selection, start: 7, end: 12,"
                "replacement: 'bonjour', direct: false, expiresAtPerformanceMs: deadline,"
                "expiresAtWallMs: Date.now() + 240}, () => " + guard + "); })()"
            )
        result = self.page.evaluate("window.editorProbeInspect()")
        # Capture the DOM before undo/redo can flush a pending page-owned edit.
        history = self.page.evaluate("window.editorProbeHistory()")
        return {**before, **receipt, **result, **history}

    def test_production_capture_publish_and_single_tab_acceptance(self):
        self.production_acceptance()

    def test_control_acceptance_preserves_marks_and_consumes_one_press(self):
        self.production_acceptance(control=True)

    def test_control_claim_cannot_be_downgraded_after_page_reentry(self):
        self.production_acceptance(control=True, kind="reentrant-guarded")

    def test_control_claim_is_quarantined_after_class_change_and_restore(self):
        self.production_acceptance(control=True, kind="class-roundtrip")

    def test_rich_editor_draft_survives_source_blur_but_not_new_policy_class(self):
        self.rich_editor_return()

    def test_rich_editor_return_rejects_selection_change_and_restore_during_focus(self):
        self.rich_editor_return(selection_change=True)

    def rich_editor_return(self, *, selection_change=False):
        self.isolated(browser_e2e.TRANSPORT + "\n" + CONTENT.read_text())
        self.isolated("""fixtureCommand({id: 1, op: 'configure-shortcuts', data:
          {...fixtureBindings, accept: 'Ctrl+F7', exit: 'Ctrl+F8'}})""")
        self.page.evaluate("editorProbeSetup('guarded')")
        self.page.bring_to_front()
        self.page.wait_for_function(
            "document.hasFocus() && document.activeElement.id === 'editor-source'",
            timeout=5000,
        )
        self.page.keyboard.press("Shift")
        self.assertTrue(
            self.isolated("""(() => {
              const described = fixtureCommand({id: 2, op: 'describe'});
              const source = fixtureCommand({id: 3, op: 'capture', data: {
                target: described?.target, authority: described?.authority}});
              if (!source) return false;
              window.fixtureRichView = {target: source.target, authority: source.authority,
                revision: 1, view: {state: 'ValidTranslation', text: 'Initial candidate',
                  editable: true, presentation: {visible: true, editorOwnsText: false}}};
              return fixtureCommand({id: 4, op: 'overlay-render', data: fixtureRichView})?.visible;
            })()""")
        )

        def editor():
            return next(
                (
                    node
                    for node in self.cdp.send("Accessibility.getFullAXTree")["nodes"]
                    if not node.get("ignored")
                    and node.get("name", {}).get("value") == "TypeLay 译文"
                    and node.get("role", {}).get("value") == "textbox"
                ),
                None,
            )

        candidate = editor()
        self.assertIsNotNone(candidate)
        self.assertTrue(
            self.page.locator("#editor-source").evaluate(
                "root => root.classList.contains('ProseMirror-focused')"
            )
        )
        border = self.cdp.send("DOM.getBoxModel", {"backendNodeId": candidate["backendDOMNodeId"]})[
            "model"
        ]["border"]
        self.page.mouse.click(sum(border[::2]) / 4, sum(border[1::2]) / 4)
        self.assertTrue(
            self.isolated("""fixtureMessages.some(message => message.event === 'ui'
              && message.data.operation === 'beginEdit')""")
        )
        self.isolated("""fixtureRichView.revision = 2;
          fixtureRichView.view.state = 'EditingTranslation';
          fixtureRichView.view.presentation.editorOwnsText = true;
          fixtureCommand({id: 5, op: 'overlay-render', data: fixtureRichView});
          fixtureCommand({id: 6, op: 'overlay-edit', data: {
            target: fixtureRichView.target, allowed: true}});""")
        self.assertFalse(
            self.page.locator("#editor-source").evaluate(
                "root => root.classList.contains('ProseMirror-focused')"
            )
        )
        self.page.keyboard.press("Control+A")
        self.page.keyboard.insert_text("Manual rich candidate.")
        # Renew the original display authority after the real Tiptap blur.
        # No source recapture or new write permit can repair this handoff.
        self.isolated("""fixtureCommand({id: 7, op: 'overlay-renew', data: {
          target: fixtureRichView.target, authority: fixtureRichView.authority}})""")
        current = editor()
        self.assertIsNotNone(current)
        self.assertEqual(current.get("value", {}).get("value"), "Manual rich candidate.")
        self.assertTrue(
            any(
                item.get("name") == "focused" and item.get("value", {}).get("value")
                for item in current.get("properties", [])
            )
        )
        self.assertEqual(self.page.evaluate("editorProbeInspect().after"), INITIAL)
        if selection_change:
            self.page.locator("#editor-source").evaluate("""root => {
              root.addEventListener('focus', () => {
                const editor = root.editor, original = editor.state.selection;
                editor.commands.setTextSelection(1);
                editor.view.dispatch(editor.state.tr.setSelection(original));
              }, {once: true});
            }""")
        self.page.keyboard.press("Control+F7")
        self.assertTrue(
            self.isolated("""fixtureMessages.some(message => message.event === 'ui'
              && message.data.operation === 'return')""")
        )
        returned = self.isolated("""fixtureCommand({id: 8, op: 'return', data: {
          target: fixtureRichView.target, authority: fixtureRichView.authority}})""")
        self.assertEqual(returned, {"returned": not selection_change})
        self.assertEqual(self.page.evaluate("editorProbeInspect().after"), INITIAL)
        self.assertEqual(
            self.page.evaluate("editorProbeInspect().selection"), {"from": 8, "to": 13}
        )
        self.assertEqual(
            self.isolated("fixtureMessages.filter(message => message.event === 'accepted')"), []
        )
        if selection_change:
            return
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "editor-source")
        observed = self.isolated("""(() => {
          const described = fixtureCommand({id: 9, op: 'describe'});
          const captured = fixtureCommand({id: 10, op: 'capture', data: {
            target: described?.target, authority: described?.authority}});
          return captured && {originalInput: fixtureRichView.authority.inputRevision,
            returnedInput: captured.authority.inputRevision, selection: captured.selection};
        })()""")
        self.assertIsNotNone(observed)
        self.assertEqual(observed["originalInput"], observed["returnedInput"])
        self.assertEqual(observed["selection"], {"start": 7, "end": 12})
        self.page.locator("#editor-source").evaluate("root => root.classList.add('terminal')")
        self.isolated("""fixtureCommand({id: 11, op: 'overlay-renew', data: {
          target: fixtureRichView.target, authority: fixtureRichView.authority}})""")
        self.assertIsNone(editor())
        self.assertEqual(self.page.evaluate("editorProbeInspect().after"), INITIAL)
        self.assertEqual(
            self.isolated("fixtureMessages.filter(message => message.event === 'accepted')"), []
        )

    def production_acceptance(self, *, control=False, kind="guarded"):
        self.isolated(browser_e2e.TRANSPORT + "\n" + CONTENT.read_text())
        self.isolated("fixtureCommand({id: 1, op: 'configure-shortcuts', data: fixtureBindings})")
        if control:
            self.isolated(
                "fixtureCommand({id: 2, op: 'configure-shortcuts', data: "
                "{...fixtureBindings, accept: 'Ctrl+F7', exit: 'Ctrl+F8'}})"
            )
        self.page.evaluate("kind => window.editorProbeSetup(kind)", kind)
        if kind == "class-roundtrip":
            self.page.evaluate("""() => {
              const root = document.querySelector('#editor-source');
              window.originalControlClass = root.getAttribute('class');
              window.controlClassChanged = false;
              root.editor.on('transaction', ({transaction}) => {
                if (!transaction.docChanged) return;
                root.classList.add('cm-editor');
                root.classList.remove('cm-editor');
                window.controlClassChanged = true;
              });
            }""")
        self.page.evaluate("""() => {
          window.controlKeys = [];
          for (const type of ['keydown', 'keyup']) window.addEventListener(type, event => {
            if (event.key === 'F7') controlKeys.push([type, event.key]);
          });
        }""")
        # DOM focus alone does not activate a real Chromium window. Establish
        # the actual keyboard destination before the one trusted IME observation.
        self.page.bring_to_front()
        self.page.wait_for_function(
            """() => {
          const root = document.querySelector('#editor-source');
          const selected = document.getSelection();
          return document.hasFocus() && document.visibilityState === 'visible'
            && document.activeElement === root && root.contains(selected.anchorNode)
            && root.contains(selected.focusNode) && !selected.isCollapsed;
        }""",
            timeout=5000,
        )
        self.isolated("""window.addEventListener('keydown', event => {
          window.fixtureShift = {trusted: event.isTrusted, key: event.key,
            targetIsRoot: event.target === document.querySelector('#editor-source'),
            hasFocus: document.hasFocus(), composing: event.isComposing};
        }, {capture: true, once: true});""")
        self.page.keyboard.press("Shift")  # Trusted non-composing state evidence.
        sequence = 2

        def command(operation, data=None):
            nonlocal sequence
            sequence += 1
            message = {"id": sequence, "op": operation, "data": data or {}}
            return self.isolated("fixtureCommand(" + json.dumps(message) + ")")

        # Keep the approved describe/capture pair in one browser turn. Python
        # transport latency must not consume the production 150 ms authority.
        captured = self.isolated("""(() => {
          const describeRequest = {id: 1, op: 'describe', data: {}};
          const description = fixtureCommand(describeRequest);
          const captureRequest = {id: 2, op: 'capture', data: {
            target: description?.target, authority: description?.authority}};
          const snapshot = description ? fixtureCommand(captureRequest) : null;
          const root = document.querySelector('#editor-source');
          const selected = document.getSelection();
          return {snapshot, diagnostic: {
            described: !!description, imeKnown: description?.metadata.hasTextEdit,
            describedFocused: description?.focused, hasFocus: document.hasFocus(),
            activeIsRoot: document.activeElement === root, visibility: document.visibilityState,
            shift: window.fixtureShift, authorityObservedAtMs: description?.authority.observedAtMs,
            describeClock: describeRequest.clock, captureClock: captureRequest.clock,
            wallTimeMs: Date.now(), performanceMs: performance.now(),
            selection: {anchorInRoot: root.contains(selected.anchorNode),
              anchorType: selected.anchorNode?.nodeType, anchorOffset: selected.anchorOffset,
              focusInRoot: root.contains(selected.focusNode),
              focusType: selected.focusNode?.nodeType, focusOffset: selected.focusOffset,
              collapsed: selected.isCollapsed}}};
        })()""")
        snapshot = captured["snapshot"]
        self.assertIsNotNone(snapshot, json.dumps(captured["diagnostic"], sort_keys=True))
        if control:
            self.assertTrue(snapshot["sessionBindings"]["sourceReady"])
            self.assertTrue(snapshot["sessionBindings"]["editorReady"])
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
        if control:
            try:
                self.page.keyboard.down("Control")
                self.page.keyboard.down("F7")
                self.page.keyboard.down("F7")
            finally:
                self.page.keyboard.up("Control")
                self.page.keyboard.up("F7")
            self.assertEqual(self.page.evaluate("controlKeys"), [])
        else:
            self.page.keyboard.press("Tab")
        result = self.page.evaluate("window.editorProbeInspect()")
        accepted = self.isolated("fixtureMessages.filter(message => message.event === 'accepted')")
        self.assertEqual(len(accepted), 1)
        self.assertEqual(accepted[0]["data"]["planId"], plan["id"])
        if kind in ("reentrant-guarded", "class-roundtrip"):
            self.assertEqual(accepted[0]["data"]["receipt"]["status"], "OutcomeUnknown")
            self.assertEqual(
                result["after"], NEW_SOURCE if kind == "reentrant-guarded" else TRANSLATED
            )
            if kind == "class-roundtrip":
                self.assertTrue(self.page.evaluate("controlClassChanged"))
                self.assertTrue(
                    self.page.evaluate(
                        "document.querySelector('#editor-source').getAttribute('class') === originalControlClass"
                    )
                )
            self.assertTrue(result["preserved"])
            self.assertTrue(result["updateRestored"])
            self.assertFalse(
                self.isolated("""(() => {
              const described = fixtureCommand({id: 20, op: 'describe', data: {}});
              return fixtureCommand({id: 21, op: 'capture', data: {
                target: described.target, authority: described.authority}})?.writeSupported;
            })()""")
            )
            return
        self.assertEqual(result["after"], TRANSLATED)
        self.assertTrue(result["preserved"])
        self.assertTrue(result["updateRestored"])
        accepted = self.isolated("fixtureMessages.filter(message => message.event === 'accepted')")
        self.assertEqual(len(accepted), 1)
        self.assertEqual(accepted[0]["data"]["receipt"]["status"], "Verified")
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "editor-source")
        self.assertEqual(self.page.evaluate("window.editorProbeHistory()")["undone"], INITIAL)

        # A page may synchronously revoke IME evidence while a capture reply is
        # returning across worlds. The production port must not publish its text.
        self.page.evaluate("""() => {
          const root = document.querySelector('#editor-source');
          const revoke = event => {
            const response = JSON.parse(event.detail);
            if (typeof response.result?.text !== 'string') return;
            root.removeEventListener('typelay-editor-response', revoke);
            root.dispatchEvent(new CompositionEvent('compositionstart', {bubbles: true}));
            root.dispatchEvent(new CompositionEvent('compositionend', {bubbles: true}));
          };
          root.addEventListener('typelay-editor-response', revoke);
        }""")
        description = command("describe")
        self.assertIsNotNone(description)
        self.assertIsNone(
            command(
                "capture", {"target": description["target"], "authority": description["authority"]}
            )
        )

    def test_transaction_preserves_rich_spans_selection_and_native_undo(self):
        result = self.probe("guarded")
        self.assertEqual(result["status"], "Applied")
        self.assertEqual(result["after"], TRANSLATED)
        self.assertTrue(result["preserved"])
        self.assertTrue(result["updateRestored"])
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
        self.assertTrue(result["updateRestored"])
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

    def test_commit_guard_preserves_change_from_a_later_application_listener(self):
        result = self.probe("after-guard-change")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertTrue(result["reentered"])
        self.assertTrue(result["updateRestored"])
        self.assertEqual(result["after"], NEW_SOURCE)
        self.assertEqual(result["undone"], INITIAL)
        self.assertEqual(result["redone"], NEW_SOURCE)

    def test_removal_and_reinsertion_cannot_reuse_the_guarded_root(self):
        result = self.probe("bridge-reinsert")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertTrue(result["reentered"])
        self.assertTrue(result["connected"])
        self.assertEqual(result["after"], INITIAL)

    def test_unrelated_page_mutation_does_not_block_the_editor(self):
        result = self.probe("bridge-unrelated")
        self.assertEqual(result["status"], "Applied")
        self.assertEqual(result["after"], TRANSLATED)

    def test_ancestor_metadata_cannot_change_after_isolated_approval(self):
        result = self.probe("bridge-metadata-change")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertTrue(result["reentered"])
        self.assertEqual(result["after"], INITIAL)

    def test_synchronous_page_callback_cannot_outlive_the_real_deadline(self):
        result = self.probe("bridge-expire")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertTrue(result["reentered"])
        self.assertEqual(result["after"], INITIAL)

    def test_composition_cycle_after_approval_cannot_clear_the_sticky_veto(self):
        result = self.probe("bridge-composition-cycle")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertTrue(result["reentered"])
        self.assertFalse(result["composing"])
        self.assertEqual(result["after"], INITIAL)

    def test_guard_rechecks_after_page_observes_the_bridge_check(self):
        result = self.probe("bridge-check-change")
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertTrue(result["reentered"])
        self.assertTrue(result["updateRestored"])
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
