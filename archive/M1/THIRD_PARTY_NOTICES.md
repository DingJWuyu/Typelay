# 依赖与参考资料说明

本工程起点没有打包 .NET SDK、Windows 系统 DLL、Python runtime、Argos、模型权重或字体，也没有第三方 NuGet PackageReference。

`reference/` 是用户提供规格、PoC 报告和两个 HTML 原型的原样副本；它们用于项目工程交接。本轮复用旧 PoC 的技术知识与用例，没有把旧 Python/Tk 实现复制成新生产模块。

.NET / Windows API / Argos 的官方技术资料来源见 `docs/ENGINEERING_PLAN.md`。后续引入依赖、构建工具、模型或打包内容时必须增加确切版本、校验值和适用许可证清单。本文件不是模型或依赖的许可审查通过证明。

此工程未擅自为用户项目选择开源许可证；对外发布前由项目所有者确定许可与分发政策。
