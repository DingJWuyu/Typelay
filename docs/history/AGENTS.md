# Historical evidence boundary

仅在追溯旧决策、PoC 结论或验证证据时读取。本目录不参与当前构建、测试、打包或 Agent 指令发现。

迁移只改变路径，历史记录与生成日志不重写。内部相对路径、旧目录名、旧命令和状态描述对应产生记录时的代码，不是当前工作区入口。当前命令由根 dev.ps1 提供。

milestones/ 来自原 archive/；preview-docs/ 来自原 docs/；preview-evidence/ 是原 evidence/；windows-validation/ 是原 artifacts/。verification-before-cleanup.md 保留整理开始前的正式验证状态。

旧 workflow、bootstrap、source-verifier 和发布脚本已删除，原内容可从 Git 历史恢复，不再作为另一套开发入口保留。不要把历史 passed、NOT RUN 或部分通过改为新提交结果；新证据保存至当前 CI artifact 或任务明确指定的位置。
