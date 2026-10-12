/* Real Tiptap 2.11.5 transaction experiment. Not installed in the extension. */
import {Editor} from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import {closeHistory} from '@tiptap/pm/history';

const initial = '<p>Before <strong>hello world</strong> after <em>untouched</em></p>';
let editor;
function setup() {
  editor?.destroy();
  document.querySelector('#mount').replaceChildren();
  editor = new Editor({element: document.querySelector('#mount'), extensions: [StarterKit], content: initial});
  editor.view.focus();
  editor.commands.setTextSelection({from: 8, to: 13});
  return editor;
}
// Only this tentative function is TypeLay logic. All state, DOM, selection and
// history operations below run in the actual editor and actual browser.
function replace(root, guarded) {
  const target = root.editor;
  if (!target || target.view.dom !== root || target.isDestroyed || !target.isEditable
      || target.view.composing || !root.isConnected) return {status: 'unsupported'};
  const view = target.view, before = target.state;
  const selection = document.getSelection();
  const selected = {anchorNode: selection.anchorNode, anchorOffset: selection.anchorOffset,
    focusNode: selection.focusNode, focusOffset: selection.focusOffset};
  const transaction = closeHistory(before.tr.insertText('bonjour', 8, 13));
  transaction.setMeta('typelay-probe', true);
  const abort = new Error('source-changed');
  const mutations = new MutationObserver(() => {});
  mutations.observe(root, {subtree: true, childList: true, characterData: true, attributes: true});
  const guard = ({transaction: current}) => {
    if (current === transaction && (target.state !== before || target.view !== view
        || view.dom !== root || !root.isConnected || view.composing
        || document.activeElement !== root
        || selection.anchorNode !== selected.anchorNode || selection.anchorOffset !== selected.anchorOffset
        || selection.focusNode !== selected.focusNode || selection.focusOffset !== selected.focusOffset
        || mutations.takeRecords().length)) throw abort;
  };
  if (guarded) target.on('beforeTransaction', guard);
  try {
    view.dispatch(transaction);
    if (!target.state.doc.eq(transaction.doc)) return {status: 'unknown'};
    return {status: 'written'};
  } catch (error) {
    if (error === abort) return {status: 'source-changed'};
    throw error;
  } finally {
    if (guarded) target.off('beforeTransaction', guard);
    mutations.disconnect();
  }
}
window.editorProbe = kind => {
  const editor = setup(), root = editor.view.dom;
  const other = root.querySelector('em');
  const before = editor.getHTML();
  let reentered = false;
  if (kind.includes('reentrant') || kind === 'listener-change') {
    editor.on('beforeTransaction', ({transaction}) => {
      if (!transaction.getMeta('typelay-probe') || reentered) return;
      reentered = true;
      if (kind === 'listener-change') {
        editor.off('beforeTransaction');
        editor.on('beforeTransaction', () => {});
      }
      // A normal application callback makes a new document transaction. It
      // does not mutate a fake state or bypass ProseMirror's apply operation.
      editor.view.dispatch(closeHistory(editor.state.tr.insertText('NEW ', 1)));
    });
  }
  if (kind === 'selection-change') {
    editor.on('beforeTransaction', ({transaction}) => {
      if (transaction.getMeta('typelay-probe')) editor.commands.setTextSelection(1);
    });
  }
  if (kind === 'dom-change') {
    editor.on('beforeTransaction', ({transaction}) => {
      if (transaction.getMeta('typelay-probe')) root.querySelector('strong').firstChild.data = 'NEW world';
    });
  }
  if (kind === 'dom-selection-change') {
    editor.on('beforeTransaction', ({transaction}) => {
      if (transaction.getMeta('typelay-probe')) document.getSelection().collapse(root.firstChild.firstChild, 0);
    });
  }
  if (kind === 'composing') root.dispatchEvent(new CompositionEvent('compositionstart', {bubbles: true}));
  const result = replace(root, kind !== 'reentrant-unguarded');
  const domAfter = root.textContent;
  const after = editor.getHTML();
  const selection = {from: editor.state.selection.from, to: editor.state.selection.to};
  const preserved = root.querySelector('em') === other;
  editor.commands.undo();
  const undone = editor.getHTML();
  const undoSelection = {from: editor.state.selection.from, to: editor.state.selection.to};
  editor.commands.redo();
  return {...result, before, after, domAfter, selection, preserved, undone, undoSelection, redone: editor.getHTML(), reentered};
};
