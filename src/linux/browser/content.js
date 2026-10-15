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
  let counter = 0, publication = null, shortcut = null, configuration = null, hintTimer = null;
  let clock = null, commandClock = null, lastHostStamp = NaN, alive = true, overlay = null, mutating = false, ownInputEvent = null;
  // Cache native methods in the extension's isolated world, never invoke a
  // page-overridden setter or a page-supplied replacement function.
  const inputReplace = HTMLInputElement.prototype.setRangeText;
  const areaReplace = HTMLTextAreaElement.prototype.setRangeText;
  const inputValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").get;
  const areaValue = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").get;
  const send = (event, data) => { if (alive) port.postMessage({event, data}); };
  const ui = (operation, value) => {
    if (overlay) send("ui", {target: overlay.field.id, operation, revision: overlay.revision,
      generation: overlay.generation, value});
  };
  // Configuration is Applied only by this document's installed source/editor
  // listeners. A native-host or another document's ACK cannot grant capability.
  function sessionApplied() {
    return typeof configuration?.sessionEnabled === "boolean" && typeof configuration.generation === "string"
      && configuration.generation.length > 0
      && typeof configuration.accept === "string" && typeof configuration.exit === "string"
      && (configuration.accept === "Tab" || /^F(?:[1-9]|1[0-9]|2[0-4])$/.test(configuration.accept))
      && (configuration.exit === "Esc" || /^F(?:[1-9]|1[0-9]|2[0-4])$/.test(configuration.exit))
      && configuration.accept !== configuration.exit;
  }
  function sessionConfigured() { return sessionApplied() && configuration.sessionEnabled; }
  function sessionCurrent(generation) {
    return sessionConfigured() && generation === configuration.generation;
  }
  function sessionBindings() {
    return {generation: configuration?.generation ?? null, accept: configuration?.accept ?? null,
      exit: configuration?.exit ?? null, sourceReady: sessionConfigured(), editorReady: sessionConfigured()};
  }
  function sessionKey(event, action) {
    return sessionConfigured() && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey
      && event.key === (configuration[action] === "Esc" ? "Escape" : configuration[action]);
  }
  function viewCurrent(field, data) {
    if (!field || !sessionApplied() || data.generation !== configuration.generation) return false;
    if (data.view?.state === "Paused") return true; // Metadata-only pause has its own revision.
    const context = data.view?.viewContext;
    return configuration.sessionEnabled && context?.token?.target?.value === field.id
      && context.token.commandGeneration === data.generation
      && context.bindings?.accept === configuration.accept && context.bindings?.exit === configuration.exit;
  }
  function overlayCurrent(data) {
    return overlay && sessionCurrent(data.generation) && overlay.generation === data.generation
      && overlay.revision === data.revision;
  }
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
  // Fixed, bounded health counters only. No text, metadata, tokens or paths.
  const captureCounts = Object.fromEntries(["supported", "metadata", "ime", "clockUnavailable",
    "clockExpired", "clockDiscontinuity", "authorityMissing", "authorityChanged", "authorityExpired",
    "authorityClock", "hostAge", "timerPrecision", "intervalConflict", "hostFuture", "invalidClock",
    "richUnavailable", "invalidText", "postcheck"].map(reason => [reason, 0]));
  let lastCaptureReason = "none", lastClockReason = "none";
  let authorityAgeMs = null, hostAgeMs = null, timerTickMs = null, timerProbeMs = null;
  const boundedMetric = value => Number.isFinite(value)
    ? Math.round(Math.max(-10000, Math.min(10000, value)) * 10) / 10 : null;
  const diagnostics = () => ({captureCounts: {...captureCounts}, lastCaptureReason, lastClockReason,
    authorityAgeMs: boundedMetric(authorityAgeMs), hostAgeMs: boundedMetric(hostAgeMs),
    timerTickMs: boundedMetric(timerTickMs), timerProbeMs: boundedMetric(timerProbeMs),
    clockWidthMs: boundedMetric(clock ? clock.upper - clock.lower : NaN),
    clockAgeMs: boundedMetric(clock ? monoNow() - clock.mono : NaN)});
  function noteCapture(reason) {
    lastCaptureReason = Object.hasOwn(captureCounts, reason) ? reason : "postcheck";
    captureCounts[lastCaptureReason] = Math.min(1000000, captureCounts[lastCaptureReason] + 1);
    return null;
  }
  function clockReject(reason) { lastClockReason = reason; return false; }
  function fineClock() {
    const first = monoNow();
    let previous = first;
    timerTickMs = null;
    for (let index = 0; index < 32768; index++) {
      const current = monoNow(), delta = current - previous;
      previous = current;
      timerProbeMs = current - first;
      if (delta < 0) return false;
      if (delta > 0) {
        timerTickMs = timerTickMs === null ? delta : Math.min(timerTickMs, delta);
        if (delta <= 1) return true;
        // A scheduled-out sample measures a gap, not timer resolution. Keep
        // observing within this same budget; never reuse an earlier proof.
      }
    }
    return false;
  }
  function clockValid() {
    if (!clock) return clockReject("clockUnavailable");
    const elapsed = monoNow() - clock.mono;
    if (elapsed < 0 || Math.abs(Date.now() - clock.wall - elapsed) > 20) {
      observationInterrupted();
      return clockReject("clockDiscontinuity");
    }
    if (elapsed > 1500) { clock = publication = shortcut = null; return clockReject("clockExpired"); }
    return true;
  }
  function now(upper = false) {
    return clockValid() ? (upper ? clock.upper + monoNow() : Math.floor(clock.lower + monoNow())) : NaN;
  }
  function issued(field, basis) {
    const record = field?.authorities?.get(basis?.token);
    if (!record) return clockReject("authorityMissing");
    if (record.key !== JSON.stringify(basis)) return clockReject("authorityChanged");
    const elapsed = monoNow() - record.mono;
    if (elapsed < 0 || elapsed + CLOCK_MARGIN > 150) return clockReject("authorityExpired");
    if (Math.abs(Date.now() - record.wall - elapsed) > 20) return clockReject("authorityClock");
    return record;
  }
  function calibrate(field, basis) {
    lastClockReason = "none";
    if (clock && !clockValid()) return false;
    const record = issued(field, basis);
    if (!record) return false;
    if (!commandClock) return clockReject("clockUnavailable");
    if (commandClock.boottimeMs < basis.observedAtMs
      || commandClock.boottimeMs - basis.observedAtMs > 150) return clockReject("hostAge");
    if (!fineClock()) return clockReject("timerPrecision");
    if (!issued(field, basis)) return false;
    const mono = monoNow(), wall = Date.now(), host = commandClock.boottimeMs;
    // The echoed, locally issued token proves the host sampled H after t0 and
    // before this receipt t1. No wall-clock subtraction grants freshness.
    let lower = host - mono - CLOCK_MARGIN, upper = host - record.mono + CLOCK_MARGIN;
    if (clock) {
      if (!clockValid()) return false;
      lower = Math.max(lower, clock.lower);
      upper = Math.min(upper, clock.upper);
      if (lower > upper) { observationInterrupted(); return clockReject("intervalConflict"); }
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
  function metadata(field, domOnly = false) {
    const element = field.element;
    if (field.dead || !(field.richText ? rich?.root(element) === element
      && (mutating || domOnly || rich.probe(element)?.supported === true) : allowedElement(element))
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
  function publicationState(field, meta = null, domOnly = false) {
    const current = publication, plan = current?.permit?.plan, basis = current?.authority;
    const unavailable = {planId: null, ready: false, readyUntilHostMs: null};
    if (!field || !current || !sessionCurrent(current.generation) || typeof plan?.id !== "string" || !plan.id.length || plan.id.length > 256
      || current.target !== field.id || plan.expected?.target?.value !== field.id
      || basis?.target !== field.id || consumed.has(plan.id) || consumed.size >= 128 || field.dead || field.quarantined || field.ime !== "Clear"
      || !focused(field) || field.content !== basis?.contentRevision
      || field.input !== basis.inputRevision || field.focus !== basis.focusRevision) return unavailable;
    meta ||= metadata(field, domOnly);
    if (!meta || meta.password || !meta.enabled || meta.readOnly || meta.offscreen
      || basis.metadataKey !== JSON.stringify(meta) || !liveDeadline(current)
      || publication !== current) return unavailable;
    const mono = monoNow();
    const remaining = Math.min(current.expiresAtPerformanceMs - mono, current.expiresAtWallMs - Date.now());
    const readyUntilHostMs = Math.floor(Math.min(current.permit.expiresAtMs, clock.lower + mono + remaining));
    return remaining > 0 && Number.isSafeInteger(readyUntilHostMs)
      ? {planId: plan.id, ready: true, readyUntilHostMs} : unavailable;
  }
  function describe(field) {
    drainMutations();
    if (!field || (!commandClock && !clockValid())) return null;
    const meta = metadata(field);
    if (!meta || meta.password || !meta.enabled || meta.readOnly || meta.offscreen) return null;
    return {target: field.id, metadata: meta, authority: authority(field, meta),
      geometry: geometry(field), focused: focused(field), diagnostics: diagnostics(),
      publication: publicationState(field, meta), sessionBindings: sessionBindings()};
  }
  function approved(field, basis, domOnly = false) {
    const meta = field && metadata(field, domOnly);
    return !!meta && !meta.password && meta.enabled && !meta.readOnly && !meta.offscreen
      && basis?.target === field.id && basis.metadataKey === JSON.stringify(meta);
  }
  function pauseCurrent(field, basis, domOnly = false) {
    return !!field && field.ime === "Clear" && focused(field) && approved(field, basis, domOnly)
      && field.content === basis.contentRevision && field.input === basis.inputRevision
      && field.focus === basis.focusRevision;
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
  function capture(field, basis, requireFocused = false) {
    const applied = configuration;
    drainMutations();
    const record = field?.authorities?.get(basis?.token);
    authorityAgeMs = record ? monoNow() - record.mono : null;
    hostAgeMs = commandClock && basis ? commandClock.boottimeMs - basis.observedAtMs : null;
    if (!approved(field, basis)) return noteCapture("metadata");
    if (field.ime !== "Clear") return noteCapture("ime");
    if (!(commandClock ? calibrate(field, basis) : issued(field, basis) && clockValid())) {
      return noteCapture(lastClockReason);
    }
    if (field.ime !== "Clear") return noteCapture("ime");
    if (requireFocused && !focused(field)) return noteCapture("postcheck");
    // Body access occurs only after the desktop's metadata approval. Unknown or
    // composing input is not shipped even for preview: there is no committed range API.
    const captured = field.richText ? rich.capture(field.element) : null;
    if (field.richText && !captured) return noteCapture("richUnavailable");
    const text = captured ? captured.text : read(field);
    if (!validText(text)) return noteCapture("invalidText");
    if (captured) {
      // A page model can change without an input event or visible text change.
      // Never silently rebind an already published plan to a new immutable state.
      if (field.richVersion && field.richVersion !== captured.version) changed(field, true, false);
      field.richVersion = captured.version;
    }
    const selected = captured?.selection || selection(field);
    const result = describe(field);
    if (!result || !approved(field, basis)) return noteCapture("postcheck");
    if (field.ime !== "Clear") return noteCapture("ime");
    if (!issued(field, basis) || !clockValid()) return noteCapture(lastClockReason);
    if (requireFocused && !focused(field) || configuration !== applied) return noteCapture("postcheck");
    noteCapture("supported");
    return {...result, diagnostics: diagnostics(), text, selection: selected,
      ime: field.ime, observedAtMs: now(), writeSupported: !field.quarantined && sessionConfigured()};
  }
  const rejection = reason => ({status: "RejectedBeforeMutation", reason});
  function write(field, request, direct) {
    drainMutations();
    const plan = request.plan, basis = request.authority, approval = direct ? request.approved : basis;
    const expected = plan?.expected, applied = configuration;
    if (!sessionCurrent(request.generation)) return rejection("EpochChanged");
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
    if (field.richText) return writeRich(field, request, direct, text, start, end, result, applied);
    if (!liveDeadline(request)) return rejection("StaleObservation");
    if (!sessionCurrent(request.generation) || configuration !== applied) return rejection("EpochChanged");
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
  function writeRich(field, request, direct, text, start, end, result, applied) {
    const basis = request.authority, approval = direct ? request.approved : basis;
    const plan = request.plan;
    const guard = () => {
      drainMutations();
      const current = selection(field);
      return !field.dead && !field.quarantined && field.ime === "Clear"
        && approved(field, approval) && approved(field, basis)
        && liveDeadline(request) && sessionCurrent(request.generation)
        && field.content === basis.contentRevision && read(field) === text
        && (direct || focused(field) && field.input === basis.inputRevision && field.focus === basis.focusRevision
          && current?.start === plan.expected.selection?.start && current?.end === plan.expected.selection?.end)
        && sessionCurrent(request.generation) && configuration === applied;
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
    hide();
    for (const field of targets.values()) { field.ime = "Unknown"; field.authorities?.clear(); }
  }
  function changed(field, textChanged, focusChanged) {
    publication = shortcut = null;
    if (!focusChanged) field.input++;
    if (textChanged) field.content++;
    if (focusChanged) field.focus++;
    if (overlay?.paused && overlay.field === field) hide();
    refreshHint();
    send("input", {target: field.id, textChanged, focusChanged, focused: focused(field)});
  }
  function mutations(records) {
    if (records.length && overlay?.paused && !pauseCurrent(overlay.field, overlay.authority, true)) hide();
    for (const record of records) {
      if (record.type === "attributes") continue;
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
  observer.observe(document, {subtree: true, childList: true, characterData: true, attributes: true});
  function drainMutations() { mutations(observer.takeRecords()); }
  function hide() {
    clearTimeout(hintTimer); hintTimer = null;
    if (!overlay) return;
    const previous = overlay;
    ui("visible", false);
    overlay = null;
    previous.host.remove();
  }
  function refreshHint() {
    clearTimeout(hintTimer); hintTimer = null;
    const current = overlay;
    if (!current || current.paused) return;
    // UI refresh must not open a page-owned rich-editor callback after a
    // capture result has passed its final IME/source guard.
    const state = publicationState(current.field, null, true);
    if (overlay !== current) return;
    const shortcuts = current.shortcuts;
    const accepts = shortcuts && [shortcuts.acceptHint, shortcuts.passThroughHint].includes(current.tabHint);
    const text = current.editing ? shortcuts?.returnHint || ""
      : accepts ? (state.ready ? shortcuts.acceptHint : shortcuts.passThroughHint) : current.tabHint || "";
    if (current.hint.textContent !== text) current.hint.textContent = text;
    if (state.ready && publication) {
      const remaining = Math.min(publication.expiresAtPerformanceMs - monoNow(), publication.expiresAtWallMs - Date.now());
      hintTimer = setTimeout(refreshHint, Math.max(1, Math.ceil(remaining)));
    }
  }
  function position() {
    refreshHint();
    if (!overlay) return;
    const geo = geometry(overlay.field);
    if (!geo || !overlay.host.isConnected || !document.hasFocus() || !fresh(overlay.renewed, 800)
      || overlay.paused && !pauseCurrent(overlay.field, overlay.authority, true)) { hide(); return; }
    const {bounds} = geo, host = overlay.host;
    const width = overlay.paused ? Math.min(innerWidth - 16, 160)
      : Math.min(innerWidth - 16, Math.max(180, Math.min(overlay.expanded ? 720 : 520, overlay.expanded ? Math.max(520, bounds.width) : bounds.width)));
    host.style.width = `${width}px`;
    sizeEditor();
    host.style.left = `${Math.max(8, Math.min(innerWidth - width - 8, bounds.x + bounds.width - width))}px`;
    const height = host.getBoundingClientRect().height;
    host.style.top = `${Math.max(8, Math.min(innerHeight - height - 8,
      bounds.y >= height + 8 ? bounds.y - height - 6 : bounds.y + bounds.height + 6))}px`;
  }
  function render(field, data) {
    if (!viewCurrent(field, data)) return {visible: false};
    if (overlay && overlay.field === field && data.revision < (overlay.revision || 0)) {
      return {visible: overlay.host.isConnected, editing: overlay.editing};
    }
    const view = data.view, paused = view?.state === "Paused";
    if (paused) publication = shortcut = null;
    // A paused view has no capture. Echoing its metadata authority establishes
    // the same bounded host clock proof without reading source text/selection.
    if (paused && (!pauseCurrent(field, data.authority) || !calibrate(field, data.authority))) {
      hide(); return {visible: false};
    }
    if (!approved(field, data.authority) || field.ime !== "Clear" || !document.hasFocus()
      || !fresh(data.expiresAtMs - 800, 800) || !view?.presentation?.visible
      || (!focused(field) && overlay?.field !== field) || !viewCurrent(field, data)) { hide(); return {visible: false}; }
    if (overlay && overlay.field !== field) hide();
    if (!overlay) {
      const host = document.createElement("div");
      host.style.cssText = "all:initial;position:fixed;z-index:2147483647;display:block;max-width:calc(100vw - 16px)";
      const shadow = host.attachShadow({mode: "closed"});
      const style = document.createElement("style");
      style.textContent = `
        :host{color-scheme:light dark}
        section{--surface:light-dark(#fff,#252830);--ink:light-dark(#252a31,#eef1f6);
          --muted:light-dark(#666b75,#b6bdc9);--line:light-dark(#dfe3eb,#48515e);
          --hover:light-dark(#f1f3f6,#303741);--accent:light-dark(#3267e3,#83a7ff);
          font:13px system-ui;color:var(--ink);background:var(--surface);border:1px solid var(--line);
          border-radius:12px;box-shadow:0 8px 24px #0002;overflow:hidden}
        header,footer{display:flex;align-items:center;gap:8px;font-size:12px;color:var(--muted)}
        header{padding:10px 12px 6px;flex-wrap:wrap}
        header .status{flex:1;min-width:0;text-align:right;overflow-wrap:anywhere}
        footer{margin:8px 12px 0;padding:7px 0 8px;border-top:1px solid var(--line);flex-wrap:wrap}
        footer .hint{flex:1}
        .more-actions{display:flex;flex-direction:column;gap:4px;margin:0 12px;padding:8px 0;
          border-top:1px solid var(--line);color:var(--muted);font-size:12px}
        .more-actions button{text-align:left}
        textarea{box-sizing:border-box;display:block;resize:none;width:calc(100% - 16px);margin:0 8px;
          min-height:64px;max-height:160px;border:1px solid transparent;border-radius:6px;padding:6px;
          outline:none;font:15px/1.58 system-ui;background:transparent;color:var(--ink)}
        textarea[data-editable=true]:read-only:hover,textarea[data-editable=true]:read-only:focus-visible{background:var(--hover)}
        textarea:not(:read-only){border-color:var(--accent)}
        textarea:focus-visible{border-color:var(--accent)}
        button{flex:none;font:inherit;color:inherit;border:0;border-radius:6px;padding:4px 6px;
          min-height:26px;background:transparent;cursor:pointer}
        button:hover{background:var(--hover);color:var(--ink)}
        button:focus-visible{outline:1px solid var(--accent);outline-offset:1px}
        section.paused{color:var(--muted);box-shadow:0 2px 8px #0001}
        section.paused header{padding-bottom:10px;justify-content:space-between}
        section.paused .status{text-align:left}
        button[aria-disabled=true]{cursor:default;opacity:.5}
        [hidden]{display:none!important}`;
      const panel = document.createElement("section"), header = document.createElement("header");
      const language = document.createElement("button"), status = document.createElement("span");
      language.title = "语言和设置";
      status.className = "status";
      language.addEventListener("click", () => ui("settings"));
      const resume = document.createElement("button");
      resume.textContent = "恢复";
      resume.tabIndex = -1;
      resume.hidden = true;
      resume.addEventListener("pointerdown", event => event.preventDefault());
      resume.addEventListener("click", event => {
        const current = overlay;
        if (!event.isTrusted || !current?.paused || current.resume !== resume || current.resumePending) return;
        const {field, authority, revision, renewed} = current;
        drainMutations();
        if (overlay !== current) return;
        if (!fresh(renewed, 800)) { hide(); return; }
        const allowed = pauseCurrent(field, authority);
        // The page-owned probe can synchronously occupy the event loop past
        // expiry or replace the rendered view. Timers cannot guard this return.
        if (overlay !== current || current.field !== field || current.authority !== authority
          || current.revision !== revision || current.renewed !== renewed
          || !current.paused || current.resumePending) return;
        if (!allowed || !fresh(renewed, 800)) { hide(); return; }
        current.resumePending = true;
        current.resume.setAttribute("aria-disabled", "true");
        ui("resume");
      });
      header.append(language, status, resume);
      const editor = document.createElement("textarea");
      editor.setAttribute("aria-label", "TypeLay 译文");
      editor.readOnly = true;
      editor.addEventListener("pointerdown", event => {
        if (!overlay?.editing) { event.preventDefault(); if (overlay?.editable && sessionCurrent(overlay.generation)) ui("beginEdit"); }
      });
      editor.addEventListener("input", () => { if (overlay?.editing) ui("draft", editor.value); });
      editor.addEventListener("compositionstart", event => { if (event.isTrusted) { overlay.composing = true; ui("composition", true); } });
      editor.addEventListener("compositionend", event => { if (event.isTrusted) { overlay.composing = false; ui("composition", false); } });
      editor.addEventListener("focus", () => ui("focus", true));
      editor.addEventListener("blur", () => ui("focus", false));
      const footer = document.createElement("footer"), hint = document.createElement("span");
      hint.className = "hint";
      footer.append(hint);
      const expand = document.createElement("button");
      expand.textContent = "展开";
      expand.addEventListener("pointerdown", event => event.preventDefault());
      expand.addEventListener("click", event => {
        if (!event.isTrusted || !overlay) return;
        overlay.expanded = !overlay.expanded;
        expand.textContent = overlay.expanded ? "缩小" : "展开";
        setMoreOpen(false);
      });
      const collapse = document.createElement("button");
      collapse.textContent = "收起";
      collapse.addEventListener("pointerdown", event => event.preventDefault());
      collapse.addEventListener("click", () => ui("escape"));
      const more = document.createElement("button"), menu = document.createElement("div");
      more.textContent = "更多";
      more.setAttribute("aria-expanded", "false");
      more.setAttribute("aria-controls", "more-actions");
      menu.id = menu.className = "more-actions";
      menu.setAttribute("role", "group");
      menu.setAttribute("aria-label", "更多操作");
      menu.hidden = true;
      const setMoreOpen = open => {
        menu.hidden = !open;
        more.setAttribute("aria-expanded", String(open));
        position();
      };
      // Disclosure controls retain source/editor focus just like the existing
      // actions. Opening this UI never renews its host lease or a write permit.
      more.addEventListener("pointerdown", event => event.preventDefault());
      more.addEventListener("click", event => {
        if (event.isTrusted && overlay) setMoreOpen(menu.hidden);
      });
      const copy = document.createElement("button");
      copy.textContent = "复制译文";
      copy.addEventListener("pointerdown", event => event.preventDefault());
      copy.addEventListener("click", async event => {
        if (!event.isTrusted || !overlay) return;
        const current = overlay;
        setMoreOpen(false);
        try {
          if (!navigator.clipboard?.writeText) throw new Error("clipboard-unavailable");
          await navigator.clipboard.writeText(current.editor.value);
          if (overlay === current) current.status.textContent = "已复制译文";
        } catch (_) {
          if (overlay === current) current.status.textContent = "复制失败，请选中译文手动复制";
        }
      });
      header.append(expand, more);
      footer.append(collapse);
      menu.append(copy);
      for (const [text, operation] of [["重新翻译", "regenerate"], ["不要在这个应用中显示", "exclude"], ["设置", "settings"]]) {
        const button = document.createElement("button"); button.textContent = text;
        button.addEventListener("pointerdown", event => event.preventDefault());
        button.addEventListener("click", () => { setMoreOpen(false); ui(operation); }); menu.append(button);
      }
      panel.append(header, editor, footer, menu); shadow.append(style, panel);
      document.documentElement.append(host);
      overlay = {field, host, shadow, panel, editor, language, status, resume, hint, footer, more, menu, expand, collapse, editing: false, composing: false, expanded: false};
    }
    if (data.revision < (overlay.revision || 0)) return {visible: true, editing: overlay.editing};
    if (overlay.revision !== data.revision) overlay.resumePending = false;
    overlay.revision = data.revision;
    overlay.generation = data.generation;
    overlay.shortcuts = view.sessionShortcuts;
    overlay.collapse.textContent = view.sessionShortcuts?.exitHint || "收起";
    overlay.paused = paused;
    overlay.authority = data.authority;
    overlay.resume.setAttribute("aria-disabled", String(!!overlay.resumePending));
    overlay.resume.hidden = !paused;
    overlay.language.hidden = paused;
    overlay.panel.classList.toggle("paused", paused);
    overlay.renewed = data.expiresAtMs - 800;
    overlay.editable = !paused && view.editable && view.viewContext?.canEdit === true;
    overlay.editor.dataset.editable = String(!!overlay.editable);
    overlay.editor.title = overlay.editable
      ? view.presentation.editorOwnsText ? "编辑译文" : "点击译文以编辑" : "";
    overlay.host.style.colorScheme = ["light", "dark"].includes(data.theme) ? data.theme : "light dark";
    const languageName = code => ({auto: "Auto", zh: "中文", en: "EN"})[code] || code;
    overlay.language.textContent = `${languageName(view.sourceLanguage)} → ${languageName(view.targetLanguage)}`;
    overlay.status.textContent = paused ? "已暂停" : view.status || "";
    overlay.tabHint = view.presentation.tabHint;
    overlay.editor.hidden = paused || !view.text && view.state === "IdleCapsule";
    overlay.footer.hidden = overlay.editor.hidden;
    overlay.more.hidden = overlay.editor.hidden;
    overlay.expand.hidden = overlay.editor.hidden;
    if (overlay.footer.hidden) {
      overlay.menu.hidden = true;
      overlay.more.setAttribute("aria-expanded", "false");
    }
    if (paused || !view.presentation.editorOwnsText) {
      overlay.editor.value = paused ? "" : view.text || "";
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
      case "capture": return capture(field, data.authority, data.requireFocused === true);
      case "publish": {
        if (!sessionCurrent(data.generation)) return {published: false};
        const previous = publication, oldBasis = previous?.authority, basis = data.authority;
        const sameSource = field && previous?.target === field.id && data.target === field.id
          && field.content === basis?.contentRevision && field.input === basis?.inputRevision
          && field.focus === basis?.focusRevision
          && oldBasis?.contentRevision === basis?.contentRevision
          && oldBasis?.inputRevision === basis?.inputRevision
          && oldBasis?.focusRevision === basis?.focusRevision
          && oldBasis?.metadataKey === basis?.metadataKey
          && previous?.permit?.plan?.expected?.text === data.permit?.plan?.expected?.text;
        const previousRemaining = sameSource ? Math.min(previous.expiresAtPerformanceMs - monoNow(),
          previous.expiresAtWallMs - Date.now()) : NaN;
        const previousPermitRemainingMs = Number.isFinite(previousRemaining)
          ? Math.max(-10000, Math.min(10000, previousRemaining)) : null;
        // This publication echoes the new authority issued by its capture.
        // Recalibrate before freezing this permit's deadlines; the cached
        // authority used to capture must not age its successor's wall bound.
        if (data.permit && !calibrate(field, data.authority) || !sessionCurrent(data.generation)) {
          publication = null;
          return {published: false};
        }
        const expiresAtPerformanceMs = data.permit ? deadline(data.permit.expiresAtMs, 100) : NaN;
        publication = {...data, expiresAtPerformanceMs,
          expiresAtWallMs: data.permit?.expiresAtMs - clock?.upperWall};
        // Metadata-only acknowledgment of the publication actually received by
        // this document. It grants no permission beyond the existing permit.
        if (field) send("publication", {target: field.id, ...publicationState(field), previousPermitRemainingMs});
        return {published: !data.permit || !!publication && liveDeadline(publication)};
      }
      case "publish-shortcut": shortcut = calibrate(field, data.authority) ? data : null; return null;
      case "configure-shortcuts":
        // Revoke the old generation before applying even invalid or disabled input.
        publication = shortcut = null; hide();
        configuration = {...data};
        return null;
      case "revoke": publication = null; if (data.shortcuts) shortcut = null; return null;
      case "direct": {
        if (!sessionCurrent(data.generation)) return rejection("EpochChanged");
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
        if (!snapshot || !sessionCurrent(data.generation)) return {triggered: false};
        send("shortcut", {kind: data.kind, generation: data.generation, native: snapshot, acceptedAtMs: now()});
        publication = shortcut = null;
        return {triggered: true};
      }
      case "quarantine": if (field) field.quarantined = true; publication = null; return null;
      case "return": {
        if (!sessionCurrent(data.generation) || !field || !approved(field, data.authority)
          || field.content !== data.authority.contentRevision || field.ime !== "Clear"
          || !document.hasFocus() || overlay?.composing || !sessionCurrent(data.generation)) return {returned: false};
        field.element.focus({preventScroll: true});
        return {returned: sessionCurrent(data.generation) && focused(field)};
      }
      case "overlay-render": return render(field, data);
      case "overlay-renew":
        if (!sessionApplied() || data.generation !== configuration.generation) return null;
        if (overlay?.field === field && sessionApplied() && data.generation === configuration.generation
          && (overlay.paused || configuration.sessionEnabled) && overlay.generation === data.generation
          && approved(field, data.authority)
          && (!overlay.paused || pauseCurrent(field, data.authority) && calibrate(field, data.authority))
          && fresh(data.expiresAtMs - 800, 800) && sessionApplied() && data.generation === configuration.generation
          && overlay?.generation === data.generation) {
          overlay.authority = data.authority;
          overlay.renewed = data.expiresAtMs - 800; position();
        } else hide();
        return null;
      case "overlay-edit":
        if (overlay?.field === field && overlayCurrent(data) && overlay.editable && !overlay.paused
          && data.allowed === true && document.hasFocus() && field.ime === "Clear") {
          overlay.editing = true; overlay.editor.readOnly = false; overlay.editor.focus({preventScroll: true});
        }
        return null;
      case "overlay-resume":
        if (overlay?.field === field && overlayCurrent(data) && !overlay.paused && document.hasFocus()) {
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
      lastClockReason = "hostFuture";
    } else if (!synced) lastClockReason = "invalidClock";
    commandClock = synced ? value : null;
    if (synced) lastHostStamp = Math.max(Number.isFinite(lastHostStamp) ? lastHostStamp : 0, value.boottimeMs);
    const traceRound = message.data?.traceRound;
    const captureStarted = configuration?.trace && message.op === "capture"
      && Number.isInteger(traceRound) && traceRound > 0 && traceRound < 2147483648 ? monoNow() : null;
    let result = null;
    try {
      if (synced || message.op === "configure-shortcuts") result = command(message);
      else {
        if (message.op === "capture") noteCapture(lastClockReason);
        publication = shortcut = null; hide();
      }
    }
    catch (_) { publication = shortcut = null; hide(); }
    commandClock = null;
    if (captureStarted !== null && result) {
      const elapsed = monoNow() - captureStarted;
      if (Number.isFinite(elapsed) && elapsed >= 0 && elapsed <= 10000)
        result.captureTrace = {round: traceRound, pageCaptureMs: Math.round(elapsed * 10) / 10};
    }
    if (message.id) port.postMessage({id: message.id, data: result});
    refreshHint();
  });
  port.onDisconnect.addListener(() => { alive = false; observationInterrupted(); });
  const reportFocus = () => send("focus", document.visibilityState === "visible" && document.hasFocus());
  window.addEventListener("focus", reportFocus, true);
  window.addEventListener("blur", () => {
    reportFocus();
    // Focus loss does not interrupt these window-capture observers. Keep both
    // Clear and sticky Composing so a frozen direct request retains its source.
    if (!document.hasFocus()) { publication = shortcut = null; hide(); }
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
  function tabDiagnostic(event, field) {
    if (!configuration?.trace || event.key !== "Tab" || event.ctrlKey || event.altKey
      || event.shiftKey || event.metaKey) return () => {};
    const current = publication, basis = current?.authority, mono = monoNow();
    const detail = {publication: !!current, imeClear: field?.ime === "Clear",
      publicationRound: Number.isInteger(current?.traceRound) && current.traceRound > 0
        && current.traceRound < 2147483648 ? current.traceRound : null,
      targetMatches: !!field && current?.target === field.id,
      inputMatches: !!field && field.input === basis?.inputRevision,
      focusMatches: !!field && field.focus === basis?.focusRevision,
      remainingMs: current ? Math.min(current.expiresAtPerformanceMs - mono,
        current.expiresAtWallMs - Date.now()) : null};
    return reason => {
      try { send("tab-diagnostic", {...detail, reason}); }
      catch (_) { /* A diagnostic channel failure cannot change key handling. */ }
    };
  }
  window.addEventListener("keydown", event => {
    if (!event.isTrusted) return;
    if (held.has(event.code)) { tabDiagnostic(event, fields.get(event.target))("held"); if (held.get(event.code)) consume(event); return; }
    if (event.repeat) { tabDiagnostic(event, fields.get(event.target))("repeat"); return; }
    held.set(event.code, false);
    // A closed shadow root retargets to the host at window capture. Handle the
    // installed editor here so one physical key has one owner across focus/config.
    if (overlay && event.target === overlay.host && overlay.editing
      && overlay.shadow.activeElement === overlay.editor && sessionCurrent(overlay.generation)) {
      if (event.isComposing || overlay.composing || event.keyCode === 229) return;
      if (sessionKey(event, "accept")) { publication = null; consume(event); ui("return"); }
      else if (sessionKey(event, "exit")) { publication = null; consume(event); ui("escape"); }
      return;
    }
    const field = fieldFor(event.target);
    const reportTab = tabDiagnostic(event, field);
    if (!field || !focused(field)) { reportTab("target-unfocused"); return; }
    if (event.isComposing || event.keyCode === 229) { reportTab("composing"); field.ime = "Composing"; publication = shortcut = null; refreshHint(); return; }
    // A trusted non-composing keyboard event provides positive state evidence;
    // it does not retroactively make a previously issued unknown-state plan valid.
    if (field.ime === "Unknown") { reportTab("ime-unknown"); field.ime = "Clear"; changed(field, false, false); return; }
    if (field.ime !== "Clear") { reportTab("composing"); return; }
    if (configuration?.enabled && shortcut && shortcut.generation === configuration.generation
      && approved(field, shortcut.authority) && issued(field, shortcut.authority)) {
      const kind = shortcutMatches(event, configuration.wake) ? "wake" : shortcutMatches(event, configuration.direct) ? "direct" : null;
      if (kind) {
        const current = shortcut, generation = configuration.generation;
        const snapshot = capture(field, current.authority);
        if (snapshot && shortcut === current && sessionCurrent(generation)) {
          consume(event);
          send("shortcut", {kind, generation, native: snapshot, acceptedAtMs: now()});
          publication = shortcut = null; refreshHint();
          return;
        }
      }
    }
    if (!publication || !sessionCurrent(publication.generation) || event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) { reportTab("no-publication"); return; }
    const current = publication, basis = current.authority;
    if (!approved(field, basis) || field.input !== basis.inputRevision || field.focus !== basis.focusRevision) { reportTab("authority-changed"); publication = null; refreshHint(); return; }
    if (publication !== current || !sessionCurrent(current.generation)) return;
    if (sessionKey(event, "exit") && publication.lease?.escapeAllowed && fresh(publication.lease.source.observedAtMs, 150)) {
      publication = null; refreshHint(); consume(event); send("escape", {target: field.id,
        generation: current.generation, sessionRevision: current.lease.sessionRevision}); return;
    }
    const permit = publication.permit;
    if (!sessionKey(event, "accept") || !permit?.plan?.id || consumed.has(permit.plan.id)) { reportTab("no-permit"); return; }
    for (const [id, expiry] of consumed) if (monoNow() > expiry) consumed.delete(id);
    if (consumed.size >= 128) { reportTab("capacity"); publication = null; refreshHint(); return; }
    consumed.set(permit.plan.id, monoNow() + 100);
    const {expiresAtPerformanceMs, expiresAtWallMs} = publication;
    const acceptedAtMs = now();
    const receipt = write(field, {plan: permit.plan, authority: basis, generation: current.generation, expiresAtPerformanceMs, expiresAtWallMs}, false);
    reportTab(receipt.reason);
    publication = null; refreshHint();
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
