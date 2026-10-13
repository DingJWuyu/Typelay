"""Real Chromium DOM contract for the production content-script input adapter.

The fixture replaces only the extension message transport. Chromium owns DOM,
selection, keyboard dispatch, composition and synchronous mutation. No model is
loaded, no extension is installed and no user browser profile is opened. This
is not physical IME, native-host routing or complete desktop acceptance.
"""

import argparse
import os
import shutil
import tempfile
import unittest
from pathlib import Path

from browser_process import chromium_context
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[2]
CONTENT = ROOT / "src/TypeLay.Linux/browser/content.js"
FIXTURE = """<!doctype html><meta charset='utf-8'>
<label>Message <textarea id='source'></textarea></label>
<input id='other'><input id='password' type='password'>
<script>window.submitted = 0;</script>"""
TRANSPORT = """(() => {
  const output = [];
  let receive;
  window.chrome = {runtime: {connect: () => ({
    postMessage: message => output.push(message),
    onMessage: {addListener: callback => { receive = callback; }},
    onDisconnect: {addListener: () => {}}
  })}};
  window.fixtureMessages = output;
  window.fixtureHostClock = () => ({wallTimeMs: Date.now(), boottimeMs: Math.floor(performance.now()) + 1000000});
  window.fixtureCommand = message => {
    message.clock = fixtureHostClock();
    if (message.data?.permit) message.data.permit.expiresAtMs = message.clock.boottimeMs + 90;
    if (message.op === 'direct') message.data.expiresAtMs = message.clock.boottimeMs + 240;
    if (['overlay-render', 'overlay-renew'].includes(message.op))
      message.data.expiresAtMs = message.clock.boottimeMs + 800;
    receive(message);
    return output.findLast(item => item.id === message.id)?.data;
  };
})();"""


