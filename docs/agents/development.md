# Development execution

在启动工作区、加依赖或修改 CI 时读取。Windows 与共享检查以 `dev.ps1` 为实现来源，Linux 构建以 `eng/build-linux.py` 为准；README 是面向人的概览。

## 准备与执行

在 Windows x64 使用 PowerShell 7.2+、global.json 中的 .NET SDK、Python 3.13 和 LLVM clang/lld-link。
运行 `python -m pip install -r eng/requirements.txt` 安装固定版本的检查工具。不要在仓库或发布包安装模型来跑快速测试。

检查范围匹配实际变更。仅删除未被消费的文档、调整说明/注释或移除对应格式排除项时，核对差异、现役引用和目录即可，不安装模型、不启动桌面、不为此重建发布包。若涉及可执行逻辑、项目依赖、启动或部署清单，则运行对应检查，不能以“清理”为由跳过。

Windows 代码变更前运行 `./dev.ps1 check` 获取基线；缺陷修复还须按 tests/AGENTS.md 建立红色回归。代码变更后运行 `./dev.ps1 format`，再运行 `./dev.ps1 check`。发布路径、启动、资源或跨进程变更还须运行 `./dev.ps1 pack`。

`./dev.ps1 shared-check` 可跨平台运行 Core、Runtime、协议和 Python worker 回归，无需 Windows SDK、桌面或模型。只需 PowerShell、global.json 的 .NET SDK 和 Python。共享 Runtime 测试以真实编排配合受控系统端口验证时序；不是 Linux/macOS 客户端，也不能报告为 Windows GUI 验收。单项测试仍可直接 dotnet test 对应 csproj；Python 单项需设置 PYTHONPATH 指向 src/TypeLay.Worker。

Linux 构建、运行依赖及发行包回归命令见 `docs/linux.md`。修改安装/启动流程时，用实际 archive 驱动 `tests/linux/distribution_e2e.py`；修改原生输入或界面时，分别运行嵌套原生测试与生产桌面测试。后者只使用明确准备的隔离组件和真实模型；快速检查不下载模型。Python 修改后运行 Ruff check/format，Linux 原生构建与发行验证不能用 Windows check 替代。

新增 NuGet 测试依赖在 Directory.Packages.props 统一固定版本；用 dotnet restore 生成相应 packages.lock.json，不手改锁文件。更新后在 locked mode 验证。Python 开发工具只进 eng/requirements.txt，模型运行依赖只进 src/TypeLay.Worker/requirements.txt，并与 ModelInstaller 的实际安装声明保持一致。

## CI

只保留 `.github/workflows/check.yml`。它在两个 preview 分支的 push/PR 或手动执行时运行共享、Windows 和 Linux 检查（包括托盘回归）；具体命令与平台以工作流为准。Linux 的模型准备步骤在独立测试数据目录显式安装并验证真实组件，随后运行生产桌面测试；不把下载放进快速测试。不运行自动修代码、自动提交、自动合并或开发环境归档运输；权限保持 `contents: read`。

CI 发布的 artifact 以提交 SHA 命名，包含对应平台的测试结果、当前运行的发行包和校验和。不要上传 artifacts 的整个目录。工作流日志是对应提交的执行记录，不写回源码树。

只有对应输入和平台确实发生变化时才扩展 CI job。真实交互验收使用目标平台的物理桌面，不以 hosted runner 的进程测试或嵌套合成器冒充。

## 结束条件

检查 git diff 中没有用户数据、临时脚本、构建输出或意外生成物。按根 AGENTS 的内容保留规则处理失效文件及其引用，不另写任务归档或清理总结。

查看生产行为变更与结构变更的边界。测试、lint、打包失败必须解决或明确标记未完成。推送前重新读取根 AGENTS 指定的目标分支；Linux 工作只更新 `cachyos-preview`。只做 fast-forward，不 force push，不改 main。没有通过的工作保留必要证据，禁止用完成声明掩盖未验证的代码。
