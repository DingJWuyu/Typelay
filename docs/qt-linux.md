# Qt6 原生输入路径

Qt 路径已在隔离 CI 中构建、验证，普通 Linux 安装不会自动启用；可选合作包已通过实际构建、中文空格目录搬迁与 `launch --qt6` 验证，启用和回退步骤见 [Fcitx 合作组件](fcitx-linux.md)。用户已确认 Qt 6.11.2-3.1、Fcitx Qt 5.1.15、GTK4 4.22.5-1.1；Arch 隔离环境已验证同上游版本的 Qt 6.11.2 与 GTK4 4.22.5。CI 的 Arch 构建与用户的 CachyOS 发行修订仍须区分，须通过合作包的运行时版本与依赖检查，不能据此声明用户机器通过。该路径复用同一个 Linux 宿主、Core/Runtime 会话与本地模型，不另建翻译或接受控制器。

## 实现范围

Qt 6.11.2 与固定 Fcitx Qt 5.1.15 的隔离 Wayland/niri 链路已通过 13 项场景。标准 QLineEdit 已验证：实际英文键入、真实本地模型、浮层、单次 Tab、原生撤销、点击编辑后的两阶段 Tab、UTF-16 选区替换与中文/emoji 周边保留；真实设置切换按需模式后，默认 Ctrl+Shift+F9 唤醒及 Ctrl+Shift+F10 一次直接替换通过。显式命令待捕获时的正文/焦点变化会取消，捕获后的原文变化也会阻止直接写回。实际拼音组合、敏感标签、密码、validator、input mask、仅 buddy 标签敏感及聚焦但被裁剪的字段均有拒绝证据。

标准 QPlainTextEdit 已验证真实模型的跨行 UTF-16 选区替换，保留 emoji/NBSP 周边及其他字段，一次 Tab 产生一个文档编辑，原生 undo/redo 精确恢复。实际拼音组合不触发 TypeLay 写回；只读、有限 block 数及自定义对象格式在正文读取前拒绝。应用在写入通知中移动光标时保留应用位置，返回一次 `OutcomeUnknown`、隔离该目标且不重试。

- 加载器是公开 `QGenericPlugin`，通过 `QT_QPA_GENERIC_PLUGINS` 追加 `typelay`，保留 Qt 原输入法插件。
- 当前源字段支持实际类型恰为 `QLineEdit` 的普通单行输入框，以及恰为 `QPlainTextEdit` 的普通纯文本多行框。自定义派生类、密码/隐藏/敏感输入提示、只读/禁用字段、validator、input mask 及数字/日期/邮箱/URL 等限定输入提示均不授予正文捕获或写入能力。共享字段策略先检查标签（含实际 QLabel buddy）、帮助与标识等元数据，再批准正文读取；完全位于父窗口裁剪范围外的字段同样在正文读取前拒绝。
- 目标身份绑定实际控件的 `QPointer`、生命周期和合作 provider 的 `fieldGeneration`。多行另绑定实际 `QTextDocument` 的弱引用与代次；文档替换、销毁、正文和选区/光标变化均撤销旧许可。同一窗口内其他输入框或替换后的文档不能继承许可。
- 多行只接纳标准 `QTextDocument`/`QPlainTextDocumentLayout`，正文读取前拒绝 `maximumBlockCount > 0`、超过既有 8192 UTF-16 单元上限、列表、子 frame/表格、嵌入对象、链接/自定义语义标记等不支持结构；普通字体、颜色与对齐样式不因此被排除。授权后用 `toRawText()`，仅将段落分隔符 U+2029 一对一映射为 LF，保留 NBSP 和 U+2028，选区仍使用原生 UTF-16 位置。
- 原生模块使用既有 v2 同用户 socket 协议；hello 的 backend 为 `qt6`，共享目标 backend 为 `linux-qt6`。`qt-toplevel` 几何只表示父窗口内部的逻辑坐标，不是 Wayland 全局坐标。
- 浮层是源窗口拥有的 Qt 子控件，不创建独立顶层窗口、不移动应用布局。正文预览、开始编辑、草稿、返回、接受、重译、复制、展开和隐藏沿用宿主消息。编辑器取得文本所有权后，异步展示回声不得重设正文、光标或撤销历史。第一 Tab 只请求返回源框；宿主得到新的源观察后才可发布下一次 Tab 许可。
- 自动模式与按需模式使用相同协议。已配置的 wake/direct 快捷键按 Linux keyval/modifier 规则匹配；niri 触发同样必须携带当前目标的短期授权，不能读取后来聚焦的字段。

## 输入法与写回边界

合作 Fcitx Qt provider 使用 QtCore-only、版本化 IID，在实际焦点 QObject 上发布自身。消费者经 `qobject_cast` 验证接口并使用弱生命周期引用。`inspect` 读取真实 context、在途请求、输出与字段代次；`validateLocal` 只能验证同一次观察，不能自行建立 Clear；`prepareCaret` 后仍须重新观察与核对原目标。

Fcitx 按键请求继续异步发出；完成处理增加 provider 内部 FIFO，按真实请求顺序处理已收到 `finished` 的 watcher，运行原有结果、fallback 与转发逻辑。后一个回复先到不能越过尚未完成的前一个请求。请求在 watcher 实际销毁前仍计为 pending，转发事件也必须等 Qt 的真实事件分发结束才 ACK；不以队列空闲猜测或提前读取 `isFinished()` 建立 Clear。

