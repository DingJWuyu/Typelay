# 工程验证协议

修改测试、排障或声明验收结果时读本文件和 tests/AGENTS.md。检查范围按[共享开发流程](development.md)选择，产品要求以[产品验收基线](../product/README.md)为准。

## 修复交接检查清单

每次修复交付（包括子代理交接）须逐项列明：

- 受影响的既有测试、fixture 与消费者路径，以及它们依赖的旧行为假设；新增回归不能代替这项核对。
- 各项保留、更新、替换或退役的处置及理由；退役时说明原有效行为由何处覆盖，或为何已无现役用途。
- 同一可观察边界的修复前后证据、邻近行为验证结果与未测项；明确区分静态审查、受控端口和实际桌面证据。
- 核查本次变更相关的被替代实现、重复逻辑、无用文件、过期文档和依赖，确认目录职责仍清晰；不扩展为无边界重构。
- 确认无现役消费者或实际维护用途的内容及时退役，同步清理引用并纳入本次验证，不以“兼容”为由无限保留死代码；保留有效安全合同与必要验证证据。

保留仍有效的安全拒绝和未知状态检查，不通过删除安全门、放宽断言或忽略失败求绿。清单随交付说明提供，不另建历史报告文件。

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

## Argos 普通翻译单因素调优

本节是下一轮实验合同，不是已执行结果。执行者固定负责 Argos，实际推理与 Hy 顺序使用模型资源；先确认同一正式安装代次可复用，再由集成负责人分配运行槽。当前机器没有模型或 CT2 时只准备采集，不下载以填满磁盘，也不手造安装状态。

