# 从 GitHub Actions 读取 Windows 证据

这台 Linux 机器不能驱动 Windows。先按 `docs/agents/issue-tracker.md` 确认仓库为 `DingJWuyu/Typelay`，再查询对应提交的 Actions。优先使用机器提供的 `gh-axi`；标准 `gh` 等效命令如下。

```bash
gh run list --repo DingJWuyu/Typelay --commit "$(git rev-parse HEAD)" \
  --json databaseId,headSha,status,conclusion,url
```

选择实际 `headSha` 匹配的运行，以其返回的数字 ID 设置 `run_id`。然后读取 job 并下载 Windows 原件：

```bash
gh run view "$run_id" --repo DingJWuyu/Typelay \
  --json headSha,status,conclusion,jobs,url
gh run download "$run_id" --repo DingJWuyu/Typelay \
  --dir "artifacts/verification/windows-$run_id"
```

PR 工作流可能运行合成 merge commit。分别记录 PR head、运行的 `headSha` 与 base，不把旧 head、其它分支或整体绿色状态当作当前 Windows 证据。检查 `.github/workflows/check.yml` 中实际 Windows job 的步骤、跳过状态、TRX、截图和日志。`index-windows` 只证明它实际执行的模型生命周期；共享 Windows.Contracts 不证明 WPF、UIA、Hook、物理输入法或桌面视觉。

对应提交没有运行、job 被跳过或 artifact 缺失时，报告“Windows 未验证”并写明原因。用户没有授权时，不为取证触发额外工作流，不更新 `main` 或 `trunk`。

核对默认 Index 浏览器时，读取主 Windows job 的 `index-production/result.json` 和 `browser-cooperation/result.json`。两个现役入口依次消费该 job 一次构建的发行包。核对两份结果的 `testedCommit`、`runId`、`runAttempt` 和 `packageSha256` 相同，再逐项比较共同消费的程序集、worker 和 Index 载荷摘要及固定 profile。生命周期逐项要求通过，并核对 `allOwnedWorkersExited=true` 与 `cleanup=[]`。独立 `index-windows` job 的结果只证明其自己的包，不能代替主 job 的同包结果。

浏览器自身必须满足 `indexPreparation.workerExitConfirmed=true`、`appUsedSameGeneration=true`，且所有场景通过。结合 `index-installed.json`、`index-components.json` 核对正式安装代。检查 `draftObservation` 的第一次回源零写、按键释放和独立接受，要求最终字段精确等于人工稿且仅一次替换。打开四张 `draft-*.png` 检查真实浮层，并逐句核对合成输入与实际译文。自动断言非空不等于基本保义。最后核对三个进程退出标记、`cleanupErrors=[]` 与同次 `consumer-exit.json`，不能用独立模型生命周期替代桌面或清理结果。
