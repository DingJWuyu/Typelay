/* Isolated-world bounded bridge to page-owned Tiptap transactions. A page can
 * interfere with its own editor; it never receives a native port, host command,
 * approval token, another document's data, or the final-guard closure. */
(() => {
  "use strict";
  const REQUEST = "typelay-editor-request", RESPONSE = "typelay-editor-response";
  const CHECK = "typelay-editor-check", CHECK_RESULT = "typelay-editor-check-result";
  const textValue = Object.getOwnPropertyDescriptor(Node.prototype, "textContent").get;
  const pending = new WeakSet();
  function root(element) {
    if (!(element instanceof HTMLElement)) return null;
    const candidate = element.closest(".ProseMirror[contenteditable='true']");
    return candidate?.getRootNode() === document ? candidate : null;
  }
  function length(node) {
    if (node.nodeType === Node.TEXT_NODE) return node.length;
    let size = 0;
    for (const child of node.childNodes) size += length(child);
    return size;
  }
  function offset(element, node, position) {
    if (!node || (node !== element && !element.contains(node))) return null;
    let value = 0;
    if (node.nodeType === Node.TEXT_NODE) {
      if (position < 0 || position > node.length) return null;
      value = position;
    } else {
      if (position < 0 || position > node.childNodes.length) return null;
      for (let i = 0; i < position; i++) value += length(node.childNodes[i]);
    }
    while (node !== element) {
      for (let previous = node.previousSibling; previous; previous = previous.previousSibling) value += length(previous);
      node = node.parentNode;
      if (!node) return null;
    }
    return value;
  }
  function domSelection(element) {
    const selected = document.getSelection();
    if (!selected) return null;
    const start = offset(element, selected.anchorNode, selected.anchorOffset);
    const end = offset(element, selected.focusNode, selected.focusOffset);
    return start === null || end === null ? null : {start: Math.min(start, end), end: Math.max(start, end)};
  }
  function read(element) {
    if (!element.isConnected || length(element) > 8192) return null;
    return textValue.call(element);
  }
  function invoke(element, op, data, finalGuard) {
    if (root(element) !== element || pending.has(element)) return null;
    const id = crypto.randomUUID();
    let responses = 0, result = null, guardCalled = false, guardAllowed = false;
    const receive = event => {
      if (event.target !== element || typeof event.detail !== "string" || event.detail.length > 40000) return;
      try {
        const reply = JSON.parse(event.detail);
        if (reply.id !== id) return;
        responses++; result = reply.result;
      } catch (_) { /* Replies are page-controlled data. */ }
    };
    const verify = event => {
      if (event.target !== element || typeof event.detail !== "string" || event.detail.length > 256) return;
      let message;
      try { message = JSON.parse(event.detail); } catch (_) { return; }
      if (message.id !== id) return;
      let allowed = false;
      if (!guardCalled && op === "replace" && typeof finalGuard === "function") {
        guardCalled = true;
        try { allowed = finalGuard() === true; } catch (_) { allowed = false; }
      }
      guardAllowed = allowed;
      if (!allowed) event.preventDefault();
      element.dispatchEvent(new CustomEvent(CHECK_RESULT, {bubbles: true, detail: JSON.stringify({id, allowed})}));
    };
    pending.add(element);
    window.addEventListener(RESPONSE, receive, true);
    window.addEventListener(CHECK, verify, true);
    try {
      element.dispatchEvent(new CustomEvent(REQUEST, {bubbles: true, detail: JSON.stringify({id, op, data})}));
      if (responses !== 1) return op === "replace" ? {status: "OutcomeUnknown", reason: "UnknownOutcome"} : null;
      if (op === "replace" && result?.status === "Applied") {
        const selected = domSelection(element);
        if (!guardCalled || !guardAllowed || read(element) !== result.text
          || document.activeElement === element && (!selected || selected.start !== result.selection?.start || selected.end !== result.selection?.end)) {
          return {status: "OutcomeUnknown", reason: "UnknownOutcome"};
        }
      }
      return result;
    } finally {
      pending.delete(element);
      window.removeEventListener(RESPONSE, receive, true);
      window.removeEventListener(CHECK, verify, true);
    }
  }
  globalThis.TypeLayEditorBridge = Object.freeze({
    root,
    selectionIdentity(element) {
      const selected = document.getSelection();
      if (!selected || (!element.contains(selected.anchorNode) && !element.contains(selected.focusNode))) return null;
      return {anchorNode: selected.anchorNode, anchorOffset: selected.anchorOffset,
        focusNode: selected.focusNode, focusOffset: selected.focusOffset};
    },
    probe: element => invoke(element, "probe"),
    // DOM offsets count UTF-16 units without reading source characters. A blurred
    // editor can retain a model selection after the document selection moves away.
    selection: element => domSelection(element) || invoke(element, "selection"),
    read,
    capture(element) {
      const result = invoke(element, "capture");
      const selected = domSelection(element);
      if (!result || typeof result.version !== "string" || result.version.length > 64
        || typeof result.text !== "string" || result.text.length > 8192 || read(element) !== result.text
        || !Number.isInteger(result.selection?.start) || !Number.isInteger(result.selection?.end)
        || result.selection.start < 0 || result.selection.end < result.selection.start || result.selection.end > result.text.length
        || document.activeElement === element && (!selected || selected.start !== result.selection.start || selected.end !== result.selection.end)) return null;
      return result;
    },
    replace: (element, data, finalGuard) => invoke(element, "replace", data, finalGuard),
  });
})();
