# Domain docs

本仓库使用单一领域上下文：根目录 GLOSSARY.md 和 docs/adr/。App、Broker、worker 的进程隔离不代表多个业务领域。

## 读取领域资料

探索领域、命名或评审设计前，读取根 [GLOSSARY.md](../../GLOSSARY.md) 和 [docs/adr/](../adr/) 中涉及当前范围的决策。涉及 Linux 原生坐标时，再读取 [docs/platforms/linux/adr/](../platforms/linux/adr/)。

文件缺失时继续工作。术语或决策实际得到确认后，由 domain-modeling 按需建立对应文档。

## 使用统一术语

代码、测试、任务和假设使用 GLOSSARY.md 定义的名称，特别区分 Preview、Candidate、WritePlan 和 AcceptancePermit。缺少术语时，先判断它是否是本项目的领域概念；通用编程术语不加入 glossary。

## 处理 ADR 冲突

发现与现有 ADR 冲突时，指出对应决策和冲突原因。新 ADR 记录已实际解决的决定，日期与证据来自本次操作。产品行为变更遵循根 AGENTS.md 的授权要求。
