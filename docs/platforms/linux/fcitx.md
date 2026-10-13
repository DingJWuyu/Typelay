# Fcitx 合作组件预览

这套可选组件让 TypeLay 与实际 Fcitx 输入事务协作。已验证范围包括 GTK3、GTK4 的标准 GtkEntry 和原生 GtkEntryBuffer；标准纯文本 TextView 使用原生 TextBuffer，两代已有多行选区、真实模型、Tab 和 IME 实证；GTK3 验证宿主 user-action 分组，GTK4 另验原生 undo/redo。GTK4 须单独以 --gtk4 启动。Qt 的可选同版构建与启动入口见末节；非纯文本/自定义控件、通用 Electron 尚未开放。普通 TypeLay 安装不会安装或启用这些组件。

固定生产输入以 `eng/linux/cooperative/inputs.py` 为准，当前为 Core 5.1.22、Chinese Addons 5.1.14、GTK IM 5.1.7、完整私有 LibIME 1.1.16。对应组件已在隔离环境完整构建，实际库、模型路径与同版官方包均有校验。搬迁后的私有包已通过默认组件会话、配置/前台重启、原 session bus，以及嵌套 niri 的实际 CPU 模型、自动预览、一次 Tab 写回、编辑后两阶段 Tab 与拼音组合保护。同窗后台直接替换也已验证实际类型恰为标准 GTK3/GTK4 Entry 的原字段：一次真实 F10/模型任务写 A 时保持 B 的焦点、正文与选择，原文变化时拒绝；不外推到 TextView 或跨窗口/应用后台。实际按键由隔离桌面自动化送入，不能代替用户 CachyOS 桌面的物理输入验收。

## 会话与用户数据

合作需要服务端和 GTK 客户端都具备对应能力。仅加载 GTK 模块而连接未修改的系统服务端不能获得安全写入许可。

私有组件使用原 session bus。一个总线上只能有一个 `org.fcitx.Fcitx5` 服务。当前方案要求用户先完成组合输入并明确退出原输入法进程，然后手动运行私有版本。启动器发现已有服务时拒绝，不终止或替换其它进程，不改系统文件、桌面自动启动、shell 配置或全局 GTK 配置。

私有版本沿用原 HOME、XDG 与 FCITX 配置路径，因此保留输入法顺序、快捷键、皮肤和个人词库，并按原 Fcitx 行为保存学习结果。它不复制出另一份日常词库，不同步双 daemon，也不会在恢复时回滚用户的新输入学习。使用前应退出原 daemon 后备份自己的 Fcitx 配置与数据目录。自定义 FCITX_CONFIG_HOME / FCITX_DATA_HOME 优先于默认 `~/.config/fcitx5` / `~/.local/share/fcitx5`。

如果桌面监督程序自动重启原 daemon，先由用户明确暂停该启动方式；启动器不会替用户修改它。两份 daemon 不得并发访问同一 profile。已有应用可能在服务切换时重新连接，应先结束所有组合输入，按需重新启动目标应用。

官方候选窗与 X11/Wayland/原生 Fcitx 前端保留。为避免上游 IBus 兼容前端接管或终止另一套 IBus 服务，私有启动命令明确排除 ibusfrontend；仅使用 IBus 协议的应用不在这个私有会话范围内。该排除只属于私有进程命令行，不改用户配置，恢复原 daemon 后即恢复原行为。其它用户插件不被关闭。TypeLay 仅为已审计的实际加载组件与明确完成的输入事务授予能力；未知插件仍可按原输入法行为运行，但 TypeLay 保持 Unknown。不能用配置中的输入法名称或看起来为空的候选窗代替实际状态。

## 构建与打包

仅在匹配的 Arch/CachyOS x86_64 开发环境构建。官方系统依赖仍由包管理器提供；本包不替换它们。生产配方与补丁在 `eng/linux/cooperative/`，单独构建不编译 fixture、不运行桌面测试：

```sh
python eng/linux/cooperative/fcitx.py --output artifacts/linux/fcitx-build
```

发布验收使用原测试入口调用同一生产构建，然后执行真实输入事务断言，再打包对应产物：

