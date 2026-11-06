# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 本轮范围：实施架构目标

用户已明确授权开始实施[目标架构](architecture-target.md)。当前分支为 `work/architecture-deepening`，从已评估的 `work/linux-usability` 基线 `48baf7f` 创建；产品规格、已确认原型和有效 ADR 保持。本轮在本地完成迁移和必要验证，不操作用户桌面，不启动 GitHub 流程或此前平台诊断。

**用户已要求恢复，当前正在完成独立实现确认。** 已核对暂停提交 `9dd5ada` 与在盘验证原件，补齐正式 Linux 搬迁包检查，并收完 advisor 指出的文档前言和 Windows 文案重复。完整平台验收仍未完成。

[固定 rubric](architecture-rubric.md)保持不变，**90.3/100** 和 **90.5/100** 仍只是设计分数。当前独立实现复评针对 `9dd5ada` 给出 **85.3/100**，认可本轮架构收敛，未发现必须修改的代码缺陷，但明确不能宣称完整桌面产品交付。原件为 `artifacts/architecture/implementation-advisor-r2.json`。收尾后的代码还需新的独立确认，最终结论写入[独立评估](architecture-assessment.md)。目标文档只同步过时前言，职责、产品约束、迁移条件和 rubric 未变。

| DAG 切片 | 负责人 | 当前状态与下一动作 | 完成条件 |
| --- | --- | --- | --- |
| M0 基线与环境 | 主代理 | 已完成，固定 SDK 10.0.401 与基线 Runtime 1565 项通过；GTK3/4、Qt6 可编译 | 区分既有失败、可保持路径与未测平台能力 |
| M1 设置提交 | `/root/implement_settings` | 已补齐 Linux 简化保存入口、教程及 Windows 教程完成的明确结果交付；通知失败沿既有反馈显示，已保存教程仍按原行为关闭。真实 host 相关 10 项、反馈 4 项、练习 15 项与相关输入 4 项通过，待独立复评 | 视图不再推断提交点，所有保存入口交付明确结果，受影响行为验证完成 |
| M2 Linux 输入所有权 | `/root/implement_linux_input` | 实现完成；受控输入 221、setup 117、desktop 20、practice 14 项通过；完整原生交互未验收 | Overlay 的 Input 私有记录读写退出，回源与断连语义保持 |
| M3 交互身份与呈现 | 主代理、`/root/implement_presentation_consumers` | 实现完成；暂停呈现和表单单向身份已整合，浏览器三组共 34 项真实 DOM 回归、三端原生编译通过；GTK3 拒绝路径已在隔离桌面验证 | 显示与行为身份分离，各消费者旧比较退出 |
| M4 会话与结算 | `/root/implement_session_operations`、`/root/implement_write_settlement`、`/root/implement_windows_write_retirement` | 实现完成；真实 Linux host 关闭交接和 GTK3 精确未用许可退休通过；其它原生写入与退出路径仍缺平台证据 | 具体操作拥有寿命，主 Capture/Stop 退出专用分支，关闭先交还结果 |
| M5 教程职责 | `/root/implement_session_operations` | 已补齐按需练习命令的登记、观察和退休，主控制器不再写其私有字段；65 项受影响回归与最终全套 Runtime 通过 | 进度不参与权限，真实自有源证据保留 |
| M6 清理与复评 | 主代理 | `9dd5ada` 已获 85.3 分独立认可；已同步目标前言，Windows 四处通知反馈改用既有共享结果或常量，最新 App 编译通过，准备最后一次全新上下文确认 | 旧路径退出，受影响构建/验证完成，实际实现独立复评 |

实现代理均以 `fork_turns=none` 创建，请求配置为 `gpt-6-astra`、`xhigh`；工具只确认创建，实际配置未核实。主代理负责集成、冲突与检查，不让每项常规动作再等待用户确认。

`/root/implementation_spec_advisor_r2` 同样请求 `gpt-6-astra`、`xhigh`，工具只回显任务创建，实际配置未核实。它没有读取先前分数或评审结论，只读固定规则、目标正文、当前源码及必要验证原件。

