# TypeLay PoC v0.1.1 Windows 收尾验证报告

日期：2026-09-28 至 2026-09-29

## 结论

本轮结论为：**部分通过**。

- 第 1 组修复后的 Windows 原生自动化测试通过。
- 第 2 组真实 Edge 基础流程通过：真实 Argos 译文稳定出现，物理 Tab 成功写回，Submit count 保持 0。
- 第 2 组完整流程未通过：点击译文尝试编辑时，浮层消失，无法完成“第一次 Tab 返回、第二次 Tab 接受”。
- 第 3 组真实中文输入法与浏览器失效保护未执行。

因此不能签署“限定范围的 Windows 核心 PoC 通过”。本轮结论仍是部分通过，并按 PoC 收尾原则停止继续扩展。保留了两项已有真机正面证据的针对性修改：稳定启动 Edge 本地测试页，以及放宽 Chromium UIA 探测和写回的合理时间预算。未能解决浮层编辑问题的实验性修改已撤回。

## 范围与方法

本轮只验证指定的三组收尾项目。先使用 Computer Use 检查原生 Windows 控制能力，再依据 `README.md`、`docs/TEST_PLAN.md`、`docs/NEXT_AGENT_RUN.md` 和 `docs/VERIFIED_STATUS.md` 执行最新版工程提供的取证工具。

项目目录：

```text
D:\Word_PPT_Excel\后端开发\个人知识库\30_Projects(项目)\Typelay\TypeLay-PoC-v0.1.1\typelay-poc
```

自动化回归复用了 v0.1.0 已安装依赖的 Python 解释器，没有复制或移动虚拟环境：

```text
D:\Word_PPT_Excel\后端开发\个人知识库\30_Projects(项目)\Typelay\TypeLay-PoC-v0.1.0\typelay-poc\.venv\Scripts\python.exe
```

执行命令：

```powershell
python tools\collect_evidence.py --gui --native
```

实际命令使用了上述 Python 解释器的绝对路径，并在 v0.1.1 工程根目录运行。

## 环境

- Windows 版本：Windows Home China 23H2，build 22631.6199，AMD64
- Python：3.11.9，64 位
- Tcl/Tk：8.6.12
- uiautomation：2.0.29
- comtypes：1.4.11
- argostranslate：1.9.6
- 已安装 Microsoft Edge：154.0.4258.37
- 用户语言：zh-Hans-CN
- 已登记中文输入法 TIP：`0804:{81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}{FA550B04-5AD7-411F-A5AC-CA038EC515D7}`
- 收尾后的源码 SHA-256：`4b3123feb45e4e64a11a6190a14ec400ccb3c4a9dc0e4289124928f8eef6d962`
- 收尾后的源文件数：47

浏览器基础流程结论来自真实 Edge、真实 Argos 和用户物理 Tab 操作。输入法只通过只读系统信息确认，没有据此推断真实 composition 流程通过。

## Computer Use 原生控制能力检查

最初通过旧的 CUA 入口检查时，没有发现原生 Windows surface。Codex Desktop 完全重启后，`@oai/sky` 原生窗口能力可用，能够枚举并操作 TypeLay、Edge 和原生 fixture 窗口。

浏览器内容读取仍受到安全限制：Computer Use 无法高置信识别 Edge 的本地 `file://` URL，因此不能直接读取或操作页面内容。验证采用以下分工：

1. agent 启动和配置 TypeLay、打开指定本地 fixture、读取 TypeLay 面板和诊断事件。
2. 用户只执行真实鼠标点击、物理 Tab 和观察页面结果。
3. agent 根据现场回执和 TypeLay 诊断记录结果。

Computer Use 注入的 Tab 会被 TypeLay 按设计识别为 injected 并放行，不能冒充物理 Tab 证据。本轮物理 Tab 结果来自用户真实键盘操作。

## 第 1 组：修复后的原生测试

结果：**通过**。

新取证 run：

- run_id：`cfca0b39706c44659b892d18c46c598a`
- 状态：`automated-subset-passed`
- 源码执行期间未变化：`true`
- 取证目录：`evidence/runs/20260928T141236-cfca0b39/`
- ZIP：`evidence/runs/20260928T141236-cfca0b39.zip`

执行结果：

- compileall：通过
- 单元、契约和工具回归：93/93 通过
- doctor：通过
- 合成 GUI：9/9 通过，仅作辅助证据
- Windows 原生 smoke：13/13 产品断言通过

13 条真实 Win32 断言：

1. 原生 fixture 可读。
2. 全文写回并重新读取成功。
3. caret 位于替换末尾。
4. 原生选区偏移可读。
5. 仅替换选区，前后文字保留。
6. 写回不触发提交。
7. 原文变化后以 `source-changed` 拒绝旧写回，且不覆盖新原文。
8. emoji 相邻选区替换后内容和 UTF-16 边界保持正确。
9. 焦点目标变化后以 `target-changed` 拒绝旧写回，两个字段均未被误写。
10. 单行控件拒绝多行输出。
11. 密码字段被阻止且不返回文本。
12. 只读字段被阻止且不返回文本。
13. 全部场景结束后 Submit count 仍为 0。