```sh
python tests/linux/fcitx_cooperative/run.py --core-version 5.1.22
python packaging/linux/fcitx-cooperative/pack.py \
  --build-output artifacts/linux/fcitx-cooperative-5.1.22 \
  --output artifacts/TypeLay-Fcitx-cooperative.tar.gz
```

生产构建与测试分别记录输入/产物哈希，测试记录不冒充生产构建来源。各构建应使用新的输出目录，不能把已修改或旧构建的源码静默复用；默认测试产物目录保持不变。固定来源只在 `inputs.py` 维护，不另复制一套版本或补丁清单。

包内包含私有运行库、原始许可证、修改后的对应源码、补丁、固定来源校验和与重建脚本。保留整个源码与许可证目录后再分发；不要只抽取修改后的二进制。LGPL 组件的原 SPDX 声明及修改说明保留在源码中。系统库版本、构建记录与文件校验和见包内 manifest.json。

解压到独立用户目录即可，不需要安装或 sudo。以下只是用户审阅后明确执行的操作，不会由 TypeLay 自动执行。

```sh
tar -xzf TypeLay-Fcitx-cooperative.tar.gz
cd TypeLay-Fcitx-cooperative
python fcitx-cooperative.py preflight
```

preflight 校验包文件、实际 ELF 依赖、私有 Core/Config/Utils 和三个 LibIME 库的解析路径，以及 5.1.22 daemon 版本。共用原 profile 时，用户系统 Chinese Addons 必须为 5.1.14、LibIME 必须为 1.1.16，不能以构建机器的系统版本代替私有源码版本。构建机器可有较新的系统组件，但本包完整构建并链接固定私有组件，记录源码、安装库、构建机器和用户现装四类版本。GTK3/glibc 仍须满足实际动态依赖。缺失或不匹配时拒绝启动，不得忽略错误继续。

