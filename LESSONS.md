# Lessons from project work

审查工作或运行 retro 前，读取本文件。

尚未提升为规范的有效经验保存在 Lessons 一节。LESSONS-ARCHIVE.md 保存已提升和已停用的条目。首次移动条目时再创建该文件，包含 `## Promoted lessons` 和 `## Inactive lessons` 两节。

## Record a lesson

能够说明已确认的错误及类似工作中的避免方法后，再记录经验。

- 审查：记录负责人阅读代码、测试、设计或文档审查后确认的错误。
- 回顾：记录负责人批准的 retro 经验。

先在本文件和已有的 LESSONS-ARCHIVE.md 中查找相同错误。已有条目就更新；新条目使用以下字段：

- **Rule**：应该怎么做，以及适用条件。
- **Kind**：确定性检查可以发现的错误记为 `mechanical`，例如固定语法模式、禁用 API、导入形式或文件位置；其余记为 `judgement`。
- **Why**：发生了什么错误，以及原因。
- **Evidence**：能证明该错误的 issue、PR、提交或复现。
- **Seen**：独立发生次数和每次日期，从 1 开始。

记录 mechanical 经验时，同时提出对应的 lint 规则、pre-commit hook 或 CI 检查建议。

审查和回顾发现同一次错误只计一次；同一次错误的多轮修复也只计一次。两次发生之间间隔很长，可能只是同类工作较少。条目增多后按相关主题分组。

## Update a lesson

错误再次发生时，补充案例和日期，并增加 Seen。提升为规范后仍继续计数。

新证据改变规则或解释时，更新 Rule 和 Why。已经提升的规则在其现行位置修改。保留原始证据，并在旁边记录更正。

## Promote a lesson

- judgement：Seen 达到 3 时，核对这些案例是否遵守了规则。遵守后仍失败的，先修正规则，再将适用条件和例外写入 docs/CODING_STANDARDS.md。
- mechanical：对应检查已经落实后提升。

将条目移入 LESSONS-ARCHIVE.md 的 `## Promoted lessons`，移除 Rule 字段，链接现行规范或检查。规则只保存在现行位置，Why、Evidence 和 Seen 留在经验条目中。修改已提升的规则或检查时，把原因写入对应条目的 Why。

## Retire or restore a lesson

错误修复后保留仍适用的经验。经验不再适用时，将完整条目移入 `## Inactive lessons`，记录日期和原因。已提升的条目先补回 Rule，再删除规范中的规则或对应检查。删除规则或检查时，同步停用对应经验。

新案例与已停用经验匹配时，核对是否为同一类错误，以及规则是否仍适用。必要时修正规则，再恢复条目：

- 曾经提升：恢复 docs/CODING_STANDARDS.md 中的规则或对应检查，将条目移入 `## Promoted lessons`，移除其中 Rule 字段。
- 尚未提升：将条目移回 Lessons。

沿用 Seen 计数，并补充新案例和日期。

## Lessons
