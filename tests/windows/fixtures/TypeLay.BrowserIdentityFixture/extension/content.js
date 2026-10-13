/* Isolated-world script: never reads a field value, page command, or postMessage. */
"use strict";
let port;
let bridge;
let focusBefore;
function start() {
    if (port || window !== window.top) return;
    port = chrome.runtime.connect({ name: "identity-top" });
    const current = port;
    current.onDisconnect.addListener(() => {
        if (port !== current) return;
        bridge?.remove();
        bridge = undefined;
        port = undefined;
    });
    current.onMessage.addListener(message => {
        if (message.kind === "attach" && !bridge) {
            focusBefore = document.activeElement;
            const pageFocused = document.hasFocus();
            bridge = document.createElement("iframe");
            bridge.src = chrome.runtime.getURL("bridge.html");
            bridge.hidden = true;
            bridge.tabIndex = -1;
            bridge.setAttribute("aria-hidden", "true");
            document.documentElement.append(bridge);
            current.postMessage({ kind: "inserted", pageFocused,
                focusUnchanged: document.activeElement === focusBefore });
        } else if (message.kind === "check-focus") {
            current.postMessage({ kind: "focus", pageFocused: document.hasFocus(),
                focusUnchanged: document.activeElement === focusBefore,
                fieldKind: ["INPUT", "TEXTAREA"].includes(document.activeElement?.tagName)
                    ? document.activeElement.tagName : "other" });
        }
    });
    // Readiness is an observed condition, not an arbitrary browser startup sleep.
    const deadline = performance.now() + 10000;
    function ready() {
        if (port !== current) return;
        const field = ["INPUT", "TEXTAREA"].includes(document.activeElement?.tagName);
        if (field && document.hasFocus()) {
            current.postMessage({ kind: "ready", pageFocused: true, fieldKind: document.activeElement.tagName });
        } else if (performance.now() < deadline) {
            requestAnimationFrame(ready);
        } else {
            current.postMessage({ kind: "not-focused" });
        }
    }
    ready();
}
chrome.runtime.onMessage.addListener((message, sender) => {
    if (sender.id === chrome.runtime.id && !sender.tab && message.kind === "restart") start();
});
window.addEventListener("pagehide", () => port?.disconnect(), { once: true });
start();
