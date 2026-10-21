# TSF 只读实验的环境前提

这不是产品后端，也不是应用支持验收。`preflight.py` 只读探测先回答隔离 Windows runner 上已经存在什么，不注册、启用或加载新的 TSF COM 模块，不安装输入法、不改变默认或当前输入 profile、不启动 Notepad、不发送按键、不读输入框正文、不写回。没有现有 Notepad 窗口时记录 `unknown`，不是应用失败。

## 范围与执行前审查

- 本目录测试工具不被生产 solution、App、Broker、安装包或普通 checks 消费；独立 `.github/workflows/tsf-preflight.yml` 使用下面两个互斥的显式诊断级别。
- Python 只调用系统自带 PowerShell 的固定只读查询及 Win32 元数据接口。无需构建、安装或新增依赖。
- 只在经 root/lead 确认的一次性 GitHub-hosted Windows job 上执行。环境标签是必要条件，不是权限证明；workflow 和 runner 生命周期证据必须另外审查。不得在用户电脑或长期 self-hosted runner 上设置标签绕过门槛。
- 当前代码不含注册阶段。注册/辅助类别激活需另审，不能因为此报告有 `present` 而执行。

独立 workflow 的 `observe` job 只监听既有授权分支 `work/linux-usability` 上本目录或该 workflow 的实际变更，且提交末段必须唯一为 `TypeLay-TSF-Preflight: true`。执行前核对事件与 Git HEAD、完整提交正文、非强推有效祖先以及真实 probe 文件或这一个独立 workflow 的差异；独立 workflow 的真实修复只有同次显式标记才可重新采集，无标记或仅其它文件变化均不采集。此入口不修改主 checks；与下一正常能力批同发，不为诊断造空提交。独立并发组不取消正式检查。远端发布及精确 opt-in 仍须 root 审核，由 lead 单写；未来注册不能继承此授权。

诊断命令：

```powershell
python -B tests/windows/tsf_probe/preflight.py --output artifacts/tsf-preflight.json
```

输出父目录由已审 CI 创建；文件使用独占创建，已有证据不覆盖。命令总是返回 2，表示未获下一阶段准入；CI 不能把它翻译成产品通过。系统查询最多 40 秒；独立 job 5 分钟、采集步骤 2 分钟。workflow 只容忍带有效身份与只读计数的 `observed-prerequisites-only` 报告对应的 2；异常或 blocked 不静默放行，始终尝试上传本次单个 JSON。报告记录 SHA/run 身份、OS、语言及已配置输入 profile、Notepad 文件版本/哈希、包版本、已在场 Notepad 的窗口类和线程 HKL、同会话判断、desktop 与 integrity 类别。最多记录 128 个窗口；不读取窗口标题、正文、原始账户 SID/用户名、注册表全树或用户文件。

`present` 仅表示对应静态配置/候选窗口元数据已观察到。进程枚举与窗口枚举之间未持有同一进程身份，PID 可能复用，因此目标身份始终 `targetIdentityVerified=false`，`notepadWindow=unknown`；不把候选元数据认作真实 Notepad 身份证明。callback 异常捕获为固定错误类型，停止枚举并标记 incomplete；达到窗口上限也停止枚举并标记截断。中文 language/profile 配置和 HKL 均不能证明真实中文 IME 已在目标运行，更不能证明 TSF 可读完整正文/选区、能观察组合态与编辑变化。这些项保持 `unknown`。没有注册、激活或新进程启动，因此本阶段没有卸载动作，不能伪造后续 COM 模块的恢复证明。

## 下一阶段停止条件

1. lead/root 先核对此报告及真实隔离契约；缺中文 IME 或目标时只记录缺前提，不自动安装/切换。
2. 下一候选优先已有 TSF manager + 自有 client ID，不新建/注册 TIP profile。先编译与 no-DLL 目标生命周期，再另审只读模块加载；各级不自动串联。无既有 context 则停止，不用 Activate/CreateContext 冒支持。
3. 真应用 + 原中文 IME 共存、完整正文/选区、组合态和变更任一未证明，就不进入写回。自有 fixture 不代替真实应用。

## 验证与代码健康

```sh
python -B -m unittest discover -s tests/windows/tsf_probe -p 'test_*.py' -v
```

回归通过公开入口验证拒绝准入、不做系统观察、保留证据、不覆盖，以及静态配置不能升级运行能力。受控元数据只测试报告规则，不冒充 Windows 实机/真实 IME 证据。Windows 采集必须单独执行并审阅。

本目录 preflight 验证环境前提，native 实验验证独占真实目标的不同级别，两者不互相代替、不扩生产协议、不新增运行时依赖。当前不删除既有安全测试。

## 独立生命周期级别（候选，未执行）

同一 workflow 新增 `lifecycle` job，仅接受唯一终段 `TypeLay-TSF-Lifecycle: true`，不接受旧 Preflight 标记；混合两个标记拒绝。共用 HEAD/祖先/真实路径核验，未复制或放宽准入门。这个级别只复用 runner 已装好的 x64 MSVC 编译测试文件、执行纯协议/结果合同，然后启动本次独占经典 Notepad 并验证正常退出；**不加载 observer DLL、不观察 TSF、不换 IME、不注册或写回**。缺现成 SDK/经典目标即失败，不下载安装或找其它应用补位。

具体源码/对象生命周期与分级命令见 [native/README.md](native/README.md)。此 job 仍需 root 对精确提交/标记的独立发布授权；单次只读 `--single-read` 不存在 workflow 入口，不能继承 lifecycle 成功。现 workflow 全部仍是实验，不是产品验收。
