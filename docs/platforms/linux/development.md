# Linux 构建与验证

本页拥有 Linux 主客户端的依赖、构建与验收方法。安装和使用见[客户端说明](README.md)，固定合作组件的构建、打包和回退见[Fcitx 说明](fcitx.md)；共享依赖与 CI 约定见[工程开发流程](../../engineering/development.md)。

## 构建

在目标机器安装依赖（fish 可直接运行）：

```fish
sudo pacman -Syu --needed git base-devel cmake ninja pkgconf dotnet-sdk gtk3 gtk4 json-glib python python-gobject libayatana-appindicator niri uv
```

.NET SDK 版本须满足仓库 `global.json`。普通用户使用自包含发行包，不需要 .NET SDK。GTK、PyGObject、Ayatana 等桌面库由系统包管理器维护；翻译 worker 使用独立 CPython，不修改系统 Python。

```fish
git clone --branch trunk git@github.com:DingJWuyu/Typelay.git
cd Typelay
python eng/linux/build.py
```

输出：`artifacts/TypeLay-Linux-preview-x86_64.tar.gz` 及对应 `.sha256`。包内包括自包含 .NET 宿主、GTK3/GTK4 原生模块、浏览器扩展及本地主机、可选 VS Code VSIX、子进程监督程序、桌面代码、共享 worker、安装脚本和文件校验清单。模型不在包内，也不会在首次启动时无提示下载。

