# Qt6 原生输入路径

Qt 路径目前是 CI 中隔离构建、验证的合作组件，不随普通 Linux 发行包或用户合作包启用。用户已确认 Qt 6.11.2-3.1、Fcitx Qt 5.1.15、GTK4 4.22.5-1.1；现有隔离实测使用 Qt 6.12.0、GTK4 4.24.1。同上游版本的 Arch 环境仍待构建与验证，不能把 CI 的私有插件直接当作用户可安装组件，也不能据此声明用户机器通过。它复用同一个 Linux 宿主、Core/Runtime 会话与本地模型，不另建翻译或接受控制器。

## 实现范围

Qt 6.12.0 与固定 Fcitx Qt 5.1.15 的隔离 Wayland/niri 链路已验证：实际英文键入、真实本地模型、浮层、单次 Tab、原生撤销、点击编辑后的两阶段 Tab、UTF-16 选区替换与中文/emoji 周边保留；真实设置切换按需模式后，默认 Ctrl+Shift+F9 唤醒及 Ctrl+Shift+F10 一次直接替换通过。显式命令待捕获时的正文/焦点变化会取消，捕获后的原文变化也会阻止直接写回。实际拼音组合、敏感标签、密码、validator、input mask、仅 buddy 标签敏感及聚焦但被裁剪的字段均有拒绝证据。

- 加载器是公开 `QGenericPlugin`，通过 `QT_QPA_GENERIC_PLUGINS` 追加 `typelay`，保留 Qt 原输入法插件。
- 当前源字段仅为标准、实际类型为 `QLineEdit` 的普通单行输入框。自定义派生类、密码/隐藏/敏感输入提示、只读/禁用字段、validator、input mask 及数字/日期/邮箱/URL 等限定输入提示均不授予正文捕获或写入能力。共享字段策略先检查标签（含实际 QLabel buddy）、帮助与标识等元数据，再批准正文读取；完全位于父窗口裁剪范围外的字段同样在正文读取前拒绝。
- 目标身份绑定实际 `QPointer<QLineEdit>`、控件生命周期和合作 provider 的 `fieldGeneration`。同一窗口内第二个输入框不能继承第一个输入框的许可。
- 原生模块使用既有 v2 同用户 socket 协议；hello 的 backend 为 `qt6`，共享目标 backend 为 `linux-qt6`。`qt-toplevel` 几何只表示父窗口内部的逻辑坐标，不是 Wayland 全局坐标。
- 浮层是源窗口拥有的 Qt 子控件，不创建独立顶层窗口、不移动应用布局。正文预览、开始编辑、草稿、返回、接受、重译、复制、展开和隐藏沿用宿主消息。编辑器取得文本所有权后，异步展示回声不得重设正文、光标或撤销历史。第一 Tab 只请求返回源框；宿主得到新的源观察后才可发布下一次 Tab 许可。
- 自动模式与按需模式使用相同协议。已配置的 wake/direct 快捷键按 Linux keyval/modifier 规则匹配；niri 触发同样必须携带当前目标的短期授权，不能读取后来聚焦的字段。

## 输入法与写回边界

合作 Fcitx Qt provider 使用 QtCore-only、版本化 IID，在实际焦点 QObject 上发布自身。消费者经 `qobject_cast` 验证接口并使用弱生命周期引用。`inspect` 读取真实 context、在途请求、输出与字段代次；`validateLocal` 只能验证同一次观察，不能自行建立 Clear；`prepareCaret` 后仍须重新观察与核对原目标。

Fcitx 按键请求继续异步发出；完成处理增加 provider 内部 FIFO，按真实请求顺序处理已收到 `finished` 的 watcher，运行原有结果、fallback 与转发逻辑。后一个回复先到不能越过尚未完成的前一个请求。请求在 watcher 实际销毁前仍计为 pending，转发事件也必须等 Qt 的真实事件分发结束才 ACK；不以队列空闲猜测或提前读取 `isFinished()` 建立 Clear。

