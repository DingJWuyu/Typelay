/* One browser-owned native connection. No page-facing message or network API. */
"use strict";
const api = globalThis.browser || chrome;
const documents = new Map();
const routes = new WeakMap();
const pending = new Map();
let native = null;
let active = null;
let configuration = null;
let lastConnect = -Infinity;
let connectTimer = null;
let nextRequestId = 0;
const nativeDispatch = () => ["native", "source-local"].includes(configuration?.data?.shortcutDispatch);
const routable = port => !!port && (!nativeDispatch() || !!routes.get(port)?.documentId);
function postDocument(connection, port, message) {
  if (routable(port)) postNative(connection, {...message, route: routes.get(port)});
}
function documentFocus(port, focused) {
  if (native && nativeDispatch()) postDocument(native, port, {event: "document-focus", data: focused});
}

function disconnect(connection, close = false) {
  if (native !== connection) return;
  // Our own Port.disconnect() does not notify this end of the port.
  // Retire its identity before closing, including during document navigation.
  native = null;
  configuration = null;
  pending.clear();
  active = null;
  for (const port of documents.values()) {
    try { port.postMessage({op: "disconnect"}); } catch { /* Document already closed. */ }
  }
  if (close) connection.disconnect();
}
function postNative(connection, message) {
  if (native !== connection) return;
  try { connection.postMessage(message); }
  catch { disconnect(connection, true); } // Revoke; never replay a failed request.
}
function connect() {
  if (native || !documents.size) return;
  const remaining = 2000 - (performance.now() - lastConnect);
  if (remaining > 0) {
    if (connectTimer === null) {
      connectTimer = setTimeout(() => {
        connectTimer = null;
        connect();
      }, remaining);
    }
    return;
  }
  if (connectTimer !== null) clearTimeout(connectTimer);
  connectTimer = null;
  lastConnect = performance.now();
  const connection = api.runtime.connectNative("org.typelay.browser");
  native = connection;
  connection.onDisconnect.addListener(() => disconnect(connection));
  connection.onMessage.addListener(message => {
    if (native !== connection) return;
    const data = message.data || {};
    if (message.op === "configure-shortcuts") configuration = message;
    if (["revoke", "configure-shortcuts", "overlay-hide"].includes(message.op)) {
      for (const port of documents.values()) port.postMessage(message);
      if (message.op === "configure-shortcuts" && active) documentFocus(active, true);
      return;
    }
    for (const [id, request] of pending) {
      if (request.expires < Date.now()) pending.delete(id);
    }
    const target = data.target;
    const port = target ? documents.get(target.split(":")[0])
      : data.documentToken ? documents.get(data.documentToken) : active;
    if (!routable(port)) {
      if (message.id) postNative(connection, {id: message.id, data: null});
      return;
    }
    if (message.id) {
      if (pending.size >= 32 || nextRequestId >= Number.MAX_SAFE_INTEGER
        || Array.from(pending.values()).some(request => request.connection === connection
          && request.nativeId === message.id)) {
        postNative(connection, {id: message.id, data: null});
        return;
      }
      // A document Port can outlive its native host. Never reuse a document-side
      // request id after reconnect, even when the next host restarts its counter.
      const id = ++nextRequestId;
      pending.set(id, {nativeId: message.id, connection, port, expires: Date.now() + 1000});
      port.postMessage({...message, id});
      return;
    }
    port.postMessage(message);
  });
}
api.runtime.onConnect.addListener(port => {
  const sender = port.sender;
  if (port.name !== "typelay-document" || sender?.id !== api.runtime.id || !sender.tab
    || sender.frameId !== 0 || !Number.isSafeInteger(sender.tab.id) || sender.tab.id < 0
    || !Number.isSafeInteger(sender.tab.windowId) || sender.tab.windowId < 0) return;
  let scheme;
  try { scheme = new URL(sender.url).protocol; } catch { return; }
  if (!["http:", "https:"].includes(scheme)) return;
  // These values come from the extension Port, never from a page message.
  // Firefox/page dispatch may lack documentId; native dispatch cannot use it.
  const route = {extensionId: sender.id,
    documentId: typeof sender.documentId === "string" && sender.documentId.length > 0
      && sender.documentId.length <= 128 ? sender.documentId : null,
    documentToken: null, tabId: sender.tab.id, windowId: sender.tab.windowId,
    frameId: 0, scheme};
  let documentId = null;
  port.onMessage.addListener(message => {
    if (message.event === "document") {
      if (documentId || typeof message.data !== "string" || !/^[a-f0-9-]{36}$/.test(message.data)
        || documents.has(message.data)) return;
      documentId = message.data;
      routes.set(port, {...route, documentToken: documentId});
      documents.set(documentId, port);
      connect();
      if (configuration) port.postMessage(configuration);
      return;
    }
    if (!documentId) return;
    if (message.event === "focus") {
      if (message.data === true) {
        const previous = active;
        active = port; connect();
        if (previous !== port) {
          if (previous) documentFocus(previous, false);
          documentFocus(port, true);
        }
      } else if (message.data === false && active === port) {
        active = null; documentFocus(port, false);
      }
      return;
    }
    if (!native) return;
    if (message.id) {
      const request = pending.get(message.id);
      if (request?.port !== port || request.connection !== native) return;
      pending.delete(message.id);
      if (request.expires < Date.now()) return;
      postDocument(request.connection, port, {...message, id: request.nativeId});
      return;
    }
    postDocument(native, port, message);
  });
  port.onDisconnect.addListener(() => {
    if (documentId && native && nativeDispatch())
      postDocument(native, port, {event: "document-closed", data: null});
    if (documentId) documents.delete(documentId);
    if (active === port) active = null;
    for (const [id, owner] of pending) {
      if (owner.port === port) {
        pending.delete(id);
        postDocument(owner.connection, port, {id: owner.nativeId, data: null});
      }
    }
    if (!documents.size) {
      if (connectTimer !== null) clearTimeout(connectTimer);
      connectTimer = null;
      if (native) disconnect(native, true);
    }
  });
});
