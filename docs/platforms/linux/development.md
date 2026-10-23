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

## Argos 安装包观测

正式安装复用已下载或命中的本地 Argos ZIP，不额外请求网络。在全部 ZIP 路径、重复条目、方向和模型布局检查通过后，`models/installed.json` 的 schema 3 包条目记录可选 `archiveObservation`；现役旧式 CLI 安装入口在原 `dependencies` 中记录同组数据。`compressedBytes` 和 `observedSha256` 对应本地归档实际字节，`declaredUnpackedBytes` 是 ZIP 全条目声明大小之和（含未保留的 Stanza 文件），`retainedBytes` 是实际写出的模型及根级元数据/许可证字节数。目录项不计实际保留量。

维护者用这些记录审计安装磁盘预算；它们不是发布者签名、可信固定锁或 Ready 资格，也不改变当前准入上限。`catalogUrl` 只是目录指定的 URL，不能证明命中缓存的来源。记录只含包 ID、版本、固定 URL、哈希和计数，不含用户目录、文本或凭据。Linux 保留包重建仅携带形状有效的既有观测；没有旧证据时不补造。Windows/共享读者忽略该可选字段，不承诺 Windows 重建会保留或生成它。现役 CLI 正式安装在真实自检通过后，将白名单摘要写入既有 `self-check.json.archiveObservations`；该文件可能随旧代清理，不能假定最终 CI 产物一定包含它。真实 Settings 方向安装测试在每次单向/双向就绪及库存断言通过后，立即把白名单摘要累积到既有、会上传的 `direction-installation.json.archiveObservations`，不依赖旧代留存。缺失或无效观测逐包标为 `missing`，不补造尺寸或降低 Ready、自检与原安装断言；后续测试失败保留已完成安装的观测，但整条 `outcome` 仍为失败。不上传模型包、整个缓存或含文件路径的完整 inventory。

## Hy worker 可选构建

默认模型准备继续使用原 Argos。CI 的正式 Linux 候选包通过固定 b11717 官方 CPU 闭包加入 Hy 运行资产，仍需用户在设置中显式选择下载并使用 Hy。手动开发构建只有传入 `python eng/linux/build.py --hy-runtime <已有CPU目录>` 才包含这些资产；该参数不下载、不编译 llama.cpp、不安装模型。设置显示固定 Hy 预览包，新装默认十二方向，中英双向须另行明确补充；只有发行包的 native 闭包通过只读完整性检查后，才开放下载、使用与修复。该目录必须与 `src/shared/TypeLay.Worker/native/hy/cpu-runtime.lock.json` 的17项文件逐字节匹配。

只编生产 adapter 可执行 `python eng/linux/hy/build.py --runtime <已有CPU目录> --output <空输出目录>`。输出包含唯一共享 C++ 源编出的固定 ABI5 导出的 adapter、固定 CPU 库、模型/CPU 锁、production-adapter 清单与许可证；发布包不带 C++、headers 或 fixture 门。Hy 模型数据单独位于 `models/packages/<id>/<version>/`，精确包含锁定 GGUF 与 `model_LICENSE.txt`。缺少运行资产或固定模型数据时，active Hy 明确启动失败。

发布者显式准备 CPU 资产的入口是 `python eng/linux/hy/prepare_runtime.py --cache artifacts/linux/hy-cpu-download --output artifacts/linux/hy-cpu-runtime`。它只获取固定官方 `llama-b11717-bin-ubuntu-x64.tar.gz`，完整校验下载锁和生产17文件锁后，把实际 ELF 复制为普通文件；不带工具、服务器、其他后端或模型。`--offline` 仅复验已有精确缓存。完整候选包的发行回归设置 `TYPELAY_REQUIRE_HY_RUNTIME=1`，要求搬迁后的 native 可用并拒绝包内 GGUF。

完整 Linux CI 在同一45分钟 job 中、已有 Argos 消费者结束后，仅运行一次 Hy 模型包链。CPU archive 与模型/许可证使用独立精确哈希 cache，无前缀回退；模型不进入源码或上传 artifact。尾段准入要求剩余12分钟，并按当前正式 Argos 代实际大小预算五次事务：当前代字节数 × 5 + 固定 Hy 字节数 × 5 + 2 GiB余量，包含独立下载缓存和移除前保留的恢复代。时间分配为：cache 恢复2分钟、准备3分钟、保存2分钟、GTK及清理5分钟。实际 GTK 工作仍限240秒，随后20秒退出清理；产品请求期限不变。空间和进程采样进入结果 JSON，不以准入预算冒充实测峰值。

本地执行该链先按现正式安装入口准备 Argos 与 CPython 3.13.16，再显式运行 `python tests/linux/hy_package_e2e.py --prepare-cache --package artifacts/linux/package --cache artifacts/linux/hy-model-cache`。随后在隔离 XDG 数据与缓存、现嵌套桌面环境中运行 `tests/linux/native_e2e.py --desktop --desktop-scope model-packages`。该场景从搬迁后的实际用户安装启动，操作 GTK 确认下载并使用、回切 Argos、选择已安装 Hy、再次回切与移除；观察实际包身份和十二方向 hello、一个中译法请求、退出及清理。固定模型缓存准备独立于 GTK 期限；禁止通过改安装标记或换成旧 Python 跳过正式安装。未实际运行的新场景保持未验收，单句成功也不代表十二方向质量或物理桌面验收。

