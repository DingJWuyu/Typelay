# ADR-0001: .NET workspace and runtime-only distribution

Status: Accepted.
Scope: workspace boundaries, developer workflow and distribution.

## Decision

`TypeLay.slnx` 是 .NET 工作区，`src/` 只包含生产组件。Windows App 保留基础设施、Windows 和 UI；跨平台产品编排已由 ADR-0003 明确迁入 TypeLay.Runtime。仅在有实际职责与依赖隔离需要时建立生产项目，不为目录或测试便利拆分。

`src/` 和 `tests/` 先按 shared、windows、linux 的实际所有者划分。平台内 fixtures 是受控宿主/资源，support 是多个测试消费的测试库；没有跨平台消费者时不建立共享 fixture/support。纯逻辑使用标准 xUnit；发布包和 Broker 协议通过真实子进程验证；受控桌面工具用于定位 Hook、焦点和编辑问题，但不代替生产 UIA 验收。

`dev.ps1` 是 Windows 与共享检查的薄入口，分别分发至 eng/windows/dev.ps1 与 eng/shared/check.ps1；`eng/linux/build.py` 是 Linux 构建入口；CI 复用对应平台的相同命令。工作区约束集中在共享构建配置中，开发工具与模型运行依赖分别管理。

各平台发布包只复制自己的显式运行时清单：宿主与输入组件、自包含 .NET 运行时、worker、启动/安装文件、用户说明及必要资源和声明。Windows 保留 App/Broker 与原生启动器，Linux 保留其桌面与合作输入模块。普通用户完整解压即可启动，不需要 SDK 或源码。组件和模型安装仍需用户明确同意。

README 面向使用者与开发者；AGENTS 与 docs/agents/ 提供 Agent 操作指引，GLOSSARY.md 定义领域词汇；docs/engineering/ 提供共享工程约定，docs/platforms/ 提供平台专属方法；docs/product/ 与 docs/design/ 分别提供现役产品和 UI 基线。内容保留和删除遵循根 AGENTS 的仓库规则，ADR 只承担当前架构决策解释。

## Rationale and tradeoffs

统一 workspace 和命令入口能避免本地与 CI 维护两套构建语义。采用 .NET 与 Python 的原生工程结构，Windows 保留 WPF，Linux 使用本机桌面工具包；不为仿照其它语言项目引入 Cargo、空 benchmarks 或没有使用者的示例目录。

运行时分发把构建工具管理留给开发者，避免普通用户承担 SDK 下载、源码构建与工具链故障。代价是发布者必须生成目标平台的自包含包，并保留实际解析版本的上游许可证。

快速、确定性的行为测试适合覆盖安全判定和时序组合；全部改为 GUI E2E 会削弱反馈质量。真实进程测试和交互式工具补足各自边界，不能互相冒充覆盖。

性能基准只在有具体性能问题、明确设备/模型和可重复操作序列时引入；教程作为产品资产分发，不冒充性能证据或测试结果。

## Required verification

工作区和项目引用变更需要 solution 构建与受影响的测试；部署路径、资源或启动变更需要迁移后的发行包/进程 E2E。仅文档或注释变更按 `docs/engineering/development.md` 执行最小相关检查。

B-01/B-02/B-03 的当前定义和关闭条件见 `docs/engineering/architecture.md`。结构调整和 CI 成功不能关闭产品安全或真实桌面验收风险。
