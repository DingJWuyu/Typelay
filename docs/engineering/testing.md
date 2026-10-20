# 工程验证协议

修改测试、排障或声明验收结果时读本文件和 tests/AGENTS.md。检查范围按[共享开发流程](development.md)选择，产品要求以[产品验收基线](../product/README.md)为准。

## 共享行为与协议

./dev.ps1 shared-check 在普通 .NET 与 Python 环境执行共享 Core、Runtime、worker 帧协议及 Python worker 回归，不运行 WPF、UIA、Hook 或真实模型推理。Windows.Contracts 的可移植契约也由 shared-check 执行，保留非 Windows 覆盖；它仍归 tests/windows/TypeLay.Windows.Contracts.Tests，不能作为共享产品协议。

`tests/shared/TypeLay.Runtime.Tests` 用确定性串行宿主和受控输入/模型端口执行生产控制器，覆盖激活、异步结果隔离、草稿、直接替换和能力拒绝；设置测试使用真实临时文件。它们不证明桌面、真实模型或原子写入。浮层顶部保留简短的会话状态；编辑入口由原译文控件的悬停、键盘焦点和无障碍名称呈现，不增加常驻解释行。完整候选、可用源框及当前编辑确认仍由 Session 原有判定控制，短期接受许可到期不应使编辑入口闪烁；翻译中、部分预览、IME 锁定和回源中保留原状态。Windows 的 OverlayRendering 回归另检查窄宽浮层中的完整文本可见性及已有状态覆盖优先，仍需在 Windows 桌面实际执行。`shared-check` 在非 Windows 环境构建并运行共享层，完整 Windows check/pack 保留。

设置保存回归区分真实持久化与配置变化通知：规范化后的有效值完全相同时仍写入文件并报告写入失败，但不重配会话；覆盖人工草稿、按需激活、练习进度及待派发直接替换。排除列表按原顺序和大小写比较，外部修改数组后再次保存仍应识别变化。普通设置的实际变化与真实焦点离开仍执行原失效规则；这不是跨窗口保留人工稿的承诺。风格使用独立控制器保存入口与通知：真实临时文件回归验证继承/删除覆盖、人工稿保存前拒绝、当前源及能力变化、退休等待失效、同步提交后的通知异常、原旧表单不能覆写，以及原命令应用身份和未知身份全局继承。风格仅重绘而不广播普通 settings/Configure；原自动教程保存与退休回归继续保留。

共享生产规则与协议回归位于 tests/shared/；平台测试分别位于 tests/windows/ 和 tests/linux/。fixtures 保存受控宿主/资源，support 保存本平台多个测试消费的库；不为目录对称建立没有实际消费者的共享 fixture/support。

术语回归使用现公开边界：真实临时settings文件验证损坏新字段不丢隐私排除、原子保存/失败/取消及旧表单拒覆盖；生产SessionController端口验证机器原源重译、迟到结果拒绝、长时间人工编辑保存不毁稿、原人工稿接受、Append冻结新规则与旧prefix、Direct提交前取消/提交中拒存。真实管道peer验证精确hello、专用op、Auto候选方向、无关方向保持普通、非Hy/旧worker拒绝和组合拒绝；worker帧与纯匹配验证冲突/边界/最长命中/保护段/每段缺词及统一deadline。native无模型回归验证结构化ABI边界与旧版本拒绝，不能证明真实tokenization、推理或质量。

## 平台验证入口

