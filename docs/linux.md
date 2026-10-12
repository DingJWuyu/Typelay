# TypeLay Linux 客户端

目标环境：CachyOS x86_64、niri、Wayland。源码和提交只在 `cachyos-preview`。Linux 构建不会修改 Windows 安装包或用户数据。

## 当前可用范围与验收边界

这是正在完成桌面验证的预览客户端，不是已通过完整产品验收的 Linux 正式版。现有产品规格仍是验收依据；下列缺口不是新的产品规格或永久支持名单。

客户端复用 Core、Runtime 和 Python worker，提供自动/按需模式、翻译预览及编辑、原框返回、Tab 接受、按需快捷键、绑定原框的直接替换、应用排除、设置持久化及本地模型安装。Linux 桌面集成使用 GTK3 设置界面、Ayatana 托盘、niri IPC 和绑定源控件的 GTK3/Qt6 原生浮层与输入适配器。是否真正可用由每个控件的元数据、输入法状态、窗口身份、几何、版本和时效共同决定，不能仅凭编译成功判断。

GTK 原生后端覆盖标准 GTK3 GtkEntry / GtkSearchEntry / GtkTextView（普通无富文本标记的缓冲区）。应用必须在启动时加载模块，已经运行的实例不能通过再次打开窗口补加载。浏览器后端通过单独明确安装的扩展覆盖 Firefox/Chromium 顶层网页普通 input/textarea，详见 [浏览器连接与限制](browser-linux.md)。Qt6 标准 Widgets 通过附加 generic plugin 接入受限非 IME 路径，收到输入法事件的控件会停用至重建，支持边界见 [Qt6 连接与限制](qt6-linux.md)。GTK4、Qt5/Qt Quick、Electron/VS Code、富文本尚无输入后端；终端/命令区按产品策略默认排除。这些缺口仍是未完成的功能覆盖，不可描述成通用输入框支持。

同一 GTK3 进程的多个窗口可分别绑定原生控件；预览和编辑要求活动父窗口，直接替换保持绑定触发时的原控件。浮层使用 GTK 父窗口坐标，不依赖 niri 缺失的平铺窗口绝对位置，也不猜测屏幕坐标。控件身份、父窗口状态、输入法状态或锚点不能确认时拒绝操作。定位契约见源码中的 `docs/adr/0004-linux-native-anchors.md`。

快捷键默认在已连接的输入适配器中处理；也可将 niri 全局按键绑定到下述 --wake/--direct 命令。默认 Ctrl+Shift+F9 唤醒、Ctrl+Shift+F10 直接替换，仅按需模式生效。全局触发不会为没有输入后端的应用伪造读取/写入能力。所有应用的兼容性、物理快捷键与托盘视觉一致性仍需实际验证。

## 构建

在目标机器安装依赖（fish 可直接运行）：

```fish
sudo pacman -Syu --needed git base-devel cmake ninja pkgconf dotnet-sdk gtk3 json-glib python python-gobject libayatana-appindicator niri uv qt6-base qt6-wayland
```

.NET SDK 版本须满足仓库 `global.json`。普通用户使用自包含发行包，不需要 .NET SDK。GTK、PyGObject、Ayatana 等桌面库由系统包管理器维护；翻译 worker 使用独立 CPython，不修改系统 Python。

```fish
git clone --branch cachyos-preview --single-branch git@github.com:DingJWuyu/Typelay.git
cd Typelay
python eng/build-linux.py
```

输出：`artifacts/TypeLay-Linux-preview-x86_64.tar.gz` 及对应 `.sha256`。包内包括自包含 .NET 宿主、GTK3/Qt6 模块、浏览器扩展及本地主机、子进程监督程序、桌面代码、共享 worker、安装脚本和文件校验清单。模型不在包内，也不会在首次启动时无提示下载。

构建时的外部 .NET SDK/NuGet 获取仍需要网络。模型安装的原生 Python 依赖固定直接版本，通过 uv 为 CPython 3.13 解析 Linux wheel 并生成带哈希的依赖锁。安装记录保留实际解析版本，不声称不同时间解析出的传递依赖完全相同。

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

选择源语言和目标语言（自动识别/中文/英语），选择自动或按需模式。需要 Tab 写回时启用“原生输入框替换”。用户排除项使用可执行文件名，一行一个。不允许密码、敏感字段或未知输入法状态绕过安全约束。

## 连接目标应用

先运行 TypeLay，再使用启动器启动一个全新的 GTK3 或 Qt6 Widgets 应用进程：

```fish
~/.local/bin/typelay launch mousepad
```

`mousepad` 是操作示例，不是内置应用白名单，需要自行安装。应先彻底退出原应用；某些应用会通过 D-Bus 把新启动请求转交给旧进程，这样旧进程仍不会加载模块。

启动器只给该应用进程传入 GTK3_MODULES 和附加 Qt generic plugin 路径，保留已有模块和 QT_IM_MODULE；不会全局修改桌面环境、设置 LD_PRELOAD、注入现有进程、模拟粘贴或修改剪贴板。应用退出后该集成自然结束。为已确认兼容的日常应用创建启动项时，可将命令改为 `~/.local/bin/typelay launch 原命令`。

自动模式：聚焦支持的输入框后显示胶囊，输入后按共享防抖规则翻译。绿色就绪且底部明确显示“Tab 接受译文”时，Tab 才由 TypeLay 接受；否则仍属于原应用。点击译文编辑，第一次 Tab 返回原输入框，第二次 Tab 才接受。展开/收起、重译、Esc 隐藏和语言方向设置位于浮层。

