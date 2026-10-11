# 本次交付验证状态

日期：2026-09-29。对象：本包的新 Windows MVP M1 工程。

## 结论

**已建立工程并编写第一阶段源码、测试与 fixture；M1 尚未通过验收。** 这不是可安装使用的 MVP，不包含已运行的 WPF 客户端、模型或全局 Hook。

## 本轮实际执行

| 检查 | 结果 | 证明范围 |
|---|---|---|
| 用户提供的五类资料核对 | 已完成 | 规格/报告/源码包/核心 HTML/Tray HTML 均存在；核心 title 实际 v1.3，准确 v1.4 文件未收到 |
| PoC 最终源码指纹计算 | 与报告一致 | 47 个源码文件，`4b3123...f6d962`；完整结果见 `evidence/poc-source-identity.json` |
| PoC 独立 PACKAGE_MANIFEST 校验 | 有一项不一致 | `typelay/ui.py` 失败；没有修改旧源码或补签旧结论 |
| 新工程 XML/JSON 配置与项目路径检查 | 通过 | 5 个项目路径/引用存在，无第三方 PackageReference |
| 原始参考文件副本 SHA-256 | 通过 | 4 个参考文件字节未变，包含严格“部分通过”的原报告 |
| 有限源码字符串检查 | 通过 | 未发现所列剪贴板/HTTP/键盘注入调用模式；**不证明完整隐私或无网络** |
| 浏览器 fixture JS 语法 | 通过 | 实际运行 `node --check`；不证明 DOM 交互或浏览器兼容 |
| `tools/verify-source.py` | 通过 | 实际输出保留在 `evidence/source-audit.json`；仅静态/包装检查 |

## 本轮没有执行的项目

| 项目 | 状态与原因 |
|---|---|
| .NET / C# 构建 | **not-run**：Linux 环境没有 dotnet / C# 编译器；获取 SDK 受到网络限制 |
| C# Core runner | **not-run**：没有 .NET runtime/SDK，源码测试不能视为通过 |
| Windows 原生 runner | **not-run**：没有 Windows 交互桌面 |
| WPF 实际编辑器 / 焦点进入与回源 | **not-run**：正式 UI 尚未实现 |
| 真实物理 Tab / Esc | **not-run**：没有真实 Windows 键盘链路，Hook 尚未实现 |
| 真实微软拼音 | **not-run**：没有 Windows IME 环境 |
| Edge / Chromium UIA | **not-run**：只有 fixture 页面，新的 browser provider 尚未实现 |
| 真实本地模型 | **not-run**：worker 和模型尚未接入 |
| GitHub CI | **not-run**：仅写入 workflow 文件，没有推送或触发远程运行 |
| 安装器、Tray、Settings、多屏/DPI/滚动 | **not-run**：尚未实现完整客户端 |

代码可能仍有编译或运行缺陷。不能把 Python 的源码检查、JS 语法检查、旧 PoC 的 13/13 或 98/98，替代新 C# 测试结果。

## 首次 Windows 验收入口

从普通用户、未锁屏的 Windows x64 桌面运行：

```powershell
dotnet --info
dotnet build TypeLay.slnx -c Release
dotnet tests/TypeLay.Core.Tests/bin/Release/net10.0/TypeLay.Core.Tests.dll
dotnet tests/TypeLay.Windows.Tests/bin/Release/net10.0/TypeLay.Windows.Tests.dll fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll
```

统一取证可以运行 `tools/validate.ps1 -Native`，生成新的 run_id、源码执行前后摘要、退出码与日志。只有这些真实运行完成，才能把对应条目从 not-run 改为 passed/failed。源代码变化后新建验收记录，不覆盖本次环境事实。
