'use strict';

const vscode = require('vscode');
const { randomUUID } = require('node:crypto');
const MAX_TEXT_UNITS = 8192;

// An explicit document command, not an automatic input/IME backend. Never maps
// editor change events to an asserted composition state.
class EditorTransaction {
    constructor(editor, { wholeDocument = false } = {}) {
        if (!editor || !vscode.window.state.focused || editor.document.isClosed
            || !['plaintext', 'markdown'].includes(editor.document.languageId)
            || !['file', 'untitled'].includes(editor.document.uri.scheme)
            || editor.selections.length !== 1 || (editor.selection.isEmpty && !wholeDocument)) {
            throw new Error('unsupported-editor');
        }
        const document = editor.document;
        const text = document.getText();
        if (!text.trim() || text.length > MAX_TEXT_UNITS || text.includes('\0')) {
            throw new Error('invalid-text');
        }
        this.editor = editor;
        this.document = document;
        this.selection = editor.selection;
        this.version = document.version;
        this.text = text;
        this.valid = true;
        this.used = false;
        this.documentId = randomUUID();
        const start = this.selection.isEmpty ? 0 : document.offsetAt(this.selection.start);
        const end = this.selection.isEmpty ? text.length : document.offsetAt(this.selection.end);
        this.scope = Object.freeze({ start, length: end - start });
        this.subscriptions = [
            vscode.workspace.onDidChangeTextDocument(event => {
                if (event.document === document) this.valid = false;
            }),
            vscode.workspace.onDidCloseTextDocument(closed => {
                if (closed === document) this.valid = false;
            }),
            vscode.window.onDidChangeActiveTextEditor(active => {
                if (active !== editor) this.valid = false;
            }),
            vscode.window.onDidChangeTextEditorSelection(event => {
                if (event.textEditor === editor && (event.selections.length !== 1
                    || !event.selections[0].anchor.isEqual(this.selection.anchor)
                    || !event.selections[0].active.isEqual(this.selection.active))) this.valid = false;
            }),
            vscode.window.onDidChangeWindowState(state => {
                if (!state.focused) this.valid = false;
            }),
        ];
    }

    sameSelection() {
        return this.editor.selections.length === 1
            && this.editor.selection.anchor.isEqual(this.selection.anchor)
            && this.editor.selection.active.isEqual(this.selection.active);
    }

    snapshot() {
        return {
            documentId: this.documentId,
            version: this.version,
            selection: { ...this.scope },
            text: this.text.slice(this.scope.start, this.scope.start + this.scope.length),
            languageId: this.document.languageId,
        };
    }

    async apply(reply) {
        if (this.used) return 'RejectedBeforeMutation';
        this.used = true;
        if (!reply || reply.documentId !== this.documentId || reply.version !== this.version
            || reply.selection?.start !== this.scope.start || reply.selection?.length !== this.scope.length
            || typeof reply.translation !== 'string' || !reply.translation.trim()
            || reply.translation.includes('\0')) return 'RejectedBeforeMutation';
        const replacement = reply.translation.replace(/\r\n|\r|\n/g,
            this.document.eol === vscode.EndOfLine.CRLF ? '\r\n' : '\n');
        const result = this.text.slice(0, this.scope.start) + replacement
            + this.text.slice(this.scope.start + this.scope.length);
        if (result.length > MAX_TEXT_UNITS || !this.valid || this.document.isClosed
            || !vscode.window.state.focused || vscode.window.activeTextEditor !== this.editor
            || this.editor.document !== this.document || this.document.version !== this.version
            || this.document.getText() !== this.text || !this.sameSelection()) {
            return 'RejectedBeforeMutation';
        }
        const range = new vscode.Range(this.document.positionAt(this.scope.start),
            this.document.positionAt(this.scope.start + this.scope.length));
        // No await between the source/version checks and editor.edit. VS Code
        // carries the document version with this transaction to the renderer.
        try {
            const applied = await this.editor.edit(builder => builder.replace(range, replacement),
                { undoStopBefore: true, undoStopAfter: true });
            return applied ? 'Applied' : 'RejectedBeforeMutation';
        } catch {
            // Never retry or attempt a compensating edit after an uncertain IPC result.
            return 'OutcomeUnknown';
        }
    }

    dispose() {
        this.valid = false;
        for (const subscription of this.subscriptions) subscription.dispose();
        this.subscriptions = [];
        this.text = '';
    }
}

module.exports = { EditorTransaction };
