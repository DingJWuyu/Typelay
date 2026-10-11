# Matt Pocock skills

Source: https://github.com/mattpocock/skills
Revision: 49dd158d1076134a641b33efb035946536778336
Installer: skills CLI 1.7.2, project scope, universal agent directory.

27 个正式技能及各自的参考文件保留在 .agents/skills，安装器生成 skills-lock.json。保留原始文件，不翻译、压缩或手工重写 SKILL.md。更新时明确选择 revision 与本文件列出的 27 个 skill 名称，通过安装器重建并检查 diff；不要用 --skill *，以免引入上游的实验或归档技能，不在 CI 自动追踪 latest。只使用一种安装方式；已由编辑器全局插件提供同一套技能时避免重复注册。

本目录供能发现 .agents/skills 的编码 Agent 使用。无法原生调用 skills 的环境应读取原文执行可用步骤，明确说明缺少的调用/子代理能力，不能声称已经运行不存在的 Skill 工具。

## Model-invoke: 11

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

## User-invoke: 16

工程：ask-matt、grill-with-docs、triage、improve-codebase-architecture、setup-matt-pocock-skills、to-spec、to-tickets、implement、implement-spec、wayfinder、retro。
生产力：grill-me、handoff、teach、to-questionnaire、wait-what。

这些技能保留 disable-model-invocation，不因“当前整理相关”自行触发。项目配置位于根 AGENTS 的 Agent skills 区块及 issue-tracker/domain/triage-labels 文件；不会自行发布任务或开启 PR。