合作 provider 在原始输入请求之前，只对无修饰 Tab/Escape 或已配置的控制键调用消费者。Tab/Escape 仍须当下同步完成全部字段、许可和 IME 检查才可消费，拒绝时原异步处理恰好执行一次。在普通控件的 Tab 回调中，该事件自身仍处于 pending，不能在那里忽略 pending 或延后接受。重复按键与对应释放沿原已消费键配对，不生成第二次接受。

F9/F10 是显式命令：有效的当前目标/元数据授权可登记最多一个待捕获请求，此时不读取正文。请求固定继承原授权的 150ms 截止，不因新发布或轮询续期；已有 25ms tick 只检查真实 pending 是否退休并重新取得 Clear，随后才捕获同一原字段。正文、选区、焦点、非 modifier 新输入、配置变更、显式撤销授权及超期都会取消，并给出短提示，不在取消后重试。纯 modifier 事件仍撤销即时 IME 证明，但不伪装成源正文变化；实际组合、提交或输入法变化继续使请求失效。

`FCITX_QT_USE_SYNC=1` 不为 TypeLay 建立能力。强制同步模式保持原普通输入处理，但 TypeLay 按 Unknown 拒绝捕获、编辑与接受。不能使用“同步 API 看起来更简单”绕过上游实际按键投递行为。

写入在源 GUI 线程执行：验证原文与 UTF-16 选区，准备并重验 provider，调用原生 `setSelection`，在所有同步选区回调后再次检查身份、字段元数据、输入/焦点/正文版本、期限与本地 IME 证明，再调用 `insert`。禁止通过 `setText` 清空撤销记录。写后核对完整结果、光标、字段与 IME；不匹配返回 `OutcomeUnknown` 并隔离该字段，不盲目重试或回滚覆盖应用的新内容。该范围包含普通应用事件过滤器与重入回调；不宣称对恶意应用提供原子事务保证。

直接替换分别核验冻结的原源版本和写前新批准的完整 Clear 证明，不能把纯 modifier 之后的旧证明继续当作可写许可。目前 provider 只为实际焦点对象提供能力，F10 写回仍要求原字段聚焦；任务期间切换到其他字段后的后台写回未完成，结果也不会转写新焦点。Qt 多行、富文本和自绘源字段同样未完成。

## 构建与验证

合作 provider 的构建与隔离运行由 `tests/linux/qt_cooperative/run.py` 管理。它依赖与实际 Qt 版本匹配的 Qt private headers；通用 consumer 自身只使用公开 Qt Core/Gui/Widgets API。consumer 可以单独编译：

```sh
cmake -S src/TypeLay.Linux/native/qt6 -B artifacts/linux/qt-native -G Ninja -DTYPELAY_QT_NATIVE_TESTS=ON
cmake --build artifacts/linux/qt-native
```

输出 `qt6/generic/libtypelay-qt6.so` 与 `qt-native-fixture`。仅在已验证的隔离合作环境中，为该测试进程追加插件目录和 `typelay` generic plugin，并设置 `TYPELAY_FCITX_COOPERATIVE=1`。普通安装器不会修改系统 Qt 插件目录、系统输入法或用户会话。

`tests/linux/qt_native_fixture.cpp` 提供两个真实标准字段、原生撤销状态、输入事件计数、字段保护配置及一次性应用回调干扰。它只使用合成正文。生产模块不输出字段正文或任意元数据日志。`tests/linux/qt_desktop.py` 使用正式宿主、真实本地模型与嵌套 niri 路径进行集成验收，当前上述范围由 [ea7b93c 的 Qt 隔离验收](https://github.com/DingJWuyu/Typelay/actions/runs/38340397397) 覆盖；`qt_cooperative` 另用真实完成顺序扰动检查异步 FIFO 和实际字段内容，不能用正文长度代替内容完整性。

编译与 offscreen 加载只证明构建/加载，不证明 socket、Fcitx 输入、物理 Tab、真实中文 IME、多屏缩放或完整产品 UI 通过。Qt 富文本、多行源字段和应用自绘编辑器仍未实现；本路径不能代表通用 Qt 输入框全部完成。
