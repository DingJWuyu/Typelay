# 第三方组件与分发边界

## 发布包

`./dev.ps1 pack` 生成 Windows x64 自包含运行包：TypeLay 启动器、App、Broker、Python worker 源文件、教程资源、启动脚本及 .NET Core / Windows Desktop 运行时。不分发 .NET SDK、测试、原型、开发工具、Matt skills、Python 解释器、模型权重或用户数据。

运行时版本以 `app/TypeLay.App.runtimeconfig.json` 的 `includedFrameworks` 为准。发布过程从该次构建实际解析的 runtime packs 原样复制包内实际提供的许可证与第三方 notices，按包名和版本保存到 `app/licenses/`。不得删改、翻译或用本文件替代上游声明；更新 SDK 或发布方式后重新检查包内文件并运行发行包测试。

## Linux 系统依赖

Linux 自包含发行包包含 .NET 运行时和共享 Python worker 源码；实际运行时包名/版本以 `app/TypeLay.Linux.deps.json` 的 `runtimepack.*` 条目为准。构建按这些条目匹配 SDK 解析的 runtime packs，把原始许可证与第三方 notices 保留在 `app/licenses/<包名>/<版本>/`；缺失或版本不符时打包失败。GTK、PyGObject、Ayatana AppIndicator 及其 D-Bus 菜单依赖由 CachyOS/Arch 的系统包管理器提供，不将系统库复制进发行包。系统依赖的许可证和 notices 随各自软件包维护；Ayatana 的来源为 https://github.com/AyatanaIndicators/libayatana-appindicator 。Linux 桌面的 TypeLay 托盘 SVG 为本项目资源。

## 首次联网准备

私有 Python、推理依赖及模型分别由 Windows 的 `src/windows/TypeLay.App/Infrastructure/ModelInstaller.cs` 与 Linux 的 `src/linux/desktop/setup.py` 管理。Python worker 的直接依赖版本见 `src/shared/TypeLay.Worker/requirements.txt`，不得在本文件另建一份易失配的版本清单。

下载的 CPython、wheel、模型各自携带的许可证和 notices 必须保留。Argos 软件的许可证不能自动套用于模型权重或训练数据。引入离线模型包或新增二进制再分发前，逐项审查实际下载内容；本文件不是完整许可证审查结论。

安装器将解析后的依赖来源与摘要写入用户数据目录中的 `dependency-lock.json`。模型索引未提供摘要时，首次下载得到的 SHA-256 仅记录当次内容，不等于上游预先认证的摘要。不要把版本约束或安装后记录描述成已附带所有依赖的离线供应链锁。

## Index 模型与原生运行时

默认 Index 模型来自官方 [IndexTeam/Index-Translate-2B-GGUF](https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF/tree/3a0eca6ce931d88d00614d427ab3ed8ff0743902)，权重不进入发行 ZIP/tar。安装器只在用户确认后按 `native/index/model.lock.json` 的固定 revision、字节数和 SHA-256 获取 Q4_K_M 权重，连同官方 [Apache-2.0 原文](https://github.com/bilibili/Index-Translate/blob/912e86c8325d6c7c29a6a0b09c5f53482c6602f6/LICENSE) 保存到该模型包。GGUF 官方模型卡与源码许可证均已核验，不沿用 Hy 或 Argos 的许可推断。

发行包的 `worker/native/index` 带独立 Index ABI1 适配器、固定 b11913 (`7aca0d2ea8d92b56911233b822db01f9fbb1ae50`) llama.cpp 官方 CPU 闭包和锁。`LICENSE.llama.cpp` 保留该源码 revision 的 MIT 原文；`LICENSE.Index-Translate` 保留上述 Apache 原文，覆盖适配器内固定的官方模板。CPU 子目录保留官方包实际提供的全部许可证叶文件（Windows 包含 LLVM OpenMP 声明）。构建检查这些文件的字节与 SHA，不以本文替代原文。构建头文件、源码、下载 archive、模型与审计工具不随 runtime payload 分发。

## 仅用于开发

`.agents/skills/` 保留 Matt Pocock 上游技能及参考文件，原始许可证位于 `.agents/MATT-LICENSE`；来源 revision 和安装约束见 `docs/agents/skills.md`，安装器记录位于 `skills-lock.json`。这些文件不进入 Windows 运行包。

.NET SDK、xUnit、Ruff、Clang/LLD 用于构建、测试或格式检查，不作为产品工具链再分发。独立的物理 Tab 验证包包含已有测试输出及其 xUnit/测试依赖，遵循 [该包专属声明](packaging/windows/physical-tab/THIRD_PARTY_NOTICES.md)，不混入产品运行包。原生启动器只导入 Windows 系统 API。产品规格和已确认原型位于 `docs/product/` 与 `docs/design/`，不作为发布包的一部分。
