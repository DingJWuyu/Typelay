# 架构目标的独立评估

本页记录用户授权的架构目标收敛结果，评分仅来自全新上下文 advisor。[目标架构](architecture-target.md)尚未实施，[固定 rubric](architecture-rubric.md)的权重和锚点在第一轮前锁定。

## 当前结论

目标架构已收敛。连续两位全新上下文 advisor 对同一目标文件独立给出 **90.3/100** 和 **90.5/100**，均认可作为实施目标，必须修正项为零，五项产品与可行性约束全部满足设计要求。

设计评估没有达到用户希望争取的 95 分。两位 advisor 都建议结束设计扩展，把剩余检查放到实际迁移中。继续增加工作流框架、全局账本或跨语言生成层没有得到支持。用户现已授权实施，进度见[当前开发状态](development-state.md)；rubric 与这份设计评分不因开始实施而改变。

设计评估的代码基线为 `48baf7fe59a65a67a7a981470e259f01fdc7b4a4`。上述设计分数不覆盖其后的实现变更；产品规格、已确认原型和有效 ADR 继续约束迁移。

## 最终独立评分

评分原件为 [advisor-r4.json](../../artifacts/architecture/advisor-r4.json) 和 [advisor-r5.json](../../artifacts/architecture/advisor-r5.json)。下表转录 advisor 的分数，主代理只核对哈希与加权算术，没有自行评分。

| 维度 | 权重 | advisor r4 | advisor r5 |
| --- | ---: | ---: | ---: |
| A 产品与架构匹配 | 15% | 9.0 | 9.0 |
| B module 职责与 interface | 20% | 9.0 | 9.0 |
| C 修改的 locality | 20% | 9.0 | 9.0 |
| D 跨平台 leverage | 15% | 9.0 | 9.0 |
| E 原文保护与失败处理 | 15% | 9.5 | 9.5 |
| F 可测试性与诊断 | 5% | 9.0 | 9.0 |
| G 代码组织与可理解性 | 5% | 8.5 | 8.5 |
| H 运行与分发 | 5% | 8.5 | 9.0 |
| **加权总分** | **100%** | **90.3** | **90.5** |

单项满分 10 分，加权总分满分 100 分。r4 的未舍入结果为 90.25，按固定规则保留一位为 90.3；r5 为 90.50。

第一轮 advisor 对现有实现的独立评分是 **68.0/100**，见 [advisor-r1.json](../../artifacts/architecture/advisor-r1.json)。对话中的 62 分是原参考值，差异来自评价校准。现有代码仍未改变，不能把设计分数解释为实现已经从 62 或 68 分提升到 90 分。

此前的 [85.8 分](../../artifacts/architecture/advisor-r2.json)和 [92 分](../../artifacts/architecture/advisor-r3.json)分别对应较早的目标文本。最终采用上表同一文件的两次独立结论，评分变化说明工程判断存在差异。

## 收敛依据

两位 advisor 均确认四个深化目标有真实生产消费者和具体旧职责退出条件：会话操作集中管理交互寿命；Linux Input 独占源字段记录；共享呈现区分显示与交互身份；设置提交返回实际结果。关键回源、同步接受、独立直接任务和关闭交还均有具体合同，没有通过减少合法用户路径换取简化。

两轮审查分别由 `/root/architecture_advisor_r4` 和 `/root/architecture_advisor_r5` 执行，均以 `fork_turns=none` 创建。两者只读取固定 rubric、目标、术语及必要产品、ADR、源码和验证入口，没有读取对方或先前分数。请求配置均为 `gpt-6-astra`、`xhigh`；工具只确认代理创建，没有回显实际模型配置，因此实际配置未核实。

两份评分原件记录了相同的文件指纹：

```text
architecture-target.md
3c3004fcb2d98c303d2860aa8562d63f95ac39eabb972f269a7b290867569ff7

architecture-rubric.md
a57c7d6fa66e97b6ffd8b800082a5e26ce6afdfda1427d617eb07a62a7025b85

GLOSSARY.md
4ae54c1df0dab734e0ccb135fb67dd923faf283928b1599f67340854c77d469c
```

固定 rubric 的双重确认条件已满足。本页维护评分与完成状态，目标文件保持两位 advisor 审查时的原文。

## 后续实施要证明什么

这些是两位 advisor 指出的实施检查点，具体顺序与退出条件以[目标架构的迁移 DAG](architecture-target.md#迁移顺序与退出条件)为准。

- **职责确实退出旧位置。** 主 Capture/Stop 不再解释浏览器和教程操作的内部阶段，Overlay 不再读写 Input 私有记录，两端视图不再推断设置提交点。新类数量和目录移动不构成完成证据。
- **正常路径与异常路径都保持。** 普通 Tab、鼠标、browser 后来稿与 ABA、owned Keep/Replace 各按原语义继续。未使用的许可与已执行写入分别处理，普通许可过期不能被误当成写入失败或永久隔离。
- **各真实消费者完成协议迁移。** 显示字段、行为关联与原生权限分别映射；对应旧比较在该消费者切换后退出。跨进程最终校验继续保留。
- **宿主仍能接收结果直到责任交还。** 通过实际 Windows/Linux 关闭入口观察回执消费与释放顺序，保持有界等待；未知如实未知，同步源接受零补写。
- **保存与分发结果可追溯到实际事实。** 设置文件提交、通知和平台副作用分别报告；源码链接、资源和发行清单随切片更新。现役架构入口最终指向实际代码，目标说明不长期充当第二份当前事实。

## 验证范围与交付物

advisor 进行源码静态抽查，未运行产品 GUI、构建、测试或模型。性能、内存和翻译质量未评分，既有平台能力缺口仍保持原记录。

主代理核对了两份最终报告的文件哈希和评分算术、文档差异与本地链接。图文报告使用独立 headless Chrome 检查呈现，没有操作用户桌面；这不构成产品 GUI 验证。

可阅读[目标架构](architecture-target.md)、[固定 rubric](architecture-rubric.md)及[图文报告](../../artifacts/architecture/architecture-review.html)。后续实施状态仍维护在唯一的[当前开发状态与安排](development-state.md)。
