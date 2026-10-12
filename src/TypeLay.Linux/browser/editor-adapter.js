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
    const mutations = new MutationObserver(() => {});
    mutations.observe(root, {subtree: true, childList: true, characterData: true, attributes: true});
    let dispatched = false, checked = false;
    const abort = new Error("typelay-source-changed");
    const unchanged = () => editor.state === state && editor.view === view && view.dom === root && root.isConnected
      && editor.isEditable && !view.composing && document.activeElement === active && !mutations.takeRecords().length
      && selected && selected.anchorNode === anchor[0] && selected.anchorOffset === anchor[1]
      && selected.focusNode === anchor[2] && selected.focusOffset === anchor[3];
    const guard = ({transaction: incoming}) => {
      if (incoming !== transaction) return;
      // CHECK is itself observable by the page. Recheck without another event
      // after it returns, so handlers cannot slip an edit behind the last guard.
      if (checked || !unchanged() || !check(root, id) || !unchanged()) throw abort;
      checked = true;
    };
    try {
      editor.on("beforeTransaction", guard);
      dispatched = true;
      view.dispatch(transaction);
      const after = target(root);
      if (!checked || !after || after.editor !== editor || after.view !== view || view.composing
        || !after.state.doc.eq(transaction.doc) || root.textContent !== result
        || after.state.selection.from !== start + replacement.length + 1
        || after.state.selection.to !== start + replacement.length + 1) return unknown();
      return {status: "Applied", text: result, selection: {start: start + replacement.length, end: start + replacement.length},
        version: version(root, after)};
    } catch (_) {
      // A custom dispatcher could commit before invoking a hook or throwing.
      // Never label any failure after entering it as a zero-mutation rejection.
      return dispatched ? unknown() : rejected("SourceChanged");
    } finally {
      mutations.disconnect();
      editor.off("beforeTransaction", guard);
    }
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