合作 provider 在原始输入请求之前，只对无修饰 Tab/Escape 或已配置的控制键调用消费者。Tab/Escape 仍须当下同步完成全部字段、许可和 IME 检查才可消费，拒绝时原异步处理恰好执行一次。在普通控件的 Tab 回调中，该事件自身仍处于 pending，不能在那里忽略 pending 或延后接受。重复按键与对应释放沿原已消费键配对，不生成第二次接受。

F9/F10 是显式命令：有效的当前目标/元数据授权可登记最多一个待捕获请求，此时不读取正文。请求固定继承原授权的 150ms 截止，不因新发布或轮询续期；已有 25ms tick 只检查真实 pending 是否退休并重新取得 Clear，随后才捕获同一原字段。正文、选区、焦点、非 modifier 新输入、配置变更、显式撤销授权及超期都会取消，并给出短提示，不在取消后重试。纯 modifier 事件仍撤销即时 IME 证明，但不伪装成源正文变化；实际组合、提交或输入法变化继续使请求失效。

`FCITX_QT_USE_SYNC=1` 不为 TypeLay 建立能力。强制同步模式保持原普通输入处理，但 TypeLay 按 Unknown 拒绝捕获、编辑与接受。不能使用“同步 API 看起来更简单”绕过上游实际按键投递行为。

写入在源 GUI 线程执行：验证原文与 UTF-16 选区，准备并重验 provider，设置原生选区，在所有同步选区回调后再次检查身份、字段元数据、输入/焦点/正文版本、期限与本地 IME 证明。单行沿用 `setSelection`/`insert`；多行用 `QTextCursor` 的 `beginEditBlock`/`insertText`/`endEditBlock`，核对完整正文与当前光标后再设置 cursor，最终核对文档变更范围。禁止通过 `setText`/`setPlainText` 清空撤销记录，也不强制开启应用关闭的 undo。写后核对完整结果、光标、字段、文档身份与 IME；不匹配返回 `OutcomeUnknown` 并隔离该字段，不盲目重试或回滚覆盖应用的新内容。该范围包含普通应用事件过滤器与重入回调；不宣称对恶意应用提供原子事务保证。

直接替换分别核验冻结的原源版本和写前新批准的完整 Clear 证明，不能把纯 modifier 之后的旧证明继续当作可写许可。标准 QLineEdit 另有同窗口后台直接替换路径：离开原字段时，只有原输入与输出全部退休、真实输入法 Clear、原上下文及字段版本仍一致，provider 才建立独立的 detached 证明。写入原字段时不聚焦它，也不 reset 当前字段的输入法；原正文、选区、元数据或绑定变化仍拒绝。真实模型回归已验证 A→B 后写 A、保持 B 焦点/正文和 A 原生撤销；独立输入法回归验证 B 处于真实拼音组合时证明不清理 B，以及未退休输入时拒绝。此增量尚待纳入下一发行包。QPlainTextEdit 后台、跨窗口/应用后台仍未完成；结果不会转写新焦点。QTextEdit、其他富文本结构和自绘源字段仍未完成。

## 构建与验证

合作 provider 的构建与隔离运行由 `tests/linux/qt_cooperative/run.py` 管理。它依赖与实际 Qt 版本匹配的 Qt private headers；通用 consumer 自身只使用公开 Qt Core/Gui/Widgets API。consumer 可以单独编译：

```sh
cmake -S src/TypeLay.Linux/native/qt6 -B artifacts/linux/qt-native -G Ninja -DTYPELAY_QT_NATIVE_TESTS=ON
cmake --build artifacts/linux/qt-native
```

输出 `qt6/generic/libtypelay-qt6.so` 与 `qt-native-fixture`。仅在已验证的隔离合作环境中，为该测试进程追加插件目录和 `typelay` generic plugin，并设置 `TYPELAY_FCITX_COOPERATIVE=1`。普通安装器不会修改系统 Qt 插件目录、系统输入法或用户会话。

`tests/linux/qt_native_fixture.cpp` 提供两个真实 QLineEdit 和按场景启用的标准 QPlainTextEdit、原生 undo/redo 与文档变更状态、输入事件计数、字段保护配置及一次性应用回调干扰。它只使用合成正文。生产模块不输出字段正文或任意元数据日志。`tests/linux/qt_desktop.py` 使用正式宿主、真实本地模型与嵌套 niri 路径进行集成验收，上述范围及同窗口 QLineEdit 后台直接替换由 [238f15a 的 14 项 Qt 隔离验收](https://github.com/DingJWuyu/Typelay/actions/runs/38347539688) 覆盖；该单项验收不代表完整发行回归。测试记录实际 fixture PID 的库和 Wayland 插件映射，并核对构建、请求与运行版本均为 6.11.2，拒绝混版或 XCB 替代；`qt_cooperative` 另用真实完成顺序扰动检查异步 FIFO 和实际字段内容，不能用正文长度代替内容完整性。

编译与 offscreen 加载只证明构建/加载，不证明 socket、Fcitx 输入、物理 Tab、真实中文 IME、多屏缩放或完整产品 UI 通过。QTextEdit、其他 Qt 富文本结构和应用自绘编辑器仍未实现；本路径不能代表通用 Qt 输入框全部完成。
