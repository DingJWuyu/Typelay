# Development execution

在启动工作区、加依赖或修改 CI 时读取。用户入口与命令参数以 dev.ps1 为唯一实现来源；README 是面向人的概览。

## 准备与执行

在 Windows x64 使用 PowerShell 7.2+、global.json 中的 .NET SDK、Python 3.13 和 LLVM clang/lld-link。
运行 `python -m pip install -r eng/requirements.txt` 安装固定版本的检查工具。不要在仓库或发布包安装模型来跑快速测试。

检查范围匹配实际变更。仅删除未被消费的文档、调整说明/注释或移除对应格式排除项时，核对差异、现役引用和目录即可，不安装模型、不启动桌面、不为此重建发布包。若涉及可执行逻辑、项目依赖、启动或部署清单，则运行对应检查，不能以“清理”为由跳过。

代码变更前运行 `./dev.ps1 check` 获取基线；缺陷修复还须按 tests/AGENTS.md 建立红色回归。代码变更后运行 `./dev.ps1 format`，再运行 `./dev.ps1 check`。发布路径、启动、资源或跨进程变更还须运行 `./dev.ps1 pack`。

`./dev.ps1 shared-check` 可跨平台运行 Core、Runtime、协议和 Python worker 回归，无需 Windows SDK、桌面或模型。只需 PowerShell、global.json 的 .NET SDK 和 Python。共享 Runtime 测试以真实编排配合受控系统端口验证时序；不是 Linux/macOS 客户端，也不能报告为 Windows GUI 验收。单项测试仍可直接 dotnet test 对应 csproj；Python 单项需设置 PYTHONPATH 指向 src/TypeLay.Worker。

新增 NuGet 测试依赖在 Directory.Packages.props 统一固定版本；用 dotnet restore 生成相应 packages.lock.json，不手改锁文件。更新后在 locked mode 验证。Python 开发工具只进 eng/requirements.txt，模型运行依赖只进 src/TypeLay.Worker/requirements.txt，并与 ModelInstaller 的实际安装声明保持一致。

## CI

只保留 .github/workflows/check.yml。它在 windows-preview 的 push/PR 或手动执行时，Windows job 调用 check/pack，Ubuntu job 调用 shared-check；权限为 contents: read。不运行需求拆票、自动修代码、自动提交、自动合并、模型下载或 Linux/macOS 客户端矩阵。

CI 发布的 artifact 以提交 SHA 命名，包含 TRX、当前运行的 ZIP 和校验和。不要上传 artifacts 的整个目录。工作流日志是对应提交的执行记录，不写回源码树。

只有对应输入和平台确实发生变化时才扩展 CI job。真实交互验收使用已解锁 Windows 桌面，不以 hosted runner 的进程测试冒充。

## 结束条件

检查 git diff 中没有用户数据、临时脚本、构建输出或意外生成物。按根 AGENTS 的内容保留规则处理失效文件及其引用，不另写任务归档或清理总结。

查看生产行为变更与结构变更的边界。测试、lint、打包失败必须解决或明确标记未完成。推送前重新读取 origin/windows-preview；只做 fast-forward，不 force push，不改 main。没有通过的工作保留必要证据，禁止用完成声明掩盖未验证的代码。
