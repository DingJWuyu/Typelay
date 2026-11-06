# Qt6 原生输入路径

Qt 合作路径让标准单行和纯文本多行控件使用 TypeLay 的浮层、编辑与受限写回。它复用 Linux 宿主、共享会话和本地模型，输入法状态由固定版本的 Fcitx Qt provider 提供。普通安装不会启用此路径；实际接入与回退按 [Fcitx 合作包说明](fcitx.md#可选-qt-6112-构建与启动)操作。

本页说明后端的支持边界和维护方法。当前固定组合是 Qt 6.11.2 与 Fcitx5-Qt 5.1.15。运行库必须通过合作包的版本及依赖检查；Arch 同上游版本的结果不代表 CachyOS 的发行修订或用户机器已验收。当前提交的编译、受控回归和 GUI 结果由开发状态分别记录，入口见 [README 开发与验收导航](README.md#开发与验收)。

## 实现范围

模块通过公开 `QGenericPlugin` 加载，保留 Qt 原输入法插件。源字段只接纳实际类型恰为 `QLineEdit` 或 `QPlainTextEdit` 的标准控件。自定义派生类、QTextEdit、QtQuick、其他富文本和自绘编辑器尚未实现。

| 字段 | 正文和写回边界 |
| --- | --- |
| `QLineEdit` | 支持普通单行文字。密码、隐藏、只读、禁用、validator、input mask 和限定输入提示会被拒绝。 |
| `QPlainTextEdit` | 支持标准 `QTextDocument` 与 `QPlainTextDocumentLayout` 的纯文本内容。有限 block 数、列表、子 frame、表格、嵌入对象、链接及自定义语义标记会被拒绝。普通字体、颜色和对齐样式不因此被排除。 |

共享策略先检查标签、实际 QLabel buddy、帮助及标识，再批准正文读取。完全被父窗口裁剪的字段也在读取前拒绝。正文沿用 8192 UTF-16 单元上限；多行捕获使用 `toRawText()`，仅把 U+2029 一对一映射为 LF，保留 NBSP 和 U+2028，选区继续使用原生 UTF-16 位置。

字段身份绑定控件的 `QPointer`、生命周期与 provider 的 `fieldGeneration`。多行字段另绑定实际文档对象和代次。文档替换、销毁及已观察到的正文、选区或光标变化都会撤销旧许可，同窗口另一字段不能继承。

源端通过 v2 同用户 socket 与宿主通信，backend 是 `qt6`，共享目标使用 `linux-qt6`。`qt-toplevel` 表示父窗口内逻辑坐标，并附带缩放比例；它不表示 Wayland 全局位置，完整定位约束由 ADR-0004 记录，可从 [README 开发导航](README.md#开发与验收)中的 Linux 架构打开。

## 输入法与写回边界

provider 在实际焦点 QObject 上发布版本化接口，consumer 通过 `qobject_cast` 和弱引用确认身份。`inspect` 检查真实输入上下文、在途请求、输出和字段代次；`validateLocal` 只能复核原观察，不能自行建立 Clear。`prepareCaret` 可能触发回调，完成后仍须重验原目标。

Fcitx 按键继续异步处理。provider 按请求顺序处理已完成的 watcher，后一个回复不能越过前一个未完成请求。watcher 销毁前仍计为 pending，转发事件也须等 Qt 实际分发完成才确认。队列空闲或 `isFinished()` 本身都不能证明 Clear。强制 `FCITX_QT_USE_SYNC=1` 会使 TypeLay 保持 Unknown，不提供捕获、编辑或接受能力。

### 每次按键只使用本次资格

接受和退出默认 Tab/Esc，也可配置无修饰 F1-F24。Ctrl+F1-F24 另需有序控制能力，源操作和译文编辑分别确认。仅能转换旧 `TypeLayFcitxQtV1` 接口的 provider 保持只读；动态键集和组合编辑必须由各自的新增接口确认。接受与退出暂须使用不同主键，这是当前实现限制。

配置变更先撤销旧许可。describe/capture 回显当前字段的配置代次、键对及 source/editor readiness，不能把另一字段或旧 provider 的确认转用于当前字段。进入译文编辑器后，仍须确认编辑器使用同一个 provider；丢失后重新挂回属性不会恢复旧资格。

物理按下及释放按 native scan code 配对。原按次的重复或释放不会形成新接受，未绑定键被按住后也不能因配置变化成为新按次。拒绝发生在认领前时，原异步输入恰好继续一次；已经认领的功能键不重放。配置确认不保证桌面或应用会把功能键交给该字段。

Ctrl 前缀在进入输入法前固定已有资格和原截止。consumer 等真实前序输入退休后裁决功能键，后读只用于核对原源。正文、输入法输出、目标或配置变化都撤销原资格，后续发布和显示续租不续票。

译文编辑的组合票只允许回源或退出，不含写入计划。第一组接受只回源；整组实际释放后，宿主须捕获新源并发布新许可，下一组独立按下才接受人工稿。源许可的例行撤销不误撤编辑票，真正的草稿、输入法、焦点、provider 或生命周期变化仍会撤销它。

### 显式命令不会转读新焦点

唤醒和直接替换默认使用 Ctrl+Shift+F9/F10。命令可以为已批准的原目标登记一个待捕获请求，此时不读取正文。请求继承原授权的 150ms 截止；25ms tick 只检查真实 pending 是否退休，重新确认 Clear 后才捕获同一字段。

正文、选区、焦点、非 modifier 新输入、配置变更、显式撤权和超期都会取消请求。纯 modifier 撤销即时输入法证明，但不冒充正文变化。取消后不转读新字段、不重试，也不因轮询获得新期限。

### 写回保留原生编辑语义

源 GUI 线程先验证完整原文和选区，再准备输入法状态、设置原生选区。所有同步回调结束后，线程重新核对字段、文档、元数据、版本、期限和输入法证明，才执行替换。

单行使用 `setSelection` / `insert`；多行使用 `QTextCursor` 编辑块。模块保留宿主撤销策略，不用 `setText` / `setPlainText` 清空历史，也不强制开启应用关闭的 undo。写后检查完整结果、光标、文档身份和输入法。应用回调改变结果时返回 `OutcomeUnknown`，隔离原目标，不重试或回滚覆盖应用的新内容；这不是对任意应用回调的原子事务保证。

未用许可退休和写入结果分别结算。consumer 只有在原许可已无执行资格且未被认领时，才报告 `writeRetired`。显示退休、到期或断连本身不能代替该证明；已经派发的操作仍按原计划接收实际回执。

### 后台直接替换有独立证明

标准 QLineEdit 的同窗口后台路径要求原输入与输出全部退休、输入法 Clear、原 context 和字段版本一致。provider 才能建立 detached 证明。写入不聚焦原字段，也不 reset 当前字段的输入法。

源码另有标准 QPlainTextEdit 切到同窗 QLineEdit 后的后台路径，交接绑定原 QTextDocument 身份与修订，写入继续检查文档代次、全文和内容增量。两条路径都不能转写新焦点，也不覆盖跨窗口或跨应用后台。测试应在原字段同步变更时检查另一字段的焦点、非空选区和组合状态；终态截图不能替代写入时观察。

## 浮层保持源窗口与编辑器身份

浮层是源窗口拥有的子控件，不创建独立平铺窗口，也不改变应用布局。共享 Runtime 决定候选、草稿、返回与接受；原生模块只消费对应呈现和交互身份。译文编辑器取得文本所有权后，展示回声不能重设正文、光标或撤销历史。

主题沿用 `system` / `light` / `dark`。显式主题只作用于 TypeLay 浮层及自有菜单；系统模式读取有效颜色方案，Unknown 时使用应用 Window/WindowText 调色板。重画保留正文、焦点、几何与原有效期，不改变宿主全局主题。实际 Light/Dark 系统分支与调色板回退需要分别验收。

More 与语言入口使用源窗口拥有的 child 菜单，互斥显示，不抢源焦点。菜单动作绑定当前交互身份；新菜单或动作状态退休旧按钮。功能开放仍受共享能力和质量门约束，可见禁用项不代表功能可用。具体视觉差距由原型对照记录，入口见 [README 开发与验收导航](README.md#开发与验收)。

展开和收起只改变原编辑器高度，保持同侧锚点。需要换侧或裁限时直接落位；新目标、几何、主题或编辑视图会取消旧动画。显示续租不重播，也不延长接受资格。隐藏先取消动画并清正文，不允许旧回调恢复浮层。减少动态效果读取 `QStyle::SH_Widget_Animation_Duration`，值为零时直接布局；这不保证每个桌面样式都传递系统偏好。

## 构建与验证

先从 [README 开发与验收导航](README.md#开发与验收)打开 Linux 开发指南，准备依赖。生产 provider 构建需要匹配 Qt 版本的 private headers；consumer 只使用公开 Qt Core/Gui/Widgets API。使用新输出目录构建：

```sh
python eng/linux/cooperative/qt.py --out artifacts/linux/qt-build
```

只检查 consumer 和真实控件 fixture 的编译时，可运行：

```sh
cmake -S src/linux/native/qt6 -B artifacts/linux/qt-native -G Ninja -DTYPELAY_QT_NATIVE_TESTS=ON
cmake --build artifacts/linux/qt-native
```

输出为 `qt6/generic/libtypelay-qt6.so` 和 `qt-native-fixture`。这些构建不安装系统插件，也不证明 socket、输入法或桌面交互通过。完整 provider 回归及可选包封装命令见 [Fcitx Qt 构建](fcitx.md#可选-qt-6112-构建与启动)。

验证时按证据边界选择入口：

| 要判断的结果 | 现有入口与观察要求 |
| --- | --- |
| 真实 provider/consumer 合作 | `tests/linux/qt_cooperative/run.py`检查默认异步模式、完成顺序、按键所有权及实际字段内容。 |
| 宿主、模型和浮层交互 | `tests/linux/qt_desktop.py`由 `native_e2e.py --desktop --desktop-scope qt` 调用，观察真实源控件、回执、编辑返回及拒写。 |
| 可搬迁发行包 | 从搬迁后的合作包执行 `launch --qt6`，检查实际 PID 的库和 Wayland 插件。源码目录运行不能替代。 |
| 字体、主题、动效和菜单 | 检查实际 glyph runs、控件状态和实绘帧；调色板或样式字符串不能证明悬停、裁剪和像素结果。 |

测试记录应核对编译、请求和实际 Qt 运行版本。桌面 Wayland 验收不能由 XCB 或 offscreen 加载替代；合作协议的隔离 XCB 回归也不能冒充 Wayland 桌面结果。旧 provider 的完整二进制兼容性、物理中文输入法、多屏缩放和当前提交的完整 GUI 仍需各自证据。

### 部分 Qt 检查不构成发行验收

Qt-only CI 仅用于已有的临时迭代入口。选择器先比较相对 `trunk` 的整支历史，再核对被执行的非 Qt 生产源码，与 `eng/shared/ci_scope.py` 中 `QT_BASELINE` 指定的提交逐字节相同。缺少提交对象或比较失败时回到完整 Linux 检查。复用的发行 artifact 必须匹配固定来源及校验；失效或不符就失败，不换未知版本。结果只覆盖本次 Qt 场景。

完整 Qt probe 明确失败后，若正式 module/fixture 已生成，仍可收集独立交互证据。既有 `tests/linux/qt_cooperative/desktop_admission.py`先核对同源码输入、实际二进制和已完成的默认异步证据，再允许明确选择未封装的构建输入：

```sh
python tests/linux/qt_cooperative/desktop_admission.py \
  --probe-outcome failure \
  --fcitx-root artifacts/linux/fcitx-cooperative-5.1.22 \
  --qt-output artifacts/linux/qt-cooperative \
  --output artifacts/linux/qt-desktop/qt-desktop-admission.json
```

只有 `admitted-build-input` 才能继续对应桌面观察，报告始终标记 `distributionAcceptance: false`。缺失或不匹配时停止；显式合作包不能回退到这条路径。probe 成功仍走搬迁包入口。该观察不改变 probe、打包或整体 CI 的失败，不回填发行 manifest，不放宽完整包依赖和原预算。默认异步证据在每次测试入口清旧，只有本次真实 client 完成后才能绑定本次输入发布。

## 当前时序限制

短期许可可能在按键到达或同步选区回调结束前失效，造成译文可见但本次接受被拒绝。唤醒也可能在入队前因 `StaleObservation` 拒绝，直接替换可能在等待真实转发事件退休时超期。这些情况保护原文，但仍影响可用性；不得通过补发按键、放宽 100ms 接受或 150ms 命令期限来隐藏。

既有展开过程还出现过空捕获导致宿主撤下浮层的中断，原因尚未确定。当前证据不能把它归因于截图、动画或某段队列。安全撤下仍需生效，不能跨已隐藏的控件续播。现役失败和目标平台验证安排由当前开发状态统一维护，入口见 [README 开发与验收导航](README.md#开发与验收)。

### 程序性改文的观察假设

正文与选区版本依赖控件信号和实际输入事件。应用若用 `QSignalBlocker` 包住 `setText` 或选区回填，解除阻塞后不会补发这些信号，见 [QObject::blockSignals](https://doc.qt.io/qt-6.11/qobject.html#blockSignals)。因此标准类型检查不能证明应用的变更通知完整。

这会影响显式命令从触发到捕获的语义：排队期间发生静默回填，后续捕获可能取到后来值。写前全文比较仍能阻止捕获后的异值覆盖，却不能恢复未观察到的触发时正文。此类应用需要明确的变更通知契约；仅检查 `signalsBlocked()`、末值或忽略 pending 都不足以补齐证明。

维护接口时从 `src/linux/native/fcitx5/typelay-fcitx-qt-v1.h`核对能力边界，从 `eng/linux/cooperative/patches/fcitx5-qt.patch`核对真实输入事务，再到 `src/linux/native/qt6/qt-module.cpp`检查字段、写回、呈现与结算。固定输入由 `eng/linux/cooperative/inputs.py`统一维护。
