# TypeLay

TypeLay 是 Windows/Linux 的本地输入翻译工具。自动模式跟随当前文本输入框显示译文，支持修改候选，并在满足安全条件时用 Tab 接受；按需模式由快捷键唤醒翻译 UI，或执行一次性直接翻译并替换。两端均处于开发预览，尚未完成通用输入支持与完整产品验收。

## 使用与当前限制

- [Windows 预览](docs/platforms/windows/README.md)：下载、设置、按需操作与当前写回范围。
- [Linux 预览](docs/platforms/linux/README.md)：CachyOS + niri/Wayland 的安装、合作输入和能力边界。

Windows 原有实验性 Tab 写回默认关闭，通用 UIA 读/校验/写入不能组成原子事务。直接替换与 Linux 合作路径各自依赖经过校验的目标能力；构建或自动化通过不等于物理输入法、Tab、所有应用或完整产品验收通过。

Windows/Linux 同步推进共同产品功能与体验，各平台问题和验收分别记录；一端通过不代表另一端通过。[Windows 待办](docs/platforms/windows/open-issues.md)保留当前问题和复现证据。

源码中新用户明确准备时默认选择 **Index Translate 2B Q4_K_M**，已有 Argos/Hy 安装与选择不自动改变。启动不下载模型；模型更换不改变输入、焦点、预览、写回与 Undo 的安全条件。当前切换的实际验证及未通过项见[开发状态](docs/engineering/development-state.md)，不把新源码默认等同于既有推荐包已升级。

## 产品验收入口

两端共同按[冻结规格 v1.0](docs/product/TypeLay_产品规格_v1.0.md)及已接受的[自动/按需与一次性直接替换增量](docs/adr/0002-on-demand-and-direct-write.md)验收。重叠处以已接受增量为准，其余冻结要求继续有效。共同模式、交互基线与当前共同开发缺口见[产品说明](docs/product/README.md)。平台实现和现有缺陷不改写产品要求。

## 从源码开发

三条线的当前进度、待办、负责人和先后关系统一见[当前开发状态与安排](docs/engineering/development-state.md)。

长期集成入口是 trunk，main 保留为稳定发布分支。分支权限见 [AGENTS.md](AGENTS.md)；依赖、共享检查与 CI 见[工程开发流程](docs/engineering/development.md)，实际平台命令分别见 [Windows 开发](docs/platforms/windows/development.md)与 [Linux 开发](docs/platforms/linux/development.md)。验收证据分层见[测试协议](docs/engineering/testing.md)。

## 源码与文档导航

先看[三张架构图](docs/engineering/architecture.md#先看整体怎么工作)，了解共享代码、运行进程与翻译写回的区别。

```text
src/shared/                 Core、Runtime、Protocol、Python worker
src/windows/                WPF、Broker、Windows.Contracts、原生入口与输入事务
src/linux/                  Linux 宿主、desktop/browser/vscode/native
eng/shared/                 跨平台共享检查
eng/windows/                Windows 构建、开发与打包任务
eng/linux/                  Linux 构建与 cooperative 固定输入/补丁
packaging/{windows,linux}/  运行时启动、安装与发行包装配
tests/{shared,windows,linux}/  按实际所有者划分回归及受控工具
docs/product/               冻结规格、已接受增量入口与共同产品行为
docs/design/                已确认 UI 原型
docs/engineering/           共享架构、开发和验证约定
docs/platforms/             Windows/Linux 专属使用、实现与验收
docs/adr/                   当前有效的工作区、产品增量与共享运行时决策
docs/agents/                Agent skills、任务跟踪、领域与 triage 指引
.agents/skills/             固定版本的上游 skills
```

Windows 与 Linux 保留独立宿主，不复制 Core、Runtime 或 worker；Linux 内多个后端共享的实现仍属于 Linux。模块归属与安全不变量见[架构约束](docs/engineering/architecture.md)，接口取舍见 [ADR-0003](docs/adr/0003-cross-platform-runtime.md)。VS Code 仅提供手动命令，尚未交付 macOS。

## 维护约定

根 AGENTS.md 是 Agent 入口，按任务读取现役文档。仓库只保留当前开发、运行、测试或明确后续维护需要的内容，历史追溯使用 Git。没有已验收性能基准时不建空 benchmarks；复杂用例由产品教程承担。

Matt Pocock skills 保留上游 user-invoke / model-invoke 分类，分支直接提交约定优先于默认 PR 工作流。许可证及第三方组件说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)；B-01/B-02/B-03 及关闭条件见[开放风险](docs/engineering/architecture.md#开放风险与安全边界)。