上述普通构建是 setup-only 包。完整桌面接入包在同一干净提交完成 [合作组件构建与打包](fcitx.md#构建与打包) 后，再执行：

```fish
python eng/linux/build.py --finalize --cooperation-package artifacts/TypeLay-Fcitx-cooperative.tar.gz
```

finalize 校验主包原始清单、构建提交、合作组件的固定来源及对应源码/构建输入，把原始合作压缩包和描述文件放入 `support/desktop/`，然后更新主包清单与归档，不重新编译。必须使用同一轮受控构建产物；自带哈希只证明完整性，不认证未知包的发布者。完整包保留合作组件要求的源码和许可证，不含模型。CI 对最终归档再验证搬迁、安装与设置所用准备接口。

构建时的外部 .NET SDK/NuGet 获取仍需要网络。模型安装的原生 Python 依赖固定直接版本，通过 uv 为 CPython 3.13 解析 Linux wheel 并生成带哈希的依赖锁。安装记录保留实际解析版本，不声称不同时间解析出的传递依赖完全相同。

## 变更检查

修改安装/启动流程时，用实际 archive 驱动 `tests/linux/distribution_e2e.py`；修改原生输入或界面时，分别运行嵌套原生测试与生产桌面测试。后者只使用明确准备的隔离组件和真实模型；快速检查不下载模型。Python 修改后运行 Ruff check/format，Linux 原生构建与发行验证不能用 Windows check 替代。

合作组件的固定来源、版本和补丁在 `eng/linux/cooperative/` 维护，发行打包消费生产构建产物；`tests/linux/` 保留 fixture 编译、隔离运行与场景断言。更改生产 recipe 或打包清单后同时验证测试入口和实际包重建，不只检查测试 runner 能导入。

Linux CI 的模型准备在独立测试数据目录显式安装并验证真实组件，随后运行生产桌面测试，不把下载放进快速测试。

## 验证

CI 将共享测试、Windows 原生/打包检查、Arch 容器中的 Linux 构建、发行包生命周期及嵌套 niri/GTK3/GTK4 及浏览器测试分开执行。查看当前提交对应 Actions 的实际结果；失败的任务不算通过，旧提交成功也不能证明新提交通过。

本地最小静态检查：

```fish
python -m pip install -r eng/requirements.txt
python -m ruff check src/linux/desktop packaging/linux eng tests/linux
dotnet build src/linux/TypeLay.Linux -c Release
```

发行包回归使用实际构建产物，从含中文和空格的新目录安装，验证重复安装、完整性校验、卸载保留数据、显式清理及活动会话/组件安装锁。测试使用独立 HOME 和 XDG 目录，不下载模型，也不连接用户桌面：

```fish
env TYPELAY_LINUX_PACKAGE="$PWD/artifacts/TypeLay-Linux-preview-x86_64.tar.gz" python tests/linux/distribution_e2e.py -v
```

托盘控件回归不安装模型，使用实际 GTK、Ayatana、已构建的共享宿主和隔离的 D-Bus 面板端点：

```fish
env GDK_BACKEND=x11 dbus-run-session -- xvfb-run -a python tests/linux/tray_e2e.py --package artifacts/linux/package
```

该回归验证注册、菜单动作、暂停/恢复和持久化，不等同于真实 Waybar、物理鼠标或目标桌面视觉验收。缺少 PyGObject、Ayatana 或显示服务时应报告未执行，不能记为通过。

设置导航的桌面回归位于 `desktop_e2e.py` 的 setup 场景：用 niri 的实际浮动窗口与尺寸操作请求 790 × 560，再通过 AT-SPI 检查首屏主题预览、应用支持主动作与模型就绪后的折叠修复入口。原生主题单选项通过键盘保存；真实临时文件阻塞验证失败回滚与恢复后重试。侧栏选择和上下方向键遍历四页，继续验证原生开关焦点、跨页四键/排除草稿、语言即时保存、验证失败与关闭保留/放弃。现有已注册 GTK fixture 同时检查排除选择取消不改草稿、添加后的保存失败保留、保存后拒绝源捕获/浮层及逐项移除仍需显式保存；这些新增路径须在对应提交的 setup 场景执行，解析单测不代替桌面结果。GTK 暂停及 Qt 设置路径同样先选择实际页面，再操作本页控件。此回归需要下述嵌套桌面环境；静态检查、HTML 原型和共享持久化测试不能代替 GTK 交互、滚动裁剪或截图验收。设置窗口的浅深配色还需分别在浅色与深色 GTK 主题下检查，浮层主题选项不会切换设置窗口主题。

嵌套测试需要独立 Xvfb、niri、Mesa 软件渲染、D-Bus 和 xdotool。CI 的 XTest 只向独立嵌套合成器注入测试按键，不是生产客户端的输入方案。原生 --gtk-refusal 测试只验证未知 GTK 输入法的拒绝与透传，不验证 GTK 翻译功能；Chromium DOM、本地主机与完整桌面链路有独立用例，不能冒充 Firefox 或物理键盘实测。

Qt 的原生实现、provider FIFO、实际版本核对、构建和验证边界统一见 [Qt6 原生输入路径](qt.md#构建与验证)。Fcitx 的固定构建与实际打包命令见[合作组件说明](fcitx.md#构建与打包)，不在此复制第二份配方。

物理 CachyOS + niri 验收必须记录系统/niri/GTK/PyGObject/输入法版本，并逐项保留结果：

| 项目 | 操作与预期 | 尚需验证的范围 |
| --- | --- | --- |
| 自动/按需模式 | 同一普通字段分别聚焦、输入、唤醒，按需模式不得自行翻译 | 浏览器普通字段与长期运行 |
| 原文读取 | 单行、多行、emoji、选区、删除和粘贴；密码/排除项不被读取 | 实际应用敏感元数据 |
| Tab 语义 | 有许可才写回；编辑后一次 Tab 只返回，第二次才接受 | 物理键盘、重复键、快速切焦点 |
| 直接替换 | 触发后立即换框、改原文、关原窗口 | 原控件身份、取消及无误写 |
| 输入法 | Fcitx5/IBus 中文组合、候选选择、取消、切换输入法 | 组合期不拦 Tab、不读半成品、不写回 |
| 几何/视觉 | 平铺滚动、浮动、窗口动画、不同缩放屏幕、屏幕边缘 | 无遮挡输入框、无抢焦点、主题一致 |
| 托盘 | Waybar 启用 tray，测试暂停/恢复、切换模式、交换语言、中键设置、面板重启、退出 | 真实面板点击、主题与图标清晰度 |
| 完整生命周期 | 模型安装、断网、退出、重复启动、登录启动、卸载 | 无孤儿模型进程、无遗留键盘占用 |
| 网页普通字段 | Firefox/Chromium 扩展安装、input/textarea、切标签页、DOM 重建和框架受控值 | native messaging 路由、真实网页状态与撤销栈；详见浏览器说明 |
| 富文本 | Tiptap 单段选区替换，保留范围外 marks 与原生撤销语义 | 其他版本、自定义 dispatcher、多段/列表/mention 尚未覆盖 |
| 合作 GTK | GTK3/GTK4 标准 Entry、纯文本 TextView，模型、选区、编辑回源、组合与敏感字段拒绝；标准 Entry 同窗后台写回与原文冲突 | 已有隔离 niri 证据；GTK3 验证宿主 user-action 分组，GTK4 另验原生 undo/redo，实际应用及物理输入仍需验收；TextView 后台未覆盖 |
| 合作 Qt6 | QLineEdit 自动/按需、F9/F10、选区、编辑回源、同窗后台写回/冲突拒绝；QPlainTextEdit 多行、undo/redo、真实拼音、结构拒绝和应用回调隔离 | 隔离 niri 及搬迁可选包已有证据；CachyOS 发行修订、实际应用与物理输入仍须验收；QPlainTextEdit 后台及跨窗口/应用后台未覆盖 |
| 其他输入框 | 带 tags/嵌入对象及非标准 GTK 控件、Electron/VS Code 自动浮层、Qt QTextEdit/富文本/自绘控件 | 尚未实现，不能标记通过 |

未经实际执行的项目保持未验证。源码中存在实现、单元测试通过和嵌套桌面通过是三个不同层次的证据。

## 浏览器与合作输入的证据边界

- tests/linux/native_e2e.py --gtk-refusal 只检查未知 GTK 输入法状态下的正文/写入拒绝与按键透传；加 --desktop 后使用正式客户端、已经安装的真实模型及 Chromium/Firefox 的 Wayland 扩展输入链路。
- tests/linux/firefox_native_e2e.py 使用官方 Firefox/geckodriver、临时扩展和发行包 host 检查真实加载、字段读写及身份；它不代替 --desktop 的 niri+模型链路。各浏览器命令与契约见[浏览器说明](browser.md#开发验证)。
- Fcitx/Qt runner 只拥有 fixture 编译与真实输入事务断言，复用 eng/linux/cooperative/ 的生产构建。GTK/Qt 桌面回归另用正式宿主与真实 CPU 模型；可选包还须从搬迁目录经过实际启动器验证，不用源构建目录成功替代。
- 当前后台写回证据的控件/窗口范围以[Linux 能力表](README.md#与-windows-的功能对齐)为准，单次链路成功不等于物理输入或长期调度稳定。

## 按变更选择场景

trunk、两个保留的 preview 分支、Pull Request 与手动 workflow 使用共享、Windows 和 Linux 的完整检查；分支触发器不改变根 AGENTS.md 的分支权限。

本地定位可以选用上述发行包、托盘、浏览器、Fcitx、Qt 或 VS Code 的现役独立命令；检查范围由实际改动决定。共享 Core/Runtime/Protocol、宿主、启动、生产构建或打包变更仍需对应完整验证。单场景通过只证明该场景，不能代替整个平台或物理桌面验收。
