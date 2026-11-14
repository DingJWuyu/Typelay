# 接受、编辑与直接替换

用户明确接受译文后，TypeLay 才尝试写回原字段。人工稿的第一次接受只回源，第二次独立接受才可能写入。直接替换始终绑定触发时的字段。

## Sub-features

- `writeback/gtk3-index` 从空数据目录正式准备默认 Index，完成普通 GTK3 Entry 与 TextView 的预览、人工稿、默认两次 Tab、Unicode 选区和后台 Direct。
- `writeback/accept` 在当前资格有效时用 Tab 接受，并观察原字段只发生一次替换。
- `writeback/draft` 编辑译文后回源零写，再独立接受人工稿。
- `writeback/direct` 通过直接替换快捷键或 CLI 操作原字段。
- `writeback/refusal` 在输入法未知、源文改变或目标变化时拒绝不安全写入。
- `writeback/undo` 与目标控件自己的原生替换和撤销行为比较。
- `writeback/browser-undo-index` 是 [Linux 原生 Undo 必过旅程](undo.md)。它在 Tiptap 中接受真实 Index 候选，再用宿主快捷键撤销和重做。
- `writeback/browser-index` 使用正式默认 Index，验证普通网页的人工稿、两次默认 Tab、UTF-16 选区与后台直接替换。
- `writeback/gtk4-interaction` 在真实 Index 和合作 GTK4 控件中执行成功交互及原生 Undo。

## How to get to it (user POV)

- 在设置的通用页明确开启默认关闭的“原生输入框替换”实验选项。
- 在已连接的普通字段输入，看到当前译文后按接受键，默认 Tab。
- 点击浮层译文进入编辑，第一次接受回源，释放整组按键后再独立接受。
- 使用保存的直接替换快捷键，默认 `Ctrl+Shift+F10`，或 niri 绑定的 `typelay --direct`。

## Driving it with private niri

Preconditions: 对本次合成字段有明确写回验收意图，实验选项通过真实设置 UI 开启，Fcitx 提供已确认状态。缺少任意条件时保持拒绝。

- **GTK3 默认 Index 完整旅程。** 在本技能的私有会话内运行下列现役 runner。`--native` 指向当前源码以 `--with-test-fixture` 构建的 fixture；产品模块从私有正式包加载。`--model-cache` 只复用官方下载缓存，每次会话都在最终数据目录重新正式安装。输出目录须先创建。

  ```bash
  mkdir "$TYPELAY_VERIFY_OUTPUT/gtk-index"
  DISPLAY="$TYPELAY_VERIFY_XDISPLAY" /usr/bin/python3 tests/linux/native_e2e.py \
    --inside --desktop --desktop-scope gtk-index \
    --package "$TYPELAY_VERIFY_PACKAGE" \
    --fcitx-cooperative "$TYPELAY_VERIFY_COOPERATIVE" \
    --native "$PWD/artifacts/linux/native" \
    --model-cache "$TYPELAY_VERIFY_MODEL_CACHE" \
    --output "$TYPELAY_VERIFY_OUTPUT/gtk-index"
  ```

  该入口明确注入初始中英方向、教程完成和实验写回设置，使用受控宿主中的普通 GTK 控件。它通过真实键鼠、Fcitx、生产模块和 Index 执行交互，没有翻译或写入替身。Unicode 人工稿使用 GTK 的 `Ctrl+Shift+U` 组合输入；结构样例使用私有 Wayland 剪贴板的合成正文和真实 `Ctrl+V`，不访问用户桌面的剪贴板。它不替代设置 UI 的用户入口验收。`initial-settings.json`、`installed.json`、`doctor-ready.json` 和 `fixture-identity.json` 保留这些前提。

  检查 `entry-*`、`textview-*` 的预览、编辑、回源和接受截图及 JSON。固定原句为 `Please keep the backup until Friday.`，逐句确认保留备份到周五的意思。TextView 选区外保留 `Before 😀` 和 `After untouched`，精确人工稿包含 Unicode 与换行。首次 Tab 的按住、重复及释放都零写入；第二次独立 Tab 重新观察资格，只产生一条 Verified 回执。`result.json` 保存完整字段、选区、按键时序与实际模型输出。

  `structure-preview.*` 核对既有 Index 生命周期样例 `  Please keep https://example.com\r\nThank you.  `，GTK 粘贴先将 CRLF 正规化为 LF，结果分别记录粘贴输入与实际字段。可见译文须保留当前字段的链接、唯一 LF 与首尾各两个空格，并表达保留和感谢；这不证明模型的 CRLF 保留。全选候选不能改变原 TextView 选区；候选编辑器不自动抢占 PRIMARY，显式复制与粘贴仍可用。

  `direct-background.*` 与结果中的 Direct 记录分别保留原 A、新 B 和写入时的 B 状态。B 的正文、焦点、选区与修改计数必须保持不变。未支持的富文本、嵌入对象与只读字段继续拒绝。检查内部 `process-cleanup.json` 和外层 `cleanup.json`，成功时要求无残留进程、scratch/runtime 已清理、上述证据仍存在。