无模型的实际 ELF/搬迁校验可设置 `TYPELAY_HY_CPU_RUNTIME=<已有CPU目录>` 后运行 `python -m unittest discover -s tests/linux -p test_hy_runtime.py -v`；它只核库加载、导出和隔离，不调用 hy_open_v5 或模型推理。

当前 ABI5 增加结构化术语词对并保留内部自然/正式指令请求，软件合同与无模型 ELF 检查不构成模型质量验证，用户入口仍未开放。此前 ABI3 真实生产长度帧已在云端 Linux 通过：重新构建生产 adapter 后，普通 hello 精确返回 active 包身份及十二方向且不推理，随后唯一原句“我没有同意删除备份。”中译法以真实 EOG 完成，进程正常退出并回收。测试使用隔离 Python 3.13.5，官方 CPU 库动态选择 zen4；固定2线程、context1024、输出192 token、地址空间上限2.5 GiB、请求60秒/总180秒。本机采样译文耗时1.89秒，峰值 RSS 约1.19 GiB、地址空间约1.43 GiB；唯一临时模型副本已清理，原缓存完整性不变。这只验证精确库存布局下的 worker 加载和单句请求，不作为性能保证、十二方向质量认证、正式 Python 3.13.16 安装事务、包管理交互、物理桌面或 Windows 生产路径验收。

## 模型包事务

新包请求使用固定目录身份，不接受 URL、版本或路径：`format: 1`、`operation: Install|Repair|Select|Remove`、`packageIds`、`activePackages` 和 `expectedCurrent`。安装只增加所选未装包，修复只重建已装包并保留方向子集和当前选择；切换与移除完全离线。移除仅允许一个非当前包，须先切换模型再移除。原默认 Argos 双向及 legacy 方向安装入口保留，但已有 schema 3 库存不能回到旧整库修复路径。

Hy 中英预览通过正式目录列出的双向能力与明确设置确认开放，仍仅用 format2 Install 加 `addDirections: ["zh_en", "en_zh"]`。目标必须是已安装固定 Hy 包，两方向均为增量，`expectedCurrent` 精确，`activePackages` 原样不变；准备只复用已验证权重/运行时并复制新代，不能联网补齐或借 Repair 扩方向。format1 新装默认仍十二方向，Argos 默认不变。实际 Host 使用嵌入目录，Python 准备测试不等于十四方向 Host/GUI 激活；发行 Host 与 worker 必须使用一致目录。启用后不支持直接降级旧版本，用户可在当前版本切回 Argos；未安装时需另行确认下载安装，不保证离线可切回。旧版本拒绝十四方向库存而不裁剪，现有 `--activate-model` 不是任意旧代恢复工具；Remove 的 activation 还可能含被移除 Hy14 元数据，不能据新 installed.json 只有 Argos 就承诺旧版本可读。没有公开跨版本恢复入口，不手改库存或承诺“移除即可降级”。

准备期间原代继续服务。请求与 current 比较在同一安装锁内、复制或下载前完成；暂停、继续和重试保留原请求，陈旧请求不能自行改基准重试。普通文件复制形成新代，私有 Python 必须指向新代，复制后离线完成 prefix/import 检查。format1 Install/Repair 发现私有解释器缺失、损坏或依赖检查失败时，可在预留最坏额外空间后重建候选 runtime；权限、空间或超时失败不触发该回退。Select/Remove 的 runtime 失败只报错，不联网修复。新包准备只下载、校验和构建库存，唯一 serving worker 在最后激活时加载模型；用户试译另行验收。

`installed.json` schema 3 保留完整 `packages` 和 `activePackages`。`activation.json` schema 3 仅有 `schema`、`previous`、`packages`、`operation`、`removedPackages`、`recoveryPrevious` 六字段。前两种旧 schema 迁移时须真实复制旧方向文件并重写清单。普通操作保留恢复前代；Remove 的 `recoveryPrevious` 为 null，不能因启动失败复活已移除包。库存记录不可读时显示未知并禁用包操作，不能猜成空库；恢复需要用户显式安排独立重装，不在包管理中自动覆盖。

