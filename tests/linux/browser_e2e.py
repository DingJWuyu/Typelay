"""Real Chromium DOM contract for the production content-script input adapter.

The fixture replaces the extension message transport; the coarse-clock case
also supplies an explicitly controlled clock bucket. Chromium owns DOM,
selection, keyboard dispatch, composition and synchronous mutation. No model is
loaded, no extension is installed and no user browser profile is opened. This
is not physical IME, native-host routing or complete desktop acceptance.
"""

import argparse
import base64
import json
import os
import shutil
import sys
import tempfile
import unittest
import uuid
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
  window.addEventListener('keydown', event => {
    if (event.key === 'Tab' && event.isTrusted && window.fixtureTabBusyMs) {
      const until = performance.now() + window.fixtureTabBusyMs;
      while (performance.now() < until) { /* Delay this real event before the adapter. */ }
    }
  }, true);
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
    if (message.op === 'overlay-render') {
      data.appearance ??= {uiLanguage: 'zh-CN'};
      revision = Math.max(revision, data.revision || 0);
    }
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


# Bounded diagnostics beside existing public command/DOM reads. No additional
# transport request, capture, clock update, event, or display renewal is sent.
READ_OBSERVER = r"""(() => {
  const rows = []; let omitted = 0, expiry = null;
  const state = node => ({ms: performance.now(), wall: Date.now(), expiry,
    documentFocused: document.hasFocus(), visibility: document.visibilityState,
    sourceFocused: document.activeElement?.id === 'source',
    hostConnected: !!document.querySelector('html > div[role=region][aria-label=TypeLay]')?.isConnected,
    nodeConnected: node?.isConnected ?? null, nodeHidden: node?.hidden ?? null,
    nodeFocused: node?.matches?.(':focus') ?? null});
  const note = (kind, detail = {}, node = null) => {
    if (rows.length < 128) rows.push({kind, ...state(node), ...detail}); else omitted++;
  };
  const raw = fixtureRawCommand;
  window.fixtureRawCommand = message => {
    note('command-before', {operation: message.op, revision: message.data?.revision ?? null});
    const result = raw(message);
    if (['overlay-render', 'overlay-renew'].includes(message.op))
      expiry = message.data.expiresAtMs - 1000000; // Existing fixture clock offset.
    const basis = result?.authority ?? message.data?.authority;
    note('command-after', {operation: message.op, revision: message.data?.revision ?? null,
      visibleReply: result?.visible ?? null, returned: !!result,
      inputRevision: basis?.inputRevision ?? null, focusRevision: basis?.focusRevision ?? null,
      authorityObservedAtMs: basis?.observedAtMs ?? null,
      clockReason: result?.diagnostics?.lastClockReason ?? null,
      clockAgeMs: result?.diagnostics?.clockAgeMs ?? null});
    return result;
  };
  window.fixtureReadAudit = {
    drain(node) {
      note('existing-read-return', {}, node);
      const value = {rows: rows.splice(0), omitted}; omitted = 0; return value;
    }
  };
})();"""


