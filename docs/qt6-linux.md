# Linux Qt6 输入适配器

当前是受限的非 IME 路径：目标控件一旦收到输入法事件，本次控件寿命内停止读取、接受和写回；不声明 Qt6 中文输入法流程已经完成。

Qt6 适配器通过 Qt 的 `QGenericPlugin` 加载到用户明确启动的目标进程中。`typelay launch command ...` 把发行包的 `native/qt6` 置于插件搜索路径前，并把 `typelay` 追加到 generic plugin 列表。已有 `QT_PLUGIN_PATH`、generic plugins 与 `QT_IM_MODULE` 保留；不替换 fcitx、ibus 或 Wayland 输入法上下文，不设置全局环境，不注入已运行的进程。目标应用需要重新启动。

## 支持与拒绝

- 标准 `QLineEdit`、`QPlainTextEdit`，以及关闭 `acceptRichText` 且文档无富文本格式的 `QTextEdit`。按具体控件与文档能力判断，不按应用名称定义支持。
- 自定义子类、Qt Quick、自绘编辑器、Qt5、富文本框、替换过文档对象、受限最大段落数、带 validator/inputMask 的字段不声明可写支持。
- 密码回显、隐藏/敏感输入提示、只读、禁用字段在读取正文前拒绝。字段标签、对象名与辅助描述交给共享字段策略进一步排除机器输入等场景；元数据错误仍是平台限制。
- 全文读取和选区使用 Qt 原生 UTF-16 坐标，最多 8192 个 UTF-16 单元。不截断，不模拟复制粘贴。
- 插件启动期观察控件创建。晚发现或收到任何 `QInputMethodEvent` 的控件保持 `Unknown`，拒绝正文读取、接受和写回，直到应用销毁并重新创建该控件。公共 event filter 在目标处理事件之前运行，异步 timer 与循环层级无法证明处理完成，因此不把空 preedit 或延时当作 Clear。浮层编辑区收到输入法事件后同样不再自动返回/接受；可复制草稿，关闭并重建浮层，不能以此声明中文人工编辑完成。

## 写回与浮层

描述先返回元数据；通过批准的元数据指纹和短期观察版本后才读取正文。Tab 许可绑定控件身份、进程寿命、原文、选区、输入/焦点/内容版本及截止时间。最终核验与替换在目标 UI 线程同一调用中完成，成功才消费 Tab；未尝试文本变更的拒绝让 Tab 继续交给应用。重复许可不能再次写入。

`QLineEdit` 的选区变更可能同步调用应用回调，因此插入之前再次核验文本、生命期、焦点、输入法、元数据和期限。`QTextCursor` 在原文档上替换范围，不把富文本退化成整框纯文本写回。写后不一致返回 `OutcomeUnknown` 并隔离目标，不回滚、不重试、不改写另一个字段。直接替换绑定触发时的原字段，可在该字段失焦后执行，但不激活窗口或改变前台焦点。

浮层是目标窗口的普通子控件，不创建抢焦点的顶层窗口，不需要 niri 的全局坐标。`qt-toplevel` 几何是原父窗口内的逻辑坐标，附带浮点设备缩放。窗口裁剪、移动和缩放使用实际父控件几何；连接断开、目标销毁、父窗口失活或短期续租过期时撤下。人工译文编辑由共享会话控制器批准，草稿通过原 native v2 协议往返。

## 构建与验证

构建需要 Qt6 Core、Gui、Widgets、Network 的开发包及 C++17 编译器，运行需要与该构建兼容的 Qt6 库及目标应用平台插件。构建入口仍是 `eng/build-linux.py`；不得把系统 Qt 库或插件复制为未经审计的私有发行包。

`TYPELAY_NATIVE_TESTS=ON` 构建真实 Qt Widgets fixture。以下命令在专用 Xvfb 中启动嵌套 niri，复用 Linux 原生协议测试帮助函数：

```sh
xvfb-run -a python3 tests/linux/qt_e2e.py --native artifacts/linux/native --output artifacts/linux/qt-e2e
```

测试覆盖 generic plugin 实际加载、完整文本、UTF-16 选区后台直接替换、合成器 Tab 同步提交、未拥有 Tab 的正常导航、密码读取拒绝、标准多行控件、真实控件上的受控 preedit 事件拒写。受控事件不是物理中文输入法认证；真实 fcitx/ibus 候选、Wayland 多显示器缩放、人工编辑点击定位与视觉仍需物理桌面验收。构建成功或 offscreen 插件加载不能替代这些证据。
