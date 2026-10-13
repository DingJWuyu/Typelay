# TypeLay

TypeLay 是 Windows/Linux 的本地输入翻译工具。自动模式跟随当前文本输入框显示译文，支持修改候选，并在满足安全条件时用 Tab 接受；按需模式由快捷键唤醒翻译 UI，或执行一次性直接翻译并替换。两端均处于开发预览，尚未完成通用输入支持与完整产品验收。

Windows 的使用入口见下文；CachyOS + niri/Wayland 的构建、安装、能力缺口与验收步骤见 [Linux 客户端说明](docs/linux.md)。[Windows 待办](docs/windows-open-issues.md)保留当前问题和复现边界；按约定先完成 Linux 主线与 trunk 整理，通知用户检查，收到回复后再开始 Windows 修复。

**重要限制：Windows 原有实验性 Tab 写回默认关闭。** Windows 通用 UI Automation 的读取、校验和写入不能组成原子操作，仍有覆盖并发输入的风险。测试实验写回时，只使用已备份的非敏感文本。直接替换与 Linux 合作输入路径各自依赖经过校验的目标能力，不通过开启开关扩大兼容性。编译和自动化测试通过不等于真实中文输入法、物理 Tab 或所有应用都已验收。

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

长期集成入口是 `trunk`，稳定发布保留在 `main`。临时分支与现有 preview 的规则见根 [AGENTS.md](AGENTS.md)。开发依赖、命令、变更检查与 CI 流程统一见 [开发流程](docs/agents/development.md)；Linux 平台的构建和部署见 [Linux 说明](docs/linux.md#构建)。

Windows 快速启动（先满足开发流程中的工具要求）：

```powershell
git clone --branch trunk https://github.com/DingJWuyu/Typelay.git
cd Typelay
python -m pip install -r eng/requirements.txt
./dev.ps1 check
./dev.ps1 run
```

`run` 构建并启动真实客户端。受控测试、真实桌面验收与证据边界见 [测试协议](docs/agents/testing.md)。

## 跨平台架构

Windows 与 Linux 保留独立宿主，共用一份 Core、Runtime、Protocol 和 Python worker。Windows 负责 WPF/UIA/Hook/原生 Edit；Linux 负责 GTK/Qt 合作输入、浏览器适配、niri 集成与 XDG 安装。VS Code 仅有独立手动命令，尚未交付 macOS。

平台后端拥有本机控件、IME、焦点、按键及写入证据，共享层拥有产品规则和会话编排。具体模块归属与安全不变量见 [架构约束](docs/agents/architecture.md)，小接口和依赖方向的决定见 [ADR-0003](docs/adr/0003-cross-platform-runtime.md)。

## 源码导航

```text
src/TypeLay.{Core,Runtime,Protocol,Worker}/  单份共享规则、编排、协议与模型进程
src/TypeLay.{App,Broker,Windows.Contracts}/ Windows 宿主、UIA 与私有协议
src/TypeLay.{Launcher,NativeInput}/        Windows 原生入口与受限输入事务
src/TypeLay.Linux/                        Linux 宿主及 desktop/browser/vscode
src/TypeLay.Linux/native/                 gtk3/gtk4/qt6、common 与 fcitx5 合作接口
eng/                                     共享开发依赖、Windows/Linux 构建入口
eng/linux/cooperative/                   Linux 合作组件构建、固定输入与补丁
packaging/{windows,linux}/               运行时启动、安装与发行包装配
tests/{behavior,contracts,worker}/       共享逻辑/worker 与明确标识的 Windows 契约
tests/{e2e,fixtures,support,linux}/        分发/平台回归及受控测试工具
assets/tutorial/                        随产品分发的本地实操教程
docs/agents/                            工程约束与验证流程
docs/adr/                               仍有效的架构决策
docs/reference/                         冻结规格与已确认 UI 原型
docs/{linux,qt-linux,fcitx-linux,...}.md   各现役平台路径的部署、能力与限制
.agents/skills/                         固定版本的 Matt Pocock skills
```

不创建空的 benchmarks 或 examples：当前没有通过验收的性能基准；真实推理和 UI 延迟需要在明确的设备、模型与操作序列下测量，不能用假模型报告性能。复杂用例目前由产品教程承担。

## 维护约定

仓库根目录 `AGENTS.md` 是 Agent 入口，按当前任务继续读取其中指向的文档。仓库仅保留有实际用途的内容，历史追溯使用 Git，不另存旧报告或归档副本。

Matt Pocock skills 保留上游的 user-invoke / model-invoke 分类；没有用户请求时，不应自行启动需求问卷、拆票、PR 或大规模子代理流程。分支直接提交约定优先于默认 PR 工作流。

许可证及第三方组件说明见 `THIRD_PARTY_NOTICES.md`。当前开放的 B-01/B-02/B-03 风险及关闭条件见 [架构约束](docs/agents/architecture.md#开放风险与安全边界)。
