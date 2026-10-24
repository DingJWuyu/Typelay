# 隔离物理 Tab 包的第三方组件

本包与产品运行包分开，包含现有测试构建的 xUnit、Microsoft 测试基础设施及其托管依赖；不分发 .NET SDK、.NET Desktop Runtime、Python或模型。系统 .NET 10 Desktop Runtime x64 由使用者预先安装，其许可证随安装提供。

`licenses/packages.lock.json` 是本次测试构建的锁；`licenses/packages/<包名>/<版本>/` 原样保留该锁所有已恢复测试依赖的 nuspec 与实际包内许可证/notices，包含少量只用于构建的依赖元数据，不额外复制其工具或二进制。包名、版本及许可表达式以这些实际文件为准。不能将产品说明中的“xUnit仅用于开发、不再分发”用于本包。

部分 NuGet 包只包含许可表达式而没有完整法律文本。`licenses/upstream/` 补充下列已锁上游提交的原始文件，不翻译或改写。更新测试依赖时重新核对其 repository commit、许可证与 notices，缺件即停止打包。

| 目录 | 锁定来源与文件 |
| --- | --- |
| xunit | [xunit/xunit@728c1dce](https://github.com/xunit/xunit/tree/728c1dce012cd82193035dddfeaba184baaa88c6) 的 LICENSE（包括嵌入代码的 MIT 声明） |
| xunit-visualstudio | [xunit/visualstudio.xunit@50e68bbb](https://github.com/xunit/visualstudio.xunit/tree/50e68bbb8b9ddd4b1bbb95d062b62010caf99909) 的 License.txt |
| xunit-analyzers | [xunit/xunit.analyzers@a2260df3](https://github.com/xunit/xunit.analyzers/tree/a2260df3e96395e6b513e5c7485bd6c53806871e) 的 LICENSE；仅构建依赖元数据 |
| vstest | [microsoft/vstest@490850ae](https://github.com/microsoft/vstest/tree/490850ae3fdc1b470e3804ceab4f6a41cf89ae51) 的 LICENSE、THIRD-PARTY-NOTICES.txt |
| testfx | [microsoft/testfx@cb5afc3b](https://github.com/microsoft/testfx/tree/cb5afc3bb9bb01ebd75b57f89e8358b914ee2a49) 的 LICENSE、THIRD-PARTY-NOTICES.TXT |
| applicationinsights | [microsoft/ApplicationInsights-dotnet@2faa7e8b](https://github.com/microsoft/ApplicationInsights-dotnet/tree/2faa7e8b157a431daa2e71785d68abd5fa817b53) 的 LICENSE |

Apache-2.0.txt 是 [SPDX 的完整 Apache-2.0 文本](https://github.com/spdx/license-list-data/blob/b8d6af45ad2fcfed61bb85a8ad068aa4a77eadf9/text/Apache-2.0.txt) 原件，Git blob SHA 为 `137069b823873b8bcf42979bcf8e9371052d26a2`。NuGet 中 Bcl.AsyncInterfaces、Win32.Registry 和 Newtonsoft.Json 已带原始 LICENSE，连同实际 notices 原样复制。
