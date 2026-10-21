# TSF 只读实验的环境前提

这不是产品后端，也不是应用支持验收。`preflight.py` 只读探测先回答隔离 Windows runner 上已经存在什么，不注册、启用或加载新的 TSF COM 模块，不安装输入法、不改变默认或当前输入 profile、不启动 Notepad、不发送按键、不读输入框正文、不写回。没有现有 Notepad 窗口时记录 `unknown`，不是应用失败。

## 范围与执行前审查

- 本目录测试工具不被生产 solution、App、Broker、安装包或普通 checks 消费；独立 `.github/workflows/tsf-preflight.yml` 使用下面三个互斥的显式诊断级别。
- Python 只调用系统自带 PowerShell 的固定只读查询及 Win32 元数据接口。无需构建、安装或新增依赖。
- 只在经 root/lead 确认的一次性 GitHub-hosted Windows job 上执行。环境标签是必要条件，不是权限证明；workflow 和 runner 生命周期证据必须另外审查。不得在用户电脑或长期 self-hosted runner 上设置标签绕过门槛。
- 当前代码不含注册阶段。注册/辅助类别激活需另审，不能因为此报告有 `present` 而执行。

独立 workflow 的 `observe` job 只准许既有授权分支 `work/linux-usability` 上本目录或该 workflow 的实际变更，且提交末段必须唯一为 `TypeLay-TSF-Preflight: true`。执行前核对事件与 Git HEAD、完整提交正文、非强推有效祖先以及真实 probe 文件或这一个独立 workflow 的差异；独立 workflow 的真实修复只有同次显式标记才可重新采集，无标记或仅其它文件变化均不采集。此入口不修改主 checks；与下一正常能力批同发，不为诊断造空提交。独立并发组不取消正式检查。远端发布及精确 opt-in 仍须 root 审核，由 lead 单写；未来注册不能继承此授权。

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

## 原生隔离级别（执行须独立批准）

同一 workflow 的 `isolated-native` job 只接受唯一终段 `TypeLay-TSF-Lifecycle: true` 或 `TypeLay-TSF-Read: true`。与 Preflight 三标记严格互斥，混合、未知及无标记不产生执行准入。共用 HEAD/祖先/真实差异门后输出枚举 `level=lifecycle/read`；失败不输出可执行级别。两个末端分别硬编码命令，不由标记拼接程序参数、没有默认 read。

workflow 监听授权分支的 push，但无标记时两个 job 都跳过、不分配 Windows runner。Preflight/Lifecycle 仍要求本目录或该 workflow 的真实变更；Read 可以随任何真实非空 tracked diff 的正常能力批执行，且 before 与 after 必须不同。每批都须明确唯一 Read 末段标记与 root 精确授权，不是自动后台读取；不为触发造探针 touch、空提交或复跑旧代码。

每次是新的 GitHub-hosted Windows VM。Read 的三个 .cpp、两个 .h 与 build_native.py 共六个编译输入的 Git blob 必须逐文件等于已验证生命周期基线 `d83f04a2fead9c3dd2978e6d3ce7f949f2c6329e`，工作树也必须干净；日志列 blob 哈希。复用已装好的 MSVC 编译和纯合同测试，不下载安装或改原观察逻辑。Lifecycle 可编译本批明确审过的新候选，但只能执行纯合同与 no-DLL 启退；两级均保留 source-baseline.json 的当前提交与六个 blob 清单。新 Lifecycle 通过及清单不自动升级 Read 基线；须 root 核验原报告后另审固定基线提交及一次 Read，不能把锁改成 HEAD。

- `lifecycle`：固定 `runner.exe --lifecycle-only <新测试目录> <observer.dll绝对路径>`。只验证独占 Notepad 启退，不加载 observer。
- `read`：先执行只读环境采集。exit 2 只是原协议返回值，必须同时验证 `observed-prerequisites-only`、当前 SHA 与零注册/激活/输入变化/正文读取，才能继续。Notepad 尚未在场与中文 IME 未证明均保留 unknown，不改系统补条件。然后且仅一次 `runner.exe --single-read <新测试目录> <observer.dll绝对路径>`。

Read 只有完整合成正文逐字符匹配、有效 UTF-16 选区范围、一次 callback、正常退出、owned job 归零、合成文件移除且无强杀，才报告单次观察完成。report validator 校验固定字段、类型、范围与不升级权限；不接受额外正文键，不打印失败 payload。任何 exit 2/未知/context 缺失/焦点身份变化/组合中/超时/清理失败均停止，保留原有界 JSON，不重试或自动改模式。数字初始零值不代表有效无组合；必须 `readSnapshotValid=true`，且即便如此 `imeVerified/writebackAllowed/liveUnloadProven` 仍为 false。

该级别最多证明隔离 VM、自有合成文档的一次正文/选区观察与进程级移除。没有中文 IME 不伪装 Clear，更不证明共存、连续变化、运行中卸载或写回。TID 挂 hook 的非原子身份限制见 [native/README.md](native/README.md)，不外推用户应用生产安全。原生说明中的逐级权限规则仍有效；Read 入口存在不等于已获本次发布执行授权。远端精确提交/标记仍由 root 审核、lead 单写，不自动挂正式产品验收。

诊断协议 v2 只增加发送 API 事实、经验证的目标阶段位与只读 mitigation 摘要，见 [native/README.md](native/README.md#发送与入口诊断)。当前 Read 锁定下述 v3 已验生命周期提交；一次实际 Read 仍须本批精确审批，不因基线更新自动执行。查询策略不可信或签名策略不兼容时在 hook 前停止，不修改系统策略。

当前 v3：仅在 Transitory 拒绝前采一次当前栈元数据，见 [native/README.md](native/README.md#单次-transitory-栈元数据v3)。它不读取 base 正文、不申请其它 context 会话，最终仍拒绝读取。v3 已完成真实 Windows 编译、纯合同及 no-DLL Lifecycle，元数据均为未开始；这不证明目标侧采集成功。Read 固定上述 v3 基线，一次栈观察仍待独立执行审批。单层 Transitory 无替代时停止此探针线，不无限加字段或重试。
