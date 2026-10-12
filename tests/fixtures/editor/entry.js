/* Actual Tiptap application fixture. TypeLay logic is loaded from production. */
import {Editor} from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';

const initial = '<p>Before <strong>hello world</strong> after <em>untouched</em></p>';
let editor, other, reentered;
window.editorProbeSetup = kind => {
  editor?.destroy();
  document.querySelector('#mount').replaceChildren();
  // A long, valid native grouping interval makes adjacent-input history testing
  // independent of cross-world debugging transport latency. No sleeps are used.
  const extensions = kind === 'adjacent' ? [StarterKit.configure({history: {newGroupDelay: 60000}})] : [StarterKit];
  editor = new Editor({element: document.querySelector('#mount'), extensions, content: initial});
  const root = editor.view.dom;
  root.id = 'editor-source';
  editor.view.focus();
  if (kind === 'adjacent') editor.commands.insertContentAt(13, '!');
  editor.commands.setTextSelection({from: 8, to: 13});
  other = root.querySelector('em');
  reentered = false;
  if (kind.includes('reentrant') || kind === 'listener-change') {
    editor.on('beforeTransaction', ({transaction}) => {
      if (!transaction.docChanged || reentered) return;
      reentered = true;
      if (kind === 'listener-change') {
        editor.off('beforeTransaction');
        editor.on('beforeTransaction', () => {});
      }
      editor.view.dispatch(editor.state.tr.insertText('NEW ', 1));
    });
  }
  if (['selection-change', 'dom-change', 'dom-selection-change'].includes(kind)) {
    editor.on('beforeTransaction', ({transaction}) => {
      if (!transaction.docChanged || reentered) return;
      reentered = true;
      if (kind === 'selection-change') editor.commands.setTextSelection(1);
      if (kind === 'dom-change') root.querySelector('strong').firstChild.data = 'NEW world';
      if (kind === 'dom-selection-change') document.getSelection().collapse(root.firstChild.firstChild, 0);
    });
  }
  if (kind === 'bridge-check-change') {
    root.addEventListener('typelay-editor-check', () => {
      if (reentered) return;
      reentered = true;
      editor.view.dispatch(editor.state.tr.insertText('NEW ', 1));
    }, {once: true});
  }
  if (kind === 'composing') root.dispatchEvent(new CompositionEvent('compositionstart', {bubbles: true}));
  return {before: editor.getHTML()};
};
window.editorProbeUnsafe = () => {
  editor.view.dispatch(editor.state.tr.insertText('bonjour', 8, 13));
};
window.editorProbeInspect = () => {
  const root = editor.view.dom;
  return {after: editor.getHTML(), domAfter: root.textContent,
    selection: {from: editor.state.selection.from, to: editor.state.selection.to},
    preserved: root.querySelector('em') === other, reentered};
};
window.editorProbeHistory = () => {
  editor.commands.undo();
  const undone = editor.getHTML();
  const undoSelection = {from: editor.state.selection.from, to: editor.state.selection.to};
  editor.commands.redo();
  return {undone, undoSelection, redone: editor.getHTML()};
};
