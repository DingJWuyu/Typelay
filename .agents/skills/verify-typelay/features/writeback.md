# 接受、编辑与直接替换

用户明确接受译文后，TypeLay 才尝试写回原字段。人工稿的第一次接受只回源，第二次独立接受才可能写入。直接替换始终绑定触发时的字段。

## Sub-features

- `writeback/accept` 在当前资格有效时用 Tab 接受，并观察原字段只发生一次替换。
- `writeback/draft` 编辑译文后回源零写，再独立接受人工稿。
- `writeback/direct` 通过直接替换快捷键或 CLI 操作原字段。
- `writeback/refusal` 在输入法未知、源文改变或目标变化时拒绝不安全写入。
- `writeback/undo` 与目标控件自己的原生替换和撤销行为比较。

## How to get to it (user POV)

- 在设置的通用页明确开启默认关闭的“原生输入框替换”实验选项。
- 在已连接的普通字段输入，看到当前译文后按接受键，默认 Tab。
- 点击浮层译文进入编辑，第一次接受回源，释放整组按键后再独立接受。
- 使用保存的直接替换快捷键，默认 `Ctrl+Shift+F10`，或 niri 绑定的 `typelay --direct`。

## Driving it with private niri

Preconditions: 对本次合成字段有明确写回验收意图，实验选项通过真实设置 UI 开启，Fcitx 提供已确认状态。缺少任意条件时保持拒绝。

- **接受。** 在私有旅程中按预览文件打开普通 GTK 字段，记录实际译文、源文和选区。向 `DISPLAY="$TYPELAY_VERIFY_XDISPLAY"` 投递 `xdotool key --clearmodifiers Tab`。检查同一源字段变为该次候选，保存前后 AT-SPI 文本与截图。只看到浮层关闭不能判为接受成功。
- **人工稿。** 通过浮层可编辑 TEXT 控件的公开焦点接口进入编辑，再用私有 XTest 输入合成修改。第一次 Tab 后检查源文仍原样，第二次独立 Tab 后检查人工稿写回，分别保存证据。
- **直接入口。** 在原字段聚焦时执行 `"$TYPELAY_VERIFY_PACKAGE/typelay" --direct`，另一次独立运行投递 `ctrl+shift+F10`。操作前冻结原字段身份与正文，操作后检查该字段与其它字段，不能用后来聚焦的字段代替原目标。
- **既有原生合同。** 在私有会话里执行 `DISPLAY="$TYPELAY_VERIFY_XDISPLAY" /usr/bin/python3 tests/linux/native_e2e.py --inside --gtk-refusal --native "$PWD/artifacts/linux/native" --output "$TYPELAY_VERIFY_OUTPUT/refusal"`，先创建输出目录。这条受控链验证 Unknown 拒绝与按键透传，不能算成功写回。
- **真实合作回归。** 正式模型安装后，可运行 `native_e2e.py --inside --desktop --desktop-scope gtk`，提供私有 `--package`、`--fcitx-cooperative "$TYPELAY_VERIFY_COOPERATIVE"`、`--native "$PWD/artifacts/linux/native"` 和新的 `--output`，并设置私有 DISPLAY。该入口使用受控 GTK fixture 与初始化设置，保留其候选、Verified 回执和计数判据，单独标明证据边界。
- **Windows 合作浏览器。** 本机只按 [Windows 证据](../references/windows.md)读取 Actions。对应入口覆盖默认 Index、人工稿两次独立 Tab 和原字段直接替换，不能用 Linux 旅程替代。
- **浏览器草稿传输。** 在私有会话运行 `tests/linux/browser_e2e.py`，检查暂时不完整的 UTF-16 编辑仍逐次传递，补齐 emoji 后保留完整稿。结合共享 Protocol 与 Core 回归，检查非法载荷被拒、非法最终稿不能写入。该受控 DOM 契约不替代 Windows 的真实 Index 旅程。
- **窄浏览器浮层。** 同一 DOM 契约在 180、320 和 520 像素宽度、亮色与暗色下保存编辑截图。检查“编辑中”完整显示在一行，语言、操作与回源提示均在浮层内；Windows 仍须核对真实人工稿场景截图。

## Gotchas

- 基础 Launch 的实验写回保持默认关闭，因此 `gtk-preview.py` 明确记录 `writebackTested=false`。不能把预览通过升级为写回通过。
- 可见译文、`ValidTranslation` 与短期接受许可不同。资格失效时不得重复投递直到“碰巧通过”，也不得放宽输入法状态或焦点判断。
- GIMP 3.2.6-2 的 Comment 不提供评论修改的原生 Undo。既有 `ordinary-gimp` 会比较原生粘贴轨迹，这种不支持不能报告成 TypeLay 撤销成功。
- 合成键盘、受控 fixture 与物理输入法各有边界。本机未执行物理输入法验收。
- Windows 逐 UTF-16 单元输入 emoji 时，编辑器可能短暂持有孤立代理码元。验收保留原输入和完整稿，核对第一次 Tab 前的浮层仍可见，回源零写与下一次独立接受分别有证据。不能删除 emoji 或改换输入入口来绕过断连。
