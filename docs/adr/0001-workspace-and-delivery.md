# ADR-0001: .NET workspace, runtime-only distribution and observable regressions

Status: Accepted for the windows-preview cleanup.
Date: 2026-10-10, America/Phoenix.
Scope: repository organization and developer workflow, not product behavior.

## Context

原工作区把源码、历史阶段文档、已生成 exe、验收日志与运行分发混在一起。协议/策略测试名为 Integration 但没有跨进程执行；生产源码目录还包含只供旧 fixture 使用的适配器。发布脚本复制整个开发工程，启动器维护另一套私有 SDK 下载和构建流程。

参考 apache/asyncband 与 apache/datasketches-rust 的 workspace / xtask 原则：统一成员与约束，面向用户边界组织测试，按实际需要提供工具和示例。TypeLay 实际采用 .NET/WPF + Python，不引入 Cargo、Rust crates 或对等的空目录。

## Decision

1. TypeLay.slnx 是 .NET 工作区，src 仅保留生产组件；App 内按应用编排、基础设施、Windows 与 UI 分组。保持公开类型/命名空间，避免无意义的跨项目拆分。
2. tests 区分 behavior、contracts、e2e、fixtures 和 support。C# 回归使用标准 xUnit / dotnet test；删除自制执行注册表与源码字样验收。真实发行包和 Broker 子进程承担对应边界回归。
3. dev.ps1 是开发者任务入口，CI 复用它；不引入常驻控制面、复杂矩阵或自动提交工作流。
4. pack 只部署运行时显式清单，从源代码生成 native launcher；不再分发源码、技能、测试、历史文档或 SDK 下载器。保留首次显式组件/模型安装和原有安全开关。
5. README 面向人。AGENTS、glossary、ADR 与 docs/agents 面向 Agent。docs/reference 和 docs/history 原文不改，用目录级说明标记适用性；第三方法律文本保留原文。
6. 当前没有可信性能基准，不放空 benchmarks；教程是产品资产，不伪装成测试或示例。需要具体性能决策时再引入可重复测量。

## Alternatives rejected

保留源码分发与自动 SDK 下载会形成第二套构建系统，并使普通用户承担开发工具管理。
按 Rust 目录逐项新建空目录没有消费场景；继续保留所有旧测试并新增一层包装则仍维护两套测试执行语义。
把所有逻辑测试改为 GUI E2E 会降低确定性，也不能替代快速时序/安全状态回归。选择与实际边界匹配的行为测试，而不是按层级名决定价值。

## Consequences and validation

开发者使用固定 SDK 和显式工具；普通使用者只需完整解压预编译 ZIP。目录变更必须由 solution 构建、公开行为测试和迁移后的分发/进程回归一起验证。

B-01 非原子写回、B-02 真实桌面验收、B-03 通用编辑后端限制继续保留。任何一次 CI 成功都不能自动关闭它们。
