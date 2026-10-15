# Qt6 原生输入路径

Qt 路径已在隔离 CI 中构建、验证，普通 Linux 安装不会自动启用；可选合作包已通过实际构建、中文空格目录搬迁与 `launch --qt6` 验证，启用和回退步骤见 [Fcitx 合作组件](fcitx.md)。用户已确认 Qt 6.11.2-3.1、Fcitx Qt 5.1.15、GTK4 4.22.5-1.1；Arch 隔离环境已验证同上游版本的 Qt 6.11.2 与 GTK4 4.22.5。CI 的 Arch 构建与用户的 CachyOS 发行修订仍须区分，须通过合作包的运行时版本与依赖检查，不能据此声明用户机器通过。该路径复用同一个 Linux 宿主、Core/Runtime 会话与本地模型，不另建翻译或接受控制器。

## 实现范围

Qt 6.11.2 与固定 Fcitx Qt 5.1.15 的隔离 Wayland/niri 链路已有实际模型与控件行为证据。标准 QLineEdit 已验证：实际英文键入、真实本地模型、浮层、单次 Tab、原生撤销、点击编辑后的两阶段 Tab、UTF-16 选区替换与中文/emoji 周边保留；真实设置切换按需模式后，默认 Ctrl+Shift+F9 唤醒及 Ctrl+Shift+F10 一次直接替换通过。显式命令待捕获时已观察到的正文/焦点变化会取消，捕获后的原文变化也会阻止直接写回。实际拼音组合、敏感标签、密码、validator、input mask、仅 buddy 标签敏感及聚焦但被裁剪的字段均有拒绝证据。

标准 QPlainTextEdit 已验证真实模型的跨行 UTF-16 选区替换，保留 emoji/NBSP 周边及其他字段，一次 Tab 产生一个文档编辑，原生 undo/redo 精确恢复。实际拼音组合不触发 TypeLay 写回；只读、有限 block 数及自定义对象格式在正文读取前拒绝。应用在写入通知中移动光标时保留应用位置，返回一次 `OutcomeUnknown`、隔离该目标且不重试。

- 加载器是公开 `QGenericPlugin`，通过 `QT_QPA_GENERIC_PLUGINS` 追加 `typelay`，保留 Qt 原输入法插件。
- 当前源字段支持实际类型恰为 `QLineEdit` 的普通单行输入框，以及恰为 `QPlainTextEdit` 的普通纯文本多行框。自定义派生类、密码/隐藏/敏感输入提示、只读/禁用字段、validator、input mask 及数字/日期/邮箱/URL 等限定输入提示均不授予正文捕获或写入能力。共享字段策略先检查标签（含实际 QLabel buddy）、帮助与标识等元数据，再批准正文读取；完全位于父窗口裁剪范围外的字段同样在正文读取前拒绝。
- 目标身份绑定实际控件的 `QPointer`、生命周期和合作 provider 的 `fieldGeneration`。多行另绑定实际 `QTextDocument` 的弱引用与代次；文档替换、销毁和已观察到的正文及选区/光标变化均撤销旧许可。同一窗口内其他输入框或替换后的文档不能继承许可。
- 多行只接纳标准 `QTextDocument`/`QPlainTextDocumentLayout`，正文读取前拒绝 `maximumBlockCount > 0`、超过既有 8192 UTF-16 单元上限、列表、子 frame/表格、嵌入对象、链接/自定义语义标记等不支持结构；普通字体、颜色与对齐样式不因此被排除。授权后用 `toRawText()`，仅将段落分隔符 U+2029 一对一映射为 LF，保留 NBSP 和 U+2028，选区仍使用原生 UTF-16 位置。
- 原生模块使用既有 v2 同用户 socket 协议；hello 的 backend 为 `qt6`，共享目标 backend 为 `linux-qt6`。`qt-toplevel` 几何只表示父窗口内部的逻辑坐标，不是 Wayland 全局坐标。
- 浮层是源窗口拥有的 Qt 子控件，不创建独立顶层窗口、不移动应用布局。正文预览、开始编辑、草稿、返回、接受、重译、复制、展开和隐藏沿用宿主消息。编辑器取得文本所有权后，异步展示回声不得重设正文、光标或撤销历史。第一 Tab 只请求返回源框；宿主得到新的源观察后才可发布下一次 Tab 许可。
- 自动模式与按需模式使用相同协议。已配置的 wake/direct 快捷键按 Linux keyval/modifier 规则匹配；niri 触发同样必须携带当前目标的短期授权，不能读取后来聚焦的字段。