class BrowserContract(unittest.TestCase):
    executable = "/usr/bin/chromium"

    @classmethod
    def setUpClass(cls):
        cls.playwright = sync_playwright().start()
        cls.home = tempfile.TemporaryDirectory(prefix="typelay-browser-test-")

    @classmethod
    def tearDownClass(cls):
        cls.playwright.stop()
        cls.home.cleanup()

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(dir=self.home.name)
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        environment = {
            **os.environ,
            "HOME": str(root),
            "XDG_CONFIG_HOME": str(root / "config"),
            "XDG_CACHE_HOME": str(root / "cache"),
        }
        self.browser_scope = chromium_context(
            self.playwright, self.executable, root / "profile", environment
        )
        self.context = self.browser_scope.__enter__()
        self.addCleanup(self.browser_scope.__exit__, None, None, None)
        clock_setup = ""
        if self._testMethodName == "test_coarse_privacy_timer_refuses_capture":
            # Capability fault in the real browser, before production caches its
            # clock. A 100ms privacy clock cannot grant a 90ms write permit.
            clock_setup = """(() => {
              const nativeNow = performance.now.bind(performance);
              let origin = 0;
              window.fixtureBeginClockBucket = () => { origin = nativeNow(); };
              Object.defineProperty(performance, 'now', {
                value: () => origin + Math.floor((nativeNow() - origin) / 100) * 100});
            })();"""
        self.context.add_init_script(clock_setup + TRANSPORT + "\n" + CONTENT.read_text())
        self.page = self.context.new_page()
        self.page.route(
            "http://127.0.0.1/fixture",
            lambda route: route.fulfill(body=FIXTURE, content_type="text/html"),
        )
        self.page.goto("http://127.0.0.1/fixture")
        self.sequence = 0
        self.page.locator("#source").focus()

    def command(self, operation, data=None):
        self.sequence += 1
        return self.page.evaluate(
            "message => window.fixtureCommand(message)",
            {"id": self.sequence, "op": operation, "data": data or {}},
        )

    def capture(self):
        description = self.command("describe")
        self.assertIsNotNone(description)
        return self.command(
            "capture", {"target": description["target"], "authority": description["authority"]}
        )

    def type_source(self, text="Hello"):
        self.page.keyboard.insert_text(text)
        snapshot = self.capture()
        self.assertIsNotNone(snapshot)
        return snapshot

    @staticmethod
    def plan(snapshot, replacement="你好", scope=None):
        # Explicit literals in tests validate UTF-16 boundaries and the expected result.
        expected = {
            "target": {"backend": "linux-browser", "value": snapshot["target"], "lifetime": "test"},
            "text": snapshot["text"],
            "selection": snapshot["selection"],
            "observedAtMs": snapshot["observedAtMs"],
        }
        return {
            "id": "test-plan",
            "expected": expected,
            "replacement": replacement,
            "scope": scope,
            "resultText": replacement,
            "caret": len(replacement),
        }

    def publish(self, snapshot, plan):
        self.command(
            "publish",
            {
                "target": snapshot["target"],
                "authority": snapshot["authority"],
                "permit": {"plan": plan},
                "lease": {"source": {"observedAtMs": snapshot["observedAtMs"]}},
            },
        )

    def accepted(self):
        return self.page.evaluate(
            "window.fixtureMessages.filter(message => message.event === 'accepted')"
        )

    def test_unknown_ime_never_reads_body_then_trusted_input_allows_acceptance(self):
        self.assertIsNone(self.capture())
        snapshot = self.type_source()
        self.publish(snapshot, self.plan(snapshot))
        self.page.keyboard.press("Tab")
        self.assertEqual(self.page.locator("#source").input_value(), "你好")
        self.assertEqual(len(self.accepted()), 1)
        self.assertEqual(self.accepted()[0]["data"]["receipt"]["status"], "Verified")
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        self.assertEqual(self.page.evaluate("window.submitted"), 0)

    def test_capture_authority_survives_host_wall_quantization_rebase(self):
        self.page.keyboard.insert_text("Hello")
        result = self.page.evaluate("""() => {
          fixtureHostClock = () => ({wallTimeMs: Date.now() - 2, boottimeMs: 1000403});
          const description = fixtureCommand({id: 910, op: 'describe'});
          let sampledAt;
          fixtureHostClock = () => {
            sampledAt = performance.now();
            return {wallTimeMs: Date.now(), boottimeMs: 1000404};
          };
          const snapshot = fixtureCommand({id: 911, op: 'capture', data: {
            target: description.target, authority: description.authority}});
          // The host continues advancing during capture/geometry processing.
          // Include its <1ms integer sampling error and the measured elapsed time.
          const hostUpperAtReturn = 1000404 + 1 + Math.ceil(performance.now() - sampledAt);
          return {snapshot, descriptionStamp: description.authority.observedAtMs, hostUpperAtReturn};
        }""")
        self.assertEqual(result["descriptionStamp"], 1000403)
        self.assertIsNotNone(result["snapshot"])
        self.assertEqual(result["snapshot"]["text"], "Hello")
        self.assertLessEqual(result["snapshot"]["observedAtMs"], result["hostUpperAtReturn"])

    def test_source_capture_reuses_description_with_live_focus_and_metadata_checks(self):
        self.page.keyboard.insert_text("Hello")
        result = self.page.evaluate("""() => {
          const description = fixtureCommand({id: 940, op: 'describe'});
          const first = fixtureCommand({id: 941, op: 'capture', data: {
            target: description.target, authority: description.authority, requireFocused: true}});
          if (!first) return {first: false};
          const second = fixtureCommand({id: 942, op: 'capture', data: {
            target: first.target, authority: first.authority, requireFocused: true}});
          if (!second) return {first: true, second: false};
          window.fixtureCachedCapture = {target: second.target, authority: second.authority, requireFocused: true};
          window.fixtureCaptureExpires = performance.now() + 150;
          document.querySelector('#other').focus();
          const unfocused = fixtureCommand({id: 943, op: 'capture', data: {
            target: second.target, authority: second.authority, requireFocused: true}});
          document.querySelector('#source').focus();
          document.querySelector('#source').setAttribute('autocomplete', 'current-password');
          const sensitive = fixtureCommand({id: 944, op: 'capture', data: {
            target: second.target, authority: second.authority, requireFocused: true}});
          return {first: true, second: true, text: second.text,
            renewed: first.authority.token !== second.authority.token, unfocused, sensitive};
        }""")
        self.assertTrue(result["first"])
        self.assertTrue(result.get("second"))
        self.assertEqual(result["text"], "Hello")
        self.assertTrue(result["renewed"])
        self.assertIsNone(result["unfocused"])
        self.assertIsNone(result["sensitive"])
        self.page.wait_for_function("performance.now() > fixtureCaptureExpires", timeout=1000)
        expired = self.page.evaluate("""() => {
          document.querySelector('#source').removeAttribute('autocomplete');
          const snapshot = fixtureCommand({id: 945, op: 'capture', data: fixtureCachedCapture});
          return {snapshot, reason: fixtureCommand({id: 946, op: 'describe'}).diagnostics.lastCaptureReason};
        }""")
        self.assertIsNone(expired["snapshot"])
        self.assertEqual(expired["reason"], "authorityExpired")

    def test_unrelated_clock_rebase_cannot_extend_published_deadline(self):
        self.page.keyboard.insert_text("Hello")
        template = self.plan({"target": "", "text": "", "selection": {}, "observedAtMs": 0})
        # This transport contract does not model Python/CDP round-trip latency.
        # Establish the one approved capture and initial permit in one browser turn.
        published = self.page.evaluate(
            """plan => {
              const started = performance.now();
              const description = fixtureCommand({id: 918, op: 'describe'});
              const snapshot = description && fixtureCommand({id: 919, op: 'capture', data: {
                target: description.target, authority: description.authority}});
              if (!snapshot) return {captured: false, diagnostic: description?.diagnostics};
              plan.expected = {...plan.expected, target: {...plan.expected.target, value: snapshot.target},
                text: snapshot.text, selection: snapshot.selection, observedAtMs: snapshot.observedAtMs};
              const command = {id: 920, op: 'publish', data: {
                target: snapshot.target, authority: snapshot.authority, permit: {plan}}};
              const result = fixtureCommand(command);
              window.fixtureOriginalDeadline = command.data.permit.expiresAtMs - 1000000;
              const observed = fixtureCommand({id: 922, op: 'describe'});
              return {...result, captured: true, publication: observed?.publication,
                acknowledged: fixtureMessages.findLast(item => item.event === 'publication')?.data,
                expiresAtMs: command.data.permit.expiresAtMs,
                diagnostic: {...observed?.diagnostics, elapsedMs: performance.now() - started,
                  focused: document.hasFocus(), activeIsSource: document.activeElement.id === 'source'}};
            }""",
            template,
        )
        diagnostic = str(published.get("diagnostic"))
        self.assertTrue(published["captured"], diagnostic)
        self.assertTrue(published["published"], diagnostic)
        self.assertTrue(published["publication"]["ready"], diagnostic)
        self.assertTrue(published["acknowledged"]["ready"], diagnostic)
        self.assertEqual(published["acknowledged"]["planId"], "test-plan")
        self.assertEqual(published["publication"]["planId"], "test-plan")
        self.assertLessEqual(published["publication"]["readyUntilHostMs"], published["expiresAtMs"])
        # Wait on the actual issued host deadline, never on an arbitrary sleep.
        self.page.wait_for_function("performance.now() > fixtureOriginalDeadline + 2", timeout=1000)
        publication = self.page.evaluate("""() => {
          const original = fixtureHostClock;
          fixtureHostClock = () => { const clock = original(); clock.boottimeMs -= 30; return clock; };
          return fixtureCommand({id: 921, op: 'describe'}).publication;
        }""")
        self.assertFalse(publication["ready"])
        self.assertIsNone(publication["readyUntilHostMs"])
        self.page.keyboard.press("Tab")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "other")

    def test_modified_authority_token_cannot_establish_clock(self):
        self.page.keyboard.insert_text("Hello")
        description = self.command("describe")
        self.assertIsNotNone(description)
        description["authority"]["contentRevision"] += 1
        self.assertIsNone(
            self.command(
                "capture", {"target": description["target"], "authority": description["authority"]}
            )
        )

    def test_coarse_privacy_timer_refuses_capture(self):
        self.page.keyboard.insert_text("Hello")
        # Start a real 100ms bucket and echo the new authority in the same page
        # turn. A Python/CDP round trip could instead cross a bucket boundary
        # and correctly reject wall/monotonic disagreement before the precision
        # capability check that this case isolates.
        result = self.page.evaluate("""() => {
          fixtureBeginClockBucket();
          const description = fixtureCommand({id: 930, op: 'describe'});
          const capture = fixtureCommand({id: 931, op: 'capture', data: {
            target: description.target, authority: description.authority}});
          return {capture, diagnostics: fixtureCommand({id: 932, op: 'describe'}).diagnostics};
        }""")
        self.assertIsNone(result["capture"])
        diagnostics = result["diagnostics"]
        self.assertEqual(diagnostics["captureCounts"]["timerPrecision"], 1)
        self.assertEqual(diagnostics["captureCounts"]["supported"], 0)
        tick = diagnostics["timerTickMs"]
        self.assertTrue(tick is None or tick >= 100)
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_stale_value_and_dom_replacement_are_rejected_without_consuming_tab(self):
        snapshot = self.type_source()
        self.publish(snapshot, self.plan(snapshot))
        self.page.evaluate("document.querySelector('#source').value = 'New user text'")
        self.page.keyboard.press("Tab")
        self.assertEqual(self.page.locator("#source").input_value(), "New user text")
        self.assertEqual(self.accepted(), [])
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "other")
        self.page.evaluate("""() => {
          const old = document.querySelector('#source');
          const replacement = old.cloneNode(); old.replaceWith(replacement); replacement.focus();
        }""")
        self.assertIsNone(self.command("describe", {"target": snapshot["target"]}))
        self.assertIsNone(self.capture())

    def test_composition_revokes_a_published_candidate(self):
        snapshot = self.type_source()
        self.publish(snapshot, self.plan(snapshot))
        cdp = self.context.new_cdp_session(self.page)
        cdp.send("Input.imeSetComposition", {"text": "ni", "selectionStart": 2, "selectionEnd": 2})
        self.assertIsNone(self.capture())
        self.page.keyboard.press("Tab")
        self.assertEqual(self.accepted(), [])
        self.assertNotEqual(self.page.locator("#source").input_value(), "你好")

    def test_direct_scope_preserves_emoji_and_quarantines_reentrant_page_changes(self):
        self.type_source("A😀中B")
        self.page.evaluate("document.querySelector('#source').setSelectionRange(3, 4)")
        snapshot = self.capture()
        plan = self.plan(snapshot, "文", {"start": 3, "end": 4})
        plan.update(resultText="A😀文B", caret=4)
        result = self.command(
            "direct",
            {
                "target": snapshot["target"],
                "plan": plan,
                "authority": snapshot["authority"],
                "approved": snapshot["authority"],
            },
        )
        self.assertEqual(result["status"], "Verified")
        self.assertEqual(self.page.locator("#source").input_value(), "A😀文B")
        self.page.locator("#source").fill("Another message")
        snapshot = self.capture()
        plan = self.plan(snapshot)
        plan["id"] = "second-plan"
        self.page.evaluate("""document.querySelector('#source').addEventListener('input', event => {
          if (event.inputType === 'insertReplacementText') event.target.value = 'Page changed it';
        })""")
        result = self.command(
            "direct",
            {
                "target": snapshot["target"],
                "plan": plan,
                "authority": snapshot["authority"],
                "approved": snapshot["authority"],
            },
        )
        self.assertEqual(result["status"], "OutcomeUnknown")
        self.assertEqual(self.page.locator("#source").input_value(), "Page changed it")
        self.assertFalse(self.capture()["writeSupported"])

    def another_tab(self):
        second = self.context.new_page()
        second.route(
            "http://127.0.0.1/second",
            lambda route: route.fulfill(body=FIXTURE, content_type="text/html"),
        )
        second.goto("http://127.0.0.1/second")
        second.bring_to_front()
        second.locator("#source").focus()
        second.keyboard.insert_text("Second tab")
        self.assertFalse(self.page.evaluate("document.hasFocus()"))
        return second

    def test_frozen_direct_replaces_only_original_field_after_tab_switch(self):
        snapshot = self.type_source()
        second = self.another_tab()
        description = self.command("describe", {"target": snapshot["target"]})
        self.assertIsNotNone(description)
        result = self.command(
            "direct",
            {
                "target": snapshot["target"],
                "plan": self.plan(snapshot),
                "authority": snapshot["authority"],
                "approved": description["authority"],
            },
        )
        self.assertEqual(result["status"], "Verified")
        self.assertEqual(self.page.locator("#source").input_value(), "你好")
        self.assertEqual(second.locator("#source").input_value(), "Second tab")
        self.assertTrue(second.evaluate("document.hasFocus()"))

    def test_composing_remains_a_veto_across_blur_without_trusted_end(self):
        snapshot = self.type_source()
        # Synthetic composition can revoke authority but can never restore it.
        # No real IME was started, so tab blur cannot legitimately end this
        # observation by committing the browser's own active composition.
        self.page.evaluate("""document.querySelector('#source').dispatchEvent(
          new CompositionEvent('compositionstart', {bubbles:true, data:'ni'}))""")
        second = self.another_tab()
        description = self.command("describe", {"target": snapshot["target"]})
        self.assertTrue(description["metadata"]["hasTextEdit"])
        result = self.command(
            "direct",
            {
                "target": snapshot["target"],
                "plan": self.plan(snapshot),
                "authority": snapshot["authority"],
                "approved": description["authority"],
            },
        )
        self.assertEqual(result["status"], "RejectedBeforeMutation")
        self.assertEqual(result["reason"], "ImeUnclear")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(second.locator("#source").input_value(), "Second tab")

    def test_editor_draft_survives_render_and_two_separate_tabs_return_then_accept(self):
        self.page.locator("#source").evaluate(
            "element => {element.style.marginTop = '220px'; element.style.width = '480px';}"
        )
        snapshot = self.type_source()
        revision = snapshot["authority"]["inputRevision"]
        view = {
            "presentation": {"visible": True, "editorOwnsText": False, "tabHint": "Tab 接受译文"},
            "text": "Initial candidate",
            "editable": True,
            "state": "ValidTranslation",
            "sourceLanguage": "en",
            "targetLanguage": "zh",
            "status": "译文就绪",
        }
        data = {
            "target": snapshot["target"],
            "authority": snapshot["authority"],
            "view": view,
            "revision": 1,
            "theme": "light",
        }
        self.assertTrue(self.command("overlay-render", data)["visible"])
        # The shared view's suggested accept hint cannot stand in for a native
        # publication. Read the rendered accessible text, including closed shadow DOM.
        accessibility = self.context.new_cdp_session(self.page).send("Accessibility.getFullAXTree")
        names = [node.get("name", {}).get("value") for node in accessibility["nodes"]]
        self.assertIn("Tab 交还应用", names)
        self.assertNotIn("Tab 接受译文", names)
        # The real pointer enters the closed-shadow editor; no test-only access
        # to its DOM, value or implementation is exposed by the adapter.
        box = self.page.locator("html > div").bounding_box()
        self.assertIsNotNone(box)
        self.page.mouse.click(box["x"] + 30, box["y"] + 55)
        self.assertTrue(
            self.page.evaluate("""window.fixtureMessages.some(message =>
          message.event === 'ui' && message.data.operation === 'beginEdit')""")
        )
        view["presentation"]["editorOwnsText"] = True
        view["state"] = "EditingTranslation"
        data["revision"] = 2
        self.command("overlay-render", data)
        self.command("overlay-edit", {"target": snapshot["target"], "allowed": True})
        self.page.keyboard.press("Control+A")
        self.page.keyboard.insert_text("Human draft")
        view["text"] = "Stale remote candidate"
        data["revision"] = 3
        self.command("overlay-render", data)
        self.page.keyboard.press("End")
        self.page.keyboard.insert_text("!")
        draft = self.page.evaluate("""window.fixtureMessages.findLast(message =>
          message.event === 'ui' && message.data.operation === 'draft').data.value""")
        self.assertEqual(draft, "Human draft!")
        self.page.keyboard.press("Tab")
        self.assertTrue(
            self.page.evaluate("""window.fixtureMessages.some(message =>
          message.event === 'ui' && message.data.operation === 'return')""")
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        description = self.command("describe", {"target": snapshot["target"]})
        self.assertTrue(
            self.command(
                "return",
                {
                    "target": snapshot["target"],
                    "authority": description["authority"],
                },
            )["returned"]
        )
        returned = self.capture()
        self.assertEqual(returned["selection"], snapshot["selection"])
        self.assertEqual(returned["authority"]["inputRevision"], revision)
        self.publish(returned, self.plan(returned, draft))
        self.page.keyboard.press("Tab")
        self.assertEqual(self.page.locator("#source").input_value(), "Human draft!")
        self.assertEqual(len(self.accepted()), 1)

    def test_untrusted_tab_and_password_are_not_adapted(self):
        snapshot = self.type_source()
        self.publish(snapshot, self.plan(snapshot))
        self.page.evaluate("""document.querySelector('#source').dispatchEvent(
          new KeyboardEvent('keydown', {key:'Tab', code:'Tab', bubbles:true, cancelable:true}))""")
        self.assertEqual(self.accepted(), [])
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.page.locator("#password").focus()
        self.assertIsNone(self.command("describe"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--chromium", default=shutil.which("chromium") or "/usr/bin/chromium")
    arguments, remaining = parser.parse_known_args()
    BrowserContract.executable = arguments.chromium
    unittest.main(argv=[__file__, *remaining])
