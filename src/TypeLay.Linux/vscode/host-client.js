'use strict';

const { spawn } = require('node:child_process');
const path = require('node:path');
const os = require('node:os');
const MAX_FRAME = 256 * 1024;

// One owned local process and one source-bound request. No socket listener,
// network endpoint, shell evaluation, workspace configuration, or text logging.
function translateSnapshot(hostPath, snapshot, cancellation) {
    if (process.platform !== 'linux' || typeof hostPath !== 'string'
        || !path.isAbsolute(hostPath)) return Promise.reject(new Error('host-not-configured'));
    const body = Buffer.from(JSON.stringify({ id: 1, operation: 'translateEditorSnapshot', data: snapshot }), 'utf8');
    if (body.length > MAX_FRAME) return Promise.reject(new Error('invalid-frame'));
    const prefix = Buffer.alloc(4);
    prefix.writeUInt32LE(body.length);
    return new Promise((resolve, reject) => {
        let child;
        let finished = false;
        let closed = false;
        let buffer = Buffer.alloc(0);
        let cancellationSubscription;
        const timer = setTimeout(() => finish(new Error('host-timeout')), 215000);
        function finish(error, result) {
            if (finished) return;
            finished = true;
            clearTimeout(timer);
            cancellationSubscription?.dispose();
            buffer = Buffer.alloc(0);
            if (child && !closed) {
                child.stdin.destroy();
                // The detached group contains only our host and its worker.
                try { process.kill(-child.pid, 'SIGTERM'); } catch {}
                const killTimer = setTimeout(() => {
                    if (!closed) {
                        try { process.kill(-child.pid, 'SIGKILL'); } catch {}
                    }
                }, 1000);
                killTimer.unref();
            }
            if (error) reject(error); else resolve(result);
        }
        try {
            child = spawn(hostPath, ['--editor-host'], {
                shell: false, cwd: os.homedir(), detached: true,
                stdio: ['pipe', 'pipe', 'ignore'],
            });
            child.once('error', () => finish(new Error('host-unavailable')));
            child.once('close', () => {
                closed = true;
                if (!finished) finish(new Error('host-stopped'));
            });
            child.stdin.on('error', () => finish(new Error('host-stopped')));
            child.stdout.on('error', () => finish(new Error('host-stopped')));
            child.stdout.on('data', chunk => {
                if (finished) return;
                if (buffer.length + chunk.length > MAX_FRAME + 4) return finish(new Error('invalid-frame'));
                buffer = Buffer.concat([buffer, chunk]);
                if (buffer.length < 4) return;
                const length = buffer.readUInt32LE(0);
                if (!length || length > MAX_FRAME || buffer.length > length + 4) return finish(new Error('invalid-frame'));
                if (buffer.length !== length + 4) return;
                try {
                    const response = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(buffer.subarray(4)));
                    if (response?.id === 1 && ['disabled', 'not-ready', 'no-translation'].includes(response.error)) {
                        return finish(new Error(response.error));
                    }
                    if (!response || response.id !== 1 || response.error || !response.data) {
                        return finish(new Error('translation-unavailable'));
                    }
                    const reply = response.data;
                    if (reply.documentId !== snapshot.documentId || reply.version !== snapshot.version
                        || reply.selection?.start !== snapshot.selection.start
                        || reply.selection?.length !== snapshot.selection.length
                        || typeof reply.translation !== 'string' || !reply.translation.trim()
                        || reply.translation.length > 8192 || reply.translation.includes('\0')) {
                        return finish(new Error('invalid-frame'));
                    }
                    finish(null, reply);
                } catch { finish(new Error('invalid-frame')); }
            });
            cancellationSubscription = cancellation?.onCancellationRequested(() => finish(new Error('cancelled')));
            if (cancellation?.isCancellationRequested) return finish(new Error('cancelled'));
            child.stdin.end(Buffer.concat([prefix, body]));
        } catch { finish(new Error('host-unavailable')); }
    });
}

module.exports = { translateSnapshot };