# Failure evidence for held-key and source-control lifetime contracts. Observe the existing fixture
# transport and trusted event delivery before content.js; never issue a command,
# refresh an authority clock, change a key, or extend the contract's deadline.
HELD_KEY_OBSERVER = r"""(() => {
  const rows=[]; let omitted=0;
  const log=value=>{
    if(rows.length<192) rows.push({sequence:rows.length,mono:performance.now(),wall:Date.now(),...value});
    else omitted++;
  };
  window.fixtureHeldAudit={rows,get omitted(){return omitted;}};
  log({kind:'observer-ready'});
  for(const type of ['keydown','keyup','focus','blur','focusin','focusout','visibilitychange']) window.addEventListener(type,event=>{
    log({kind:'event',type:event.type,key:event.key,code:event.code,trusted:event.isTrusted,repeat:event.repeat,
      eventStamp:event.timeStamp,shift:event.shiftKey,ctrl:event.ctrlKey,alt:event.altKey,meta:event.metaKey,
      focus:document.hasFocus(),visibility:document.visibilityState,active:document.activeElement?.id||'',
      target:event.target?.id||'',prevented:event.defaultPrevented});
  },true);
  window.addEventListener('error',event=>log({kind:'error',message:String(event.message).slice(0,400)}),true);
  window.addEventListener('unhandledrejection',event=>log({kind:'unhandledrejection',message:String(event.reason).slice(0,400)}),true);
  const raw=window.fixtureRawCommand;
  window.fixtureRawCommand=message=>{
    const first=fixtureMessages.length;
    log({kind:'command-before',op:message.op,id:message.id,releasedAtMs:message.data?.releasedAtMs,
      focus:document.hasFocus(),active:document.activeElement?.id||''});
    try {
      const result=raw(message);
      const replies=fixtureMessages.slice(first).filter(item=>item.event==='command-owner');
      const acks=replies.slice(0,16).map(item=>({
        state:item.data.state,rejectionReason:item.data.rejectionReason,grantSequence:item.data.grantSequence}));
      const publications=fixtureMessages.slice(first).filter(item=>item.event==='publication');
      log({kind:'command-after',op:message.op,id:message.id,clock:message.clock,
        authorityObservedAtMs:result?.authority?.observedAtMs,diagnostics:result?.diagnostics,
        permitExpiryAtPerformanceMs:message.data?.permit?.expiresAtMs === undefined ? null
          : message.data.permit.expiresAtMs-1000000,
        publication:publications.length ? {ready:publications.at(-1).data.ready,
          readyUntilHostMs:publications.at(-1).data.readyUntilHostMs} : null,
        acks,omittedAcks:Math.max(0,replies.length-acks.length)});
      return result;
    } catch(error) {log({kind:'command-error',op:message.op,message:String(error).slice(0,400)});throw error;}
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
        self.read_observations = (
            []
            if self._testMethodName
            in (
                "test_host_renewed_motion_modes_override_os_and_legacy_boolean",
                "test_more_menu_hide_hint_and_outside_pointer_dismissal",
                "test_packaged_cjk_faces_render_by_target_without_loading_all_regions",
            )
            else None
        )
        self.read_omitted = 0
        self.addCleanup(self.save_read_observations)
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
        if self._testMethodName == "test_coarse_clock_frozen_bucket_refuses_capture":
            # Capability fault before production caches its clock. Both clocks
            # stay in one explicitly controlled 100ms bucket during capture.
            # This is not an emulation of any browser's privacy-clock algorithm.
            clock_setup = """(() => {
              const wallOrigin = Date.now();
              let bucket = 0;
              window.fixtureAdvanceClockBucket = () => { bucket += 100; };
              Object.defineProperty(performance, 'now', {value: () => bucket});
              Date.now = () => wallOrigin + bucket;
            })();"""
        if self._testMethodName in (
            "test_accept_hint_hands_off_before_permission_and_recovers_on_new_publication",
            "test_hint_preserves_unchanged_text_and_updates_on_revocation",
        ):
            # Controlled scheduling, with a fine monotonic sampling step for the
            # adapter's timer-resolution guard. Not Firefox latency evidence.
            clock_setup = """(() => {
              const wall = Date.now(), timers = new Map();
              let elapsed = 0, tick = 0, identity = 0;
              Object.defineProperty(performance, 'now', {value: () => elapsed + ++tick / 10000});
              Date.now = () => wall + elapsed;
              window.setTimeout = (callback, delay = 0, ...args) => {
                const id = ++identity;
                timers.set(id, {at: elapsed + Math.max(1, delay), callback, args});
                return id;
              };
              window.clearTimeout = id => timers.delete(id);
              window.fixtureAdvanceHintClock = milliseconds => {
                elapsed += milliseconds;
                for (const [id, timer] of [...timers]) if (timer.at <= elapsed) {
                  timers.delete(id);
                  timer.callback(...timer.args);
                }
              };
            })();"""
        command_metadata_setup = ""
        if (
            self._testMethodName
            == "test_source_command_reports_changed_metadata_without_capturing_or_writing"
        ):
            # Change associated label text at the real key boundary, before the
            # production capture listener. Unlike a field attribute change, this
            # retains the field/policy and exercises its live metadata comparison.
            command_metadata_setup = """window.addEventListener('keydown', event => {
              if (event.isTrusted && event.key === 'F10' && !event.repeat) {
                document.querySelector('label').firstChild.data = 'Changed label ';
              }
            }, true);"""
        bridge = (
            BRIDGE.read_text()
            if self._testMethodName
            in (
                "test_paused_page_probe_cannot_outlive_display_deadline",
                "test_rich_field_does_not_advertise_standard_source_local_acceptance",
            )
            else ""
        )
        held_key_observer = (
            HELD_KEY_OBSERVER
            if self._testMethodName
            in (
                "test_source_command_activation_requires_release_proof_frozen_when_keys_were_observed",
                "test_control_postmutation_focus_roundtrip_is_unknown_and_consumed",
                "test_control_source_keeps_original_plan_and_release_ownership",
            )
            else ""
        )
        font_setup = (
            "window.chrome.runtime.getURL = file => 'http://127.0.0.1/' + file;"
            if self._testMethodName
            == "test_packaged_cjk_faces_render_by_target_without_loading_all_regions"
            else ""
        )
        self.context.add_init_script(
            clock_setup
            + command_metadata_setup
            + TRANSPORT
            + "\n"
            + font_setup
            + "\n"
            + held_key_observer
            + "\n"
            + (READ_OBSERVER if self.read_observations is not None else "")
            + "\n"
            + bridge
            + "\n"
            + CONTENT.read_text()
        )
        self.page = self.context.new_page()
        self.cdp = self.context.new_cdp_session(self.page)
        if font_setup:
            self.font_requests = []
            locked = json.loads((ROOT / "eng/fonts/fonts.lock.json").read_text())["files"]
            font_files = {
                "http://127.0.0.1/fonts/" + item["path"]: ROOT / "artifacts/ui-fonts" / item["path"]
                for item in locked
                if "family" in item
            }

            def packaged_font(route):
                path = font_files[route.request.url]
                self.font_requests.append(path.name)
                route.fulfill(
                    path=path, content_type="font/otf" if path.suffix == ".otf" else "font/ttf"
                )

            self.page.route("http://127.0.0.1/fonts/**", packaged_font)
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
        if operation == "overlay-render":
            data = {**(data or {})}
            data.setdefault("appearance", {"uiLanguage": "zh-CN"})
        self.sequence += 1
        if self.read_observations is not None:
            observed = self.page.evaluate(
                """message => {
                  const value = fixtureCommand(message);
                  return {value, audit: fixtureReadAudit.drain()};
                }""",
                {"id": self.sequence, "op": operation, "data": data or {}},
            )
            self.record_read_observations(observed["audit"])
            return observed.get("value")
        return self.page.evaluate(
            "message => window.fixtureCommand(message)",
            {"id": self.sequence, "op": operation, "data": data or {}},
        )

    def record_read_observations(self, audit):
        if self.read_observations is None or not audit:
            return
        remaining = max(0, 256 - len(self.read_observations))
        self.read_observations.extend(audit["rows"][:remaining])
        self.read_omitted += audit["omitted"] + max(0, len(audit["rows"]) - remaining)

    def save_read_observations(self):
        if self.read_observations is None:
            return
        output = Path(
            os.environ.get(
                "TYPELAY_BROWSER_LIFETIME_OUTPUT",
                ROOT / "artifacts/linux/browser-native/overlay/lifetime",
            )
        )
        output.mkdir(parents=True, exist_ok=True)
        (output / (self._testMethodName + "-reads.json")).write_text(
            json.dumps({"rows": self.read_observations, "omitted": self.read_omitted}, indent=2),
            encoding="utf-8",
        )

    def capture(self):
        # Keep setup inside one browser turn so Python/CDP round trips do not
        # consume the production authority's unchanged lifetime. Never retry.
        self.sequence += 3
        result = self.page.evaluate(
            """id => {
              const description = fixtureCommand({id, op: 'describe'});
              if (!description) return {described: false, snapshot: null, diagnostics: null,
                audit: window.fixtureReadAudit?.drain()};
              const snapshot = fixtureCommand({id: id + 1, op: 'capture', data: {
                target: description.target, authority: description.authority}});
              const diagnostics = snapshot ? null : fixtureCommand({id: id + 2,
                op: 'describe', data: {target: description.target}})?.diagnostics ?? null;
              return {described: true, snapshot, diagnostics, audit: window.fixtureReadAudit?.drain()};
            }""",
            self.sequence - 2,
        )
        self.record_read_observations(result.get("audit"))
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
        return self.command(
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

    def test_source_local_acceptance_requires_explicit_dispatch_version_and_supported_keys(self):
        self.type_source()
        cases = [
            ({}, 0),
            ({"sourceLocalAcceptanceVersion": 1}, 0),
            ({"shortcutDispatch": "native"}, 0),
            ({"shortcutDispatch": "page", "sourceLocalAcceptanceVersion": 1}, 1),
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
              const routedReply = connections[1].sent.at(-1);
              // A legacy browser without documentId keeps page preview. Its
              // unproven route cannot obtain the desktop's version-1 capability.
              connections[1].onMessage.emit({op: 'configure-shortcuts', data: {
                shortcutDispatch: 'page', sourceLocalAcceptanceVersion: 1}});
              connections[1].onMessage.emit({id: 4, op: 'describe'});
              const legacyRequest = unproved.sent.at(-1);
              unproved.onMessage.emit({id: legacyRequest.id, data: {preview: true}});
              const legacyPreview = connections[1].sent.at(-1);
              document.onDisconnect.emit();
              return {replies, blocked, retirement, routedReply, legacyPreview,
                closed: connections[1].sent.at(-1),
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
        self.assertEqual(result["legacyPreview"]["id"], 4)
        self.assertEqual(result["legacyPreview"]["data"], {"preview": True})
        self.assertIsNone(result["legacyPreview"]["route"]["documentId"])
        self.assertEqual(result["closed"]["event"], "document-closed")
        self.assertEqual(result["closed"]["route"]["documentToken"], result["documentToken"])

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
        diagnostics = self.page.evaluate(
            "fixtureMessages.filter(item => item.event === 'source-command-diagnostic').map(item => item.data)"
        )
        self.assertEqual(len(diagnostics), 1)
        diagnostic = diagnostics[0]
        self.assertEqual(
            {
                key: value
                for key, value in diagnostic.items()
                if key not in ("policyAuthorityAgeMs", "policyAuthorityAgeAtTriggerMs")
            },
            {
                "kind": "direct",
                "firstReason": "Sent",
                "commandSent": True,
                "hasNative": True,
                "policyCheckPhase": "after-capture",
            },
        )
        self.assertGreaterEqual(diagnostic["policyAuthorityAgeAtTriggerMs"], 0)
        self.assertLessEqual(
            diagnostic["policyAuthorityAgeAtTriggerMs"], diagnostic["policyAuthorityAgeMs"]
        )
        self.assertGreaterEqual(diagnostic["policyAuthorityAgeMs"], 0)
        self.assertLessEqual(diagnostic["policyAuthorityAgeMs"], 146)
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

    def test_source_command_reports_changed_metadata_without_capturing_or_writing(self):
        self.type_source()
        self.activate_source_command_owner(trace_command=True)
        self.page.keyboard.press("F10")
        self.assertEqual(self.page.locator("label").text_content(), "Changed label ")
        commands = self.source_commands()
        self.assertEqual(len(commands), 1)
        self.assertEqual(commands[0]["data"]["reason"], "PolicyUnavailable")
        self.assertNotIn("native", commands[0]["data"])
        diagnostics = self.page.evaluate(
            "fixtureMessages.filter(item => item.event === 'source-command-diagnostic').map(item => item.data)"
        )
        self.assertEqual(len(diagnostics), 1)
        self.assertEqual(diagnostics[0]["firstReason"], "PolicyMetadataChanged")
        self.assertEqual(diagnostics[0]["policyCheckPhase"], "before-capture")
        self.assertTrue(diagnostics[0]["commandSent"])
        self.assertFalse(diagnostics[0]["hasNative"])
        self.assertNotIn("Changed label", json.dumps(diagnostics))
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.page.evaluate("fixturePassedCommandKeys"), [])

    def test_source_command_attribute_change_retires_policy_before_capture(self):
        self.type_source()
        self.activate_source_command_owner(trace_command=True)
        self.page.locator("#source").evaluate(
            "element => element.setAttribute('aria-label', 'Changed label')"
        )
        self.page.keyboard.press("F10")
        commands = self.source_commands()
        self.assertEqual(len(commands), 1)
        self.assertEqual(commands[0]["data"]["reason"], "PolicyUnavailable")
        self.assertNotIn("native", commands[0]["data"])
        diagnostics = self.page.evaluate(
            "fixtureMessages.filter(item => item.event === 'source-command-diagnostic').map(item => item.data)"
        )
        self.assertEqual(len(diagnostics), 1)
        self.assertEqual(diagnostics[0]["firstReason"], "PolicyMissing")
        self.assertIsNone(diagnostics[0]["policyCheckPhase"])
        self.assertTrue(diagnostics[0]["commandSent"])
        self.assertFalse(diagnostics[0]["hasNative"])
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.page.evaluate("fixturePassedCommandKeys"), [])

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
                    "policyAuthorityAgeMs": None,
                    "policyAuthorityAgeAtTriggerMs": None,
                    "policyCheckPhase": None,
                }
            ],
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")

    def test_source_command_owner_renewal_cannot_renew_policy_or_revive_a_retired_owner(self):
        self.type_source()
        owner = self.activate_source_command_owner(trace_command=True)["owner"]
        self.page.wait_for_timeout(175)  # Expire only the original 150ms read policy.
        self.page.evaluate("""() => fixtureCommand({id: 7810, op: 'renew-command-owner', data: {
          ...fixtureOwner, grantSequence: 2, expiresAtMs: fixtureHostClock().boottimeMs + 800}})""")
        self.page.keyboard.press("F10")
        commands = self.source_commands()
        self.assertEqual(len(commands), 1)
        self.assertEqual(commands[0]["data"]["reason"], "PolicyUnavailable")
        diagnostics = self.page.evaluate(
            "fixtureMessages.filter(item => item.event === 'source-command-diagnostic').map(item => item.data)"
        )
        self.assertEqual(len(diagnostics), 1)
        diagnostic = diagnostics[0]
        self.assertEqual(
            {
                key: value
                for key, value in diagnostic.items()
                if key not in ("policyAuthorityAgeMs", "policyAuthorityAgeAtTriggerMs")
            },
            {
                "kind": "direct",
                "firstReason": "PolicyAuthorityExpired",
                "commandSent": True,
                "hasNative": False,
                "policyCheckPhase": "before-capture",
            },
        )
        self.assertGreater(diagnostic["policyAuthorityAgeAtTriggerMs"], 146)
        self.assertLessEqual(
            diagnostic["policyAuthorityAgeAtTriggerMs"], diagnostic["policyAuthorityAgeMs"]
        )
        self.assertGreater(diagnostic["policyAuthorityAgeMs"], 146)
        self.assertLessEqual(diagnostic["policyAuthorityAgeMs"], 10000)
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
        self.activate_source_command_owner(trace_command=True)
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
        self.assertEqual(
            self.page.evaluate(
                "fixtureMessages.filter(item => item.event === 'source-command-diagnostic').map(item => item.data.firstReason)"
            ),
            ["PolicyMissing"],
        )
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
        try:
            # No describe/capture has calibrated the page when this press starts.
            self.page.keyboard.insert_text("Hello")
            self.page.keyboard.down("Shift")
            result = self.activate_source_command_owner()
            self.assertEqual(result["ack"]["state"], "rejected")
            self.assertEqual(result["ack"]["rejectionReason"], "KeysHeld")
            self.assertNotIn("diagnostic", result["ack"])
            # A simultaneous deadline failure is not a recoverable release rejection.
            expired_ack = self.page.evaluate("""() => {
              const prepared = fixtureCommand({id: 7804, op: 'prepare-command-owner',
                data: {ownerId: fixtureOwner.ownerId, generation: fixtureOwner.generation}});
              fixtureCommand({id: 7805, op: 'activate-command-owner', data: {
                ...fixtureOwner, authority: prepared.authority, expiresAtMs: 0}});
              return fixtureMessages.filter(item => item.event === 'command-owner').at(-1).data;
            }""")
            self.assertEqual(expired_ack["state"], "rejected")
            self.assertNotIn("rejectionReason", expired_ack)
            self.page.keyboard.up("Shift")
            # Release does not restore the consumed preparation or the old owner.
            self.page.evaluate("""() => fixtureCommand({id: 7803,
              op: 'activate-command-owner', data: fixtureOwner})""")
            old_ack = self.page.evaluate(
                "fixtureMessages.filter(item => item.event === 'command-owner').at(-1).data"
            )
            self.assertEqual(old_ack["state"], "rejected")
            self.assertNotIn("rejectionReason", old_ack)
            self.page.keyboard.down("Shift")
            result = self.activate_source_command_owner(released_at=0)
            self.assertEqual(result["ack"]["state"], "rejected")
            self.assertEqual(result["ack"]["rejectionReason"], "KeysHeld")
            self.assertNotIn("diagnostic", result["ack"])
            self.page.keyboard.up("Shift")
            result = self.activate_source_command_owner("released-owner")
            self.assertEqual(result["ack"]["state"], "active")
            self.assertEqual(result["ack"]["ownerId"], "released-owner")
            self.assertNotIn("rejectionReason", result["ack"])
            self.assertNotIn("diagnostic", result["ack"])
            self.page.keyboard.press("F10")
            self.assertEqual(len(self.source_commands()), 1)
            self.assertIn("native", self.source_commands()[0]["data"])
        except Exception:
            # Read already-collected evidence only after the original failure.
            # Collection errors must not replace or repair that failure.
            try:
                evidence = self.page.evaluate("""() => ({
                  rows: fixtureHeldAudit.rows, omitted: fixtureHeldAudit.omitted,
                  documentEvents: fixtureMessages.filter(item => item.event === 'document')
                    .slice(0, 4).map(item => item.data)
                })""")
            except Exception as error:
                evidence = {"observationError": repr(error)}
            try:
                print(
                    "HELD_KEY_FAILURE " + json.dumps(evidence, ensure_ascii=False), file=sys.stderr
                )
            except Exception:
                pass  # Evidence output cannot replace the original test failure.
            raise

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
        def save_lifetime():
            # Read only after the original sequence: no publication or clock refresh.
            evidence = self.page.evaluate("""() => ({rows: fixtureHeldAudit.rows,
              omitted: fixtureHeldAudit.omitted, controlEvents: window.controlEvents ?? [],
              sourceFocused: document.querySelector('#source').matches(':focus')})""")
            output = Path(
                os.environ.get(
                    "TYPELAY_BROWSER_LIFETIME_OUTPUT",
                    ROOT / "artifacts/linux/browser-native/overlay/lifetime",
                )
            )
            output.mkdir(parents=True, exist_ok=True)
            (output / (self._testMethodName + ".json")).write_text(
                json.dumps(evidence, indent=2), encoding="utf-8"
            )

        self.addCleanup(save_lifetime)
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
        def save_lifetime():
            evidence = self.page.evaluate("""() => ({rows: fixtureHeldAudit.rows,
              omitted: fixtureHeldAudit.omitted, controlEvents: window.controlEvents ?? [],
              sourceFocused: document.querySelector('#source').matches(':focus')})""")
            output = Path(
                os.environ.get(
                    "TYPELAY_BROWSER_LIFETIME_OUTPUT",
                    ROOT / "artifacts/linux/browser-native/overlay/lifetime",
                )
            )
            output.mkdir(parents=True, exist_ok=True)
            (output / (self._testMethodName + ".json")).write_text(
                json.dumps(evidence, indent=2), encoding="utf-8"
            )

        self.addCleanup(save_lifetime)
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
        published = self.publish(snapshot, self.plan(snapshot))
        self.assertTrue(published["published"], published)
        self.page.evaluate("""document.querySelector('#source').addEventListener('input', () => {
          fixtureCommand({id: 5002, op: 'configure-shortcuts', data: {
            generation: 'configured-in-input', enabled: false, sessionEnabled: true,
            accept: 'F12', exit: 'F24'}});
        }, {once: true});""")
        self.page.keyboard.press("Tab")
        actual = self.page.locator("#source").input_value()
        diagnostics = (
            None
            if actual == "你好"
            else {
                "state": self.command("describe"),
                "events": self.page.evaluate("fixtureMessages.filter(m => m.event)"),
            }
        )
        self.assertEqual(actual, "你好", diagnostics)
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
        published = self.publish(snapshot, self.plan(snapshot))
        self.assertTrue(published["published"], published)
        self.page.keyboard.press("Tab")
        actual = self.page.locator("#source").input_value()
        diagnostics = (
            None
            if actual == "你好"
            else {
                "state": self.command("describe"),
                "events": self.page.evaluate("fixtureMessages.filter(m => m.event)"),
            }
        )
        self.assertEqual(actual, "你好", diagnostics)
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
        self.check_tab_timing(0)

    def test_real_tab_delayed_before_adapter_reports_expired_local_check(self):
        self.check_tab_timing(160)

    def check_tab_timing(self, busy_ms):
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
        self.page.evaluate("value => window.fixtureTabBusyMs = value", busy_ms)
        self.page.keyboard.press("Tab")
        diagnostic = self.page.evaluate(
            "fixtureMessages.findLast(item => item.event === 'tab-diagnostic')?.data"
        )
        self.assertIsNotNone(diagnostic)
        self.assertEqual(diagnostic["publicationRound"], 53)
        self.assertGreaterEqual(diagnostic["eventToTabCheckMs"], 0)
        self.assertGreaterEqual(diagnostic["tabCheckToWriteMs"], 0)
        for prefix in ("event", "tabCheck"):
            self.assertLessEqual(
                diagnostic[prefix + "HostLowerMs"], diagnostic[prefix + "HostUpperMs"]
            )
        if busy_ms:
            self.assertGreaterEqual(diagnostic["eventToTabCheckMs"], busy_ms - 2)
            self.assertLess(diagnostic["remainingMs"], 0)
            self.assertEqual(diagnostic["reason"], "StaleObservation")
            self.assertEqual(self.page.locator("#source").input_value(), "Hello")

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

    def test_coarse_clock_frozen_bucket_refuses_capture(self):
        self.page.keyboard.insert_text("Hello")
        # Isolate the precision capability: no fine tick can be observed in this
        # frozen bucket. Both clocks advance together before the new authority,
        # then stay fixed regardless of OS scheduling. Clock disagreement/expiry
        # must not substitute for the timerPrecision rejection asserted below.
        result = self.page.evaluate("""() => {
          fixtureAdvanceClockBucket();
          const description = fixtureCommand({id: 930, op: 'describe'});
          const capture = fixtureCommand({id: 931, op: 'capture', data: {
            target: description.target, authority: description.authority}});
          return {capture, diagnostics: fixtureCommand({id: 932, op: 'describe'}).diagnostics};
        }""")
        diagnostics = result["diagnostics"]
        self.assertIsNone(result["capture"], diagnostics)
        self.assertEqual(diagnostics["captureCounts"]["timerPrecision"], 1, diagnostics)
        self.assertEqual(diagnostics["captureCounts"]["supported"], 0, diagnostics)
        self.assertIsNone(diagnostics["timerTickMs"], diagnostics)
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

    def render_paused(self, revision=7, *, theme=None, appearance=None):
        # The real paused flow only describes metadata. It must establish its
        # own clock proof without obtaining a source text/selection capture.
        self.sequence += 2
        return self.page.evaluate(
            """({id, revision, theme, appearance}) => {
              const source = fixtureCommand({id, op: 'describe'});
              if (!source) return {source, rendered: {visible: false}};
              const rendered = fixtureCommand({id: id + 1, op: 'overlay-render', data: {
                target: source.target, authority: source.authority, revision, theme, appearance,
                view: {state: 'Paused', canAccept: false, pauseRevision: 3,
                  source: {target: {backend: 'linux-browser', value: source.target,
                    lifetime: 'fixture'}, bounds: source.geometry.bounds},
                  presentation: {kind: 'Paused', visible: true, status: '已暂停',
                    tabHint: '点击恢复', dot: 'Neutral', editorOwnsText: false,
                    showEscape: false}}}});
              return {source, rendered};
            }""",
            {
                "id": self.sequence - 1,
                "revision": revision,
                "theme": theme,
                "appearance": appearance,
            },
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
                    for name in ("Auto → EN", "Tab 交还应用"):
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
                    self.assertNotIn("Esc 退出当前翻译", names)
                    self.assertLessEqual(editor_box[5], more_box[1])
                    host = self.page.locator("html > div[role=region][aria-label=TypeLay]")
                    self.assertTrue(host.evaluate("element => element.shadowRoot === null"))
                    bounds = host.bounding_box()
                    self.assertEqual(bounds["width"], width)
                    for box in (editor_box, more_box):
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
                                ("Translating", "", False, "rgb(20, 20, 19)"),
                                ("Translating", original_text, False, "rgb(116, 114, 108)"),
                                ("ValidTranslation", "Fresh translation", False, "rgb(20, 20, 19)"),
                                (
                                    "TranslationFailed",
                                    "Fresh translation",
                                    False,
                                    "rgb(20, 20, 19)",
                                ),
                                ("EditingTranslation", "Human draft", True, "rgb(20, 20, 19)"),
                                ("ReturningToSource", "Human draft", True, "rgb(20, 20, 19)"),
                                (
                                    "EditedTranslationPending",
                                    "Human draft",
                                    False,
                                    "rgb(20, 20, 19)",
                                ),
                                ("Translating", "Human draft", False, "rgb(116, 114, 108)"),
                                ("Translating", "Human draft", True, "rgb(20, 20, 19)"),
                                ("ValidTranslation", original_text, False, "rgb(20, 20, 19)"),
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

    def test_packaged_cjk_faces_render_by_target_without_loading_all_regions(self):
        self.assertEqual(self.font_requests, [])
        data = self.motion_view()
        data["view"]["text"] = "中文译文"
        data["view"]["targetLanguage"] = "zh"
        data["appearance"] = {"font": "claude", "uiLanguage": "zh-CN", "reduceMotion": True}
        self.assertTrue(self.command("overlay-render", data)["visible"])
        self.page.evaluate("document.fonts.ready")
        self.assertIn("NotoSerifSC-Regular.otf", self.font_requests)
        self.assertFalse(any("JP-" in name or "KR-" in name for name in self.font_requests))
        self.cdp.send("DOM.enable")
        self.cdp.send("CSS.enable")

        def actual_fonts(object_id):
            self.cdp.send("DOM.getDocument", {"depth": -1, "pierce": True})
            node = self.cdp.send("DOM.requestNode", {"objectId": object_id})["nodeId"]
            # The textarea's rendered text belongs to its user-agent
            # shadow subtree, not the light-DOM textarea element.
            tree = self.cdp.send("DOM.describeNode", {"nodeId": node, "depth": -1, "pierce": True})[
                "node"
            ]
            pending = [tree]
            fonts = []
            while pending:
                rendered_node = pending.pop()
                pending.extend(rendered_node.get("children", []))
                pending.extend(rendered_node.get("shadowRoots", []))
                if rendered_node["nodeType"] == 1:
                    rendered_id = self.cdp.send(
                        "DOM.pushNodesByBackendIdsToFrontend",
                        {"backendNodeIds": [rendered_node["backendNodeId"]]},
                    )["nodeIds"][0]
                    fonts.extend(
                        self.cdp.send("CSS.getPlatformFontsForNode", {"nodeId": rendered_id})[
                            "fonts"
                        ]
                    )
            return fonts

        for language, region, sample in [
            ("zh", "SC", "中文译文"),
            ("ja", "JP", "日本語の翻訳"),
            ("ko", "KR", "한국어 번역"),
        ]:
            for kind, family in [("claude", "Serif"), ("sans", "Sans")]:
                with self.subTest(language=language, kind=kind):
                    snapshot = self.capture()
                    data.update(target=snapshot["target"], authority=snapshot["authority"])
                    data["revision"] += 1
                    data["appearance"]["font"] = kind
                    data["view"]["text"] = sample
                    data["view"]["targetLanguage"] = language
                    self.assertTrue(self.command("overlay-render", data)["visible"])
                    self.page.evaluate("document.fonts.ready")
                    # Loading a new regional file can outlive the old display
                    # lease. Render a newly captured view after font readiness;
                    # do not extend or retry the original authority.
                    snapshot = self.capture()
                    data.update(target=snapshot["target"], authority=snapshot["authority"])
                    data["revision"] += 1
                    self.assertTrue(self.command("overlay-render", data)["visible"])
                    fonts = actual_fonts(self.overlay_control("TypeLay 译文", "textbox"))
                    self.assertTrue(
                        any(
                            font["isCustomFont"]
                            and font["familyName"] == f"Noto {family} {region}"
                            and font["glyphCount"] > 0
                            for font in fonts
                        ),
                        fonts,
                    )
        for size in range(13, 20):
            with self.subTest(latin_optical_size=size):
                snapshot = self.capture()
                data.update(target=snapshot["target"], authority=snapshot["authority"])
                data["revision"] += 1
                data["appearance"].update(font="claude", textSize=size)
                data["view"].update(text="Latin optical sizing 012345", targetLanguage="en")
                self.assertTrue(self.command("overlay-render", data)["visible"])
                self.page.evaluate("document.fonts.ready")
                # A new normal view after loading proves selection of this
                # packaged role; it does not revive the old display authority.
                snapshot = self.capture()
                data.update(target=snapshot["target"], authority=snapshot["authority"])
                data["revision"] += 1
                self.assertTrue(self.command("overlay-render", data)["visible"])
                used = [
                    font
                    for font in actual_fonts(self.overlay_control("TypeLay 译文", "textbox"))
                    if font["glyphCount"] > 0
                ]
                self.assertTrue(used)
                self.assertTrue(
                    all(
                        font["isCustomFont"]
                        and font["familyName"] == f"TypeLay Serif {size}"
                        and font["postScriptName"] == f"TypeLaySerif-{size}-Regular"
                        for font in used
                    ),
                    used,
                )
        snapshot = self.capture()
        data.update(target=snapshot["target"], authority=snapshot["authority"])
        data["revision"] += 1
        data["appearance"]["font"] = "system"
        data["view"].update(text="System fonts", targetLanguage="en")
        self.assertTrue(self.command("overlay-render", data)["visible"])
        for object_id in (
            self.overlay_control("TypeLay 译文", "textbox"),
            self.overlay_control("EN → EN"),
        ):
            fonts = actual_fonts(object_id)
            self.assertTrue(any(font["glyphCount"] > 0 for font in fonts), fonts)
            self.assertFalse(any(font["isCustomFont"] for font in fonts), fonts)
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

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
        compact = self.page.locator("html > div[role=region][aria-label=TypeLay]").bounding_box()
        click_button("更多")
        names = menu_names(True)
        self.assertEqual(
            self.page.locator("html > div[role=region][aria-label=TypeLay]").bounding_box(),
            compact,
            "Opening the anchored menu must not resize or move the translation bubble",
        )
        for name in ["复制译文", "重新翻译", "不要在这个应用中显示", "设置"]:
            self.assertIn(name, names)
        self.assertIn("Tab 交还应用", names)
        self.assertNotIn("Tab 接受译文", names)
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        click_button("展开")
        self.page.wait_for_function(
            "document.querySelector('html > div[role=region][aria-label=TypeLay]').getBoundingClientRect().width === 520"
        )
        expanded = self.page.locator("html > div[role=region][aria-label=TypeLay]").bounding_box()
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
          const rect = document.querySelector('html > div[role=region][aria-label=TypeLay]').getBoundingClientRect();
          return ['x', 'y', 'width', 'height'].every(name => rect[name] === expected[name]);
        }""",
            arg=compact,
        )
        self.assertEqual(
            self.page.locator("html > div[role=region][aria-label=TypeLay]").bounding_box(), compact
        )
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

    def test_loading_and_updating_decorations_preserve_editor_and_source(self):
        output = ROOT / "artifacts/linux/browser-native/overlay" / f"states-{uuid.uuid4().hex}"
        output.mkdir(parents=True)
        print(f"Overlay state screenshots: {output}", flush=True)
        inspect = """function() {
          const root = this.getRootNode(), skeleton = root.querySelector('#loading-skeleton');
          const sweep = root.querySelector('#updating-sweep');
          const bars = skeleton ? [...skeleton.children] : [];
          return {sameEditor: this.isConnected && root.querySelector('textarea') === this,
            value: this.value, readOnly: this.readOnly, editorHidden: this.hidden,
            skeletonVisible: !!skeleton && !skeleton.hidden,
            skeletonHiddenFromAccessibility: skeleton?.getAttribute('aria-hidden'),
            bars: bars.map(bar => ({width: bar.getBoundingClientRect().width,
              height: bar.getBoundingClientRect().height, animation: getComputedStyle(bar).animationName})),
            gap: skeleton && getComputedStyle(skeleton).rowGap,
            sweepVisible: !!sweep && !sweep.hidden && getComputedStyle(sweep).display !== 'none',
            sweepAnimation: sweep && getComputedStyle(sweep).animationName,
            sweepPointerEvents: sweep && getComputedStyle(sweep).pointerEvents,
            color: getComputedStyle(this).color,
            retryVisible: !!root.querySelector('#translation-retry'),
            selection: [document.querySelector('#source').selectionStart, document.querySelector('#source').selectionEnd]};
        }"""
        for theme, language in (("light", "zh-CN"), ("dark", "en")):
            for motion in ("full", "reduce"):
                # Each preference pair is an independent source session. Do not
                # spread twenty states and ten screenshots across one clock
                # proof or renew an expired session just to keep a matrix alive.
                self.page.reload()
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
                data = self.motion_view()
                editor = self.overlay_control("TypeLay 译文", "textbox")
                revision = 1
                for state, text, status in (
                    ("IdleCapsule", "", ""),
                    ("Translating", "", "翻译中"),
                    ("ValidTranslation", "Existing translation.", "译文就绪"),
                    ("Translating", "Existing translation.", "更新中"),
                    ("TranslationFailed", "Existing translation.", "暂时无法翻译"),
                ):
                    with self.subTest(theme=theme, motion=motion, state=state, text=bool(text)):
                        revision += 1
                        data.update(
                            revision=revision,
                            theme=theme,
                            appearance={"uiLanguage": language, "motion": motion},
                        )
                        data["view"].update(state=state, text=text, status=status, editable=False)
                        self.assertTrue(self.command("overlay-render", data)["visible"])
                        actual = self.overlay_dom(editor, inspect)
                        self.assertTrue(actual["sameEditor"] and actual["readOnly"], actual)
                        self.assertEqual(actual["value"], text)
                        self.assertEqual(actual["selection"], [5, 5])
                        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
                        loading, updating = (
                            state == "Translating" and not text,
                            state == "Translating" and bool(text),
                        )
                        self.assertEqual(actual["skeletonVisible"], loading, actual)
                        self.assertEqual(actual["editorHidden"], loading or state == "IdleCapsule")
                        self.assertEqual(actual["sweepVisible"], updating and motion == "full")
                        self.assertEqual(actual["retryVisible"], state == "TranslationFailed")
                        if loading:
                            self.assertEqual(actual["skeletonHiddenFromAccessibility"], "true")
                            self.assertEqual(len(actual["bars"]), 2)
                            self.assertEqual(actual["gap"], "9px")
                            self.assertAlmostEqual(
                                actual["bars"][1]["width"] / actual["bars"][0]["width"],
                                0.62,
                                places=2,
                            )
                            for bar in actual["bars"]:
                                self.assertAlmostEqual(bar["height"], 15 * 0.62, delta=0.02)
                                self.assertEqual(
                                    bar["animation"], "none" if motion == "reduce" else "tl-sweep"
                                )
                        if updating:
                            self.assertEqual(actual["sweepPointerEvents"], "none")
                            self.assertEqual(
                                actual["sweepAnimation"],
                                "none" if motion == "reduce" else "tl-sweep",
                            )
                            self.assertEqual(
                                actual["color"],
                                "rgb(116, 114, 108)" if theme == "light" else "rgb(154, 152, 143)",
                            )
                        if motion == "reduce":
                            host = self.page.locator("html > div[role=region][aria-label=TypeLay]")
                            self.page.screenshot(
                                path=output / f"{theme}-{state}-{'old' if text else 'empty'}.png",
                                clip=host.bounding_box(),
                            )
                self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                self.assertEqual(self.accepted(), [])

    def test_failure_retry_uses_original_view_and_refuses_unknown_composition(self):
        data = self.motion_view()
        data.update(revision=2, appearance={"uiLanguage": "zh-CN", "motion": "reduce"})
        data["view"].update(
            state="TranslationFailed",
            text="Previous translation",
            status="暂时无法翻译",
            editable=False,
        )
        self.assertTrue(self.command("overlay-render", data)["visible"])
        retry = self.overlay_control("重试")
        point = self.overlay_dom(
            retry,
            "function() { return this.getBoundingClientRect().toJSON(); }",
        )
        self.page.mouse.click(point["x"] + point["width"] / 2, point["y"] + point["height"] / 2)

        def retries():
            return self.page.evaluate(
                "fixtureMessages.filter(m => m.event === 'ui' && m.data.operation === 'regenerate').map(m => ({revision:m.data.revision,generation:m.data.generation,target:m.data.target}))"
            )

        self.assertEqual(
            retries(), [{"revision": 2, "generation": "browser-default", "target": data["target"]}]
        )
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        self.assertTrue(self.overlay_dom(retry, "function() { return this.disabled; }"))
        self.page.mouse.click(point["x"] + point["width"] / 2, point["y"] + point["height"] / 2)
        self.assertEqual(len(retries()), 1)
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        data["revision"] = 3
        data["view"].update(state="ValidTranslation", text="New translation", status="译文就绪")
        self.assertTrue(self.command("overlay-render", data)["visible"])
        self.assertFalse(self.overlay_dom(retry, "function() { return this.isConnected; }"))
        data["revision"] = 4
        data["view"].update(state="TranslationFailed", status="暂时无法翻译")
        self.assertTrue(self.command("overlay-render", data)["visible"])
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.overlay_dom(
            editor,
            "function() { this.dispatchEvent(new CompositionEvent('compositionstart', {bubbles:true})); }",
        )
        self.click_overlay_control("重试")
        self.assertEqual(len(retries()), 1)
        self.assertTrue(
            self.overlay_dom(
                editor,
                "function() { return this.isConnected && this.value === 'New translation'; }",
            )
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        # The retired button's old pixel now hits ordinary header chrome.
        # A real click there may naturally blur the source; it is not a late
        # event delivered to the detached button. Check only its lack of action.
        data["revision"] = 5
        data["view"].update(state="ValidTranslation", status="译文就绪")
        self.assertTrue(self.command("overlay-render", data)["visible"])
        self.assertFalse(self.overlay_dom(retry, "function() { return this.isConnected; }"))
        self.page.mouse.click(point["x"] + point["width"] / 2, point["y"] + point["height"] / 2)
        self.assertEqual(len(retries()), 1)
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

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

    def observe_overlay_lifetime(self):
        # Observe public transport and actual DOM only. No heartbeat, capture,
        # mutation of product state, or production deadline is introduced here.
        self.page.evaluate("""() => {
          let editor = null, menu = null, host = null, expiry = null, omitted = 0, phase = null;
          const rows = [];
          const state = () => ({
            documentFocused: document.hasFocus(), visibility: document.visibilityState,
            sourceFocused: document.querySelector('#source').matches(':focus'), phase,
            hostConnected: !!(host || document.querySelector('html > div[role=region][aria-label=TypeLay]'))?.isConnected,
            editorConnected: editor?.isConnected ?? null, editorFocused: editor?.matches(':focus') ?? null,
            menuHidden: menu?.hidden ?? null,
            hostExpiryAtPerformanceMs: expiry
          });
          const log = (kind, detail = {}) => {
            if (rows.length < 96) rows.push({kind, ms: performance.now(), wall: Date.now(), ...state(), ...detail});
            else omitted++;
          };
          let previous = '';
          const changed = () => {
            const current = JSON.stringify(state());
            if (current !== previous) { previous = current; log('dom-change'); }
          };
          const observer = new MutationObserver(changed);
          observer.observe(document.documentElement, {childList: true, subtree: true});
          for (const type of ['keydown', 'keyup']) window.addEventListener(type, event => {
            if (['Escape', 'Control', 'F7'].includes(event.key))
              log('key-capture', {type, key: event.key, trusted: event.isTrusted,
                repeat: event.repeat, composing: event.isComposing});
          }, true);
          window.addEventListener('keydown', event => {
            if (event.key === 'Escape') log('key-bubble', {key: event.key,
              trusted: event.isTrusted, prevented: event.defaultPrevented});
          });
          const raw = window.fixtureRawCommand;
          window.fixtureRawCommand = message => {
            log('command-before', {operation: message.op});
            const result = raw(message);
            if (['overlay-render', 'overlay-renew'].includes(message.op))
              expiry = message.data.expiresAtMs - 1000000; // Exact fixture clock offset.
            log('command-after', {operation: message.op,
              visibleReply: result?.visible ?? null, described: message.op === 'describe' ? !!result : null,
              clockReason: result?.diagnostics?.lastClockReason ?? null,
              clockAgeMs: result?.diagnostics?.clockAgeMs ?? null});
            return result;
          };
          window.fixtureOverlayLifetime = {
            phase(value) { phase = value; editor = menu = host = null; log('phase'); },
            watch(value) {
              editor = value; host = value.getRootNode().host;
              menu = value.getRootNode().querySelector('#more-actions');
              observer.observe(host, {childList: true, attributes: true});
              if (menu) observer.observe(menu, {attributes: true, attributeFilter: ['hidden']});
              editor.addEventListener('compositionstart', event => log('compositionstart', {trusted: event.isTrusted}));
              log('watch-editor');
            },
            finish() { log('final'); observer.disconnect(); return {rows, omitted}; }
          };
          log('observer-ready');
        }""")

        def save():
            value = self.page.evaluate("fixtureOverlayLifetime.finish()")
            output = Path(
                os.environ.get(
                    "TYPELAY_BROWSER_LIFETIME_OUTPUT",
                    ROOT / "artifacts/linux/browser-native/overlay/lifetime",
                )
            )
            output.mkdir(parents=True, exist_ok=True)
            (output / (self._testMethodName + ".json")).write_text(
                json.dumps(value, indent=2), encoding="utf-8"
            )

        self.addCleanup(save)

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
        if self.read_observations is not None:
            function = (
                "function() { const value = ("
                + function
                + """).call(this);
              return {value, audit: fixtureReadAudit.drain(this)}; }"""
            )
        result = self.cdp.send(
            "Runtime.callFunctionOn",
            {
                "objectId": object_id,
                "functionDeclaration": function,
                "returnByValue": True,
            },
        )["result"].get("value")
        if self.read_observations is not None and result is not None:
            self.record_read_observations(result["audit"])
            return result.get("value")
        return result

    def click_overlay_control(self, name):
        rect = self.overlay_dom(
            self.overlay_control(name),
            "function() { return this.getBoundingClientRect().toJSON(); }",
        )
        self.page.mouse.click(rect["x"] + rect["width"] / 2, rect["y"] + rect["height"] / 2)

    def observe_overlay_frames(self, *, start_on_click=False, click_label="展开"):
        # Public browser DOM/RAF observation of the real closed-shadow widgets.
        # It neither reads adapter variables nor supplies an animation model.
        return self.cdp.send(
            "Runtime.callFunctionOn",
            {
                "objectId": self.overlay_control("TypeLay 译文", "textbox"),
                "arguments": (
                    [{"objectId": self.overlay_control(click_label)}] if start_on_click else []
                ),
                "returnByValue": True,
                "functionDeclaration": """function(expandControl) {
          const editor = this, panel = editor.parentElement, host = panel.getRootNode().host;
          const bar = panel.querySelector('header');
          window.fixtureMotionFrames = [];
          window.fixtureMotionDone = false;
          window.fixtureMotionStartedMs = null;
          window.fixtureMotionClick = null;
          window.fixtureMotionControl = expandControl ?? null;
          // Read only after a failed wait, retaining the observed control's
          // identity even if the live overlay was removed or replaced.
          window.fixtureMotionInspection = () => {
            const liveHost = document.querySelector('html > div[role=region][aria-label=TypeLay]');
            return {ms: performance.now(),
              visibility: document.visibilityState, documentFocused: document.hasFocus(),
              hostConnected: host.isConnected, editorConnected: editor.isConnected,
              expandConnected: expandControl?.isConnected ?? null,
              liveHostPresent: !!liveHost, sameHost: liveHost === host,
              editorFocused: panel.getRootNode().activeElement === editor,
              sourceFocused: document.activeElement?.id === 'source',
              host: host.getBoundingClientRect().toJSON(),
              editor: editor.getBoundingClientRect().toJSON(),
              expand: expandControl?.getBoundingClientRect().toJSON() ?? null};
          };
          function startObservation() {
            const start = performance.now();
            window.fixtureMotionStartedMs = start;
            function sample() {
              // CDP Animation playback changes can offset RAF's timeline. Use
              // the same monotonic clock as the unchanged observation start.
              const now = performance.now();
              fixtureMotionFrames.push({ms: now - start, connected: host.isConnected,
                host: host.getBoundingClientRect().toJSON(), editor: editor.getBoundingClientRect().toJSON(),
                bar: bar.getBoundingClientRect().toJSON(), text: editor.value,
                start: editor.selectionStart, end: editor.selectionEnd,
                sourceFocused: document.activeElement.id === 'source'});
              if (now - start < 360) requestAnimationFrame(sample);
              else window.fixtureMotionDone = true;
            }
            requestAnimationFrame(sample);
            return start;
          }
          if (!expandControl) return startObservation();
          // Preparation/AX/CDP latency must not consume the motion window.
          // Only the resolved expand button's real click starts observation.
          function onExpand(event) {
            if (!event.isTrusted) return;
            window.fixtureMotionClick = {ms: performance.now(), trusted: event.isTrusted,
              sameControl: event.currentTarget === expandControl,
              connected: expandControl.isConnected};
            expandControl.removeEventListener('click', onExpand, true);
            startObservation();
          }
          expandControl.addEventListener('click', onExpand, true);
        }""",
            },
        )["result"].get("value")

    def motion_frames(self):
        self.page.wait_for_function("window.fixtureMotionDone === true")
        return self.page.evaluate("fixtureMotionFrames")

    @staticmethod
    def translation_menu_state(**changes):
        return {
            "settingsRevision": 12,
            "styleReleased": True,
            "canChangeStyle": True,
            "canInheritStyle": True,
            "applicationStyle": None,
            "effectiveStyle": None,
            "styleUnavailableReason": None,
            "inheritUnavailableReason": None,
            "alternativeReleased": True,
            "canRequestAlternative": True,
            "alternativeUnavailableReason": None,
            "feedback": None,
            **changes,
        }

    def set_translation_menu(self, data, actions, accept="Tab", exit_key="Esc"):
        data["view"]["viewContext"] = {
            "token": {
                "target": {"backend": "linux-browser", "value": data["target"], "lifetime": "test"},
                "commandGeneration": self.page.evaluate("fixtureBindings.generation"),
                "revision": data["revision"],
            },
            "bindings": {"accept": accept, "exit": exit_key},
            "canEdit": bool(data["view"]["editable"]),
            "translationActions": actions,
        }
        self.assertTrue(self.command("overlay-render", data)["visible"])

    def test_more_menu_four_styles_separate_inheritance_and_real_state(self):
        output = ROOT / "artifacts/linux/browser-native/overlay" / f"more-{uuid.uuid4().hex}"
        output.mkdir(parents=True)
        print(f"More menu screenshots: {output}", flush=True)
        for theme, language in (("light", "zh-CN"), ("dark", "en")):
            for phase in ("preview", "closed", "editing", "loading", "updating"):
                with self.subTest(theme=theme, language=language, phase=phase):
                    # Independent real captures, not renewal of a previous menu.
                    self.page.reload()
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
                    if phase == "editing":
                        _, data = self.open_editor("Tab", "Esc")
                    else:
                        data = self.motion_view()
                        data["view"].update(text="Current translation.", editable=False)
                    data.update(
                        theme=theme, appearance={"uiLanguage": language, "motion": "reduce"}
                    )
                    if phase in ("loading", "updating"):
                        data["view"].update(
                            state="Translating",
                            text="" if phase == "loading" else "Previous translation.",
                        )
                    actions = self.translation_menu_state(
                        styleReleased=phase != "closed",
                        alternativeReleased=phase != "closed",
                        canChangeStyle=phase == "preview",
                        canInheritStyle=phase in ("preview", "closed"),
                        canRequestAlternative=phase == "preview",
                        applicationStyle=None if phase == "preview" else "Formal",
                        effectiveStyle=None if phase == "closed" else "Formal",
                        styleUnavailableReason="请先完成当前编辑" if phase == "editing" else None,
                        glossaryReleased=False,
                        canOpenGlossary=phase not in ("loading", "updating"),
                        glossaryUnavailableReason=None,
                    )
                    self.set_translation_menu(data, actions)
                    editor = (
                        self.overlay_control(
                            "TypeLay Translation" if language == "en" else "TypeLay 译文", "textbox"
                        )
                        if phase != "loading"
                        else None
                    )
                    self.click_overlay_control("More" if language == "en" else "更多")
                    menu = self.overlay_control(
                        "More Actions" if language == "en" else "更多操作", "group"
                    )
                    actual = self.overlay_dom(
                        menu,
                        """function() {
                      const style = this.querySelector('[role=group]');
                      const observe = button => ({text:(button.querySelector(':scope > span')?.textContent || button.textContent).trim(), disabled:button.disabled,
                        pressed:button.getAttribute('aria-pressed'), rect:button.getBoundingClientRect().toJSON(),
                        background:getComputedStyle(button).backgroundColor});
                      return {buttons:[...this.querySelectorAll(':scope > button')].map(observe),
                        styles:[...style.querySelectorAll('button')].map(observe),
                        styleRect:style.getBoundingClientRect().toJSON(), styleTitle:style.title,
                        styleIcons:style.querySelectorAll('svg').length,
                        rect:this.getBoundingClientRect().toJSON(),
                        editorFocused:this.getRootNode().querySelector('textarea').matches(':focus'),
                        sourceFocused:document.querySelector('#source').matches(':focus'),
                        selection:[document.querySelector('#source').selectionStart,document.querySelector('#source').selectionEnd]};
                    }""",
                    )
                    styles = actual["styles"]
                    self.assertEqual(len(styles), 4, actual)
                    self.assertEqual(
                        [item["text"].removeprefix("✓ ") for item in styles],
                        ["Natural", "Formal", "Concise", "Casual"]
                        if language == "en"
                        else ["自然", "正式", "简洁", "口语"],
                    )
                    self.assertEqual(
                        [item["disabled"] for item in styles],
                        [phase != "preview"] * 2 + [True, True],
                    )
                    self.assertEqual(
                        [item["pressed"] == "true" for item in styles],
                        [False, phase != "closed", False, False],
                    )
                    for item in styles:
                        self.assertAlmostEqual(item["rect"]["y"], styles[0]["rect"]["y"], delta=1)
                        self.assertEqual(item["rect"]["height"], 22)
                    if phase != "closed":
                        self.assertNotEqual(styles[1]["background"], styles[0]["background"])
                    else:
                        self.assertEqual(styles[1]["background"], styles[0]["background"])
                    self.assertEqual(actual["styleIcons"], 1)
                    if phase == "editing":
                        self.assertTrue(actual["styleTitle"], actual)
                    elif phase == "preview":
                        self.assertEqual(actual["styleTitle"], "")
                    buttons = actual["buttons"]
                    expected = (
                        [
                            "Translate Again",
                            "Rephrase",
                            "Copy Translation",
                            "Add to Glossary",
                            "Hide for Now",
                            "Disable for This App",
                            "Inherit",
                            "Settings",
                        ]
                        if language == "en"
                        else [
                            "重新翻译",
                            "换种说法",
                            "复制译文",
                            "加入术语表",
                            "暂时隐藏",
                            "不要在这个应用中显示",
                            "继承",
                            "设置",
                        ]
                    )
                    self.assertEqual(
                        [item["text"].removeprefix("✓ ") for item in buttons], expected
                    )
                    self.assertGreater(actual["styleRect"]["y"], buttons[1]["rect"]["y"])
                    self.assertLess(actual["styleRect"]["bottom"], buttons[2]["rect"]["bottom"])
                    self.assertEqual(buttons[0]["disabled"], phase in ("loading", "updating"))
                    self.assertEqual(buttons[2]["disabled"], phase == "loading")
                    self.assertEqual(buttons[6]["disabled"], phase not in ("preview", "closed"))
                    self.assertEqual(buttons[6]["pressed"] == "true", phase == "preview")
                    self.assertEqual(actual["selection"], [5, 5])
                    self.assertEqual(actual["editorFocused"], phase == "editing")
                    self.assertEqual(actual["sourceFocused"], phase != "editing")
                    if editor:
                        self.assertTrue(
                            self.overlay_dom(editor, "function() { return this.isConnected; }")
                        )
                    self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                    self.assertEqual(self.accepted(), [])
                    self.page.screenshot(
                        path=output / f"{theme}-{language}-{phase}.png", clip=actual["rect"]
                    )
                    if phase == "preview":
                        # Losing qualification clears only the applied-style
                        # highlight; this same opening does not gain authority.
                        self.set_translation_menu(
                            data,
                            {
                                **actions,
                                "effectiveStyle": None,
                                "styleReleased": False,
                                "canChangeStyle": False,
                            },
                        )
                        self.assertEqual(
                            self.overlay_dom(
                                menu,
                                """function() {
                              return {connected:this.isConnected, hidden:this.hidden,
                                pressed:[...this.querySelector('[role=group]').querySelectorAll('button')]
                                  .map(button=>button.getAttribute('aria-pressed') === 'true'),
                                disabled:[...this.querySelector('[role=group]').querySelectorAll('button')]
                                  .map(button=>button.disabled)};
                            }""",
                            ),
                            {
                                "connected": True,
                                "hidden": False,
                                "pressed": [False] * 4,
                                "disabled": [True] * 4,
                            },
                        )
                        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
                        self.assertEqual(self.accepted(), [])

    def test_idle_and_paused_capsules_fit_visible_content_and_keep_source_anchor(self):
        output = ROOT / "artifacts/linux/browser-native/overlay" / f"capsule-{uuid.uuid4().hex}"
        output.mkdir(parents=True)
        print(f"Capsule screenshots and geometry: {output}", flush=True)
        observations = []
        for theme, language in (("light", "zh-CN"), ("dark", "en")):
            for phase in ("idle", "paused"):
                self.page.reload()
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
                self.page.locator("#source").evaluate("""element => {
                  element.style.cssText = 'display:block;margin-top:500px;margin-left:160px;width:480px;box-sizing:border-box';
                }""")
                self.page.locator("#source").focus()
                appearance = {
                    "font": "claude",
                    "textSize": 15,
                    "uiLanguage": language,
                    "motion": "reduce",
                }
                if phase == "paused":
                    self.page.keyboard.insert_text("Hello")
                    self.assertTrue(
                        self.render_paused(theme=theme, appearance=appearance)["rendered"][
                            "visible"
                        ]
                    )
                    control = self.overlay_control("Resume" if language == "en" else "恢复")
                else:
                    snapshot = self.type_source()
                    self.assertTrue(
                        self.command(
                            "overlay-render",
                            {
                                "target": snapshot["target"],
                                "authority": snapshot["authority"],
                                "revision": 1,
                                "theme": theme,
                                "appearance": appearance,
                                "view": {
                                    "presentation": {
                                        "visible": True,
                                        "editorOwnsText": False,
                                        "tabHint": "",
                                    },
                                    "state": "IdleCapsule",
                                    "text": "",
                                    "editable": False,
                                    "sourceLanguage": "auto",
                                    "targetLanguage": "en",
                                    "status": "",
                                },
                            },
                        )["visible"]
                    )
                    control = self.overlay_control("Auto → EN")
                actual = self.overlay_dom(
                    control,
                    """function() {
                  const root = this.getRootNode(), panel = root.querySelector('section');
                  const header = panel.querySelector('header'), source = document.querySelector('#source');
                  const language = header.querySelector('.language'), status = header.querySelector('.status');
                  const resume = [...header.children].find(e => e.tagName === 'BUTTON' && e !== language);
                  const range = document.createRange(); range.selectNodeContents(status);
                  const rect = e => e.getBoundingClientRect().toJSON();
                  return {host: rect(root.host), source: rect(source), language: rect(language),
                    status: rect(status), statusContents: range.getBoundingClientRect().toJSON(), resume: rect(resume),
                    paused: panel.classList.contains('paused'), pauseIcon: status.querySelector('svg')?.getAttribute('aria-hidden') ?? null,
                    statusDisplay: getComputedStyle(status).display, statusGrow: getComputedStyle(status).flexGrow,
                    actionsDisplay: getComputedStyle(header.querySelector('.footer-actions')).display,
                    editorHidden: panel.querySelector('textarea').hidden, editorReadonly: panel.querySelector('textarea').readOnly,
                    sourceFocused: document.activeElement === source, selection: [source.selectionStart, source.selectionEnd],
                    sourceText: source.value, connected: root.host.isConnected};
                }""",
                )
                observations.append(
                    {"theme": theme, "language": language, "phase": phase, **actual}
                )
                (output / "geometry.json").write_text(
                    json.dumps(observations, indent=2), encoding="utf-8"
                )
                self.page.screenshot(path=str(output / f"{theme}-{language}-{phase}.png"))
                with self.subTest(theme=theme, phase=phase, part="intrinsic-shell"):
                    self.assertEqual(actual["host"]["height"], 34)
                    if phase == "idle":
                        self.assertAlmostEqual(
                            actual["host"]["width"], actual["language"]["width"] + 8, delta=0.1
                        )
                        self.assertEqual(actual["statusDisplay"], "none")
                    else:
                        # Prototype pause label has 9/10px padding, then a 6px
                        # gap and the real Resume button; no fixed shell width.
                        expected = (
                            actual["statusContents"]["width"]
                            + 19
                            + 6
                            + actual["resume"]["width"]
                            + 8
                        )
                        self.assertAlmostEqual(actual["host"]["width"], expected, delta=0.1)
                        self.assertEqual(actual["statusGrow"], "0")
                    self.assertEqual(actual["actionsDisplay"], "none")
                with self.subTest(theme=theme, phase=phase, part="source-and-controls"):
                    self.assertTrue(actual["connected"] and actual["sourceFocused"])
                    self.assertTrue(actual["editorHidden"] and actual["editorReadonly"])
                    self.assertEqual(actual["selection"], [5, 5])
                    self.assertEqual(actual["sourceText"], "Hello")
                    self.assertAlmostEqual(
                        actual["host"]["right"], actual["source"]["right"], delta=0.1
                    )
                    self.assertAlmostEqual(
                        actual["host"]["bottom"], actual["source"]["top"] - 6, delta=0.1
                    )
                    if phase == "paused":
                        self.assertEqual(actual["pauseIcon"], "true")
                        self.assertEqual(actual["resume"]["height"], 26)
                        self.assertGreater(actual["resume"]["width"], 0)
                        self.assertGreaterEqual(
                            actual["resume"]["left"], actual["host"]["left"] + 4
                        )
                        self.assertLessEqual(actual["resume"]["right"], actual["host"]["right"] - 4)
                    self.assertEqual(self.accepted(), [])

    def test_overlay_footer_compact_direction_and_icon_controls_preserve_source(self):
        output = ROOT / "artifacts/linux/browser-native/overlay" / f"footer-{uuid.uuid4().hex}"
        output.mkdir(parents=True)
        print(f"Footer screenshots: {output}", flush=True)
        scenarios = (
            ("light", "zh-CN", "en", "zh", "英语", "简体中文", "EN → 中", False, "preview"),
            (
                "dark",
                "en",
                "zh-Hant",
                "en",
                "Traditional Chinese",
                "English",
                "繁 → EN",
                True,
                "preview",
            ),
            ("light", "zh-CN", "auto", "ja", "自动检测", "日语", "Auto → JA", False, "preview"),
            (
                "dark",
                "en",
                "ko",
                "zh-CN",
                "Korean",
                "Simplified Chinese",
                "KO → 中",
                True,
                "preview",
            ),
        ) + tuple(
            (theme, language, "auto", "en", "Auto", "English", "Auto → EN", False, phase)
            for theme, language in (("light", "zh-CN"), ("dark", "en"))
            for phase in ("idle", "paused")
        )
        for (
            theme,
            language,
            source,
            target,
            source_name,
            target_name,
            compact,
            long,
            phase,
        ) in scenarios:
            with self.subTest(theme=theme, source=source, target=target, phase=phase):
                # Each independent direction starts a normal capture, or the
                # existing metadata-only paused path. No old deadline is extended.
                self.page.reload()
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
                appearance = {"uiLanguage": language, "motion": "reduce"}
                if phase == "paused":
                    self.page.keyboard.insert_text("Hello")
                    self.assertTrue(
                        self.render_paused(theme=theme, appearance=appearance)["rendered"][
                            "visible"
                        ]
                    )
                    control = self.overlay_control("Resume" if language == "en" else "恢复")
                else:
                    data = self.motion_view()
                    data.update(theme=theme, appearance=appearance)
                    data["view"].update(
                        sourceLanguage=source,
                        targetLanguage=target,
                        sourceLanguageLabel=source_name,
                        targetLanguageLabel=target_name,
                        text="" if phase == "idle" else "Current candidate. " * (80 if long else 1),
                        state="IdleCapsule" if phase == "idle" else "ValidTranslation",
                    )
                    self.assertTrue(self.command("overlay-render", data)["visible"])
                    control = self.overlay_control(f"{source_name} → {target_name}")
                actual = self.overlay_dom(
                    control,
                    """function() {
                  const panel=this.getRootNode().querySelector('section'), bar=panel.querySelector('header');
                  const editor=panel.querySelector('textarea');
                  const direction=bar.querySelector('.language'), controls=[...bar.querySelectorAll('.disclosure')];
                  return {label:direction.textContent.trim(), aria:direction.getAttribute('aria-label'),
                    editorConnected:editor.isConnected, readonly:editor.readOnly, editorHidden:editor.hidden,
                    controls:controls.map(button=>({hidden:button.hidden, name:button.getAttribute('aria-label'),
                      display:getComputedStyle(button).display, svg:button.querySelector('svg')?.getAttribute('aria-hidden') ?? null,
                      width:button.getBoundingClientRect().width, height:button.getBoundingClientRect().height})),
                    rect:panel.getRootNode().host.getBoundingClientRect().toJSON()};
                }""",
                )
                if phase != "paused":
                    with self.subTest(part="compact-direction-and-full-accessible-name"):
                        self.assertEqual(actual["label"], compact)
                        self.assertEqual(actual["aria"], f"{source_name} → {target_name}")
                with self.subTest(part="short-content-keeps-expand-hidden"):
                    self.assertEqual(
                        [c["hidden"] for c in actual["controls"]], [not long, phase != "preview"]
                    )
                with self.subTest(part="native-icon-button-size-and-names"):
                    self.assertEqual(
                        [c["name"] for c in actual["controls"]],
                        ["Expand", "More"] if language == "en" else ["展开", "更多"],
                    )
                    self.assertEqual([c["svg"] for c in actual["controls"]], ["true", "true"])
                    for control in actual["controls"]:
                        if control["hidden"]:
                            self.assertEqual(control["display"], "none")
                            self.assertEqual([control["width"], control["height"]], [0, 0])
                        else:
                            self.assertEqual(control["display"], "grid")
                            self.assertEqual([control["width"], control["height"]], [26, 26])
                self.assertTrue(actual["editorConnected"] and actual["readonly"])
                self.assertEqual(actual["editorHidden"], phase != "preview")
                self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
                self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                self.assertEqual(self.accepted(), [])
                # Hover only, not a click, for the normal footer screenshot.
                rect = actual["rect"]
                self.page.mouse.move(rect["x"] + 12, rect["y"] + 12)
                self.page.screenshot(
                    path=output / f"{theme}-{source}-{target}-{phase}.png", clip=rect
                )

    def test_overlay_footer_reveals_on_hover_focus_and_pins_open_more(self):
        output = (
            ROOT
            / "artifacts/linux/browser-native/overlay"
            / f"footer-interaction-{uuid.uuid4().hex}"
        )
        output.mkdir(parents=True)
        print(f"Footer interaction screenshots: {output}", flush=True)
        for mode in ("pointer", "editor-focus"):
            with self.subTest(mode=mode):
                self.page.reload()
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
                if mode == "editor-focus":
                    _, data = self.open_editor("Tab", "Esc")
                else:
                    data = self.motion_view()
                data["appearance"] = {"uiLanguage": "zh-CN", "motion": "reduce"}
                self.assertTrue(self.command("overlay-render", data)["visible"])
                editor = self.overlay_control("TypeLay 译文", "textbox")
                self.page.mouse.move(10, 10)

                def actual():
                    return self.overlay_dom(
                        editor,
                        """function() {
                      const panel=this.parentElement, more=panel.querySelector('header button[aria-controls]');
                      return {connected:this.isConnected, focused:this.matches(':focus'),
                        opacity:getComputedStyle(more).opacity, open:more.getAttribute('aria-expanded'),
                        menuHidden:panel.querySelector('#more-actions').hidden};
                    }""",
                    )

                self.assertEqual(actual()["opacity"], "1" if mode == "editor-focus" else "0")
                if mode == "pointer":
                    rect = self.overlay_dom(
                        editor, "function() {return this.getBoundingClientRect().toJSON();}"
                    )
                    self.page.mouse.move(rect["x"] + 10, rect["y"] + 10)
                    self.assertEqual(actual()["opacity"], "1")
                self.page.screenshot(path=output / f"{mode}-revealed.png")
                self.click_overlay_control("更多")
                self.page.mouse.move(10, 10)
                self.assertEqual(
                    actual(),
                    {
                        "connected": True,
                        "focused": mode == "editor-focus",
                        "opacity": "1",
                        "open": "true",
                        "menuHidden": False,
                    },
                )
                self.page.screenshot(path=output / f"{mode}-pinned.png")
                self.page.keyboard.press("Escape")
                self.assertEqual(
                    actual(),
                    {
                        "connected": True,
                        "focused": mode == "editor-focus",
                        "opacity": "1" if mode == "editor-focus" else "0",
                        "open": "false",
                        "menuHidden": True,
                    },
                )
                self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                self.assertEqual(self.accepted(), [])
                self.assertFalse(
                    self.page.evaluate("""fixtureMessages.some(m =>
                    m.event === 'ui' && ['escape','return'].includes(m.data.operation))""")
                )

    def test_more_menu_hide_hint_and_outside_pointer_dismissal(self):
        for language, exit_key in (("zh-CN", "Esc"), ("en", "F7")):
            with self.subTest(language=language):
                self.page.reload()
                self.command(
                    "configure-shortcuts",
                    {
                        "generation": "browser-default",
                        "sessionEnabled": True,
                        "enabled": False,
                        "accept": "Tab",
                        "exit": exit_key,
                    },
                )
                self.page.locator("#source").focus()
                data = self.motion_view()
                data["appearance"] = {"uiLanguage": language, "motion": "reduce"}
                self.set_translation_menu(data, self.translation_menu_state(), exit_key=exit_key)
                self.click_overlay_control("More" if language == "en" else "更多")
                menu = self.overlay_control(
                    "More Actions" if language == "en" else "更多操作", "group"
                )
                before = self.overlay_dom(
                    menu,
                    """function() {
                  const buttons=[...this.querySelectorAll(':scope > button')];
                  const button=buttons[4];
                  return {label:button.querySelector('span').textContent,
                    hint:button.querySelector('kbd')?.textContent,
                    editor:this.getRootNode().querySelector('textarea').value};
                }""",
                )
                with self.subTest(part="localized-hide-and-real-key"):
                    self.assertEqual(
                        before["label"], "Hide for Now" if language == "en" else "暂时隐藏"
                    )
                    self.assertEqual(before.get("hint"), exit_key)
                # Clicking the current source outside the popup closes only the
                # menu, without consuming the source pointer or ending a session.
                self.page.locator("#source").click(position={"x": 450, "y": 10})
                self.assertEqual(
                    self.overlay_dom(
                        menu,
                        """function() {
                  return {connected:this.isConnected, hidden:this.hidden,
                    editor:this.getRootNode().querySelector('textarea').value};
                }""",
                    ),
                    {"connected": True, "hidden": True, "editor": before["editor"]},
                )
                self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
                self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                self.assertEqual(self.accepted(), [])
                self.assertFalse(
                    self.page.evaluate("""fixtureMessages.some(m =>
                  m.event === 'ui' && ['escape','return'].includes(m.data.operation))""")
                )

    def test_more_menu_copy_shortcut_uses_current_text_and_refuses_unknown_or_expired(self):
        self.context.grant_permissions(
            ["clipboard-read", "clipboard-write"], origin="http://127.0.0.1"
        )
        original_clipboard = self.page.evaluate("navigator.clipboard.readText()")
        self.addCleanup(
            lambda: self.page.evaluate(
                "text => navigator.clipboard.writeText(text)", original_clipboard
            )
        )
        for mode in ("control", "meta", "editor", "unknown-editor", "expired", "no-menu"):
            with self.subTest(mode=mode):
                self.page.reload()
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
                self.page.evaluate("navigator.clipboard.writeText('clipboard sentinel')")
                if mode in ("editor", "unknown-editor"):
                    _, data = self.open_editor("Tab", "Esc")
                else:
                    data = self.motion_view()
                    data["view"]["text"] = "Current whole translation."
                data["appearance"] = {"uiLanguage": "zh-CN", "motion": "reduce"}
                self.set_translation_menu(data, self.translation_menu_state())
                editor = self.overlay_control("TypeLay 译文", "textbox")
                before = self.overlay_dom(
                    editor,
                    "function() { return [this.value,this.selectionStart,this.selectionEnd]; }",
                )
                menu = None
                if mode != "no-menu":
                    self.click_overlay_control("更多")
                    menu = self.overlay_control("更多操作", "group")
                if mode == "unknown-editor":
                    self.overlay_dom(
                        editor,
                        """function() {
                      this.dispatchEvent(new CompositionEvent('compositionstart'));
                    }""",
                    )
                elif mode == "expired":
                    # Original display deadline, never renewed for this refusal.
                    self.page.wait_for_timeout(850)
                elif mode == "no-menu":
                    # Ordinary selected-source copying belongs to the browser.
                    self.page.keyboard.press("Control+A")
                self.page.evaluate("""() => {
                  window.menuCopyKeys=[];
                  window.addEventListener('keydown', event => {
                    if(event.code==='KeyC') menuCopyKeys.push({trusted:event.isTrusted,prevented:event.defaultPrevented});
                  });
                }""")
                self.page.keyboard.press("Meta+c" if mode == "meta" else "Control+c")
                if mode in ("control", "meta", "editor"):
                    self.assertEqual(
                        self.page.evaluate("navigator.clipboard.readText()"), before[0]
                    )
                    self.assertEqual(self.page.evaluate("menuCopyKeys"), [])
                    self.assertTrue(
                        self.overlay_dom(
                            menu, "function() { return this.isConnected && this.hidden; }"
                        )
                    )
                else:
                    self.assertEqual(
                        self.page.evaluate("menuCopyKeys"), [{"trusted": True, "prevented": False}]
                    )
                    self.assertEqual(
                        self.page.evaluate("navigator.clipboard.readText()"),
                        "Hello" if mode == "no-menu" else "clipboard sentinel",
                    )
                if mode in ("editor", "unknown-editor"):
                    if mode == "unknown-editor":
                        self.assertFalse(
                            self.overlay_dom(menu, "function() { return this.hidden; }")
                        )
                    self.assertTrue(
                        self.overlay_dom(editor, "function() { return this.matches(':focus'); }")
                    )
                    self.assertEqual(
                        self.overlay_dom(
                            editor,
                            "function() { return [this.value,this.selectionStart,this.selectionEnd]; }",
                        ),
                        before,
                    )
                else:
                    self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
                self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                self.assertEqual(self.accepted(), [])
                self.assertFalse(
                    self.page.evaluate("""fixtureMessages.some(m =>
                  m.event === 'ui' && ['escape','return'].includes(m.data.operation))""")
                )

    def test_more_text_actions_follow_current_draft_without_host_render(self):
        _, data = self.open_editor("Tab", "Esc")
        data["appearance"] = {"uiLanguage": "zh-CN", "motion": "reduce"}
        self.set_translation_menu(
            data, self.translation_menu_state(canChangeStyle=False, canInheritStyle=False)
        )
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.click_overlay_control("更多")
        copy = self.overlay_control("复制译文")
        regenerate = self.overlay_control("重新翻译")
        for control in (copy, regenerate):
            self.assertFalse(self.overlay_dom(control, "function() { return this.disabled; }"))
        self.click_overlay_control("更多")
        self.page.keyboard.press("Control+A")
        self.page.keyboard.press("Backspace")
        self.assertEqual(self.overlay_dom(editor, "function() { return this.value; }"), "")
        # No host render follows this input: opening reads the actual draft.
        self.click_overlay_control("更多")
        menu = self.overlay_control("更多操作", "group")
        for control in (copy, regenerate):
            self.assertTrue(
                self.overlay_dom(
                    control, "function() { return this.isConnected && this.disabled; }"
                )
            )
        self.click_overlay_control("重新翻译")
        self.assertFalse(self.overlay_dom(menu, "function() { return this.hidden; }"))
        self.assertTrue(self.overlay_dom(editor, "function() { return this.matches(':focus'); }"))
        # Typing while the same menu remains open must not leave stale disabled
        # controls. This is a local edit, not a new capture or display renewal.
        self.page.keyboard.insert_text("Draft after clearing")
        for control in (copy, regenerate):
            self.assertTrue(
                self.overlay_dom(
                    control, "function() { return this.isConnected && !this.disabled; }"
                )
            )
        self.assertTrue(
            self.overlay_dom(
                editor,
                "function() { return this.matches(':focus') && this.value === 'Draft after clearing'; }",
            )
        )
        self.assertFalse(
            self.page.evaluate(
                "fixtureMessages.some(message => message.event === 'ui' && message.data.operation === 'regenerate')"
            )
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_translation_menu_uses_shared_state_and_freezes_opened_settings_revision(self):
        data = self.motion_view()
        self.page.emulate_media(reduced_motion="reduce")
        # Missing/unknown payloads must not advertise an action.
        for actions in (
            None,
            self.translation_menu_state(applicationStyle="unknown"),
            self.translation_menu_state(effectiveStyle="unknown"),
        ):
            self.set_translation_menu(data, actions)
            self.click_overlay_control("更多")
            names = [
                node.get("name", {}).get("value")
                for node in self.cdp.send("Accessibility.getFullAXTree")["nodes"]
                if not node.get("ignored")
            ]
            self.assertNotIn("换种说法", names)
            self.assertNotIn("当前应用风格", names)
            self.click_overlay_control("更多")
        self.set_translation_menu(data, self.translation_menu_state(canChangeStyle=False))
        self.click_overlay_control("更多")
        inherit = self.overlay_control("✓ 继承")
        self.assertEqual(
            self.overlay_dom(inherit, "function() { return this.getAttribute('aria-pressed'); }"),
            "true",
        )
        self.set_translation_menu(
            data, self.translation_menu_state(settingsRevision=13, applicationStyle="Formal")
        )
        self.click_overlay_control("自然")
        event = self.page.evaluate("""fixtureMessages.findLast(message =>
          message.event === 'ui' && message.data.operation === 'style').data""")
        self.assertEqual(event["value"], {"settingsRevision": 12, "style": "Natural"})
        self.assertEqual(event["revision"], data["revision"])
        self.click_overlay_control("更多")
        self.click_overlay_control("换种说法")
        self.assertTrue(
            self.page.evaluate("""fixtureMessages.some(message =>
          message.event === 'ui' && message.data.operation === 'alternative')""")
        )
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_more_popup_clamps_to_viewport_and_escape_preserves_source(self):
        self.observe_overlay_lifetime()
        data = self.motion_view()
        self.page.emulate_media(reduced_motion="reduce")
        for width, height, x, y in [
            (640, 500, 8, 8),
            (640, 500, 472, 440),
            (280, 220, 8, 8),
            (280, 220, 112, 160),
        ]:
            with self.subTest(width=width, height=height, x=x, y=y):
                self.page.evaluate(
                    "phase => fixtureOverlayLifetime.phase(phase)",
                    {"width": width, "height": height, "x": x, "y": y},
                )
                self.command("overlay-hide")
                self.page.set_viewport_size({"width": width, "height": height})
                self.page.locator("#source").evaluate(
                    "(element, p) => {element.style.cssText = `position:fixed;left:${p.x}px;top:${p.y}px;width:160px;height:40px;margin:0;box-sizing:border-box`;}",
                    {"x": x, "y": y},
                )
                self.page.locator("#source").focus()
                self.page.keyboard.press("Control+A")
                snapshot = self.type_source()
                data.update(target=snapshot["target"], authority=snapshot["authority"])
                data["revision"] += 1
                self.set_translation_menu(data, self.translation_menu_state())
                host = self.page.locator("html > div[role=region][aria-label=TypeLay]")
                before = host.bounding_box()
                selection = self.page.locator("#source").evaluate(
                    "e => [e.selectionStart, e.selectionEnd]"
                )
                self.click_overlay_control("更多")
                menu = self.overlay_control("更多操作", "group")
                rect = self.overlay_dom(
                    menu,
                    """function() {
                      fixtureOverlayLifetime.watch(this.getRootNode().querySelector('textarea'));
                      return this.getBoundingClientRect().toJSON();
                    }""",
                )
                self.assertEqual(host.bounding_box(), before)
                self.assertGreaterEqual(rect["x"], 8)
                self.assertGreaterEqual(rect["y"], 8)
                self.assertLessEqual(rect["right"], width - 8)
                self.assertLessEqual(rect["bottom"], height - 8)
                self.page.keyboard.press("Escape")
                self.assertTrue(self.overlay_dom(menu, "function() { return this.hidden; }"))
                self.assertEqual(host.bounding_box(), before)
                self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
                self.assertEqual(
                    self.page.locator("#source").evaluate(
                        "e => [e.selectionStart, e.selectionEnd]"
                    ),
                    selection,
                )
                self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                self.assertEqual(self.accepted(), [])
        self.assertFalse(
            self.page.evaluate(
                "fixtureMessages.some(m => m.event === 'ui' && ['escape', 'return'].includes(m.data.operation))"
            )
        )

    def test_more_popup_escape_keeps_editor_draft_and_selection(self):
        _snapshot, data = self.open_editor("Tab", "Esc")
        self.page.emulate_media(reduced_motion="reduce")
        self.set_translation_menu(data, self.translation_menu_state(), "Tab", "Esc")
        editor = self.overlay_control("TypeLay 译文", "textbox")
        before = self.overlay_dom(
            editor, "function() { return [this.value, this.selectionStart, this.selectionEnd]; }"
        )
        self.click_overlay_control("更多")
        self.assertTrue(self.overlay_dom(editor, "function() { return this.matches(':focus'); }"))
        self.page.keyboard.press("Escape")
        self.assertTrue(self.overlay_dom(editor, "function() { return this.matches(':focus'); }"))
        self.assertEqual(
            self.overlay_dom(
                editor,
                "function() { return [this.value, this.selectionStart, this.selectionEnd]; }",
            ),
            before,
        )
        self.assertFalse(
            self.page.evaluate(
                "fixtureMessages.some(m => m.event === 'ui' && ['escape', 'return'].includes(m.data.operation))"
            )
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_more_popup_host_renewal_during_resize_anchors_to_settled_header(self):
        # The normal host keeps this positive layout scene displayed. Renew once
        # after CDP preparation, only while the original view is still alive.
        # This is not a capture, key-ticket renewal, or revival of an expired view.
        self.page.evaluate("""() => {
          window.fixtureMenuRenewals = [];
          window.fixtureMenuDisplay = null;
          const original = window.fixtureRawCommand;
          window.fixtureRawCommand = message => {
            const result = original(message);
            if (['overlay-render', 'overlay-renew'].includes(message.op)
                && fixtureMenuRenewals.length < 8) {
              fixtureMenuRenewals.push({ms: performance.now(), operation: message.op,
                expiresAtPerformanceMs: message.data.expiresAtMs - 1000000});
              fixtureMenuDisplay = JSON.stringify({target: message.data.target,
                authority: message.data.authority, revision: message.data.revision});
            }
            return result;
          };
        }""")
        data = self.motion_view()
        self.set_translation_menu(data, self.translation_menu_state())
        self.cdp.send("Animation.enable")
        self.cdp.send("Animation.setPlaybackRate", {"playbackRate": 0})
        try:
            initial_width = self.page.locator(
                "html > div[role=region][aria-label=TypeLay]"
            ).bounding_box()["width"]
            self.click_overlay_control("展开")
            editor = self.overlay_control("TypeLay 译文", "textbox")
            animation_count = self.overlay_dom(
                editor,
                """function() {
                  window.fixtureMenuEditor = this;
                  const animations = this.getRootNode().host.getAnimations();
                  for (const animation of animations)
                    animation.currentTime = 140;
                  return animations.length;
                }""",
            )
            self.assertGreater(animation_count, 0)
            intermediate_width = self.page.locator(
                "html > div[role=region][aria-label=TypeLay]"
            ).bounding_box()["width"]
            self.assertGreater(intermediate_width, initial_width)
            self.assertLess(intermediate_width, 520)
            self.click_overlay_control("更多")
            menu = self.overlay_control("更多操作", "group")
            self.assertFalse(self.overlay_dom(menu, "function() { return this.hidden; }"))
            self.sequence += 1
            renewal = {
                "id": self.sequence,
                "op": "overlay-renew",
                "data": {
                    "target": data["target"],
                    "authority": data["authority"],
                    "revision": data["revision"],
                },
            }
            renewed = self.overlay_dom(
                menu,
                """function() {
              const request = %s, host = this.getRootNode().host;
              const before = fixtureMenuRenewals.at(-1), now = performance.now();
              const sameProof = fixtureMenuDisplay === JSON.stringify(request.data);
              if (!before || !(now < before.expiresAtPerformanceMs) || !sameProof
                  || !this.isConnected || this.hidden || !host?.isConnected
                  || fixtureMenuEditor.getRootNode().host !== host
                  || !document.hasFocus() || document.activeElement.id !== 'source')
                return {eligible: false};
              const animations = host.getAnimations(), times = animations.map(a => a.currentTime);
              const count = fixtureMenuRenewals.length;
              fixtureCommand(request);
              const after = host.getAnimations();
              return {eligible: true, singleRenewal: fixtureMenuRenewals.length === count + 1
                && fixtureMenuRenewals.at(-1).operation === 'overlay-renew',
                sameView: this.isConnected && !this.hidden && host.isConnected
                  && fixtureMenuEditor.isConnected && fixtureMenuEditor.getRootNode().host === host,
                sameMotion: animations.length > 0 && animations.length === after.length
                  && animations.every((a, i) => a === after[i] && a.currentTime === times[i])};
            }"""
                % json.dumps(renewal),
            )
            self.assertEqual(
                renewed,
                {"eligible": True, "singleRenewal": True, "sameView": True, "sameMotion": True},
            )
        finally:
            self.cdp.send("Animation.setPlaybackRate", {"playbackRate": 1})
        self.observe_overlay_frames()
        frames = self.motion_frames()
        output = ROOT / "artifacts/linux/browser-native/overlay/motion"
        output.mkdir(parents=True, exist_ok=True)
        evidence = self.page.evaluate("""() => ({renewals: fixtureMenuRenewals,
          sampleStartedMs: fixtureMotionStartedMs, focus: document.hasFocus(),
          visibility: document.visibilityState,
          frames: fixtureMotionFrames.slice(0, 120).map(({text, ...frame}) => frame)})""")
        (output / "more-popup-resize-observation.json").write_text(
            json.dumps(evidence, indent=2), encoding="utf-8"
        )
        self.assertEqual(frames[-1]["host"]["width"], 520)
        geometry = self.overlay_dom(
            menu,
            """function() {
              const button = this.getRootNode().querySelector('[aria-controls="more-actions"]');
              return {menu: this.getBoundingClientRect().toJSON(),
                button: button.getBoundingClientRect().toJSON(), hidden: this.hidden};
            }""",
        )
        self.assertFalse(geometry["hidden"])
        self.assertAlmostEqual(geometry["menu"]["right"], geometry["button"]["right"] + 4, delta=1)
        viewport_height = self.page.evaluate("innerHeight")
        menu_height = geometry["menu"]["height"]
        below = geometry["button"]["bottom"] + 6
        anchored_top = (
            below
            if below + menu_height <= viewport_height - 8
            else geometry["button"]["top"] - menu_height - 6
        )
        self.assertAlmostEqual(
            geometry["menu"]["top"],
            max(8, min(viewport_height - menu_height - 8, anchored_top)),
            delta=1,
        )
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_more_popup_escape_does_not_consume_unknown_editor_composition(self):
        self.observe_overlay_lifetime()
        _snapshot, data = self.open_editor("Tab", "Esc")
        self.page.emulate_media(reduced_motion="reduce")
        self.set_translation_menu(data, self.translation_menu_state(), "Tab", "Esc")
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.overlay_dom(editor, "function() { fixtureOverlayLifetime.watch(this); }")
        self.click_overlay_control("更多")
        menu = self.overlay_control("更多操作", "group")
        before = self.overlay_dom(editor, "function() { return this.value; }")
        # An untrusted notification cannot establish trusted IME completion.
        # It revokes Clear; this is a negative boundary, not physical IME proof.
        self.overlay_dom(
            editor,
            "function() { this.dispatchEvent(new CompositionEvent('compositionstart')); }",
        )
        self.page.evaluate("""() => {
          window.popupEscapes = [];
          window.addEventListener('keydown', event => {
            if (event.key === 'Escape') popupEscapes.push(event.isTrusted);
          });
        }""")
        # An unfinished composition remains across the normal 100ms layout
        # tick. This does not renew the original 800ms display/key deadlines.
        self.page.wait_for_timeout(150)
        self.page.keyboard.press("Escape")
        self.assertEqual(self.page.evaluate("popupEscapes"), [True])
        self.assertFalse(self.overlay_dom(menu, "function() { return this.hidden; }"))
        self.assertTrue(self.overlay_dom(editor, "function() { return this.matches(':focus'); }"))
        self.assertEqual(self.overlay_dom(editor, "function() { return this.value; }"), before)
        self.assertFalse(
            self.page.evaluate(
                "fixtureMessages.some(m => m.event === 'ui' && ['escape', 'return'].includes(m.data.operation))"
            )
        )
        # Keeping a menu visible does not give an unfinished editor action
        # authority. Each real pointer click must preserve this same opening.
        for name in ("自然", "换种说法", "复制译文", "暂时隐藏"):
            self.click_overlay_control(name)
            self.assertFalse(self.overlay_dom(menu, "function() { return this.hidden; }"))
            self.assertTrue(
                self.overlay_dom(
                    editor, "function() { return this.isConnected && this.matches(':focus'); }"
                )
            )
        self.assertFalse(
            self.page.evaluate(
                "fixtureMessages.some(m => m.event === 'ui' && ['escape', 'return', 'style', 'alternative'].includes(m.data.operation))"
            )
        )
        self.assertEqual(self.overlay_dom(editor, "function() { return this.value; }"), before)
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_closed_quality_menu_keeps_future_styles_disabled_but_allows_removing_override(self):
        data = self.motion_view()
        self.page.emulate_media(reduced_motion="reduce")
        self.set_translation_menu(
            data,
            self.translation_menu_state(
                styleReleased=False, alternativeReleased=False, applicationStyle="Formal"
            ),
        )
        self.click_overlay_control("更多")
        for name in ("自然", "正式", "换种说法", "简洁 · 尚未提供", "口语 · 尚未提供"):
            self.assertTrue(
                self.overlay_dom(self.overlay_control(name), "function() { return this.disabled; }")
            )
        self.click_overlay_control("继承")
        events = self.page.evaluate(
            "fixtureMessages.filter(m => m.event === 'ui' && m.data.operation === 'style').map(m => m.data.value)"
        )
        self.assertEqual(events, [{"settingsRevision": 12, "style": None}])
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_translation_menu_disables_unavailable_actions_and_rejects_stale_open_menu(self):
        data = self.motion_view()
        self.page.emulate_media(reduced_motion="reduce")
        self.set_translation_menu(
            data,
            self.translation_menu_state(
                canChangeStyle=False,
                canInheritStyle=True,
                canRequestAlternative=False,
                styleUnavailableReason="style-unavailable",
                alternativeUnavailableReason="alternative-busy",
                feedback="没有找到不同的说法。",
            ),
        )
        self.click_overlay_control("更多")
        for name, reason in (("自然", "style-unavailable"), ("换种说法", "alternative-busy")):
            self.assertEqual(
                self.overlay_dom(
                    self.overlay_control(name), "function() { return [this.disabled, this.title]; }"
                ),
                [True, reason],
            )
        self.assertFalse(
            self.overlay_dom(self.overlay_control("✓ 继承"), "function() { return this.disabled; }")
        )
        self.click_overlay_control("更多")
        self.set_translation_menu(data, self.translation_menu_state())
        self.click_overlay_control("更多")
        retired = self.overlay_control("自然")
        retired_rect = self.overlay_dom(
            retired, "function() { return this.getBoundingClientRect().toJSON(); }"
        )
        data["revision"] += 1
        self.set_translation_menu(data, self.translation_menu_state(settingsRevision=13))
        self.assertTrue(
            self.overlay_dom(retired, "function() { return this.closest('#more-actions').hidden; }")
        )
        self.page.mouse.click(
            retired_rect["x"] + retired_rect["width"] / 2,
            retired_rect["y"] + retired_rect["height"] / 2,
        )
        self.assertFalse(
            self.page.evaluate("""fixtureMessages.some(message =>
          message.event === 'ui' && ['style', 'alternative'].includes(message.data.operation))""")
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_translation_menu_feedback_does_not_retire_existing_editor_control_ticket(self):
        _snapshot, data = self.open_editor("Ctrl+F7", "Ctrl+F8")
        self.set_translation_menu(data, self.translation_menu_state(), "Ctrl+F7", "Ctrl+F8")
        self.page.keyboard.down("Control")
        self.set_translation_menu(
            data,
            self.translation_menu_state(canChangeStyle=False, feedback="没有找到不同的说法。"),
            "Ctrl+F7",
            "Ctrl+F8",
        )
        self.page.keyboard.down("F7")
        self.page.keyboard.up("Control")
        self.page.keyboard.up("F7")
        self.assertEqual(
            self.page.evaluate("""fixtureMessages.filter(message =>
          message.event === 'ui' && message.data.operation === 'return').length"""),
            1,
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_overlay_appearance_and_language_change_preserve_candidate(self):
        data = self.motion_view()
        data["appearance"] = {
            "uiLanguage": "en",
            "font": "sans",
            "textSize": 19,
            "accent": "ocean",
            "reduceMotion": True,
            "reduceTransparency": True,
        }
        self.assertTrue(self.command("overlay-render", data)["visible"])
        editor = self.overlay_control("TypeLay Translation", "textbox")
        value = self.overlay_dom(editor, "function() { return this.value; }")
        self.assertEqual(value, data["view"]["text"])
        self.assertEqual(
            self.overlay_dom(
                editor,
                """function() {
          const section = this.closest('section');
          return [getComputedStyle(this).fontSize, section.lang, section.dataset.motion, section.dataset.material];
        }""",
            ),
            ["19px", "en", "reduce", "solid"],
        )
        self.click_overlay_control("More")
        self.overlay_control("Copy Translation")
        data["appearance"] = {
            "uiLanguage": "zh-CN",
            "font": "system",
            "textSize": 13,
            "accent": "sage",
            "reduceMotion": True,
        }
        self.assertTrue(self.command("overlay-render", data)["visible"])
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.assertEqual(self.overlay_dom(editor, "function() { return this.value; }"), value)
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_source_glossary_management_without_quality_release_preserves_candidate(self):
        self.check_source_glossary_returns_without_editing_or_mutating_candidate(False)

    def test_source_glossary_returns_without_editing_or_mutating_candidate(self):
        self.check_source_glossary_returns_without_editing_or_mutating_candidate(True)

    def check_source_glossary_returns_without_editing_or_mutating_candidate(self, released):
        data = self.motion_view()
        self.page.emulate_media(reduced_motion="reduce")
        actions = self.translation_menu_state(
            glossaryReleased=released, canOpenGlossary=True, glossaryUnavailableReason=None
        )
        self.set_translation_menu(data, actions)
        self.page.locator("#source").evaluate("element => element.setSelectionRange(1, 4)")
        editor = self.overlay_control("TypeLay 译文", "textbox")
        before = self.overlay_dom(
            editor,
            "function() { return [this.value, this.selectionStart, this.selectionEnd, this.readOnly]; }",
        )
        self.click_overlay_control("更多")
        self.click_overlay_control("加入术语表")
        self.assertTrue(
            self.page.evaluate(
                "fixtureMessages.some(message => message.event === 'ui' && message.data.operation === 'sessionGlossaryBegin')"
            )
        )
        entry = {
            "formId": str(uuid.uuid4()),
            "token": data["view"]["viewContext"]["token"],
            "settingsRevision": 12,
            "directions": [],
            "sourceOrigin": True,
        }
        envelope = {
            "target": data["target"],
            "generation": entry["token"]["commandGeneration"],
            "revision": data["revision"],
            "formId": entry["formId"],
            "token": entry["token"],
        }
        self.set_translation_menu(data, {**actions, "canOpenGlossary": False})
        self.assertTrue(
            self.command("overlay-glossary-open", {**envelope, "entry": entry})["opened"]
        )
        self.assertTrue(
            self.overlay_dom(
                self.overlay_control("源词", "textbox"),
                "function() { return this.getRootNode().activeElement === this; }",
            )
        )
        self.assertEqual(
            self.page.evaluate(
                "fixtureMessages.filter(message => message.event === 'input' && message.data.glossaryTransfer).map(message => [message.data.glossaryTransfer, message.data.focused])"
            ),
            [[{"formId": entry["formId"], "token": entry["token"], "phase": "Opening"}, False]],
        )
        description = self.command("describe", {"target": data["target"]})
        self.assertFalse(description["sessionBindings"]["sourceReady"])
        self.assertFalse(description["sessionBindings"]["editorReady"])
        self.page.keyboard.insert_text("PR")
        self.page.keyboard.press("Tab")
        self.page.keyboard.insert_text("pull request")
        self.click_overlay_control("保存")
        self.assertEqual(
            self.command("overlay-glossary-return", envelope),
            {"formId": entry["formId"], "token": entry["token"], "returned": True},
        )
        self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        self.assertEqual(
            self.page.evaluate(
                "fixtureMessages.filter(message => message.event === 'input' && message.data.glossaryTransfer).map(message => [message.data.glossaryTransfer, message.data.focused])"
            ),
            [
                [{"formId": entry["formId"], "token": entry["token"], "phase": phase}, focused]
                for phase, focused in (("Opening", False), ("Returning", True))
            ],
        )
        self.assertEqual(
            self.page.locator("#source").evaluate(
                "element => [element.value, element.selectionStart, element.selectionEnd]"
            ),
            ["Hello", 1, 4],
        )
        description = self.command("describe", {"target": data["target"]})
        self.assertFalse(description["sessionBindings"]["sourceReady"])
        self.assertFalse(description["sessionBindings"]["editorReady"])
        self.assertEqual(
            self.overlay_dom(
                editor,
                "function() { return [this.value, this.selectionStart, this.selectionEnd, this.readOnly]; }",
            ),
            before,
        )
        self.assertFalse(
            self.page.evaluate(
                "fixtureMessages.some(message => message.event === 'ui' && ['beginEdit', 'return'].includes(message.data.operation))"
            )
        )
        self.assertEqual(self.accepted(), [])

    def test_source_glossary_owned_focus_never_marks_interposed_or_synthetic_events(self):
        for interruption in ("external-roundtrip", "synthetic-overflow"):
            with self.subTest(interruption=interruption):
                data = self.motion_view()
                self.page.emulate_media(reduced_motion="reduce")
                actions = self.translation_menu_state(
                    glossaryReleased=True, canOpenGlossary=True, glossaryUnavailableReason=None
                )
                self.set_translation_menu(data, actions)
                self.click_overlay_control("更多")
                self.click_overlay_control("加入术语表")
                entry = {
                    "formId": str(uuid.uuid4()),
                    "token": data["view"]["viewContext"]["token"],
                    "settingsRevision": 12,
                    "directions": [],
                    "sourceOrigin": True,
                }
                envelope = {
                    "target": data["target"],
                    "generation": entry["token"]["commandGeneration"],
                    "revision": data["revision"],
                    "formId": entry["formId"],
                    "token": entry["token"],
                }
                self.set_translation_menu(data, {**actions, "canOpenGlossary": False})
                self.page.evaluate(
                    """kind => {
                    window.fixtureMessages.length = 0;
                    const source = document.querySelector('#source');
                    source.addEventListener('blur', () => {
                        if (kind === 'external-roundtrip') {
                            document.querySelector('#other').focus(); source.focus();
                        } else {
                            for (let index = 0; index < 12; index++) source.dispatchEvent(
                                new FocusEvent('focusout', {bubbles: true, composed: true}));
                        }
                    }, {once: true});
                }""",
                    interruption,
                )
                self.assertFalse(
                    self.command("overlay-glossary-open", {**envelope, "entry": entry})["opened"]
                )
                inputs = self.page.evaluate(
                    "fixtureMessages.filter(message => message.event === 'input').map(message => message.data)"
                )
                self.assertTrue(inputs)
                self.assertTrue(all("glossaryTransfer" not in event for event in inputs))
                if interruption == "synthetic-overflow":
                    self.assertGreaterEqual(len(inputs), 13)
                self.command("overlay-glossary-close", envelope)
                self.command("overlay-hide", {})

    def test_inline_glossary_preserves_editor_and_requires_clear_separate_auxiliary_fields(self):
        snapshot, data = self.open_editor("Ctrl+F7", "Ctrl+F8")
        self.page.emulate_media(reduced_motion="reduce")
        actions = self.translation_menu_state(
            glossaryReleased=True, canOpenGlossary=True, glossaryUnavailableReason=None
        )
        self.set_translation_menu(data, actions, "Ctrl+F7", "Ctrl+F8")
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.page.keyboard.press("Control+A")
        self.page.keyboard.insert_text("Human draft")
        self.overlay_dom(editor, "function() { this.setSelectionRange(2, 5); }")
        self.click_overlay_control("更多")
        self.click_overlay_control("加入术语表")
        entry = {
            "formId": str(uuid.uuid4()),
            "token": data["view"]["viewContext"]["token"],
            "settingsRevision": 12,
            "directions": [
                {"source": "en", "target": "zh", "sourceName": "英语", "targetName": "中文"}
            ],
        }
        # The owner's Begin render withdraws CanOpen before acknowledging this request.
        self.set_translation_menu(data, {**actions, "canOpenGlossary": False}, "Ctrl+F7", "Ctrl+F8")
        envelope = {
            "target": snapshot["target"],
            "generation": entry["token"]["commandGeneration"],
            "revision": data["revision"],
        }
        self.assertTrue(
            self.command("overlay-glossary-open", {**envelope, "entry": entry})["opened"]
        )
        description = self.command("describe", {"target": snapshot["target"]})
        self.assertFalse(description["focused"])
        self.assertFalse(description["sessionBindings"]["sourceReady"])
        self.assertFalse(description["sessionBindings"]["editorReady"])
        self.assertEqual(
            description["glossaryFocus"],
            {"formId": entry["formId"], "token": entry["token"], "ime": "Unknown"},
        )
        self.page.keyboard.insert_text("PR")
        self.page.keyboard.press("Tab")
        self.page.keyboard.press("Escape")
        self.assertFalse(
            self.page.evaluate(
                "fixtureMessages.some(message => message.event === 'ui' && message.data.operation === 'sessionGlossaryClose')"
            )
        )
        self.page.keyboard.insert_text("pull request")
        self.page.keyboard.press("Control+F7")
        self.assertFalse(
            self.page.evaluate(
                "fixtureMessages.some(message => message.event === 'ui' && message.data.operation === 'return')"
            )
        )
        self.command("overlay-renew", {**envelope, "authority": snapshot["authority"]})
        self.click_overlay_control("保存")
        request = self.page.evaluate(
            "fixtureMessages.findLast(message => message.event === 'ui' && message.data.operation === 'sessionGlossaryAdd').data.value"
        )
        self.assertEqual(
            request,
            {
                "formId": entry["formId"],
                "token": entry["token"],
                "settingsRevision": 12,
                "rule": {
                    "sourceTerm": "PR",
                    "targetTerm": "pull request",
                    "sourceLanguage": None,
                    "targetLanguage": None,
                },
            },
        )
        self.assertEqual(
            self.command("describe", {"target": snapshot["target"]})["glossaryFocus"]["ime"],
            "Clear",
        )
        self.command(
            "overlay-glossary-result",
            {
                **envelope,
                "formId": entry["formId"],
                "result": {
                    "saved": False,
                    "code": "glossary-unconfirmed",
                    "message": "保存结果尚未确认，请检查术语表",
                },
            },
        )
        # These are separate user interactions after host feedback. Keep the
        # ordinary display lease alive so a stale form cannot satisfy the
        # no-retry assertion merely by rejecting all clicks.
        self.command("overlay-renew", {**envelope, "authority": snapshot["authority"]})
        self.click_overlay_control("保存")
        self.assertEqual(
            self.page.evaluate(
                "fixtureMessages.filter(message => message.event === 'ui' && message.data.operation === 'sessionGlossaryAdd').length"
            ),
            1,
        )
        self.assertEqual(
            self.overlay_dom(
                self.overlay_control("源词", "textbox"), "function() { return this.value; }"
            ),
            "PR",
        )
        source_term = self.overlay_control("源词", "textbox")
        self.command("overlay-renew", {**envelope, "authority": snapshot["authority"]})
        self.click_overlay_control("取消")
        self.assertEqual(
            self.page.evaluate(
                "fixtureMessages.filter(message => message.event === 'ui' && message.data.operation === 'sessionGlossaryClose').map(message => message.data.value)"
            ),
            [entry["formId"]],
        )
        self.assertFalse(self.overlay_dom(source_term, "function() { return this.isConnected; }"))
        self.assertEqual(
            self.overlay_dom(
                editor,
                "function() { return [this.isConnected, this.value, this.selectionStart, this.selectionEnd, this.matches(':focus')]; }",
            ),
            [True, "Human draft", 2, 5, True],
        )
        self.page.keyboard.press("Control+Z")
        self.assertEqual(
            self.overlay_dom(editor, "function() { return this.value; }"), "Initial candidate"
        )
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

    def test_overlay_language_title_uses_host_labels_without_advertising_extra_browser_choices(
        self,
    ):
        data = self.motion_view()
        for code, label in (
            ("ja", "日本語"),
            ("ko", "한국어"),
            ("fr", "Français"),
            ("es", "Español"),
            ("de", "Deutsch"),
            ("pt", "Português"),
        ):
            with self.subTest(code=code):
                data["revision"] += 1
                data["view"].update(
                    sourceLanguage="zh",
                    targetLanguage=code,
                    sourceLanguageLabel="中文",
                    targetLanguageLabel=label,
                )
                self.assertTrue(self.command("overlay-render", data)["visible"])
                self.overlay_control(f"中文 → {label}")
                self.assertEqual(self.page.evaluate("document.activeElement.id"), "source")
        data["revision"] += 1
        data["view"].update(targetLanguage="unknown", targetLanguageLabel="")
        self.assertTrue(self.command("overlay-render", data)["visible"])
        self.click_overlay_control("中文 → unknown（当前不可用）")
        self.assertTrue(
            self.page.evaluate("""fixtureMessages.some(message =>
          message.event === 'ui' && message.data.operation === 'languagesPicker')""")
        )
        self.assertFalse(
            self.page.evaluate("""fixtureMessages.some(message =>
          message.event === 'ui' && message.data.operation === 'languages')""")
        )

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
            before = self.page.locator("html > div[role=region][aria-label=TypeLay]").bounding_box()
            self.observe_overlay_frames(start_on_click=True)
            self.click_overlay_control("展开")
            frames = self.motion_frames()
        finally:
            self.cdp.send("Page.stopScreencast")
            self.cdp.remove_listener("Page.screencastFrame", save_frame)
        observation = self.page.evaluate(
            "({sampleStartedMs: fixtureMotionStartedMs, click: fixtureMotionClick})"
        )
        (output / "resize.json").write_text(
            json.dumps({**observation, "frames": frames, "screenshots": screenshots}, indent=2)
        )
        self.assertIsNotNone(observation["click"])
        for name in ("trusted", "sameControl", "connected"):
            self.assertIs(observation["click"][name], True)
        self.assertLessEqual(observation["click"]["ms"], observation["sampleStartedMs"])
        end = frames[-1]["host"]
        self.assertEqual(end["width"], 520)
        self.assertGreater(end["height"], before["height"])
        self.assertGreater(len({round(frame["host"]["height"], 1) for frame in frames}), 3)
        self.assertGreaterEqual(len(screenshots), 3)
        for frame in frames:
            self.assertTrue(frame["sourceFocused"])
            self.assertAlmostEqual(frame["host"]["right"], end["right"], delta=1)
            self.assertAlmostEqual(frame["host"]["bottom"], end["bottom"], delta=1)
            self.assertGreaterEqual(frame["bar"]["top"], frame["host"]["top"])
            self.assertLessEqual(frame["bar"]["bottom"], frame["host"]["bottom"])
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
        before = self.page.locator("html > div[role=region][aria-label=TypeLay]").bounding_box()
        # Freeze the browser animation timeline, not adapter state. Otherwise
        # the header can move between CDP's rectangle read and mouse dispatch,
        # so a missed collapse click would test an uninterrupted expansion.
        self.cdp.send("Animation.enable")
        self.cdp.send("Animation.setPlaybackRate", {"playbackRate": 0})
        try:
            self.click_overlay_control("展开")
            editor = self.overlay_control("TypeLay 译文", "textbox")
            self.overlay_dom(
                editor,
                """function() {
              for (const animation of this.getRootNode().host.getAnimations())
                animation.currentTime = 140;
            }""",
            )
            intermediate = self.page.locator(
                "html > div[role=region][aria-label=TypeLay]"
            ).bounding_box()
            self.assertGreater(intermediate["width"], before["width"])
            self.assertLess(intermediate["width"], 520)
            self.command(
                "overlay-renew", {"target": data["target"], "authority": data["authority"]}
            )
            self.click_overlay_control("缩小")
            self.assertTrue(self.overlay_control("展开"))
            self.observe_overlay_frames()
        finally:
            self.cdp.send("Animation.setPlaybackRate", {"playbackRate": 1})
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
        self.assertEqual(
            self.page.locator("html > div[role=region][aria-label=TypeLay]").count(), 0
        )
        self.page.locator("#source").focus()
        data = self.motion_view()
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.click_overlay_control("展开")
        self.page.locator("#source").evaluate("element => element.remove()")
        self.page.wait_for_timeout(360)
        self.assertEqual(
            self.page.locator("html > div[role=region][aria-label=TypeLay]").count(), 0
        )
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

    def test_host_renewed_motion_modes_override_os_and_legacy_boolean(self):
        # Long animation samples are separate preference scenes, not one
        # synthetic host session without its normal fresh source observations.
        # Only full/reduce long-frame positives receive one normal host display
        # renewal after setup. Never revive a failed scene or renew a key ticket;
        # the 1500/800ms clocks and 360ms sample window remain unchanged.
        observations = []
        output = Path(
            os.environ.get(
                "TYPELAY_BROWSER_LIFETIME_OUTPUT",
                ROOT / "artifacts/linux/browser-native/overlay/lifetime",
            )
        )
        output.mkdir(parents=True, exist_ok=True)
        for mode in ("full", "reduce", "system"):
            with self.subTest(mode=mode):
                self.page.reload()
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
                self.page.evaluate("""() => {
                  const rows = [], media = matchMedia('(prefers-reduced-motion: reduce)');
                  let editor = null, panel = null, expiry = null, omitted = 0, viewProof = null, sameView = true;
                  let displayProof = null, renewals = 0;
                  const log = kind => {
                    if (rows.length < 48) rows.push({kind, ms: performance.now(),
                      matches: media.matches, connected: editor?.isConnected ?? null,
                      motion: editor?.parentElement?.dataset.motion ?? null, expiry, sameView, wall: Date.now(),
                      documentFocused: document.hasFocus(), sourceFocused: document.activeElement?.id === 'source'});
                    else omitted++;
                  };
                  const observer = new MutationObserver(() => log('panel-change'));
                  const changed = () => log('media-change');
                  media.addEventListener('change', changed);
                  const raw = fixtureRawCommand;
                  window.fixtureRawCommand = message => {
                    const result = raw(message);
                    if (message.op === 'overlay-render') {
                      expiry = message.data.expiresAtMs - 1000000;
                      const proof = JSON.stringify([message.data.target, message.data.generation,
                        message.data.revision, message.data.view.viewContext?.token]);
                      sameView = viewProof === null || viewProof === proof; viewProof ??= proof;
                      displayProof = JSON.stringify([message.data.target, message.data.authority,
                        message.data.revision, message.data.generation]);
                      log('render');
                    }
                    if (message.op === 'overlay-renew') {
                      expiry = message.data.expiresAtMs - 1000000;
                      log('renew');
                    }
                    if (message.op === 'capture') log('capture-return');
                    return result;
                  };
                  window.fixtureMotionAudit = {
                    read(value) {
                      if (!editor) {
                        editor = value; panel = value.parentElement;
                        observer.observe(panel, {attributes: true, attributeFilter: ['data-motion']});
                      }
                      if (!sameView || value !== editor || !editor.isConnected || editor.parentElement !== panel)
                        throw new Error('motion scene changed its original editor/view');
                      log('assert-read');
                      return value.parentElement.dataset.motion;
                    },
                    renew(value, request, selection) {
                      request.data.generation ??= fixtureBindings.generation;
                      const proof = JSON.stringify([request.data.target, request.data.authority,
                        request.data.revision, request.data.generation]);
                      const host = panel?.getRootNode().host, now = performance.now();
                      const trigger = window.fixtureMotionControl;
                      if (renewals || !sameView || value !== editor || !editor?.isConnected
                          || editor.parentElement !== panel || !host?.isConnected
                          || !trigger?.isConnected || trigger.getRootNode().host !== host
                          || !(now < expiry) || proof !== displayProof || !document.hasFocus()
                          || document.activeElement?.id !== 'source' || !editor.readOnly
                          || editor.selectionStart !== selection[0] || editor.selectionEnd !== selection[1])
                        return {eligible: false};
                      const animations = host.getAnimations(), times = animations.map(a => a.currentTime);
                      const previousExpiry = expiry;
                      renewals++;
                      fixtureCommand(request);
                      const after = host.getAnimations();
                      return {eligible: true, singleRenewal: renewals === 1,
                        sameView: editor.isConnected && editor.parentElement === panel && host.isConnected
                          && trigger.isConnected && trigger.getRootNode().host === host,
                        sameSelection: editor.selectionStart === selection[0] && editor.selectionEnd === selection[1],
                        sameMotion: animations.length === after.length
                          && animations.every((a, i) => a === after[i] && a.currentTime === times[i]),
                        displayed: performance.now() < expiry && expiry > previousExpiry,
                        sourceFocused: document.hasFocus() && document.activeElement?.id === 'source'};
                    },
                    ready() {
                      return editor?.isConnected && editor.parentElement === panel && panel.dataset.motion === 'reduce'
                        && Number.isFinite(expiry) && performance.now() < expiry;
                    },
                    finish() {
                      log('final'); observer.disconnect(); media.removeEventListener('change', changed);
                      return {rows, omitted};
                    }
                  };
                }""")
                frames = []
                try:
                    data = self.motion_view()
                    if mode == "full":
                        self.page.emulate_media(reduced_motion="reduce")
                    data["appearance"] = {
                        "uiLanguage": "zh-CN",
                        "motion": mode,
                        "reduceMotion": mode != "reduce",
                    }
                    self.assertTrue(self.command("overlay-render", data)["visible"])
                    editor = self.overlay_control("TypeLay 译文", "textbox")

                    def actual_mode():
                        return self.overlay_dom(
                            editor, "function() { return fixtureMotionAudit.read(this); }"
                        )

                    self.assertEqual(actual_mode(), "reduce" if mode == "reduce" else "full")
                    selection = self.overlay_dom(
                        editor,
                        "function() { return [this.selectionStart, this.selectionEnd]; }",
                    )
                    if mode in ("full", "reduce"):
                        if mode == "reduce":
                            self.click_overlay_control("展开")
                        label = "展开" if mode == "full" else "缩小"
                        self.observe_overlay_frames(start_on_click=True, click_label=label)
                        # Resolve the original single-click point before renewal,
                        # not by looking for a replacement after an expired view.
                        control = self.overlay_control(label)
                        rect = self.overlay_dom(
                            control, "function() { return this.getBoundingClientRect().toJSON(); }"
                        )
                        self.sequence += 1
                        renewal = {
                            "id": self.sequence,
                            "op": "overlay-renew",
                            "data": {
                                "target": data["target"],
                                "authority": data["authority"],
                                "revision": data["revision"],
                            },
                        }
                        self.assertEqual(
                            self.overlay_dom(
                                editor,
                                "function() { return fixtureMotionAudit.renew(this, %s, %s); }"
                                % (json.dumps(renewal), json.dumps(selection)),
                            ),
                            dict(
                                eligible=True,
                                singleRenewal=True,
                                sameView=True,
                                sameSelection=True,
                                sameMotion=True,
                                displayed=True,
                                sourceFocused=True,
                            ),
                        )
                        self.page.mouse.click(
                            rect["x"] + rect["width"] / 2, rect["y"] + rect["height"] / 2
                        )
                        frames = self.motion_frames()
                        if mode == "full":
                            self.assertGreater(
                                len({frame["host"]["height"] for frame in frames}), 2
                            )
                            self.page.emulate_media(reduced_motion="no-preference")
                            self.assertEqual(actual_mode(), "full")
                        else:
                            self.assertEqual(len({frame["host"]["height"] for frame in frames}), 1)
                    else:
                        # Preserve immediate three-mode changes on this same
                        # live editor/token, without three long frame samples.
                        for changed_mode in ("full", "reduce", "system"):
                            data["appearance"] = {
                                "uiLanguage": "zh-CN",
                                "motion": changed_mode,
                                "reduceMotion": changed_mode != "reduce",
                            }
                            self.assertTrue(self.command("overlay-render", data)["visible"])
                            self.assertEqual(
                                actual_mode(), "reduce" if changed_mode == "reduce" else "full"
                            )
                            self.assertEqual(
                                self.overlay_dom(
                                    editor,
                                    "function() { return [this.selectionStart, this.selectionEnd]; }",
                                ),
                                selection,
                            )
                            self.assertEqual(self.accepted(), [])
                        self.page.emulate_media(reduced_motion="reduce")
                        # The new query can match before the cached query's
                        # change event. Keep the original live-panel budget.
                        self.page.wait_for_function(
                            "matchMedia('(prefers-reduced-motion: reduce)').matches && fixtureMotionAudit.ready()"
                        )
                        self.assertEqual(actual_mode(), "reduce")
                    self.assertEqual(
                        self.overlay_dom(
                            editor,
                            "function() { return [this.readOnly, this.selectionStart, this.selectionEnd]; }",
                        ),
                        [True, *selection],
                    )
                    self.assertEqual(
                        self.page.locator("#source").evaluate(
                            "element => [element.value, element.selectionStart, element.selectionEnd, document.activeElement === element]"
                        ),
                        ["Hello", 5, 5, True],
                    )
                    self.assertEqual(self.accepted(), [])
                finally:
                    observations.append(
                        {
                            "mode": mode,
                            "frames": frames,
                            **self.page.evaluate("fixtureMotionAudit.finish()"),
                        }
                    )
                    (output / (self._testMethodName + ".json")).write_text(
                        json.dumps(observations, indent=2), encoding="utf-8"
                    )

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
        # The host fixture issues 90ms, but the existing 4ms conservative clock
        # bound makes the local deadline 86ms and presentation handoff 61ms.
        self.page.evaluate("fixtureAdvanceHintClock(60)")
        self.assertEqual(
            self.page.evaluate("fixtureReadHint()"), {"text": "Tab 接受译文", "changes": 0}
        )
        self.page.evaluate("fixtureAdvanceHintClock(1)")
        self.assertEqual(self.page.evaluate("fixtureReadHint()"), {"text": "", "changes": 1})
        self.assertTrue(self.command("describe")["publication"]["ready"])
        self.page.evaluate("fixtureAdvanceHintClock(24)")
        self.assertTrue(self.command("describe")["publication"]["ready"])
        self.assertEqual(self.page.evaluate("fixtureReadHint()"), {"text": "", "changes": 0})
        self.page.evaluate("fixtureAdvanceHintClock(1)")
        self.assertEqual(
            self.page.evaluate("fixtureReadHint()"), {"text": "Tab 交还应用", "changes": 1}
        )
        self.assertFalse(self.command("describe")["publication"]["ready"])
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

    def test_retranslation_confirmation_escape_keeps_the_live_editor_draft(self):
        for phase in ("clear", "unknown-editor", "new-view", "changed-draft"):
            with self.subTest(phase=phase):
                self.page.reload()
                self.page.locator("#source").focus()
                _, data = self.open_editor("Tab", "Esc")
                data["appearance"] = {"uiLanguage": "zh-CN", "motion": "reduce"}
                self.assertTrue(self.command("overlay-render", data)["visible"])
                self.page.keyboard.press("Control+A")
                self.page.keyboard.insert_text("My keyboard-confirmed draft")
                self.assertTrue(self.command("overlay-render", data)["visible"])
                editor = self.overlay_control("TypeLay 译文", "textbox")
                self.click_overlay_control("更多")
                self.click_overlay_control("重新翻译")
                question = self.overlay_control("替换你修改过的译文？", "group")
                self.assertTrue(
                    self.overlay_dom(question, "function() { return this.isConnected; }")
                )
                self.overlay_dom(
                    editor,
                    """function() {
                  window.confirmationKeys=[];
                  this.addEventListener('keydown', event=>{
                    if(event.key==='Escape') confirmationKeys.push({trusted:event.isTrusted,prevented:event.defaultPrevented});
                  });
                }""",
                )
                if phase == "unknown-editor":
                    # Synthetic IME observation revokes Clear; it cannot prove
                    # a real input-method session or manufacture a trusted end.
                    self.overlay_dom(
                        editor,
                        """function() {
                      this.dispatchEvent(new CompositionEvent('compositionstart'));
                    }""",
                    )
                elif phase == "new-view":
                    data["revision"] += 1
                    self.assertTrue(self.command("overlay-render", data)["visible"])
                    self.assertFalse(
                        self.overlay_dom(question, "function() {return this.isConnected;}")
                    )
                elif phase == "changed-draft":
                    self.page.keyboard.insert_text("!")
                    self.assertFalse(
                        self.overlay_dom(question, "function() {return this.isConnected;}")
                    )
                before = self.overlay_dom(
                    editor,
                    """function() {
                  return [this.value,this.selectionStart,this.selectionEnd];
                }""",
                )
                self.page.keyboard.press("Escape")
                self.assertEqual(
                    self.overlay_dom(question, "function() { return this.isConnected; }"),
                    phase == "unknown-editor",
                )
                self.assertEqual(
                    self.page.evaluate("""fixtureMessages.filter(m =>
                    m.event === 'ui' && ['escape','return','regenerate'].includes(m.data.operation))
                    .map(m=>m.data.operation)"""),
                    ["escape"] if phase in ("new-view", "changed-draft") else [],
                )
                # A new Escape after retirement belongs to the ordinary editor;
                # the old confirmation must not permanently swallow that key.
                self.assertEqual(
                    self.page.evaluate("confirmationKeys"),
                    [{"trusted": True, "prevented": False}] if phase == "unknown-editor" else [],
                )
                self.assertEqual(
                    self.overlay_dom(
                        editor,
                        """function() {
                  return [this.isConnected,this.value,this.selectionStart,this.selectionEnd,this.readOnly,this.matches(':focus')];
                }""",
                    ),
                    [True, *before, False, True],
                )
                self.assertEqual(self.page.locator("#source").input_value(), "Hello")
                self.assertEqual(self.accepted(), [])

    def test_retranslation_confirms_human_draft_and_rejects_superseded_confirmation(self):
        snapshot, data = self.open_editor("Tab", "Esc")
        # This contract isolates confirmation authority; motion interruption has
        # separate real-pointer coverage. Keep disclosure coordinates stable.
        data["appearance"] = {"uiLanguage": "zh-CN", "reduceMotion": True}
        self.assertTrue(self.command("overlay-render", data)["visible"])
        self.page.keyboard.press("Control+A")
        self.page.keyboard.insert_text("My edited translation")
        self.assertTrue(self.command("overlay-render", data)["visible"])
        self.click_overlay_control("更多")
        self.click_overlay_control("重新翻译")
        self.overlay_control("替换你修改过的译文？", "StaticText")

        def count():
            return self.page.evaluate(
                "fixtureMessages.filter(m => m.event === 'ui' && m.data.operation === 'regenerate').length"
            )

        self.assertEqual(count(), 0)
        self.click_overlay_control("保留")
        self.assertEqual(count(), 0)
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.assertEqual(
            self.overlay_dom(editor, "function() { return this.value; }"), "My edited translation"
        )
        self.assertTrue(self.command("overlay-render", data)["visible"])
        self.click_overlay_control("更多")
        self.click_overlay_control("重新翻译")
        data["revision"] += 1
        self.assertTrue(self.command("overlay-render", data)["visible"])
        names = [
            node.get("name", {}).get("value")
            for node in self.cdp.send("Accessibility.getFullAXTree")["nodes"]
        ]
        self.assertNotIn("替换你修改过的译文？", names)
        self.assertEqual(count(), 0)
        self.assertTrue(self.command("overlay-render", data)["visible"])
        self.click_overlay_control("更多")
        self.click_overlay_control("重新翻译")
        self.click_overlay_control("替换")
        self.assertEqual(count(), 1)
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
        box = self.page.locator("html > div[role=region][aria-label=TypeLay]").bounding_box()
        self.assertIsNotNone(box)
        editor_rect = self.overlay_dom(
            self.overlay_control("TypeLay 译文", "textbox"),
            "function() { return this.getBoundingClientRect().toJSON(); }",
        )
        self.page.mouse.click(editor_rect["x"] + 12, editor_rect["y"] + editor_rect["height"] / 2)
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
            "rgb(20, 20, 19)",
        )
        if accept == "Tab":
            # Reuse the existing two-stage return case for motion, Chinese
            # selection and Chromium's actual textarea undo/redo stack.
            self.page.keyboard.press("Shift+ArrowLeft")
            selection = self.overlay_dom(
                editor, "function() { return [this.selectionStart, this.selectionEnd]; }"
            )
            # Keep the original motion assertion, but distinguish a late click
            # from a real jump/cancellation without logging the draft text.
            self.overlay_dom(
                editor,
                """function() {
                  const panel = this.parentElement, host = panel.getRootNode().host;
                  const snapshot = () => ({ms: performance.now(),
                    host: host.getBoundingClientRect().toJSON(),
                    reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches});
                  window.fixtureDraftResize = {beforeObservation: snapshot()};
                  panel.addEventListener('click', event => {
                    fixtureDraftResize.trustedClick = event.isTrusted;
                    fixtureDraftResize.beforeClick = snapshot();
                  }, {capture: true, once: true});
                  panel.addEventListener('click', () => {
                    fixtureDraftResize.afterClick = snapshot();
                  }, {once: true});
                }""",
            )
            self.observe_overlay_frames(start_on_click=True)
            self.click_overlay_control("展开")
            try:
                frames = self.motion_frames()
            except Exception as error:
                # A failed wait used to lose the click/start/partial-frame
                # evidence. Save it without retrying input or hiding the error.
                try:
                    diagnostics = self.page.evaluate(
                        """draft => {
                          const frames = window.fixtureMotionFrames;
                          const recorded = Array.isArray(frames) ? frames : [];
                          const bounded = recorded.length <= 120 ? recorded
                            : [...recorded.slice(0, 119), recorded.at(-1)];
                          return {...window.fixtureDraftResize,
                            failure: 'motion-frames-wait',
                            done: window.fixtureMotionDone ?? null,
                            click: window.fixtureMotionClick ?? null,
                            sampleStartedMs: window.fixtureMotionStartedMs ?? null,
                            frameCount: Array.isArray(frames) ? frames.length : null,
                            atFailure: window.fixtureMotionInspection?.() ?? null,
                            frames: bounded.map(frame => ({ms: frame.ms,
                              connected: frame.connected, host: frame.host,
                              editor: frame.editor, exit: frame.exit,
                              start: frame.start, end: frame.end,
                              sourceFocused: frame.sourceFocused,
                              draftPreserved: frame.text === draft}))};
                        }""",
                        human_draft,
                    )
                    output = ROOT / "artifacts/linux/browser-native/overlay/motion"
                    output.mkdir(parents=True, exist_ok=True)
                    (output / "editor-draft-resize.json").write_text(
                        json.dumps(diagnostics, indent=2), encoding="utf-8"
                    )
                except Exception as diagnostic_error:
                    error.add_note(
                        "Editor motion diagnostics unavailable: " + type(diagnostic_error).__name__
                    )
                raise
            diagnostics = self.page.evaluate("fixtureDraftResize")
            diagnostics["sampleStartedMs"] = self.page.evaluate("fixtureMotionStartedMs")
            diagnostics["frameCount"] = len(frames)
            # The observer is time-bounded; cap serialized samples as well,
            # retaining the endpoint even on a high-refresh-rate display.
            diagnostic_frames = frames if len(frames) <= 120 else frames[:119] + frames[-1:]
            diagnostics["frames"] = [
                {
                    **{key: value for key, value in frame.items() if key != "text"},
                    "draftPreserved": frame["text"] == human_draft,
                }
                for frame in diagnostic_frames
            ]
            output = ROOT / "artifacts/linux/browser-native/overlay/motion"
            output.mkdir(parents=True, exist_ok=True)
            (output / "editor-draft-resize.json").write_text(
                json.dumps(diagnostics, indent=2), encoding="utf-8"
            )
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
            [draft, "rgb(20, 20, 19)"],
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
        self.observe_overlay_lifetime()
        snapshot, data = self.open_editor("Ctrl+F7", "Ctrl+F8", dispatch="native")
        editor = self.overlay_control("TypeLay 译文", "textbox")
        self.overlay_dom(editor, "function() { fixtureOverlayLifetime.watch(this); }")
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
        self.assertEqual(
            self.page.locator("html > div[role=region][aria-label=TypeLay]").count(), 1
        )
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
        self.assertEqual(
            self.page.locator("html > div[role=region][aria-label=TypeLay]").count(), 0
        )

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

    def test_accept_hint_hands_off_before_permission_and_recovers_on_new_publication(self):
        self.motion_view()

        def names():
            return [
                node.get("name", {}).get("value")
                for node in self.cdp.send("Accessibility.getFullAXTree")["nodes"]
            ]

        def publish_current():
            snapshot = self.capture()
            self.assertIsNotNone(snapshot, self.capture_diagnostics)
            self.publish(snapshot, self.plan(snapshot))

        self.assertIn("Tab 交还应用", names())
        self.assertNotIn("Tab 接受译文", names())
        publish_current()
        self.assertIn("Tab 接受译文", names())
        # 90ms host permit minus its existing 4ms clock margin: local expiry
        # is 86ms, not 90ms. The cue hands off 25ms before that local expiry.
        self.page.evaluate("fixtureAdvanceHintClock(60)")
        self.assertIn("Tab 接受译文", names())
        self.page.evaluate("fixtureAdvanceHintClock(1)")
        self.assertNotIn("Tab 交还应用", names())
        self.assertNotIn("Tab 接受译文", names())
        # Presentation withdrawal does not revoke the original permission.
        self.assertTrue(self.command("describe")["publication"]["ready"])
        self.page.evaluate("fixtureAdvanceHintClock(24)")
        self.assertTrue(self.command("describe")["publication"]["ready"])
        self.assertNotIn("Tab 交还应用", names())
        self.assertNotIn("Tab 接受译文", names())
        self.page.evaluate("fixtureAdvanceHintClock(1)")
        self.assertIn("Tab 交还应用", names())
        self.assertNotIn("Tab 接受译文", names())
        self.assertFalse(self.command("describe")["publication"]["ready"])
        publish_current()
        self.assertIn("Tab 接受译文", names())
        self.page.evaluate("fixtureAdvanceHintClock(61)")
        self.assertNotIn("Tab 接受译文", names())
        self.assertNotIn("Tab 交还应用", names())
        publish_current()
        self.assertIn("Tab 接受译文", names())
        # Cross the previous publication's deadline: its timer cannot remove
        # a successor's valid cue or resurrect the old pass-through hint.
        self.page.evaluate("fixtureAdvanceHintClock(25)")
        self.assertIn("Tab 接受译文", names())
        self.assertNotIn("Tab 交还应用", names())
        self.page.locator("#other").focus()
        self.page.locator("#source").focus()
        self.assertNotIn("Tab 接受译文", names())
        self.page.evaluate("fixtureAdvanceHintClock(100)")
        self.assertNotIn("Tab 接受译文", names())
        self.assertEqual(self.page.locator("#source").input_value(), "Hello")
        self.assertEqual(self.accepted(), [])

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
