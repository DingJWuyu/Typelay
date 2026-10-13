# 共享开发流程

开发从 trunk 开始；分支权限与 preview 保留规则见根 AGENTS.md。模块归属见[工程架构](architecture.md)。Windows 实际构建与桌面命令见[Windows 开发](../platforms/windows/development.md)，Linux 依赖、构建与验收命令见[Linux 开发](../platforms/linux/development.md)。

根 dev.ps1 提供用户命令入口；Windows 命令实现在 eng/windows/dev.ps1，共享检查在 eng/shared/check.ps1。Linux 构建入口是 eng/linux/build.py，合作组件生产构建在 eng/linux/cooperative/。CI 复用这些入口，不维护第二套构建语义。

## 共享检查与依赖

`./dev.ps1 shared-check` 可跨平台运行 Core、Runtime、协议和 Python worker 回归，无需 Windows SDK、桌面或模型。只需 PowerShell、global.json 的 .NET SDK 和 Python。它也执行可移植的 Windows.Contracts 契约回归；运行环境可跨平台不改变其 Windows 所有权。共享 Runtime 测试以真实编排配合受控系统端口验证时序；不是 Linux/macOS 客户端，也不能报告为 Windows GUI 验收。单项测试仍可直接 dotnet test 对应 csproj；Python 单项需设置 PYTHONPATH 指向 src/shared/TypeLay.Worker。

运行 python -m pip install -r eng/requirements.txt 安装固定版本的检查工具；快速测试不在仓库或发布包安装模型。

新增 NuGet 测试依赖在 Directory.Packages.props 统一固定版本；用 dotnet restore 生成相应 packages.lock.json，不手改锁文件。更新后在 locked mode 验证。Python 开发工具只进 eng/requirements.txt，模型运行依赖只进 src/shared/TypeLay.Worker/requirements.txt，并与两端安装器的实际安装声明保持一致。

发布包与测试证据进入被忽略的 `artifacts/`，.NET 中间结果留在各项目 `bin/`、`obj/`。测试使用标准 `dotnet test` 和 Python `unittest`，受控桌面工具的边界与操作见 [测试协议](testing.md)。

检查范围匹配实际变更。仅删除未被消费的文档、调整说明/注释或移除对应格式排除项时，核对差异、现役引用和目录即可，不安装模型、不启动桌面、不为此重建发布包。若涉及可执行逻辑、项目依赖、启动或部署清单，则运行对应检查，不能以“清理”为由跳过。

## CI

只保留 .github/workflows/check.yml。它接收 trunk 和现有两个 preview 分支的 push/PR 或手动执行；trunk 执行共享、Windows 和 Linux 完整检查。保留 preview 触发器不授权修改那些分支。具体命令与平台以工作流为准，本地按变更风险选择现役场景命令，见[Linux 开发说明](../platforms/linux/development.md#按变更选择场景)。不自动修代码、提交、合并或归档开发环境；权限保持 contents: read。

CI 发布的 artifact 以提交 SHA 命名，包含对应平台的测试结果、当前运行的发行包和校验和。不要上传 artifacts 的整个目录。工作流日志是对应提交的执行记录，不写回源码树。

只有对应输入和平台确实发生变化时才扩展 CI job。真实交互验收使用目标平台的物理桌面，不以 hosted runner 的进程测试或嵌套合成器冒充。

## 结束条件

检查 git diff 中没有用户数据、临时脚本、构建输出或意外生成物。按根 AGENTS 的内容保留规则处理失效文件及其引用，不另写任务归档或清理总结。

查看生产行为变更与结构变更的边界。测试、lint、打包失败必须解决或明确标记未完成。推送前按根 AGENTS 核对本次授权、目标分支及远端进度。没有通过的工作保留必要证据，禁止用完成声明掩盖未验证的代码。Windows 未结问题和当前修复顺序以 [Windows 待办](../platforms/windows/open-issues.md)为入口。
