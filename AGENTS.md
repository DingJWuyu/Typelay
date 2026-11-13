# TypeLay agent instructions

TypeLay 是跨平台本地输入框翻译浮层。

## 硬性不变量

- 产品需求仅来自[冻结规格](docs/product/TypeLay_产品规格_v1.0.md)、已接受的 [ADR-0002](docs/adr/0002-on-demand-and-direct-write.md)、[浮层原型](docs/design/TypeLay_UIUX_Prototype.html)和[托盘与设置原型](docs/design/TypeLay_Tray_Settings_Prototype.html)。改变产品行为须取得负责人的明确授权。
- 未知输入法状态、焦点身份和写入安全不得猜测或降级绕过。
- 使用中文沟通，不使用长破折号。提交信息不添加 agent 署名或 agent co-author。

## 分支与交付

工作从 `hitl` 切分支，经 PR 合回 `hitl`，由负责人审查并合并；PR 的创建与堆叠操作见 [issue-tracker.md](docs/agents/issue-tracker.md)。

禁止对 `hitl`、`main`、`trunk` 强推。`main`、`trunk` 仅按负责人的明确授权更新。

## 仓库内容保留

Git 历史负责追溯，不建归档目录、阶段报告或历史快照。

## 按任务读取

- 改代码前，读取 [GLOSSARY.md](GLOSSARY.md) 和[系统架构](docs/engineering/architecture.md)。涉及布局、依赖或打包决策时，再读取 [ADR-0001](docs/adr/0001-workspace-and-delivery.md)。
- 执行开发命令、增加依赖或调整 CI 时，读取[开发流程](docs/engineering/development.md)。Windows 和共享检查使用根目录 `dev.ps1`；Linux 构建和部署见 [Linux 开发](docs/platforms/linux/development.md)、`eng/linux/build.py` 和 `eng/linux/cooperative/`。
- 改测试、修复缺陷或验收时，读取 [tests/AGENTS.md](tests/AGENTS.md) 和[测试策略](docs/engineering/testing.md)。
- 改产品行为时，从 README 的[产品验收入口](README.md#产品验收入口)读取基线；改 UI 时，同时读取[设计入口](docs/design/README.md)和两份已确认原型。
- 查阅平台使用、架构或验收方法时，读取 [Windows](docs/platforms/windows/README.md) 或 [Linux](docs/platforms/linux/README.md) 平台入口。

## Agent skills

使用机器上安装的全局工程 skills。

### Issue tracker

规格和 tickets 使用 DingJWuyu/Typelay 的 GitHub Issues。读写任务或依赖时，读取 [issue-tracker.md](docs/agents/issue-tracker.md)。

### Triage labels

执行 triage 时，读取 [triage-labels.md](docs/agents/triage-labels.md) 中的五个默认角色映射。

### Domain docs

本仓库使用单一领域上下文。探索领域、命名或评审设计前，按 [domain.md](docs/agents/domain.md) 读取 GLOSSARY.md 和相关 ADR。

### Docs

编辑各级 AGENTS.md、docs/agents/、docs/CODING_STANDARDS.md、LESSONS.md 或 LESSONS-ARCHIVE.md 时，使用 writing-for-agents。docs/engineering/architecture-target.md、docs/engineering/testing.md 和 docs/design/prototype-parity.md 也按执行约束编写，使用 writing-for-agents。其他文档使用 technical-writing。

## Coding standards

编写和审查代码时，遵循 [docs/CODING_STANDARDS.md](docs/CODING_STANDARDS.md)。修改规范时，按 [LESSONS.md](LESSONS.md) 更新对应经验条目。

## Lessons

审查工作或运行 retro 前，读取 [LESSONS.md](LESSONS.md)，并按其中规则记录和更新经验。
