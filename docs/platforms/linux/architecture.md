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