- [Windows 测试](../platforms/windows/testing.md)：Windows 构建与桌面回归、Win32 适配器、Hook、真实 Broker、搬迁 ZIP 与物理微软拼音验收。
- [Linux 开发与验证](../platforms/linux/development.md#验证)：实际 tar.gz、CLI、安装生命周期、托盘、嵌套 niri 和目标桌面验收。浏览器、Fcitx、Qt 与 VS Code 的独立用例由各自现役说明维护。

受控端口、构建、进程、嵌套桌面、真实模型与物理桌面是不同层次的证据，不能互相替代。控制器状态与原生焦点是异步观察，断言等待实际用户结果，不以任一状态通知代替另一个进程已经完成操作。

测试失败保留命令、提交 SHA、平台、退出码和最小失败输出。只使用合成测试文本；TRX、临时包、日志与截图进入 artifacts/ 或 CI artifact，不将执行快照、阶段报告提交到源码树。

## Argos 另述真实质量采集

`tests/shared/worker/alternative_quality.py` 属于共享 worker 的人工质量采集，当前只在 Linux 执行；它不在 shared-check 或 CI 自动运行。仅复用正式安装器成功得到的同一代合法 schema1/2 或 schema3 Argos 双向模型和 venv，不下载依赖或权重、不改 installed.json、不手造 Ready，也不接受替代来源。固定输入 `alternative_quality_inputs.json` 的 SHA-256 为 `87452b28c414bc964afbd84542480dd46a65c0b4e15c42ad36422270413bcbe5`；文件只保留 schema 与 samples，16句、方向、顺序和语义锚点保持固定。生产 op 不暴露原始双候选，因此只启动一次生产 worker，完整校验后各方向初始化一次；schema1/2 hello 核对双向方向，并精确核对可选的已验证包身份；schema3 必须核对精确包身份；hello 不推理，每句原普通翻译后请求一次另述，最多32次翻译请求。无重试、重启、改题或挑选。每请求默认90秒、采集总计600秒，退出另有最多7秒的自有进程组清理，清理期间延后处理 Ctrl-C/TERM；失败仍保存已完成结果、失败阶段和进程退出码，未确认退出不算完成。正式 CI 的现有 `Install and verify actual local translation components` 步骤成功后可手动复用以下路径；工作流的显式采集见本页“Linux 模型包生命周期中的显式质量采集”：

```sh
components="$PWD/artifacts/linux/desktop-data/typelay/components/current"
python tests/shared/worker/alternative_quality.py \
  --python "$components/runtime/venv/bin/python" --models "$components/models" \
  --output artifacts/argos-alternative-quality/result.json
```

`--validate-only --output artifacts/argos-alternative-quality/inputs-check.json` 只检查固定输入、参数并生成 not-run JSON，不启动 worker；有意返回2，不能把退出0冒充推理证据。输出禁止覆盖，失败后保留原文件。JSON 分别记录每次请求的方向、状态/code、worker/端到端耗时、原候选和实际筛选后的另述；未暴露的原始 n-best 不伪造。自动比较仅限精确重复、大小写/空白/标点差异与文本不同；`completed` 只表示采集和清理完成，`no-alternative` 不是质量通过。每句的 ordinary/alternative 人工复核字段初始为 null，须对照固定锚点审查否定、数字、主体、时态/条件、语气、增漏义、自然度与严重错义；不得从文本不同推断语义保持。[首次实际采集](https://github.com/DingJWuyu/Typelay/actions/runs/39395814894)已完成全部32响应及退出，逐句审阅发现保义问题，发布门保持关闭；原始输出的 `not-reviewed` 不被改写为自动质量通过，审阅结论见[产品缺口](../product/README.md#当前共同开发缺口)。现有受控端口回归、参数检查均不能替代该复核，也不能证明 UI、人工稿、结构保护或产品另述功能已交付。

## Hy 自然/正式成对质量采集

`tests/shared/worker/styled_quality.py` 是独立于原 Argos 16 句的窄采集入口，不接入 shared-check 或自动 CI。固定 `styled_quality_inputs.json` 的 SHA-256 为 `6d5a77c5369be23cec23e526cb89f102e7c2726e3d84221e48d6572c9b62576c`，只含四个合成日常原句，依次覆盖 zh_ja、ja_zh、zh_fr、fr_zh；每句原源依次请求 Natural、Formal，最多8次翻译，无重试、重启、改题或普通翻译回退。语义锚点用于人工检查否定、主体、数字、状态、时态/条件及承诺强度，不是参考译文或自动评分。

实际执行只接受正式安装器已成功得到的同一代 venv Python 与 models，库存必须是合法 schema3、仅一个 active Hy 包，已选方向须包含这四个方向。`--worker` 指向同期实际发行包的 `worker/run_worker.py`；脚本先逐字节核对入口、Python 模块闭包、requirements/catalog 与模型/CPU固定锁，拒绝额外 Python 包或字节码遮蔽。它不复制资产、安装依赖、下载权重、改库存或制造 Ready。发行 worker 必须自验 runtime-only production-adapter、CPU 库及模型文件并实际加载；真实 hello 必须精确匹配 active 包 id/version/方向及 `styledTranslationVersion=1`。旧源码、旧 ABI adapter、缺失资源或能力均停止，不把旧模型单句结果视为本轮风格证据。

仅启动一个 Linux 独立进程组，地址空间上限2.5 GiB（已有更低限制保持）；实际生产 ABI5 固定单模型、2线程、greedy、context1024、最多192新 token及每请求60秒。外层仅给每次翻译65秒收帧预算，加载与 hello 90秒，总采集600秒含加载；退出另有原有最多7秒的 EOF/TERM/KILL 自有进程组清理，不扩大模型配置。以下路径均须来自已完成的正式安装及同期含 ABI5 native 资产的发行包；脚本不会准备它们：

```sh
components="$PWD/artifacts/linux/desktop-data/typelay/components/current"
bundle="$PWD/artifacts/linux/package"
python tests/shared/worker/styled_quality.py \
  --python "$components/runtime/venv/bin/python" --models "$components/models" \
  --worker "$bundle/worker/run_worker.py" \
  --output artifacts/hy-styled-quality/result.json
```

`--validate-only --output artifacts/hy-styled-quality/inputs-check.json` 只验证固定输入、参数与源码身份记录，不查看安装、不加载 native、不启动 worker；生成 `status=not-run`、`quality=not-run`、`workerStarts=0` JSON 并有意返回2。输出禁止覆盖。正式采集记录完整原源、Natural/Formal原始响应、hello、输入/源码/库存/adapter清单身份、固定预算、逐请求状态/code、worker及端到端耗时、失败阶段与退出证明；未暴露的 native token 指标不伪造。所有逐输出及成对人工复核字段保持 null；`completed` 仅代表8请求采集及清理完成，质量仍为 `not-reviewed`，不是质量 pass。失败保留已完成响应，未确认退出不能完成。

当前实际 ABI5 成对推理、资源峰值、语言质量与人工复核均为 not-run。本入口准备及参数检查不构成任何方向的语言质量验收，也不开放四方向、十二方向或用户界面；原 Frozen 产品规格与 Argos 16句保持不变。

## Hy 术语窄采集准备

`tests/shared/worker/glossary_quality.py` 复用上节 runner 的正式库存/发行包/ABI5 核验、单进程所有权、精确 hello 与原资源/时间预算；仅要求 active 方向包含 zh_fr、fr_zh，hello 另须精确整数 `glossaryTranslationVersion=1`。固定 `glossary_quality_inputs.json` 的 SHA-256 为 `4330b0d550f16080b577561ef871077253d9d3b9a661ae78faceb36482a3e6b9`。五个合成用例依次覆盖有向规则优先于全局、术语伴随数字/否定、CRLF/URL/提及/编号/emoji保护、修改保护内容的明确拒绝，以及风格与术语组合的明确拒绝。最多五次请求，不扩方向矩阵、不改原风格或 Argos 语料。

```sh
python tests/shared/worker/glossary_quality.py --validate-only \
  --output artifacts/hy-glossary-quality/inputs-check.json
```

当前只准备代码与样例，实际采集 not-run。`--validate-only` 不查看安装、不加载 native 或模型、不启动 worker，写出 `status=not-run`、`quality=not-run`、`workerStarts=0` 并有意返回2。以后经授权实际采集时使用上节相同来源的 `--python`、`--models`、`--worker` 参数；不下载、不安装、不改库存、不接自动 CI。预算保持2.5 GiB地址空间、单模型、2线程、greedy、context1024、192新token、worker每请求60秒、收帧65秒、加载/hello90秒、总计600秒及另计最多7秒清理。

报告保留完整原始请求（原源、术语、方向及组合字段）、每次完整响应、身份/hello、耗时、拒绝code和退出证明，输出文件禁止覆盖。预期拒绝必须为空text并匹配明确code；意外结果保留后停止，不重试、换题、回退或硬替换输出。`completed` 只表示固定响应采集及清理完成，质量仍为 `not-reviewed`；术语、专属优先、结构、拒绝合理性、原义/数字/否定等人工复核字段始终初始化为null，不能把术语出现或协议拒绝当成质量通过。准备回归仅检查schema、无资产的validate-only及禁止覆盖，不证明真实ABI5推理、资源峰值、语言质量或用户界面可用。

## Linux 模型包生命周期中的显式质量采集

`tests/linux/native_e2e.py --desktop --desktop-scope model-packages --collect-translation-quality` 是显式入口。仅此 scope 接受采集标志；不传标志时，原240秒生命周期、260秒外层和原安装/切换/移除链不变。需先完成正式 Argos 双向安装，并有同期含 ABI5 的发行包、已验证的 Hy 模型/许可证缓存及现嵌套桌面依赖。数据和缓存都必须是本 checkout 的 `artifacts/` 下已存在、当前用户拥有的隔离目录，不能指向用户数据或缓存；输出目录必须全新，已有质量 JSON 不覆盖。入口本身不准备模型、复制权重或制造库存/Ready。

现有 `check.yml` 的手动 `workflow_dispatch` 可明确选择默认 false 的 `collect_translation_quality`。只有该输入为 true，才将现唯一 Linux 模型包生命周期调用切换为上述采集模式，输出到全新 `artifacts/linux/quality-capture`；不在成功移除 Hy 及缓存之后重复执行。共享、Windows 与 Linux 完整检查的选择不变，push、PR 及默认手动执行均不采集。仅显式运行的 Linux job 为70分钟、该步骤为36分钟；原自动路径仍为45分钟、5分钟。采集前 admission 保留原磁盘检查，并要求至少2760秒：缓存恢复120秒、准备180秒、缓存保存120秒、采集及退出2091秒、步骤启动/收尾69秒、artifact余量180秒，不再叠加原5分钟生命周期。未准入即停止，不开始缓存准备。

显式采集的 admission、缓存准备和隔离 inside Python 均禁止生成字节码，避免这些只读导入在发行 worker 中添加 `__pycache__`；隔离进程使用 `-B`，因为 `-I` 会忽略 Python 环境选项。已有字节码仍被 runner 的精确目录身份核验拒绝，不清理后假称原包未变。上传只增加采集输出目录顶层 JSON、日志和截图，不包含模型、runtime 或失败 HOME；完整原始合成响应保留 `quality=not-reviewed`，不构成语言质量通过。

```sh
export XDG_DATA_HOME="$PWD/artifacts/linux/desktop-data"
export XDG_CACHE_HOME="$PWD/artifacts/linux/hy-model-cache"
python tests/linux/native_e2e.py --desktop --desktop-scope model-packages \
  --collect-translation-quality --package artifacts/linux/package \
  --native artifacts/linux/native --output artifacts/linux/quality-capture
```

原宿主本轮实际加载 Argos 双向并 Ready 后，先正常退出宿主，核对原 desktop、Host 与 worker 的 PID/starttime 均退休及两个私有 socket 已移除，再执行现 `alternative_quality.py` 的16句/32请求。采集器继续唯一拥有自己的进程组及原 SIG/EOF/TERM/KILL 清理；退出证实后，以未改的库存、设置及精确同一安装代重启真实宿主，再次等待实际 hello Ready。随后沿原 GTK Install Hy 流程，Hy Ready 后同样停服，串行执行 `styled_quality.py` 的8请求和 `glossary_quality.py` 的5请求，每个退出已证实才进入下一个。再次同代重启后，原单句中译法、Select、回切及 Remove 断言全部保留。重启只接受初始化 `idle` 及精确同代；正常包事务仍要求迁代，未放宽原断言。

采集期间仅锁住该自有 session、安装器与已实际服务的精确代的现有生产锁，拒绝 Host/installer 并发，不创建锁标记或遍历未知代。runner 在 fixture 进程中同步执行，有限隔离并恢复其 Python 模块、argv 与导入路径，避免已导入的发行包模块遮蔽当前采集源码；不增加 wrapper 模型所有者，不复制采集逻辑。

每个 runner 保持600秒采集及最多7秒清理，原产品每请求期限和模型资源配置不变。fixture 原240秒只扣除实际采集 wall time；外层从启动起共2061秒工作窗口，另30秒退出余量，总2091秒，后3秒留给 compositor 清理。超过工作截止不再启动或恢复进程。compositor 提前退出或外层中断时，仅向本轮已核 PID/starttime 的 inside 进程发 TERM，等待 runner 原清理和 fixture finally；不能把 compositor 已退出当作 worker 退出证明。任意外部 SIGKILL、无法读取身份或缺少最终报告不构成回收保证。

输出的三个原始 JSON 保留完整原有响应及人工复核字段；`hy-package-result.json` 另记录总 wall time、采集时间、非采集时间、最多45个质量请求及实际记录的请求尝试数（包含失败在途请求，hello 另计；不包含原生命周期单句试译）。完成要求逐份 `workerStarts=1`、正常退出码0、`workerExitConfirmed=true`、`status=completed`、精确输入/库存身份，以及32/8/5请求，不能只以“不超过上限”放行。产生响应后 `quality` 保持 `not-reviewed`，尚未采集则如实为 `not-run`；两者都不是语言质量通过。

失败不重试，不继续恢复、Select 或 Remove。原 JSON、隔离数据和失败 HOME/搬迁包保留；缺失、坏或空的报告及退出未知均失败。outer 只在 fixture finally 后的本轮完成/失败标记、精确 inside 退出及各份退出证据齐全后收束。本入口的受控测试只证明参数、导入隔离、预算、取消/进程清理和失败保留；实际45请求、GTK全链、资源峰值与语言人工复核仍须分别执行，不能由这些测试推断。

## 结果表述

在任务回复或 CI 结果中分别说明构建、行为/协议、分发/进程、模型推理、真实桌面交互与视觉结果，使用 passed/failed/not-run 并明确范围。结果属于执行时的提交，不能外推到其它版本；不新增仓库内验收报告或空白验收模板。
