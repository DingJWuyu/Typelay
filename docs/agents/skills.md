# Agent skills

## 模型分工与 advisor

当前所有 Agent 任务统一使用用户指定的目标配置：**`gpt-6-astra` + `xhigh`**，包括研究、规划、开发、修复、独立审查、写作、advisor 和最简单的机械任务，不选用其他模型或思考强度。后续平台配置能力变化不自动恢复旧分档规则；仅按用户的新决定调整。历史分工查 Git，不保留并行生效的旧表。

按实际职责与可独立验证的边界分派，必要时拆出独立判断职责，不为每个动作创建代理。这不改变 TypeLay 产品内的翻译模型、运行预算或发布授权，也不是自动配置或强制执行机制。

派发时在现有任务记录中写清任务、负责人或 Agent 标识、目标模型与思考强度，以及工具实际确认的配置。工具未确认的实际值标记为未核实，不能用请求值冒充。平台或工具不能选择目标配置时，如实说明具体限制、实际可用配置与受影响工作，不静默换用其他配置或声称目标已生效；继续不受影响且已获授权的工作。长程任务仍使用[当前开发状态与安排](../engineering/development-state.md)维护现役职责，不另建分工快照，也不保存原始内部提示词。

### advisor 的职责与决策边界

- 主代理遇到待解决的问题、不确定性或取舍时，主动请独立 advisor 判断，不限于任务编排。给出目标、已知证据、约束与待决问题，让 advisor 独立检验假设、比较选项并提出建议。
- 主代理检查建议与证据，自行抉择并负责执行和验证，不把普通技术选择交回用户，也不把 advisor 的意见当成授权或已验证事实。
- 产品偏好、需求变更及必须由用户批准的动作仍由用户决定；权限、安全边界和既有批准条件保持不变。
- 咨询围绕具体问题，以形成有依据的决定和下一验证动作收束；同一问题在证据与约束未变时复用结论，新证据或实质变化再咨询。不让每个常规动作等待 advisor，不递归开启顾问链，也不阻塞无关的已授权工作。缺少独立调用能力时报告限制并明确标记自审，不冒称独立咨询。

## Matt Pocock skills

Source: https://github.com/mattpocock/skills
Revision: 49dd158d1076134a641b33efb035946536778336
Installer: skills CLI 1.7.2, project scope, universal agent directory.

Matt Pocock 的 27 个正式技能及各自的参考文件保留在 .agents/skills，安装器生成 skills-lock.json。保留原始文件，不翻译、压缩或手工重写 SKILL.md。更新时明确选择 revision 与本文件列出的 27 个 skill 名称，通过安装器重建并检查 diff；不要用 --skill *，以免引入上游的实验或归档技能，不在 CI 自动追踪 latest。只使用一种安装方式；已由编辑器全局插件提供同一套技能时避免重复注册。

本目录供能发现 .agents/skills 的编码 Agent 使用。无法原生调用 skills 的环境应读取原文执行可用步骤，明确说明缺少的调用/子代理能力，不能声称已经运行不存在的 Skill 工具。

### Model-invoke: 11

条件成立时读取对应 .agents/skills/<name>/SKILL.md 和其要求的参考文件。不要为了凑数量执行不相关流程。

| Skill | 触发场景 |
| --- | --- |
| writing-for-agents | 编写或改动 steering docs、文档、技能和 Agent 上下文 |
| codebase-design | 模块边界、接口、依赖与长期可维护性决策 |
| domain-modeling | 领域概念、歧义命名或需要记录的实际 ADR |
| tdd | 通过公开边界的红绿重构；尤其行为变更与回归 |
| diagnosing-bugs | 意外行为、测试失败、崩溃或不稳定性；先建立失败反馈环 |
| code-review | 交付前分别做工程标准和需求符合性审查 |
| research | 已识别的知识缺口，需要查证而不是猜测 |
| prototype | 真正需要一次可丢弃实验以降低实现不确定性 |
| wizard | 只有确实需要用户手动完成凭据、控制台或基础设施步骤时生成交互式引导；Agent 可自行执行的工作不触发 |
| grilling | 用户明确要求 grill 或压力测试某个决策时；不能将一般澄清需求视为触发 |
| pr | 用户明确需要 PR 工作流时；本项目默认直接提交分支，不触发 |

code-review 若要求独立子代理而当前环境没有该能力，则分别执行 Standards 与 Spec 两轮自审并如实说明，不伪称独立评审。大量子代理流程仍须用户事先批准。

### User-invoke: 16

工程：ask-matt、grill-with-docs、triage、improve-codebase-architecture、setup-matt-pocock-skills、to-spec、to-tickets、implement、implement-spec、wayfinder、retro。
生产力：grill-me、handoff、teach、to-questionnaire、wait-what。

这些技能保留 disable-model-invocation，不因主题相关而自行触发。项目配置位于根 AGENTS 的 Agent skills 区块及 issue-tracker/domain/triage-labels 文件；不会自行发布任务或开启 PR。技能输出同样遵守根 AGENTS 的内容保留规则，不另建历史归档。

## HumanLayer show-me

Source: https://github.com/humanlayer/skills
Revision: 653b6411c1f70c275a18e37673b042ff99f67ceb
Source path: plugins/show-me/skills/show-me/
License: MIT，见 `.agents/skills/show-me/LICENSE`（上游根 LICENSE 原文）。

用户明确调用 show-me，或明确要求用此技能画架构图、流程图时，读取 `.agents/skills/show-me/SKILL.md`。保持上游 user-invoke 设置：`disable-model-invocation: true` 与 `agents/openai.yaml` 中的 `allow_implicit_invocation: false`。图描述当前源码中的真实模块、边界与流程；按问题选择最小清晰视图，图旁保留必要说明。HTML 展示使用当前环境实际可用的浏览器或文件交付方式。

此技能按固定 revision 直接复制上游 `SKILL.md`、`agents/openai.yaml`，另附根 LICENSE；不安装上游插件或依赖，也不改写技能原文。更新时先审查指定 revision，再复制这两个文件及许可证并核对字节一致。其固定来源由本节管理；`skills-lock.json` 仍由原安装器管理 Matt Pocock 的 27 个技能，不手工增写生成锁文件。
