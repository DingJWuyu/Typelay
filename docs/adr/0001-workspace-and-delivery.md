# ADR-0001: .NET workspace and runtime-only distribution

Status: Accepted.
Scope: workspace boundaries, developer workflow and distribution.

## Decision

`TypeLay.slnx` 是 .NET 工作区，`src/` 只包含生产组件。App 内按应用编排、基础设施、Windows 和 UI 划分职责，避免为目录或测试便利另建生产项目。

`tests/` 按可观察边界划分 behavior、contracts、e2e、fixtures 和 support。纯逻辑使用标准 xUnit；发布包和 Broker 协议通过真实子进程验证；受控桌面工具用于定位 Hook、焦点和编辑问题，但不代替生产 UIA 验收。

`dev.ps1` 是开发任务入口，CI 复用相同命令。工作区约束集中在共享构建配置中，开发工具与模型运行依赖分别管理。

发布包只复制显式运行时清单：启动器、App/Broker、自包含 .NET 运行时、worker、启动脚本、教程与必要声明。普通用户完整解压即可启动，不需要 SDK 或源码。组件和模型安装仍需用户明确同意。

README 面向使用者与开发者；AGENTS、glossary 和 `docs/agents/` 提供按任务读取的约束；`docs/reference/` 提供现役产品与 UI 基线。内容保留和删除遵循根 AGENTS 的仓库规则，ADR 只承担当前架构决策解释。

## Rationale and tradeoffs

统一 workspace 和命令入口能避免本地与 CI 维护两套构建语义。采用 .NET/WPF + Python 的原生工程结构，不为仿照其它语言项目引入 Cargo、空 benchmarks 或没有使用者的示例目录。

运行时分发把构建工具管理留给开发者，避免普通用户承担 SDK 下载、源码构建与工具链故障。代价是发布者必须生成目标平台的自包含包，并保留实际解析版本的上游许可证。

快速、确定性的行为测试适合覆盖安全判定和时序组合；全部改为 GUI E2E 会削弱反馈质量。真实进程测试和交互式工具补足各自边界，不能互相冒充覆盖。

性能基准只在有具体性能问题、明确设备/模型和可重复操作序列时引入；教程作为产品资产分发，不冒充性能证据或测试结果。

## Required verification

工作区和项目引用变更需要 solution 构建与受影响的测试；部署路径、资源或启动变更需要迁移后的发行包/进程 E2E。仅文档或注释变更按 `docs/agents/development.md` 执行最小相关检查。

B-01/B-02/B-03 的当前定义和关闭条件见 `docs/agents/architecture.md`。结构调整和 CI 成功不能关闭产品安全或真实桌面验收风险。
