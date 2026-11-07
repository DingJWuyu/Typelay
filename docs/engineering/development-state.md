# 当前开发状态与安排

测试、测试夹具、CI 和工具脚本整治已完成。审查以 `40abf67` 为基线，核对基线与当前文件的联合集合、每个测试及参数、helper 和现役消费者。共享、Linux、Windows、浏览器与工程工具范围均无未审项，删除项也保留了原文用途核验。工程规范与需求独立审查无剩余必须修改项。

## 当前代码与最新验证

[最新定向 CI](https://github.com/DingJWuyu/Typelay/actions/runs/42343105820) 在验证提交 `077aa1c` 上通过。主工作区的产品、测试和工具源码与该验证提交一致。验证分支 `work/test-quality-verification` 额外包含临时验证工作流、故障实验脚本和避免触发完整模型 CI 的分支排除项，这些不进入主工作区。没有更新 `trunk`、`main` 或创建 PR。

| 验证范围 | 结果与边界 |
| --- | --- |
| 共享正式入口 | 远端实际运行 `./dev.ps1 shared-check`。Core 318、Runtime 1597、Protocol 28、Windows.Contracts 211 项通过；worker 与共享 Python 检查通过。 |
| Windows 受影响场景 | 21 项 WPF 与 catalog 场景、四项显式 MSG/严格来源维护回归均执行并通过，无跳过。实际 native guards、13 项 runtime 准备和 10 项 CI 选择器回归通过。 |
| PowerShell 包场景入口 | 四种参数路由、包内五程序集复制和子进程失败传播通过。这里控制了进程边界，不代表真实模型包生命周期已重跑。 |
| Windows 故障与副作用 | 术语弹窗的静默拒绝变体证明旧测试漏检、新测试失败。旧 General 测试修改真实启动项且错误期待重试；新回归保持真实启动项不变，并区分 Retry 与 View。恢复旧英文映射后，新回归按预期失败。正常英文警告及重试截图已检查。 |
| Linux 与工具本地回归 | Host 86、Input 220、GTK geometry 80、capture 40、Hy preparation 13、scope completion 12、build bundle 58 项通过；实际隔离 X11 编辑器 23 项、GTK 保存反馈五项通过。真实私有 niri 进程确认清理失败不会成为成功。 |
| 浏览器观察与工程检查 | 真实隔离 Code 与私有 socket 变体验证预览写回一致性和原三秒截止时间。Ruff、C# 格式、差异检查及 Actionlint 通过。Actionlint 未启用 shellcheck/pyflakes。 |

远端原件在 `artifacts/test-quality/remote-final/`，修前失败和本地红绿原件在同一任务目录的对应子目录。覆盖证据在 `artifacts/test-quality/audit/`；临时副本与依赖缓存已清理，必要原始证据保留于被忽略目录。换工作区后若原件缺失，不得凭本文声称已恢复证据。

本轮没有运行完整模型、合作桌面及发行验收。保留的 `settings_ui.py` default/focused 维护入口包含尚无等价替代的真实 GTK 设置覆盖，本轮未完整运行。当前产品缺口和下一动作继续以下方安排为准，不能用这轮测试审查或定向 CI 关闭它们。

模型目标配置遵循[模型分工](../agents/skills.md#模型分工与-advisor)：审计为 `gpt-6.1-sol` / `high`，修复为 `gpt-6.1-sol` / `xhigh`，Advisor 与独立审查为 `gpt-6-astra` / `xhigh`。工具只确认代理创建，实际配置未核实；工具审计代理接续浏览器修复时无法在 followup 中调整 effort，不能把期望配置写成实际配置。

## 当前验证环境与交付范围

本地工具在被忽略的 `artifacts/tools/` 中。`.NET SDK` 位于 `dotnet/dotnet`，版本为 10.0.401；工程 Python、Ruff 与 Ninja 位于 `engineering/` 私有环境。GTK3、GTK4 和 Qt6 开发库可用，三个原生实现均有编译结果。

本机可以交叉编译 Windows App，不能运行 WPF。远端已完成本轮源码的完整 Windows 构建及上述定向 WPF 验证，完整发行验收仍另行判断。

下表链接到本工作区保留的原件。`artifacts/` 不随 Git 提交；换工作区后，文件缺失就表示原件不可用，不能凭本文重建“通过”结论。结果只覆盖表中对象和范围。

| 验证对象 | 已有结果与原件 | 证据范围 |
| --- | --- | --- |
| 共享 Runtime | 1597 项通过，原件在 `artifacts/test-quality/remote-final/test-quality-shared-077aa1cf557ef45e9dd2cfe66556fb457fa9a3f8/`。 | 对应当前 Runtime 源码与测试，验证真实编排及受控端口，不证明系统输入。 |
| Core 与 Linux Input | Core 318 项在最新定向 CI 通过；Input 220 项本地通过，原件见 `artifacts/test-quality/linux-oracles/`。 | 当前规则和输入合同回归，不代替物理输入法或合作桌面。 |
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

本轮测试整治没有采集新的模型质量证据，也没有改变发布配置。安装成功、worker 握手、完成采集和语言质量通过分别判断。模型采集、CI 与发布需要对应任务授权，不随文档整理恢复。
