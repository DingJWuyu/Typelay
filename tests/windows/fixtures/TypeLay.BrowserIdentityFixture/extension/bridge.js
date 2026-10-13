/* An extension-origin frame; it accepts no page messages or URL parameters. */
"use strict";
const worker = chrome.runtime.connect({ name: "identity-bridge" });
let native;
let consumed = false;
worker.onMessage.addListener(message => {
    if (message.kind !== "challenge" || consumed || !/^[a-f0-9]{64}$/.test(message.challenge)) {
        worker.disconnect();
        return;
    }
    consumed = true;
    // hostName arrives only over the private worker port, never from the page.
    native = chrome.runtime.connectNative(message.hostName);
    native.onMessage.addListener(result => worker.postMessage({ kind: "native", result }));
    native.onDisconnect.addListener(() => {
        void chrome.runtime.lastError;
        worker.postMessage({ kind: "native-disconnected" });
        worker.disconnect();
    });
    native.postMessage({ challenge: message.challenge });
});
worker.onDisconnect.addListener(() => {
    if (native) {
        native.disconnect();
        native = undefined;
    }
});