实现取舍已由各次独立上下文 advisor 核对。`/root/implementation_advisor_writes` 明确指出：Published 许可不能凭 TTL、容量或断连当作未执行；普通未使用许可靠原执行者的精确未用退休事实结束，已认领执行靠原回执结算。Windows 复用原 Grant 的 `Quiet && !Claimed`，GTK/Qt/browser 在原源线程区分在途执行和已经完全退休的许可。超期缺事实才按原目标记为未知。正常关闭保持两端事件循环和原回执消费，结算后再释放。没有新持久账本、重试或通用 Quiet。

验证证据在 `artifacts/architecture/validation/`、`artifacts/m3-consumers/` 与 `artifacts/m3gtk/`。原生 GUI 目前仅通过 GTK3 Unknown IME 拒绝及未用许可退休路径；Windows WPF 运行和物理 IME 尚未在本轮验证。App.Tests 交叉编译受 Linux 缺少原生 `TypeLay.NativeInput.dll` fixture 限制，不宣称完整测试项目通过。后文旧 CI 与桌面事实只说明基线限制，不能替代当前工作区验证。

本次改进针对编辑回源、菜单呈现和保存设置的实际知识分散，证据与收益见目标文档的四项深化目标。用户故障发生频率未知；验证优先复用现有生产入口，只为新的责任交接补充必要场景，不扩展完整桌面或模型矩阵。

## 当前验证环境与交付范围

本机固定 .NET SDK 为 `artifacts/tools/dotnet/dotnet`，版本 10.0.401。GTK3、GTK4、Qt6 开发库可用，三端原生源码已编译。Windows App、Broker、Practice 与 NativeFixture 可交叉编译；本机不能运行 WPF。App.Tests 的 C# 编译成功，但完整构建仍缺生产 `TypeLay.NativeInput.dll`，不能把该结果写成完整测试项目通过。

浏览器验证使用隔离的 headless Chromium，没有连接用户的浏览器配置。GTK3 补验沿现有 `native_e2e.py --gtk-refusal`，使用独立 Xvfb、niri 和私有 D-Bus，原入口退出码为 0，覆盖 Unknown IME 拒绝、按键透传和精确单次 `writeRetired`，另含原有 8 项通知与 19 项受控直接写入边界。原始证据见 `artifacts/m3gtk/native-result.json` 与 `run.log`；自有测试进程已退出。Xvfb/xdotool 只解包到 `artifacts/tools/x11/`，已核系统包签名，未安装到系统。

GTK3 成功捕获、编辑与接受、GTK4/Qt GUI 仍未验证；完整合作 Fcitx 环境未准备。本轮没有操作用户桌面，没有物理键盘、IME 或逐页像素验收。已有模型生命周期和质量事实保持，未下载模型或扩大质量矩阵。

已用 `eng/linux/build.py --output artifacts/architecture/package` 构建正式 setup-only 包，`build-info.json` 记录 `9dd5ada` 且 `sourceDirty=false`。原有 `distribution_e2e.py` 的共享 worker 目录、真实 Host 设置/练习反馈、中文空格目录安装/重装/CLI 启动退出和卸载保留数据四项通过，日志为 `artifacts/architecture/validation/linux-relocated-package.log`。构建日志为 `linux-package-build-with-ninja.log`；首次缺 Ninja 的环境失败另保留在 `linux-package-build.log`，仅在私有工程 venv 补齐工具后完成构建。构建通过正式校验准备固定 CPU 运行库，没有下载模型。该包不含合作桌面 payload，不能用于宣称完整 GUI、合作 IME 或 Windows 分发已经验收。

本轮本地变更的完成条件是职责迁移、受影响验证、现役文档与独立实现评估。平台桌面验收缺口单独保留，不将其计作本轮已经通过，不自动恢复 CI、发布或此前诊断任务。

## 当前接续顺序

