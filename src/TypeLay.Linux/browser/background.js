/* One browser-owned native connection. No page-facing message or network API. */
"use strict";
const api = globalThis.browser || chrome;
const documents = new Map();
const pending = new Map();
let native = null;
let active = null;
let configuration = null;
let lastConnect = 0;

function disconnect(connection, close = false) {
  if (native !== connection) return;
  // Our own Port.disconnect() does not notify this end of the port.
  // Retire its identity before closing, including during document navigation.
  native = null;
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
  if (native || Date.now() - lastConnect < 2000) return;
  lastConnect = Date.now();
  const connection = api.runtime.connectNative("org.typelay.browser");
  native = connection;
  connection.onDisconnect.addListener(() => disconnect(connection));
  connection.onMessage.addListener(message => {
    if (native !== connection) return;
    const data = message.data || {};
    if (message.op === "configure-shortcuts") configuration = message;
    if (["revoke", "configure-shortcuts", "overlay-hide"].includes(message.op)) {
      for (const port of documents.values()) port.postMessage(message);
      return;
    }
    for (const [id, request] of pending) {
      if (request.expires < Date.now()) pending.delete(id);
    }
    const target = data.target;
    const port = target ? documents.get(target.split(":")[0]) : active;
    if (!port) {
      if (message.id) postNative(connection, {id: message.id, data: null});
      return;
    }
    if (message.id) {
      if (pending.size >= 32) {
        postNative(connection, {id: message.id, data: null});
        return;
      }
      pending.set(message.id, {port, expires: Date.now() + 1000});
    }
    port.postMessage(message);
  });
}
api.runtime.onConnect.addListener(port => {
  if (port.name !== "typelay-document" || !port.sender?.tab || port.sender.frameId !== 0) return;
  let documentId = null;
  port.onMessage.addListener(message => {
    if (message.event === "document") {
      if (documentId || typeof message.data !== "string" || !/^[a-f0-9-]{36}$/.test(message.data)) return;
      documentId = message.data;
      documents.set(documentId, port);
      connect();
      if (configuration) port.postMessage(configuration);
      return;
    }
    if (!documentId) return;
    if (message.event === "focus") {
      if (message.data === true) { active = port; connect(); }
      else if (active === port) active = null;
      return;
    }
    if (!native) return;
    if (message.id) {
      if (pending.get(message.id)?.port !== port) return;
      pending.delete(message.id);
    }
    postNative(native, message);
  });
  port.onDisconnect.addListener(() => {
    if (documentId) documents.delete(documentId);
    if (active === port) active = null;
    for (const [id, owner] of pending) {
      if (owner.port === port) {
        pending.delete(id);
        if (native) postNative(native, {id, data: null});
      }
    }
    if (!documents.size && native) disconnect(native, true);
  });
});
