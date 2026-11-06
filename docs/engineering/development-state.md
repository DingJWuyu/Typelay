# 当前开发状态与安排

架构改造已经完成独立复评，工程与平台文档也已按当前实现重写。完整桌面产品验收仍有缺口。本页记录当前安排、后续依赖和可核对的证据；产品要求见[产品基线](../product/README.md)与[已确认原型](../design/README.md)。

## 当前身份与最近 CI

当前分支是 `work/architecture-deepening`。架构改造基于 `48baf7f`，最后一个代码提交是 `dd48597`，之后的改动只整理文档。

两位全新上下文 advisor 已确认该代码提交的架构改造收敛，均未提出必须修正项。评分、评估对象和可信范围见[架构独立评估](architecture-assessment.md)。这项结论不代表完整产品验收通过。

本分支只做本地开发，没有推送、合并、运行 CI 或发布。下文引用的 CI587 问题来自既有基线，不能当作当前提交的运行结果。

## 当前安排

文档任务已完成。五位写作代理核对了当前源码和命令入口；独立规范复核与事实复核提出的问题均已处理。Linux 文档封装的两项既有回归通过，生成后五份随包文档的本地链接均可解析。仓库内受影响路径与 Markdown 锚点、差异空白检查也通过。

写作与复核代理均请求 `gpt-6-astra`、`xhigh`。工具只确认代理创建，实际配置未核实。文档整理没有运行产品桌面、模型采集或 CI，也没有操作用户桌面。

当前没有待修文档、待返回的复核或运行中的自有测试。主代理负责接续下表中的平台验证；对应环境与证据仍是完成条件。模型采集、CI 和发布不随文档任务恢复。

## 当前验证环境与交付范围

本地工具在被忽略的 `artifacts/tools/` 中。`.NET SDK` 位于 `dotnet/dotnet`，版本为 10.0.401；工程 Python、Ruff 与 Ninja 位于 `engineering/` 私有环境。GTK3、GTK4 和 Qt6 开发库可用，三个原生实现均有编译结果。

本机可以交叉编译 Windows App，不能运行 WPF。Windows App.Tests 的托管编译 `-t:Compile` 通过；完整构建缺少生产 `TypeLay.NativeInput.dll`，新增 GUI 断言尚未运行。

下表链接到本工作区保留的原件。`artifacts/` 不随 Git 提交；换工作区后，文件缺失就表示原件不可用，不能凭本文重建“通过”结论。结果只覆盖表中对象和范围。

| 验证对象 | 已有结果与原件 | 证据范围 |
| --- | --- | --- |
| 共享 Runtime | [1598 项通过](../../artifacts/architecture/validation/runtime-final-implementation.trx)。 | 运行对象为 `46b6629`，使用真实编排和受控端口。之后的代码修正仅涉及 Windows 消费者。 |
| Core 与 Linux Input | [Core 320 项通过](../../artifacts/architecture/validation/core-final.trx)，[Input 221 项通过](../../artifacts/architecture/validation/input-final.log)。 | 证明规则和输入合同；最后的菜单修正没有改变这两部分源码。 |
| 风格保存结果 | [修复前失败](../../artifacts/architecture/validation/style-menu-notification-red.trx)，[修复后 68 项通过](../../artifacts/architecture/validation/style-menu-notification-final.trx)。 | 从公共保存入口写真实设置文件，核对提交后的通知警告。 |
| Linux 设置反馈 | [5 项通过](../../artifacts/architecture/validation/style-menu-linux-feedback-green.log)。 | 真实 Python 消费方法保留共享结果；受控 Host 不证明系统通知可见。 |
| Linux Host 与 Windows App | [Linux 构建](../../artifacts/architecture/validation/style-menu-linux-final-build.log)和[最新 Windows 构建](../../artifacts/architecture/validation/glossary-consumer-windows-build.log)均无警告、无错误。 | Windows 术语浮层、浏览器和撤销反馈已修改；[App.Tests 编译](../../artifacts/architecture/validation/glossary-consumer-windows-compile.log)不能代替 WPF 运行。 |
| 浏览器实际 DOM | [呈现 13 项](../../artifacts/m3-consumers/browser.log)、[源端交互 14 项](../../artifacts/m3-consumers/browser-source-regressions.log)、[结算 7 项](../../artifacts/m3-consumers/browser-write-settlement.log)通过。 | 使用隔离 headless Chromium，没有连接用户浏览器。 |
| GTK3 拒绝与许可退休 | [原生检查结果](../../artifacts/m3gtk/native-result.json)与[运行日志](../../artifacts/m3gtk/run.log)。 | 覆盖 Unknown 输入法状态下的拒绝、按键透传及精确单次 `writeRetired`，不证明成功捕获、编辑、接受或原生 Undo。 |
| Linux setup-only 包 | [构建身份](../../artifacts/architecture/package/build-info.json)、[构建日志](../../artifacts/architecture/validation/linux-package-build-with-ninja.log)与[4 项搬迁检查](../../artifacts/architecture/validation/linux-relocated-package.log)。 | 包来自干净提交 `9dd5ada`，覆盖实际 worker/Host、中文空格目录安装重装、命令行启退和保留数据的卸载。 |

