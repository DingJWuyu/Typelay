# Qt6 原生输入路径

Qt 路径目前是 CI 中隔离构建、验证的合作组件，不随普通 Linux 发行包启用。目标机器的 Qt ABI 尚未确认，不能把 CI 的私有插件直接当作用户可安装组件。它复用同一个 Linux 宿主、Core/Runtime 会话与本地模型，不另建翻译或接受控制器。

## 实现范围

- 加载器是公开 `QGenericPlugin`，通过 `QT_QPA_GENERIC_PLUGINS` 追加 `typelay`，保留 Qt 原输入法插件。
- 当前源字段仅为标准、实际类型为 `QLineEdit` 的普通单行输入框。自定义派生类、密码/隐藏/敏感输入提示、只读/禁用字段、validator、input mask 及数字/日期/邮箱/URL等限定输入提示 均不授予正文捕获或写入能力。共享字段策略先检查标签、帮助与标识等元数据，再批准正文读取。
- 目标身份绑定实际 `QPointer<QLineEdit>`、控件生命周期和合作 provider 的 `fieldGeneration`。同一窗口内第二个输入框不能继承第一个输入框的许可。
- 原生模块使用既有 v2 同用户 socket 协议；hello 的 backend 为 `qt6`，共享目标 backend 为 `linux-qt6`。`qt-toplevel` 几何只表示父窗口内部的逻辑坐标，不是 Wayland 全局坐标。
- 浮层是源窗口拥有的 Qt 子控件，不创建独立顶层窗口、不移动应用布局。正文预览、开始编辑、草稿、返回、接受、重译、复制、展开和隐藏沿用宿主消息。第一 Tab 只请求返回源框；宿主得到新的源观察后才可发布下一次 Tab 许可。
- 自动模式与按需模式使用相同协议。已配置的 wake/direct 快捷键按 Linux keyval/modifier 规则匹配；niri 触发同样必须携带当前目标的短期授权，不能读取后来聚焦的字段。

## 输入法与写回边界

合作 Fcitx Qt provider 使用 QtCore-only、版本化 IID，在实际焦点 QObject 上发布自身。消费者经 `qobject_cast` 验证接口并使用弱生命周期引用。`inspect` 读取真实 context、在途请求、输出与字段代次；`validateLocal` 只能验证同一次观察，不能自行建立 Clear；`prepareCaret` 后仍须重新观察与核对原目标。

默认异步 Fcitx 路径保持不变。转发事件必须等 Qt 的真实事件分发结束才 ACK。在普通控件的 Tab 回调中，该事件自身仍处于 pending，因此不能在那里忽略 pending 或用下一轮 timer 假装它已完成。合作 provider 在原始输入请求之前，只对无修饰 Tab/Escape 或已配置的控制键调用消费者。消费者同步完成全部字段/许可/IME 检查后才可消费；拒绝时原异步路径恰好执行一次。重复按键与对应释放沿原已消费键配对，不生成第二次接受。

`FCITX_QT_USE_SYNC=1` 不为 TypeLay 建立能力。强制同步模式保持原普通输入处理，但 TypeLay 按 Unknown 拒绝捕获、编辑与接受。不能使用“同步 API 看起来更简单”绕过上游实际按键投递行为。

写入在源 GUI 线程执行：验证原文与 UTF-16 选区，准备并重验 provider，调用原生 `setSelection`，在所有同步选区回调后再次检查身份、字段元数据、输入/焦点/正文版本、期限与本地 IME 证明，再调用 `insert`。禁止通过 `setText` 清空撤销记录。写后核对完整结果、光标、字段与 IME；不匹配返回 `OutcomeUnknown` 并隔离该字段，不盲目重试或回滚覆盖应用的新内容。该范围包含普通应用事件过滤器与重入回调；不宣称对恶意应用提供原子事务保证。

## 构建与验证

合作 provider 的构建与隔离运行由 `tests/linux/qt_cooperative/run.py` 管理。它依赖与实际 Qt 版本匹配的 Qt private headers；通用 consumer 自身只使用公开 Qt Core/Gui/Widgets API。consumer 可以单独编译：

```sh
cmake -S src/TypeLay.Linux/native/qt6 -B artifacts/linux/qt-native -G Ninja -DTYPELAY_QT_NATIVE_TESTS=ON
cmake --build artifacts/linux/qt-native
```

输出 `qt6/generic/libtypelay-qt6.so` 与 `qt-native-fixture`。仅在已验证的隔离合作环境中，为该测试进程追加插件目录和 `typelay` generic plugin，并设置 `TYPELAY_FCITX_COOPERATIVE=1`。普通安装器不会修改系统 Qt 插件目录、系统输入法或用户会话。

`tests/linux/qt_native_fixture.cpp` 提供两个真实标准字段、原生撤销状态、输入事件计数、字段保护配置及一次性应用回调干扰。它只使用合成正文。生产模块不输出字段正文或任意元数据日志。`tests/linux/qt_desktop.py` 使用正式宿主、真实本地模型与嵌套 niri 路径进行集成验收，结果以对应提交的 CI 为准。

编译与 offscreen 加载只证明构建/加载，不证明 socket、Fcitx 输入、物理 Tab、真实中文 IME、多屏缩放或完整产品 UI 通过。Qt 富文本、多行源字段和应用自绘编辑器仍未实现；本路径不能代表通用 Qt 输入框全部完成。
