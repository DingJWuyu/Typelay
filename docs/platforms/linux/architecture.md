# Linux 宿主与合作输入边界

共享端口与规则见[工程架构](../../engineering/architecture.md)。本页描述 src/linux/ 内的平台职责；构建与验证见[开发说明](development.md)，用户安装与当前能力见[客户端说明](README.md)。

## 宿主与平台内共享

`src/linux/TypeLay.Linux/` 是 Linux .NET 组合根，直接装配共享 Runtime，不复制会话控制器。其 .NET 宿主拥有串行事件循环、类型化输入/视图端口及 worker 生命周期；同级 `src/linux/desktop/` 的 GTK 进程负责 niri IPC、同用户 socket、设置界面和 Ayatana 托盘。Linux 子进程监督和 XDG 安装路径不进入共享层。

`src/linux/native/` 的 `gtk3/`、`gtk4/`、`qt6/` 分别拥有各 toolkit 的源控件与父窗口内浮层，`common/` 保留实际共用的原生实现，`fcitx5/` 保留输入法合作接口；根目录只负责总构建和子进程监督。普通 GTK API 无法证明异步 IME 空闲，通用路径保持 Unknown。可选 Fcitx 合作组件提供真实 context、在途状态、输出序号与字段绑定，只向经过验证的标准控件授予能力。GTK 两代共用私有 TextBuffer 写入实现，平台对象/元数据检查仍归各自模块；Qt 使用实际 QObject/provider 与源 GUI 线程，不复用 GTK 控件身份。标准控件、前台与后台写回的不同范围见 [Linux 能力表](README.md#与-windows-的功能对齐)及 [Qt 路径](qt.md)。

`src/linux/browser/` 的扩展在真实文档事件线程处理标准 DOM 输入；单段 Tiptap 富文本通过有界 MAIN-world 桥调用源编辑器事务，隔离脚本持有 IME 与最终写入许可，经浏览器拥有的 native-messaging 子进程进入相同宿主端口。宿主独立验证同用户进程祖先与启动寿命，不接受页面声明的 PID。GTK/Qt 父窗口和浏览器视口坐标明确分开，只有对应的 renderer 使用，不能混为屏幕全局坐标。GTK 锚点契约见 `docs/platforms/linux/adr/0004-native-anchors.md`，浏览器部署/限制见 `docs/platforms/linux/browser.md`。同级 `src/linux/vscode/` 仅提供手动预览确认替换，不代表自动浮层后端。

`eng/linux/cooperative/` 拥有合作组件的固定输入、补丁、生产构建和 Qt 运行时探针；`packaging/linux/fcitx-cooperative/` 负责产物、对应源码/许可及逐应用启动器。测试 runner 调用这些构建入口，不再拥有生产 recipe。当前部署与验收边界见 `docs/platforms/linux/README.md`、`docs/platforms/linux/fcitx.md`。

## 生产构建与测试归属

`tests/linux/` 拥有 GTK/Qt 原生 fixture、合作输入回归、浏览器/VS Code 场景和发行包验证。`fcitx_cooperative/run.py`、`qt_cooperative/run.py` 调用 `eng/linux/cooperative/` 构建，再执行隔离输入事务断言；生产构建不导入或调用测试 runner；打包独立核验构建来源，Qt 可选发行仍要求同一构建的真实异步输入回归记录。

发行根由 eng/linux/build.py 装配，安装使用用户级 XDG 布局。GTK/Qt 共用的合作输入证据不等于全平台通用能力；GTK 两代真实共用的原生实现留在 Linux native 内，不能上提为与 Windows 共用的规则。

## 输入与坐标依据

`src/linux/desktop/niri.py` 当前专属 niri，通过其 IPC 获取前台状态。会话身份与 socket 路由仍在 Python 与原生模块之间共同依赖 WAYLAND_DISPLAY/NIRI_SOCKET；目录分层没有解除这项跨语言绑定，也未形成合成器无关实现。浏览器在文档事件线程维护目标身份、组合生命周期和同步写入事务。GTK 原生父窗口浮层的定位机制不解决输入法事务可观察性，因此通用 GTK 路径保持 Unknown。

后续原生实现需要输入框与实际输入法框架共同提供可验证的事务状态，包括已发出尚未完成的异步请求，以及最终读取/写入的原子边界。当前 Fcitx 合作实现关联真实输入上下文、已发出与已应用输出序号，以及拼音旧提交、键盘组合和 QuickPhrase 活动状态；仅查询“当前是否有候选”仍不够。写入前完成远端授权，正文修改期间只做同一控件线程的本地状态核验。更广控件、其他引擎和第三方 addon 仍需实现及真实回归，不能只靠最终用户手工确认来消除。

GTK 的 [输入法菜单](https://github.com/GNOME/gtk/blob/gtk-3-24/gtk/gtkimmulticontext.c) 可直接切换内部上下文，而控件的 im-module 属性只表示请求配置。[Fcitx GTK 实现](https://github.com/fcitx/fcitx5-gtk/blob/master/gtk4/fcitximcontext.cpp) 包含异步按键请求。不能通过强制 Simple、模拟粘贴或放宽 IME 断言隐藏这个缺口。

参考：[niri IPC](https://niri-wm.github.io/niri/IPC.html)、[niri 集成](https://niri-wm.github.io/niri/Integrating-niri.html)、[Ayatana AppIndicator](https://github.com/AyatanaIndicators/libayatana-appindicator)、[Waybar tray](https://github.com/Alexays/Waybar/wiki/Module:-Tray)、[GTK3 输入法](https://docs.gtk.org/gtk3/class.IMContext.html)、[uv Python 安装](https://docs.astral.sh/uv/guides/install-python/)。

## 浏览器短租约风险

Linux 浏览器链路仍有短租约到期与续期之间的可用性空窗风险；某次模型/Tab 回归通过不能证明所有调度条件稳定。保持到期拒绝，不延长授权或放宽断言掩盖。当前实现缺口统一见[Linux 能力表](README.md#与-windows-的功能对齐)。

## 按需唤醒与缺失捕获

[ADR-0002](../../adr/0002-on-demand-and-direct-write.md) 将唤醒资格绑定到当前目标的焦点家族；原输入框和 TypeLay 编辑器属于同一家族。当前共享控制器仍存在可用性缺口：显式唤醒后，同一控制器代次的 `CaptureAsync` 返回空快照会经过 `StopSession("capture-empty")`，撤销会话及唤醒资格。之后即使成功捕获原字段，也保持按需静默。通过现有 Runtime 宿主端口可确定性区分这一路径：先唤醒、后启动并完成空捕获，随后同目标的新鲜捕获不启动翻译；若空捕获在唤醒前启动，其旧控制器代次被丢弃，随后新鲜捕获可以启动翻译。这是生产控制器时序证据，不是原生桌面验收。

当前诊断缺少空捕获请求/完成代次以及无会话时的终止记录，不能将此机制认定为已观察桌面故障的唯一原因。

`InputCapture(null)` 不携带原字段身份、原生焦点版本或缺失原因，默认 `Focus=Other`。仅在恢复时看到相同 `TargetId` 或 `GetFocus=Source`，不能证明缺失期间没有离开再返回：Linux 的焦点摘要依赖已绑定字段和 peer，Windows monitor 的这一查询只比较顶层窗口。Linux 快照的 `FocusEpoch` 来自原生 `focusRevision`，桌面的 `ForegroundEpoch` 与 `WindowEpoch` 另有观察范围；Windows provider 的 `FocusEpoch` 由捕获和清理推进。它们不能不经平台契约核实就互相替代。GTK 的 `TargetId.Lifetime` 属于模块寿命，socket 重连并不必然改变它，也不能单独证明 peer 连接连续。

在改变当前拒绝语义前，最小恢复依据必须同时证明：

- 原字段身份及寿命一致，承载它的已认证 peer/连接期间未替换或断连。
- 缺失期间始终属于原焦点家族，版本足以排除离开再返回；新鲜恢复捕获来自该原字段，不能只比较前后两个焦点摘要。
- 命令配置、控制器请求/完成代次和设置仍对应同一次显式唤醒；模式、设置、暂停、释放、字段切换或 peer 生命周期变化均终止旧资格。

若以后允许保留唤醒意图，缺失期间也只能保留上述无正文依据；会话正文、预览、草稿、候选、视图命令和接受许可必须撤销，旧异步结果失效。恢复后重新捕获并独立验证字段策略、IME、编辑与写入能力，不重放快捷键、不恢复旧候选、不自动写回。当前端口尚未提供可统一依赖的连续性保证，继续保持空捕获终止唤醒，不以等待、延长 TTL 或重复触发掩盖该缺口。

## 暂停的元数据观察

暂停使用独立的 `PauseAnchor` 和 `ObservePauseAsync`，不把空正文伪装成 `InputSnapshot`。Python 只请求现有 `describe`，经宿主暂停专用字段策略检查后返回身份、几何与安全状态；正文 `Capture` 仍受启用状态和配置代次限制。GTK3、GTK4、Qt6 与浏览器分别渲染弱化入口，点击绑定当前目标和视图代次；共享控制器再观察元数据，仅更新最新设置的 `Enabled`。

暂停清除会话、候选、草稿并撤销可执行许可。原来已经接受的事务仍短期保留核验回执所需的事实，不用于暂停锚点、显示或授予新写入。现有定时清理以一秒为目标窗口，调度延迟可能推迟清理；这不是进程内所有正文副本的硬实时销毁上限。到达的真实回执与新的写入授权不能混为一谈，结果未知仍隔离且不自动重试。