浮层主题沿用设置的 `system` / `light` / `dark` 小写合同。显式浅色或深色只作用于 `typelay-overlay` 子树；跟随系统时读取 Qt 当前有效颜色方案，平台返回 Unknown 时以应用的 Window/WindowText 调色板回退，不修改宿主应用的全局主题。当前可见浮层随颜色方案或应用调色板变化重画，保留正文、光标、焦点、几何与原有效期。译文编辑器的可访问名称为“译文”，预览与卡片使用一致底色，编辑焦点边框与文字选区仍可辨认；按钮保留原动作与点击区域。

展开/收起只在当前原生 `QPlainTextEdit` 上改变高度，约 280ms，宽度不变；右侧与上方底侧（或下方顶侧）锚保持不变。父窗口边缘需要换侧或裁限时直接落位。现有展开通知与显示续期保持；宿主仅对该次展开排队的下一份视图附一次性 `visualExpansionFrom` 标记，后续排队覆盖、发送前消费。客户端只在完整目标、代次、源/编辑器实例、父窗口、几何、全文视图与主题都未变时沿原结束时刻继续几何，标记不提供接受或编辑资格。其它新视图直接取消旧动效；续租不会重播。所有隐藏先取消动画并清正文，没有退出残影或完成后恢复/聚焦。