词典与语言模型由 LibIME 1.1.16 源码指定的官方归档与 SHA256 生成，安装到私有目录。私有模型搜索不回退系统模型；XDG 数据路径优先私有词典，个人数据路径仍保留。三个 LibIME 数据归档的 LGPL-2.1-or-later 与权利人依据 [Debian 官方逐文件版权声明](https://metadata.ftp-master.debian.org/changelogs/main/libi/libime/unstable_copyright)，原档、完整许可、固定 KenLM 子模块及对应源码一起保留。生成的官方码表配置改用包内相对路径，用户码表配置不修改。

Chinese 的 py_table.mb / py_stroke.mb 及两份原始归档不随本包分发，因为尚未核实两份数据的单独许可声明。它们来自用户已安装的官方 Chinese 5.1.14：preflight 核验 /usr/share/fcitx5/pinyinhelper 下两文件的固定 SHA256，原模块通过原 StandardPaths::PkgData 读取。私有启动环境保留个人数据优先级，并在末尾确保系统官方数据目录存在。缺失或被改动时拒绝启动，不静默缺少查询功能。包内保留官方下载 recipe/hash 供自行重建。

GTK IM 缓存包含安装路径，不能直接搬运 CI 缓存。每次逐应用启动都使用官方 gtk-query-immodules-3.0 为当前解压目录生成缓存，写入当前用户 XDG_RUNTIME_DIR；不修改系统 GTK 模块缓存。

## 明确切换与逐应用启用

先在原 Fcitx 托盘中明确选择退出，确认没有正在组合的文字。然后在终端运行：

```sh
python fcitx-cooperative.py daemon
```

该命令以前台方式运行私有 daemon，沿用原桌面会话和配置。保留这个终端，以便 Ctrl+C 正常退出。

另一个终端通过同一包启动 TypeLay，确保此前的 TypeLay 已正常退出，避免复用旧环境的后台服务：

```sh
python /完整路径/TypeLay-Fcitx-cooperative/fcitx-cooperative.py launch ~/.local/bin/typelay
```

目标 GTK3 应用也需要明确逐应用启动：

```sh
python /完整路径/TypeLay-Fcitx-cooperative/fcitx-cooperative.py launch ~/.local/bin/typelay launch 应用命令
```

GTK4 应用使用 `python fcitx-cooperative.py launch --gtk4 应用命令`。只有该入口校验系统 GTK4 动态依赖并设置私有 GTK_PATH；普通 GTK3/daemon 路径不因系统未安装 GTK4 而失败。GTK4 原 Fcitx 模块显式链接包内 libtypelay-gtk4.so，经实际 set_client_widget 绑定后才提供能力；库加载本身没有字段权限。隔离 niri 已验证真实模型与两阶段 Tab、拼音及 Unicode 组合保护，以及隐藏 Entry/GtkPasswordEntry 的拒绝和普通 Tab 导航。实际应用、物理输入法与缩放仍需目标机器验证。

启动器只为这些子进程设置私有 GTK IM 模块和合作开关，不替换 session bus。它检查当前服务端确实来自同一私有包。先用非敏感合成文本验证普通输入、拼音候选、取消、Backspace、切换输入法与 TypeLay Tab；未知能力时仍按普通应用行为输入，不应出现读取或写回。

## 恢复原输入法

1. 在目标应用完成或取消组合，正常退出 TypeLay 和逐应用启用的测试窗口。
2. 在私有 daemon 的终端按 Ctrl+C，等待进程退出和总线名称释放。
3. 通过原桌面启动方式或系统 `/usr/bin/fcitx5` 恢复原服务。若此前明确暂停过自动启动，按原方式恢复。
4. 正常启动应用，不经过私有包启动器。用户词库与配置仍在原位置。

不需要还原系统库、删除词库或更改全局输入法变量。确认不再使用后可删除私有解压目录；不要删除自己的 Fcitx 数据目录。

## 当前能力边界与维护依据

守卫检查实际 `loadedAddonNames()`，不是配置中列出的可用插件。每个已加载模块必须同时满足已审计的名称、官方 type/library/version，以及 loader 成功加载的真实库路径。私有启动器明确传入私有库目录；缺少来源声明、系统同名回退库或不匹配模块都会使 TypeLay 返回 Unknown。路径查询只读取 loader 已有成功结果，不改变插件加载、回退或普通输入。

当前审计版本固定为 Core 5.1.22、Chinese Addons 5.1.14。已审计组件：dbus、dbusfrontend、keyboard、pinyin、punctuation、pinyinhelper、spell、quickphrase、classicui、wayland、waylandim、xcb、xim、notifications、notificationitem、clipboard、unicode、imselector、chttrans、fullwidth、cloudpinyin、emoji、fcitx4frontend、kimpanel、virtualkeyboard。其他插件不因“属于官方包”或同名而自动继承结论。

维护时必须核对对应上游实际路径：

- `src/lib/fcitx/tempmodemanager.cpp` 的活动模式分派使用真实 per-context `isActive`。合作只读查询遍历同一状态，覆盖 QuickPhrase、剪贴板、Unicode、输入法选择模式；不能把空候选或空提示当完成。
- pinyin 的延后标点提交在原输入法切换后仍可能存活，因此同一 IC 查询已加载 pinyin 的真实 pending 状态，同时保留当前 keyboard/pinyin 与 Core compose 检查。
- `src/ui/classicui/inputwindow.cpp` 点击时重新读取当前 candidateList。classicui/notificationitem 的延迟菜单 action 只按已审计组件的实际动作处理，不缓存旧正文提交，不能仅因有 timer 就拒绝。
- `src/modules/clipboard/xcbclipboard.cpp` 与 `waylandclipboard.cpp` 的异步接收只更新剪贴板历史；真正提交发生在新按键或当前候选选择中。剪贴板活动模式仍必须拒绝。
- `src/frontend/waylandim/` 和 `src/frontend/xim/xim.cpp` 向各自 IC 路由输入，不赋予这些前端本身 TypeLay 字段权限。本包仅开放已绑定的 GTK3/GTK4 标准 Entry 或纯文本 TextView DBus context。
- Chinese `modules/chttrans` 与 `modules/fullwidth` 为同步输出过滤，保留用户繁简/全角偏好；chttrans 构建保留 OpenCC。
- Chinese `modules/cloudpinyin/cloudpinyin_public.h` 的网络结果只回填受 `watch()` 生命周期保护的候选，并刷新当前候选 UI；未完成候选的 select 直接返回，不登记迟到提交。实际提交只发生在已有结果后的真实选词中。私有包同源重编 cloudpinyin 并核实际来源，保留原云拼音开关，不启用或关闭云服务。该模块可在读取拼音配置时加载，加载本身不代表用户开启了网络查询。

源码更新、模块替换或增加新的输入/输出插件都需要重新核对这些行为。部署测试必须使用搬迁后的实际包和未被清空的正常配置；白名单隔离测试不能代替日常默认组件组合验证。

完整上游 Fcitx 的 IBus 兼容前端会尝试接管 IBus 服务。因此私有进程明确排除该兼容前端，并在启动前检查 session bus 的 IBus owner 和当前用户的 ibus-daemon 进程；检测到活动 IBus 或无法确认时拒绝。原 daemon 的关闭、桌面启动方式与任何冲突输入法的调整仍由用户明确操作。

私有会话的托盘重启沿实际可执行文件 exec，并保留原参数和 IBus 排除项，仍由原前台终端管理。配置菜单运行同目录打包的原 fcitx5-configtool 脚本，由它寻找系统官方配置 GUI；未安装配置 GUI 时按原脚本提示处理，不更改系统安装。

## 可选 Qt 6.11.2 构建与启动

Qt 组件是显式可选内容，只有使用 `pack.py --qt-output <本轮Qt构建目录>` 才进入包。固定 Fcitx5-Qt 5.1.15 和 Qt 6.11.2；包内不带 Qt 系统库，也不安装、升级或替换系统输入法插件。构建记录、每个输入文件与 ELF 的 SHA256、完整修改后的上游源码、BSD/LGPL 许可和重建脚本随包提供。原 GTK3/GTK4 入口不要求系统安装 Qt。

Qt 的生产构建和原生实现见 [Qt 路径](qt.md#构建与验证)。可选发行包还要求同一构建的真实异步输入回归记录，使用新输出目录执行：

```sh
python tests/linux/qt_cooperative/run.py \
  --fcitx-root artifacts/linux/fcitx-cooperative-5.1.22
python packaging/linux/fcitx-cooperative/pack.py \
  --build-output artifacts/linux/fcitx-cooperative-5.1.22 \
  --qt-output artifacts/linux/qt-cooperative \
  --output artifacts/TypeLay-Fcitx-cooperative-qt6.tar.gz
```

有 Qt 组件的包可执行 `python fcitx-cooperative.py preflight --qt6`，再以 `python fcitx-cooperative.py launch --qt6 应用命令` 逐应用启动。仍须先按前述步骤手动启动同包私有 Fcitx daemon。启动器仅在这个子进程设置 Qt 插件路径、TypeLay generic 插件、原 Fcitx 输入模块与合作开关。无 GUI 的版本探针核实实际 `qVersion()`、编译版本和两个插件的元数据；另独立检查 ELF 动态依赖，未知或混合 Qt ABI 时拒绝。强制同步 Fcitx Qt 模式不支持，不能靠它绕过原异步输入事务。

Qt 系统包的发行修订可与 CI 的 Arch 构建不同；校验明确针对 Qt 6.11.2 上游 ABI，不声称与 CachyOS 包逐字节相同。Qt 6.10 起客户端 Wayland 库及平台插件已在 qt6-base 内，无需安装仅提供 compositor 的 qt6-wayland。应用自带另一套 Qt、Qt5、QtQuick、自绘或富文本不属于本入口的支持范围。已用真实搬迁包的 launcher 在 Qt 6.11.2/Wayland 验证标准 QLineEdit 与纯文本 QPlainTextEdit：本地模型、选区/Unicode、Tab、人工编辑回源、原生撤销、按需快捷键和组合拒写。同窗标准 QLineEdit 后台直接替换和原文冲突拒绝也已通过该搬迁包入口，QPlainTextEdit 后台及跨窗口/应用后台未覆盖。物理输入与实际应用需用户机器验证。

撤回此逐应用选项只需退出该应用，随后正常启动原应用；不修改 shell 或全局配置。私有 daemon 的整体退出和原输入法恢复沿用前述流程。
