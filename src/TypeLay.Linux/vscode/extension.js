'use strict';

// This development-only extension intentionally has no registered commands or
// activation events. Shipping a new command interaction needs explicit product
// approval; extension-host tests exercise the real transaction implementation.
function activate() {}
module.exports = { activate };
