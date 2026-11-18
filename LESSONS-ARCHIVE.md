# Lessons archive

## Promoted lessons

### 按首次失败的断言核对基线

现行规范见[测试中的 CI 失败归因](docs/CODING_STANDARDS.md#按首次失败的断言核对基线)。

- **Kind**：judgement。
- **Why**：#112 把单行重置和取消的失败归为早于原多行保存，两者却都经过改动的术语流程。#115 记录了首次 F9 的过期观察，尚未查明拒绝发生在变更的会话控制器创建之前。#120 把 GTK3 预览列为范围外，返工才查清选点与预览使用不同进程。后两例说明，实际执行路径也能提供排除关联的证据。
- **Evidence**：#112 的[负责人审查](https://github.com/DingJWuyu/Typelay/pull/112#pullrequestreview-5949428480)与[受控过期对照](https://github.com/DingJWuyu/Typelay/pull/112#pullrequestreview-5950031890)。#115 的[负责人审查](https://github.com/DingJWuyu/Typelay/pull/115#pullrequestreview-5949674136)与[首次 F9 的归因](https://github.com/DingJWuyu/Typelay/pull/115#pullrequestreview-5950466920)。#120 的[负责人审查](https://github.com/DingJWuyu/Typelay/pull/120#pullrequestreview-5950876576)与[GTK3 执行路径对照](https://github.com/DingJWuyu/Typelay/pull/120#pullrequestreview-5951113760)。
- **Seen**：3 次。#112、#115、#120 各 1 次，均为 2026-11-17。

## Inactive lessons
