'use strict';

const vscode = require('vscode');
const { EditorTransaction } = require('./transaction');
const { translateSnapshot } = require('./host-client');

function activate(context) {
    let busy = false;
    let activeCancellation;
    context.subscriptions.push({ dispose: () => activeCancellation?.cancel() });
    async function translate(wholeDocument) {
        if (busy) {
            void vscode.window.showInformationMessage('TypeLay：请先完成或取消当前翻译。');
            return { status: 'Busy' };
        }
        const editor = vscode.window.activeTextEditor;
        if (!vscode.workspace.isTrusted || process.platform !== 'linux') {
            void vscode.window.showWarningMessage('TypeLay 编辑器命令仅支持本机 Linux 的受信任工作区。');
            return { status: 'Failed', reason: 'unsupported-environment' };
        }
        if (!editor || (!wholeDocument && editor.selection.isEmpty)) {
            void vscode.window.showInformationMessage('请先选择要翻译的文字，或运行“TypeLay: 翻译整篇文档”。');
            return { status: 'Failed', reason: 'selection-required' };
        }
        // Only a user/machine choice may select executable code. Workspace and
        // folder settings must never choose a program to run on this machine.
        const hostPath = vscode.workspace.getConfiguration('typelay').inspect('hostPath')?.globalValue;
        if (typeof hostPath !== 'string' || !hostPath.startsWith('/')) {
            void vscode.window.showWarningMessage('请在用户设置 typelay.hostPath 中填写已安装 TypeLay 启动器的绝对路径。');
            return { status: 'Failed', reason: 'host-not-configured' };
        }
        busy = true;
        let transaction;
        activeCancellation = new vscode.CancellationTokenSource();
        try {
            if (wholeDocument) {
                const choice = await vscode.window.showInformationMessage('翻译整篇文档？', {
                    modal: true,
                    detail: '请先完成输入法候选。整篇正文只交给本机 TypeLay；翻译后仍需预览并确认，不会自动替换。',
                }, '翻译整篇文档');
                if (choice !== '翻译整篇文档') return { status: 'Cancelled' };
                if (vscode.window.activeTextEditor !== editor) return { status: 'RejectedBeforeMutation', reason: 'StaleObservation' };
            }
            transaction = new EditorTransaction(editor, { wholeDocument });
            const snapshot = transaction.snapshot();
            const reply = await vscode.window.withProgress({
                location: vscode.ProgressLocation.Notification, title: 'TypeLay 正在本机翻译', cancellable: true,
            }, async (_progress, cancellation) => {
                const subscription = cancellation.onCancellationRequested(() => activeCancellation.cancel());
                try { return await translateSnapshot(hostPath, snapshot, activeCancellation.token); }
                finally { subscription.dispose(); }
            });
            if (!transaction.isCurrent()) {
                void vscode.window.showInformationMessage('原文、选区或编辑器已变化，译文已作废，请重新翻译。');
                return { status: 'RejectedBeforeMutation', reason: 'StaleObservation' };
            }
            const choice = await vscode.window.showInformationMessage('TypeLay 翻译预览', {
                modal: true,
                detail: `原文：\n${snapshot.text}\n\n译文：\n${reply.translation}\n\n请核对原文完整，输入法候选已完成。确认只替换本次范围，不会发送或保存文档。`,
            }, '确认替换');
            if (choice !== '确认替换') return { status: 'Cancelled' };
            const status = await transaction.apply(reply);
            if (status === 'RejectedBeforeMutation') {
                void vscode.window.showInformationMessage('原文、选区或编辑器已变化，未执行替换。');
            } else if (status === 'OutcomeUnknown') {
                void vscode.window.showWarningMessage('无法确认编辑结果。请检查原文；TypeLay 不会自动重试。');
            }
            return { status };
        } catch (error) {
            if (error.message !== 'cancelled') {
                const messages = {
                    'unsupported-editor': '仅支持本地纯文本或 Markdown 单选区。代码、终端、远程文档和未知编辑器不读取正文。',
                    'invalid-text': '正文为空、包含无效字符或超过 8192 UTF-16 单位，未翻译。',
                    'disabled': 'TypeLay 当前已暂停。请在 TypeLay 设置中启用后重试。',
                    'not-ready': '本机模型未就绪。请先在 TypeLay 中完成模型安装。',
                    'no-translation': '本机模型没有为当前文本提供译文，原文未修改。',
                };
                const message = messages[error.message]
                    || '本机翻译未完成。请检查 TypeLay 启动器和已安装模型；原文未由此命令替换。';
                void vscode.window.showWarningMessage(`TypeLay：${message}`);
                const reasons = ['unsupported-editor', 'invalid-text', 'disabled', 'not-ready', 'no-translation',
                    'host-not-configured', 'invalid-frame', 'host-timeout', 'host-unavailable', 'host-stopped',
                    'translation-unavailable', 'invalid-request', 'translation-failed', 'host-failed'];
                return { status: 'Failed', reason: reasons.includes(error.message) ? error.message : 'unexpected-error' };
            }
            return { status: 'Cancelled' };
        } finally {
            transaction?.dispose();
            activeCancellation.dispose();
            activeCancellation = undefined;
            busy = false;
        }
    }
    context.subscriptions.push(
        vscode.commands.registerCommand('typelay.translateSelection', () => translate(false)),
        vscode.commands.registerCommand('typelay.translateDocument', () => translate(true)),
    );
}

module.exports = { activate };