1. 已核对 `work/architecture-deepening` 的最新本地提交与工作树。保留现有源码、工具链和验证证据，不重做已通过且未再变动的检查。
2. 当前修正后的 Runtime 原件为 `artifacts/architecture/validation/runtime-pause.trx`，1592 项通过；Linux 宿主与 Windows App 最新构建均为零警告、零错误。Core、Linux 输入与浏览器原验证仍见前文；编译与受控端口不能冒充原生桌面通过。
3. 创建新的 `fork_turns=none` advisor 对收尾后的提交做最后一次独立确认。只给目标文件第 1 至 332 行、必要代码和原始验证证据，不给本页评分段、评估页、旧 advisor 报告或期待分数。请求配置仍为 `gpt-6-astra`、`xhigh`，实际未回显则记为未核实。
4. 主代理处理新证据支持的必须修正项；原生平台缺少运行条件时继续保持未验收，不能靠改 rubric 或制造通用抽象收敛。取得新的独立结论后更新评估页与本页，评分原件保持 advisor 原文。

当前整体验收未完成，按用户要求保留 `artifacts/architecture-baseline` 基线 worktree、临时诊断和测试证据，待达到完成条件后再清理。继续执行既定架构目标，不启动 GitHub 流程或另一个重构方案。

设置修正的验证先沿真实 host 复现结果被压成布尔值，再验证实际 Python 保存方法能保留共享消息。原 Host 回归的一个旧 setup 仍断言布尔值，迁移到明确 `saved` 后该项通过；练习 fixture 改读 `PracticeResult.allowed`，不把结果对象的 truthiness 当作允许。没有放宽实际安全断言。Ruff 检查本批 20 个 Python 文件通过，差异空白检查通过；完整 Runtime 和双端构建后仅 Python 反馈与测试收尾，相关 4 项反馈回归再次通过。

Windows 最后四处重复反馈只改为消费已有 `result.Message` 或 `SettingsCommitPresentation.NotificationFailedMessage`，没有增加状态、改变反馈或取消规则。App 最新交叉编译零警告、零错误，原件为 `artifacts/architecture/validation/windows-shared-feedback-build.log`；本机无法运行 WPF 横幅测试，因此不新增镜像测试或声称已运行这些测试。

## 仍需目标平台验证的已有问题

这些问题来自基线 [CI587](https://github.com/DingJWuyu/Typelay/actions/runs/42157722940)，不是本轮新头结果。它们仍限制产品验收；只有取得对应新证据才能关闭。具体产品缺口以[产品基线](../product/README.md)和[原型对照](../design/prototype-parity.md)为准。

| 已有问题 | 当前已知事实与后续验证边界 |
| --- | --- |
| Qt 目标语言搜索 | 实际 Composing 日语行的新 press 已到，clicked 等待失败；release 接收者与几何根因未明。验证原单次动作，不改原期限、次数或 IME 门 |
| GTK4 术语及配置 | 源端术语首例在 capture-more 到 retain-more 之间 fixture 退出，40 个案例未完成；另有配置窗口 readiness 时服务器焦点为 0、本地为 1 的分歧。不能以耗时或本地标志推断真实源失焦或 IME Clear |
| Windows 设置进度 | 首个目标帧未在原 3s 观察期限内出现，尚未到后续重定向断言。需沿实际 WPF 渲染定位，不放宽时限 |
| Windows 本地稿 Replace | 过期 claim 后原 anchor 已无效，尚未验证下一次 fresh 操作；需核实际取消与 claim 顺序，不续原 down 起算的 500ms |
| 普通输入框与浏览器 | 物理 Tab、完整中文 IME、不同显示器/DPI、完整混版及恢复路径尚未形成同头验收；软件注入、DOM 合同与受控端口不能替代 |
| 分发与视觉 | 搬迁后的完整发行包、真实原生 Undo/redo 与各端逐页像素一致性仍需对应平台证据；构建和静态检查不关闭这些缺口 |

Index 仍是用户明确准备时的默认模型，保留已有 Argos/Hy 安装与选择。术语仅对已登记固定 Index profile 的中英双向开放；风格、另述及组合质量门不因架构整理而开放。安装、握手、采集和语言质量是不同证据。