GTK3 检查使用独立 Xvfb、niri 和私有 D-Bus。Xvfb/xdotool 只解包到私有目录，系统包签名已核对。自有测试进程已经退出。

setup-only 包没有合作桌面组件，来源也早于最后的风格反馈修正。因此，它不能作为最终代码的完整发行验收。构建只准备了固定 CPU 运行库，没有下载翻译模型。

## 仍需目标平台验证的已有问题

主代理负责接续下列工作。每项都需要对应环境和真实用户路径证据；本次文档整理没有启动这些实验。下表中的既有失败来自 [CI587](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940)，尚无本分支的新证据将其关闭。

| 工作 | 当前事实与依赖 | 下一动作与完成条件 |
| --- | --- | --- |
| Windows 设置与术语反馈 | 本机不能运行 WPF。新增术语警告及删除撤销用例只有编译结果。既有设置进度检查未在原 3 秒期限内观察到首个目标帧。 | 在 Windows 沿真实设置、浮层和撤销入口验证保存结果，并定位首帧未出现的原因。原观察期限内得到结果后，才能关闭对应问题。 |
| Windows 编辑回源与关闭结算 | 既有本地稿 Replace 在过期 claim 后失去了原锚点，下一次新操作尚未验证。本轮写入退休和正常关闭也缺原生证据。 | 核对实际取消、认领与回执顺序，验证下一次独立操作及关闭。原按键按下起算的 500 毫秒期限保持。 |
| GTK4 术语和配置 | 源端术语首例在 `capture-more` 与 `retain-more` 之间退出，40 个案例未完成。配置窗口还有服务器焦点为 0、本地为 1 的分歧。 | 在合作 Fcitx 环境复现退出与焦点分歧。依据原生事实确认焦点和输入法状态，再完成受影响路径。 |
| Qt 语言搜索 | 日语行在 Composing 状态下收到了新 press，检查没有等到 clicked。release 接收者与几何原因未明。 | 追踪原单次动作的接收者和位置，取得正确完成或拒绝的证据。保留原期限、次数和输入法条件。 |
| GTK3、GTK4 与 Qt 成功交互 | 本轮 GTK3 仅有拒绝路径证据，GTK4/Qt 仅有编译结果。完整合作 Fcitx 环境尚未准备。 | 验证成功捕获、编辑、接受，以及未使用许可的退休。编译和 Unknown 拒绝不关闭这项工作。 |
| 普通输入框、分发与视觉 | 物理 Tab、完整中文输入法、不同显示器/DPI、混合组件版本、恢复流程和真实 Undo/redo 尚缺同一提交的完整验收。 | 使用搬迁后的完整发行包验证相关路径，并核对各端实际界面。软件注入、受控端口或局部截图不能代替这些证据。 |

产品层面的剩余要求见[共同开发缺口](../product/README.md#当前共同开发缺口)和[原型对照](../design/prototype-parity.md)。各平台的输入能力限制继续由对应平台文档维护。

## 模型能力边界

新用户明确准备模型时，默认选择 Index；已有 Argos/Hy 安装与选择保留。术语仅对已登记的固定 Index profile 开放中英双向。风格、另述及组合质量门保持关闭。

本轮架构改造和文档重写都没有采集新的模型质量证据，也没有改变发布配置。安装成功、worker 握手、完成采集和语言质量通过分别判断。模型采集、CI 与发布需要对应任务授权，不随文档整理恢复。
