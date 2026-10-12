/* MAIN-world Tiptap adapter. No extension APIs or host/model commands are exposed.
 * The page owns this editor; every reply remains untrusted by the isolated world.
 * Only a single paragraph of text/marks is supported. Native history grouping is
 * retained. No private ProseMirror PluginKey or DOM-writing fallback is used. */
(() => {
  "use strict";
  const REQUEST = "typelay-editor-request", RESPONSE = "typelay-editor-response";
  const CHECK = "typelay-editor-check", CHECK_RESULT = "typelay-editor-check-result";
  const snapshots = new WeakMap();
  const busy = new WeakSet();
  let serial = 0;
  const MAX = 8192;
  const monotonic = performance.now.bind(performance);
  const computedStyle = window.getComputedStyle.bind(window);
  const clientRects = Element.prototype.getClientRects;
  const boundingRect = Element.prototype.getBoundingClientRect;
  const rejected = reason => ({status: "RejectedBeforeMutation", reason});
  const unknown = () => ({status: "OutcomeUnknown", reason: "UnknownOutcome"});
  function valid(text) {
    if (typeof text !== "string" || text.length > MAX || /[\0\r\n]/.test(text)) return false;
    for (let index = 0; index < text.length; index++) {
      const code = text.charCodeAt(index);
      if (code >= 0xd800 && code <= 0xdbff) {
        const next = text.charCodeAt(++index);
        if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
      } else if (code >= 0xdc00 && code <= 0xdfff) return false;
    }
    return true;
  }
  function target(root) {
    if (!(root instanceof HTMLElement) || !root.isConnected || root.getRootNode() !== document
      || !root.isContentEditable) return null;
    const editor = root.editor, view = editor?.view, state = editor?.state;
    if (!editor || editor.isDestroyed || !editor.isEditable || editor.isCapturingTransaction
      || view?.dom !== root || view.isDestroyed || !state || typeof view.dispatch !== "function"
      || typeof editor.on !== "function" || typeof editor.off !== "function") return null;
    // Structural capability discovery must not access the model's text.
    const doc = state.doc;
    if (doc?.childCount !== 1) return null;
    const paragraph = doc.firstChild;
    if (paragraph?.type?.name !== "paragraph" || !paragraph.isTextblock || paragraph.type.spec.code
      || paragraph.content.size > MAX) return null;
    let supported = true;
    paragraph.forEach(node => {
      if (!node.isText || node.marks.some(mark => mark.type.spec.code || mark.type.name === "code")) supported = false;
    });
    return supported ? {editor, view, state, paragraph} : null;
  }
  function version(root, current) {
    let entry = snapshots.get(root);
    if (!entry || entry.editor !== current.editor || entry.view !== current.view || entry.state.doc !== current.state.doc) {
      entry = {...current, version: String(++serial)};
      snapshots.set(root, entry);
    }
    return entry.version;
  }
  function selection(current) {
    const range = current.state.selection;
    const size = current.paragraph.content.size;
    return Number.isInteger(range?.from) && Number.isInteger(range?.to)
      && range.from >= 1 && range.to >= range.from && range.to <= size + 1
      ? {start: range.from - 1, end: range.to - 1} : null;
  }
  function capture(root, current) {
    if (current.view.composing) return null;
    const text = current.paragraph.textContent, range = selection(current);
    if (!valid(text) || !range || text !== root.textContent) return null;
    return {text, selection: range, version: version(root, current)};
  }
  function check(root, id) {
    let count = 0, allowed = false;
    const receive = event => {
      if (event.target !== root || typeof event.detail !== "string" || event.detail.length > 256) return;
      try {
        const result = JSON.parse(event.detail);
        if (result.id !== id) return;
        count++; allowed = result.allowed === true;
      } catch (_) { /* An invalid page message does not authorize a write. */ }
    };
    window.addEventListener(CHECK_RESULT, receive, true);
    try {
      const event = new CustomEvent(CHECK, {bubbles: true, cancelable: true, detail: JSON.stringify({id})});
      root.dispatchEvent(event);
      return count === 1 && allowed && !event.defaultPrevented;
    } finally { window.removeEventListener(CHECK_RESULT, receive, true); }
  }
  function replace(root, current, data, id) {
    const remaining = data.expiresAtPerformanceMs - monotonic();
    if (!Number.isFinite(remaining) || remaining < 0 || remaining > 250) return rejected("StaleObservation");
    if (current.view.composing) return rejected("ImeUnclear");
    const captured = capture(root, current);
    if (!captured || data.version !== captured.version || data.expectedText !== captured.text) return rejected("SourceChanged");
    if (!data.direct && (document.activeElement !== root || data.selection?.start !== captured.selection.start
      || data.selection?.end !== captured.selection.end)) return rejected("SelectionChanged");
    const {start, end, replacement} = data, text = captured.text;
    if (!Number.isInteger(start) || !Number.isInteger(end) || start < 0 || end < start || end > text.length
      || !valid(replacement) || !replacement.trim() || !valid(text.slice(0, start)) || !valid(text.slice(end))) return rejected("InvalidText");
    const result = text.slice(0, start) + replacement + text.slice(end);
    if (!valid(result)) return rejected("InvalidText");
    const {editor, view, state} = current;
    const selected = document.getSelection(), active = document.activeElement;
    const anchor = selected && [selected.anchorNode, selected.anchorOffset, selected.focusNode, selected.focusOffset];
    const transaction = state.tr.insertText(replacement, start + 1, end + 1);
    const originalUpdate = view.updateState;
    const originalDescriptor = Object.getOwnPropertyDescriptor(view, "updateState");
    const mutations = new MutationObserver(() => {});
    mutations.observe(root, {subtree: true, childList: true, characterData: true, attributes: true});
    // Also see removal/reinsertion and the ancestors used by metadata approval.
    // Unrelated page UI changes do not invalidate the source.
    const ancestors = new Set();
    for (let parent = root.parentElement; parent; parent = parent.parentElement) ancestors.add(parent);
    mutations.observe(document, {subtree: true, childList: true, attributes: true, characterData: true});
    const relevantMutation = record => record.target === root || root.contains(record.target)
      || record.type === "attributes" && ancestors.has(record.target)
      || Array.from(record.removedNodes || []).some(node => node === root || node.contains?.(root));
    let sourceEvent = false;
    const inputEvents = ["compositionstart", "compositionupdate", "compositionend", "beforeinput", "input", "select"];
    const interrupted = event => {
      if (event.target === root || event.target instanceof Node && root.contains(event.target)) sourceEvent = true;
    };
    for (const type of inputEvents) window.addEventListener(type, interrupted, true);
    const visible = () => {
      if (!clientRects.call(root).length) return false;
      const rect = boundingRect.call(root);
      if (rect.right <= 0 || rect.bottom <= 0 || rect.left >= innerWidth || rect.top >= innerHeight) return false;
      for (const element of [root, ...ancestors]) {
        const style = computedStyle(element);
        if (style.display === "none" || style.visibility !== "visible" || Number(style.opacity) === 0) return false;
      }
      return true;
    };
    let checked = false, wrapperActive = true, nextState = null, completedState = null;
    let receipt = unknown();
    const abort = new Error("typelay-source-changed");
    const unchanged = () => editor.state === state && editor.view === view && view.dom === root && root.isConnected
      && editor.isEditable && !view.composing && !sourceEvent && document.activeElement === active
      && (data.direct || document.hasFocus() && document.visibilityState === "visible")
      && visible()
      && !mutations.takeRecords().some(relevantMutation)
      && selected && selected.anchorNode === anchor[0] && selected.anchorOffset === anchor[1]
      && selected.focusNode === anchor[2] && selected.focusOffset === anchor[3]
      && monotonic() <= data.expiresAtPerformanceMs;
    const wrappedUpdate = function (incomingState) {
      // Preserve nested application transactions. Only the outer transaction's
      // exact computed nextState gets the final check at its actual commit.
      if (wrapperActive && incomingState === nextState && (!checked || !unchanged())) throw abort;
      return originalUpdate.call(this, incomingState);
    };
    const guard = ({transaction: incoming, nextState: incomingState}) => {
      if (incoming !== transaction) return;
      if (!incomingState?.doc?.eq(transaction.doc)) throw abort;
      nextState = incomingState;
      // CHECK is itself observable by the page. Recheck without another event
      // after it returns, so handlers cannot slip an edit behind the last guard.
      if (checked || !unchanged() || !check(root, id) || !unchanged()) throw abort;
      checked = true;
    };
    try {
      view.updateState = wrappedUpdate;
      editor.on("beforeTransaction", guard);
      view.dispatch(transaction);
      const after = target(root);
      if (checked && after && after.editor === editor && after.view === view && !view.composing && !sourceEvent
        && Object.getOwnPropertyDescriptor(view, "updateState")?.value === wrappedUpdate
        && after.state.doc.eq(transaction.doc) && root.textContent === result
        && after.state.selection.from === start + replacement.length + 1
        && after.state.selection.to === start + replacement.length + 1) {
        completedState = after.state;
        receipt = {status: "Applied", text: result,
          selection: {start: start + replacement.length, end: start + replacement.length}, version: version(root, after)};
      }
    } catch (_) {
      // Page callbacks may mutate and then throw. Never report zero mutation
      // after entering editor code, including listener registration/cleanup.
      receipt = unknown();
    } finally {
      wrapperActive = false;
      mutations.disconnect();
      try { editor.off("beforeTransaction", guard); }
      finally {
        // Do not overwrite a method the application deliberately replaced.
        // Even if the page freezes the view, any remaining wrapper is inert.
        try {
          if (Object.getOwnPropertyDescriptor(view, "updateState")?.value === wrappedUpdate) {
            if (originalDescriptor) Object.defineProperty(view, "updateState", originalDescriptor);
            else delete view.updateState;
          }
        } finally {
          for (const type of inputEvents) window.removeEventListener(type, interrupted, true);
        }
      }
    }
    const finalTarget = target(root);
    if (receipt.status === "Applied" && (!finalTarget || finalTarget.editor !== editor || finalTarget.view !== view
      || editor.state !== completedState || editor.view !== view || view.updateState !== originalUpdate
      || view.dom !== root || !root.isConnected || root.textContent !== result || view.composing || sourceEvent)) return unknown();
    return receipt;
  }
  window.addEventListener(REQUEST, event => {
    const root = event.target;
    if (!(root instanceof HTMLElement) || busy.has(root) || typeof event.detail !== "string" || event.detail.length > 40000) return;
    let request;
    try { request = JSON.parse(event.detail); } catch (_) { return; }
    if (typeof request.id !== "string" || request.id.length > 64 || !["probe", "capture", "selection", "replace"].includes(request.op)) return;
    busy.add(root);
    let result = null;
    try {
      const current = target(root);
      if (current) {
        if (request.op === "probe") result = {kind: "tiptap", supported: true};
        else if (request.op === "selection") result = selection(current);
        else if (request.op === "capture") result = capture(root, current);
        else result = replace(root, current, request.data || {}, request.id);
      }
    } catch (_) { result = request.op === "replace" ? unknown() : null; }
    finally {
      busy.delete(root);
      root.dispatchEvent(new CustomEvent(RESPONSE, {bubbles: true, detail: JSON.stringify({id: request.id, result})}));
    }
  }, true);
})();
