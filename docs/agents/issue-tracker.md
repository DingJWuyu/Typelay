# Issue tracker: GitHub

Repository: DingJWuyu/Typelay. Specs 和 tickets 使用 GitHub Issues；有连接器时使用连接器，否则在 clone 中使用 gh。无 tracker 访问时报告限制，不编造 issue 状态或转而建立平行任务系统。

PRs as a request surface: no。

开发按根 AGENTS 的 windows-preview 直接提交约定执行；技能的默认 PR 步骤不得覆盖此约定。只有用户要求发布规格、拆票或管理任务时才创建/修改 issue，不为代码整理自动生成任务。

常用读取：`gh issue view <number> --json number,title,body,labels,comments`。常用创建：`gh issue create --repo DingJWuyu/Typelay --title ... --body-file ...`。修改/关闭前读取当前状态；任务依赖优先使用 GitHub 原生依赖，能力不足时在 issue 中写明确的 Blocked by 引用。

不要把 PR 编号当成普通 issue 而未确认类型。不要在任务正文或评论里上传原始用户输入、令牌、模型缓存或私有诊断。
