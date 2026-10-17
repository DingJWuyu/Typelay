# Windows 开发与分发

共用依赖、变更检查选择与 CI 约定见[共享开发流程](../../engineering/development.md)。本页命令要求 Windows x64，平台机制见[Windows 架构](architecture.md)。

## 准备与执行

在 Windows x64 使用 PowerShell 7.2+、global.json 中的 .NET SDK、Python 3.13 和 LLVM clang/lld-link。
运行 `python -m pip install -r eng/requirements.txt` 安装固定版本的检查工具。不要在仓库或发布包安装模型来跑快速测试。

| 命令 | 用途 |
| --- | --- |
| `./dev.ps1 run` | 构建并启动真实 Windows 开发客户端 |
| `./dev.ps1 check` | Windows 构建、行为/契约、Python、lint 与格式检查 |
| `./dev.ps1 format` | 修正格式及可自动修复的 lint |
| `./dev.ps1 pack` | 自包含 Windows 包、校验和、搬迁分发与进程 E2E |
| `./dev.ps1 e2e` | 对已有发布包重跑分发与进程回归 |
| `./dev.ps1 native-e2e` | 已解锁 Windows 桌面的受控 Win32 适配器契约 |

`check` 的 App 桌面测试需要可交互 Windows，会启动和清理自己的测试控件进程，不下载模型，也不操作用户正在编辑的窗口。`run` 使用真实客户端；需要模型的调试先通过正式安装流程准备组件。

```powershell
git clone --branch trunk https://github.com/DingJWuyu/Typelay.git
cd Typelay
python -m pip install -r eng/requirements.txt
./dev.ps1 check
./dev.ps1 run
```

Windows 代码变更前运行 `./dev.ps1 check` 获取基线；缺陷修复还须按 tests/AGENTS.md 建立红色回归。代码变更后运行 `./dev.ps1 format`，再运行 `./dev.ps1 check`。发布路径、启动、资源或跨进程变更还须运行 `./dev.ps1 pack`。

命令实现位于 eng/windows/dev.ps1；原生入口与受限输入 DLL 分别通过 eng/windows/build-launcher.py、eng/windows/build-native-input.py 构建。根 dev.ps1 保持上述用户命令。

`pack` 包含独立 `TypeLay.BrowserHost` 和明确的 Chrome 扩展运行文件。搬迁分发回归默认只验证其依赖及非法入口拒绝，不替开发者注册扩展。仅在明确独占的 Windows 测试账户设置 `TYPELAY_BROWSER_REGISTRATION_E2E=1`，才执行真实 `register/status/unregister` 回归：起点要求 TypeLay 用户数据目录与 HKCU/HKLM 两视图同名 host 均不存在；冲突直接失败，清理只删除本次精确拥有的注册与文件。CI 的专用 Windows runner 显式设置该开关，不用于用户本机授权。实际 Chrome 与模型验收另有自己的生命周期，不能用注册成功代替。

受控工具、生产桌面及物理输入法验收见[Windows 测试](testing.md)。当前未结问题与修复顺序见 [Windows 待办](open-issues.md)；按共同产品定义并行开发，Windows 桌面与真实输入法证据仍须单独取得。
