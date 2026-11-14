# 采集与评审翻译质量

本指南用于已有明确假设的模型质量任务。先冻结问题、输入和执行身份，再选择既有采集入口，最后独立评审原始响应。日常软件回归按[测试协议](testing.md)执行，不需要模型采集。

当前只有固定 Index profile 的中英双向术语获得功能增量准入。风格、另述和组合质量门仍关闭。发布行见 [`TranslationReleasePolicy.Current`](../../src/shared/TypeLay.Runtime/Models/TranslationService.cs)，现有能力说明见[模型能力边界](model-runtime.md#模型能力边界)。本页提供操作方法，不授权启动下一轮试验或改变质量门。

## 冻结问题、输入与运行条件

先按[实际价值判断](testing.md#实际价值判断)说明用户场景、已有失败、发生频率和预期收益。把本轮假设、比较对象、采用条件和停止条件写入当前任务。已有结果相同或严重错义未减少时，应停止同配置盲跑；下一轮需要新的明确假设。

采集前确认以下条件：

1. 冻结源文、语义锚点、顺序、精确请求数和输入 SHA。标明开发集、已见回归与尚未消费的独立验证集。
2. 冻结模型、安装代、worker、native adapter、CPU runtime、prompt 和 sampler 身份。使用正式安装器已经成功产生的模型与 venv，并核对同期发行包。
3. 确认独占模型资源槽。Argos 与 Hy 试验串行执行，避免与 GUI 或其他重负载争用同一资源。
4. 指定全新的 `artifacts/` 输出目录。保留失败原件，不覆盖报告；原始用户文本不进入采集。
5. 确认入口的身份与无模型行为回归仍有效。缺少安装、固定缓存或目标平台时，只完成准备并标记 `not-run`。

新试验维持 2 CPU、worker 硬地址空间上限 2.5 GiB、单请求 60 秒、共享总预算 600 秒及末尾最多 7 秒清理。预算包含加载，不按方向续时；不重试、重启、换 seed 或追加样本。下文两个旧 Argos 入口的请求默认值为 90 秒，使用时必须明确这个差别，不能将旧默认误报为新的 60 秒约束。

正式包完整性和质量身份分别核验。包 manifest、模型锁与原生闭包证明实际执行资源；受审 profile 还绑定影响输出的源码、模板、采样、上下文和资源策略。发布表、UI、编译路径和时间不属于推理身份；平台重建本身不等于新质量配置。报告中的哈希或 hello 能力字段不能代替 Host 对实际执行输入的验证。

## 选择已有采集入口

下列脚本均位于 [`tests/shared/worker/`](../../tests/shared/worker/)。表中请求数指质量采集，不含 hello、安装自检或生命周期自己的 smoke。脚本负责核固定输入 SHA，文档不复制哈希清单。

| 需要回答的问题 | 入口 | 固定采集范围 |
| --- | --- | --- |
| Argos 普通与另述是否保义 | `alternative_quality.py` | 16 源，各请求普通和另述，共 32 次。 |
| Argos 原始候选在哪个边界失真 | 上述入口加 `--capture-decoding-boundary` | 同 32 次，另保存真实 CT2 候选与转换证据。 |
| Argos 重复惩罚是否影响保义 | `argos_repetition_comparison.py` | 24 源，A/B 各一次，共 48 次普通请求。 |
| Hy Natural 与 Formal 是否有效 | `styled_quality.py` | 原 4 源共 8 次；`--include-holdout` 使用 8 源共 16 次。 |
| Hy 普通提示与风格提示有何差异 | `ordinary_quality.py` | 同一组 8 源，各一次普通请求。 |
| Hy decoder 组合是否改善结果 | 上述入口加 `--decode-comparison` | 同 8 源，A/B 共 16 次普通请求。 |
| Hy 术语请求及拒绝是否符合预期 | `glossary_quality.py` | 5 次固定请求，包含结构及组合拒绝。 |
| Hy 普通、风格、术语与组合有何差异 | `styled_glossary_quality.py` | 中译法、法译中各一源，四种操作共 8 次。 |
| Index 六种操作的固定回归 | `index_quality.py`、`index_final_quality.py` | 分别使用固定 8 源和 16 源；另述依赖同轮普通成功输出。 |
| Index 风格与另述的功能增量 | `index_style_alternative_quality.py` | 固定 16 源、64 个计划帧，判据见下文。 |

各入口只采集事实，不自动给语言质量结论。Index 的旧开发集和已消费留出仍可作为已知回归，不能通过重命名或重排变成独立新样本。Index 术语的 20 源增量评审是已完成的固定计划，不存在可替代该计划的通用常驻 runner；后续运行需先明确新的任务和输入来源。

## 先检查准备结果

在仓库根目录用 `--validate-only` 检查输入、参数和计划。例如：

```sh
PYTHONDONTWRITEBYTECODE=1 PYTHONPATH=src/shared/TypeLay.Worker \
  python -B tests/shared/worker/styled_quality.py --include-holdout \
  --validate-only --output artifacts/hy-styled-prepared/plan.json
```

这些准备入口不读取安装、不启动 worker，也不加载模型。预期报告为 `status=not-run`、`quality=not-run`，并有意返回退出码 2。返回 2 且计划有效表示准备完成，不能算推理通过。已有输出路径会被拒绝。

`ordinary_quality.py --decode-comparison` 的输出文件名必须为 `ordinary-quality.json`。准备时它生成 A/B 两份 `not-run` 计划。固定 profile 或源码身份校验失败时，先核来源，不能改锁或输入来让准备通过。

## 从正式安装采集

本页独立 CLI 的实际运行要求 Linux。以下示例路径必须已经来自正式安装和同期发行包，命令不会下载资源、修补库存或制造 Ready。

```sh
components="$PWD/artifacts/linux/desktop-data/typelay/components/current"
bundle="$PWD/artifacts/linux/package"
PYTHONDONTWRITEBYTECODE=1 PYTHONPATH=src/shared/TypeLay.Worker \
  python -B tests/shared/worker/styled_quality.py --include-holdout \
  --python "$components/runtime/venv/bin/python" --models "$components/models" \
  --worker "$bundle/worker/run_worker.py" \
  --output artifacts/hy-styled-capture/result.json
```

Hy 和 Index 入口要求同代 schema3 库存、准确的 active 包与方向，以及同期发行 worker。采集器核入口、Python 模块、requirements、catalog、模型锁和 CPU 锁；额外包或字节码遮蔽也会失败。正式 native adapter 必须实际加载，hello 必须准确声明本操作所需能力。组合操作还需独立组合能力，不能借用单项资格。

Argos 入口使用同代正式双向安装，支持其合法 schema1/2/3 库存。它不接受替代来源；schema3 必须核精确包身份。普通与另述采集可使用以下命令：

```sh
PYTHONDONTWRITEBYTECODE=1 PYTHONPATH=src/shared/TypeLay.Worker \
  python -B tests/shared/worker/alternative_quality.py \
  --python "$components/runtime/venv/bin/python" --models "$components/models" \
  --output artifacts/argos-alternative-capture/result.json
```

该入口默认每请求 90 秒、总计 600 秒，退出另留最多 7 秒。Hy 保持生产单请求 60 秒，外层收帧 65 秒，加载及 hello 90 秒，总计 600 秒。Index 仍使用自己的固定 profile，不借 Hy 配方运行。

成功采集必须同时满足身份准确、计划覆盖完成和自有 worker 退出已确认。失败时保存已完成响应、失败阶段、退出码及未运行项。退出未知或缺失报告均不能算完成；也不能把 compositor 退出当作 worker 已退出。

## Linux 模型包生命周期中的显式质量采集

需要同时验证安装生命周期和固定响应时，使用现有 `model-packages` 场景。它只在显式传入 `--collect-translation-quality` 时采集；不传标志则保留原安装、切换和移除路径。

### 确认包、缓存与预算

先完成正式 Argos 双向安装，准备同期含 ABI5 的完整发行包，并具备嵌套桌面依赖。Hy 模型与许可证缓存必须已经验证。数据、缓存和输出均使用本 checkout 的 `artifacts/` 下、当前用户拥有的隔离目录；输出目录必须全新。

workflow 的默认 8 响应模式、Hy 解码对照和固定诊断都使用 Hy `--cache-only`。缺文件、链接或哈希不符即停止，不下载补齐。Argos 的正式安装步骤仍可能下载官方包，因此不能将整轮称为“零下载”。独立 CLI 的原 53 响应路径不自动获得上述缓存约束。

预算准入由 [`hy_package_e2e.py`](../../tests/linux/hy_package_e2e.py) 检查磁盘和剩余时间。默认 8 响应模式需要至少 1477 秒，其余模式需要 2760 秒。未准入时停止，不开始缓存准备。显式采集的 Linux job 为 70 分钟，生命周期步骤为 36 分钟；默认自动路径仍使用原预算。

### 选择采集模式

以下模式互斥，选择一个即可。它们替换质量采集集合，不追加到同一轮。

| 模式 | 生命周期参数 | 采集内容 |
| --- | --- | --- |
| 默认组合对照 | `--combination-only` | `styled_glossary_quality.py` 的 8 次响应。workflow 默认使用此项。 |
| Hy 解码对照 | `--hy-decode-comparison` | ordinary A/B 各 8 次，共 16 次。 |
| 固定根因诊断 | `--fixed-model-diagnostics` | Argos 解码边界 32 次，Hy ordinary 8 次，共 40 次。 |
| 独立 CLI 原集合 | 不加上述参数 | Argos 32 次、Hy styled 16 次、glossary 5 次，共 53 次。 |

本地默认组合采集命令如下。安装、缓存和发行包仍须先满足上述条件。

```sh
export XDG_DATA_HOME="$PWD/artifacts/linux/desktop-data"
export XDG_CACHE_HOME="$PWD/artifacts/linux/hy-model-cache"
PYTHONDONTWRITEBYTECODE=1 python -B tests/linux/native_e2e.py \
  --desktop --desktop-scope model-packages \
  --collect-translation-quality --combination-only \
  --package artifacts/linux/package --native artifacts/linux/native \
  --output artifacts/linux/quality-capture
```

采集在原宿主真实 Ready 后开始。fixture 先确认 desktop、Host 和 worker 的 PID/starttime 已退休，且两个私有 socket 已移除，再锁住已经服务过的精确安装代。采集器独占自己的进程组；退出已确认后才恢复同代宿主、继续切换和移除。正常包事务仍要求迁代，不能把同代恢复例外用于安装。

默认组合模式在 Hy 安装、回切 Argos、再次选择 Hy 后采集。原 53 响应路径先采 Argos，再串行采 Hy styled 与 glossary。每个 runner 最多 600 秒采集及 7 秒清理；前一 worker 退出未知时，不启动下一项。

fixture 的非采集预算仍为 240 秒。默认组合模式工作窗口为 847 秒，另留 30 秒外层退出；其他模式为 2061 秒加 30 秒。实际采集时间从 fixture 预算单独扣除。超过截止不再启动或恢复进程；外层中断只清理本轮核验身份的自有进程。

### 从 CI 显式发起

常规 CI 只执行必过验证，不提供质量采集开关。已有授权的研究使用上文的现役采集入口独立执行，并保留对应提交、模型和实际输出。采集结果不构成完整产品验收。

### 核对完成与退出

逐份检查报告的输入、库存与 worker 身份，以及 `workerStarts=1`、`workerExitCode=0`、`workerExitConfirmed=true` 和完成状态。请求数必须精确匹配所选集合，每个 runner 另有一次 hello。不能只凭“不超过上限”宣布完成。

`hy-package-result.json` 还记录总时间、采集时间、非采集时间和实际尝试数，包含失败在途请求。原生命周期单句试译另计。默认组合模式不执行额外 Hy 单句预览，其他生命周期断言仍保留。

采集失败后停止恢复、Select 和 Remove，保留原 JSON、隔离数据与失败 HOME/搬迁包。上传只包含明确列出的 JSON、日志、截图及解码构建证据，不上传模型、runtime 或失败 HOME。读取发行 worker 的准备和隔离 Python 使用禁字节码方式，不能清理 `__pycache__` 后假称原包身份未变。

## 判读固定诊断

### Argos 普通翻译单因素调优

`argos_repetition_comparison.py` 比较重复惩罚 1.05 与 1.0。两臂使用同代官方中英双向包、CPU INT8、beam2、相同线程数及输入/解码上限。A 后 B 顺序运行，共享 600 秒含加载；每请求默认 90 秒，每组退出最多 7 秒。A 不完整、身份不符或退出未确认时，不开始 B。

输入由原 16 源和已消费的 8 源组成，A/B 共 48 个响应，只算两套配置。采用候选需要原 16 源重大错义减少，8 源回归不增加重大错义，且无新增数字、否定、主体或结构错误。还需新的独立验证与普通/另述共同消费者检查，才可提出生产改动。结果相同、错义未减少或出现退化时停止。

此入口未接入 CI。`--fixed-model-diagnostics` 包含普通与另述，不是普通 A/B。运行前先验证该入口的无模型合同，再由负责人安排模型资源；[`test_argos_repetition_process.py`](../../tests/linux/test_argos_repetition_process.py) 的受控进程测试不算质量采集。

### Argos 另述真实质量采集

`alternative_quality.py` 每源先取得实际普通译文，再请求另述。`no-alternative` 只表示没有可用不同候选；文本不同也不能证明保义。人工复核分别检查两个输出，不能根据 n-best 未公开的信息猜测候选内容。

只有添加 `--capture-decoding-boundary` 时，固定 fixture 才通过 `alternative_boundary_worker.py` 观察外部 CT2 API。入口核固定请求顺序和上一普通响应，额外或改序请求在推理前拒绝。插桩保持编码、解码、候选顺序与筛选不变，并记录 observer SHA、真实 source pieces、hypotheses 和转换结果。

这条诊断只用于合成 fixture，不增加用户文本日志开关。未经实际采集的 raw 候选不可从旧报告补造；插桩报告也不能冒称未插桩生产运行。

### Hy ordinary 诊断对照

`ordinary_quality.py` 用生产 greedy 对同一 8 源请求普通翻译。比较前核对输入 SHA、worker、库存、模型和 native adapter 全部一致，并确认风格基线也是 greedy。

普通提示同时去掉风格指令和 styled 保义补充句，因此不是只改 `style_name` 的单变量实验。ordinary 同句仍错，说明去掉风格路径不足以修复；ordinary 正确，只支持继续定位提示条件，不证明普遍改善。

### Hy 普通翻译解码对照

`ordinary_quality.py --decode-comparison` 以生产 greedy 为 A，固定采样组合为 B。B 依次采用 repetition penalty 1.05、temperature 0.7、top-k 20、top-p 0.6，逐请求重置 seed 为 `20261020`。该顺序沿固定对照实现，不代表原厂完整运行配置。

两臂保持同一模型、普通 prompt、模板和输入，只改变 decoder。B 在忽略目录编译派生 adapter，native 和 manifest 身份须分别记录。该组合实验不能区分每个参数的独立作用，也不修改生产默认或锁文件。

构建和两臂共享 600 秒，沿用 Hy 原资源及请求期限。另留至少 256 MiB 临时磁盘并检查实际构建占用。A 不完整、超时、身份不符、拒绝或清理未知时停止，不换 seed 或挑结果。

输出为 `ordinary-quality.json`、`ordinary-b-quality.json` 和 `ordinary-hy-decode/` 构建证据。`promptIdentity` 记录指令表达式与模板，不是运行时 token 轨迹。native 未暴露的 token 数和结束原因仍为未知，不能由非空输出推断未截断。后续 prompt 对照需要新的明确假设，不能同时改 prompt 和 decoder。

## 普通翻译评测数据分层

样本按实际用途分类，不按文件名推断独立性。`alternative_quality_inputs.json` 的 16 源、`argos_repetition_holdout.json` 的 8 源，以及 Hy 原始和 holdout 的 8 源均已消费。不同模型或配置对同源的响应不算新样本。

新的最终验证集应在候选配置完全冻结后，由独立作者创建并复核。共享工作区不等于盲评隔离，暴露情况须记录；揭晓后用于调参即已消费。不要复用已消费的 Index 或 Hy 留出作为新一轮独立验证，也不要因部分方向未测而缩窄产品语言目标。

## 语义锚点与人工判分

采集原始 JSON 保持 `quality=not-reviewed`，人工复核另存。锚点提示要检查的含义，不是唯一参考译文。先隐藏操作标签独立判保义，再解盲比较功能差异；已经暴露标签的评审须如实标明。

逐项检查主体、收发方向、否定、条件、数量、时态、时间界限、承诺强度和增漏义，再评价自然度。以下歧义按相同标准处理所有候选：

- `before Friday` 与 `by Friday` 不自动视为相同边界。缺语境时记录风险或未决；严格小于不能改成包含上界。
- 源文未明示性别时，不按姓名猜性别。日语或中文自然省略主语可以接受，但指称、主体和行动方向必须清楚。
- 合理一致的音译可以接受，人物身份仍须可区分。“给了”不自动要求赠予所有权；交付、借出方向与数量仍需保留。
- 删去冗余强调与反转否定分别记录。自然度收益不能抵消重大错义。

每个争议保存原文、译文片段、可接受读法、严重程度和理由。区分重大错义、表达问题和未决。拒绝、超限、缺输出与未运行另列，不算语义通过，也不伪报成已发生的翻译错误。双语 Agent 复核不称为专业译者人评。

旧锚点修订不能重写旧运行身份或历史结论。原件不可读时标明无法复核，不能补造译文。小样本未见错误不构成总体错误率保证，更不自动开放质量门。

## Index 术语功能增量验收

本项沿用户已批准的功能增量标准：同源、同模型和同配置比较 ordinary 与仅 glossary，判断规则是否兑现，以及非术语语义是否退化。共同的默认翻译错误单列限制，不冒称全句正确；术语出现也不能抵消新增错义。

已冻结计划为 20 源成对验证，共 40 次推理。其中 16 源在冻结时为独立新源，每向 8 条；另 4 源是 H01、严格小于 20、审批作用域和订金截止范围的已见回归。当前均不能重新计作新样本。旧 `by Friday` 边界解释单独保留，不能重写旧原件。

判定时保留以下要求：

- 使用完整生产 glossary 对象，覆盖全局/方向优先、错误方向、多规则、最长不重叠命中、重复与词边界，以及 CRLF、外层空白、URL 和 mention。测试标签不能代替实际请求覆盖。
- 对每个成功译文，全部有效规则须在正确位置兑现，禁用目标不能误用。非术语内容不得新增或加重主体、授权、否定、条件、数值与时间界限重大错误。每个方向还须有实际偏好收益；ordinary 已有目标词只证明未破坏。
- ordinary 与 glossary 若都因既定结构守卫拒绝且均无可接受正文，记为共同可用性欠账。仍保留该场景和原始失败，不算翻译成功。ordinary 成功而 glossary 新增拒绝属于退化，不能借用此例外。
- 仅预先限定的空正文业务拒绝可记录后继续：两操作的 `structure-not-preserved`，以及 glossary 的 `glossary-output-missing`。未知码、非空错误正文、身份、资源、传输、超时、取消或退出未知立即停止。
- 中断原件和原停止规则不变。只有明确批准后，才可用新报告与新自有会话执行原计划未尝试的后缀。核对原索引、内容及身份，扣除已用预算，分别记录两次会话。已尝试项不重跑，未完成覆盖不补算。

评审通过后，仍需独立审查具体操作、方向与 profile 的发布行，并验证实际平台消费者。Host 将实际输入的受信 profile 绑定到同一严格握手的 owned 连接；包版本和能力数字单独不足以准入。旧、缺失或不匹配的 profile 继续关闭高级门，普通 Ready 保持兼容。

跨身份复用语言证据时，须说明不变的推理路径、已审源码差异和确定性校验/序列化回放，并列出还需实际执行的变更路径。旧失败和超时原件不变，旧响应不能补算为新二进制下完成。

同一受审语义 profile 可在各平台完成实际安装、执行与完整性验证后绑定。语言评审的平台来源仍须明示；Linux 输出评审不等于逐字评审过 Windows 输出。`IsReleased` 只表示存在发布行，界面应用资格仍看 `CanApply` 及具体原因。Auto 必须覆盖已加载的全部可能来源，不能借一个获准方向放行其他来源。术语准入不授予风格、组合或另述资格。

## Index 风格与另述的功能增量验收

固定 profile 的独立评审已完成，两方向 Style 和 Alternative 均未达到原标准，质量门保持关闭。当前没有继续调 prompt、采样或模型的安排。以下判据用于解释和维护既有决定，不授权新一轮采集。

固定计划使用 16 源，每方向各有 4 个效用源、2 个中性源和 2 个保护结构源。先效用、再中性、最后结构，方向交替；每源依次 ordinary、Natural、Formal、Alternative，共 64 个计划帧。当前固定输入已消费，只能作为已知回归。

- 先盲化判保义，再解盲判功能。相对 ordinary 新增或加重的重大错义，拒绝该方向对应操作。共同普通错误单列，未决不通过。
- Style 每向 4 个效用源中，至少 2 个应同时给出合宜、保义且清楚不同的 Natural/Formal 语域。标点、拼写或缩写变化不计收益；中性句可以相同。发布键同时覆盖两种风格，单款通过不能放行两款。
- Alternative 每向 4 个效用源中，至少 2 个应给出保义、可用且实质不同的表达。效用源的合法 `no-alternative` 计零收益，分母仍为 4；中性源合理退让不计收益。纯标点或机械同义替换不计收益。
- `previousCandidate` 必须使用同轮 ordinary 成功输出的原字节。ordinary 无正文时标记 `dependency-not-run`，不借旧输出凑请求。ordinary 成功而风格新增结构拒绝，属于可用性退化。保护源的另述拒绝只证明能力边界；普通源意外不可另述不算预注册结构拒绝。
- 固定入口只允许严格合法的空正文业务结果继续采集：普通/风格的 `structure-not-preserved`，以及另述的 `no-alternative`、`alternative-unavailable`。继续采集不等于可准入；未知响应或身份、协议、资源、传输、超时、取消和退出问题均硬停。

入口为 [`index_style_alternative_quality.py`](../../tests/shared/worker/index_style_alternative_quality.py)。它固定输入 SHA、受审 profile、实际 runtime 与生产 native 身份，使用单个自有 worker、2 CPU、2.5 GiB、单请求 60 秒和总计 600 秒含验证/加载，末尾清理最多 7 秒。预算不足一次终止，不按方向续时，也不自动补题或续轮。

实际执行数须与计划数分开报告。因 ordinary 失败而未发送的 Alternative 不能算已验证拒绝。后续若有新的明确目的和授权，重新冻结身份与输入，再独立评审；采集器本身始终不作发布决定。