按需模式：聚焦时不自动显示或翻译。Ctrl+Shift+F9 唤醒当前输入框；Ctrl+Shift+F10 冻结当前选择范围或整段文本并直接翻译替换，不改变工作模式。即使用户随后切换输入框，也不会把结果写入新的焦点；原目标内容改变、关闭或失去写入权限时拒绝写回。

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

## 验证

CI 将共享测试、Windows 原生/打包检查、Arch 容器中的 Linux 构建、发行包生命周期及嵌套 niri/GTK3/Qt6 及浏览器测试分开执行。查看当前提交对应 Actions 的实际结果；失败的任务不算通过，旧提交成功也不能证明新提交通过。

本地最小静态检查：

```fish
python -m pip install -r eng/requirements.txt
python -m ruff check src/TypeLay.Linux/desktop packaging/linux eng/build-linux.py tests/linux
dotnet build src/TypeLay.Linux -c Release
```

发行包回归使用实际构建产物，从含中文和空格的新目录安装，验证重复安装、完整性校验、卸载保留数据、显式清理及活动会话/组件安装锁。测试使用独立 HOME 和 XDG 目录，不下载模型，也不连接用户桌面：

```fish
python eng/build-linux.py
env TYPELAY_LINUX_PACKAGE="$PWD/artifacts/TypeLay-Linux-preview-x86_64.tar.gz" python tests/linux/distribution_e2e.py -v
```


托盘控件回归不安装模型，使用实际 GTK、Ayatana、已构建的共享宿主和隔离的 D-Bus 面板端点：

```fish
env GDK_BACKEND=x11 dbus-run-session -- xvfb-run -a python tests/linux/tray_e2e.py --package artifacts/linux/package
```

该回归验证注册、菜单动作、暂停/恢复和持久化，不等同于真实 Waybar、物理鼠标或目标桌面视觉验收。缺少 PyGObject、Ayatana 或显示服务时应报告未执行，不能记为通过。

嵌套测试需要独立 Xvfb、niri、Mesa 软件渲染、D-Bus 和 xdotool。CI 的 XTest 只向独立嵌套合成器注入测试按键，不是生产客户端的输入方案。原生测试验证受控 GTK3/Qt6 控件；Chromium DOM、本地主机与完整桌面链路有独立用例，不能冒充 Firefox 或物理键盘实测。

物理 CachyOS + niri 验收必须记录系统/niri/GTK/PyGObject/输入法版本，并逐项保留结果：

| 项目 | 操作与预期 | 尚需验证的范围 |
| --- | --- | --- |
| 自动/按需模式 | 同一普通字段分别聚焦、输入、唤醒，按需模式不得自行翻译 | 真实 GTK3 应用与长期运行 |
| 原文读取 | 单行、多行、emoji、选区、删除和粘贴；密码/排除项不被读取 | 实际应用敏感元数据 |
| Tab 语义 | 有许可才写回；编辑后一次 Tab 只返回，第二次才接受 | 物理键盘、重复键、快速切焦点 |
| 直接替换 | 触发后立即换框、改原文、关原窗口 | 原控件身份、取消及无误写 |
| 输入法 | Fcitx5/IBus 中文组合、候选选择、取消、切换输入法 | 组合期不拦 Tab、不读半成品、不写回 |
| 几何/视觉 | 平铺滚动、浮动、窗口动画、不同缩放屏幕、屏幕边缘 | 无遮挡输入框、无抢焦点、主题一致 |
| 托盘 | Waybar 启用 tray，测试暂停/恢复、切换模式、交换语言、中键设置、面板重启、退出 | 真实面板点击、主题与图标清晰度 |
| 完整生命周期 | 模型安装、断网、退出、重复启动、登录启动、卸载 | 无孤儿模型进程、无遗留键盘占用 |
| 网页普通字段 | Firefox/Chromium 扩展安装、input/textarea、切标签页、DOM 重建和框架受控值 | native messaging 路由、真实网页状态与撤销栈；详见浏览器说明 |
| 其他输入框 | Electron/VS Code、GTK4、Qt5/Qt Quick、富文本 | 当前没有后端，不能标记通过 |

未经实际执行的项目保持未验证。源码中存在实现、单元测试通过和嵌套桌面通过是三个不同层次的证据。

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

## 设计依据

niri IPC 提供合成器的前台状态；非模态 GTK 原生浮层绑定真实源控件，在父窗口内定位，不占用独立平铺窗口。观察文本不等于可以安全改写，裸 Tab 的所有权和 IME 状态也不是 GlobalShortcuts portal 提供的文本事务，因此未使用“AT-SPI 能读文本”推导“可以通用模拟粘贴”。当前 GTK3 后端在目标应用事件线程同步校验并写入，返回一次性回执，共享控制器不会重复执行已经完成的写入。

参考：[niri IPC](https://niri-wm.github.io/niri/IPC.html)、[niri 集成](https://niri-wm.github.io/niri/Integrating-niri.html)、[Ayatana AppIndicator](https://github.com/AyatanaIndicators/libayatana-appindicator)、[Waybar tray](https://github.com/Alexays/Waybar/wiki/Module:-Tray)、[GTK3 输入法](https://docs.gtk.org/gtk3/class.IMContext.html)、[uv Python 安装](https://docs.astral.sh/uv/guides/install-python/)。
