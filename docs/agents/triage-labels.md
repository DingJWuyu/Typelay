# Triage labels

执行用户请求的 triage 流程时使用以下角色映射，不根据状态名猜测别名。

| Role | Label |
| --- | --- |
| needs-triage | needs-triage |
| needs-info | needs-info |
| ready-for-agent | ready-for-agent |
| ready-for-human | ready-for-human |
| wontfix | wontfix |

配置描述词汇，不等于远端已经创建。真正执行 triage 时先查询现有 labels，仅在该次任务授权范围内补齐缺少项。此轮工程整理不批量修改 issue 或 labels。
