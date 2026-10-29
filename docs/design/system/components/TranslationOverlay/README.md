# TranslationOverlay

The one surface TypeLay draws on top of other apps: a language capsule that grows into a translation panel above the focused field.

## States
| State | Shape | Bar shows | Tab does |
| --- | --- | --- | --- |
| Idle / already in target language | capsule | `Auto → EN` (grey dot when quiet) | passes to the app |
| Translating / preparing model | panel, skeleton lines | phase word, dot breathes | passes to the app |
| Valid translation | panel | `Tab Accept` | accepts |
| Updating · failed · IME composing · preview only | panel, text in `ink-3` + shimmer | one phase word (failed: amber + retry) | passes to the app |
| Editing | panel, field ring in `accent` | `Tab Back` | returns to the source field |
| Edited, pending | panel | `Edited` in `accent-ink` + `Tab Accept` | accepts the edit |
| Paused | dimmed capsule | pause glyph · `Paused` · `Resume` | passes to the app |

## Rules
- Capsule and panel are one element. Width, height and corner radius are spring-driven (`spring-grow`, `spring-settle`); content is pinned bottom-left so the chip rides the growing edge.
- Anchored to the field's top-right, it grows up and to the left. It never pushes layout and never takes focus. Overlay buttons cancel `pointerdown` so the source field keeps focus.
- Translation text uses `--font-content` with `lang` set to the target language. Size is the user's *Translation size* (13–19px).
- Secondary actions stay out of sight until hover or focus: **Expand** (only when the text overflows) and **More**. More holds Translate Again, Rephrase, Tone (locked after a manual edit), Copy, Add to Glossary (after an edit), Hide for Now, and Don’t Show in ‹app›.
- Clicking the language chip opens a search-first language picker with Swap. Changing direction only changes configuration — it never writes to the field.
- Permission is synchronous: the moment the source changes, `Tab Accept` disappears before any animation runs.

## Consumer provides
The anchor rect of the focused field, the app name for “Don’t show in…”, and the translate callback. Respect `prefers-reduced-motion` (springs snap; no blur or stagger).
