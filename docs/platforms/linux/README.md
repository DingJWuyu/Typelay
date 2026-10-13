# TypeLay Linux 客户端

目标环境：CachyOS x86_64、niri、Wayland。Linux 与 Windows 在 `trunk` 共用单份 Core、Runtime、Protocol 和 Python worker；分支规则见根 `AGENTS.md`。Linux 构建不会修改 Windows 安装包或用户数据。

## 当前可用范围与验收边界

这是正在完成桌面验证的预览客户端，不是已通过完整产品验收的 Linux 正式版。[产品验收入口](https://github.com/DingJWuyu/Typelay/blob/trunk/README.md#产品验收入口)统一规定冻结基线与已接受增量；本页能力表描述实现和证据，不降低产品要求，也不构成永久应用支持名单。

现役输入路径：

- [浏览器扩展](browser.md)：顶层 HTTP(S) 普通 input/textarea，以及已验证事务接口下的 Tiptap 单段文字/marks。Chromium/Firefox 已有实际扩展、本地主机和 Wayland+niri+真实模型链路证据，短租约续期空窗仍是风险；物理输入法、真实网页状态与撤销语义仍须验收。
- [Fcitx 合作 GTK](fcitx.md)：标准 GTK3/GTK4 Entry 与纯文本 TextView，通过真实输入上下文与事务状态授予能力；需用户明确切换到私有 daemon 并逐应用启动。通用未合作 GTK 路径保持 Unknown，仅描述非正文元数据，不捕获正文、不授予写入能力、不拦截 Tab。
- [合作 Qt6](qt.md)：标准 QLineEdit/QPlainTextEdit，复用同一宿主、模型与会话；可选包已通过实际构建、中文空格目录搬迁和 `launch --qt6` 的模型及同窗单行后台写回验证。Arch 同上游 Qt/GTK 版本的结果不代表用户 CachyOS 发行修订或实机验收。
- [VS Code 手动命令](vscode.md)：选择文字或明确选择整篇文档，经本地模型预览并确认替换。它是额外的手动功能，不监听通用输入框、不接管 Tab，不计入自动浮层对齐。

GTK 非纯文本/非标准控件、Qt QTextEdit/富文本/自绘控件、Electron/VS Code 自动浮层、更广富文本和输入法事务能力仍是开发缺口，不能标成“只需真机验证”。终端/命令区按产品策略默认排除。GTK 父窗口定位契约见 [ADR-0004](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/platforms/linux/adr/0004-native-anchors.md)。

快捷键默认在已连接的输入适配器中处理，也可将 niri 全局按键绑定到下述 --wake/--direct 命令。全局触发不会为没有输入后端的应用伪造读取或写入能力。

## 与 Windows 的功能对齐

[现役产品验收入口](https://github.com/DingJWuyu/Typelay/blob/trunk/README.md#产品验收入口)包含冻结基线及已接受的双模式/直接替换增量，Windows 的现有缺陷不是 Linux 应复制的行为。共享 `Core`/`Runtime` 只保留一份会话、许可和模型编排；输入、渲染、安装保留平台边界。下面区分实现范围和验收，不能用一端通过替代另一端。

| 功能 | 两端共同实现与 Linux 当前范围 | 尚需对齐或验证 |
| --- | --- | --- |
| 自动/按需 | 共用自动持续翻译、按需目标唤醒和一次性直接任务；浏览器、合作 GTK3/GTK4 与 Qt6 普通字段已有隔离模型/桌面证据 | 通用 Electron/VS Code 自动输入路径未完成；实际应用与长期运行仍须验收 |
| 快捷键 | 已实现可配置 F9/F10 组合，目前 Tab/Esc 固定；Linux 提供适配器触发及 niri CLI 入口 | niri 用户绑定不会随设置自动更新，Linux 缺全局冲突反馈；共同快捷键缺口见产品说明 |
| 浮层 | 胶囊、状态、展开、复制、重译、隐藏、排除和语言入口均有实现；Linux 使用原窗口/页面内定位 | 更多菜单布局尚不完全一致；目标桌面缩放、滚动、边缘和主题仍需验收 |
| 编辑/接受 | 共用人工草稿、第一 Tab 回源、重新核验后第二 Tab 接受；写回默认需显式开启 | 物理键盘、真实应用快速切焦点与各控件原生撤销仍须逐路径验证 |
| 直接替换 | 绑定原目标生命周期，在源事件线程写入；实际类型恰为标准 GTK3/GTK4 Entry、Qt QLineEdit 的字段已验证同窗后台写原字段、保持另一字段焦点/正文及原文冲突拒绝 | TextView/QPlainTextEdit 后台与跨窗口/应用后台未覆盖；不能把前台多行通过外推为后台能力，也不能转写新焦点 |
| IME/焦点 | 未知状态不授予编辑/接受资格，源框与浮层属于同一会话 | Linux 合作 Fcitx 需用户手动切换私有会话，仍未达到无需输入法协作的通用体验 |
| 设置/托盘与暂停 | 启停、模式、方向、交换、排除、主题、启动项、模型状态和退出已实现，托盘保留恢复入口 | Linux 设置布局仍有差异，平台菜单形式不要求相同；共同暂停交互缺口见产品说明 |
| 安装/首次使用 | 显式模型安装、校验后启用、设置持久化、单会话与退出清理已有实现 | Linux 已实现下载暂停/验证后续传与显式本地试译引导；独立 GTK 实操窗口复用生产自动胶囊/两阶段 Tab，需已启用合作组件；普通未合作环境仅提供接入指引，真实实操结果以对应提交 CI 为准 |

共同未完要求统一见[产品开发缺口](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/product/README.md#当前共同开发缺口)，不属于平台天然限制或仅待真机测试。共享边界见 [架构约束](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/engineering/architecture.md)，Windows 未结问题见 [Windows 待办](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/platforms/windows/open-issues.md)。

源码开发、架构和产品基线链接指向仓库在线文档；browser.md、fcitx.md、qt.md、vscode.md 随发行包提供，可离线查阅接入步骤。

## 获取发行包

从当前提交成功的 TypeLay checks 运行下载 TypeLay-Linux-preview-x86_64.tar.gz 及校验和。需要源码构建时，依赖与命令见[Linux 开发说明](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/platforms/linux/development.md#构建)。发行包自带 .NET 运行时；GTK、PyGObject、Ayatana 等桌面依赖仍由系统包管理器提供。

## 安装、启动与首次配置

```fish
cd artifacts
sha256sum -c TypeLay-Linux-preview-x86_64.tar.gz.sha256
tar -xzf TypeLay-Linux-preview-x86_64.tar.gz
cd TypeLay-Linux-preview
python install.py install
~/.local/bin/typelay --settings
```

安装是用户级操作，不使用 sudo、不改 fish 配置。启动器位于 `~/.local/bin/typelay`，应用代码位于 `~/.local/lib/typelay/releases/`，`current` 指向当前版本。安装会写入应用菜单入口，并在重复安装同一版本时校验已安装文件。若已有版本损坏，安装会拒绝复用，不覆盖可能仍被运行中进程使用的文件；退出所有 TypeLay 会话后，从完整解压包先卸载再安装，默认保留设置和模型。未将 `~/.local/bin` 加入 PATH 时，可继续使用以上完整路径。

在设置窗口点击“安装本地翻译组件…”并确认下载，或显式执行：

```fish
~/.local/bin/typelay --install-models
```

此命令会从 Astral 下载私有 CPython 3.13.16、从 PyPI 安装固定直接依赖及解析后的传递依赖，从 Argos 下载 1.9 版中英双向模型。需要网络和数 GB 可用空间。下载、提取与真实双向翻译自检完成后，才原子切换到新组件目录；失败保留原版本。命令行安装完成后，退出并重新启动正在运行的客户端以加载新组件；设置窗口发起的安装会请求当前服务重新加载。

Argos 发布源未提供可用的固定发布者摘要时，安装器记录 HTTPS 首次下载的 SHA-256，并在后续加载时检查文件完整性。这不能替代独立发布者签名，也不应表述成已验证供应链真实性。依赖锁、模型库存清单、自检结果保存在组件版本目录中。旧组件版本保留，避免正在使用旧版本的进程失去文件。

设置中的“暂停安装”会停止本次安装进程族并保留已验证下载进度；再次安装时，仅在资源验证器、长度及缓存校验一致时续传，否则安全重下。退出期间不会切换未完成的组件，已有可用版本继续保留。

首次使用从“应用支持”开始：先准备模型，再连接应用，最后点击“试一试”。本地试译教程在自有练习框显式生成译文，不自动改写原文；只有明确点击“完成”或“跳过”才保存该提示流程状态，不代表真实应用验收已完成。可从设置重新打开，或执行 `~/.local/bin/typelay --tutorial`。

完整实操使用独立标准 GTK 输入框，所有候选、编辑和 Tab 由同一个正式后台处理。先按 [合作组件说明](fcitx.md) 明确启用私有会话，再从该包目录运行：

```fish
python fcitx-cooperative.py launch ~/.local/bin/typelay --practice
```

练习窗继承这次明确启动的输入法环境，不自动切换 daemon。普通 `typelay --practice` 也能打开窗口，但没有合作能力时不会生成可接受候选。选择自动模式、语言方向并开启原生替换后，分别练习一次 Tab 和编辑后的两阶段 Tab；“我已完成练习”是用户明确确认，关闭窗口不会替用户标记完成。

选择源语言和目标语言（自动识别/中文/英语），选择自动或按需模式。需要 Tab 写回时启用“原生输入框替换”。用户排除项使用可执行文件名，一行一个。不允许密码、敏感字段或未知输入法状态绕过安全约束。

## 连接目标应用

包含 `support/desktop/component.json` 的完整发行包可在“应用支持”中选择“桌面普通输入框”，确认安装后准备随包合作组件。准备过程只校验和展开文件到用户数据目录，保留源码与许可证，不下载组件、不执行包内程序或切换当前输入法。安装来源的信任来自你取得的完整 TypeLay 主包，文件清单不是发布者签名。没有附带该组件的 setup-only 包继续提供手动接入说明。

“已准备”尚不表示应用已连接。准备后可明确选择“连接应用…”进行 TypeLay 自有 GTK 输入框练习：这会启动本包的私有输入法并打开随包练习窗口，使用独立练习配置和词库，不复制个人输入法数据。已有 Fcitx/IBus 时会先解释占用，必须由你保存工作并自行退出原输入法；当前入口不会切换现有会话，也不会让已经运行的其他应用自动获得支持。高级手动应用启动仍见下列合作组件说明。

关闭 TypeLay 不会关闭练习窗口或结束该私有输入法。正常关闭练习及其子窗口后，可在同一设置页确认“移除接入…”：只停止已验证由 TypeLay 启动的私有输入法并移除受管组件文件，保留模型、设置及练习配置；不会自动恢复原输入法，需要你自行启动。运行中应用仍使用组件时拒绝移除。监督进程异常退出、无法证明后代已结束时，本次系统启动内保持使用中保护，重启系统后再移除，不能据锁文件空闲猜测安全。准备失败保留原组件版本，可以从完整主包重新准备。

当前实际输入路径请按 [浏览器连接说明](browser.md) 安装扩展并注册本地主机，再运行 TypeLay。

单独运行 `typelay launch 原命令` 只启用 GTK 模块诊断，不会使通用 GTK 字段获得读取或替换能力。需要合作路径时，按 [Fcitx 说明](fcitx.md) 通过已验证的私有会话与逐应用启动器运行。启动器仅为子进程添加 GTK3_MODULES，保留已有模块与输入法配置，不全局更改桌面环境或替换用户输入法。

自动模式：聚焦支持的输入框后显示胶囊，输入后按共享防抖规则翻译。绿色就绪且底部明确显示“Tab 接受译文”时，Tab 才由 TypeLay 接受；否则仍属于原应用。点击译文编辑，第一次 Tab 返回原输入框，第二次 Tab 才接受。展开/收起、重译、Esc 隐藏和语言方向设置位于浮层。

按需模式：聚焦时不自动显示或翻译。Ctrl+Shift+F9 唤醒当前输入框；Ctrl+Shift+F10 冻结当前选择范围或整段文本并直接翻译替换，不改变工作模式。即使用户随后切换输入框，也不会把结果写入新的焦点；原目标内容改变、关闭或失去写入权限时拒绝写回。

Qt 显式命令使用原目标的限时授权，真实异步输入退休并重新证明 Clear 后才捕获正文；取消、超期或原文变化都不能转读新焦点。具体待捕获与后台写回规则见 [Qt 路径](qt.md#输入法与写回边界)。

快捷键可在设置中修改。输入法组合中的按键不会被当作 TypeLay 命令。输入法状态不能被确认时，不以“看起来没有候选窗”作为替换许可。

需要合成器级快捷键时，可自行在 niri 配置的 binds 区块加入以下内容，路径替换为自己的绝对路径：

```kdl
binds {
    Ctrl+Shift+F9 repeat=false { spawn "/home/你的用户名/.local/bin/typelay" "--wake"; }
    Ctrl+Shift+F10 repeat=false { spawn "/home/你的用户名/.local/bin/typelay" "--direct"; }
}
```

命令只使用触发时窗口对应的已批准目标与短期原生租约，再由目标事件线程核对字段身份、原文/焦点版本及 IME。切焦点、超期、未知输入法或尚无已连接字段时返回非零，不改为读取后来聚焦的控件。此配置由用户决定；程序不改写 niri 配置。更改设置中的应用内快捷键不会自动修改 niri 中的绑定。

## 托盘与快速操作

客户端通过 `libayatana-appindicator` 向当前 D-Bus 会话的 StatusNotifier 托盘面板注册。使用 Waybar 时，需要在自己的 Waybar 配置中启用 `tray` 模块；TypeLay 不安装面板、不修改 Waybar 或 niri 配置。已有面板配置应保留，只添加缺失的模块。

托盘菜单提供启用/暂停、自动/按需模式、源/目标语言、交换语言、模型就绪状态、设置和退出。暂停时保留灰色图标和恢复入口，不把“暂停”变成“退出”。中键打开设置。自动检测不能作为目标语言，因此源语言为自动识别时，交换入口不可用；选定固定源语言后再交换。上述操作只修改共享运行时的设置，不写入任何输入框，不改变原生写回的安全约束。

没有托盘库或没有运行托盘面板时，设置窗口明确提示原因；应用菜单、`--settings` 和 `--quit` 仍可使用。面板稍后启动或重启时由 Ayatana 重新建立连接，不需要重启翻译服务。托盘图标随包提供，不依赖用户图标主题恰好包含某个图标。

## 诊断、退出、登录启动

```fish
~/.local/bin/typelay --doctor
~/.local/bin/typelay --background
~/.local/bin/typelay --settings
~/.local/bin/typelay --quit
```

`--doctor` 输出安装、niri 版本和本会话的服务状态，不包含正文、译文、窗口标题或进程参数。`trayAvailable` 表示托盘库可用，`trayConnected` 表示与面板完成注册，不代表图标已通过视觉验收。它不是兼容性认证；`nativePeers` 大于零只说明有应用连接，不代表该应用的全部控件可读取或替换。

“登录 niri 时启动”写入 XDG autostart 文件，需要当前 niri 会话启动 XDG 自动启动目标。使用 `niri-session` 的 systemd 集成路径可按 niri 的官方集成文档配置；其他启动方式可在 niri 配置中显式加入：

```kdl
spawn-at-startup "/home/你的用户名/.local/bin/typelay" "--background"
```

必须替换成真实绝对路径，不要同时配置两种自动启动方式。启动器有每个 Wayland 会话的单实例锁，重复后台启动不会额外打开设置。

数据位置遵循 XDG：配置 `$XDG_CONFIG_HOME/typelay`（默认 `~/.config/typelay`），组件 `$XDG_DATA_HOME/typelay`（默认 `~/.local/share/typelay`），缓存 `$XDG_CACHE_HOME/typelay`（默认 `~/.cache/typelay`）。运行时 socket 和锁位于 `$XDG_RUNTIME_DIR/typelay` 下的会话目录，仅同一用户可访问。

## 卸载

退出所有 Wayland 会话中的 TypeLay，再从解压包执行：

```fish
python install.py uninstall
```

默认保留设置、模型和缓存。显式删除这些数据：

```fish
python install.py uninstall --purge
```

卸载只删除 TypeLay 程序及其菜单/自动启动入口，不删除通过 `launch` 启动的目标应用，也不修改用户的 fish/niri 配置。已安装启动器缺失时仍可从完整解压包卸载，但活动会话的锁检查不会跳过。手动加入 niri 的 `spawn-at-startup` 或应用启动命令需要同步移除。仍有会话占有锁时，卸载拒绝操作；组件安装占有锁时，`--purge` 拒绝删除组件数据，不使用进程名批量杀死其他程序。

## 开发与验收

构建、发行包验证、托盘与实际桌面验收统一见[Linux 开发说明](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/platforms/linux/development.md)；内部共享、输入能力与坐标契约见[Linux 架构](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/platforms/linux/architecture.md)。本页能力表不代替对应提交的执行证据。
