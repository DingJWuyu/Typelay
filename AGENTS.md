# TypeLay agent instructions

TypeLay 是 Windows 本地翻译浮层。先保持既有产品行为，再改进实现。

## 不变量

- 只在 `DingJWuyu/Typelay` 的 `windows-preview` 开发、提交和验证；不改动、合并到、删除或重命名 `main`。windows-preview 是长期分支，不删除或重命名。推送前检查远端，禁止覆盖他人提交。不自动创建 PR。
- 用中文沟通；使用普通连字符，不使用长破折号。提交信息不添加 Agent 署名或 co-author。
- 不手动修改 CHANGELOG.md、自动生成文件或上游 skills。生成物通过相应工具更新；冻结输入和历史证据不重写内容。
- 优先质量、简单性、健壮性和长期维护；不为单次操作建立平台、策略层或多余抽象。
- 缺陷修复先沿实际用户路径复现；在能观察同一故障的边界保留回归。不要用源码字符串、实现反射或假模型测试证明产品可用。
- 未知输入法状态、焦点身份和写入安全不得猜测或降级绕过。普通输入框是产品目标，代表性应用不是支持名单。
- 发现真实 UI 问题、lint 失败、测试失败或不稳定性时处理根因；不以重试或放宽断言掩盖问题。
- 立即产生大量子代理的 harness / dynamic workflows / ultra code 需要事先说明取舍并取得用户明确批准。
- 达到完成条件后删除临时文件；未完成时保留证据和进度。报告失败、未测和环境限制，不冒充通过。

## 按任务读取

- 改代码前读 `GLOSSARY.md` 与 `docs/agents/architecture.md`；涉及布局、依赖或打包决策时读 `docs/adr/0001-workspace-and-delivery.md`。
- 执行开发命令、增加依赖或调整 CI 时读 `docs/agents/development.md`，命令以根目录 `dev.ps1` 为准。
- 改测试、修复缺陷或验收时读 `tests/AGENTS.md` 与 `docs/agents/testing.md`。
- 改产品行为时读 `docs/reference/TypeLay_产品规格_v1.0.md`；改 UI 时再打开该目录的两个已确认 HTML 原型。不要以工程整理为由改规格。
- 查历史决策或旧证据时才读 `docs/history/AGENTS.md`，不要把退休脚本作为当前命令执行。

## Agent skills

按 `docs/agents/skills.md` 的触发规则使用 `.agents/skills/`。model-invoke 的条件成立时读取对应 SKILL.md 及其要求的参考文件；user-invoke 仅在用户明确调用或明确请求该流程时使用。

### Issue tracker

本仓库使用 GitHub Issues。需要读写任务、规格或依赖时读 `docs/agents/issue-tracker.md`。PRs as a request surface: no。

### Triage labels

执行用户请求的 triage 时读 `docs/agents/triage-labels.md`；不要为了整理代码自动创建任务或改变标签。

### Domain docs

单一领域上下文：根目录 `GLOSSARY.md` 与 `docs/adr/`。进行领域探索或命名决策时读 `docs/agents/domain.md`。
