/* Top-level standard fields and source-owned single-paragraph rich editors.
 * Approval and final mutation guards stay in this isolated document event loop. */
(() => {
  "use strict";
  const api = globalThis.browser || chrome;
  const rich = globalThis.TypeLayEditorBridge;
  const port = api.runtime.connect({name: "typelay-document"});
  const random = crypto.getRandomValues(new Uint8Array(16));
  random[6] = (random[6] & 15) | 64;
  random[8] = (random[8] & 63) | 128;
  const hex = Array.from(random, value => value.toString(16).padStart(2, "0")).join("");
  const documentId = `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  const fields = new WeakMap();
  const targets = new Map();
  const consumed = new Map();
  const held = new Map();
  const MAX_TEXT = 8192;
  let counter = 0, publication = null, shortcut = null, configuration = null;
  let clock = null, commandClock = null, lastHostStamp = NaN, alive = true, overlay = null, mutating = false, ownInputEvent = null;
  // Cache native methods in the extension's isolated world, never invoke a
  // page-overridden setter or a page-supplied replacement function.
  const inputReplace = HTMLInputElement.prototype.setRangeText;
  const areaReplace = HTMLTextAreaElement.prototype.setRangeText;
  const inputValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").get;
  const areaValue = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").get;
  const send = (event, data) => { if (alive) port.postMessage({event, data}); };
  const ui = (operation, value) => {
    if (overlay) send("ui", {target: overlay.field.id, operation, value});
  };
  const monoNow = performance.now.bind(performance);
  // Firefox StaticPrefList.yaml currently uses 1000us plus bounded jitter;
  // Chromium non-isolated performance.now() is quantized at 100us (MDN).
  // Require an observed <=1ms clock tick, including on each calibration. Four
  // milliseconds conservatively cover two local 1ms errors + host floor(1ms).
  // Coarse privacy timers are unsupported, not assigned a longer permit.
  // Sources: mozilla-firefox/firefox modules/libpref/init/StaticPrefList.yaml
  // and toolkit/components/resistfingerprinting/nsRFPService.cpp;
  // https://developer.mozilla.org/en-US/docs/Web/API/Performance/now
  const CLOCK_MARGIN = 4;
  function fineClock() {
    const first = monoNow();
    for (let index = 0; index < 32768; index++) {
      const delta = monoNow() - first;
      if (delta > 0) return delta <= 1;
    }
    return false;
  }
  function clockValid() {
    if (!clock) return false;
    const elapsed = monoNow() - clock.mono;
    if (elapsed < 0 || Math.abs(Date.now() - clock.wall - elapsed) > 20) {
      observationInterrupted();
      return false;
    }
    if (elapsed > 1500) { clock = publication = shortcut = null; return false; }
    return true;
  }
  function now(upper = false) {
    return clockValid() ? (upper ? clock.upper + monoNow() : Math.floor(clock.lower + monoNow())) : NaN;
  }
  function issued(field, basis) {
    const record = field?.authorities?.get(basis?.token);
    if (!record || record.key !== JSON.stringify(basis)) return null;
    const elapsed = monoNow() - record.mono;
    if (elapsed < 0 || elapsed + CLOCK_MARGIN > 150
      || Math.abs(Date.now() - record.wall - elapsed) > 20) return null;
    return record;
  }
  function calibrate(field, basis) {
    if (clock && !clockValid()) return false;
    const record = issued(field, basis);
    if (!record || !commandClock || commandClock.boottimeMs < basis.observedAtMs
      || commandClock.boottimeMs - basis.observedAtMs > 150 || !fineClock()
      || !issued(field, basis)) return false;
    const mono = monoNow(), wall = Date.now(), host = commandClock.boottimeMs;
    // The echoed, locally issued token proves the host sampled H after t0 and
    // before this receipt t1. No wall-clock subtraction grants freshness.
    let lower = host - mono - CLOCK_MARGIN, upper = host - record.mono + CLOCK_MARGIN;
    if (clock) {
      if (!clockValid()) return false;
      lower = Math.max(lower, clock.lower);
      upper = Math.min(upper, clock.upper);
      if (lower > upper) { observationInterrupted(); return false; }
    }
    // Keep a separate wall bound solely to veto a deadline after short Linux
    // suspend intervals that performance.now() does not include.
    clock = {lower, upper, upperWall: host - record.wall + CLOCK_MARGIN, mono, wall};
    return true;
  }
  function deadline(expires, duration) {
    if (!clockValid() || !commandClock || !Number.isFinite(expires)
      || expires < commandClock.boottimeMs || expires - commandClock.boottimeMs > duration) return NaN;
    return expires - clock.upper;
  }
  function liveDeadline(request) {
    return clockValid() && Number.isFinite(request.expiresAtPerformanceMs)
      && Number.isFinite(request.expiresAtWallMs)
      && monoNow() <= request.expiresAtPerformanceMs && Date.now() <= request.expiresAtWallMs;
  }
  const fresh = (stamp, age) => Number.isFinite(stamp) && stamp <= lastHostStamp
    && Number.isFinite(now(true)) && now(true) >= stamp && now(true) - stamp <= age;
  function focused(field) {
    return document.visibilityState === "visible" && document.hasFocus()
      && document.activeElement === field.element && field.element.isConnected;
  }
  function allowedElement(element) {
    return element instanceof HTMLTextAreaElement
      || element instanceof HTMLInputElement && ["text", "search"].includes(element.type);
  }
  function fieldFor(element) {
    let richText = false;
    if (!allowedElement(element)) {
      element = rich?.root(element);
      if (!element) return null;
      // Observe lifecycle/input even while the synchronous page bridge is busy.
      // A nested probe is deliberately unavailable and cannot erase a veto.
      const existing = fields.get(element);
      if (existing && !existing.dead) return existing;
      if (rich.probe(element)?.supported !== true) return null;
      richText = true;
    }
    if (element.getRootNode() !== document) return null;
    let field = fields.get(element);
    if (!field || field.dead) {
      field = {element, richText, id: `${documentId}:${++counter}`, content: 0, input: 0, focus: 0,
        ime: "Unknown", quarantined: false, dead: false,
        selectionStart: element.selectionStart, selectionEnd: element.selectionEnd};
      fields.set(element, field);
      targets.set(field.id, field);
      rememberSelection(field);
    }
    return field;
  }
  function geometry(field) {
    const element = field.element;
    const rootStyle = document.documentElement && getComputedStyle(document.documentElement);
    // A transformed root changes fixed-position containing blocks. Do not guess
    // how viewport coordinates map into that page's nonstandard layout.
    if (!rootStyle || rootStyle.transform !== "none" || rootStyle.perspective !== "none"
      || rootStyle.filter !== "none" || (rootStyle.zoom && !["1", "normal"].includes(rootStyle.zoom))) return null;
    if (!element.isConnected || !element.getClientRects().length) return null;
    const style = getComputedStyle(element);
    if (style.visibility !== "visible" || style.display === "none" || Number(style.opacity) === 0) return null;
    const rect = element.getBoundingClientRect();
    let x = Math.max(0, rect.left), y = Math.max(0, rect.top);
    let right = Math.min(innerWidth, rect.right), bottom = Math.min(innerHeight, rect.bottom);
    for (let parent = element.parentElement; parent; parent = parent.parentElement) {
      const css = getComputedStyle(parent), clip = parent.getBoundingClientRect();
      if (css.visibility !== "visible" || css.display === "none" || Number(css.opacity) === 0) return null;
      if (/(hidden|clip|scroll|auto)/.test(css.overflowX)) {
        x = Math.max(x, clip.left); right = Math.min(right, clip.right);
      }
      if (/(hidden|clip|scroll|auto)/.test(css.overflowY)) {
        y = Math.max(y, clip.top); bottom = Math.min(bottom, clip.bottom);
      }
    }
    if (right <= x || bottom <= y || !Number.isFinite(devicePixelRatio) || devicePixelRatio <= 0) return null;
    return {space: "browser-viewport", scale: devicePixelRatio, frameWidth: innerWidth,
      frameHeight: innerHeight, bounds: {x, y, width: right - x, height: bottom - y}};
  }
  function metadata(field) {
    const element = field.element;
    if (field.dead || !(field.richText ? rich?.root(element) === element
      && (mutating || rich.probe(element)?.supported === true) : allowedElement(element))
      || element.getRootNode() !== document || !element.isConnected) return null;
    let ancestors = "";
    let parent = element.parentElement;
    for (let depth = 0; parent && depth < 8; depth++, parent = parent.parentElement) {
      // Include editor-container capabilities, not broad application classes
      // such as monaco-workbench that also enclose ordinary chat inputs.
      const machineClasses = Array.from(parent.classList).filter(name =>
        /^(?:monaco-editor|CodeMirror(?:-.*)?|cm-editor|ace_editor|xterm(?:-.*)?|terminal)$/i.test(name));
      ancestors += ` ${parent.getAttribute("role") || ""} ${parent.getAttribute("aria-roledescription") || ""} ${machineClasses.join(" ")}${machineClasses.some(name => /^(cm-editor|xterm)/i.test(name)) ? " code-editor" : ""}`;
      if (parent.isContentEditable) return null;
      if (ancestors.length > 4096) return null;
    }
    const attributes = ["autocomplete", "inputmode", "role", "aria-label", "aria-description", "class", "name"]
      .map(name => element.getAttribute(name) || "").join(" ") + ancestors;
    const labels = Array.from(element.labels || [], label => label.textContent || "").join(" ");
    const result = {process: "", label: labels, automationId: element.id,
      help: element.getAttribute("placeholder") || "", aria: attributes,
      password: /(?:^|\s)(?:current-password|new-password|one-time-code|cc-\S+)(?:\s|$)/i.test(attributes),
      enabled: !element.disabled && !element.matches(":disabled") && !element.closest("[inert]"),
      readOnly: !!element.readOnly || element.getAttribute("aria-readonly") === "true",
      offscreen: !geometry(field), inDocument: true, multiline: field.richText || element instanceof HTMLTextAreaElement,
      hasValue: true, hasTextEdit: field.ime !== "Unknown"};
    // Oversized metadata is rejected, not truncated into an apparently safe label.
    return JSON.stringify(result).length <= 8192 ? result : null;
  }
  function authority(field, meta) {
    const mono = monoNow(), token = crypto.randomUUID();
    const result = {target: field.id, metadataKey: JSON.stringify(meta), contentRevision: field.content,
      inputRevision: field.input, focusRevision: field.focus,
      observedAtMs: commandClock?.boottimeMs ?? now(), token};
    field.authorities ||= new Map();
    for (const [id, record] of field.authorities) if (mono - record.mono > 150) field.authorities.delete(id);
    if (field.authorities.size >= 8) field.authorities.delete(field.authorities.keys().next().value);
    field.authorities.set(token, {mono, wall: Date.now(), key: JSON.stringify(result)});
    return result;
  }
  function describe(field) {
    drainMutations();
    if (!field || (!commandClock && !clockValid())) return null;
    const meta = metadata(field);
    if (!meta || meta.password || !meta.enabled || meta.readOnly || meta.offscreen) return null;
    return {target: field.id, metadata: meta, authority: authority(field, meta),
      geometry: geometry(field), focused: focused(field)};
  }
  function approved(field, basis) {
    const meta = field && metadata(field);
    return !!meta && !meta.password && meta.enabled && !meta.readOnly && !meta.offscreen
      && basis?.target === field.id && basis.metadataKey === JSON.stringify(meta);
  }
  function read(field) {
    if (field.richText) return rich.read(field.element);
    return (field.element instanceof HTMLTextAreaElement ? areaValue : inputValue).call(field.element);
  }
  function selection(field) {
    return field.richText ? rich.selection(field.element)
      : {start: field.element.selectionStart, end: field.element.selectionEnd};
  }
  function validText(text) {
    if (typeof text !== "string" || text.length > MAX_TEXT || text.includes("\0")) return false;
    for (let index = 0; index < text.length; index++) {
      const code = text.charCodeAt(index);
      if (code >= 0xd800 && code <= 0xdbff) {
        const next = text.charCodeAt(++index);
        if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
      } else if (code >= 0xdc00 && code <= 0xdfff) return false;
    }
    return true;
  }
  function capture(field, basis) {
    drainMutations();
    if (!approved(field, basis) || field.ime !== "Clear") return null;
    if (!(commandClock ? calibrate(field, basis) : issued(field, basis) && clockValid())
      || field.ime !== "Clear") return null;
    // Body access occurs only after the desktop's metadata approval. Unknown or
    // composing input is not shipped even for preview: there is no committed range API.
    const captured = field.richText ? rich.capture(field.element) : null;
    if (field.richText && !captured) return null;
    const text = captured ? captured.text : read(field);
    if (!validText(text)) return null;
    if (captured) {
      // A page model can change without an input event or visible text change.
      // Never silently rebind an already published plan to a new immutable state.
      if (field.richVersion && field.richVersion !== captured.version) changed(field, true, false);
      field.richVersion = captured.version;
    }
    const result = describe(field);
    if (!result || !approved(field, basis) || field.ime !== "Clear"
      || !issued(field, basis) || !clockValid()) return null;
    return {...result, text, selection: captured?.selection || selection(field),
      ime: field.ime, observedAtMs: now(), writeSupported: !field.quarantined};
  }
  const rejection = reason => ({status: "RejectedBeforeMutation", reason});
  function write(field, request, direct) {
    drainMutations();
    const plan = request.plan, basis = request.authority, approval = direct ? request.approved : basis;
    const expected = plan?.expected;
    if (!field || !expected || expected.target?.value !== field.id || !basis || !approval) return rejection("BadPlan");
    if (!liveDeadline(request)) return rejection("StaleObservation");
    if (!approved(field, approval) || !approved(field, basis)) return rejection("Sensitive");
    if (field.ime !== "Clear" || field.quarantined) return rejection("ImeUnclear");
    if (field.content !== basis.contentRevision) return rejection("SourceChanged");
    if (!direct && (!focused(field) || field.input !== basis.inputRevision || field.focus !== basis.focusRevision)) return rejection("EpochChanged");
    const element = field.element, text = read(field);
    if (text !== expected.text) return rejection("SourceChanged");
    const selected = selection(field);
    if (!direct && (selected?.start !== expected.selection?.start || selected?.end !== expected.selection?.end)) return rejection("SelectionChanged");
    const start = plan.scope?.start ?? 0, end = plan.scope?.end ?? text.length;
    const replacement = plan.replacement;
    if (!Number.isInteger(start) || !Number.isInteger(end) || start < 0 || end < start || end > text.length
      || (plan.scope && start === end) || !validText(replacement) || !replacement.trim()
      || !validText(text.slice(0, start)) || !validText(text.slice(end))
      || (!(element instanceof HTMLTextAreaElement) && /[\r\n]/.test(replacement))) return rejection("InvalidText");
    const result = text.slice(0, start) + replacement + text.slice(end);
    if (!validText(result) || result !== plan.resultText || plan.caret !== start + replacement.length
      || element.maxLength >= 0 && result.length > element.maxLength) return rejection("BadPlan");
    if (field.richText) return writeRich(field, request, direct, text, start, end, result);
    if (!liveDeadline(request)) return rejection("StaleObservation");
    publication = null;
    mutating = true;
    let mutated = false;
    try {
      // No beforeinput dispatch before the mutation: page handlers could change
      // focus/text inside that event. The native operation is a single synchronous edit.
      const replace = element instanceof HTMLTextAreaElement ? areaReplace : inputReplace;
      mutated = true;
      replace.call(element, replacement, start, end, "end");
      ownInputEvent = new InputEvent("input", {bubbles: true, composed: true,
        inputType: "insertReplacementText", data: replacement});
      element.dispatchEvent(ownInputEvent);
      drainMutations();
      field.content++;
      if (field.dead || !approved(field, approval) || field.ime !== "Clear" || read(field) !== result
        || field.content !== basis.contentRevision + 1 || (!direct && !focused(field))
        || element.selectionStart !== plan.caret || element.selectionEnd !== plan.caret) {
        field.quarantined = true;
        return {status: "OutcomeUnknown", reason: "UnknownOutcome"};
      }
      return {status: "Verified", reason: "None", verifiedSnapshot: {...expected, text: result,
        selection: {start: plan.caret, end: plan.caret}, observedAtMs: now(), ime: "Clear", access: "Supported"}};
    } catch (_) {
      field.quarantined = mutated;
      return mutated ? {status: "OutcomeUnknown", reason: "UnknownOutcome"} : rejection("BadPlan");
    } finally {
      rememberSelection(field);
      mutating = false; ownInputEvent = null;
    }
  }
  function writeRich(field, request, direct, text, start, end, result) {
    const basis = request.authority, approval = direct ? request.approved : basis;
    const plan = request.plan;
    const guard = () => {
      drainMutations();
      const current = selection(field);
      return !field.dead && !field.quarantined && field.ime === "Clear"
        && approved(field, approval) && approved(field, basis)
        && liveDeadline(request)
        && field.content === basis.contentRevision && read(field) === text
        && (direct || focused(field) && field.input === basis.inputRevision && field.focus === basis.focusRevision
          && current?.start === plan.expected.selection?.start && current?.end === plan.expected.selection?.end);
    };
    if (!guard() || !field.richVersion) return rejection("SourceChanged");
    publication = shortcut = null;
    mutating = true;
    try {
      const receipt = rich.replace(field.element, {version: field.richVersion, expectedText: text,
        selection: plan.expected.selection, start, end, replacement: plan.replacement, direct,
        expiresAtPerformanceMs: request.expiresAtPerformanceMs,
        expiresAtWallMs: request.expiresAtWallMs}, guard);
      drainMutations();
      if (receipt?.status === "RejectedBeforeMutation") return rejection(receipt.reason || "SourceChanged");
      const current = selection(field);
      if (receipt?.status !== "Applied" || field.dead || field.ime !== "Clear" || !approved(field, approval)
        || read(field) !== result || field.content !== basis.contentRevision || (!direct && !focused(field))
        || current?.start !== plan.caret || current?.end !== plan.caret) {
        field.quarantined = true;
        return {status: "OutcomeUnknown", reason: "UnknownOutcome"};
      }
      field.content++;
      field.richVersion = receipt.version;
      return {status: "Verified", reason: "None", verifiedSnapshot: {...plan.expected, text: result,
        selection: {start: plan.caret, end: plan.caret}, observedAtMs: now(), ime: "Clear", access: "Supported"}};
    } catch (_) {
      field.quarantined = true;
      return {status: "OutcomeUnknown", reason: "UnknownOutcome"};
    } finally { rememberSelection(field); mutating = false; }
  }
  function rememberSelection(field) {
    if (field.richText) {
      const current = rich.selectionIdentity(field.element), previous = field.domSelection;
      field.domSelection = current;
      return !!current !== !!previous || !!current && ["anchorNode", "anchorOffset", "focusNode", "focusOffset"]
        .some(key => current[key] !== previous[key]);
    }
    const start = field.element.selectionStart, end = field.element.selectionEnd;
    const different = start !== field.selectionStart || end !== field.selectionEnd;
    field.selectionStart = start; field.selectionEnd = end;
    return different;
  }
  function selectionChanged(field) {
    if (rememberSelection(field) && !mutating) changed(field, false, false);
  }
  function observationInterrupted() {
    publication = shortcut = clock = null;
    held.clear(); hide();
    for (const field of targets.values()) { field.ime = "Unknown"; field.authorities?.clear(); }
  }
  function changed(field, textChanged, focusChanged) {
    publication = shortcut = null;
    if (!focusChanged) field.input++;
    if (textChanged) field.content++;
    if (focusChanged) field.focus++;
    send("input", {target: field.id, textChanged, focusChanged, focused: focused(field)});
  }
  function mutations(records) {
    for (const record of records) {
      if (!mutating) {
        for (const field of targets.values()) {
          if (field.richText && (record.target === field.element || field.element.contains(record.target))) {
            changed(field, true, false);
          }
        }
      }
      if (record.type !== "childList") continue;
      for (const node of record.removedNodes) {
        for (const field of targets.values()) {
          if (node === field.element || node.contains?.(field.element)) {
            field.dead = true;
            targets.delete(field.id);
            publication = shortcut = null;
            send("destroy", {target: field.id});
            if (overlay?.field === field) hide();
          }
        }
      }
    }
  }
  const observer = new MutationObserver(mutations);
  observer.observe(document, {subtree: true, childList: true, characterData: true});
  function drainMutations() { mutations(observer.takeRecords()); }
  function hide() {
    if (!overlay) return;
    const previous = overlay;
    ui("visible", false);
    overlay = null;
    previous.host.remove();
  }
  function position() {
    if (!overlay) return;
    const geo = geometry(overlay.field);
    if (!geo || !overlay.host.isConnected || !document.hasFocus() || !fresh(overlay.renewed, 800)) { hide(); return; }
    const {bounds} = geo, host = overlay.host;
    const width = Math.min(innerWidth - 16, Math.max(180, Math.min(overlay.expanded ? 720 : 520, overlay.expanded ? Math.max(520, bounds.width) : bounds.width)));
    host.style.width = `${width}px`;
    sizeEditor();
    host.style.left = `${Math.max(8, Math.min(innerWidth - width - 8, bounds.x + bounds.width - width))}px`;
    const height = host.getBoundingClientRect().height;
    host.style.top = `${Math.max(8, Math.min(innerHeight - height - 8,
      bounds.y >= height + 8 ? bounds.y - height - 6 : bounds.y + bounds.height + 6))}px`;
  }
  function render(field, data) {
    const view = data.view;
    if (!approved(field, data.authority) || field.ime !== "Clear" || !document.hasFocus()
      || !fresh(data.expiresAtMs - 800, 800) || !view?.presentation?.visible
      || (!focused(field) && overlay?.field !== field)) { hide(); return {visible: false}; }
    if (overlay && overlay.field !== field) hide();
    if (!overlay) {
      const host = document.createElement("div");
      host.style.cssText = "all:initial;position:fixed;z-index:2147483647;display:block;max-width:calc(100vw - 16px)";
      const shadow = host.attachShadow({mode: "closed"});
      const style = document.createElement("style");
      style.textContent = ":host{color-scheme:light dark}section{font:13px system-ui;color:light-dark(#252a31,#eef1f6);background:light-dark(#fff,#252830);border:1px solid #9299a477;border-radius:14px;box-shadow:0 8px 28px #0003;overflow:hidden}header,footer{padding:8px 12px;font-size:11px;display:flex;align-items:center;gap:8px}header{border-bottom:1px solid #9299a433}footer{border-top:1px solid #9299a433;flex-wrap:wrap}textarea{box-sizing:border-box;display:block;resize:none;width:100%;min-height:64px;max-height:160px;border:0;padding:12px 14px;outline:none;font:14px/1.55 system-ui;background:transparent;color:inherit}textarea:focus{box-shadow:inset 0 0 0 2px #3267e355}button{font:inherit;color:inherit;border:0;border-radius:5px;padding:3px 5px;background:transparent;cursor:pointer}button:hover{background:#9299a422}[hidden]{display:none!important}";
      const panel = document.createElement("section"), header = document.createElement("header");
      const language = document.createElement("button"), status = document.createElement("span");
      language.title = "语言和设置";
      language.addEventListener("click", () => ui("settings"));
      header.append(language, status);
      const editor = document.createElement("textarea");
      editor.setAttribute("aria-label", "TypeLay 译文");
      editor.readOnly = true;
      editor.addEventListener("pointerdown", event => {
        if (!overlay?.editing) { event.preventDefault(); if (overlay?.editable) ui("beginEdit"); }
      });
      editor.addEventListener("input", () => { if (overlay?.editing) ui("draft", editor.value); });
      editor.addEventListener("compositionstart", event => { if (event.isTrusted) { overlay.composing = true; ui("composition", true); } });
      editor.addEventListener("compositionend", event => { if (event.isTrusted) { overlay.composing = false; ui("composition", false); } });
      editor.addEventListener("keydown", event => {
        if (!event.isTrusted || event.isComposing || overlay?.composing || event.keyCode === 229
          || event.ctrlKey || event.altKey || event.shiftKey || event.metaKey || event.repeat) return;
        if (event.key === "Tab") { consume(event); ui("return"); }
        else if (event.key === "Escape") { consume(event); ui("escape"); }
      });
      editor.addEventListener("focus", () => ui("focus", true));
      editor.addEventListener("blur", () => ui("focus", false));
      const footer = document.createElement("footer"), hint = document.createElement("span");
      footer.append(hint);
      const expand = document.createElement("button");
      expand.textContent = "展开";
      expand.addEventListener("pointerdown", event => event.preventDefault());
      expand.addEventListener("click", event => {
        if (!event.isTrusted || !overlay) return;
        overlay.expanded = !overlay.expanded;
        expand.textContent = overlay.expanded ? "缩小" : "展开";
        sizeEditor(); position();
      });
      const copy = document.createElement("button");
      copy.textContent = "复制";
      copy.addEventListener("pointerdown", event => event.preventDefault());
      copy.addEventListener("click", async event => {
        if (!event.isTrusted || !overlay) return;
        const current = overlay;
        try {
          if (!navigator.clipboard?.writeText) throw new Error("clipboard-unavailable");
          await navigator.clipboard.writeText(current.editor.value);
          if (overlay === current) current.status.textContent = "已复制译文";
        } catch (_) {
          if (overlay === current) current.status.textContent = "复制失败，请选中译文手动复制";
        }
      });
      footer.append(expand, copy);
      for (const [text, operation] of [["重译", "regenerate"], ["收起", "escape"], ["排除应用", "exclude"]]) {
        const button = document.createElement("button"); button.textContent = text;
        button.addEventListener("pointerdown", event => event.preventDefault());
        button.addEventListener("click", () => ui(operation)); footer.append(button);
      }
      panel.append(header, editor, footer); shadow.append(style, panel);
      document.documentElement.append(host);
      overlay = {field, host, editor, language, status, hint, footer, editing: false, composing: false, expanded: false};
    }
    if (data.revision < (overlay.revision || 0)) return {visible: true, editing: overlay.editing};
    overlay.revision = data.revision;
    overlay.renewed = data.expiresAtMs - 800;
    overlay.editable = view.editable;
    overlay.host.style.colorScheme = ["light", "dark"].includes(data.theme) ? data.theme : "light dark";
    overlay.language.textContent = `${view.sourceLanguage} → ${view.targetLanguage}`;
    overlay.status.textContent = view.status || "";
    overlay.hint.textContent = view.presentation.tabHint;
    overlay.editor.hidden = !view.text && view.state === "IdleCapsule";
    overlay.footer.hidden = overlay.editor.hidden;
    if (!view.presentation.editorOwnsText) {
      overlay.editor.value = view.text || "";
      overlay.editing = false; overlay.editor.readOnly = true;
    } else if (!overlay.editing) overlay.editor.value = view.text || "";
    sizeEditor(); position();
    return {visible: !!overlay, editing: !!overlay?.editing};
  }
  function sizeEditor() {
    if (!overlay) return;
    const maximum = Math.max(64, Math.min(overlay.expanded ? 360 : 160, innerHeight - 120));
    overlay.editor.style.maxHeight = `${maximum}px`;
    overlay.editor.style.height = "auto";
    overlay.editor.style.height = `${Math.min(maximum, Math.max(64, overlay.editor.scrollHeight))}px`;
  }
  function command(message) {
    const data = message.data || {};
    const field = data.target ? targets.get(data.target) : fieldFor(document.activeElement);
    switch (message.op) {
      case "describe": return describe(field);
      case "capture": return capture(field, data.authority);
      case "publish": {
        const expiresAtPerformanceMs = data.permit ? deadline(data.permit.expiresAtMs, 100) : NaN;
        publication = {...data, expiresAtPerformanceMs,
          expiresAtWallMs: data.permit?.expiresAtMs - clock?.upperWall};
        return {published: !data.permit || liveDeadline(publication)};
      }
      case "publish-shortcut": shortcut = calibrate(field, data.authority) ? data : null; return null;
      case "configure-shortcuts": configuration = data; shortcut = null; return null;
      case "revoke": publication = null; if (data.shortcuts) shortcut = null; return null;
      case "direct": {
        if (!data.plan?.id || consumed.has(data.plan.id)) return rejection("BadPlan");
        for (const [id, expiry] of consumed) if (monoNow() > expiry) consumed.delete(id);
        if (consumed.size >= 128) return rejection("BadPlan");
        consumed.set(data.plan.id, monoNow() + 250);
        if (!calibrate(field, data.approved)) return rejection("StaleObservation");
        return write(field, {...data, expiresAtPerformanceMs: deadline(data.expiresAtMs, 250),
          expiresAtWallMs: data.expiresAtMs - clock?.upperWall}, true);
      }
      case "trigger-shortcut": {
        if (!field || !focused(field) || !configuration?.enabled || !shortcut
          || data.generation !== configuration.generation || data.generation !== shortcut.generation
          || !["wake", "direct"].includes(data.kind) || !fresh(data.triggeredAtMs, 150)
          || !approved(field, data.authority) || !approved(field, shortcut.authority)
          || field.content !== data.authority.contentRevision || field.input !== data.authority.inputRevision
          || field.focus !== data.authority.focusRevision || field.ime !== "Clear") return {triggered: false};
        const snapshot = capture(field, data.authority);
        if (!snapshot) return {triggered: false};
        send("shortcut", {kind: data.kind, generation: data.generation, native: snapshot, acceptedAtMs: now()});
        publication = shortcut = null;
        return {triggered: true};
      }
      case "quarantine": if (field) field.quarantined = true; publication = null; return null;
      case "return": {
        if (!field || !approved(field, data.authority) || field.content !== data.authority.contentRevision
          || field.ime !== "Clear" || !document.hasFocus() || overlay?.composing) return {returned: false};
        field.element.focus({preventScroll: true});
        return {returned: focused(field)};
      }
      case "overlay-render": return render(field, data);
      case "overlay-renew":
        if (overlay?.field === field && approved(field, data.authority) && fresh(data.expiresAtMs - 800, 800)) {
          overlay.renewed = data.expiresAtMs - 800; position();
        } else hide();
        return null;
      case "overlay-edit":
        if (overlay?.field === field && data.allowed === true && document.hasFocus() && field.ime === "Clear") {
          overlay.editing = true; overlay.editor.readOnly = false; overlay.editor.focus({preventScroll: true});
        }
        return null;
      case "overlay-resume":
        if (overlay?.field === field && document.hasFocus()) {
          overlay.status.textContent = "无法返回原输入框，请点击译文继续编辑";
        }
        return null;
      case "overlay-hide": hide(); return null;
      default: return null;
    }
  }
  port.onMessage.addListener(message => {
    if (message.op === "disconnect") { observationInterrupted(); return; }
    const value = message.clock;
    let synced = Number.isSafeInteger(value?.boottimeMs) && value.boottimeMs >= 0;
    // Unrelated host messages never rebase an established clock or permit.
    // A host send cannot be later than our proved receipt upper bound.
    if (synced && clockValid() && value.boottimeMs > now(true)) {
      observationInterrupted(); synced = false;
    }
    commandClock = synced ? value : null;
    if (synced) lastHostStamp = Math.max(Number.isFinite(lastHostStamp) ? lastHostStamp : 0, value.boottimeMs);
    let result = null;
    try {
      if (synced || message.op === "configure-shortcuts") result = command(message);
      else { publication = shortcut = null; hide(); }
    }
    catch (_) { publication = shortcut = null; hide(); }
    commandClock = null;
    if (message.id) port.postMessage({id: message.id, data: result});
  });
  port.onDisconnect.addListener(() => { alive = false; observationInterrupted(); });
  const reportFocus = () => send("focus", document.visibilityState === "visible" && document.hasFocus());
  window.addEventListener("focus", reportFocus, true);
  window.addEventListener("blur", () => {
    reportFocus();
    // Focus loss does not interrupt these window-capture observers. Keep both
    // Clear and sticky Composing so a frozen direct request retains its source.
    if (!document.hasFocus()) { publication = shortcut = null; held.clear(); hide(); }
  }, true);
  document.addEventListener("visibilitychange", () => {
    reportFocus();
    if (document.hidden) { publication = shortcut = null; hide(); }
  });
  window.addEventListener("focusin", event => {
    const field = fieldFor(event.target);
    if (field) changed(field, false, true);
    reportFocus();
  }, true);
  window.addEventListener("focusout", event => {
    const field = fields.get(event.target);
    if (field) changed(field, false, true);
  }, true);
  for (const kind of ["compositionstart", "compositionupdate", "compositionend", "beforeinput", "input", "select"]) {
    window.addEventListener(kind, event => {
      const field = fieldFor(event.target);
      if (!field || event === ownInputEvent) return;
      if (kind === "select") { selectionChanged(field); return; }
      if (kind.startsWith("composition")) {
        // Synthetic DOM composition cannot establish IME authority. It can only revoke it.
        field.ime = event.isTrusted && kind === "compositionend" ? "Clear" : "Composing";
      } else if ((kind === "beforeinput" || kind === "input") && event.isTrusted) {
        // Once a composition has been observed, only its trusted end may clear
        // it. An unrelated input event must not manufacture an idle IME state.
        if (event.isComposing) field.ime = "Composing";
        else if (field.ime !== "Composing") field.ime = "Clear";
      }
      changed(field, kind === "input", false);
      if (kind === "input") rememberSelection(field);
    }, true);
  }
  window.addEventListener("selectionchange", event => {
    const field = fields.get(event.target) || fields.get(document.activeElement);
    if (field) selectionChanged(field);
  }, true);
  function consume(event) { event.preventDefault(); event.stopImmediatePropagation(); held.set(event.code, true); }
  function shortcutMatches(event, binding) {
    const keyval = /^F(?:[1-9]|[12][0-9]|3[0-5])$/.test(event.key)
      ? 65469 + Number(event.key.slice(1))
      : event.key.length === 1 ? event.key.toUpperCase().charCodeAt(0) : 0;
    const modifiers = (event.ctrlKey ? 4 : 0) | (event.altKey ? 8 : 0) | (event.shiftKey ? 1 : 0);
    return !event.metaKey && binding && binding.keyval === keyval && binding.modifiers === modifiers;
  }
  window.addEventListener("keydown", event => {
    if (!event.isTrusted) return;
    if (held.has(event.code)) { if (held.get(event.code)) consume(event); return; }
    if (event.repeat) return;
    held.set(event.code, false);
    const field = fieldFor(event.target);
    if (!field || !focused(field)) return;
    if (event.isComposing || event.keyCode === 229) { field.ime = "Composing"; publication = shortcut = null; return; }
    // A trusted non-composing keyboard event provides positive state evidence;
    // it does not retroactively make a previously issued unknown-state plan valid.
    if (field.ime === "Unknown") { field.ime = "Clear"; changed(field, false, false); return; }
    if (field.ime !== "Clear") return;
    if (configuration?.enabled && shortcut && shortcut.generation === configuration.generation
      && approved(field, shortcut.authority) && issued(field, shortcut.authority)) {
      const kind = shortcutMatches(event, configuration.wake) ? "wake" : shortcutMatches(event, configuration.direct) ? "direct" : null;
      if (kind) {
        const snapshot = capture(field, shortcut.authority);
        if (snapshot) {
          consume(event);
          send("shortcut", {kind, generation: configuration.generation, native: snapshot, acceptedAtMs: now()});
          publication = shortcut = null;
          return;
        }
      }
    }
    if (!publication || event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) return;
    const basis = publication.authority;
    if (!approved(field, basis) || field.input !== basis.inputRevision || field.focus !== basis.focusRevision) { publication = null; return; }
    if (event.key === "Escape" && publication.lease?.escapeAllowed && fresh(publication.lease.source.observedAtMs, 150)) {
      publication = null; consume(event); send("escape", {}); return;
    }
    const permit = publication.permit;
    if (event.key !== "Tab" || !permit?.plan?.id || consumed.has(permit.plan.id)) return;
    for (const [id, expiry] of consumed) if (monoNow() > expiry) consumed.delete(id);
    if (consumed.size >= 128) { publication = null; return; }
    consumed.set(permit.plan.id, monoNow() + 100);
    const {expiresAtPerformanceMs, expiresAtWallMs} = publication;
    const acceptedAtMs = now();
    const receipt = write(field, {plan: permit.plan, authority: basis, expiresAtPerformanceMs, expiresAtWallMs}, false);
    publication = null;
    if (receipt.status !== "RejectedBeforeMutation") {
      consume(event);
      send("accepted", {planId: permit.plan.id, receipt, acceptedAtMs});
    }
  }, true);
  window.addEventListener("keyup", event => {
    if (!event.isTrusted) return;
    if (held.get(event.code)) { event.preventDefault(); event.stopImmediatePropagation(); }
    held.delete(event.code);
  }, true);
  window.addEventListener("scroll", position, true);
  window.addEventListener("resize", position);
  window.addEventListener("pagehide", observationInterrupted, true);
  document.addEventListener("freeze", observationInterrupted, true);
  setInterval(() => { if (!document.hidden) reportFocus(); position(); }, 100);
  send("document", documentId);
  reportFocus();
})();
