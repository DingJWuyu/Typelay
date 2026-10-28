# 共享开发流程

开发从 trunk 开始；分支权限与清理规则见根 AGENTS.md。模块归属见[工程架构](architecture.md)。Windows 实际构建与桌面命令见[Windows 开发](../platforms/windows/development.md)，Linux 依赖、构建与验收命令见[Linux 开发](../platforms/linux/development.md)。

根 dev.ps1 提供用户命令入口；Windows 命令实现在 eng/windows/dev.ps1，共享检查在 eng/shared/check.ps1。Linux 构建入口是 eng/linux/build.py，合作组件生产构建在 eng/linux/cooperative/。CI 复用这些入口，不维护第二套构建语义。

## 共享检查与依赖

`./dev.ps1 shared-check` 可跨平台运行 Core、Runtime、协议和 Python worker 回归，无需 Windows SDK、桌面或模型。只需 PowerShell、global.json 的 .NET SDK 和 Python。它也执行可移植的 Windows.Contracts 契约回归；运行环境可跨平台不改变其 Windows 所有权。共享 Runtime 测试以真实编排配合受控系统端口验证时序；不是 Linux/macOS 客户端，也不能报告为 Windows GUI 验收。单项测试仍可直接 dotnet test 对应 csproj；Python 单项需设置 PYTHONPATH 指向 src/shared/TypeLay.Worker。

运行 python -m pip install -r eng/requirements.txt 安装固定版本的检查工具；快速测试不在仓库或发布包安装模型。

新增 NuGet 测试依赖在 Directory.Packages.props 统一固定版本；用 dotnet restore 生成相应 packages.lock.json，不手改锁文件。更新后在 locked mode 验证。Python 开发工具只进 eng/requirements.txt，模型运行依赖只进 src/shared/TypeLay.Worker/requirements.txt，并与两端安装器的实际安装声明保持一致。

发布包与测试证据进入被忽略的 `artifacts/`，.NET 中间结果留在各项目 `bin/`、`obj/`。测试使用标准 `dotnet test` 和 Python `unittest`，受控桌面工具的边界与操作见 [测试协议](testing.md)。

检查范围匹配实际变更。仅删除未被消费的文档、调整说明/注释或移除对应格式排除项时，核对差异、现役引用和目录即可，不安装模型、不启动桌面、不为此重建发布包。若涉及可执行逻辑、项目依赖、启动或部署清单，则运行对应检查，不能以“清理”为由跳过。

## CI

主产品验收使用 .github/workflows/check.yml，选择入口为 eng/shared/ci_scope.py；trunk、两个 preview、PR 和默认 full 手动执行保留共享、Windows 和 Linux 完整检查；work/linux-usability、work/windows-experience 仅在已有分支的非强推、before/after 均为有效现存提交、after 等于 HEAD 且 before 是其祖先时，按本次 push 所有提交触及的路径选择对应平台，包含后续撤销、合并及重命名前后的路径。旧分支历史不再扩大普通平台检查范围；已验证的普通 Linux push 若仅触及练习窗口 practice.py、practice_window.py 与文档，则标为练习部分验收：保留共享检查、构建、IPC、合作组件验证与封装、真实模型安装及独立设置、教程两条链，跳过无关浏览器、托盘和 GTK/Qt/VS Code 桌面尾段。涉及通用 desktop_e2e.py、输入、私有会话、共享层、工作流或其它路径时不启用该缩减；下述测试源例外以外的共享路径、根配置、工作流、未知路径或证据不足仍检查双端，空推送和空提交也保留双端；满足上述 push 证据且实际触及的所有路径仅限 docs/、README.md、AGENTS.md 或 GLOSSARY.md 时，只运行共享检查。已退休的 work/linux-settings 不再有触发器、partial 输出或首推父提交例外。显式 quality-only 模式仅用于固定质量采集，必须同时确认 collect_translation_quality=true，不构成产品验收；前提与命令见[质量采集协议](testing.md#linux-模型包生命周期中的显式质量采集)。保留 preview 触发器不授权修改那些分支。不自动修代码、提交、合并或归档开发环境；普通任务权限保持 contents: read。Qt 专属临时迭代仍按相对 trunk 的整支历史确认 Qt-only，再核对全部被执行的非 Qt 生产源码与固定提交 d607db1 字节相同，缺对象或比较失败回完整 Linux；额外以 actions: read 读取该提交的已校验发行 artifact。该结果只证明本次 Qt 场景，不是产品全验；artifact 失效或校验不符必须失败，不换用未知版本。selector 的临时 Git 历史回归由 shared-check 执行，也可直接运行 python -m unittest discover -s tests/shared -v。平台场景命令见[Linux 开发说明](../platforms/linux/development.md#按变更选择场景)。

上述有效 push 中，`tests/shared/TypeLay.{Core,Runtime,Protocol}.Tests/` 三个目录根部已有 `*Tests.cs` 的普通修改属于 Windows 检查范围，不属于文档中性路径：单独修改或仅混合 Windows、文档路径时，保留共享检查与 Windows 完整方案测试、格式检查，省去 Linux 产品链。目录不递归；新增、删除、重命名、类型变化及其后续撤销仍检查双端，任一合并父提交都计入，状态查询失败也保留双端。辅助源、fixture/support、worker、项目/锁文件和共享生产源码不适用该例外。

CI 发布的 artifact 以提交 SHA 命名，包含对应平台的测试结果、当前运行的发行包和校验和。不要上传 artifacts 的整个目录。工作流日志是对应提交的执行记录，不写回源码树。

新增的 `index-windows` / `index-linux` 在对应平台被选择时分别检查正式 Index 安装器与宿主 owner；与既有 Hy、输入及 GUI 链独立，不替换它们或放宽预算。Index 模型只下载到临时 runner，不上传 GitHub cache 或 artifact；仅 CPU archive 可缓存，artifact 只列结构化阶段/身份结果，详见[测试边界](testing.md#index-默认切换的验证边界)。

只有对应输入和平台确实发生变化时才扩展 CI job。真实交互验收使用目标平台的物理桌面，不以 hosted runner 的进程测试或嵌套合成器冒充。

## 结束条件

检查 git diff 中没有用户数据、临时脚本、构建输出或意外生成物。按根 AGENTS 的内容保留规则处理失效文件及其引用，不另写任务归档或清理总结。

查看生产行为变更与结构变更的边界。测试、lint、打包失败必须解决或明确标记未完成。推送前按根 AGENTS 核对本次授权、目标分支及远端进度。没有通过的工作保留必要证据，禁止用完成声明掩盖未验证的代码。三条线的当前任务与修复顺序统一维护在[当前开发状态与安排](development-state.md)；Windows 原始问题及关闭约束见 [Windows 待办](../platforms/windows/open-issues.md)。
