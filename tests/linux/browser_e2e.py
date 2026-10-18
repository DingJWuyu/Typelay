"""Real Chromium DOM contract for the production content-script input adapter.

The fixture replaces only the extension message transport. Chromium owns DOM,
selection, keyboard dispatch, composition and synchronous mutation. No model is
loaded, no extension is installed and no user browser profile is opened. This
is not physical IME, native-host routing or complete desktop acceptance.
"""

import argparse
import base64
import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path

from playwright.sync_api import sync_playwright
from support.browser_process import chromium_context

ROOT = Path(__file__).resolve().parents[2]
CONTENT = ROOT / "src/shared/browser/content.js"
BACKGROUND = ROOT / "src/shared/browser/background.js"
BRIDGE = ROOT / "src/shared/browser/editor-bridge.js"
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
  window.fixtureF24Events = [];
  // Observe browser-dispatched input before the adapter can consume it.
  for (const type of ['keydown', 'keyup']) window.addEventListener(type, event => {
    if (event.code === 'F24') fixtureF24Events.push({type: event.type, key: event.key,
      code: event.code, repeat: event.repeat, trusted: event.isTrusted});
  }, true);
  window.fixtureHostClock = () => ({wallTimeMs: Date.now(), boottimeMs: Math.floor(performance.now()) + 1000000});
  window.fixtureBindings = {generation: 'browser-default', sessionEnabled: true,
    enabled: false, accept: 'Tab', exit: 'Esc'};
  let revision = 0;
  // Test host builders emit the current protocol. Raw commands below retain
  // exact missing/stale fields for fail-closed compatibility regression cases.
  window.fixtureCommand = message => {
    const data = message.data ||= {};
    if (message.op === 'configure-shortcuts') window.fixtureBindings = {...data};
    const bindings = window.fixtureBindings;
    if (['publish', 'direct', 'return', 'overlay-render', 'overlay-renew',
      'overlay-edit', 'overlay-resume'].includes(message.op)) data.generation ??= bindings.generation;
    if (message.op === 'overlay-render') revision = Math.max(revision, data.revision || 0);
    if (message.op === 'overlay-render' && data.view?.state !== 'Paused') {
      const view = data.view;
      view.viewContext ??= {token: {target: {backend: 'linux-browser', value: data.target, lifetime: 'test'},
        commandGeneration: data.generation, revision: data.revision},
        bindings: {accept: bindings.accept, exit: bindings.exit}, canEdit: !!view.editable};
      view.sessionShortcuts ??= {acceptLabel: '接受译文', acceptKey: bindings.accept,
        acceptHint: `${bindings.accept} 接受译文`, passThroughHint: `${bindings.accept} 交还应用`,
        returnHint: `${bindings.accept} 返回原输入框`, exitLabel: '退出当前翻译',
        exitKey: bindings.exit, exitHint: `${bindings.exit} 退出当前翻译`};
    }
    if (['overlay-edit', 'overlay-resume'].includes(message.op)) data.revision ??= revision;
    return window.fixtureRawCommand(message);
  };
  window.fixtureRawCommand = message => {
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
        bridge = (
            BRIDGE.read_text()
            if self._testMethodName
            in (
                "test_paused_page_probe_cannot_outlive_display_deadline",
                "test_rich_field_does_not_advertise_standard_source_local_acceptance",
            )
            else ""
        )
        self.context.add_init_script(
            clock_setup + TRANSPORT + "\n" + bridge + "\n" + CONTENT.read_text()
        )
        self.page = self.context.new_page()
        self.cdp = self.context.new_cdp_session(self.page)
        self.page.route(
            "http://127.0.0.1/fixture",
            lambda route: route.fulfill(body=FIXTURE, content_type="text/html"),
        )
        self.page.goto("http://127.0.0.1/fixture")
        self.sequence = 0
        self.command(
            "configure-shortcuts",
            {
                "generation": "browser-default",
                "sessionEnabled": True,
                "enabled": False,
                "accept": "Tab",
                "exit": "Esc",
            },
        )
        self.page.locator("#source").focus()

    def command(self, operation, data=None):
        self.sequence += 1
        return self.page.evaluate(
            "message => window.fixtureCommand(message)",
            {"id": self.sequence, "op": operation, "data": data or {}},
        )

    def capture(self):
        # Keep setup inside one browser turn so Python/CDP round trips do not
        # consume the production authority's unchanged lifetime. Never retry.
        self.sequence += 3
        result = self.page.evaluate(
            """id => {
              const description = fixtureCommand({id, op: 'describe'});
              if (!description) return {described: false, snapshot: null, diagnostics: null};
              const snapshot = fixtureCommand({id: id + 1, op: 'capture', data: {
                target: description.target, authority: description.authority}});
              const diagnostics = snapshot ? null : fixtureCommand({id: id + 2,
                op: 'describe', data: {target: description.target}})?.diagnostics ?? null;
              return {described: true, snapshot, diagnostics};
            }""",
            self.sequence - 2,
        )
        self.capture_diagnostics = result["diagnostics"]
        self.assertTrue(result["described"], self.capture_diagnostics)
        return result["snapshot"]

    def type_source(self, text="Hello"):
        self.page.keyboard.insert_text(text)
        snapshot = self.capture()
        self.assertIsNotNone(snapshot, self.capture_diagnostics)
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

    def session_key(self, key, action, *, repeat=False):
        if key != "F24":
            getattr(self.page.keyboard, action)(key)
            return
        # Playwright's key table stops at F12. Use Chromium's public input
        # protocol for F24, not a page-created KeyboardEvent. No native keycode
        # is guessed: key/code and the platform-independent VKEY_F24 suffice.
        self.cdp.send(
            "Input.dispatchKeyEvent",
            {
                "type": "rawKeyDown" if action == "down" else "keyUp",
                "key": "F24",
                "code": "F24",
                "windowsVirtualKeyCode": 0x87,
                "modifiers": 0,
                "autoRepeat": repeat,
            },
        )

    def assert_f24_events(self, events):
        self.assertEqual(
            self.page.evaluate("window.fixtureF24Events"),
            [
                {"type": event, "key": "F24", "code": "F24", "repeat": repeat, "trusted": True}
                for event, repeat in events
            ],
        )

    def test_document_applies_session_bindings_independently_of_on_demand_mode(self):
        self.command(
            "configure-shortcuts",
            {
                "generation": "session-f1-f24",
                "enabled": False,
                "sessionEnabled": True,
                "accept": "F1",
                "exit": "F24",
            },
        )
        snapshot = self.type_source()
        self.assertEqual(
            snapshot.get("sessionBindings"),
            {
                "generation": "session-f1-f24",
                "accept": "F1",
                "exit": "F24",
                "sourceReady": True,
                "editorReady": True,
                "shortcutDispatch": "page",
                "target": snapshot["target"],
                "sourceLocalAcceptanceVersion": 0,
            },
        )
        self.command(
            "publish",
            {
                "target": snapshot["target"],
                "authority": snapshot["authority"],
                "generation": "session-f1-f24",
                "permit": {"plan": self.plan(snapshot)},
            },
        )
        self.page.keyboard.press("F1")
        self.assertEqual(self.page.locator("#source").input_value(), "你好")
        self.assertEqual(len(self.accepted()), 1)

    def test_source_local_acceptance_requires_explicit_native_version_and_supported_keys(self):
        self.type_source()
        cases = [
            ({}, 0),
            ({"sourceLocalAcceptanceVersion": 1}, 0),
            ({"shortcutDispatch": "native"}, 0),
            ({"shortcutDispatch": "native", "sourceLocalAcceptanceVersion": 1}, 1),
            ({"shortcutDispatch": "source-local", "sourceLocalAcceptanceVersion": 1}, 1),
            ({"shortcutDispatch": "native", "sourceLocalAcceptanceVersion": 2}, 0),
            ({"shortcutDispatch": "native", "sourceLocalAcceptanceVersion": "1"}, 0),
            (
                {
                    "shortcutDispatch": "native",
                    "sourceLocalAcceptanceVersion": 1,
                    "sessionEnabled": False,
                },
                0,
            ),
            (
                {
                    "shortcutDispatch": "native",
                    "sourceLocalAcceptanceVersion": 1,
                    "accept": "Shift+F7",
                },
                0,
            ),
        ]
        for change, expected in cases:
            with self.subTest(configuration=change):
                self.command(
                    "configure-shortcuts",
                    {
                        "generation": "source-local",
                        "sessionEnabled": True,
                        "enabled": False,
                        "accept": "Tab",
                        "exit": "Esc",
                        **change,
                    },
                )
                description = self.command("describe")
                self.assertEqual(description["sessionBindings"]["target"], description["target"])
                self.assertEqual(
                    description["sessionBindings"].get("sourceLocalAcceptanceVersion", 0), expected
                )

    def test_source_local_acceptance_acknowledges_each_actual_standard_field(self):
        self.command(
            "configure-shortcuts",
            {
                "generation": "source-local",
                "sessionEnabled": True,
                "enabled": False,
                "accept": "Ctrl+F7",
                "exit": "Ctrl+F8",
                "shortcutDispatch": "native",
                "sourceLocalAcceptanceVersion": 1,
            },
        )
        targets = []
        for selector in ("#source", "#other"):
            self.page.locator(selector).focus()
            self.page.keyboard.insert_text("Hello")
            source = self.capture()
            targets.append(source["target"])
            self.assertEqual(source["sessionBindings"]["target"], source["target"])
            self.assertEqual(source["sessionBindings"].get("sourceLocalAcceptanceVersion", 0), 1)
        self.assertNotEqual(*targets)

    def test_rich_field_does_not_advertise_standard_source_local_acceptance(self):
        self.page.evaluate("""() => {
          const root = document.createElement('div');
          root.className = 'ProseMirror'; root.contentEditable = 'true';
          root.style.cssText = 'width:480px;min-height:80px';
          document.body.append(root);
          root.addEventListener('typelay-editor-request', event => {
            const request = JSON.parse(event.detail);
            if (request.op === 'probe') root.dispatchEvent(new CustomEvent('typelay-editor-response', {
              bubbles: true, detail: JSON.stringify({id: request.id,
                result: {kind: 'tiptap', supported: true}})}));
          });
          root.focus();
        }""")
        self.command(
            "configure-shortcuts",
            {
                "generation": "source-local-rich",
                "sessionEnabled": True,
                "enabled": False,
                "accept": "Tab",
                "exit": "Esc",
                "shortcutDispatch": "native",
                "sourceLocalAcceptanceVersion": 1,
            },
        )
        description = self.command("describe")
        self.assertIsNotNone(description)
        self.assertEqual(description["sessionBindings"]["target"], description["target"])
        self.assertEqual(description["sessionBindings"]["sourceLocalAcceptanceVersion"], 0)

    def test_background_reconnect_cannot_assign_a_late_reply_to_a_new_request(self):
        result = self.page.evaluate(
            """source => {
              const event = () => {
                const listeners = [];
                return {addListener: callback => listeners.push(callback),
                  emit: message => listeners.forEach(callback => callback(message))};
              };
              const port = () => ({onMessage: event(), onDisconnect: event(), sent: [],
                postMessage(message) { this.sent.push(message); }, disconnect() {}});
              const connections = [], onConnect = event();
              const extensionId = 'a'.repeat(32), documentToken = '1'.repeat(36);
              let clock = 0;
              const chrome = {runtime: {id: extensionId, onConnect,
                connectNative() { const connection = port(); connections.push(connection); return connection; }}};
              // Run the production extension listener; only browser transport
              // ports and the reconnect clock are controlled at their boundary.
              new Function('chrome', 'performance', source)(chrome, {now: () => clock});
              const document = port();
              document.name = 'typelay-document';
              document.sender = {id: extensionId, frameId: 0, documentId: 'browser-document',
                tab: {id: 1, windowId: 1}, url: 'https://example.test/'};
              onConnect.emit(document);
              document.onMessage.emit({event: 'document', data: documentToken});
              document.onMessage.emit({event: 'focus', data: true});
              connections[0].onMessage.emit({id: 1, op: 'describe'});
              const previousId = document.sent.at(-1).id;
              connections[0].onDisconnect.emit();
              clock = 2100;
              document.onMessage.emit({event: 'focus', data: true});
              connections[1].onMessage.emit({op: 'configure-shortcuts', data: {shortcutDispatch: 'native'}});
              connections[1].onMessage.emit({id: 1, op: 'capture', data: {target: documentToken + ':1'}});
              const currentId = document.sent.at(-1).id;
              document.onMessage.emit({id: previousId, data: {oldConnectionReply: true}});
              document.onMessage.emit({id: currentId, data: {currentConnectionReply: true}});
              const replies = connections[1].sent.filter(message => message.id !== undefined);
              const unproved = port();
              unproved.name = 'typelay-document';
              unproved.sender = {...document.sender, documentId: undefined, tab: {id: 2, windowId: 1}};
              onConnect.emit(unproved);
              unproved.onMessage.emit({event: 'document', data: '2'.repeat(36)});
              unproved.onMessage.emit({event: 'focus', data: true});
              connections[1].onMessage.emit({op: 'configure-shortcuts', data: {shortcutDispatch: 'source-local'}});
              connections[1].onMessage.emit({id: 2, op: 'prepare-command-owner'});
              const blocked = connections[1].sent.at(-1);
              // Retire the previous document's owner even after active focus moved.
              connections[1].onMessage.emit({id: 3, op: 'deactivate-command-owner', data: {documentToken}});
              const retirement = document.sent.at(-1);
              document.onMessage.emit({id: retirement.id, data: {retired: true}});
              return {replies, blocked, retirement, routedReply: connections[1].sent.at(-1),
                extensionId, documentToken};
            }""",
            BACKGROUND.read_text(),
        )
        self.assertEqual(len(result["replies"]), 1)
        response = result["replies"][0]
        self.assertEqual(response["id"], 1)
        self.assertEqual(response["data"], {"currentConnectionReply": True})
        self.assertEqual(response["route"]["extensionId"], result["extensionId"])
        self.assertEqual(response["route"]["documentToken"], result["documentToken"])
        self.assertEqual(result["blocked"], {"id": 2, "data": None})
        self.assertEqual(result["retirement"]["op"], "deactivate-command-owner")
        self.assertEqual(result["routedReply"]["id"], 3)
        self.assertEqual(result["routedReply"]["data"], {"retired": True})
        self.assertEqual(result["routedReply"]["route"]["documentToken"], result["documentToken"])

    def activate_source_command_owner(
        self, owner_id="test-owner", released_at=None, trace_command=False
    ):
        return self.page.evaluate(
            """request => {
              if (!window.fixturePassedCommandKeys) {
                window.fixturePassedCommandKeys = [];
                for (const kind of ['keydown', 'keyup']) window.addEventListener(kind, event => {
                  if (['F9', 'F10'].includes(event.key)) fixturePassedCommandKeys.push(event.type);
                }, true);
              }
              fixtureCommand({id: 7800, op: 'configure-shortcuts', data: {
                generation: 'source-command', enabled: true, sessionEnabled: true, traceCommand: request.traceCommand,
                accept: 'Tab', exit: 'Esc', shortcutDispatch: 'source-local',
                sourceLocalAcceptanceVersion: 1, wake: {keyval: 65478, modifiers: 0},
                direct: {keyval: 65479, modifiers: 0}}});
              const prepared = fixtureCommand({id: 7801, op: 'prepare-command-owner', data: {
                ownerId: request.ownerId, generation: 'source-command'}});
              if (!prepared) return null;
              const stamp = fixtureHostClock().boottimeMs;
              window.fixtureOwner = {ownerId: request.ownerId, generation: 'source-command',
                documentToken: prepared.target.split(':')[0], target: prepared.target,
                authority: prepared.authority, grantSequence: 1, expiresAtMs: stamp + 800,
                releasedAtMs: request.releasedAtMs ?? stamp};
              fixtureCommand({id: 7802, op: 'activate-command-owner', data: fixtureOwner});
              return {prepared, owner: fixtureOwner,
                ack: fixtureMessages.filter(item => item.event === 'command-owner').at(-1)?.data};
            }""",
            {"ownerId": owner_id, "releasedAtMs": released_at, "traceCommand": trace_command},
        )

    def source_commands(self):
        return self.page.evaluate("fixtureMessages.filter(item => item.event === 'source-command')")

    def test_source_command_captures_current_input_once_without_a_published_shortcut(self):
        self.type_source()
        result = self.activate_source_command_owner(trace_command=True)
        self.assertNotIn("text", result["prepared"])
        self.assertNotIn("selection", result["prepared"])
        self.assertEqual(result["prepared"]["commandLocalVersion"], 1)
        self.assertEqual(result["prepared"]["sessionBindings"]["sourceLocalAcceptanceVersion"], 1)
        self.assertEqual(result["ack"]["state"], "active")
        self.assertEqual(result["ack"]["target"], result["owner"]["target"])
        self.assertEqual(result["ack"]["policyToken"], result["owner"]["authority"]["token"])
        self.page.keyboard.insert_text(" now")
        self.page.keyboard.down("F10")
        self.page.keyboard.down("F10")
        self.page.keyboard.up("F10")
        commands = self.source_commands()
        self.assertEqual(len(commands), 1)
        command = commands[0]["data"]
        self.assertEqual(command["kind"], "direct")
        self.assertEqual(
            self.page.evaluate(
                "fixtureMessages.filter(item => item.event === 'source-command-diagnostic').map(item => item.data)"
            ),
            [{"kind": "direct", "firstReason": "Sent", "commandSent": True, "hasNative": True}],
        )
        self.assertEqual(command["native"]["text"], "Hello now")
        self.assertEqual(command["native"]["selection"], {"start": 9, "end": 9})
        self.assertEqual(command["policyToken"], result["owner"]["authority"]["token"])
        self.assertEqual(command["ownerGrantSequence"], 1)
        self.assertGreaterEqual(command["capturedAtMs"], command["triggeredAtMs"])
        self.assertLessEqual(command["captureUpperMs"], command["triggeredAtMs"] + 150)
        self.assertLessEqual(command["captureUpperMs"], result["owner"]["expiresAtMs"])
        self.assertLessEqual(
            command["captureUpperMs"], result["owner"]["authority"]["observedAtMs"] + 150
        )
        self.assertEqual(self.page.evaluate("fixturePassedCommandKeys"), [])
        self.assertEqual(self.page.locator("#source").input_value(), "Hello now")
        self.assertEqual(
            self.page.evaluate("fixtureMessages.filter(item => item.event === 'shortcut')"), []
        )
        self.assertTrue(
            self.page.evaluate("""fixtureMessages.findIndex(item => item.event === 'command-owner'
          && item.data.state === 'active') < fixtureMessages.findIndex(item => item.event === 'source-command')""")
        )

    def test_source_command_diagnostic_observes_unowned_original_key_without_a_command(self):
        self.type_source()
        self.command(
            "configure-shortcuts",
            {
                "generation": "diagnostic-only",
                "enabled": True,
                "sessionEnabled": True,
                "accept": "Tab",
                "exit": "Esc",
                "shortcutDispatch": "source-local",
                "traceCommand": True,
                "wake": {"keyval": 65478, "modifiers": 0},
                "direct": {"keyval": 65479, "modifiers": 0},
            },
        )
        self.page.keyboard.press("F10")
        self.assertEqual(self.source_commands(), [])
        self.assertEqual(
            self.page.evaluate(
                "fixtureMessages.filter(item => item.event === 'source-command-diagnostic').map(item => item.data)"
            ),
            [
                {
                    "kind": "direct",
                    "firstReason": "OwnerUnavailable",
                    "commandSent": False,
                    "hasNative": False,
                }
            ],
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")

    def test_source_command_owner_renewal_cannot_renew_policy_or_revive_a_retired_owner(self):
        self.type_source()
        owner = self.activate_source_command_owner()["owner"]
        self.page.wait_for_timeout(175)  # Expire only the original 150ms read policy.
        self.page.evaluate("""() => fixtureCommand({id: 7810, op: 'renew-command-owner', data: {
          ...fixtureOwner, grantSequence: 2, expiresAtMs: fixtureHostClock().boottimeMs + 800}})""")
        self.page.keyboard.press("F10")
        commands = self.source_commands()
        self.assertEqual(len(commands), 1)
        self.assertEqual(commands[0]["data"]["reason"], "PolicyUnavailable")
        self.assertEqual(
            self.page.evaluate(
                "fixtureMessages.filter(item => item.event === 'source-command-diagnostic')"
            ),
            [],
        )
        self.assertNotIn("native", commands[0]["data"])
        self.assertEqual(self.page.evaluate("fixturePassedCommandKeys"), [])
        self.command("deactivate-command-owner", {**owner, "grantSequence": 2})
        self.page.evaluate("""() => fixtureCommand({id: 7811, op: 'renew-command-owner', data: {
          ...fixtureOwner, grantSequence: 3, expiresAtMs: fixtureHostClock().boottimeMs + 800}})""")
        self.assertEqual(
            self.page.evaluate(
                "fixtureMessages.filter(item => item.event === 'command-owner').at(-1).data.state"
            ),
            "rejected",
        )
        self.page.keyboard.press("F9")
        self.assertEqual(len(self.source_commands()), 1)
        self.assertEqual(self.page.evaluate("fixturePassedCommandKeys"), ["keydown", "keyup"])
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")

    def test_source_command_requires_new_policy_after_focus_change(self):
        self.type_source()
        self.activate_source_command_owner()
        late = self.page.evaluate("""() => {
          document.getElementById('other').focus();
          document.getElementById('source').focus();
          return fixtureCommand({id: 7819, op: 'approve-command-policy', data: {
            target: fixtureOwner.target, authority: fixtureOwner.authority,
            ownerId: fixtureOwner.ownerId, generation: fixtureOwner.generation}});
        }""")
        self.assertEqual(late, {"applied": False})
        self.page.locator("#other").focus()
        self.page.keyboard.insert_text("Other")
        self.page.keyboard.press("F9")
        self.assertEqual(self.source_commands()[0]["data"]["reason"], "PolicyUnavailable")
        applied = self.page.evaluate("""() => {
          const description = fixtureCommand({id: 7820, op: 'describe'});
          return fixtureCommand({id: 7821, op: 'approve-command-policy', data: {
            target: description.target, authority: description.authority,
            ownerId: fixtureOwner.ownerId, generation: fixtureOwner.generation}});
        }""")
        self.assertTrue(applied["applied"])
        self.page.keyboard.press("F9")
        commands = self.source_commands()
        self.assertEqual(len(commands), 2)
        self.assertEqual(commands[1]["data"]["policyToken"], applied["policyToken"])
        self.assertEqual(commands[1]["data"]["native"]["target"], applied["target"])
        self.assertEqual(commands[1]["data"]["native"]["text"], "Other")
        self.assertGreater(commands[1]["data"]["commandId"], commands[0]["data"]["commandId"])

    def test_source_command_activation_requires_release_proof_frozen_when_keys_were_observed(self):
        # No describe/capture has calibrated the page when this press starts.
        self.page.keyboard.insert_text("Hello")
        self.page.keyboard.down("Shift")
        result = self.activate_source_command_owner()
        self.assertEqual(result["ack"]["state"], "rejected")
        self.page.keyboard.up("Shift")
        self.page.keyboard.down("Shift")
        result = self.activate_source_command_owner(released_at=0)
        self.assertEqual(result["ack"]["state"], "rejected")
        self.page.keyboard.up("Shift")
        result = self.activate_source_command_owner("released-owner")
        self.assertEqual(result["ack"]["state"], "active")
        self.page.keyboard.press("F10")
        self.assertEqual(len(self.source_commands()), 1)
        self.assertIn("native", self.source_commands()[0]["data"])

    def shortcut_snapshot(self, dispatch=None):
        return self.page.evaluate(
            """dispatch => {
              const configuration = {generation: 'shortcut-dispatch', enabled: true,
                sessionEnabled: true, accept: 'Tab', exit: 'Esc', wake: {keyval: 65478, modifiers: 0},
                direct: {keyval: 65479, modifiers: 0}};
              if (dispatch !== null) configuration.shortcutDispatch = dispatch;
              fixtureCommand({id: 701, op: 'configure-shortcuts', data: configuration});
              const description = fixtureCommand({id: 702, op: 'describe'});
              const snapshot = fixtureCommand({id: 703, op: 'capture', data: {
                target: description.target, authority: description.authority}});
              fixtureCommand({id: 704, op: 'publish-shortcut', data: {
                target: snapshot.target, authority: snapshot.authority,
                generation: configuration.generation}});
              return snapshot;
            }""",
            dispatch,
        )

    def test_page_shortcut_dispatch_remains_the_default(self):
        self.type_source()
        for key, kind in (("F9", "wake"), ("F10", "direct")):
            with self.subTest(key=key):
                snapshot = self.shortcut_snapshot()
                self.assertEqual(snapshot["sessionBindings"]["shortcutDispatch"], "page")
                self.page.keyboard.press(key)
                self.assertEqual(
                    self.page.evaluate(
                        "fixtureMessages.filter(item => item.event === 'shortcut').at(-1).data.kind"
                    ),
                    kind,
                )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")

    def test_native_shortcut_uses_one_reply_and_preserves_the_host_command(self):
        self.type_source()
        snapshot = self.shortcut_snapshot("native")
        self.assertEqual(snapshot["sessionBindings"]["shortcutDispatch"], "native")
        self.page.keyboard.press("F9")
        self.page.keyboard.press("F10")
        self.page.keyboard.press("Escape")  # Close Chromium's unclaimed F10 menu, if opened.
        self.page.locator("#source").focus()
        # Each request keeps its original authority while an ordinary same-source
        # publication may have a newer token. All work remains in one DOM turn.
        result = self.page.evaluate("""() => {
          const description = fixtureCommand({id: 711, op: 'describe'});
          const source = fixtureCommand({id: 712, op: 'capture', data: {
            target: description.target, authority: description.authority}});
          const renewed = fixtureCommand({id: 713, op: 'capture', data: {
            target: source.target, authority: source.authority}});
          fixtureCommand({id: 714, op: 'publish-shortcut', data: {
            target: renewed.target, authority: renewed.authority, generation: 'shortcut-dispatch'}});
          const triggeredAtMs = fixtureHostClock().boottimeMs;
          const response = fixtureCommand({id: 715, op: 'trigger-shortcut', data: {
            target: source.target, authority: source.authority, generation: 'shortcut-dispatch',
            kind: 'wake', commandId: 37, triggeredAtMs}});
          return {response, triggeredAtMs, target: source.target, text: source.text};
        }""")
        response = result["response"]
        self.assertTrue(response["triggered"])
        self.assertEqual(response["commandId"], 37)
        self.assertEqual(response["triggeredAtMs"], result["triggeredAtMs"])
        self.assertEqual(response["native"]["target"], result["target"])
        self.assertEqual(response["native"]["text"], result["text"])
        self.assertEqual(
            self.page.evaluate("fixtureMessages.filter(item => item.event === 'shortcut')"), []
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")

    def test_native_shortcut_rejects_changed_authority_and_expired_original_trigger(self):
        self.type_source()
        self.shortcut_snapshot("native")
        for fault in ("command", "authority", "trigger"):
            with self.subTest(fault=fault):
                result = self.page.evaluate(
                    """fault => {
                      const description = fixtureCommand({id: 721, op: 'describe'});
                      const source = fixtureCommand({id: 722, op: 'capture', data: {
                        target: description.target, authority: description.authority}});
                      fixtureCommand({id: 723, op: 'publish-shortcut', data: {
                        target: source.target, authority: source.authority,
                        generation: 'shortcut-dispatch'}});
                      const data = {target: source.target, authority: {...source.authority},
                        generation: 'shortcut-dispatch', kind: 'direct', commandId: 41,
                        triggeredAtMs: fixtureHostClock().boottimeMs};
                      if (fault === 'command') data.commandId = 0;
                      if (fault === 'authority') data.authority.inputRevision++;
                      if (fault === 'trigger') data.triggeredAtMs -= 151;
                      return {response: fixtureCommand({id: 724, op: 'trigger-shortcut', data}),
                        commandId: data.commandId, triggeredAtMs: data.triggeredAtMs};
                    }""",
                    fault,
                )
                self.assertEqual(
                    result["response"],
                    {
                        "triggered": False,
                        "commandId": result["commandId"],
                        "triggeredAtMs": result["triggeredAtMs"],
                    },
                )
        self.assertEqual(
            self.page.evaluate("fixtureMessages.filter(item => item.event === 'shortcut')"), []
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")

    def configure_control(self, *, enabled=False):
        self.command(
            "configure-shortcuts",
            {
                "generation": "control-source",
                "enabled": enabled,
                "sessionEnabled": True,
                "accept": "Ctrl+F7",
                "exit": "Ctrl+F8",
            },
        )
        self.page.evaluate("""() => {
          window.controlEvents = [];
          for (const type of ['keydown', 'keyup']) window.addEventListener(type, event => {
            if (['F7', 'F8'].includes(event.key)) controlEvents.push([type, event.key]);
          });
        }""")

    def publish_control(self, snapshot, plan=None):
        plan = plan or self.plan(snapshot)
        return self.command(
            "publish",
            {
                "target": snapshot["target"],
                "authority": snapshot["authority"],
                "permit": {"plan": plan},
                "lease": {
                    "source": plan["expected"],
                    "escapeAllowed": True,
                    "sessionRevision": 1,
                    "commandGeneration": "control-source",
                },
            },
        )

    def test_control_source_keeps_original_plan_and_release_ownership(self):
        self.configure_control()
        snapshot = self.type_source()
        self.assertTrue(snapshot["sessionBindings"]["sourceReady"])
        self.assertTrue(snapshot["sessionBindings"]["editorReady"])
        self.assertTrue(self.publish_control(snapshot)["published"])
        self.page.keyboard.down("Control")
        # A normal same-candidate host renewal cannot replace the old plan or
        # lend it a newer deadline, even with a fresh authority token.
        next_snapshot = self.capture()
        next_plan = self.plan(next_snapshot)
        next_plan["id"] = "new-plan"
        self.assertTrue(self.publish_control(next_snapshot, next_plan)["published"])
        self.page.keyboard.down("F7")
        self.page.keyboard.down("F7")
        self.page.keyboard.up("Control")
        self.page.keyboard.up("F7")
        self.assertEqual(self.page.locator("#source").input_value(), "你好")
        accepted = self.accepted()
        self.assertEqual(len(accepted), 1)
        self.assertEqual(accepted[0]["data"]["planId"], "test-plan")
        self.assertEqual(accepted[0]["data"]["receipt"]["status"], "Verified")
        self.assertEqual(self.page.evaluate("controlEvents"), [])

    def test_control_source_exit_and_ordinary_binding_restore(self):
        self.configure_control()
        snapshot = self.type_source()
        self.assertTrue(self.publish_control(snapshot)["published"])
        self.page.keyboard.press("Control+F8")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        self.assertEqual(self.page.evaluate("controlEvents"), [])
        self.assertEqual(
            self.page.evaluate("fixtureMessages.filter(item => item.event === 'escape').length"),
            1,
        )
        self.command(
            "configure-shortcuts",
            {
                "generation": "ordinary-again",
                "enabled": False,
                "sessionEnabled": True,
                "accept": "Tab",
                "exit": "Esc",
            },
        )
        snapshot = self.capture()
        self.assertTrue(snapshot["sessionBindings"]["editorReady"])
        self.publish(snapshot, self.plan(snapshot))
        self.page.keyboard.press("Tab")
        self.assertEqual(self.page.locator("#source").input_value(), "你好")
        self.assertEqual(len(self.accepted()), 1)

    def test_control_revocation_focus_and_composition_cannot_be_republished(self):
        for change in ("revoke", "focus", "composition", "shortcut"):
            with self.subTest(change=change):
                self.page.reload()
                self.page.locator("#source").focus()
                self.configure_control(enabled=change == "shortcut")
                snapshot = self.type_source()
                self.assertTrue(self.publish_control(snapshot)["published"])
                if change == "shortcut":
                    self.command(
                        "publish-shortcut",
                        {
                            "target": snapshot["target"],
                            "authority": snapshot["authority"],
                            "generation": "control-source",
                        },
                    )
                self.page.keyboard.down("Control")
                if change == "revoke":
                    self.command("revoke")
                elif change == "focus":
                    self.page.evaluate("""() => {
                      document.querySelector('#other').focus();
                      document.querySelector('#source').focus();
                    }""")
                elif change == "composition":
                    # Synthetic composition only revokes; it never grants Clear.
                    self.page.locator("#source").dispatch_event("compositionstart")
                elif change == "shortcut":
                    result = self.command(
                        "trigger-shortcut",
                        {
                            "target": snapshot["target"],
                            "authority": snapshot["authority"],
                            "generation": "control-source",
                            "kind": "wake",
                            "triggeredAtMs": snapshot["observedAtMs"],
                        },
                    )
                    self.assertTrue(result["triggered"])
                self.publish_control(snapshot)
                self.page.keyboard.down("F7")
                self.page.keyboard.up("Control")
                self.page.keyboard.up("F7")
                self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                self.assertEqual(self.accepted(), [])
                self.assertEqual(
                    self.page.evaluate("controlEvents"), [["keydown", "F7"], ["keyup", "F7"]]
                )

    def test_control_renewal_does_not_extend_original_deadline(self):
        self.configure_control()
        snapshot = self.type_source()
        self.assertTrue(self.publish_control(snapshot)["published"])
        self.page.keyboard.down("Control")
        self.page.wait_for_timeout(
            160
        )  # Intentionally expire the original 90ms permit and exit lease.
        next_snapshot = self.capture()
        next_plan = self.plan(next_snapshot)
        next_plan["id"] = "renewed-after-expiry"
        self.assertTrue(self.publish_control(next_snapshot, next_plan)["published"])
        self.page.keyboard.down("F7")
        self.page.keyboard.up("Control")
        self.page.keyboard.up("F7")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        self.assertEqual(self.page.evaluate("controlEvents"), [["keydown", "F7"], ["keyup", "F7"]])

    def test_control_postmutation_focus_roundtrip_is_unknown_and_consumed(self):
        self.configure_control()
        self.page.keyboard.insert_text("Hello")
        self.page.locator(
            "#source"
        ).evaluate("""element => element.addEventListener('input', () => {
          document.querySelector('#other').focus(); element.focus();
        }, {once: true})""")
        snapshot = self.capture()
        self.assertTrue(self.publish_control(snapshot)["published"])
        self.page.keyboard.press("Control+F7")
        self.assertEqual(self.page.locator("#source").input_value(), "你好")
        accepted = self.accepted()
        self.assertEqual(len(accepted), 1)
        self.assertEqual(accepted[0]["data"]["receipt"]["status"], "OutcomeUnknown")
        self.assertEqual(self.page.evaluate("controlEvents"), [])
        self.assertFalse(self.capture()["writeSupported"])

    def test_legacy_invalid_and_disabled_configuration_remain_read_only(self):
        for config in (
            {"enabled": True, "generation": "legacy"},
            {
                "enabled": False,
                "sessionEnabled": True,
                "generation": "array",
                "accept": ["F1"],
                "exit": ["F24"],
            },
            {
                "enabled": False,
                "sessionEnabled": True,
                "generation": "same",
                "accept": "F7",
                "exit": "F7",
            },
            {
                "enabled": False,
                "sessionEnabled": True,
                "generation": "same-base",
                "accept": "F7",
                "exit": "Ctrl+F7",
            },
            {
                "enabled": False,
                "sessionEnabled": True,
                "generation": "invalid",
                "accept": "F25",
                "exit": "Esc",
            },
            {
                "enabled": True,
                "sessionEnabled": False,
                "generation": "disabled",
                "accept": "Tab",
                "exit": "Esc",
            },
        ):
            with self.subTest(config=config):
                self.command("configure-shortcuts", config)
                self.page.locator("#source").focus()
                self.page.keyboard.press("Control+A")
                snapshot = self.type_source()
                self.assertFalse(snapshot["writeSupported"])
                self.assertFalse(snapshot["sessionBindings"]["sourceReady"])
                self.assertFalse(snapshot["sessionBindings"]["editorReady"])
                self.publish(snapshot, self.plan(snapshot))
                self.page.keyboard.press("Tab")
                self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                self.assertEqual(self.accepted(), [])

    def test_missing_and_old_generation_cannot_publish_or_write(self):
        snapshot = self.type_source()
        self.publish(snapshot, self.plan(snapshot))
        self.command(
            "configure-shortcuts",
            {
                "generation": "next",
                "enabled": True,
                "sessionEnabled": True,
                "accept": "F12",
                "exit": "F24",
            },
        )
        self.page.keyboard.press("Tab")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.page.locator("#source").focus()
        snapshot = self.capture()
        for generation in (None, "browser-default"):
            with self.subTest(generation=generation):
                result = self.page.evaluate(
                    """data => fixtureRawCommand({id: 5001,
                  op: 'publish', data})""",
                    {
                        "generation": generation,
                        "target": snapshot["target"],
                        "authority": snapshot["authority"],
                        "permit": {"plan": self.plan(snapshot)},
                    },
                )
                self.assertFalse(result["published"])
                receipt = self.page.evaluate(
                    """data => fixtureRawCommand({id: 5003,
                  op: 'direct', data})""",
                    {
                        "generation": generation,
                        "target": snapshot["target"],
                        "authority": snapshot["authority"],
                        "approved": snapshot["authority"],
                        "plan": self.plan(snapshot),
                    },
                )
                self.assertEqual(
                    receipt, {"status": "RejectedBeforeMutation", "reason": "EpochChanged"}
                )
        self.page.keyboard.press("F12")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_already_applied_receipt_survives_configuration_in_input_callback(self):
        snapshot = self.type_source()
        self.publish(snapshot, self.plan(snapshot))
        self.page.evaluate("""document.querySelector('#source').addEventListener('input', () => {
          fixtureCommand({id: 5002, op: 'configure-shortcuts', data: {
            generation: 'configured-in-input', enabled: false, sessionEnabled: true,
            accept: 'F12', exit: 'F24'}});
        }, {once: true});""")
        self.page.keyboard.press("Tab")
        self.assertEqual(self.page.locator("#source").input_value(), "你好")
        self.assertEqual(len(self.accepted()), 1)
        self.assertEqual(self.accepted()[0]["data"]["receipt"]["status"], "Verified")
        self.assertEqual(
            self.command("describe")["sessionBindings"]["generation"], "configured-in-input"
        )

    def test_preheld_unbound_key_remains_unowned_after_configuration(self):
        self.type_source()
        self.page.keyboard.down("F7")
        self.command(
            "configure-shortcuts",
            {
                "generation": "held-f7",
                "enabled": False,
                "sessionEnabled": True,
                "accept": "F7",
                "exit": "F24",
            },
        )
        snapshot = self.capture()
        self.publish(snapshot, self.plan(snapshot))
        self.page.keyboard.down("F7")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        self.page.keyboard.up("F7")
        snapshot = self.capture()
        self.publish(snapshot, self.plan(snapshot))
        self.page.keyboard.press("F7")
        self.assertEqual(self.page.locator("#source").input_value(), "你好")
        self.assertEqual(len(self.accepted()), 1)

    def test_old_consumed_release_keeps_ownership_after_configuration_and_hide(self):
        self.command(
            "configure-shortcuts",
            {
                "generation": "f12",
                "enabled": False,
                "sessionEnabled": True,
                "accept": "F12",
                "exit": "F24",
            },
        )
        snapshot = self.type_source()
        self.page.evaluate("""window.fixtureF12Releases = 0;
          window.addEventListener('keyup', event => {
            if (event.code === 'F12') window.fixtureF12Releases++;
          });""")
        self.publish(snapshot, self.plan(snapshot))
        self.page.keyboard.down("F12")
        self.assertEqual(len(self.accepted()), 1)
        self.command(
            "configure-shortcuts",
            {
                "generation": "f1",
                "enabled": True,
                "sessionEnabled": True,
                "accept": "F1",
                "exit": "F12",
            },
        )
        self.command("overlay-hide")
        self.page.keyboard.down("Control")
        self.page.keyboard.up("F12")
        self.page.keyboard.up("Control")
        self.assertEqual(self.page.evaluate("fixtureF12Releases"), 0)
        self.assertEqual(len(self.accepted()), 1)

    def test_function_exit_echoes_the_displayed_publication_generation_and_revision(self):
        self.command(
            "configure-shortcuts",
            {
                "generation": "f12-exit",
                "enabled": False,
                "sessionEnabled": True,
                "accept": "F1",
                "exit": "F12",
            },
        )
        snapshot = self.type_source()
        self.command(
            "publish",
            {
                "target": snapshot["target"],
                "authority": snapshot["authority"],
                "lease": {
                    "escapeAllowed": True,
                    "sessionRevision": 72,
                    "commandGeneration": "f12-exit",
                    "source": {"observedAtMs": snapshot["observedAtMs"]},
                },
            },
        )
        self.page.keyboard.press("F12")
        escapes = self.page.evaluate(
            "fixtureMessages.filter(message => message.event === 'escape')"
        )
        self.assertEqual(
            [item["data"] for item in escapes],
            [
                {
                    "target": snapshot["target"],
                    "generation": "f12-exit",
                    "sessionRevision": 72,
                }
            ],
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")

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

    def test_capture_trace_is_numeric_and_tab_identifies_its_publication_round(self):
        self.page.keyboard.insert_text("Hello")
        template = self.plan({"target": "", "text": "", "selection": {}, "observedAtMs": 0})
        result = self.page.evaluate(
            """plan => {
          fixtureCommand({id: 970, op: 'configure-shortcuts', data: {...fixtureBindings, enabled: false, trace: true}});
          const description = fixtureCommand({id: 971, op: 'describe'});
          const snapshot = fixtureCommand({id: 972, op: 'capture', data: {
            target: description.target, authority: description.authority, traceRound: 53}});
          if (!snapshot) return {captured: false};
          plan.expected = {...plan.expected, target: {...plan.expected.target, value: snapshot.target},
            text: snapshot.text, selection: snapshot.selection, observedAtMs: snapshot.observedAtMs};
          fixtureCommand({id: 973, op: 'publish', data: {target: snapshot.target,
            authority: snapshot.authority, traceRound: 53, permit: {plan}}});
          const invalid = fixtureCommand({id: 974, op: 'capture', data: {
            target: snapshot.target, authority: snapshot.authority, traceRound: true}});
          return {captured: true, trace: snapshot.captureTrace, invalidTrace: invalid?.captureTrace ?? null};
        }""",
            template,
        )
        self.assertTrue(result["captured"], result)
        self.assertEqual(set(result["trace"]), {"round", "pageCaptureMs"})
        self.assertEqual(result["trace"]["round"], 53)
        self.assertGreaterEqual(result["trace"]["pageCaptureMs"], 0)
        self.assertLessEqual(result["trace"]["pageCaptureMs"], 10000)
        self.assertIsNone(result["invalidTrace"])
        self.page.keyboard.press("Tab")
        diagnostic = self.page.evaluate(
            "fixtureMessages.findLast(item => item.event === 'tab-diagnostic')?.data"
        )
        self.assertIsNotNone(diagnostic)
        self.assertEqual(diagnostic["publicationRound"], 53)

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

    def test_fresh_publication_does_not_inherit_cached_capture_authority_age(self):
        self.page.keyboard.insert_text("Hello")
        template = self.plan({"target": "", "text": "", "selection": {}, "observedAtMs": 0})
        result = self.page.evaluate(
            """async plan => {
              const description = fixtureCommand({id: 950, op: 'describe'});
              const first = description && fixtureCommand({id: 951, op: 'capture', data: {
                target: description.target, authority: description.authority, requireFocused: true}});
              if (!first) return {captured: false};
              // Exercise the cached authority's real age, then keep the capture
              // response and publication in the same bounded transport turn.
              const until = performance.now() + 40;
              while (performance.now() < until) {
                await new Promise(resolve => setTimeout(resolve, until - performance.now()));
              }
              const snapshot = fixtureCommand({id: 952, op: 'capture', data: {
                target: first.target, authority: first.authority, requireFocused: true}});
              if (!snapshot) return {captured: false};
              plan.expected = {...plan.expected, target: {...plan.expected.target, value: snapshot.target},
                text: snapshot.text, selection: snapshot.selection, observedAtMs: snapshot.observedAtMs};
              const command = {id: 953, op: 'publish', data: {
                target: snapshot.target, authority: snapshot.authority, permit: {plan}}};
              const published = fixtureCommand(command);
              const acknowledged = fixtureMessages.findLast(item => item.event === 'publication')?.data;
              return {captured: true, published, acknowledged,
                authorityAgeMs: snapshot.diagnostics.authorityAgeMs,
                renewed: snapshot.authority.token !== first.authority.token,
                deadlineReductionMs: command.data.permit.expiresAtMs - acknowledged?.readyUntilHostMs};
            }""",
            template,
        )
        self.assertTrue(result["captured"], result)
        self.assertTrue(result["renewed"], result)
        self.assertGreaterEqual(result["authorityAgeMs"], 39, result)
        self.assertTrue(result["published"]["published"], result)
        self.assertTrue(result["acknowledged"]["ready"], result)
        # A fresh echoed authority pays bounded clock uncertainty, not the
        # preceding 40ms refresh interval. This is not desktop latency evidence.
        self.assertLess(result["deadlineReductionMs"], 25, result)

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

    def render_paused(self, revision=7):
        # The real paused flow only describes metadata. It must establish its
        # own clock proof without obtaining a source text/selection capture.
        self.sequence += 2
        return self.page.evaluate(
            """({id, revision}) => {
              const source = fixtureCommand({id, op: 'describe'});
              if (!source) return {source, rendered: {visible: false}};
              const rendered = fixtureCommand({id: id + 1, op: 'overlay-render', data: {
                target: source.target, authority: source.authority, revision,
                view: {state: 'Paused', canAccept: false, pauseRevision: 3,
                  source: {target: {backend: 'linux-browser', value: source.target,
                    lifetime: 'fixture'}, bounds: source.geometry.bounds},
                  presentation: {kind: 'Paused', visible: true, status: '已暂停',
                    tabHint: '点击恢复', dot: 'Neutral', editorOwnsText: false,
                    showEscape: false}}}});
              return {source, rendered};
            }""",
            {"id": self.sequence - 1, "revision": revision},
        )

    def paused_button(self):
        cdp = self.context.new_cdp_session(self.page)
        self.addCleanup(cdp.detach)
        nodes = [
            node
            for node in cdp.send("Accessibility.getFullAXTree")["nodes"]
            if not node.get("ignored")
        ]
        names = [node.get("name", {}).get("value") for node in nodes]
        self.assertIn("已暂停", names)
        for name in ["TypeLay 译文", "更多", "复制译文", "Tab 接受译文"]:
            self.assertNotIn(name, names)
        buttons = [
            node
            for node in nodes
            if node.get("role", {}).get("value") == "button"
            and node.get("name", {}).get("value") == "恢复"
        ]
        self.assertEqual(len(buttons), 1)
        return cdp, buttons[0]["backendDOMNodeId"]

    def test_disabled_session_renders_and_renews_metadata_only_paused_capsule(self):
        self.command(
            "configure-shortcuts",
            {
                "generation": "paused-disabled",
                "sessionEnabled": False,
                "enabled": False,
                "accept": "F24",
                "exit": "F1",
            },
        )
        self.page.keyboard.insert_text("Hello")
        result = self.render_paused()
        self.assertTrue(result["rendered"]["visible"])
        self.assertFalse(result["source"]["sessionBindings"]["sourceReady"])
        self.assertNotIn("text", result["source"])
        description = self.command("describe")
        self.command(
            "overlay-renew",
            {"target": description["target"], "authority": description["authority"]},
        )
        self.paused_button()
        self.session_key("F24", "down")
        self.session_key("F24", "up")
        self.assert_f24_events([("keydown", False), ("keyup", False)])
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_paused_metadata_only_capsule_resumes_once_without_moving_focus(self):
        self.page.keyboard.insert_text("Hello")
        result = self.render_paused()
        self.assertTrue(result["rendered"]["visible"])
        self.assertFalse(result["rendered"]["editing"])
        self.assertNotIn("text", result["source"])
        self.assertNotIn("selection", result["source"])
        cdp, button = self.paused_button()
        border = cdp.send("DOM.getBoxModel", {"backendNodeId": button})["model"]["border"]
        x, y = (border[0] + border[4]) / 2, (border[1] + border[5]) / 2
        self.page.mouse.dblclick(x, y)
        resumed = self.page.evaluate("""fixtureMessages.filter(message =>
          message.event === 'ui' && message.data.operation === 'resume')""")
        self.assertEqual(len(resumed), 1)
        self.assertEqual(resumed[0]["data"]["target"], result["source"]["target"])
        self.assertEqual(resumed[0]["data"]["revision"], 7)
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        self.assertFalse(
            self.page.evaluate("""fixtureMessages.some(message =>
          ['shortcut', 'escape', 'accepted'].includes(message.event))""")
        )

    def test_paused_keeps_tab_in_application_and_rejects_late_editor_commands(self):
        self.page.keyboard.insert_text("Hello")
        result = self.render_paused()
        self.assertTrue(result["rendered"]["visible"])
        target = result["source"]["target"]
        self.command("overlay-edit", {"target": target, "allowed": True})
        self.command("overlay-resume", {"target": target})
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        self.paused_button()
        self.page.keyboard.press("Tab")
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "other")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        self.assertFalse(
            self.page.evaluate("""fixtureMessages.some(message =>
          message.event === 'ui' && ['resume', 'return', 'beginEdit'].includes(message.data.operation))""")
        )

    def test_paused_page_probe_cannot_outlive_display_deadline(self):
        # A hostile page can reply to the production bridge's public probe.
        # This tests rejection only, not a Tiptap model or write capability.
        self.page.evaluate("""() => {
          const root = document.createElement('div');
          root.id = 'rich-source'; root.className = 'ProseMirror';
          root.contentEditable = 'true';
          root.style.cssText = 'width:480px;min-height:80px;margin-top:220px';
          document.body.append(root);
          root.addEventListener('typelay-editor-request', event => {
            const request = JSON.parse(event.detail);
            if (request.op !== 'probe') return;
            if (window.fixtureBlockProbe) {
              window.fixtureBlockProbe = false;
              while (performance.now() <= window.fixturePausedDeadline + 2) {
                // Deliberately exhaust the actual displayed view's budget.
              }
              window.fixtureProbeBlocked = true;
            }
            root.dispatchEvent(new CustomEvent('typelay-editor-response', {
              bubbles: true, detail: JSON.stringify({id: request.id,
                result: {kind: 'tiptap', supported: true}})}));
          });
          root.focus();
        }""")
        self.page.keyboard.insert_text("Hello")
        self.assertTrue(self.render_paused()["rendered"]["visible"])
        cdp, button = self.paused_button()
        border = cdp.send("DOM.getBoxModel", {"backendNodeId": button})["model"]["border"]
        # Renew through the normal metadata-only message immediately before the
        # pointer. No timers can conceal the view while the probe is on-stack.
        renewed = self.page.evaluate("""() => {
          const source = fixtureCommand({id: 1200, op: 'describe'});
          const request = {id: 1201, op: 'overlay-renew', data: {
            target: source.target, authority: source.authority}};
          fixtureCommand(request);
          window.fixturePausedDeadline = request.data.expiresAtMs - 1000000;
          window.fixtureBlockProbe = true;
          return source.focused;
        }""")
        self.assertTrue(renewed)
        self.page.mouse.click((border[0] + border[4]) / 2, (border[1] + border[5]) / 2)
        self.assertTrue(self.page.evaluate("window.fixtureProbeBlocked"))
        self.assertFalse(
            self.page.evaluate("""fixtureMessages.some(message =>
          message.event === 'ui' && message.data.operation === 'resume')""")
        )
        self.assertEqual(self.page.locator("#rich-source").inner_text(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_older_paused_frame_preserves_current_preview_and_acceptance(self):
        self.page.keyboard.insert_text("Hello")
        template = self.plan({"target": "", "text": "", "selection": {}, "observedAtMs": 0})
        result = self.page.evaluate(
            """plan => {
              const source = fixtureCommand({id: 1210, op: 'describe'});
              const snapshot = fixtureCommand({id: 1211, op: 'capture', data: {
                target: source.target, authority: source.authority}});
              if (!snapshot) return {captured: false};
              plan.expected = {...plan.expected, target: {...plan.expected.target, value: snapshot.target},
                text: snapshot.text, selection: snapshot.selection, observedAtMs: snapshot.observedAtMs};
              const latest = fixtureCommand({id: 1212, op: 'overlay-render', data: {
                target: snapshot.target, authority: snapshot.authority, revision: 8,
                view: {state: 'ValidTranslation', text: 'Fresh candidate', editable: true,
                  presentation: {visible: true, editorOwnsText: false, tabHint: 'Tab 接受译文'}}}});
              const published = fixtureCommand({id: 1213, op: 'publish', data: {
                target: snapshot.target, authority: snapshot.authority, permit: {plan}}});
              // An already-expired token in an older frame must never reach
              // calibration, revoke the newer permit, or hide the newer view.
              const older = fixtureCommand({id: 1214, op: 'overlay-render', data: {
                target: snapshot.target, authority: {...snapshot.authority, token: 'expired-token'},
                revision: 7, view: {state: 'Paused', presentation: {visible: true}}}});
              const current = fixtureCommand({id: 1215, op: 'describe'});
              return {captured: true, latest, published, older, publication: current?.publication};
            }""",
            template,
        )
        self.assertTrue(result["captured"], result)
        self.assertTrue(result["latest"]["visible"], result)
        self.assertTrue(result["published"]["published"], result)
        self.assertTrue(result["older"]["visible"], result)
        self.assertTrue(result["publication"]["ready"], result)
        self.assertEqual(result["publication"]["planId"], "test-plan")
        cdp = self.context.new_cdp_session(self.page)
        self.addCleanup(cdp.detach)
        candidates = [
            node.get("value", {}).get("value")
            for node in cdp.send("Accessibility.getFullAXTree")["nodes"]
            if not node.get("ignored") and node.get("name", {}).get("value") == "TypeLay 译文"
        ]
        self.assertEqual(candidates, ["Fresh candidate"])

    def test_paused_unknown_and_composing_ime_remain_invisible(self):
        self.assertFalse(self.render_paused()["rendered"]["visible"])
        self.page.keyboard.insert_text("Hello")
        self.page.locator("#source").dispatch_event("compositionstart", {"data": "x"})
        self.assertFalse(self.render_paused()["rendered"]["visible"])
        self.assertEqual(self.accepted(), [])

    def test_paused_stale_metadata_and_untrusted_click_cannot_resume(self):
        self.page.keyboard.insert_text("Hello")
        self.assertTrue(self.render_paused()["rendered"]["visible"])
        cdp, button = self.paused_button()
        remote = cdp.send("DOM.resolveNode", {"backendNodeId": button})["object"]["objectId"]
        cdp.send(
            "Runtime.callFunctionOn",
            {
                "objectId": remote,
                "functionDeclaration": "function() { window.fixturePausedButton = this; this.click(); }",
            },
        )
        self.assertFalse(
            self.page.evaluate("""fixtureMessages.some(message =>
          message.event === 'ui' && message.data.operation === 'resume')""")
        )
        # Changing metadata after display must withdraw the existing affordance,
        # even without a host render or an input event.
        self.page.locator("#source").evaluate("element => element.readOnly = true")
        self.page.wait_for_function("!window.fixturePausedButton.isConnected")
        self.assertFalse(self.render_paused()["rendered"]["visible"])
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_overlay_keeps_translation_primary_and_actions_visible(self):
        output = ROOT / "artifacts/linux/browser-native/overlay"
        output.mkdir(parents=True, exist_ok=True)
        self.page.keyboard.insert_text("Hello")
        for theme in ("light", "dark"):
            for width in (320, 520):
                with self.subTest(theme=theme, width=width):
                    self.command("overlay-hide")
                    self.page.locator("#source").evaluate(
                        """(element, width) => {
                          element.style.cssText = `margin-top:300px;width:${width}px;box-sizing:border-box`;
                          element.focus();
                        }""",
                        width,
                    )
                    self.page.mouse.move(1, 1)
                    snapshot = self.capture()
                    self.assertIsNotNone(snapshot, self.capture_diagnostics)
                    view = {
                        "presentation": {
                            "visible": True,
                            "editorOwnsText": False,
                            "tabHint": "Tab 接受译文",
                        },
                        "text": "Could we pick this up tomorrow afternoon?",
                        "editable": True,
                        "state": "ValidTranslation",
                        "sourceLanguage": "auto",
                        "targetLanguage": "en",
                        "status": "译文就绪",
                    }
                    data = {
                        "target": snapshot["target"],
                        "authority": snapshot["authority"],
                        "view": view,
                        "revision": 1,
                        "theme": theme,
                    }
                    self.assertTrue(self.command("overlay-render", data)["visible"])
                    nodes = [
                        node
                        for node in self.cdp.send("Accessibility.getFullAXTree")["nodes"]
                        if not node.get("ignored")
                    ]
                    names = [node.get("name", {}).get("value") for node in nodes]
                    for name in ("Auto → EN", "译文就绪", "Tab 交还应用"):
                        self.assertIn(name, names)
                    self.assertNotIn("点击译文以编辑", names)

                    def control_box(name, role):
                        node = next(
                            node
                            for node in nodes
                            if node.get("name", {}).get("value") == name
                            and node.get("role", {}).get("value") == role
                        )
                        border = self.cdp.send(
                            "DOM.getBoxModel", {"backendNodeId": node["backendDOMNodeId"]}
                        )["model"]["border"]
                        return node, border

                    editor, editor_box = control_box("TypeLay 译文", "textbox")
                    self.assertEqual(editor.get("description", {}).get("value"), "点击译文以编辑")
                    _, more_box = control_box("更多", "button")
                    _, exit_box = control_box("Esc 退出当前翻译", "button")
                    self.assertLessEqual(more_box[5], editor_box[1])
                    self.assertLessEqual(editor_box[5], exit_box[1])
                    host = self.page.locator("html > div")
                    self.assertTrue(host.evaluate("element => element.shadowRoot === null"))
                    bounds = host.bounding_box()
                    self.assertEqual(bounds["width"], width)
                    for box in (editor_box, more_box, exit_box):
                        self.assertGreater(box[4], box[0])
                        self.assertGreater(box[5], box[1])
                        self.assertGreaterEqual(box[0], bounds["x"])
                        self.assertLessEqual(box[4], bounds["x"] + bounds["width"])
                    # Capture the actual opaque surfaces before any hover. AX and
                    # pointer geometry work through the real closed shadow root.
                    self.page.screenshot(path=output / f"{theme}-{width}-preview.png", clip=bounds)
                    if theme == "light" and width == 320:
                        editor_object = self.overlay_control("TypeLay 译文", "textbox")
                        original_text = view["text"]
                        # Exercise the existing renderer directly; no model delay
                        # or transient host-state polling is needed for colors.
                        for revision, (state, text, owned, color) in enumerate(
                            (
                                ("Translating", "", False, "rgb(37, 42, 49)"),
                                ("Translating", original_text, False, "rgb(102, 107, 117)"),
                                ("ValidTranslation", "Fresh translation", False, "rgb(37, 42, 49)"),
                                (
                                    "TranslationFailed",
                                    "Fresh translation",
                                    False,
                                    "rgb(37, 42, 49)",
                                ),
                                ("EditingTranslation", "Human draft", True, "rgb(37, 42, 49)"),
                                ("ReturningToSource", "Human draft", True, "rgb(37, 42, 49)"),
                                (
                                    "EditedTranslationPending",
                                    "Human draft",
                                    False,
                                    "rgb(37, 42, 49)",
                                ),
                                ("Translating", "Human draft", False, "rgb(102, 107, 117)"),
                                ("Translating", "Human draft", True, "rgb(37, 42, 49)"),
                                ("ValidTranslation", original_text, False, "rgb(37, 42, 49)"),
                            ),
                            start=2,
                        ):
                            view.update(state=state, text=text)
                            view["status"] = (
                                "更新中" if state == "Translating" and text else "译文就绪"
                            )
                            view["presentation"]["editorOwnsText"] = owned
                            data["revision"] = revision
                            self.assertTrue(self.command("overlay-render", data)["visible"])
                            self.assertEqual(
                                self.overlay_dom(
                                    editor_object,
                                    "function() { return [this.value, getComputedStyle(this).color]; }",
                                ),
                                [text, color],
                                (state, owned),
                            )
                            self.assertEqual(
                                self.page.evaluate("document.activeElement.id"), "source"
                            )
                            if revision == 3:
                                self.page.screenshot(
                                    path=output / "updating-preview.png", clip=bounds
                                )
                    edit_requests = self.page.evaluate("""fixtureMessages.filter(message =>
                      message.event === 'ui' && message.data.operation === 'beginEdit').length""")
                    self.page.mouse.click(
                        (editor_box[0] + editor_box[4]) / 2,
                        (editor_box[1] + editor_box[5]) / 2,
                    )
                    self.assertEqual(
                        self.page.evaluate("""fixtureMessages.filter(message =>
                      message.event === 'ui' && message.data.operation === 'beginEdit').length"""),
                        edit_requests + 1,
                    )
                    self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
                    view["presentation"]["editorOwnsText"] = True
                    view["state"] = "EditingTranslation"
                    data["revision"] += 1
                    self.assertTrue(self.command("overlay-render", data)["visible"])
                    self.command(
                        "overlay-edit",
                        {
                            "target": snapshot["target"],
                            "revision": data["revision"],
                            "allowed": True,
                        },
                    )
                    self.assertTrue(
                        self.overlay_dom(
                            self.overlay_control("TypeLay 译文", "textbox"),
                            "function() { return !this.readOnly && this.matches(':focus'); }",
                        )
                    )
                    self.page.keyboard.press("Control+A")
                    self.page.keyboard.insert_text("Human draft")
                    self.assertEqual(
                        self.page.evaluate("""fixtureMessages.findLast(message =>
                      message.event === 'ui' && message.data.operation === 'draft').data.value"""),
                        "Human draft",
                    )
                    self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                    self.assertEqual(self.accepted(), [])
                    self.page.screenshot(
                        path=output / f"{theme}-{width}-editing.png", clip=host.bounding_box()
                    )

    def test_more_actions_are_disclosed_without_moving_source_focus(self):
        self.page.set_viewport_size({"width": 640, "height": 500})
        self.page.locator("#source").evaluate(
            "element => {element.style.marginTop = '220px'; element.style.width = '480px';}"
        )
        snapshot = self.type_source()
        self.assertTrue(
            self.command(
                "overlay-render",
                {
                    "target": snapshot["target"],
                    "authority": snapshot["authority"],
                    "view": {
                        "presentation": {
                            "visible": True,
                            "editorOwnsText": False,
                            "tabHint": "Tab 接受译文",
                        },
                        "text": "Initial candidate. " * 80,
                        "editable": True,
                        "state": "ValidTranslation",
                        "sourceLanguage": "en",
                        "targetLanguage": "zh",
                        "status": "译文就绪",
                    },
                    "revision": 1,
                    "theme": "light",
                },
            )["visible"]
        )
        cdp = self.context.new_cdp_session(self.page)
        self.addCleanup(cdp.detach)

        def accessible_nodes():
            return [
                node
                for node in cdp.send("Accessibility.getFullAXTree")["nodes"]
                if not node.get("ignored")
            ]

        def menu_names(expanded):
            nodes = accessible_nodes()
            more = next(
                node
                for node in nodes
                if node.get("role", {}).get("value") == "button"
                and node.get("name", {}).get("value") == "更多"
            )
            properties = {item["name"]: item["value"].get("value") for item in more["properties"]}
            self.assertEqual(properties["expanded"], expanded)
            return [node.get("name", {}).get("value") for node in nodes]

        def click_button(name):
            # Chromium exposes the real closed-shadow control through its AX
            # tree and DOM geometry. Dispatch a real pointer, not a DOM click.
            buttons = [
                node
                for node in accessible_nodes()
                if node.get("role", {}).get("value") == "button"
                and node.get("name", {}).get("value") == name
            ]
            self.assertEqual(len(buttons), 1)
            border = cdp.send("DOM.getBoxModel", {"backendNodeId": buttons[0]["backendDOMNodeId"]})[
                "model"
            ]["border"]
            self.page.mouse.click((border[0] + border[4]) / 2, (border[1] + border[5]) / 2)

        names = menu_names(False)
        self.assertNotIn("复制", names)
        self.assertNotIn("复制译文", names)
        compact = self.page.locator("html > div").bounding_box()
        click_button("更多")
        names = menu_names(True)
        for name in ["复制译文", "重新翻译", "不要在这个应用中显示", "设置"]:
            self.assertIn(name, names)
        self.assertIn("Tab 交还应用", names)
        self.assertNotIn("Tab 接受译文", names)
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        click_button("展开")
        self.page.wait_for_function(
            "document.querySelector('html > div').getBoundingClientRect().width === 520"
        )
        expanded = self.page.locator("html > div").bounding_box()
        self.assertEqual(expanded["width"], 520)
        self.assertGreater(expanded["width"], compact["width"])
        self.assertGreater(expanded["height"], compact["height"])
        self.assertGreaterEqual(expanded["y"], 0)
        self.assertLessEqual(expanded["y"] + expanded["height"], self.page.evaluate("innerHeight"))
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        self.assertIn("缩小", menu_names(False))
        click_button("缩小")
        self.page.wait_for_function(
            """expected => {
          const rect = document.querySelector('html > div').getBoundingClientRect();
          return ['x', 'y', 'width', 'height'].every(name => rect[name] === expected[name]);
        }""",
            arg=compact,
        )
        self.assertEqual(self.page.locator("html > div").bounding_box(), compact)
        names = menu_names(False)
        self.assertNotIn("复制译文", names)
        self.assertNotIn("重新翻译", names)
        self.assertIn("展开", names)
        description = self.command("describe", {"target": snapshot["target"]})
        self.assertTrue(description["focused"])
        for revision in ["inputRevision", "focusRevision", "contentRevision"]:
            self.assertEqual(description["authority"][revision], snapshot["authority"][revision])
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        self.command("overlay-hide")
        names = [node.get("name", {}).get("value") for node in accessible_nodes()]
        self.assertNotIn("更多", names)

    def motion_view(self):
        self.page.emulate_media(reduced_motion="no-preference")
        self.page.locator("#source").evaluate("""element => {
          element.style.cssText = 'display:block;margin-top:500px;margin-left:160px;width:480px;box-sizing:border-box';
        }""")
        snapshot = self.type_source()
        data = {
            "target": snapshot["target"],
            "authority": snapshot["authority"],
            "view": {
                "presentation": {
                    "visible": True,
                    "editorOwnsText": False,
                    "tabHint": "Tab 接受译文",
                },
                "text": "Current candidate. " * 80,
                "editable": True,
                "state": "ValidTranslation",
                "sourceLanguage": "en",
                "targetLanguage": "zh",
                "status": "译文就绪",
            },
            "revision": 1,
            "theme": "light",
        }
        self.assertTrue(self.command("overlay-render", data)["visible"])
        return data

    def overlay_control(self, name, role="button"):
        nodes = self.cdp.send("Accessibility.getFullAXTree")["nodes"]
        node = next(
            node
            for node in nodes
            if not node.get("ignored")
            and node.get("role", {}).get("value") == role
            and node.get("name", {}).get("value") == name
        )
        return self.cdp.send("DOM.resolveNode", {"backendNodeId": node["backendDOMNodeId"]})[
            "object"
        ]["objectId"]

    def overlay_dom(self, object_id, function):
        return self.cdp.send(
            "Runtime.callFunctionOn",
            {
                "objectId": object_id,
                "functionDeclaration": function,
                "returnByValue": True,
            },
        )["result"].get("value")

    def click_overlay_control(self, name):
        rect = self.overlay_dom(
            self.overlay_control(name),
            "function() { return this.getBoundingClientRect().toJSON(); }",
        )
        self.page.mouse.click(rect["x"] + rect["width"] / 2, rect["y"] + rect["height"] / 2)

    def observe_overlay_frames(self):
        # Public browser DOM/RAF observation of the real closed-shadow widgets.
        # It neither reads adapter variables nor supplies an animation model.
        self.overlay_dom(
            self.overlay_control("TypeLay 译文", "textbox"),
            """function() {
          const editor = this, panel = editor.parentElement, host = panel.getRootNode().host;
          const exit = panel.querySelector('footer button');
          window.fixtureMotionFrames = [];
          const start = performance.now();
          function sample(now) {
            fixtureMotionFrames.push({ms: now - start, connected: host.isConnected,
              host: host.getBoundingClientRect().toJSON(), editor: editor.getBoundingClientRect().toJSON(),
              exit: exit.getBoundingClientRect().toJSON(), text: editor.value,
              start: editor.selectionStart, end: editor.selectionEnd,
              sourceFocused: document.activeElement.id === 'source'});
            if (now - start < 360) requestAnimationFrame(sample);
            else window.fixtureMotionDone = true;
          }
          window.fixtureMotionDone = false;
          requestAnimationFrame(sample);
        }""",
        )

    def motion_frames(self):
        self.page.wait_for_function("window.fixtureMotionDone === true")
        return self.page.evaluate("fixtureMotionFrames")

    def test_overlay_resize_has_intermediate_frames_and_preserves_anchors_and_focus(self):
        self.motion_view()
        output = ROOT / "artifacts/linux/browser-native/overlay/motion"
        output.mkdir(parents=True, exist_ok=True)
        screenshots = []

        def save_frame(event):
            if len(screenshots) < 12:
                path = output / f"resize-{len(screenshots):02d}.png"
                path.write_bytes(base64.b64decode(event["data"]))
                screenshots.append(path.name)
            self.cdp.send("Page.screencastFrameAck", {"sessionId": event["sessionId"]})

        self.cdp.on("Page.screencastFrame", save_frame)
        self.cdp.send("Page.startScreencast", {"format": "png", "everyNthFrame": 2})
        try:
            before = self.page.locator("html > div").bounding_box()
            self.observe_overlay_frames()
            self.click_overlay_control("展开")
            frames = self.motion_frames()
        finally:
            self.cdp.send("Page.stopScreencast")
            self.cdp.remove_listener("Page.screencastFrame", save_frame)
        (output / "resize.json").write_text(
            json.dumps({"frames": frames, "screenshots": screenshots}, indent=2)
        )
        end = frames[-1]["host"]
        self.assertEqual(end["width"], 520)
        self.assertGreater(end["height"], before["height"])
        self.assertGreater(len({round(frame["host"]["height"], 1) for frame in frames}), 3)
        self.assertGreaterEqual(len(screenshots), 3)
        for frame in frames:
            self.assertTrue(frame["sourceFocused"])
            self.assertAlmostEqual(frame["host"]["right"], end["right"], delta=1)
            self.assertAlmostEqual(frame["host"]["bottom"], end["bottom"], delta=1)
            self.assertGreaterEqual(frame["exit"]["top"], frame["host"]["top"])
            self.assertLessEqual(frame["exit"]["bottom"], frame["host"]["bottom"])
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_new_view_interrupts_resize_and_replaces_body_and_hints_immediately(self):
        data = self.motion_view()
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.click_overlay_control("展开")
        data["revision"] = 2
        data["view"]["text"] = "New current candidate"
        data["view"]["status"] = "新译文"
        self.assertTrue(self.command("overlay-render", data)["visible"])
        self.assertEqual(
            self.overlay_dom(editor, "function() { return this.value; }"), "New current candidate"
        )
        self.observe_overlay_frames()
        frames = self.motion_frames()
        self.assertTrue(all(frame["text"] == "New current candidate" for frame in frames))
        self.assertEqual(len({frame["host"]["height"] for frame in frames}), 1)
        self.assertTrue(self.overlay_control("新译文", "StaticText"))
        self.assertTrue(self.overlay_control("Tab 交还应用", "StaticText"))

    def test_rapid_resize_and_same_content_renew_do_not_restart_motion(self):
        data = self.motion_view()
        before = self.page.locator("html > div").bounding_box()
        self.click_overlay_control("展开")
        self.command("overlay-renew", {"target": data["target"], "authority": data["authority"]})
        self.click_overlay_control("缩小")
        self.observe_overlay_frames()
        # Same-content renewals are display proofs only; their arrival cannot
        # replay expansion or extend the numeric resize's 280ms duration.
        self.command("overlay-renew", {"target": data["target"], "authority": data["authority"]})
        frames = self.motion_frames()
        end = frames[-1]["host"]
        for name in ("x", "y", "width", "height"):
            self.assertAlmostEqual(end[name], before[name], delta=1)
        settled = [frame for frame in frames if frame["ms"] >= 300]
        self.assertTrue(all(abs(frame["host"]["height"] - end["height"]) < 1 for frame in settled))
        self.assertTrue(all(frame["sourceFocused"] for frame in frames))
        self.assertEqual(self.accepted(), [])

    def test_hide_during_resize_clears_live_editor_and_never_revives(self):
        data = self.motion_view()
        editor = self.overlay_control("TypeLay 译文", "textbox")
        data["revision"] = 2
        data["view"]["presentation"]["editorOwnsText"] = True
        data["view"]["state"] = "EditingTranslation"
        self.command("overlay-render", data)
        self.command("overlay-edit", {"target": data["target"], "allowed": True})
        self.click_overlay_control("展开")
        self.page.keyboard.press("Escape")
        self.assertTrue(
            self.page.evaluate("""fixtureMessages.some(message =>
          message.event === 'ui' && message.data.operation === 'escape')""")
        )
        # The fixture host acknowledges Escape using the normal hide command.
        self.command("overlay-hide")
        self.assertEqual(self.overlay_dom(editor, "function() { return this.value; }"), "")
        self.page.wait_for_timeout(360)  # Observation window beyond the original motion deadline.
        self.assertEqual(self.page.locator("html > div").count(), 0)
        self.page.locator("#source").focus()
        data = self.motion_view()
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.click_overlay_control("展开")
        self.page.locator("#source").evaluate("element => element.remove()")
        self.page.wait_for_timeout(360)
        self.assertEqual(self.page.locator("html > div").count(), 0)
        self.assertEqual(self.overlay_dom(editor, "function() { return this.value; }"), "")
        self.assertEqual(self.accepted(), [])

    def test_reduced_motion_lands_directly_and_cancels_running_resize(self):
        data = self.motion_view()
        self.page.emulate_media(reduced_motion="reduce")
        self.click_overlay_control("展开")
        self.observe_overlay_frames()
        frames = self.motion_frames()
        self.assertEqual({frame["host"]["width"] for frame in frames}, {520})
        self.assertEqual(len({frame["host"]["height"] for frame in frames}), 1)
        self.command("overlay-renew", {"target": data["target"], "authority": data["authority"]})
        self.page.emulate_media(reduced_motion="no-preference")
        self.click_overlay_control("缩小")
        self.page.emulate_media(reduced_motion="reduce")
        self.observe_overlay_frames()
        frames = self.motion_frames()
        self.assertEqual({frame["host"]["width"] for frame in frames}, {480})
        self.assertEqual(len({frame["host"]["height"] for frame in frames}), 1)

    def test_hint_preserves_unchanged_text_and_updates_on_revocation(self):
        snapshot = self.type_source()
        self.assertTrue(
            self.command(
                "overlay-render",
                {
                    "target": snapshot["target"],
                    "authority": snapshot["authority"],
                    "view": {
                        "presentation": {
                            "visible": True,
                            "editorOwnsText": False,
                            "tabHint": "Tab 接受译文",
                        },
                        "text": "Candidate",
                        "state": "ValidTranslation",
                        "sourceLanguage": "en",
                        "targetLanguage": "zh",
                    },
                    "revision": 1,
                },
            )["visible"]
        )
        cdp = self.context.new_cdp_session(self.page)
        self.addCleanup(cdp.detach)
        hint = next(
            node
            for node in cdp.send("Accessibility.getFullAXTree")["nodes"]
            if not node.get("ignored")
            and node.get("role", {}).get("value") == "StaticText"
            and node.get("name", {}).get("value") == "Tab 交还应用"
        )
        element = cdp.send("DOM.resolveNode", {"backendNodeId": hint["backendDOMNodeId"]})
        # Observe the actual closed-shadow DOM through Chromium's public tools,
        # without exposing any adapter-private object or changing its transport.
        cdp.send(
            "Runtime.callFunctionOn",
            {
                "objectId": element["object"]["objectId"],
                "functionDeclaration": """function() {
                  const element = this.nodeType === Node.TEXT_NODE ? this.parentElement : this;
                  let changes = 0;
                  const observer = new MutationObserver(records => { changes += records.length; });
                  observer.observe(element, {childList: true});
                  window.fixtureReadHint = () => {
                    changes += observer.takeRecords().length;
                    const result = {text: element.textContent, changes};
                    changes = 0;
                    return result;
                  };
                }""",
            },
        )
        unchanged = self.page.evaluate("""() => {
          for (let id = 960; id < 963; id++) fixtureCommand({id, op: 'describe'});
          return fixtureReadHint();
        }""")
        self.assertEqual(unchanged, {"text": "Tab 交还应用", "changes": 0})
        template = self.plan({"target": "", "text": "", "selection": {}, "observedAtMs": 0})
        initial = self.page.evaluate(
            """plan => {
              window.fixturePublishHint = () => {
                const description = fixtureCommand({id: 963, op: 'describe'});
                const snapshot = fixtureCommand({id: 964, op: 'capture', data: {
                  target: description.target, authority: description.authority, requireFocused: true}});
                if (!snapshot) return false;
                plan.expected = {...plan.expected, target: {...plan.expected.target, value: snapshot.target},
                  text: snapshot.text, selection: snapshot.selection, observedAtMs: snapshot.observedAtMs};
                const command = {id: 965, op: 'publish', data: {
                  target: snapshot.target, authority: snapshot.authority, permit: {plan}}};
                const result = fixtureCommand(command);
                window.fixtureHintDeadline = command.data.permit.expiresAtMs - 1000000;
                return result.published;
              };
              const result = {published: fixturePublishHint(), hint: fixtureReadHint()};
              for (let id = 967; id < 970; id++) fixtureCommand({id, op: 'describe'});
              return {...result, unchanged: fixtureReadHint()};
            }""",
            template,
        )
        self.assertTrue(initial["published"], initial)
        self.assertEqual(initial["hint"], {"text": "Tab 接受译文", "changes": 1})
        self.assertEqual(initial["unchanged"], {"text": "Tab 接受译文", "changes": 0})
        self.page.wait_for_function("performance.now() > fixtureHintDeadline + 2", timeout=1000)
        self.assertEqual(
            self.page.evaluate("fixtureReadHint()"), {"text": "Tab 交还应用", "changes": 1}
        )
        for action in ("revoke", "focus"):
            with self.subTest(action=action):
                result = self.page.evaluate(
                    """action => {
                      const published = fixturePublishHint(), before = fixtureReadHint();
                      if (action === 'revoke') fixtureCommand({id: 966, op: 'revoke'});
                      else document.querySelector('#other').focus();
                      return {published, before, after: fixtureReadHint()};
                    }""",
                    action,
                )
                self.assertTrue(result["published"], result)
                self.assertEqual(result["before"], {"text": "Tab 接受译文", "changes": 1})
                self.assertEqual(result["after"], {"text": "Tab 交还应用", "changes": 1})
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_editor_draft_survives_render_and_two_separate_tabs_return_then_accept(self):
        self.check_editor_draft_return_then_accept("Tab", "Esc")

    def test_f24_editor_return_requires_release_and_fresh_second_keydown(self):
        self.check_editor_draft_return_then_accept("F24", "F1")

    def test_control_editor_returns_without_write_then_requires_a_new_complete_pair(self):
        self.check_editor_draft_return_then_accept("Ctrl+F7", "Ctrl+F8")

    def open_editor(self, accept, exit_key, *, dispatch=None):
        self.command(
            "configure-shortcuts",
            {
                "generation": "editor-" + accept,
                **({"shortcutDispatch": dispatch} if dispatch else {}),
                "enabled": False,
                "sessionEnabled": True,
                "accept": accept,
                "exit": exit_key,
            },
        )
        self.page.locator("#source").evaluate(
            """element => {element.style.cssText =
              'display:block;margin-top:500px;margin-left:160px;width:480px;box-sizing:border-box';} """
        )
        snapshot = self.type_source()
        view = {
            "presentation": {
                "visible": True,
                "editorOwnsText": False,
                "tabHint": f"{accept} 接受译文",
            },
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
        self.assertIn(f"{accept} 交还应用", names)
        self.assertNotIn(f"{accept} 接受译文", names)
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
        return snapshot, data

    def check_editor_draft_return_then_accept(self, accept, exit_key):
        snapshot, data = self.open_editor(accept, exit_key)
        revision = snapshot["authority"]["inputRevision"]
        view = data["view"]
        control = accept.startswith("Ctrl+")
        main_key = accept.removeprefix("Ctrl+")
        self.page.keyboard.press("Control+A")
        human_draft = (
            "中文人工草稿，保留选区与原生撤销。" * 20 if accept == "Tab" else "Human draft"
        )
        self.page.keyboard.insert_text(human_draft)
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.assertEqual(
            self.overlay_dom(editor, "function() { return getComputedStyle(this).color; }"),
            "rgb(37, 42, 49)",
        )
        if accept == "Tab":
            # Reuse the existing two-stage return case for motion, Chinese
            # selection and Chromium's actual textarea undo/redo stack.
            self.page.keyboard.press("Shift+ArrowLeft")
            selection = self.overlay_dom(
                editor, "function() { return [this.selectionStart, this.selectionEnd]; }"
            )
            self.observe_overlay_frames()
            self.click_overlay_control("展开")
            frames = self.motion_frames()
            self.assertGreater(len({round(frame["host"]["height"], 1) for frame in frames}), 3)
            self.assertTrue(all(frame["text"] == human_draft for frame in frames))
            self.assertTrue(all([frame["start"], frame["end"]] == selection for frame in frames))
            self.page.keyboard.press("Control+Z")
            self.assertEqual(
                self.overlay_dom(editor, "function() { return this.value; }"), "Initial candidate"
            )
            self.page.keyboard.press("Control+Y")
            self.assertEqual(
                self.overlay_dom(editor, "function() { return this.value; }"), human_draft
            )
        view["text"] = "Stale remote candidate"
        data["revision"] = 3
        self.command("overlay-render", data)
        stale = {**data, "generation": "obsolete-editor"}
        self.assertFalse(self.command("overlay-render", stale)["visible"])
        self.assertFalse(
            self.command(
                "return",
                {
                    "target": snapshot["target"],
                    "authority": snapshot["authority"],
                    "generation": "obsolete-editor",
                },
            )["returned"]
        )
        self.page.keyboard.press("End")
        self.page.keyboard.insert_text("!")
        draft = self.page.evaluate("""window.fixtureMessages.findLast(message =>
          message.event === 'ui' && message.data.operation === 'draft').data.value""")
        self.assertEqual(draft, human_draft + "!")
        if control:
            self.page.keyboard.down("Control")
            self.command("revoke", {"shortcuts": False})
        self.session_key(main_key, "down")
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
        self.assertEqual(
            self.overlay_dom(
                editor, "function() { return [this.value, getComputedStyle(this).color]; }"
            ),
            [draft, "rgb(37, 42, 49)"],
        )
        returned = self.capture()
        self.assertEqual(returned["selection"], snapshot["selection"])
        self.assertEqual(returned["authority"]["inputRevision"], revision)
        self.publish(returned, self.plan(returned, draft))
        self.session_key(main_key, "down", repeat=True)
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        if control:
            # Re-pressing Ctrl while the consumed return Fn is still down does
            # not start a source ticket. Even a subsequent fresh Fn must pass.
            self.page.keyboard.up("Control")
            self.page.keyboard.down("Control")
            self.session_key(main_key, "up")
            self.session_key(main_key, "down")
            self.assertEqual(self.page.locator("#source").input_value(), "Hello")
            self.assertEqual(self.accepted(), [])
            self.page.keyboard.up("Control")
        self.session_key(main_key, "up")
        returned = self.capture()
        self.publish(returned, self.plan(returned, draft))
        if control:
            self.page.keyboard.down("Control")
        self.session_key(main_key, "down")
        if control:
            self.page.keyboard.up("Control")
        self.session_key(main_key, "up")
        self.assertEqual(self.page.locator("#source").input_value(), human_draft + "!")
        self.assertEqual(len(self.accepted()), 1)
        if accept == "F24":
            self.assert_f24_events(
                [
                    ("keydown", False),
                    ("keydown", True),
                    ("keyup", False),
                    ("keydown", False),
                    ("keyup", False),
                ]
            )

    def test_native_editor_metadata_renewal_preserves_draft_without_renewing_key_ticket(self):
        snapshot, data = self.open_editor("Ctrl+F7", "Ctrl+F8", dispatch="native")
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.page.keyboard.press("Control+A")
        self.page.keyboard.insert_text("Working draft")
        self.page.keyboard.down("Control")
        renew = """request => {
          const description = fixtureCommand({id: 7601, op: 'describe', data: {target: request.target}});
          fixtureCommand({id: 7602, op: 'overlay-renew', data: {...request,
            authority: description.authority, metadataOnly: true}});
        }"""
        request = {
            "target": snapshot["target"],
            "generation": "editor-Ctrl+F7",
            "revision": data["revision"],
        }
        # Pass both the original 800ms key ticket and the 1500ms capture clock.
        # Only describe/renew run while editing; no new source capture or permit.
        for _ in range(5):
            self.page.wait_for_timeout(400)
            self.page.evaluate(renew, request)
        self.assertEqual(self.page.locator("html > div").count(), 1)
        self.assertEqual(
            self.overlay_dom(editor, "function() { return this.value; }"), "Working draft"
        )
        self.page.keyboard.down("F7")
        self.page.keyboard.up("Control")
        self.page.keyboard.up("F7")
        self.assertFalse(
            self.page.evaluate("""fixtureMessages.some(message =>
          message.event === 'ui' && ['return', 'escape'].includes(message.data.operation))""")
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        self.page.evaluate(renew, {**request, "revision": request["revision"] + 1})
        self.assertEqual(self.page.locator("html > div").count(), 0)

    def test_control_editor_exit_owns_repeat_and_release_without_a_write(self):
        self.open_editor("Ctrl+F7", "Ctrl+F8")
        self.page.keyboard.down("Control")
        self.page.keyboard.down("F8")
        self.page.keyboard.down("F8")
        self.page.keyboard.up("Control")
        self.page.keyboard.up("F8")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])
        self.assertEqual(
            self.page.evaluate("""fixtureMessages.filter(message =>
              message.event === 'ui' && message.data.operation === 'escape').length"""),
            1,
        )

    def test_control_editor_changes_and_renewal_cannot_restore_the_original_ticket(self):
        for change in ("revoke", "input", "composition", "view", "deadline"):
            with self.subTest(change=change):
                self.page.reload()
                self.page.locator("#source").focus()
                snapshot, data = self.open_editor("Ctrl+F7", "Ctrl+F8")
                editor = self.overlay_control("TypeLay 译文", "textbox")
                self.page.evaluate("""() => {
                  window.editorKeys = [];
                  for (const type of ['keydown', 'keyup']) window.addEventListener(type, event => {
                    if (event.key === 'F7') editorKeys.push(type);
                  });
                }""")
                self.page.keyboard.down("Control")
                if change == "revoke":
                    self.command("revoke", {"shortcuts": True})
                elif change == "input":
                    # Same-value DOM notification still invalidates the old
                    # editor action; matching the final draft is insufficient.
                    self.overlay_dom(
                        editor, "function() { this.dispatchEvent(new InputEvent('input')); }"
                    )
                elif change == "composition":
                    self.overlay_dom(
                        editor,
                        """function() {
                          this.dispatchEvent(new CompositionEvent('compositionstart'));
                          this.dispatchEvent(new CompositionEvent('compositionend'));
                        }""",
                    )
                elif change == "view":
                    data["revision"] += 1
                else:
                    # Keep the visible lease alive while deliberately crossing
                    # the first Ctrl's fixed 800ms display deadline.
                    self.page.wait_for_timeout(450)
                self.assertTrue(self.command("overlay-render", data)["visible"])
                self.command(
                    "overlay-renew",
                    {"target": snapshot["target"], "authority": snapshot["authority"]},
                )
                if change == "deadline":
                    self.page.wait_for_timeout(450)
                self.page.keyboard.down("F7")
                self.page.keyboard.up("Control")
                self.page.keyboard.up("F7")
                self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                self.assertEqual(self.accepted(), [])
                self.assertFalse(
                    self.page.evaluate("""fixtureMessages.some(message =>
                      message.event === 'ui' && ['return', 'escape'].includes(message.data.operation))""")
                )
                self.assertEqual(self.page.evaluate("editorKeys"), ["keydown", "keyup"])

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