固定 Hy 文件大小与 SHA 从 worker 的生产 `model.lock.json` 读取，模型和许可证只下载一份，不按两方向重复。准备前按缓存、候选与保留前代保守预算空间，Hy 三份约 3.17 GiB，另加 Argos、runtime 和余量。Argos catalog 的可选 `archivePin` 仅用于单方向 Argos 包，严格包含 `sha256`、`bytes`、`unpackedBytes`：分别为小写 SHA256、压缩字节数和全 ZIP 声明解压字节数。当前中英双向 1.9 两包固定值来自项目真实安装的 CI 观测并经项目审核，不是上游签名或发布者认证；缓存记录的 catalog URL 也不是来源认证。存在但损坏的 pin 拒绝安装，缺失则仍采用每包 1 GiB 下载、2 GiB 解包的保守预算。带 pin 的缓存和新下载都必须匹配长度与 SHA；解包前再检查同一已打开 archive 的身份、全 ZIP 声明量，并按每文件声明量及累计上限约束实际写出，原路径、metadata 语言/版本和模型结构检查不变。新装中英双向按全 ZIP 声明量计费，而不是较小的保留文件量；同盘含 runtime 数据/缓存各 1 GiB 与 64 MiB 余量，准入为 2,531,595,196 字节。旧代复制独立按实际文件字节预留并限制写出，继续校验原库存 SHA；旧库存自身有效不表示它已经获得新 archive pin 的来源证明，也不因此改变其 Ready 状态。Confirmed 且旧进程退出已证明后才清历史代；缓存清理另获同一锁，重读 current/recovery 的完整库存引用。清理失败仅报告空间尚未释放，不把已成功切换报成失败。

可用性只读检查为 `python desktop/setup.py --check-hy-runtime <发行目录>`，只调用 native 文件校验，不载入模型。内部 `--cleanup-packages <发行目录> <数据目录> <缓存目录>` 只由确认激活后的客户端调用，按固定包缓存清理，不扫描任意历史目录。缺 native 的发行包仍可删除非当前 Hy 库存，但不能下载、修复或切换到 Hy。

## 显式快照与模型使用寿命

GTK 本地试译通过继承宿主通道发送固定 `translateEditorSnapshot`，外部 `--editor-host` 在桌面已运行时通过既有同 UID、0600 私有 Unix control socket 转发同一快照。该白名单操作包含输入正文，其它控制操作仍仅传元数据。两条入口共用 EditorHost 的精确文档身份、版本、UTF-16 选区和文本校验，不接收任意 prompt 或推理参数。返回还须匹配原设置、模型状态代次及安装代。每个 control peer 只接收一次快照；宿主只保留一个在途显式请求，取消后直到已派发任务实际结束才释放占位。

外部请求总预算为原入口起算的 215 秒，教程为 140 秒；排队或收到分块数据不会重置期限。取消、超时和断开立即撤销该请求结果与后续接受资格，不表示计算即时停止；已派发计算沿既有 worker 期限收束，不强杀共享 serving，也不自动重试。桌面启动锁被占用时，control 连接失败或拒绝不能回退独立加载。无桌面时，外层 supervisor 继承 `desktop.lock`，包住 one-shot 及其后代；只有退出码 200 表示真实退出证明，125、超时或单独 .NET 退出均不算。

每个实际模型 supervisor 在 fork 前取得精确安装代 `use.lock` 的共享锁，直到 ECHILD 且自有进程组消失。Remove 在原安装锁内，先取得全部待删代的非阻塞独占锁，再删除任何目录；占用、链接或权限异常保留全部历史代并提示空间尚未释放。本次已知退出证明失败仍禁止清理，不能用后来锁空洗掉失败。此保证覆盖正常产品生命周期；外部强杀 supervisor 后残留脱离进程组的后代属于未覆盖的异常边界，不能据此声称任意 SIGKILL 都已安全回收。

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

短许可交互的 XTest 连接和键码解析在发布许可前准备；真实按键投递、输入法退休与续约仍必须落在原期限内。Qt 续约回归记录这些边界的调用时间，保留原计划、输入版本拒绝和不延长期限的断言。XSync 只证明 X server 已处理请求，不是 Qt/Wayland 已投递。GTK 的宿主 Ready 也不代表原生控件快照已更新：多行接受先等待晚于本次 Ready 的非空实际候选，再断言语言、换行、原源与选区，并重新检查接受资格；不能筛掉错误候选后继续等待。

GTK4 在 `TYPELAY_TRACE` 下有有界、无正文的浮层撤销分支记录，含原到期时间与既有状态。它用于区分定时到期、焦点变化、宿主隐藏和续约拒绝，不授予连续性或延长租约。

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
| 合作 Qt6 | QLineEdit 自动/按需、F9/F10、选区、编辑回源、同窗后台写回/冲突拒绝；QPlainTextEdit 多行、undo/redo、真实拼音、结构拒绝和应用回调隔离 | 隔离 niri 及搬迁可选包已有证据；CachyOS 发行修订、实际应用与物理输入仍须验收；QPlainTextEdit→同窗 QLineEdit 后台已通过真实 provider/consumer 回归；跨窗口/应用后台未覆盖 |
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

### 真实 GIMP 的隔离验收坐标

`ordinary-gimp` scope 仅在自己的嵌套 niri 配置中关闭 compositor 动画，其他 scope 与产品配置不变。锁定 niri 26.04 的 tiled IPC 不提供窗口位置；该验收仅在同一聚焦非浮动窗口、唯一活动输出/工作区、无 overview、窗口和 tile 均精确占满输出且内容偏移为零时证明全屏原点。配置摘要和约束记录在结果 JSON 中。此路径不证明动画过程中的真实用户定位能力，不把任意缺失坐标当成零。