### 旧版 smoke 缺陷复核

旧版 `windows_smoke.py` 曾因父进程使用不适合跨进程控件的 `SetWindowTextW`，未能把字段恢复为中文，导致 changed-source 场景提前报 `source-not-translatable`。

v0.1.1 本轮证据显示：

- fixture 准备阶段的 `fixture-text-reset-and-reread` 全部通过。
- `changed-source-rejected-without-overwriting` 产品断言通过。
- 原生报告 `status=passed`、`passed=13`、`error=null`。
- 最终阶段为 `last_stage=final-submit-count`，说明脚本没有在 changed-source 准备阶段提前终止。

据此可确认旧版测试脚本问题在本轮实际 Windows 运行中已修复。

## 第 2 组：真实 Edge 完整流程

结果：**部分通过，完整流程阻断**。

被测浏览器为 Microsoft Edge 154.0.4258.37，本地页面为 `fixtures/browser.html`，翻译方向为中译英，模型为本地 Argos。

已通过：

- 点击 TypeLay 的“浏览器测试页”后，能够稳定打开指定本地 fixture，不再复用旧 Edge 会话中的 360 搜索页面。
- A 单行输入框中的中文能够生成真实本地译文。
- 初次实测发现浮层周期性出现和消失，诊断事件显示译文约 156 ms 完成，随后 UIA probe worker 因原有 0.75 秒预算超时。
- 将 Chromium UIA 探测预算调整为 2.5 秒、UIA 写回预算调整为 5 秒，并让接受 permit 覆盖一次探测窗口后，浮层保持稳定。
- 用户使用真实物理 Tab 后，译文成功替换 A 输入框内容。
- 页面 `Submit count` 仍为 0，接受译文没有自动提交表单。

未通过：

- 点击浮层译文后，诊断能记录 `edit-begin`，但浮层随后消失并重新进入翻译流程，无法稳定编辑译文。
- 因此“第一次 Tab 返回源输入框，第二次 Tab 接受修改后的译文”没有走通。
- 针对异步焦点切换和 Tk 宿主 HWND 识别做了两轮限定修复实验，三次真机回归仍失败。相关实验性修改已撤回，没有继续把 PoC 扩展为正式焦点管理实现。

结论：真实浏览器中的基础“中文输入内容 -> 本地译文 -> 物理 Tab 原地写回且不提交”已经成立，但完整的“点击修改译文 -> 返回 -> 再接受”核心交互尚未成立。

## 第 3 组：真实中文输入法与失效保护

结果：**未执行**。

系统登记了 zh-Hans-CN 中文输入法。由于第 2 组已经出现会阻止最终 PoC 通过的核心编辑阻断，本轮按限定收尾原则停止，没有继续要求用户执行 composition、候选窗口和选字提交验证。

以下项目均为 not-run，不得视为通过：

- 拼音组合期间不翻译组合文本、不抢焦点、不干扰候选。
- 选字提交后继续翻译。
- 无有效译文时 Tab 正常交还给应用。
- 原文变化后旧译文不能写回。
- 目标输入框变化后旧译文不能写入错误目标。

其中后两项仅在第 1 组自动化原生 smoke 中获得正面证据，不能外推为真实浏览器和真实 IME 流程已经通过。

## 收尾修改与回归

保留的修改：

- 新增 `typelay/browser_fixture.py`，显式使用 Edge 新窗口打开本地 fixture，避免恢复无关旧标签。
- 调整浏览器 UIA probe、写回和接受 permit 的时间预算，修复已复现的 worker timeout 浮层闪烁。
- 新增浏览器启动器和 UIA 时间预算回归测试。

最终自动回归：

- compileall：通过
- 单元、契约和工具测试：98/98 通过
- 未保留没有解决真机问题的实验性浮层焦点代码

## 证据索引

- `evidence/runs/20260928T141236-cfca0b39/run.json`
- `evidence/runs/20260928T141236-cfca0b39/unit-tests.log`
- `evidence/runs/20260928T141236-cfca0b39/synthetic-gui.json`
- `evidence/runs/20260928T141236-cfca0b39/windows-native.json`
- `evidence/runs/20260928T141236-cfca0b39/windows-native.log`
- `evidence/runs/20260928T141236-cfca0b39/doctor.log`
- `evidence/runs/20260928T141236-cfca0b39/installed-packages.json`
- `evidence/runs/20260928T141236-cfca0b39/ARTIFACTS.sha256`
- `evidence/runs/20260928T141236-cfca0b39.zip`

## 限定范围判断

自动化 Windows 原生读写、安全拒写和不提交路径已经通过。真实 Edge 中译英、稳定浮层、物理 Tab 写回和不提交也已获得正面证据。浮层点击编辑流程仍是核心阻断，真实中文输入法组合态和浏览器失效保护未执行。

最终判断：**部分通过**。基础路线值得继续研究，但本版本不能签署“限定范围的 Windows 核心 PoC 通过”。停止本轮验证，不继续把本 session 扩展为完整产品开发。
