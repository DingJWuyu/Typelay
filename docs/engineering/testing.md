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

显式 `--include-holdout` 在原四句后追加 `styled_quality_holdout_inputs.json` 的四句，方向及 Natural/Formal 顺序相同，共16次请求。独立样本在候选提示修改前冻结，SHA-256 为 `f837e7a5983bc2303c3c279052cc05166ba2cfe05d6dccd0f364fe15896d3616`；原四句与原 SHA 不变，报告另记 `holdoutInputSha256`。新增样本覆盖不同实体、条件、否定、数字、主体与承诺；没有旧版实测基线，不作为旧版到候选的成对改善证据。该标志不改变单进程、600秒总预算或逐请求/清理限制，不保证所有请求能在时限内完成。

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

`--validate-only --output artifacts/hy-styled-quality/inputs-check.json` 只验证固定输入、参数与源码身份记录，不查看安装、不加载 native、不启动 worker；可同时传 `--include-holdout` 检查16请求计划。生成 `status=not-run`、`quality=not-run`、`workerStarts=0` JSON 并有意返回2。输出禁止覆盖。正式采集记录完整原源、Natural/Formal原始响应、hello、输入/源码/库存/adapter清单身份、固定预算、逐请求状态/code、worker及端到端耗时、失败阶段与退出证明；未暴露的 native token 指标不伪造。所有逐输出及成对人工复核字段保持 null；`completed` 仅代表所选8或16请求采集及清理完成，质量仍为 `not-reviewed`，不是质量 pass。失败保留已完成响应，未确认退出不能完成。

