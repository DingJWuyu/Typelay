'use strict';

// Runs inside a real disposable VS Code extension host; never supplies a fake
// TextDocument/editor.edit implementation or a translation model substitute.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vscode = require('vscode');
const extensionPath = vscode.extensions.getExtension('typelay-local.typelay-editor')?.extensionPath;
assert.ok(extensionPath, 'TypeLay must be available in this isolated extension host');
const { EditorTransaction } = require(path.join(extensionPath, 'transaction'));

async function editor(text = '你好 🌍\n保留原文', language = 'plaintext') {
    const document = await vscode.workspace.openTextDocument({ content: text, language });
    return vscode.window.showTextDocument(document, { preview: false });
}

async function runTests() {
    const extension = vscode.extensions.getExtension('typelay-local.typelay-editor');
    assert.ok(extension, 'The real optional TypeLay extension must load');
    await extension.activate();
    const commands = await vscode.commands.getCommands(true);
    assert.ok(commands.includes('typelay.translateSelection'));
    assert.ok(commands.includes('typelay.translateDocument'));
    const source = await editor();
    source.selection = new vscode.Selection(0, 0, 0, 2);
    let transaction = new EditorTransaction(source);
    let snapshot = transaction.snapshot();
    assert.equal(snapshot.text, '你好', 'Only explicitly selected content crosses the model seam');
    assert.deepEqual(snapshot.selection, { start: 0, length: 2 });
    assert.equal(await transaction.apply({ ...snapshot, translation: 'Hello' }), 'Applied');
    assert.equal(source.document.getText(), 'Hello 🌍\n保留原文');
    assert.equal(await transaction.apply({ ...snapshot, translation: 'Again' }), 'RejectedBeforeMutation');
    transaction.dispose();
    console.log('PASS selected UTF-16 replacement and repeated acceptance refusal');
    await vscode.commands.executeCommand('undo');
    assert.equal(source.document.getText(), '你好 🌍\n保留原文', 'One undo restores the original document');
    await vscode.commands.executeCommand('redo');
    assert.equal(source.document.getText(), 'Hello 🌍\n保留原文');
    console.log('PASS single undo and redo');

    transaction = new EditorTransaction(source, { wholeDocument: true });
    snapshot = transaction.snapshot();
    await source.edit(builder => builder.insert(new vscode.Position(0, 0), '新输入'));
    assert.equal(await transaction.apply({ ...snapshot, translation: 'Old result' }), 'RejectedBeforeMutation');
    assert.equal(source.document.getText(), '新输入Hello 🌍\n保留原文');
    transaction.dispose();

    transaction = new EditorTransaction(source, { wholeDocument: true });
    snapshot = transaction.snapshot();
    source.selection = new vscode.Selection(1, 0, 1, 2);
    assert.equal(await transaction.apply({ ...snapshot, translation: 'Wrong range' }), 'RejectedBeforeMutation');
    transaction.dispose();

    transaction = new EditorTransaction(source, { wholeDocument: true });
    snapshot = transaction.snapshot();
    const other = await editor('另一个文档');
    assert.equal(await transaction.apply({ ...snapshot, translation: 'Wrong editor' }), 'RejectedBeforeMutation');
    assert.equal(other.document.getText(), '另一个文档');
    transaction.dispose();

    transaction = new EditorTransaction(other, { wholeDocument: true });
    snapshot = transaction.snapshot();
    assert.equal(await transaction.apply({ ...snapshot, documentId: 'wrong-id', translation: 'Wrong identity' }), 'RejectedBeforeMutation');
    transaction.dispose();

    transaction = new EditorTransaction(other, { wholeDocument: true });
    snapshot = transaction.snapshot();
    assert.equal(await transaction.apply({ ...snapshot, selection: { start: 1, length: 1 }, translation: 'Wrong scope' }), 'RejectedBeforeMutation');
    transaction.dispose();

    transaction = new EditorTransaction(other, { wholeDocument: true });
    snapshot = transaction.snapshot();
    assert.equal(await transaction.apply({ ...snapshot, translation: '\ud800' }), 'RejectedBeforeMutation');
    assert.equal(other.document.getText(), '另一个文档');
    transaction.dispose();

    const whole = await editor('第一行\n第二行');
    whole.selection = new vscode.Selection(0, 0, 0, 1);
    transaction = new EditorTransaction(whole, { wholeDocument: true });
    snapshot = transaction.snapshot();
    assert.deepEqual(snapshot.selection, { start: 0, length: 7 });
    assert.equal(await transaction.apply({ ...snapshot, translation: 'First\nSecond' }), 'Applied');
    assert.equal(whole.document.getText(), 'First\nSecond');
    transaction.dispose();

    const closing = await editor('即将关闭');
    transaction = new EditorTransaction(closing, { wholeDocument: true });
    snapshot = transaction.snapshot();
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    assert.equal(await transaction.apply({ ...snapshot, translation: 'Stale closed target' }), 'RejectedBeforeMutation');
    transaction.dispose();

    const race = await editor('原文');
    transaction = new EditorTransaction(race, { wholeDocument: true });
    snapshot = transaction.snapshot();
    const first = race.edit(builder => builder.insert(new vscode.Position(0, 0), '并发'));
    const second = transaction.apply({ ...snapshot, translation: 'Stale asynchronous result' });
    assert.equal(await first, true);
    assert.equal(await second, 'RejectedBeforeMutation');
    assert.equal(race.document.getText(), '并发原文', 'Renderer version guard preserves intervening input');
    console.log('PASS renderer document-version race refusal');
    transaction.dispose();

    const emptySelection = await editor('需要明确选择整文');
    assert.throws(() => new EditorTransaction(emptySelection), /unsupported-editor/);
    const code = await editor('const secret = 1;', 'javascript');
    assert.throws(() => new EditorTransaction(code), /unsupported-editor/);
    const large = await editor('字'.repeat(8193));
    assert.throws(() => new EditorTransaction(large, { wholeDocument: true }), /invalid-text/);
    if (process.env.TYPELAY_EDITOR_HOST) {
        const modelSource = await editor('你好，世界。\n保留原文');
        modelSource.selection = new vscode.Selection(0, 0, 0, 6);
        let changedBeforeConfirmation = false;
        const observation = vscode.workspace.onDidChangeTextDocument(event => {
            if (event.document === modelSource.document && !fs.existsSync(process.env.TYPELAY_VSCODE_CONFIRM)) {
                changedBeforeConfirmation = true;
            }
        });
        try { await vscode.commands.executeCommand('typelay.translateSelection'); }
        finally { observation.dispose(); }
        assert.equal(changedBeforeConfirmation, false, 'Preview must not mutate the source before actual UI confirmation');
        assert.notEqual(modelSource.document.lineAt(0).text, '你好，世界。');
        assert.match(modelSource.document.lineAt(0).text, /[A-Za-z]/);
        assert.equal(modelSource.document.lineAt(1).text, '保留原文');
        await vscode.commands.executeCommand('undo');
        assert.equal(modelSource.document.getText(), '你好，世界。\n保留原文');
        console.log('PASS real command, model, preview confirmation, selected replacement and undo');

        modelSource.selection = new vscode.Selection(0, 0, 0, 1);
        await vscode.commands.executeCommand('typelay.translateDocument');
        assert.equal(modelSource.document.getText(), '你好，世界。\n保留原文', 'Canceling the whole-document preview must leave source unchanged');
        console.log('PASS explicit whole-document choice and actual preview cancellation');
    }
    console.log('TypeLay VS Code real editor transaction tests passed.');
}

async function run() {
    const resultPath = process.env.TYPELAY_VSCODE_RESULT;
    assert.ok(resultPath && path.isAbsolute(resultPath), 'The isolated runner must provide a result path');
    const report = (status, detail = '') => fs.writeFileSync(resultPath,
        JSON.stringify({ status, detail, vscodeVersion: vscode.version, modelHostTest: Boolean(process.env.TYPELAY_EDITOR_HOST) }), { encoding: 'utf8' });
    report('started');
    try {
        await runTests();
        // Only the real extension host reaching the end of every assertion can
        // produce this evidence; CLI exit codes and stdout are not sufficient.
        report('passed');
    } catch (error) {
        report('failed', String(error.stack || error));
        throw error;
    }
}

module.exports = { run };
