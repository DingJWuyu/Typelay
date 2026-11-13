# 构建和验证 Linux 客户端

Linux 的生产构建入口是 [eng/linux/build.py](../../../eng/linux/build.py)。先按改动选择验证范围；涉及分发时，再从实际发行包验收。普通用户安装见[使用说明](README.md)，进程、接口和源码职责见[系统架构](architecture.md)。本页的命令从仓库根目录执行，输出放在被忽略的 `artifacts/`。

构建从[开发环境](#准备开发环境)开始。已有环境时，可直接选择[变更场景](#按变更选择场景)、[模型包事务](#模型包事务)或[文档封装](#文档与发行包)。

## 准备开发环境

构建目标是 Linux x86_64。先核对以下依赖，.NET SDK 版本以 [global.json](../../../global.json)为准：

```sh
pacman -Q git base-devel cmake ninja pkgconf dotnet-sdk gtk3 gtk4 json-glib python python-gobject libayatana-appindicator niri uv
```

按目标系统的包管理流程安装缺失项。GTK、PyGObject 和 Ayatana 使用系统包；工程工具与模型 worker 各有私有 Python 环境，不安装进系统 Python。合作输入另有固定版本和补丁要求，准备前读[Fcitx 预检](fcitx.md#预检)与[Qt 构建说明](qt.md#构建与验证)。

从集成分支 `hitl` 开始，创建工程工具环境：

```sh
git clone --branch hitl git@github.com:DingJWuyu/Typelay.git
cd Typelay
uv venv artifacts/build-tools
uv pip install --python artifacts/build-tools/bin/python -r eng/requirements.txt
```

需要执行共享指南中的 `python` 命令时，在当前 Bash 或 Zsh 终端激活这个环境：

```sh
source artifacts/build-tools/bin/activate
```

这个环境提供固定版本的 Ruff 和 fontTools。fontTools 用于生成随包离线字体，与运行时模型环境无关。GTK 桌面测试使用能加载系统 PyGObject 的 `/usr/bin/python3`。

## 构建

先运行主包构建：

```sh
artifacts/build-tools/bin/python eng/linux/build.py
```

默认输出目录是 `artifacts/linux/package`，归档是 `artifacts/TypeLay-Linux-preview-x86_64.tar.gz` 及对应 `.sha256`。包内包含自包含 .NET 宿主、GTK3/GTK4 模块、桌面代码、浏览器接入、VS Code VSIX、worker 和安装器。模型权重不在包内。

这个命令会生成 setup-only 包。它包含设置和基础客户端，但没有 Fcitx 合作接入载荷。构建会准备固定 Index CPU 运行库；首次准备需要网络，除此之外还可能访问 SDK、NuGet 和字体来源。它不会下载翻译模型。

需要完整桌面包时，在同一干净提交完成[Fcitx 合作组件构建与打包](fcitx.md#构建与打包)，再封装：

```sh
artifacts/build-tools/bin/python eng/linux/build.py --finalize --cooperation-package artifacts/TypeLay-Fcitx-cooperative.tar.gz
```

`--finalize` 校验原主包清单、提交身份、固定合作来源及对应源码，把组件放进 `support/desktop/` 后重建清单和归档，不重新编译。主包与合作组件必须来自同一轮匹配构建。自带哈希用于完整性检查，不认证未知来源的发布者。

生产合作构建归 [eng/linux/cooperative](../../../eng/linux/cooperative/)所有，打包入口归 [packaging/linux/fcitx-cooperative](../../../packaging/linux/fcitx-cooperative/)。测试 runner 调用这些入口，不维护第二份生产配方。改变生产来源、补丁或清单时，需要同时验证实际重建和对应测试入口。

### Index 默认载荷

主包默认包含 Index ABI1/b11913 CPU 载荷。需要提前准备或复用已验证目录时，执行：

```sh
artifacts/build-tools/bin/python eng/linux/index/prepare_runtime.py --cache artifacts/linux/index-cpu-download --output artifacts/linux/index-cpu-runtime
artifacts/build-tools/bin/python eng/linux/build.py --index-runtime artifacts/linux/index-cpu-runtime
```

准备器只下载固定 CPU archive，按下载锁和运行文件锁校验。`--offline` 只复验精确缓存。Index 与 Hy 使用不同运行库和锁，不能混用目录。

### Hy worker 可选构建

Hy 是可选载荷，需要先明确准备固定 CPU 运行库，再把目录传给主包构建：

```sh
artifacts/build-tools/bin/python eng/linux/hy/prepare_runtime.py --cache artifacts/linux/hy-cpu-download --output artifacts/linux/hy-cpu-runtime
artifacts/build-tools/bin/python eng/linux/build.py --hy-runtime artifacts/linux/hy-cpu-runtime
```

准备器使用固定 b11717 官方 CPU archive。`--hy-runtime` 本身不下载或编译 llama.cpp，也不安装模型；构建器校验既定文件闭包并编译共享生产 adapter。只构建 adapter 时使用：

```sh
artifacts/build-tools/bin/python eng/linux/hy/build.py --runtime artifacts/linux/hy-cpu-runtime --output artifacts/linux/hy-adapter
```

产物包含 ABI5 adapter、固定 CPU 库、锁、清单和许可证，不包含 C++ 源码、headers、fixture 或 GGUF。模型文件由用户明确安装。缺少原生闭包时，客户端明确禁用 Hy 准备和使用，不把其它模型的可用性当作 Hy 可用。

## 按变更选择场景

先按[实际价值判断](../../engineering/testing.md#实际价值判断)说明用户场景、已有证据、影响和验证收益。缺陷修复从最接近用户的路径复现，再在能观察同一故障的边界保留回归。下表用于选择入口，不是每次修改都执行全部测试。

| 改动 | 首先验证什么 | 还需要什么 |
| --- | --- | --- |
| 文档和注释 | 检查路径、锚点、命令定义和差异。 | 随包文档变化还需核对[文档封装合同](#文档与发行包)。 |
| Core、Runtime 或协议 | 运行共享检查和对应公开边界回归。 | 改变平台交互时，还需相应宿主或真实桌面场景。 |
| Linux Python 或 .NET 宿主 | 运行 Ruff、Host 构建和受影响测试。 | 输入、设置或呈现改动按下列场景补实际消费者证据。 |
| 安装、启动或发行清单 | 从新构建的实际 tar.gz 运行搬迁回归。 | 完整接入包另验合作载荷，不能以 setup-only 代替。 |
| GTK/Qt 输入与浮层 | 编译实际模块，运行相应合作事务和桌面场景。 | 物理输入法、原生撤销与目标桌面视觉另行验收。 |
| 浏览器或 VS Code | 运行各接入页列出的真实扩展/本地主机测试。 | DOM 或协议通过不能代替真实模型桌面链。 |
| 模型准备与激活 | 运行真实包生命周期和失败保旧代场景。 | 推理、语言质量与输入框写回分别判断。 |

共享检查与 CI 选择规则见[工程开发流程](../../engineering/development.md)。Qt 构建输入下的独立桌面观察只证明该场景，具体来源核验和 probe 要求见[Qt 验证](qt.md#构建与验证)。旧提交成功、单场景通过和工作流总状态都不能代替当前提交的 Linux 完整结论。

## 验证

### 先做不需要桌面的检查

```sh
artifacts/build-tools/bin/python -m ruff check src/linux/desktop packaging/linux eng/linux tests/linux
artifacts/build-tools/bin/python -m ruff format --check src/linux/desktop packaging/linux eng/linux tests/linux
dotnet build src/linux/TypeLay.Linux -c Release
```

需要共享检查时使用根入口：

```sh
pwsh -File ./dev.ps1 shared-check
```

共享 Runtime 回归使用生产编排和受控系统端口，不证明原生 GTK/Qt、输入法或实际模型可用。只有文档改动时，不为此启动桌面或安装模型。

### 从实际归档检查安装生命周期

```sh
env TYPELAY_LINUX_PACKAGE="$PWD/artifacts/TypeLay-Linux-preview-x86_64.tar.gz" /usr/bin/python3 tests/linux/distribution_e2e.py -v
```

这个入口把实际包搬到含中文和空格的新目录，使用独立 HOME/XDG，验证安装、重复安装、完整性、CLI 启退、保数据卸载和显式清理。它不下载模型，也不连接用户桌面。

验证完整候选时，增加 `TYPELAY_REQUIRE_DESKTOP_COMPONENT=1`；要求随包 Hy 时再增加 `TYPELAY_REQUIRE_HY_RUNTIME=1`。这两项要求必须由实际包满足，不能为了通过检查而省略。setup-only 包的安装成功不证明合作接入可用。

### 在隔离桌面检查输入与设置

Agent 启动、驱动和清理桌面的唯一入口是仓库技能 [verify-typelay](../../../.agents/skills/verify-typelay/SKILL.md)。技能包含独立 HOME、XDG、D-Bus、Xvfb 和 niri 的完整命令，以及当前机器的依赖、模型安装和 Btrfs 绕法。原生拒绝、设置、教程、托盘和合作输入的驱动方式按其功能地图选择。

模型 Ready、原生字段支持、实际预览、写回和物理输入法分别取证。设置场景还需检查跨页草稿、即时保存、失败回滚和关闭确认，不能用截图或持久化单测代替交互。真实 Waybar 点击、面板重启、缩放和物理输入法没有在该嵌套会话中得到证明。

### 原生输入回归的判据

短许可测试在发布许可前准备 XTest 连接和键码，实际投递与输入法退休仍须落在原期限内。XSync 只证明 X server 已处理请求。Host Ready 也不表示原生控件已显示对应候选，接受前要冻结真实候选、源文、选区和计数，接受后核对同一候选及唯一 Verified 回执。不能用写后正文补造期望，不能过滤错误候选再继续等待。

GTK 的几类专项合同分别由以下入口维护：

| 场景 | 应保留的判据 |
| --- | --- |
| [语言预览拒绝](../../../tests/linux/gtk_session_reentry.py) | 只读续显保留真实 `Known \| Detached`，焦点只允许原语言按钮或其已映射子菜单。GTK3 独立 attached popover 不等于按钮后代。正文、authority、编辑权限和外部输入变化仍须拒绝。 |
| 过期预览重放 | 原预览实际退休后，用最后有效请求的原 `expiresAtMs` 验证不能重显。新鲜回源后的合法预览另用新捕获验证，两条路径都不授予接受或写权。 |
| [GTK3 输入通知](../../../tests/linux/test_input_notifications.py) | 完整、缺额、多额、换代、回退、溢出、重入和 probe 内通知分别观察。去重只处理已经执行原 invalidation 的通知，不恢复 revision、许可或 Clear。 |
| [GTK3 原生通知与 direct](../../../tests/linux/native_e2e.py) | 真实模块加载受控 IM context，保留通知与 F9 对照。TextView direct 正例核对正文、选区、Verified snapshot，以及各一次 `begin-user-action` / `end-user-action`；输入、context、焦点、原文、选区及批准失效都须保持零改写。 |

通知去重依赖 provider 的 `input_epoch++` 与同步 INPUT signal 对应关系。已返回的 producer 嵌套通知只有在完整处理、同 context 差额精确吻合后才可计为已处理；消费者重入、probe 内通知或证据不全不能去重。实际 provider inspect 仍须证明 Clear。

GTK3 TextView 的 transport proof 续接只允许单调 transport epoch 变化，其余 basis 和三种 revision 必须精确保留。当前 focused Clear、新鲜完整批准及原 150ms 期限仍成立，不能借用 Entry 的 Detached 路径。fixture 控制文件只由隔离主循环处理固定动作和递增序号。这些合同不证明真实 Fcitx、原子性或原生 Undo，仍需相应桌面与普通应用场景。

### GIMP Comment 的撤销边界

[ordinary-gimp](../../../tests/linux/ordinary_gimp.py)比较两张独立临时图像中的实际轨迹：同一原文和译文，一边由 TypeLay direct 替换，一边由原生粘贴替换，然后各执行固定两次 Ctrl+Z。测试不循环等原文出现，也不向 GIMP 注入撤销历史。

已核版本 GIMP `3.2.6-2` 的 Comment 不为评论修改建立原生 Undo。只有该版本与字段、原生粘贴两次均保持译文、TypeLay 完整轨迹一致时，才记录 `nativeUndoSupported=false` 和 `undoRestoration=not-supported`。这表示宿主不提供撤销，不是 TypeLay 撤销通过。未来基线若恢复原文，TypeLay 也必须恢复；未知版本的不恢复或任意轨迹差异都失败。

该 scope 只在自有嵌套 niri 配置关闭动画。平铺原点必须由同一聚焦窗口、唯一活动输出与工作区、无 overview、窗口与 tile 占满输出及零内容偏移共同证明，缺失坐标不能猜成零。验证还需完成取消、断连、配置恢复和 guardian 退出，局部替换成功不等于整条链通过。

### 最后核对实际桌面

物理 CachyOS/niri 验收记录系统、niri、GTK、PyGObject 和输入法版本。按受影响路径检查以下行为，不把代表性应用变成永久支持名单：

- 自动模式持续预览，按需模式只在明确唤醒后工作。密码、敏感与排除字段不会被读取。
- 单行、多行、emoji、选区、删除和粘贴保持正确范围。人工稿回源零写，第二次独立接受才写入。
- 直接替换遇到换框、改原文或关闭窗口时，始终守住原目标。控件自己的 undo/redo 保持原语义。
- 物理输入法的组合、候选选择、取消和切换不会误读半成品或误拦接受键。
- 平铺、滚动、窗口动画、多屏缩放、边缘、主题与托盘没有遮挡、抢焦点或明显视觉错误。
- 模型准备、失败恢复、重复启动、登录启动、退出和卸载保留正确数据，没有遗留自有进程或按键占用。

缺少环境时报告未运行。构建、协议、进程生命周期、真实模型、桌面交互与视觉结果分别记录，不相互替代。当前原件和未验范围见[当前开发状态](../../engineering/development-state.md)。

## 模型包事务

修改准备或激活代码时，先核对 [setup.py](../../../src/linux/desktop/setup.py)、[LinuxModelInstallationStore](../../../src/linux/TypeLay.Linux/LinuxModelInstallationStore.cs)和[模型目录](../../../src/shared/TypeLay.Worker/model-catalog.json)。请求只接受目录身份与明确操作，不接受任意 URL、版本或路径。

保留以下事务边界：

1. Install 只增加所选包。Repair 保留已有方向子集和当前选择，不能借修复扩方向。Select 和 Remove 离线执行，Remove 只删除非当前包。
2. 同一安装锁内先核对 `expectedCurrent`，再复制或下载。暂停、继续与重试保留原请求，陈旧请求不能自行换基准重试。
3. 候选代使用普通文件复制，私有 Python 在最终路径完成 prefix/import 检查。Install/Repair 可以按原空间预算重建损坏 runtime，Select/Remove 不能联网修复。
4. 准备不发布候选。实际 worker 握手与精确包、版本、方向匹配后才切换，取消、失败或结果未知时保留必要原件。
5. schema 3 保留完整库存和当前选择。恢复只跟随 `recoveryPrevious`，Remove 断开恢复链，不能复活已移除包。未知库存禁止包操作，不能猜成空库覆盖。
6. 精确代的使用锁证明模型进程寿命。全部待删代都可独占且退出已确认后才清理；清理失败只提示空间未释放，不改写已经提交的结果。

Hy 中英补充是明确的增量事务，复用已验证本地权重，保持当前模型不变。它不能用 Repair、手改库存或旧版本激活入口代替，也不承诺直接降级恢复。

Argos 归档核对路径、重复条目、语言、模型布局和声明大小。存在 `archivePin` 时，缓存与新下载都须匹配固定长度和摘要，实际解包仍受逐文件及总量约束。`archiveObservation` 记录本地归档压缩量、声明解包量、实际保留量和 SHA-256，只用于审计空间与完整性，不证明发布者身份或 Ready。观测缺失时如实记为缺失，不补造，也不降低原安装门。

### 验证实际模型生命周期

Index 使用独立正式包回归。明确允许下载模型后才准备固定权重与许可证，再运行：

```sh
artifacts/build-tools/bin/python eng/shared/prepare_index_model.py --output artifacts/index-model-download
/usr/bin/python3 tests/linux/index_package_e2e.py --package artifacts/linux/package --cache artifacts/index-model-download --output artifacts/index-production
```

该入口通过实际 setup、Host、ModelClient 和 supervisor 检查默认准备、握手、连续请求、冷启、失败与取消保旧代、Repair、Argos 安装、离线切换及移除。它保留 600 秒总预算、每次加载/请求 60 秒和退出清理 7 秒。各阶段记录 passed、failed 或 not-run，不冒充 GTK、写回或语言质量验收。

Hy 生命周期先通过正式入口准备隔离 Argos 基线，再明确准备固定缓存：

```sh
/usr/bin/python3 tests/linux/hy_package_e2e.py --prepare-cache --package artifacts/linux/package --cache artifacts/linux/hy-model-cache
```

随后在相同测试数据与缓存配置下运行 `native_e2e.py --desktop --desktop-scope model-packages`，同时提供实际 `--package`、`--native` 和新的 `--output`。此场景从搬迁后的用户安装操作 GTK，验证确认与取消、安装和选择、补充方向、同代冷启、切回 Argos、移除和正常退出。新装十二方向与明确补充后的十四方向分别检查，实际握手不等于所有方向质量通过。

CPU 闭包的无模型检查可以单独运行：

```sh
env TYPELAY_HY_CPU_RUNTIME="$PWD/artifacts/linux/hy-cpu-runtime" /usr/bin/python3 -m unittest discover -s tests/linux -p test_hy_runtime.py -v
```

无模型 ELF/ABI 检查只证明库加载、导出和隔离。质量采集的授权、固定样本、资源预算和发布判据统一见[翻译质量验证](../../engineering/translation-quality.md)，不要把安装成功、非空输出或旧模型证据当作新质量放行。

## 文档与发行包

Linux 文档有两种交付位置。`architecture.md` 和本页留在仓库，负责工程结构与开发方法。`README.md`、`browser.md`、`fcitx.md`、`qt.md` 和 `vscode.md` 随发行包交付，负责安装、接入及对应的维护入口。

[package_documentation](../../../eng/linux/build.py)按以下规则生成随包文档：

- 构建器改写 README 的“当前可验证候选”一节，填入本包提交与脏工作区身份。该节与“安装、启动与首次配置”标题是封装边界，修改标题需同步维护构建器。
- README 中指向仓库的相对链接被转换为当前包提交下的 GitHub 链接。四份接入说明继续使用包内相对链接。
- 四份接入说明按原字节复制。它们引用源码时使用代码路径，引用工程文档时经 `README.md#开发与验收` 跳转，避免产生包内不存在的相对链接。

修改上述文档后，检查生成 README 的提交身份、包内链接和接入页复制结果。已有回归位于 [PackageReadmeTests](../../../tests/linux/test_build_bundle.py)，该组只生成临时文档，不需要桌面或模型：

```sh
python tests/linux/test_build_bundle.py PackageReadmeTests -v
```

文档生成与链接检查不证明对应包已经构建或产品验收通过。已有构建记录、测试结果和未测范围见[当前开发状态](../../engineering/development-state.md)。