- **接受。** 在私有旅程中按预览文件打开普通 GTK 字段，记录实际译文、源文和选区。向 `DISPLAY="$TYPELAY_VERIFY_XDISPLAY"` 投递 `xdotool key --clearmodifiers Tab`。检查同一源字段变为该次候选，保存前后 AT-SPI 文本与截图。只看到浮层关闭不能判为接受成功。
- **人工稿。** 通过浮层可编辑 TEXT 控件的公开焦点接口进入编辑，再用私有 XTest 输入合成修改。第一次 Tab 后检查源文仍原样，第二次独立 Tab 后检查人工稿写回，分别保存证据。
- **直接入口。** 在原字段聚焦时执行 `"$TYPELAY_VERIFY_PACKAGE/typelay" --direct`，另一次独立运行投递 `ctrl+shift+F10`。操作前冻结原字段身份与正文，操作后检查该字段与其它字段，不能用后来聚焦的字段代替原目标。
- **既有原生合同。** 在私有会话里执行 `DISPLAY="$TYPELAY_VERIFY_XDISPLAY" /usr/bin/python3 tests/linux/native_e2e.py --inside --gtk-refusal --native "$PWD/artifacts/linux/native" --output "$TYPELAY_VERIFY_OUTPUT/refusal"`，先创建输出目录。这条受控链验证 Unknown 拒绝与按键透传，不能算成功写回。
- **真实合作回归。** 正式模型安装后，可运行 `native_e2e.py --inside --desktop --desktop-scope gtk`，提供私有 `--package`、`--fcitx-cooperative "$TYPELAY_VERIFY_COOPERATIVE"`、`--native "$PWD/artifacts/linux/native"` 和新的 `--output`，并设置私有 DISPLAY。该入口使用受控 GTK fixture 与初始化设置，保留其候选、Verified 回执和计数判据，单独标明证据边界。
- **GTK4 独立交互。** 在同一私有会话通过正式安装器准备模型后，运行下面的现役交互段。`--native` 须包含与产品源码匹配的 GTK4 fixture。该 scope 独立执行预览、接受、编辑回源、选区、原生 Undo、输入法拒绝和后台直接替换；它使用受控字段和初始设置，不证明物理键盘输入。它不执行[GTK4 配置](settings.md#driving-it-with-private-niri)，不能替代默认 `gtk4` 完整 scope 或改变 CI 必过集合。

  ```bash
  mkdir -p "$TYPELAY_VERIFY_OUTPUT/gtk4-interaction"
  DISPLAY="$TYPELAY_VERIFY_XDISPLAY" TMPDIR=/tmp timeout 240 \
    /usr/bin/python3 tests/linux/native_e2e.py --inside --desktop \
    --desktop-scope gtk4-interaction --gtk-session-bindings \
    --package "$TYPELAY_VERIFY_PACKAGE" \
    --fcitx-cooperative "$TYPELAY_VERIFY_COOPERATIVE" \
    --native "$PWD/artifacts/linux/native" \
    --output "$TYPELAY_VERIFY_OUTPUT/gtk4-interaction"
  ```

  `--inside` 复用技能的 niri；外层 `timeout 240` 保留原桌面总预算。短 `TMPDIR` 仅供该 runner 的小型配置与 socket，包和正式模型仍放在 Btrfs。保留 `gtk-fixture-identity.json`、`gtk-acceptance-phases.json`、`gtk-control-*.json`、原字段状态、截图和完整日志。按实际到达的阶段报告 `passed`、`failed`、`not-run`，不能用整条旅程的失败覆盖此前已通过阶段，也不能将未到达的 Undo 或 Direct 记作失败。核对原字段的精确人工稿、选区外正文和唯一回执，再核对撤销轨迹及新焦点字段。

  重入检查另外覆盖先连接输入法、后映射窗口的启动顺序。受控 GTK4 字段先 realize，收到原 context 的 Binding 通知后才首次 present；没有补按键或再次 grab_focus。检查 `gtk4-connected-before-map/gtk-reentry-delete-unchanged.json`，确认 `connectedBeforeMapAtUs` 早于实际 `firstEntry.focusInAtUs`，并保留原 5 秒源字段就绪期限。该场景须完成真实源捕获和唯一 Verified 写入回执，原有自然启动及失效拒绝场景继续执行。

  GTK3 与 GTK4 的重入检查还通过真实 Fcitx 和原生协议覆盖 Ctrl 持有期间的候选刷新。检查 `gtk-ordered-retirement.json`，确认第二次发布发生于原 100 毫秒许可内，换框后的 F7 透传且零写入，两个原许可各退休一次，随后原字段独立 Tab 只写入一次。`gtk-ordered-refresh.json` 要求等价刷新保留原计划，`gtk-ordered-change.json` 要求正文改变撤销旧按次资格。这些受控协议场景不使用模型，不能代替完整桌面旅程；2 秒退休期限和 Unknown 隔离保持原要求。

普通网页完整旅程使用同一桌面 runner 的 `browser-index` 范围。先按 build.md 准备当前干净提交的完整包、合作包和 Index 下载缓存，再从新私有会话执行：

```bash
/usr/bin/python3 "$PWD/.agents/skills/verify-typelay/scripts/private-session.py" \
  --package artifacts/verification-build/package \
  --cooperative artifacts/verification-build/cooperation/TypeLay-Fcitx-cooperative \
  --output "$PWD/artifacts/verification/browser-index-$(date +%Y%m%d-%H%M%S)" \
  -- bash -c 'exec /usr/bin/python3 tests/linux/native_e2e.py \
    --inside --desktop --desktop-scope browser-index \
    --package "$TYPELAY_VERIFY_PACKAGE" --native "$PWD/artifacts/linux/native" \
    --index-cache "$TYPELAY_VERIFY_MODEL_CACHE" --chromium /usr/bin/chromium \
    --output "$TYPELAY_VERIFY_OUTPUT/browser-index"'
```

桌面解释器需能导入系统 PyGObject 和支持 `connect_over_cdp(no_defaults=True)` 的 Playwright。可使用带系统包的私有工程环境，并把命令中的解释器和 `--chromium` 换成已安装工具的绝对路径。若实际可执行文件是 Chrome，另传 `--browser-kind google-chrome`，按真实进程身份登记；默认类型为 `chromium`。浏览器使用本次独立 profile。保持私有会话提供的短 `TMPDIR`，只将 socket 等临时文件放入其中；正式安装、模型和包副本留在 Btrfs 等正常文件系统。

runner 通过正式 `install.py install` 安装当前包，再从空数据执行默认 `typelay --install-models`。它只预置合成旅程的英到中方向及教程完成状态，保存实际初始设置，不把这项注入当作设置 UI 验收。扩展通过开发加载，连接使用正式注册入口；页面内容由受控普通 HTML 提供，消息管道、桌面宿主和 Index 均为真实组件。

固定模型样例为 backup 普通句和 URL/换行结构句。UTF-16 与 emoji 由原字段前后缀及多行人工稿覆盖。[质量票 #23](https://github.com/DingJWuyu/Typelay/issues/23)保留额外多行 emoji 组合的 `structure-not-preserved` 拒绝；该组合没有成功译文，本旅程不将它计为通过，也不放宽结构保护。

依次检查 `browser-index/result.json` 的阶段和对应原件：

- `preview` 保留原文和真实译文，记录固定 profile、正式库存与包身份。
- `draft-before-return`、`draft-returned`、`draft-accepted` 分别证明人工稿保持、首次 Tab 零写和释放后的第二次独立 Tab 精确写入。源字段 input 事件与控制器 Verified 回执都只能出现一次。
- `selection-before`、`selection-accepted` 核对 UTF-16 范围、emoji、换行、选区外原文及折叠光标。
- `direct-before`、`direct-detached`、`direct-completed` 核对按需模式的正式 CLI Direct。A 的唯一替换事件必须记录 B 当时的焦点、正文和选区，最终状态也须一致。
- `direct-command-times.json` 只读记录真实扩展端口的命令代际、原触发时间和保守观察时间；两种时间分别用于命令排序与源观察，不将观察下界抬高到触发时间。

打开各阶段截图检查文字、位置和裁剪。逐句核对 `samples.json` 的输入输出，判断基本保义、emoji 和换行；非空译文与自动阶段通过不代表语义已审查。最后核对外层 `cleanup.json`，要求无自有残留且 scratch/runtime 已删除，原件仍在。重复验收使用新的输出目录和空安装代。

## Gotchas

- 基础 Launch 的实验写回保持默认关闭，因此 `gtk-preview.py` 明确记录 `writebackTested=false`。不能把预览通过升级为写回通过。
- 可见译文、`ValidTranslation` 与短期接受许可不同。资格失效时不得重复投递直到“碰巧通过”，也不得放宽输入法状态或焦点判断。
- GTK3 TextBuffer 不提供本旅程需要的原生 Undo。`userActionsStarted` / `userActionsFinished` 只证明一次编辑分组，结果中的 `nativeUndo=false` 不能改写为撤销成功。
- GIMP 3.2.6-2 的 Comment 不提供评论修改的原生 Undo。既有 `ordinary-gimp` 会比较原生粘贴轨迹，这种不支持不能报告成 TypeLay 撤销成功。
- 合成键盘、受控 fixture 与物理输入法各有边界。本机未执行物理输入法验收。
- 网页旅程的 Tab 使用私有 XTest；Unicode 文本使用浏览器可信输入，选区通过 DOM 设置。它不证明物理输入、任意站点或普通 textarea 的原生 Undo。Firefox、富文本和商店安装仍有各自入口。
