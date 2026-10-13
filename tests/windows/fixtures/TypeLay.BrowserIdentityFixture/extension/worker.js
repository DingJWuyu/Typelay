/* config.js exists only in the temporary extension, and is not web-accessible. */
importScripts("config.js");
"use strict";
const records = new Map();
const extensionOrigin = `chrome-extension://${chrome.runtime.id}`;
const bridgeUrl = `${extensionOrigin}/bridge.html`;
const pageUrl = `${PROBE.origin}/fixture.html`;
const challenge = () => Array.from(crypto.getRandomValues(new Uint8Array(32)),
    value => value.toString(16).padStart(2, "0")).join("");
async function report(event) {
    const response = await fetch(`${PROBE.origin}/${PROBE.token}`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(event)
    });
    if (!response.ok) throw new Error("coordinator-unavailable");
}
function identity(record) {
    return { tabId: record.tabId, windowId: record.windowId, documentId: record.documentId };
}
function revoke(record, reason) {
    if (records.get(record.tabId) !== record) return;
    records.delete(record.tabId);
    record.bridge?.disconnect();
    record.top.disconnect();
    void report({ kind: "revoked", ...identity(record), reason }).catch(() => {});
}
async function current(record) {
    if (records.get(record.tabId) !== record) return false;
    const [tab, frame] = await Promise.all([
        chrome.tabs.get(record.tabId), chrome.webNavigation.getFrame({ tabId: record.tabId, frameId: 0 })
    ]);
    return records.get(record.tabId) === record && tab.windowId === record.windowId &&
        frame?.documentId === record.documentId && frame.url === pageUrl;
}
function nativeResult(result, expected) {
    return result && Object.keys(result).length === 8 && result.challenge === expected &&
        typeof result.parentWindow === "string" && /^\d+$/.test(result.parentWindow) &&
        typeof result.rootWindow === "string" && /^\d+$/.test(result.rootWindow) &&
        typeof result.isWindow === "boolean" && Number.isSafeInteger(result.ownerPid) &&
        Number.isSafeInteger(result.hostPid) && typeof result.connectionId === "string" &&
        Number.isSafeInteger(result.lifetimeMs);
}
let baseline;
function workerBaseline() {
    if (baseline) return baseline;
    baseline = new Promise((resolve, reject) => {
        const expected = challenge();
        const port = chrome.runtime.connectNative(PROBE.hostName);
        const timeout = setTimeout(() => { port.disconnect(); reject(new Error("baseline-timeout")); }, 10000);
        let received = false;
        port.onMessage.addListener(async result => {
            if (received || !nativeResult(result, expected)) { port.disconnect(); reject(new Error("baseline-invalid")); return; }
            received = true;
            clearTimeout(timeout);
            try { await report({ kind: "baseline", result }); resolve(); }
            catch (error) { reject(error); }
            finally { port.disconnect(); }
        });
        port.onDisconnect.addListener(() => {
            void chrome.runtime.lastError;
            clearTimeout(timeout);
            if (!received) reject(new Error("baseline-disconnected"));
        });
        port.postMessage({ challenge: expected });
    });
    return baseline;
}
chrome.runtime.onConnect.addListener(port => {
    const sender = port.sender;
    if (sender?.id !== chrome.runtime.id || !sender.tab || !Number.isInteger(sender.tab.windowId) ||
        !Number.isInteger(sender.tab.id) || typeof sender.documentId !== "string" ||
        sender.documentLifecycle !== "active") { port.disconnect(); return; }
    const tabId = sender.tab.id;
    if (port.name === "identity-top") {
        if (sender.frameId !== 0 || sender.url !== pageUrl || sender.origin !== PROBE.origin) { port.disconnect(); return; }
        if (records.has(tabId)) { revoke(records.get(tabId), "duplicate-top"); port.disconnect(); return; }
        const record = { top: port, tabId, windowId: sender.tab.windowId, documentId: sender.documentId, phase: "top" };
        records.set(tabId, record);
        port.onDisconnect.addListener(() => revoke(record, "top-disconnected"));
        port.onMessage.addListener(async message => {
            try {
                if (!await current(record)) { revoke(record, "document-changed"); return; }
                if (message.kind === "ready" && record.phase === "top" && message.pageFocused === true &&
                    ["INPUT", "TEXTAREA"].includes(message.fieldKind)) {
                    record.phase = "baseline";
                    await workerBaseline();
                    if (!await current(record)) { revoke(record, "document-changed"); return; }
                    await report({ kind: "top-ready", ...identity(record), fieldKind: message.fieldKind });
                    if (!await current(record)) { revoke(record, "document-changed"); return; }
                    record.phase = "pending";
                    port.postMessage({ kind: "attach" });
                } else if (message.kind === "inserted") {
                    await report({ kind: "inserted", ...identity(record), pageFocused: message.pageFocused === true,
                        focusUnchanged: message.focusUnchanged === true });
                } else if (message.kind === "focus" && record.phase === "bound") {
                    await report({ kind: "focus", ...identity(record), pageFocused: message.pageFocused === true,
                        focusUnchanged: message.focusUnchanged === true, fieldKind: message.fieldKind });
                } else if (message.kind === "not-focused") {
                    revoke(record, "field-not-focused");
                }
            } catch { revoke(record, "top-check-failed"); }
        });
        return;
    }
    if (port.name !== "identity-bridge" || sender.origin !== extensionOrigin || sender.url !== bridgeUrl ||
        !Number.isInteger(sender.frameId) || sender.frameId <= 0) { port.disconnect(); return; }
    const record = records.get(tabId);
    if (!record) { port.disconnect(); return; }
    if (record.phase !== "pending" || record.bridge || sender.tab.windowId !== record.windowId) {
        revoke(record, "unexpected-bridge"); port.disconnect(); return;
    }
    // Reserve synchronously; two frames cannot both pass asynchronous verification.
    record.phase = "verifying";
    record.bridge = port;
    port.onDisconnect.addListener(() => revoke(record, "bridge-disconnected"));
    void (async () => {
        const frame = await chrome.webNavigation.getFrame({ tabId, documentId: sender.documentId });
        if (!await current(record) || frame?.parentFrameId !== 0 || frame.parentDocumentId !== record.documentId ||
            frame.url !== bridgeUrl || frame.documentId !== sender.documentId) {
            revoke(record, "bridge-parent-mismatch"); return;
        }
        record.expected = challenge();
        record.phase = "challenged";
        port.onMessage.addListener(async message => {
            try {
                if (message.kind !== "native" || record.phase !== "challenged" ||
                    !nativeResult(message.result, record.expected)) {
                    revoke(record, "native-invalid-or-replayed"); return;
                }
                // Consume synchronously, before yielding to live-document checks.
                record.phase = "checking-native";
                record.expected = undefined;
                if (!await current(record)) { revoke(record, "document-changed"); return; }
                record.phase = "bound";
                await report({ kind: "iframe", ...identity(record), frameId: sender.frameId,
                    bridgeDocumentId: sender.documentId, result: message.result });
                if (!await current(record)) { revoke(record, "document-changed"); return; }
                record.top.postMessage({ kind: "check-focus" });
            } catch { revoke(record, "native-check-failed"); }
        });
        port.postMessage({ kind: "challenge", hostName: PROBE.hostName, challenge: record.expected });
    })().catch(() => revoke(record, "bridge-check-failed"));
});
chrome.webNavigation.onBeforeNavigate.addListener(details => {
    if (details.frameId === 0 && records.has(details.tabId)) revoke(records.get(details.tabId), "navigation");
});
chrome.tabs.onDetached.addListener(tabId => { if (records.has(tabId)) revoke(records.get(tabId), "tab-detached"); });
chrome.tabs.onRemoved.addListener(tabId => { if (records.has(tabId)) revoke(records.get(tabId), "tab-removed"); });
chrome.tabs.onAttached.addListener(tabId => {
    void chrome.tabs.sendMessage(tabId, { kind: "restart" }, { frameId: 0 }).catch(() => {});
});
