# 第三方组件与分发边界

## 发布包

`./dev.ps1 pack` 生成 Windows x64 自包含运行包：TypeLay 启动器、App、Broker、Python worker 源文件、教程资源、启动脚本及 .NET Core / Windows Desktop 运行时。不分发 .NET SDK、测试、原型、开发工具、Matt skills、Python 解释器、模型权重或用户数据。

运行时版本以 `app/TypeLay.App.runtimeconfig.json` 的 `includedFrameworks` 为准。发布过程从该次构建实际解析的 runtime packs 原样复制包内实际提供的许可证与第三方 notices，按包名和版本保存到 `app/licenses/`。不得删改、翻译或用本文件替代上游声明；更新 SDK 或发布方式后重新检查包内文件并运行发行包测试。

## 首次联网准备

私有 Python、推理依赖及模型由 `src/TypeLay.App/Infrastructure/ModelInstaller.cs` 管理。Python worker 的直接依赖版本见 `src/TypeLay.Worker/requirements.txt`，不得在本文件另建一份易失配的版本清单。

下载的 CPython、wheel、模型各自携带的许可证和 notices 必须保留。Argos 软件的许可证不能自动套用于模型权重或训练数据。引入离线模型包或新增二进制再分发前，逐项审查实际下载内容；本文件不是完整许可证审查结论。

安装器将解析后的依赖来源与摘要写入用户数据目录中的 `dependency-lock.json`。模型索引未提供摘要时，首次下载得到的 SHA-256 仅记录当次内容，不等于上游预先认证的摘要。不要把版本约束或安装后记录描述成已附带所有依赖的离线供应链锁。

## 仅用于开发

`.agents/skills/` 保留 Matt Pocock 上游技能及参考文件，原始许可证位于 `.agents/MATT-LICENSE`；来源 revision 和安装约束见 `docs/agents/skills.md`，安装器记录位于 `skills-lock.json`。这些文件不进入 Windows 运行包。

.NET SDK、xUnit、Ruff、Clang/LLD 用于构建、测试或格式检查，不作为工具链再分发。原生启动器只导入 Windows 系统 API。冻结的产品规格、原型及历史证据位于 `docs/reference/` 与 `docs/history/`，不作为发布包的一部分。

旧源码交付包的声明保存在 `docs/history/third-party-before-cleanup.md`，不得用于判断当前运行包内容。
