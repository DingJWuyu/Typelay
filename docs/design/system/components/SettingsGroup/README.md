# SettingsGroup

A macOS-style grouped card of settings rows: label on the left, one control on the right.

- One row, one idea, one control: switch, segmented control, popup, button or keycap. No helper text under labels; if a setting needs explaining, split or rename it.
- Rows are 44px tall with 14px side padding and a hairline (`line`) separator inset 14px from the left. Groups sit 18px apart. A group title (12px, `ink-2`) is used only when a page holds several groups of different kinds.
- Changes apply immediately. There is no Save button; a failed write shows one banner with Retry at the top of the page and leaves the control showing the old value.
- A hero row (72px) leads a page about one thing: an app icon, a name, a status dot plus status word, and the master control.
- Keycaps record on click (accent ring pulses). Invalid keys shake, and the row shows one amber line underneath. A conflict with another app offers **Use Anyway** — the PRD lets people force-save.
