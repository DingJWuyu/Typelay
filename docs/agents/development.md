# Development execution

在启动工作区、加依赖或修改 CI 时读取。用户入口与命令参数以 dev.ps1 为唯一实现来源；README 是面向人的概览。

## 准备与执行

在 Windows x64 使用 PowerShell 7.2+、global.json 中的 .NET SDK、Python 3.13 和 LLVM clang/lld-link。
运行 `python -m pip install -r eng/requirements.txt` 安装固定版本的检查工具。不要在仓库或发布包安装模型来跑快速测试。

修改前运行 `./dev.ps1 check` 获取当前基线；缺陷修复还须按 tests/AGENTS.md 建立红色回归。修改后运行 `./dev.ps1 format`，再运行 `./dev.ps1 check`。发布路径、启动、资源或跨进程变更还须运行 `./dev.ps1 pack`。

纯 Python 回归可跨平台运行：设置 PYTHONPATH 指向 src/TypeLay.Worker，然后执行 `python -m unittest discover -s tests/worker -v`。Core/Protocol 可单独 dotnet test 对应 csproj。不要把这些命令报告为 Windows GUI 验收。

新增 NuGet 测试依赖在 Directory.Packages.props 统一固定版本；用 dotnet restore 生成相应 packages.lock.json，不手改锁文件。更新后在 locked mode 验证。Python 开发工具只进 eng/requirements.txt，模型运行依赖只进 src/TypeLay.Worker/requirements.txt，并与 ModelInstaller 的实际安装声明保持一致。

## CI

只保留 .github/workflows/check.yml。它在 windows-preview 的 push/PR 或手动执行时调用相同的 check 和 pack 入口；权限为 contents: read。不运行需求拆票、自动修代码、自动提交、自动合并、模型下载或全平台矩阵。

CI 发布的 artifact 以提交 SHA 命名，包含 TRX、当前运行的 ZIP 和校验和。不要上传 artifacts 的整个历史目录。工作流日志是本次执行记录，仓库内的历史记录不是新提交的验收结果。

只有对应输入和平台确实发生变化时才扩展 CI job。真实交互验收使用已解锁 Windows 桌面，不以 hosted runner 的进程测试冒充。

## 结束条件

检查 git diff 中没有用户数据、临时迁移脚本、构建输出或意外生成物。查看生产行为变更与结构变更的边界。测试、lint、打包失败必须解决或明确标记未完成。

推送前重新读取 origin/windows-preview；只做 fast-forward，不 force push，不改 main。没有通过的工作保留证据，禁止用“工程已整理完毕”掩盖未验证的代码。
