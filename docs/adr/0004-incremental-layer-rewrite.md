# ADR-0004：按层局部重写，逐消费者迁移

状态：接受。负责人于 2026-11-13 确认本轮重构路线。

TypeLay 采用按层局部重写，每次迁移一个真实消费者和一条用户路径。产品行为继续以[冻结规格](../product/TypeLay_产品规格_v1.0.md)、[ADR-0002](0002-on-demand-and-direct-write.md)、[浮层原型](../design/TypeLay_UIUX_Prototype.html)和[托盘与设置原型](../design/TypeLay_Tray_Settings_Prototype.html)为准；代码、测试、CI、工程文档和交付流程均可调整。

这条路线允许重新设计职责和接口，同时逐步验证迁移对现役消费者的影响。仅整理现有实现会保留不合适的接口约束；整体重写则会同时重新暴露原生输入、输入法、写回、撤销和分发边界的风险。

工作按 spec、tickets、堆叠 PR 的顺序交付，最终合入 `hitl`，由负责人按风险审查并合并。各批次的范围和验收条件由对应 spec 确定。