**第一轮只回答一个问题：普通翻译的重复惩罚是否损害保义。** 基线 A 为 `repetition_penalty=1.05`，候选 B 为 `1.0`；两者同用当前官方中英双向 1.9 包、固定依赖、CPU INT8、beam2、384 输入 token 拒绝上限、512 解码上限及同一线程数。候选只在隔离测试入口调整该参数，不改生产默认。取消重复惩罚可能改善应当重复的主体或词，也可能增加重复；这是待检验的假设，不是已发现根因。[CT2 官方参数说明](https://opennmt.net/CTranslate2/python/ctranslate2.Translator.html#ctranslate2.Translator.translate_batch)定义 `1.0` 为默认值，`>1` 惩罚已生成 token；实验仍固定仓库的 4.8.2 依赖，不随在线文档版本升级。已失败的 beam4 不作为新实验再计数。

- 固定开发集：复用 `tests/shared/worker/alternative_quality_inputs.json` 的全部 16 句及原 SHA-256，顺序不变，只请求 `translate`，不请求另述。
- 预先冻结留出集：`tests/shared/worker/argos_repetition_holdout.json` 八句在查看本轮输出前确定；不能根据模型输出改题。每句分别检查主体、否定/条件、数值边界和增漏义。留出结果不用于反复调这一轮参数；若后续据此修改方案，它们转为开发样本，并另设新的留出集。
- A、B 各完整 24 个普通请求，共两套配置、48 个响应，不能宣称尝试了 48 套方案。同机顺序 A 后 B，不能拿历史不同机器的耗时作为公平性能对照；一组因错误提前停止仍保存全部已完成输出，不自动重试或换 seed。
- 两组共享 600 秒总推理预算，含加载；每请求最多 90 秒，进程退出沿用每组至多 7 秒清理。A 不完整、身份核验失败、预算不足或退出未确认时不启动 B。禁止与 Hy、Windows GUI 或其他重负载在同一资源槽并跑；记录线程数、CPU、依赖锁、包与 worker/实验入口哈希、每请求原输出/失败/耗时及退出证据。
- **采用条件**：逐句盲化对照复核后，开发集严重错义总数下降，留出集严重错义不增加，两组均没有新增数值/否定/主体错误或结构损坏；不能用自然度收益抵消保义退化。还须在新的留出样本独立复核并检查普通/另述共用消费者，才提出生产改动。小样本通过不是整体产品质量过关，也不开放另述/风格门。
- **停止条件**：结果完全相同、严重错义未减少、出现上述退化，或运行身份/完整性/资源限制不满足时，本轮不采用；记录原因，不继续围绕单句盲扫参数。后续实验须另写有依据的单因素假设。

留出集覆盖中英两个方向的严格时间边界、包含边界人数、否定/条件、重复主体与方向；其 SHA-256 为 `7fb5abd79757e67142a3e3be78f4457ea474550eda317f9bdaea7a379b0a6752`。runner 与隔离 worker 均核对固定文件，未匹配的任意输入在模型调用前拒绝。

```sh
components="$PWD/artifacts/linux/desktop-data/typelay/components/current"
python tests/shared/worker/argos_repetition_comparison.py \
  --python "$components/runtime/venv/bin/python" --models "$components/models" \
  --output artifacts/argos-repetition/result.json
```

`--validate-only --output artifacts/argos-repetition/inputs-check.json` 不读取安装、不导入真实模型、不启动 worker，输出 `not-run` 并返回 2。完整运行输出两组完整原始响应和零初始化的人工复核字段；`completed` 只证明采集完成，不给语义通过结论。输出禁止覆盖。普通另述原入口仍保留原 32 请求，不默认运行本实验；CI 尚未接线。 无模型合同在共享 worker 套件运行；真实自有进程的成功、错误、超时和退出回归位于 `tests/linux/test_argos_repetition_process.py`，由 Linux 构建检查显式执行，不在 Windows 共享发现中运行 Linux 进程组操作。该回归使用受控响应，不是模型采集。

执行渠道优先复用正式 CI 安装后的 `components/current` 中 Argos 双向模型与同代 venv，下节已有 runner 负责库存与进程校验。现有 `--fixed-model-diagnostics` 包含普通与另述，不是上述普通 A/B 入口；不能把该命令当作本轮已接线。当前工作流的 `--cache-only` 只作用于 Hy 缓存准备，Argos 安装步骤仍可能下载官方包；没有可验证的现成安装时，应先明确安装与资源预算，而不是称为“零下载实验”。运行前须先完成该入口的无模型行为回归，再由集成负责人安排独占模型资源槽。

## Argos 另述真实质量采集

`tests/shared/worker/alternative_quality.py` 属于共享 worker 的人工质量采集，当前只在 Linux 执行；它不在 shared-check 或 CI 自动运行。仅复用正式安装器成功得到的同一代合法 schema1/2 或 schema3 Argos 双向模型和 venv，不下载依赖或权重、不改 installed.json、不手造 Ready，也不接受替代来源。固定输入 `alternative_quality_inputs.json` 的 SHA-256 为 `87452b28c414bc964afbd84542480dd46a65c0b4e15c42ad36422270413bcbe5`；文件只保留 schema 与 samples，16句、方向、顺序和语义锚点保持固定。生产 op 不暴露原始双候选，因此只启动一次生产 worker，完整校验后各方向初始化一次；schema1/2 hello 核对双向方向，并精确核对可选的已验证包身份；schema3 必须核对精确包身份；hello 不推理，每句原普通翻译后请求一次另述，最多32次翻译请求。无重试、重启、改题或挑选。每请求默认90秒、采集总计600秒，退出另有最多7秒的自有进程组清理，清理期间延后处理 Ctrl-C/TERM；失败仍保存已完成结果、失败阶段和进程退出码，未确认退出不算完成。正式 CI 的现有 `Install and verify actual local translation components` 步骤成功后可手动复用以下路径；工作流的显式采集见本页“Linux 模型包生命周期中的显式质量采集”：

```sh
components="$PWD/artifacts/linux/desktop-data/typelay/components/current"
python tests/shared/worker/alternative_quality.py \
  --python "$components/runtime/venv/bin/python" --models "$components/models" \
  --output artifacts/argos-alternative-quality/result.json
```

`--validate-only --output artifacts/argos-alternative-quality/inputs-check.json` 只检查固定输入、参数并生成 not-run JSON，不启动 worker；有意返回2，不能把退出0冒充推理证据。输出禁止覆盖，失败后保留原文件。JSON 分别记录每次请求的方向、状态/code、worker/端到端耗时、原候选和实际筛选后的另述；未暴露的原始 n-best 不伪造。自动比较仅限精确重复、大小写/空白/标点差异与文本不同；`completed` 只表示采集和清理完成，`no-alternative` 不是质量通过。每句的 ordinary/alternative 人工复核字段初始为 null，须对照固定锚点审查否定、数字、主体、时态/条件、语气、增漏义、自然度与严重错义；不得从文本不同推断语义保持。[首次实际采集](https://github.com/DingJWuyu/Typelay/actions/runs/39395814894)已完成全部32响应及退出，逐句审阅发现保义问题，发布门保持关闭；原始输出的 `not-reviewed` 不被改写为自动质量通过，审阅结论见[产品缺口](../product/README.md#当前共同开发缺口)。现有受控端口回归、参数检查均不能替代该复核，也不能证明 UI、人工稿、结构保护或产品另述功能已交付。

### Argos 固定合成语料的解码边界诊断

在上述同代正式安装参数上显式添加 `--capture-decoding-boundary`，才由测试目录的 `alternative_boundary_worker.py --fixed-fixture` 代理外部 CT2 API。默认仍启动未插桩的生产 worker；发行 worker 和普通用户路径不增加原文/token日志。诊断入口核验原16句文件SHA，只接受 hello 后固定顺序的32请求，逐字核对原文、方向、字段和上一普通响应；额外、变序或非fixture请求在分词/推理前拒绝。

诊断保持生产编码、解码参数、候选顺序和筛选不变，CT2返回对象原样交回生产adapter；不增加候选、评分请求、重试或模型。报告以 instrumented 方法和 observer源码SHA明确区别，`requests[].decodingBoundary` 保存实际source pieces、调用参数、原始hypotheses及仅join/空格转换后的文本；对照同请求最终text/status判断转换差异，不把候选存在或文本不同当成保义。hello附带已验证包的metadata哈希、方向/版本、target_prefix是否存在及原值、SentencePiece哈希，库存验证与原运行库来源不变。

仍为一个worker、最多32翻译、原90/600秒预算及7秒退出清理；原固定输入、人工复核空字段和质量门不变。`--validate-only --capture-decoding-boundary` 不查看模型/启动worker，只记录计划并返回2。实际raw候选、性能及模型错误来源必须经真实采集建立，受控端口测试不能代替。诊断只用于本合成fixture，不暴露通用用户文本日志开关，不从未保存原始候选的旧报告猜测所有候选都错。

## Hy 风格与术语组合合同

组合软件回归通过公开 worker 帧、受控 native 接口和真实管道检查：旧 ABI5 五导出闭包保持普通/单项且不发布组合能力；扩展闭包必须实际绑定组合符号；缺失符号、未知导出、哈希或 ABI 错误拒绝。组合帧一次携带原文、风格和术语，验证结构、指定译法、非法字段、同一截止时间及无部分输出；Runtime 另外检查独立组合质量门、连接身份和人工稿保护。单项质量准入不自动授予组合。现有风格/术语采集只验证各自能力字段，不能当作组合证据，不因新增能力字段自动执行组合。实际组合采集及其限制见下文，不改变资源配置或放行条件。

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

同分支的默认产品检查与显式 `workflow_dispatch/check_mode=quality-only` 使用两个固定 concurrency group；push、PR、缺省或其它 check_mode 均归 full，不按实验参数增加组。每组仍取消本组旧 run，quality-only 不再取消正在运行的 full。Hy 与 Argos 质量实验由编排者串行派发到同一 quality-only 组，确认上一轮已终态再发下一轮；独立 runner 不共享推理 CPU。此为工作流配置约束，实际跨组并发仍须在 GitHub run 上验证，不能由静态检查推断。

`tests/linux/native_e2e.py --desktop --desktop-scope model-packages --collect-translation-quality` 是显式入口。仅此 scope 接受采集标志；不传标志时，原240秒生命周期、260秒外层和原安装/切换/移除链不变。需先完成正式 Argos 双向安装，并有同期含 ABI5 的发行包、已验证的 Hy 模型/许可证缓存及现嵌套桌面依赖。数据和缓存都必须是本 checkout 的 `artifacts/` 下已存在、当前用户拥有的隔离目录，不能指向用户数据或缓存；输出目录必须全新，已有质量 JSON 不覆盖。入口本身不准备模型、复制权重或制造库存/Ready。

现有 `check.yml` 的手动 `workflow_dispatch` 提供默认 `full` 的 `check_mode`，并可明确选择默认 false 的 `collect_translation_quality`。该输入为 true 或下述 push footer 经严格验证时，将现唯一 Linux 模型包生命周期调用切换为采集模式，输出到全新 `artifacts/linux/quality-capture`；不在成功移除 Hy 及缓存之后重复执行。`check_mode=full` 保持共享、Windows 与 Linux 原检查选择；只设置 `collect_translation_quality=true` 仍运行原完整检查。普通 push、PR 及默认手动执行均不采集。未选择解码对照或固定诊断时，workflow 显式传入 `--combination-only`，采集两个固定原源各四种 operation 的8个响应，替换原53响应采集并关闭额外 Hy 单句预览；完整检查中的原 Argos 自检和其它模型场景仍保留，8不是整轮推理总数。仅显式运行的 Linux job 为70分钟、该步骤为36分钟；原自动路径仍为45分钟、5分钟。采集前 admission 保留原磁盘检查；8响应模式要求至少1477秒：缓存恢复120秒、准备180秒、缓存保存120秒、采集及退出877秒、artifact余量180秒。其它采集模式仍要求2760秒，其中采集及退出2091秒、步骤启动/收尾69秒，其余余量相同；均不再叠加原5分钟生命周期。未准入即停止，不开始缓存准备。

明确选择 `check_mode=quality-only` 且 `collect_translation_quality=true` 才启用非验收采集；未同时确认采集或未知模式直接失败，不默认为缩减检查。该模式保留共享检查、同一 SHA 的 Linux 构建与完整包组装、原生 ABI/manifest/模型固定锁验证、正式 Argos 安装、GTK 运行基础及原 Hy 安装/切换/退出与所选固定响应采集（默认8响应）。包组装依赖的 Fcitx/Qt 构建探针仍运行；仅跳过 Windows 和无关浏览器、托盘、教程、GTK/Qt真实模型及 VS Code 交互场景。不复制模型或制造库存，不增加旁路质量执行器；名称与摘要明确标注不构成完整产品验收。该显式模式不改变 push/PR 的分流和失败封闭规则。

默认8响应采集没有手动 dispatch 入口时，可在已授权的 `work/linux-usability` 新提交的纯文本正文末尾，以空行分隔的最后一段仅写 `TypeLay-Quality-Capture: true`，明确请求同一次完整检查附带采集。仅已存在分支的非强推、有效祖先到新 HEAD、push 的 `after` 与 HEAD 相同，且事件 `head_commit` 的 ID 和完整正文与 Git HEAD 一致时生效。它强制保留共享、Windows 和 Linux 完整检查，不启用 Qt/练习部分验收，也不是 quality-only。普通提交、旧历史中的标记、正文中途/标题/引用/缩进文本、含反引号或波浪线代码围栏的正文、重复标记、PR、其它分支及证据不全均不启用采集；未验证的显式请求仍保留完整检查。采集布尔值由同一个 scope 输出驱动原有预算、无字节码准备及正式生命周期入口，不新增模型或另一套 runner。只为已有明确实验假设添加标记；后续普通提交不会继承采集请求。仍须等待同分支上一轮完成，避免并发组取消有价值的验证。

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
  --collect-translation-quality --combination-only --package artifacts/linux/package \
  --native artifacts/linux/native --output artifacts/linux/quality-capture
```

8响应模式使用 `styled_glossary_quality.py` 与固定 `styled_glossary_quality_inputs.json`（SHA-256 `a12ac32993d521956e57f4d807dca7b7fd8d342d5473bc1bd9a1347907ad287a`）。两个原源分别为中译法、法译中，每句依次执行普通、Formal、术语、Formal与术语组合；hello 必须精确声明三项能力为整数1，不回退到普通翻译。Hy 缓存准备使用 `--cache-only`，缺失或校验失败即停止，不下载补齐。沿原五次模型事务安装 Hy、回切 Argos、选择已安装 Hy 后，在真实 Ready、原宿主退出及同代独占锁成立时仅采集这8个响应；确认采集 worker 退出后同代恢复宿主，再回切 Argos、核对原库存并移除 Hy。停服必须核对 desktop、Host、worker 的 PID/starttime 退休及两个私有 socket 移除，重启仍要求精确同代与实际 hello Ready。该模式不执行原32/16/5采集或额外 Hy 单句预览，不改变原 Argos 安装自检及完整检查的其它场景。

独立 CLI 不传 `--combination-only` 且不选其它采集模式时，仍保留53响应路径，不是当前 workflow 默认：原宿主本轮实际加载 Argos 双向并 Ready 后，先正常退出宿主，核对原 desktop、Host 与 worker 的 PID/starttime 均退休及两个私有 socket 已移除，再执行现 `alternative_quality.py` 的16句/32请求。采集器继续唯一拥有自己的进程组及原 SIG/EOF/TERM/KILL 清理；退出证实后，以未改的库存、设置及精确同一安装代重启真实宿主，再次等待实际 hello Ready。随后沿原 GTK Install Hy 流程，Hy Ready 后同样停服，串行执行 `styled_quality.py --include-holdout` 的16请求和 `glossary_quality.py` 的5请求，每个退出已证实才进入下一个。再次同代重启后，原单句中译法、Select、回切及 Remove 断言全部保留。重启只接受初始化 `idle` 及精确同代；正常包事务仍要求迁代，未放宽原断言。

采集期间仅锁住该自有 session、安装器与已实际服务的精确代的现有生产锁，拒绝 Host/installer 并发，不创建锁标记或遍历未知代。runner 在 fixture 进程中同步执行，有限隔离并恢复其 Python 模块、argv 与导入路径，避免已导入的发行包模块遮蔽当前采集源码；不增加 wrapper 模型所有者，不复制采集逻辑。

每个 runner 保持600秒采集及最多7秒清理，原产品每请求期限和模型资源配置不变。fixture 原240秒只扣除实际采集 wall time；8响应模式外层从启动起共847秒工作窗口，另30秒退出余量，总877秒；53响应、解码对照及固定诊断路径仍为2061秒工作窗口与30秒退出余量，总2091秒，后3秒留给 compositor 清理。超过工作截止不再启动或恢复进程。compositor 提前退出或外层中断时，仅向本轮已核 PID/starttime 的 inside 进程发 TERM，等待 runner 原清理和 fixture finally；不能把 compositor 已退出当作 worker 退出证明。任意外部 SIGKILL、无法读取身份或缺少最终报告不构成回收保证。

8响应模式输出 `styled_glossary-quality.json`，53响应路径输出原三个 JSON，均保留完整原始请求、响应及初始为null的人工复核字段；`hy-package-result.json` 另记录总 wall time、采集时间、非采集时间、所选固定集合的最大质量请求数及实际记录的请求尝试数（包含失败在途请求，hello 另计；不包含原生命周期单句试译）。完成要求逐份 `workerStarts=1`、正常退出码0、`workerExitConfirmed=true`、`status=completed`、精确原始/holdout输入与库存身份，以及精确请求数（8响应模式为8次翻译、1次hello；53响应路径为32/16/5次翻译、各1次hello），不能只以“不超过上限”放行。产生响应后 `quality` 保持 `not-reviewed`，尚未采集则如实为 `not-run`；两者都不是语言质量通过。

[当前8响应实际采集](https://github.com/DingJWuyu/Typelay/actions/runs/39726696991)在 `68742878` 完成上述两个原源的四种 operation：8次翻译、1次hello、1个worker，约31秒，退出码0且退出已确认。逐句复核未见明显否定、数字、主体、条件或承诺强度翻转，术语请求满足指定译法；但普通、Formal和术语中译法的 `payer` 未明确表达“付清”，组合的 `régler la somme` 更贴近原义。原始 `quality=not-reviewed` 与人工复核null字段保持不变；两个原源不足以证明十二方向质量或风格效果，相关质量门仍关闭。

失败不重试，不继续恢复、Select 或 Remove。原 JSON、隔离数据和失败 HOME/搬迁包保留；缺失、坏或空的报告及退出未知均失败。outer 只在 fixture finally 后的本轮完成/失败标记、精确 inside 退出及各份退出证据齐全后收束。本入口的受控测试只证明参数、导入隔离、预算、取消/进程清理和失败保留；所选实际响应采集、GTK全链、资源峰值与语言人工复核仍须分别执行，不能由这些测试推断。

### 可选的 Hy 解码对照

手动运行时，可在 `full` 或上述 `quality-only` 与 `collect_translation_quality=true` 之外明确设置 `hy_decode_comparison=true`。没有 dispatch 入口时，可在上述经验证的 push 采集 footer 同一末尾段中，紧接 `TypeLay-Quality-Capture: true` 下一行仅写 `TypeLay-Hy-Decode-Comparison: true`。两行各出现一次且顺序固定；对照标记单独出现、重复、换序、错误值或不在尾段均不启用采集，并保留完整检查。push 对照仍运行全部共享、Windows、Linux 检查，不提供 quality-only 捷径。该选项默认 false，与8响应组合及固定诊断模式互斥，只采集普通翻译 A/B 各8次，共16次，不混采 Argos、风格、术语或额外 Hy 预览。风格独立 collector 保留其产品质量用途。它不改变发行模型或生产默认：A 使用同次实际发行 worker 的 greedy，B 在隔离产物目录中复用已验证 CPU 库编译另一个 adapter。两组使用同一安装代、模型、提示和固定样本，但 native/manifest 摘要必须分别记录，不能冒称同一二进制。

CI 在该模式强制 `--cache-only`，先保存两臂 not-run 计划，再验证现有缓存；缺失、链接或哈希不符即失败，不下载补齐、不开始推理。计划在 `quality-plan/*.json`，未运行原因在 `hy-package/cache-verification.json` 及步骤日志；不能把计划当成实际采集。普通 release CI 默认不启用此模式。正式安装原有自检不计入16次采集，不能宣称整轮只有16次推理。

B 采用明确的 Transformers v5.6.0 顺序：repetition penalty 1.05、temperature 0.7、top-k 20、top-p 0.6，最后用逐请求重置的固定 seed `20261020` 采样。这只是原产品资源预算下的四参数对照，不是原厂完整运行配置；ctx1024、新生成192 token、CPU2、2.5 GiB及原 EOG/截断拒绝保持不变。

B 构建、真实 ELF/ABI 检查及 A/B 采集共用600秒绝对截止，最终退出清理最多7秒，不增加外层预算。A 响应不完整或退出未确认时不开始 B 推理；不重试、不换 seed、不挑结果。分别保存 `ordinary-quality.json`、`ordinary-b-quality.json` 与 `ordinary-hy-decode/build.json`、`build.log`，逐份核对响应和退出证明。采集或单 seed 样本改善都不能自动开放质量门；仍需逐项审阅全部保义锚点、记录退化及不确定性。

## 结果表述

在任务回复或 CI 结果中分别说明构建、行为/协议、分发/进程、模型推理、真实桌面交互与视觉结果，使用 passed/failed/not-run 并明确范围。结果属于执行时的提交，不能外推到其它版本；不新增仓库内验收报告或空白验收模板。

## Linux 设置与教程独立验收

完整检查及 practice 部分验收分别执行 `native_e2e.py --desktop --desktop-scope setup-settings` 和 `--desktop-scope setup-tutorial`，两者都要求原真实模型安装成功，使用独立 config、输出目录及各自隔离 HOME/退出清理。设置失败不阻止教程，教程失败也不抹去设置结果；单个作用域通过不能替代另一项。设置保留360秒原预算与方向安装回归，教程使用240秒桌面预算并核验实际主 host Ready，两者合计最大600秒工作预算及各自原清理余量，不扩大单场景期限。Linux job及质量采集 admission 总预算不变；quality-only 跳过两项，不将其计作通过。

### Hy ordinary 诊断对照

`tests/shared/worker/ordinary_quality.py` 只供显式根因定位，默认 CI 不执行、不改变质量门。它固定复用 styled 的原始四句和 holdout 四句，同一生产 Hy greedy worker 各调用一次普通 `translate`，最多 8 次；身份核验、600 秒总限时与自有进程清理复用 styled 采集器。无重试、下载或模型替换。

```sh
python tests/shared/worker/ordinary_quality.py --validate-only --output artifacts/hy-ordinary-prepared.json
# 实际采集需已安装的同代路径，不能以准备结果当作模型测试：
python tests/shared/worker/ordinary_quality.py --python "$HY_PYTHON" --models "$HY_MODELS" --worker "$HY_WORKER" --output artifacts/hy-ordinary.json
```

比较前人工核对两份报告的输入 SHA、worker 源码映射、inventory SHA、模型版本与 native adapter 身份完全相同，并确认基线为 greedy 而非采样 B。身份不同则不能归因。假设为“styled 提示路径诱发原义漂移”：若 ordinary 同句仍翻转否定，则仅去掉 styled 路径不足以修复；若 ordinary 正确，只能支持继续定位提示条件，不能证明普遍改善。生产 ordinary 同时去掉风格指令和 styled 忠实度补充句，因此不是只改变 style_name 的单变量因果实验。仍按全 8 句原义、数字、主体、承诺强度审阅；原始采集保持 not-reviewed，不自动放行，不以普通翻译替代风格功能。

### 显式固定模型根因诊断（40 响应）

需要实际定位 Argos 原始候选与选择边界、Hy ordinary 与 styled 提示路径时，可在 `collect_translation_quality=true` 之外明确选择 `fixed_model_diagnostics=true`。它与 `hy_decode_comparison` 互斥，替换而非追加默认8响应组合采集或原53响应采集。无 dispatch 入口时，沿上述严格校验的 push footer，最后一个空行分隔段仅含相邻两行，依次为 `TypeLay-Quality-Capture: true` 与 `TypeLay-Fixed-Model-Diagnostics: true`。两行必须唯一、精确且顺序固定；与 comparison 共选或畸形标记不采集，完整检查保留。普通 push/default manual 行为不变。

该选项只在原包生命周期的 Argos Ready/退出/同代独占锁之后执行 `alternative_quality.py --capture-decoding-boundary` 的32请求，Hy Ready/退出后执行 `ordinary_quality.py` 的8请求，合计40个诊断响应及另计2个 hello。不会执行 styled、glossary 或 B 采样；原生命周期自身的普通翻译 smoke 仍保留，另计且不是这40个诊断样本。instrumented Argos 入口必须先通过现有完整发行 worker Python 模块、requirements/catalog、模型/CPU锁及 ABI5 manifest 同源核验，原始报告明确记录插桩方法与 observer SHA；Hy 使用真实发行 worker 原 greedy。两模型各自固定输入、原始响应、库存与 worker 身份分开记录，不跨安装代或 native 版本冒称成对因果。

复用同一现役安装、宿主退出与重启、精确代锁和退出确认，不增加模型所有者。诊断模式下 Hy cache preparation 使用 `--cache-only`，必须已有固定缓存完整文件，缺失、链接或哈希错误直接失败，禁止下载补齐；Argos 使用本次原有正式安装。原70分钟job、36分钟步骤、2061秒工作窗/30秒外层清理及每runner600秒/7秒清理均不增加。输出仍为 `quality-capture/alternative-quality.json`、`ordinary-quality.json` 和生命周期汇总，禁止覆盖；不新增模型或 HOME artifact。失败不重试或续跑，独立CLI的53响应路径保持原行为，解码对照改为上述16响应普通翻译。此入口和无模型行为测试不是实测响应或质量放行，采集完成仍须按原义等锚点人工审阅，三项质量门保持关闭。

## GTK 语言预览拒绝回归

`gtk_session_reentry.py` 的只读续显负例保留真实源输入法 `Known | Detached`，焦点只能来自该语言按钮或它自己已映射的子菜单；GTK3 的独立 attached popover 不等于按钮后代焦点。正文、authority、编辑权限、外部焦点和源输入变化仍分别要求拒绝。

`trigger-expired-render` 等原预览实际退休后重放最后一次有效请求的原 `expiresAtMs`，验证过期请求不能重新显示。它不证明已销毁菜单仍处于 detached 续显分支，也不禁止源框恢复真实 Clear 后的正常新预览。该场景另用显式回源、新采集及默认新期限验证只读显示合法；两条路径均不授予接受或写回资格。纯 snapshot/request 测试只验证 runner 合同，实际 GTK 桌面场景仍须单独执行。

## GTK3 输入通知去重合同

`python tests/linux/test_input_notifications.py -v` 编译并运行生产私有通知账本，覆盖完整通知、缺额/多额、换代、回退、溢出、消费者重入及 probe 中通知；它不证明真实 GTK 或输入法可用。原 `native_e2e.py` Unknown-IME 拒绝场景保持不变，随后单独启动受控 IM context fixture，加载生产 GTK3 module，先保留旧 probe 缓存，再送入已完成通知及一次真实 F9。该对照包含完整15次通知、已结束的 producer 嵌套两通知，以及 probe 中输入/零cause、漏通知、context 换代、epoch 回退和未知cause的拒绝；不运行模型，不提供写回许可，不冒充真实 Fcitx Clear。

去重依据锁定 provider 的唯一 `input_epoch++` 与同次同步 INPUT signal 对应关系，只处理已执行原 invalidation 的通知；原 revision 和许可撤销不恢复。消费者自身回调重入、probe 期间通知及不完整证据不允许去重。V1 不暴露其它监听者已结束的 producer 嵌套深度：若两次通知均完整处理、原 emit 栈已返回，且后续完整 inspect 的同 context 版本差额精确吻合，则两次均视为已处理输入。此逻辑不建立 Clear，仍须实际 provider inspect 成功；GIMP 真实应用和真实输入法验收另行执行。

## GTK3 TextView 直接写回 transport proof 续接

`native_e2e.py` 在原 Unknown-IME 和8组通知合同之后，单独启动 `direct` 受控 IM context fixture。控制文件只由隔离 fixture 的 GLib 主循环消费固定动作和严格递增序号，不进入生产进程；未知动作与重复序号拒绝。通过生产公开 capture 冻结原文和 authority，然后发送一次 cause=0 的 provider 版本变化，读取真实 fresh describe，再调用公开 direct。正例必须验证实际正文、选区、Verified snapshot 和原 `begin-user-action` / `end-user-action` 各一次。输入、context、serial、组成、焦点、正文、选区、epoch 回退、prepare 中输入以及缺失/不匹配/过期批准分别保持拒绝且不得改写当前正文和选区。

续接只用于 GTK3 标准 TextView：旧 basis 除单调 transport epoch 外全部字段和三种 revision 精确保留，当前 focused Clear 与 fresh approved 完整 stamp 仍须成立，沿用150ms批准和原事务全部后验门。它不借用 Entry 的 Detached 路径，不改变 provider ABI。受控端口验证写入分组协议，不证明真实输入法、原子性或 GTK3 原生 Undo。真实 GIMP 的普通应用写回与原生 Undo 轨迹对照仍独立执行；本地对象编译不能替代这两层嵌套桌面执行。

## GIMP Comment 的宿主撤销边界

`ordinary-gimp` 保留两张独立临时图像、同原文与实际译文、一次 TypeLay direct 和一次真实用户粘贴，各执行固定两次 Ctrl+Z。比较的是这两个实际轨迹，不循环重试到原文出现，也不向 GIMP 注入历史。GIMP `3.2.6-2` 的 Image Properties → Comment 本身不为评论修改建立 Undo：官方 [GIMP_3_2_6 comment editor](https://github.com/GNOME/gimp/blob/GIMP_3_2_6/app/widgets/gimpimagecommenteditor.c#L214-L218) 只设 `GIMP_PARASITE_PERSISTENT`；[image attach/detach](https://github.com/GNOME/gimp/blob/GIMP_3_2_6/app/core/gimpimage.c#L4499-L4502) 仅在 `UNDOABLE` 时推入历史，二者是[不同 flags](https://github.com/GNOME/gimp/blob/GIMP_3_2_6/libgimpbase/gimpparasite.h#L59-L75)。

仅该已核版本/字段且原生粘贴两次均保持译文、TypeLay 完整轨迹相同时，报告 `nativeUndoSupported=false`、`undoRestoration=not-supported`，兼容性轨迹匹配单独记录，不宣称 Undo 功能通过或不存在的历史得到恢复。未来原生基线若实际恢复原文，则要求 TypeLay 同样完整轨迹；未知版本的不恢复行为、任意轨迹差异或动作数变化均失败。此项遵守规格 §17.2 的文本归属和 ADR-0002 请求保留宿主撤销语义，不改变冻结规格，不证明其它字段或已有图像编辑历史的完整性。普通应用退出、取消断开、恢复配置和 guardian 退出证明仍须继续完成，局部替换通过不等于整条验收通过。

## Hy 普通翻译解码对照

普通质量线的独立对照入口是 `ordinary_quality.py --decode-comparison`。它不接入自动 CI，不安装或下载资源。只复用正式安装成功的 Hy1.8B Q4_K_M、同期发行 worker 与 CPU runtime；调用点在 `hy_package_e2e.py` 安装并切换 Hy 成功后、移除前，路径与上述质量采集一致。准备阶段不能记作实际配置尝试。

```sh
PYTHONPATH=src/shared/TypeLay.Worker python tests/shared/worker/ordinary_quality.py \
  --decode-comparison --validate-only \
  --output artifacts/hy-ordinary-ab/ordinary-quality.json
# 实际执行须先确认已有安装、发行包与预算，用新的输出目录：
PYTHONPATH=src/shared/TypeLay.Worker python tests/shared/worker/ordinary_quality.py \
  --decode-comparison --python "$components/runtime/venv/bin/python" \
  --models "$components/models" --worker "$bundle/worker/run_worker.py" \
  --output artifacts/hy-ordinary-ab-run/ordinary-quality.json
```

固定原四句与已冻结四句，共 8 句，每臂每句一次普通翻译，共 16 次。A 为生产 greedy，B 为已有 repetitionPenalty=1.05、temperature=0.7、top_k=20、top_p=0.6、seed=20261020（每请求重置）组合。两臂沿用同一普通 prompt、官方内嵌对话模板、worker 与模型库存，只改变 decoder；此组合对照不能区分其中每个参数的独立作用。B 的派生 adapter 只在忽略目录构建，不改生产默认或锁文件。

共享 600 秒含构建与两臂，2 线程、单模型、2.5 GiB 地址空间；每请求仍为生产 60 秒/收帧 65 秒，构建及每臂退出各保留原最多 7 秒清理。无需复制模型；运行前另留至少 256 MiB 临时磁盘，实际构建占用仍须检查。身份不符、A 不完整、超时、拒绝或清理未确认即停止，不重跑挑结果。输出不可覆盖，分别为 `ordinary-quality.json`、`ordinary-b-quality.json` 与 `ordinary-hy-decode/` 构建证据；validate-only 只生成两个 not-run 计划并返回 2。

报告保留输入、逐条原始响应/拒绝码/耗时、worker/native/源文件/库存身份及固定参数。`promptIdentity` 记录源码中的指令表达式和完整模板，明确不是运行时 token 轨迹；native 未暴露 token 数和结束原因，截断状态保留未知，不能把非空输出当作未截断。质量保持 not-reviewed，按既有语义锚点人工比较否定、数字、主体、条件、承诺及增漏义；当前句子已参与过诊断，不称为未见测试集。第二阶段若有依据，再固定一个 decoder 对比官方 prompt 与单个保义约束 prompt；本入口不实现 prompt 搜索或多模型评审，也不同时改变 prompt 与 decoder。