减少动态效果通过当前 Qt 样式的公开 `QStyle::SH_Widget_Animation_Duration` 读取，零值直接最终布局。Qt 6.11 的公开 `QAccessibilityHints` 仅提供对比度偏好，没有通用减少动态效果属性；此实现不能保证每个 Linux 桌面/样式都转发系统偏好。依据：[QStyle](https://doc.qt.io/qt-6.11/qstyle.html)、[QAccessibilityHints](https://doc.qt.io/qt-6.11/qaccessibilityhints.html)。桌面 fixture 的有限控件实绘帧与几何进入现有输出目录 `qt-motion-*`，包含真实样式零值、展开/反转、续期、新编辑视图、隐藏取消、中文选区与后续原生撤销/两阶段返回；新增场景需由对应提交实际运行，不以源码或旧截图标为通过。

## 输入法与写回边界

合作 Fcitx Qt provider 使用 QtCore-only、版本化 IID，在实际焦点 QObject 上发布自身。消费者经 `qobject_cast` 验证接口并使用弱生命周期引用。`inspect` 读取真实 context、在途请求、输出与字段代次；`validateLocal` 只能验证同一次观察，不能自行建立 Clear；`prepareCaret` 后仍须重新观察与核对原目标。

Fcitx 按键请求继续异步发出；完成处理增加 provider 内部 FIFO，按真实请求顺序处理已收到 `finished` 的 watcher，运行原有结果、fallback 与转发逻辑。后一个回复先到不能越过尚未完成的前一个请求。请求在 watcher 实际销毁前仍计为 pending，转发事件也必须等 Qt 的真实事件分发结束才 ACK；不以队列空闲猜测或提前读取 `isFinished()` 建立 Clear。

合作 provider 在原始输入请求之前，只对无修饰 Tab/Escape 或已配置的控制键调用消费者。接受/退出默认 Tab/Esc，也可分别配置无修饰 F1-F24；按需 wake/direct 的启用与会话键启用独立。配置先撤销旧许可，额外 XKB 键仍经既有 `control-keys.v1` 属性安装。新增独立 `TypeLayFcitxQtControlKeysV1` 能力接口验证实际字段的 provider 与已安装键集，不改变旧 `TypeLayFcitxQtV1` ABI；只有旧接口的 provider 保持只读，不能仅凭可转换旧 IID 推断动态按键能力。describe/capture 回显当次目标的 Applied generation、规范键对及 source/editor readiness；编辑器使用同一个已安装消费者和 provider 路由；移入编辑器后只保留该目标、该代次的源按键路由确认，并核验实际编辑器绑定的是同一个 provider。编辑器 provider 丢失或更换会撤销这项确认，恢复属性本身不使其复活；该确认从不代替源 IME 或写入许可。接受/退出仍须当下同步完成全部字段、许可和 IME 检查才可消费，拒绝时原异步处理恰好执行一次。在普通控件的 Tab 回调中，该事件自身仍处于 pending，不能在那里忽略 pending 或延后接受。重复按键与对应释放按实际 native scan code 沿原按下动作配对，不生成第二次接受。未绑定键的按下同样保留，因此配置、隐藏或焦点切换不能把已按住的功能键变成新的接受。publish、直接写入、普通展示、编辑和返回都绑定配置 generation；provider/选区回调重入后写前再次核对，已经完成的写入回执仍按原 plan 返回。

F9/F10 是显式命令：有效的当前目标/元数据授权可登记最多一个待捕获请求，此时不读取正文。请求固定继承原授权的 150ms 截止，不因新发布或轮询续期；已有 25ms tick 只检查真实 pending 是否退休并重新取得 Clear，随后才捕获同一原字段。正文、选区、焦点、非 modifier 新输入、配置变更、显式撤销授权及超期都会取消，并给出短提示，不在取消后重试。纯 modifier 事件仍撤销即时 IME 证明，但不伪装成源正文变化；实际组合、提交或输入法变化继续使请求失效。

`FCITX_QT_USE_SYNC=1` 不为 TypeLay 建立能力。强制同步模式保持原普通输入处理，但 TypeLay 按 Unknown 拒绝捕获、编辑与接受。不能使用“同步 API 看起来更简单”绕过上游实际按键投递行为。

写入在源 GUI 线程执行：验证原文与 UTF-16 选区，准备并重验 provider，设置原生选区，在所有同步选区回调后再次检查身份、字段元数据、输入/焦点/正文版本、期限与本地 IME 证明。单行沿用 `setSelection`/`insert`；多行用 `QTextCursor` 的 `beginEditBlock`/`insertText`/`endEditBlock`，核对完整正文与当前光标后再设置 cursor，最终核对文档变更范围。禁止通过 `setText`/`setPlainText` 清空撤销记录，也不强制开启应用关闭的 undo。写后核对完整结果、光标、字段、文档身份与 IME；不匹配返回 `OutcomeUnknown` 并隔离该字段，不盲目重试或回滚覆盖应用的新内容。该范围包含普通应用事件过滤器与重入回调；不宣称对恶意应用提供原子事务保证。

直接替换分别核验冻结的原源版本和写前新批准的完整 Clear 证明，不能把纯 modifier 之后的旧证明继续当作可写许可。标准 QLineEdit 另有同窗口后台直接替换路径：离开原字段时，只有原输入与输出全部退休、真实输入法 Clear、原上下文及字段版本仍一致，provider 才建立独立的 detached 证明。写入原字段时不聚焦它，也不 reset 当前字段的输入法；原正文、选区、元数据或绑定变化仍拒绝。真实模型回归已验证 A→B 后写 A、保持 B 焦点/正文和 A 原生撤销；独立输入法回归验证 B 处于真实拼音组合时证明不清理 B，以及未退休输入时拒绝。同一路径已纳入可选包，并通过搬迁目录的实际 `launch --qt6` 与真实模型回归。QPlainTextEdit 后台、跨窗口/应用后台仍未覆盖；结果不会转写新焦点。QTextEdit、其他富文本结构和自绘源字段仍未完成。

## 构建与验证

合作 provider 的生产构建由 `eng/linux/cooperative/qt.py` 管理，固定来源和构建输入在同目录 `inputs.py`，补丁在 `patches/`，运行时探针是同目录 `qt-runtime-probe.cpp`。它依赖与实际 Qt 版本匹配的 Qt private headers；通用 consumer 自身只使用公开 Qt Core/Gui/Widgets API。单独生产构建使用新输出目录：

```sh
python eng/linux/cooperative/qt.py --out artifacts/linux/qt-build
```

`tests/linux/qt_cooperative/run.py` 调用同一生产构建，再编译 fixture 并执行隔离回归，命令和可选包打包流程见 [Fcitx 合作组件](fcitx.md#可选-qt-6112-构建与启动)。生产构建元数据与测试输入/结果分别记录；单独构建不是发布验收。consumer 与受控控件也可单独编译：

```sh
cmake -S src/linux/native/qt6 -B artifacts/linux/qt-native -G Ninja -DTYPELAY_QT_NATIVE_TESTS=ON
cmake --build artifacts/linux/qt-native
```

输出 `qt6/generic/libtypelay-qt6.so` 与 `qt-native-fixture`。仅在已验证的隔离合作环境中，为该测试进程追加插件目录和 `typelay` generic plugin，并设置 `TYPELAY_FCITX_COOPERATIVE=1`。普通安装器不会修改系统 Qt 插件目录、系统输入法或用户会话。

`tests/linux/fixtures/qt_native_fixture.cpp` 提供两个真实 QLineEdit 和按场景启用的标准 QPlainTextEdit、原生 undo/redo 与文档变更状态、输入事件计数、字段保护配置及一次性应用回调干扰。它只使用合成正文。生产模块不输出字段正文或任意元数据日志。`tests/linux/qt_desktop.py` 使用正式宿主、真实本地模型与嵌套 niri 路径进行集成验收，覆盖上述前台单行/多行、同窗 QLineEdit 后台写回和原文冲突拒绝；发行回归另外从搬迁后的可选包执行启动器，不用源码构建目录代替。执行结果以当前提交 CI 为准。测试记录实际 fixture PID 的库和 Wayland 插件映射，并核对构建、请求与运行版本均为 6.11.2，拒绝混版或 XCB 替代；`qt_cooperative` 另用真实完成顺序扰动检查异步 FIFO 和实际字段内容，不能用正文长度代替内容完整性。

会话键新增回归复用 `qt_cooperative` 的实际 provider 键集能力查询，以及 `qt_desktop.py` 的真实设置 UI、F1/F12/F24、编辑返回后释放与第二次按下、按需切换和默认键恢复。4a499d0 的 Qt 6.11.2 合作构建与搬迁包已通过这些场景及后续完整桌面链；provider 失效撤销旧编辑会话后，测试使用明确的新源重新建立候选，不要求旧草稿资格复活。旧 provider 完整二进制的兼容性仍未验证，不能以源码检查代替。配置确认也不保证桌面或应用会把每个功能键交给该字段；客户端不通过全局抢键绕过已有处理。

主题回归复用同一桌面 fixture，检查实际设置三态与宿主浅/深外观的组合、实绘背景及文字调色板对比度、长中文草稿切色时的光标/撤销/焦点和暂停状态，并把明暗截图写入测试输出目录。Qt 公共调色板不表示悬停样式，暂停按钮的真实悬停另由截图验收。它不以样式字符串证明视觉结果；新增主题与视觉场景仍须按实际 CI 运行和截图判定，既有桌面通过记录不能代替本次验证。

[c129def 的实际 CI 证据](https://github.com/DingJWuyu/Typelay/actions/runs/38632590992)已覆盖三种设置与两种应用调色板的六组候选态实绘；六组观测中的 Qt 颜色方案均为 Unknown，跟随系统验证的是应用调色板回退分支。两组编辑态明暗实绘及断言验证合成长中文草稿切色时保持正文、光标、选区与焦点，真实 Ctrl+Z 精确撤销键入后缀；随后手工草稿第一 Tab 仅返回源框，第二 Tab 获得 Verified 写回回执。长中文由 fixture 的控件 API 注入，不代表中文 IME 输入验证。暂停态另有两张明暗实绘与恢复按钮真实悬停证据；暂停期间无编辑器、正文捕获、模型任务或写回，点击恢复保留按需模式。上述十张主题截图已核验，Qt 明确返回 Light/Dark 时的系统主题分支仍未完成验证；本轮桌面链通过不消除下述短期许可风险。

编译与 offscreen 加载只证明构建/加载，不证明 socket、Fcitx 输入、物理 Tab、真实中文 IME、多屏缩放或完整产品 UI 通过。QTextEdit、其他 Qt 富文本结构和应用自绘编辑器仍未实现；本路径不能代表通用 Qt 输入框全部完成。

## 当前时序限制

短期许可的到达确认不保证用户按键到达或同步选区回调结束时仍未过期。实际验证曾在 Tab 入口仅余 9ms、设置选区后截止的情况下拒绝正文写入，并交还原 Tab；字段身份、版本和 IME 当时均匹配。该行为保护原文，但会造成候选可见时偶发不可接受，属于仍待解决的许可刷新/提示一致性风险。不得通过重发 Tab、放宽期限或降低身份/IME 校验隐藏它；路径迁移与发行验证通过也不代表此可用性问题已解决。

F10 的待捕获请求目前继承原观察的截止时间，而不是按下后重新获得 150ms。61f51ca 的实际请求入队时只余 60ms，在真实转发事件尚未全部退休时超期取消；字段版本仍匹配，模型和写回没有执行。provider 在 pending 时直接拒绝 inspect，不发同步 Probe；现日志不足以把这段等待细分为 DBus、QPA 或线程调度。保持原截止与可见拒绝反馈，不给旧证明续期。

F9 唤醒也存在入队前安全拒绝的可用性风险。[实际 CI 证据](https://github.com/DingJWuyu/Typelay/actions/runs/38571156327)中，真实 provider 将 Ctrl+Shift+F9 交给 consumer，但命令以 `StaleObservation` 拒绝，尚未进入待捕获或 dispatch；配置的 F12 接受键尚未按下，后续场景未到达。现有记录缺少该次命令 authority 的 basis，不能区分观察时间戳缺失、未来或已过期，也不能据此归因于配置代次或键对 ACK。`hook-enter` 的负 `remainingMs` 来自空接受 publication 的 `until=0`，不是 wake 授权年龄。新增一次 supported capture 不等于按键当下的原生命令授权已安装且有效；此前两次完整桌面链通过不能消除此风险。该问题须与 100ms 接受许可分别判断，保持命令原有 150ms 截止，不自动重发用户按键。

### 程序性改文的观察假设

当前 consumer 的正文/选区版本依赖 Qt 控件信号与实际输入事件。标准类型检查不能证明应用不会临时屏蔽通知；例如数据绑定用 `QSignalBlocker` 包住 `setText` 或选区回填时，Qt 不会在解除阻塞后补发信号，见 [QObject::blockSignals](https://doc.qt.io/qt-6/qobject.html#blockSignals)。因此不能把当前范围表述为所有普通 Qt 应用都具备不可遗漏的变更版本。

这一假设尤其影响 F10 的触发到捕获阶段：排队期间原文被静默回填后，后续捕获可能取到后来值。写前完整原文比较仍能阻止捕获之后的异值覆盖，却不能恢复未观察到的触发时正文。仅检查当时 `signalsBlocked()` 或增加末值比较不能证明中途没有发生这种更新。现阶段不扩大待捕获窗口，不以忽略 pending 的读取来补快照；目标应用若使用此类更新，需明确合作的变更通知契约后再宣称该语义通过。已观察到的输入/选区/配置变化继续不可逆取消，最终元数据、完整原文、选区、IME 和寿命检查保持必要。
