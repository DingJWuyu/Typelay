# 第三方组件与分发边界

本交付包包含 TypeLay 源码、原生启动器、测试、原型参考和文档。**未附带 .NET SDK/运行时、CPython、Python wheel、Argos 模型或字体文件。** 客户端首次准备会联网获取以下组件；相应许可证与 notices 应随实际下载的组件保留。此清单记录技术依赖，不代表完整许可证审查或生产分发授权结论。

| 组件 | 当前固定选择 | 来源 / 用途 |
|---|---|---|
| .NET SDK | 10.0.401 | Microsoft releases 元数据；首次私有构建 |
| Windows Desktop .NET | SDK 所对应版本 | WPF/WinForms/UIAutomation；运行时属于 Microsoft 分发 |
| CPython Windows x64 | NuGet `python` 3.13.16 | NuGet 官方 python 包；私有解释器，PSF 及随包第三方 notices |
| CTranslate2 | 4.8.2 | PyPI `ctranslate2`，CPU 推理；上游 MIT 及其二进制第三方 notices |
| SentencePiece | 0.2.2 | PyPI `sentencepiece`，分词；上游 Apache-2.0 |
| Lingua language detector | 2.1.1 | PyPI `lingua-language-detector`，本地语言检测；上游 Apache-2.0 |
| NumPy | 2.3.5 | PyPI `numpy`；上游 BSD 与分发内第三方 notices |
| PyYAML | 6.0.3 | PyPI `PyYAML`；上游 MIT |
| setuptools | 80.9.0 | PyPI `setuptools`；上游 MIT |
| Argos 模型 | translate-zh_en-1_9 / translate-en_zh-1_9 | argos-net.com；权重、训练数据和 Argos 软件本身的许可需要分别核对 |

本轮只查阅公开元数据/文档，未下载并逐一审阅实际二进制包内全部许可证。尤其不能直接将 Argos 软件仓库许可证套用到所有模型/训练数据。后续预编译/离线模型分发须完成相应审查，保留 downloaded artifacts 内完整许可证与 notices。

组件版本在源码中固定；wheel URL 与摘要来自该版本 PyPI 元数据，Python 包 SHA-512 来自 NuGet catalog。所用 Argos 索引未列出模型档案摘要，记录的首装 SHA-256 只说明当次下载内容，并非上游预先认证摘要。`dependency-lock.json` 在成功安装时写入用户数据目录；本 zip 没有声称已经附带预解析二进制供应链锁。

工具链 Clang/LLD 用于本轮原生启动器交叉编译，工具链本身没有再分发。`TypeLay.exe` 只导入 Windows 系统 API。原型参考材料来自用户提供的项目文件，本次未重新授权其用途。

## 官方资料入口

- .NET 下载与 release metadata： https://dotnet.microsoft.com/download/dotnet/10.0 ， https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
- NuGet Python： https://www.nuget.org/packages/python/3.13.16
- NuGet catalog 与内容： https://learn.microsoft.com/nuget/api/catalog-resource ， https://learn.microsoft.com/nuget/api/package-base-address-resource
- CTranslate2： https://opennmt.net/CTranslate2/installation.html ， https://pypi.org/project/ctranslate2/4.8.2/
- SentencePiece： https://pypi.org/project/sentencepiece/0.2.2/
- Lingua： https://pypi.org/project/lingua-language-detector/2.1.1/
- Argos 包索引： https://github.com/argosopentech/argospm-index/blob/main/index.json
- Microsoft VC++ 运行库： https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist

资料查阅日：2026-10-07。URL 不含运行中用户正文。