[候选实际采集](https://github.com/DingJWuyu/Typelay/actions/runs/39439291108)已在 `b114469b` 完成原四句及预先冻结四句的16个 ABI5 响应，原始质量字段保持 `not-reviewed`。发布 adapter 的 SHA-256 与报告清单一致，二进制含本次官方风格骨架和保义约束。逐句审阅仍发现自然中译日否定翻转与承诺增强，3/8 对全文相同，不能据此开放风格门；具体结论见[共同产品缺口](../product/README.md#当前共同开发缺口)。[原提示基线](https://github.com/DingJWuyu/Typelay/actions/runs/39415329820)仅有原四句8响应，新增样本没有旧版对照；输出变化不能单独建立提示格式与质量的因果。后续须有明确实现假设再做对照，不从官方指导或软件合同推断质量。

## Hy 术语窄采集准备

`tests/shared/worker/glossary_quality.py` 复用上节 runner 的正式库存/发行包/ABI5 核验、单进程所有权、精确 hello 与原资源/时间预算；仅要求 active 方向包含 zh_fr、fr_zh，hello 另须精确整数 `glossaryTranslationVersion=1`。固定 `glossary_quality_inputs.json` 的 SHA-256 为 `4330b0d550f16080b577561ef871077253d9d3b9a661ae78faceb36482a3e6b9`。五个合成用例依次覆盖有向规则优先于全局、术语伴随数字/否定、CRLF/URL/提及/编号/emoji保护、修改保护内容的明确拒绝，以及风格与术语组合的明确拒绝。最多五次请求，不扩方向矩阵、不改原风格或 Argos 语料。

```sh
python tests/shared/worker/glossary_quality.py --validate-only \
  --output artifacts/hy-glossary-quality/inputs-check.json
```

[当前实际采集](https://github.com/DingJWuyu/Typelay/actions/runs/39439291108)已完成五个响应及退出，包含三个符合局部语义锚点的翻译结果与两个预期拒绝；原始质量字段仍为 `not-reviewed`，该小样本不构成术语能力放行。`--validate-only` 不查看安装、不加载 native 或模型、不启动 worker，写出 `status=not-run`、`quality=not-run`、`workerStarts=0` 并有意返回2。经授权实际采集时使用上节相同来源的 `--python`、`--models`、`--worker` 参数；不下载、不安装、不改库存、不接自动 CI。预算保持2.5 GiB地址空间、单模型、2线程、greedy、context1024、192新token、worker每请求60秒、收帧65秒、加载/hello90秒、总计600秒及另计最多7秒清理。

报告保留完整原始请求（原源、术语、方向及组合字段）、每次完整响应、身份/hello、耗时、拒绝code和退出证明，输出文件禁止覆盖。预期拒绝必须为空text并匹配明确code；意外结果保留后停止，不重试、换题、回退或硬替换输出。`completed` 只表示固定响应采集及清理完成，质量仍为 `not-reviewed`；术语、专属优先、结构、拒绝合理性、原义/数字/否定等人工复核字段始终初始化为null，不能把术语出现或协议拒绝当成质量通过。准备回归仅检查schema、无资产的validate-only及禁止覆盖，不证明真实ABI5推理、资源峰值、语言质量或用户界面可用。

## Linux 模型包生命周期中的显式质量采集

`tests/linux/native_e2e.py --desktop --desktop-scope model-packages --collect-translation-quality` 是显式入口。仅此 scope 接受采集标志；不传标志时，原240秒生命周期、260秒外层和原安装/切换/移除链不变。需先完成正式 Argos 双向安装，并有同期含 ABI5 的发行包、已验证的 Hy 模型/许可证缓存及现嵌套桌面依赖。数据和缓存都必须是本 checkout 的 `artifacts/` 下已存在、当前用户拥有的隔离目录，不能指向用户数据或缓存；输出目录必须全新，已有质量 JSON 不覆盖。入口本身不准备模型、复制权重或制造库存/Ready。

现有 `check.yml` 的手动 `workflow_dispatch` 提供默认 `full` 的 `check_mode`，并可明确选择默认 false 的 `collect_translation_quality`。只有该输入为 true，才将现唯一 Linux 模型包生命周期调用切换为上述采集模式，输出到全新 `artifacts/linux/quality-capture`；不在成功移除 Hy 及缓存之后重复执行。`check_mode=full` 保持共享、Windows 与 Linux 原检查选择；只设置 `collect_translation_quality=true` 仍运行原完整检查。push、PR 及默认手动执行均不采集。仅显式运行的 Linux job 为70分钟、该步骤为36分钟；原自动路径仍为45分钟、5分钟。采集前 admission 保留原磁盘检查，并要求至少2760秒：缓存恢复120秒、准备180秒、缓存保存120秒、采集及退出2091秒、步骤启动/收尾69秒、artifact余量180秒，不再叠加原5分钟生命周期。未准入即停止，不开始缓存准备。

明确选择 `check_mode=quality-only` 且 `collect_translation_quality=true` 才启用非验收采集；未同时确认采集或未知模式直接失败，不默认为缩减检查。该模式保留共享检查、同一 SHA 的 Linux 构建与完整包组装、原生 ABI/manifest/模型固定锁验证、正式 Argos 安装、GTK 运行基础及原 Hy 安装/切换/退出与53响应采集。包组装依赖的 Fcitx/Qt 构建探针仍运行；仅跳过 Windows 和无关浏览器、托盘、教程、GTK/Qt真实模型及 VS Code 交互场景。不复制模型或制造库存，不增加旁路质量执行器；名称与摘要明确标注不构成完整产品验收。该显式模式不改变 push/PR 的分流和失败封闭规则。

默认53次采集没有手动 dispatch 入口时，可在已授权的 `work/linux-usability` 新提交的纯文本正文末尾，以空行分隔的最后一段仅写 `TypeLay-Quality-Capture: true`，明确请求同一次完整检查附带采集。仅已存在分支的非强推、有效祖先到新 HEAD、push 的 `after` 与 HEAD 相同，且事件 `head_commit` 的 ID 和完整正文与 Git HEAD 一致时生效。它强制保留共享、Windows 和 Linux 完整检查，不启用 Qt/练习部分验收，也不是 quality-only。普通提交、旧历史中的标记、正文中途/标题/引用/缩进文本、含反引号或波浪线代码围栏的正文、重复标记、PR、其它分支及证据不全均不启用采集；未验证的显式请求仍保留完整检查。采集布尔值由同一个 scope 输出驱动原有预算、无字节码准备及正式生命周期入口，不新增模型或另一套 runner。只为已有明确实验假设添加标记；后续普通提交不会继承采集请求。仍须等待同分支上一轮完成，避免并发组取消有价值的验证。

```sh
# 明确的非验收采集；不会修改默认 full 模式。
gh workflow run check.yml --repo DingJWuyu/Typelay --ref work/linux-usability \
  -f check_mode=quality-only -f collect_translation_quality=true
```

显式采集的 admission、缓存准备和隔离 inside Python 均禁止生成字节码，避免这些只读导入在发行 worker 中添加 `__pycache__`；隔离进程使用 `-B`，因为 `-I` 会忽略 Python 环境选项。已有字节码仍被 runner 的精确目录身份核验拒绝，不清理后假称原包未变。上传只增加采集输出目录顶层 JSON、日志和截图，不包含模型、runtime 或失败 HOME；完整原始合成响应保留 `quality=not-reviewed`，不构成语言质量通过。

```sh
export XDG_DATA_HOME="$PWD/artifacts/linux/desktop-data"
export XDG_CACHE_HOME="$PWD/artifacts/linux/hy-model-cache"
python tests/linux/native_e2e.py --desktop --desktop-scope model-packages \
  --collect-translation-quality --package artifacts/linux/package \
  --native artifacts/linux/native --output artifacts/linux/quality-capture
```

原宿主本轮实际加载 Argos 双向并 Ready 后，先正常退出宿主，核对原 desktop、Host 与 worker 的 PID/starttime 均退休及两个私有 socket 已移除，再执行现 `alternative_quality.py` 的16句/32请求。采集器继续唯一拥有自己的进程组及原 SIG/EOF/TERM/KILL 清理；退出证实后，以未改的库存、设置及精确同一安装代重启真实宿主，再次等待实际 hello Ready。随后沿原 GTK Install Hy 流程，Hy Ready 后同样停服，串行执行 `styled_quality.py --include-holdout` 的16请求和 `glossary_quality.py` 的5请求，每个退出已证实才进入下一个。再次同代重启后，原单句中译法、Select、回切及 Remove 断言全部保留。重启只接受初始化 `idle` 及精确同代；正常包事务仍要求迁代，未放宽原断言。

采集期间仅锁住该自有 session、安装器与已实际服务的精确代的现有生产锁，拒绝 Host/installer 并发，不创建锁标记或遍历未知代。runner 在 fixture 进程中同步执行，有限隔离并恢复其 Python 模块、argv 与导入路径，避免已导入的发行包模块遮蔽当前采集源码；不增加 wrapper 模型所有者，不复制采集逻辑。

每个 runner 保持600秒采集及最多7秒清理，原产品每请求期限和模型资源配置不变。fixture 原240秒只扣除实际采集 wall time；外层从启动起共2061秒工作窗口，另30秒退出余量，总2091秒，后3秒留给 compositor 清理。超过工作截止不再启动或恢复进程。compositor 提前退出或外层中断时，仅向本轮已核 PID/starttime 的 inside 进程发 TERM，等待 runner 原清理和 fixture finally；不能把 compositor 已退出当作 worker 退出证明。任意外部 SIGKILL、无法读取身份或缺少最终报告不构成回收保证。

输出的三个原始 JSON 保留完整原有响应及人工复核字段；`hy-package-result.json` 另记录总 wall time、采集时间、非采集时间、最多53个质量请求及实际记录的请求尝试数（包含失败在途请求，hello 另计；不包含原生命周期单句试译）。完成要求逐份 `workerStarts=1`、正常退出码0、`workerExitConfirmed=true`、`status=completed`、精确原始/holdout输入与库存身份，以及32/16/5请求，不能只以“不超过上限”放行。产生响应后 `quality` 保持 `not-reviewed`，尚未采集则如实为 `not-run`；两者都不是语言质量通过。

失败不重试，不继续恢复、Select 或 Remove。原 JSON、隔离数据和失败 HOME/搬迁包保留；缺失、坏或空的报告及退出未知均失败。outer 只在 fixture finally 后的本轮完成/失败标记、精确 inside 退出及各份退出证据齐全后收束。本入口的受控测试只证明参数、导入隔离、预算、取消/进程清理和失败保留；实际53请求、GTK全链、资源峰值与语言人工复核仍须分别执行，不能由这些测试推断。

### 可选的 Hy 解码对照

手动运行时，可在 `full` 或上述 `quality-only` 与 `collect_translation_quality=true` 之外明确设置 `hy_decode_comparison=true`。没有 dispatch 入口时，可在上述经验证的 push 采集 footer 同一末尾段中，紧接 `TypeLay-Quality-Capture: true` 下一行仅写 `TypeLay-Hy-Decode-Comparison: true`。两行各出现一次且顺序固定；对照标记单独出现、重复、换序、错误值或不在尾段均不启用采集，并保留完整检查。push 对照仍运行全部共享、Windows、Linux 检查，不提供 quality-only 捷径。默认仍采集53次；该选项将风格槽扩为 A/B 各16次，总计69次。它不改变发行模型或生产默认：A 使用同次实际发行 worker 的 greedy，B 在隔离产物目录中复用已验证 CPU 库编译另一个 adapter。两组使用同一安装代、模型、提示和固定样本，但 native/manifest 摘要必须分别记录，不能冒称同一二进制。

B 采用明确的 Transformers v5.6.0 顺序：repetition penalty 1.05、temperature 0.7、top-k 20、top-p 0.6，最后用逐请求重置的固定 seed `20261020` 采样。这只是原产品资源预算下的四参数对照，不是原厂完整运行配置；ctx1024、新生成192 token、CPU2、2.5 GiB及原 EOG/截断拒绝保持不变。

B 构建、真实 ELF/ABI 检查及 A/B 采集共用原风格槽600秒绝对截止，最终退出清理最多7秒，不增加外层预算。A 响应不完整或退出未确认时不开始 B 推理；不重试、不换 seed、不挑结果。分别保存 `styled-quality.json`、`styled-b-quality.json` 与 `hy-decode/build.json`、`build.log`，逐份核对响应和退出证明。采集或单 seed 样本改善都不能自动开放质量门；仍需逐项审阅全部保义锚点、记录退化及不确定性。

## 结果表述

在任务回复或 CI 结果中分别说明构建、行为/协议、分发/进程、模型推理、真实桌面交互与视觉结果，使用 passed/failed/not-run 并明确范围。结果属于执行时的提交，不能外推到其它版本；不新增仓库内验收报告或空白验收模板。

## Linux 设置与教程独立验收

完整检查及 practice 部分验收分别执行 `native_e2e.py --desktop --desktop-scope setup-settings` 和 `--desktop-scope setup-tutorial`，两者都要求原真实模型安装成功，使用独立 config、输出目录及各自隔离 HOME/退出清理。设置失败不阻止教程，教程失败也不抹去设置结果；单个作用域通过不能替代另一项。设置保留360秒原预算与方向安装回归，教程使用240秒桌面预算并核验实际主 host Ready，两者合计最大600秒工作预算及各自原清理余量，不扩大单场景期限。Linux job及质量采集 admission 总预算不变；quality-only 跳过两项，不将其计作通过。
