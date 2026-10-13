# TypeLay

Linux 开发分支 `cachyos-preview` 的构建、安装、功能缺口与验收步骤见 [Linux 客户端说明](docs/linux.md)。下面保留 Windows 客户端的使用和维护入口。 用户报告的待处理 Windows 问题见[临时待办](docs/windows-open-issues.md)，按约定在 Linux 与 trunk 整理后再开始修复。

TypeLay 是 Windows 上的本地输入翻译工具。自动模式跟随当前文本输入框显示译文，支持修改候选，并在满足安全条件时用 Tab 接受；按需模式由快捷键唤醒翻译 UI，或执行一次性直接翻译并替换。当前是 Windows 开发预览，不是已完成通用输入支持的稳定版本。

**重要限制：原有实验性 Tab 写回默认关闭。** 当前通用 UI Automation 的读取、校验和写入不能组成原子操作，仍有覆盖并发输入的风险。测试实验写回时，只使用已备份的非敏感文本。新增直接替换使用独立的、范围受限的原生控件事务，不通过开启该实验开关来扩大兼容性。编译和自动化测试通过不等于真实中文输入法、物理 Tab 或所有应用都已验收。

## 产品验收入口

两端共同按[冻结规格 v1.0](docs/reference/TypeLay_产品规格_v1.0.md)及其后已接受的[自动/按需与一次性直接替换增量](docs/adr/0002-on-demand-and-direct-write.md)验收。增量规定的双模式和 F9/F10 行为在下方“工作模式与按需操作”说明；与冻结基线重叠时，以该已接受增量为准，其他原要求继续有效。Windows 或 Linux 的当前实现、测试范围和已知缺口都不替代产品要求。

“工作模式”是设置项总称，选项是“自动模式”和“按需模式”。各平台能力和未完项见对应说明，不能把未实现项写成天然平台差异。

## 使用 Windows 预览包

在本仓库 Actions 中打开 `TypeLay checks` 的成功运行，下载对应提交的 artifact，取出其中的 `TypeLay-Windows-preview.zip`，完整解压后双击 `TypeLay.exe`。不要单独移动可执行文件。`Start-TypeLay.cmd` 是备用入口。

预览包自带 .NET 运行时，不需要 SDK、Python 开发环境或源码。第一次使用时，在设置中明确同意安装本地翻译组件与模型。模型安装后，推理在本机 CPU 上运行。组件和设置存放在 `%LOCALAPPDATA%\TypeLay`，不会作为源码或发布包提交。托盘菜单提供设置、实操教程与退出入口。

普通输入框是产品目标，不是按应用维护的支持名单。未知输入法状态只允许只读预览；密码、敏感字段、终端和代码编辑器不能通过弱化安全限制来“扩大支持”。

### 工作模式与按需操作

在“设置 → 常规 → 工作模式”选择模式，重启后保留。升级时没有此偏好的用户仍使用自动模式，已有应用排除项不会因新增偏好失效而被清除。

| 模式或操作 | 行为 |
| --- | --- |
| 自动模式（默认） | 聚焦后按原有规则显示语言胶囊、生成译文与接受译文。两个新增快捷键均不启用。 |
| 按需模式 | 正常聚焦、输入时不显示翻译 UI，也不自动运行翻译。 |
| 唤醒翻译 UI：默认 `Ctrl+Shift+F9` | 为当前输入框开启持续翻译体验；空框显示语言胶囊。正常编辑 TypeLay 译文不算离开；转到其他输入框后结束，返回原框也需要重新唤醒。 |
| 直接翻译并替换：默认 `Ctrl+Shift+F10` | 有选区时只处理选区，否则处理当前输入框文本。一次性操作，不显示预览、不需要 Tab、不发送或提交。 |

两个快捷键可在“设置 → 快捷键”独立修改并同时使用。非法、重复组合不会部分保存；被其他应用占用的组合不会启用，状态显示在设置中。唤醒、直接替换与接受译文均不会更改工作模式。

直接替换绑定触发时的原输入框和文本。翻译期间可切换输入框或应用，任务不会仅因焦点变化取消；结果只尝试写回原控件，不抢回焦点，不修改后来聚焦的输入框。原目标失效、内容冲突、输入法状态不明确或无法确认结果时，不进行有风险的重试。操作状态沿用托盘快速面板反馈。

**直接替换的当前兼容范围是 Windows x64 中通过运行时校验的原生系统 `Edit` 控件。** 需要未被应用替换窗口过程、身份仍有效且能完成原文校验的控件。不支持该事务的浏览器、富文本或自定义控件不会回退到 UIA `SetValue`、模拟按键或抢焦点写回。普通只读预览控件仍可按需唤醒，但这不赋予写回权限。实现依据与边界见 [ADR-0002](docs/adr/0002-on-demand-and-direct-write.md)。

