# TypeLay

TypeLay 是 Windows 上的本地翻译浮层：跟随当前文本输入框显示译文，支持修改候选，并在满足安全条件时用 Tab 接受。当前是 Windows 开发预览，不是已完成通用输入支持的稳定版本。

**重要限制：实验写回默认关闭。** 当前 UI Automation 的读取、校验和写入不能组成原子操作，仍有覆盖并发输入的风险。普通使用保留只读预览；测试实验写回时，只使用已备份的非敏感文本。编译和自动化测试通过不等于真实中文输入法、物理 Tab 或所有应用都已验收。

## 使用 Windows 预览包

在本仓库 Actions 中打开 `Windows checks` 的成功运行，下载对应提交的 artifact，取出其中的 `TypeLay-Windows-preview.zip`，完整解压后双击 `TypeLay.exe`。不要单独移动可执行文件。`Start-TypeLay.cmd` 是备用入口。

预览包自带 .NET 运行时，不需要 SDK、Python 开发环境或源码。第一次使用时，在设置中明确同意安装本地翻译组件与模型。模型安装后，推理在本机 CPU 上运行。组件和设置存放在 `%LOCALAPPDATA%\TypeLay`，不会作为源码或发布包提交。托盘菜单提供设置、实操教程与退出入口。

普通输入框是产品目标，不是按应用维护的支持名单。未知输入法状态只允许只读预览；密码、敏感字段、终端和代码编辑器不能通过弱化安全限制来“扩大支持”。

## 从源码开发

长期开发分支是 `windows-preview`；`main` 不参与本阶段开发。

Windows x64 开发环境需要 Git、PowerShell 7.2 或更高版本、`global.json` 指定的 .NET SDK、Python 3.13，以及 LLVM 的 `clang` / `lld-link`。把这些工具加入 PATH。只跑 Python 行为测试不需要安装模型；Core 和 Protocol 的测试也不依赖真实桌面。

```powershell
git clone --branch windows-preview https://github.com/DingJWuyu/Typelay.git
cd Typelay
python -m pip install -r eng/requirements.txt
./dev.ps1 check
./dev.ps1 run
```

`run` 会从源码构建可启动的开发目录，启动真实客户端，而不是演示替身。安装好组件和模型后即可使用该目录继续调试。

| 命令 | 用途 |
| --- | --- |
| `./dev.ps1 check` | 编译、行为与协议回归、Python 测试、lint 和格式检查 |
| `./dev.ps1 format` | 使用统一工具修正格式及可自动修复的 lint |
| `./dev.ps1 pack` | 生成自包含 Windows 包、校验和，并验证迁移目录后的发布包及进程协议 |
| `./dev.ps1 e2e` | 对已生成的发布包重跑进程与分发回归 |
| `./dev.ps1 native-e2e` | 在已解锁桌面运行旧原生 fixture 适配器测试，不代表生产 UIA 验收 |

构建结果仅进入 `artifacts/`；NuGet 包版本集中在 `Directory.Packages.props`。测试使用标准 `dotnet test` 和 Python `unittest`，不再依赖源码关键字检查来证明产品行为。

## 源码导航

```text
src/                  客户端、UIA Broker、纯逻辑核心、协议、worker、原生启动器
assets/tutorial/      随产品分发的本地实操教程
tests/                行为、协议、发布包 E2E，以及明确隔离的 fixtures/support
eng/                  开发工具依赖和启动器编译工具
packaging/windows/    发布包入口，不包含源码构建器
docs/agents/          Agent 按任务读取的工程约束与验证流程
docs/adr/             已作出的工程决策
docs/reference/       冻结的产品规格与原型，保留原始内容
docs/history/         历史报告和日志，不参与当前构建
.agents/skills/       固定版本的 Matt Pocock skills
```

不创建空的 benchmarks 或 examples：当前没有通过验收的性能基准；真实推理和 UI 延迟需要在明确的设备、模型与操作序列下测量，不能用假模型报告性能。复杂用例目前由产品教程承担。

## Agent 工作流

仓库根目录 `AGENTS.md` 是入口，按当前任务继续读取其中指向的文档。Matt Pocock skills 保留上游的 user-invoke / model-invoke 分类；没有用户请求时，不应自行启动需求问卷、拆票、PR 或大规模子代理流程。分支直接提交约定优先于默认 PR 工作流。

许可证及第三方组件说明见 `THIRD_PARTY_NOTICES.md`。历史 PoC 和 Windows 验收中的失败、限制与未测项不会因为这次工程整理而变成通过。