## 从源码开发

长期开发分支是 `windows-preview`；`main` 不参与本阶段开发。

Windows x64 开发环境需要 Git、PowerShell 7.2 或更高版本、`global.json` 指定的 .NET SDK、Python 3.13，以及 LLVM 的 `clang` / `lld-link`。把这些工具加入 PATH。只跑 Python 行为测试不需要安装模型；Core、Runtime 和协议测试也不依赖真实桌面。

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
| `./dev.ps1 check` | 编译、行为与协议回归、Windows 桌面契约、Python 测试、lint 和格式检查 |
| `./dev.ps1 shared-check` | 在非 Windows 环境运行共享核心、会话编排、协议和 worker 回归，不构建桌面客户端 |
| `./dev.ps1 format` | 使用统一工具修正格式及可自动修复的 lint |
| `./dev.ps1 pack` | 生成自包含 Windows 包、校验和，并验证迁移目录后的发布包及进程协议 |
| `./dev.ps1 e2e` | 对已生成的发布包重跑进程与分发回归 |
| `./dev.ps1 native-e2e` | 在已解锁桌面验证受控 Win32 fixture 的适配器契约，不代表生产 UIA 验收 |

发布包与测试报告进入 `artifacts/`；.NET 编译中间结果留在各项目的 `bin/`、`obj/`，这些生成物均不提交到 Git。NuGet 包版本集中在 `Directory.Packages.props`。测试使用标准 `dotnet test` 和 Python `unittest`；受控桌面工具的用途与运行方法见 [测试流程](docs/agents/testing.md)。`check` 中的 App 桌面测试需要可交互的 Windows 桌面，会启动并清理独立的测试控件进程；不会下载模型，也不应用于用户正在编辑的窗口。

## 跨平台架构

Windows 客户端继续保留；`cachyos-preview` 正在开发 CachyOS + niri/Wayland Linux 预览客户端，安装、支持缺口和真实验证要求见 [Linux 客户端说明](docs/linux.md)。尚未交付 macOS 客户端。共享产品逻辑已从 Windows 宿主独立出来：Core 管理会话与安全规则，Runtime 管理激活、翻译/编辑/接受时序、设置持久化和本地 worker 客户端，Protocol 管理通用帧与 worker 消息，Python worker 保留模型校验、结构保护与推理实现。

App 负责 Windows 组装和 WPF 界面；UIA Broker、输入 Hook、快捷键注册、原生 Edit 写入、焦点、Job Object、启动项和发行安装仍由 Windows 实现。Windows.Contracts 的 DTO/句柄编码只属于 Windows App 与 Broker，不要求其它平台采用它。

增加平台时实现实际可提供的输入/视图/生命周期适配器并复用 Runtime。无法确认 IME、写权限或按键所有权时仍拒绝相应行为，不能把“可观察”当作“可替换”。CachyOS + niri/Wayland、其它 Linux 桌面和 macOS 的权限、可访问性、几何、快捷键、输入法和可靠写入需要后续分别验证。设计与具体接入契约见 [ADR-0003](docs/adr/0003-cross-platform-runtime.md)。

## 源码导航

```text
src/                  客户端、UIA Broker、核心、协议、worker、原生启动器与输入事务
assets/tutorial/      随产品分发的本地实操教程
tests/                行为、协议、发布包 E2E，以及明确隔离的 fixtures/support
eng/                  开发工具依赖和原生组件编译工具
packaging/windows/    发布包入口，不包含源码构建器
docs/agents/          Agent 按任务读取的工程约束与验证流程
docs/adr/             当前架构决策及其取舍
docs/reference/       当前产品规格与已确认 UI 原型
.agents/skills/       固定版本的 Matt Pocock skills
```

不创建空的 benchmarks 或 examples：当前没有通过验收的性能基准；真实推理和 UI 延迟需要在明确的设备、模型与操作序列下测量，不能用假模型报告性能。复杂用例目前由产品教程承担。

## 维护约定

仓库根目录 `AGENTS.md` 是 Agent 入口，按当前任务继续读取其中指向的文档。仓库仅保留有实际用途的内容，历史追溯使用 Git，不另存旧报告或归档副本。

Matt Pocock skills 保留上游的 user-invoke / model-invoke 分类；没有用户请求时，不应自行启动需求问卷、拆票、PR 或大规模子代理流程。分支直接提交约定优先于默认 PR 工作流。

许可证及第三方组件说明见 `THIRD_PARTY_NOTICES.md`。当前开放的 B-01/B-02/B-03 风险及关闭条件见 [架构约束](docs/agents/architecture.md#开放风险与安全边界)。
