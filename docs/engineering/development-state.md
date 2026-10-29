# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-29**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

完整原型审查确认部分页面此前仅更换样式，未完整复刻交互，属于范围执行与验收遗漏，不是单纯视觉细节。当前优先按[固定页面/状态差距清单](../design/prototype-parity.md)逐页完成界面、真实交互与同偏好对照，再进入下一页；不先打磨细字重而保留主要交互差异。两端先收敛语言与风格页，随后按独立审查表安排搜索结果定位、术语选中/行内编辑、快捷键恢复默认、隐私卡和教程完整流程。已通过的构建或旧页面测试不等于原型一致性通过。

当前代码见 [work/linux-usability](https://github.com/DingJWuyu/Typelay/tree/work/linux-usability)。最近已结束完整回归的参考检查点为 [`2e2e747`](https://github.com/DingJWuyu/Typelay/commit/2e2e747f263c404f1cb57dfbbda300899929ecde)，[对应 CI544](https://github.com/DingJWuyu/Typelay/actions/runs/40849916681) 已终态失败。该批包括已审的原生自有草稿布局与有界诊断、Linux 公开导航与焦点滚动、浏览器独立菜单、Windows 加载态/动效观察及 Hy 公开定位修补。它不包含后续 CJK 字体、Source Serif 角色字重或共享模型能力开放。 CI544 已逐条确认前批加载态与两项真实动效修补通过，Hy 及两端 Index 生命周期通过；整体仍因 Windows 前台预检/菜单、浏览器与 Notepad，以及 Linux 公开 UI/合作模块路径失败而未验收。

当前已发布第六候选 [`fbce3ae`](https://github.com/DingJWuyu/Typelay/commit/fbce3aedd9126b297614c84f34455c7e14f8b841)，[CI545](https://github.com/DingJWuyu/Typelay/actions/runs/40857811719) 已终态失败。早期结构化 Windows App 结果为总 1006、执行 984、通过 983、失败 1、未执行 22；原 17 项导航前台失败本轮通过，新增自有置顶清理回归通过，不据此宣称 Terminal 根因已证实。剩余真实鼠标菜单项命中失败：目标 popup 可见且 native Topmost，原命中仍为 Terminal，继续保留真实命中门定位。共享检查、Windows Index 与 Hy 生命周期成功；此窄测试提交按范围跳过 Linux，不能当双端验收。浏览器 800 DIP 几何修复后 setup、automatic-preview-tab、on-demand-settings 通过，当前新红是 direct-selection / wait-public-command-ready；Notepad 可信输入条件未满足的两场景仍未执行。

第七候选 [`62e4d7c`](https://github.com/DingJWuyu/Typelay/commit/62e4d7cd3de79665f359a0c68f10858517da231e) 已发布，[CI546](https://github.com/DingJWuyu/Typelay/actions/runs/40869538279) 已终态失败：共享检查仅 Qt 测试最后增量的 Ruff 格式失败；独立 Windows Index 打包因遗漏安装构建期 fontTools 而失败。两项最小修补已审，等待本轮有效桌面结果后再发，避免取消当前验证。Linux Index 生命周期成功；Windows 主合同与 Linux 桌面链失败。Windows App 执行1016项、通过897、失败119，其中116项同一语言模板目标属性错误，另3项为SC600选择错误。该批包括已审的双端语言页候选、18 面离线字体消费、三态动效、GTK3 独立 More、WPF 菜单就绪/设置 viewport 修补及 G07 窄结构修正；不包含进行中的新术语页、推理 profile 绑定或浏览器 IME 菜单修补。Windows 新语言页和 CJK 已实际编译通过，但当前 WPF 渲染仍待此 CI。Linux 新语言页已实际通过选择、搜索键盘、旧实例失效与保存回归。跨窗口 CSS 污染经真实旧实现红断言、新作用域实现绿断言验证修复；新 DLL 双语名称与浅深页也通过。进一步公开 AT-SPI 测试发现真实 popup 父链为 19 层而旧 helper 只容 16；改有界 24 层、循环拒绝并保全部祖先资格后，真实选择/写盘失败恢复通过。托盘搜索仍在初次 popup 映射后失焦，尚未定位根因；SVG 着色已使用原 SVG alpha 与当前 CSS RGBA，真实浅深色验证不再依赖 Cairo bridge。初始 idle 候选的 after-paint 无广播及托盘回放均失败，旧实例/隐去/重开/用户移焦负控通过。后续同身份只读时间轴确认 Stack 过渡期间初次 map 后 child-visible=false、unmap 与清焦，随后重新 map；到原断言时低优先 idle 尚未执行，不能声称它执行后仍失效。正在审查基于真实 map 的单次初始化，不追加抢焦点重试或延长断言。几何与字形已取得真实证据，但仍不把这些子项算完整页面闭环。

CI544 Linux 原件分别显示应用选择器可访问名称重名、教程控件非 showing、设置未保存关闭弹窗抢前台，以及 GTK4 首呈现/Qt 第二次反向点击问题；各由真实控制所有者修复，不归为一个泛化环境错误。Qt 初次展开与同源续期通过，第二点击可能因 subprocess 延迟点位过期，采用预连接公开 XTest 单次注入检验，不增加重试或改高度/期限。GTK4 首次映射后未绘制即消失仍待证据。原始 glossary artifact 中 GTK4 40/40 通过，失败是 GTK3 第 20 例后的 textview / ValidTranslation / save 同表面 hover deadline；不得按 artifact 名字误判平台。

CI 在部分失败后继续执行后续步骤；进入打包不能证明前序成功。逐项检查实际 `conclusion`、结构化结果和精确源码身份。
已发布检查点的本地验证包括真实 Chromium 74 项交互合同、Linux GTK 设置持久化/术语和应用撤销/语言/托盘流程，以及共享运行时、协议、worker 与分发合同。Windows 托管编译通过不等于 WPF 实际运行。当前 Linux 修补还通过真实 AT-SPI 八页导航、模式即时保存、快捷键录制与还原；不替代真实模型路径验收。两端完整包、原生合作模块和最终界面状态仍在验证，视觉差异不能计为一比一完成。

Index 能力放行表保持关闭。真实固定样本存在权限语义和严格数值边界错误；独立留出集首个普通请求触发既定 60 秒单请求超时，后续样本未运行。模型包生命周期成功、协议通帧、术语成功保存均不构成翻译质量通过。异常诊断保留原失败，不提高期限、不修改提示来覆盖该结果。

当前已发布修补覆盖 WPF 几何、对比度、菜单焦点和旧交互实例，以及 Linux 八页导航、即时保存和实际控件录制流程。已发布浏览器检查点本地真实完整 74 项通过。后续独立弹出菜单候选已在独占窗口完成 78 项通过；此前 76 项运行的 2 个新视图正控过期失败原件保留，同期负载重叠不是根因证明，不提高期限。此前全量出现一次持键激活与预期拒绝不符，以及接受路径失败；单次旁观诊断与本次通过均未解释原失败，不能标记根因已修复。保留有界可信事件与命令证据，后续正常全量继续核查，不刷新授权或放宽时限。

### 当前失败的原件定位

下列路径均相对于被忽略的 `artifacts/ui-rebuild/`；判断以文件内结果为准，不以步骤名或包名推断。

| 原件 | 实际 case / stage | 当前结果 |
| --- | --- | --- |
| `ci546/contracts/*12_01_32*.trx` | Settings / onboarding 116 项同 `LanguageResources` XAML Setter 异常 | Viewbox 的 Stroke 目标错误；已窄修为内部 Path，独审和编译通过，真实 WPF 待重验 |
| 同 TRX 的 3 项 `OfflineFontRenderingTests` | SC600 字形实际落到 SC Medium | 不允许改成接受 500；派生字体中文 preferred family 缺失正独立修复 |
| `ci546-continuous/result.json`（本地 `ci546/continuous`） | `practice-model-idle` / `XamlParseException` | 实际普通、直接与自有编辑链通过；教程同类模板错误，未算整链通过 |
| `ci546-windows-job.log` | `MotionPreferenceTests` format / distribution runtime roots | 官方格式待修；根集合漏新 fonts，逐文件字体锁验证仍保留 |
| `ci546-123432833900.log` | Windows Index 正式 pack / fontTools import | 独立 job 漏构建依赖已审补；Linux Index 同轮成功，不是模型推理失败 |
| `ci545/contracts/*10_30_54*.trx` | `RegenerateKeepPreservesTheDraftAndExplicitReplacementRunsOnlyOnce(mouse:true)`，第二菜单项 `before-pointer-move` | 命中 Terminal 而非 popup，未注入第二点击；后来几何不是原始同帧证据 |
| `ci545/windows/browser-cooperation/result.json` | `direct-selection / wait-public-command-ready` | 公开就绪等待失败；旧几何与自动预览/按需设置已通过 |
| `ci545/windows/ordinary-notepad/result.json` | `automatic-accept-and-undo / trusted-source-input-required` | 未尝试；后续 edit-return 依赖未运行，不是已接受 |
| `ci544/gtk4-early/gtk3-glossary-{result,clicks}.json` | `textview / ValidTranslation / save` | 20 例后固定点 hover 失败，控件已从旧矩形移走，未点击 |
| `ci544/gtk4-early/gtk4-glossary-result.json` | 40 个 glossary case | 全部通过；不属于当前待修项，不代表模型质量放行 |
| `ci544/linux/linux/gtk-desktop/{inside.log,failure.png}` | `set_session_keys` 后 `edit_and_accept → enter_source / clear-source` | 设置脏关闭模态仍前台，源失焦正确拒绝 |
| `ci544/linux/linux/gtk4-desktop/gtk-reentry-language-options-unchanged-click.json` | 自有语言 popup 首次呈现 | 首帧前 unmapped，未 paint；源仍聚焦，原因待证 |
| `ci544/linux/linux/qt-desktop/{qt-motion-reverse.json,qt-overlay-clicks.jsonl}` | 第二次展开打断 collapse | 初展通过，第二 click 实际命中待新有界观测；末高 167 而非 360 |

## 当前负责人及下一动作

| 责任线 | 下一动作 | 完成依据 |
| --- | --- | --- |
| 集成 | 发布经审查的精确源码修补并跟踪双端 CI | 逐项结果、真实包和独立审查 |
| Windows UI | 已冻结语言页等待 CI546 真实截图，同时独立实现术语行内编辑整页；菜单就绪修补等待同头实际回归 | 新版实际操作与像素对照，不以编译替代 |
| Linux UI | 已验证 CSS 隔离、真实搜索选择及写盘失败恢复；定位初始 popup 延后失焦，保留负控，再进入术语页 | 实际 GTK/AT-SPI，再跑模型与第三方应用路径 |
| 合作浮层 | 独立组件字体包装和 GTK3 原固定点点击合同已修待 CI；继续 GTK4 几何验收 | 同平台有界几何原件及真实打包路径，不用资源探针冒充完整合作运行 |
| 共享模型能力 | 额外保义提示经两份独立评审无有效提升，已退役；术语专用 feature-delta 验证已按冻结方案单次运行：10 源 20 翻译完成，第 21 次 G07 普通请求因结构未保留停止，后续 19 次未运行；原件保留。获准仅续未执行项的新会话在 G07 术语请求触发原 60 秒超时后终止；两会话合计 22 个唯一请求，20 译文、1 结构拒绝、1 超时，余 18 项未执行。无重试、无质量放行；诊断已确认 greedy token 边界吸入输出句号造成保护串误拒，窄源锚定后处理已真实原件回放 RED→GREEN，独立审查及全 worker 207 项通过；当前新身份仅改返回校验，固定 G07–G16 共 20 次实际验证已终态：18 译文、G08 两次安全结构拒绝，118.485 秒且 worker 正常退出；G07 两模式真实结构成功，独立 feature-delta 评审与精确执行 profile 绑定待收口，旧失败保留，原新会话超时仍未解释，禁止自动重试 | 与未改普通基线比较功能收益和重大退化；不是质量放行，换种说法单列后续验收 |
| 离线字体 | 官方 CJK 与统一字体包的生产准备已成功，正在接双端与合作模块实际字体选择 | 包内离线、正确族名/字重、真实渲染；CJK 和 Source Serif 500 是本次未完成义务，不是允许留空的未来功能 |
| 独立审查 | 核对测试迁移与浏览器资格失败观测 | 不削弱写回、IME、身份、期限或取消合同 |

浏览器菜单新增已复现的 IME 可用性修补：同一显示期限内，Unknown composition 后定位 tick 错用动作资格关闭菜单，而非 Escape 被消费。分开只读显示资格与动作资格后，6 项真实 Chromium 回归通过；自然、换种说法、复制、退出在 Unknown 期间仍拒绝动作且保留菜单。完整80项回归为78通过、2失败：两项原委托问题本轮通过，新的短permit写入/viewport主浮层退休失败分别保留；单次只读focused取证两项通过而未复现，不抹原失败。先前metadata全量失败仍未完整归因。

术语执行身份绑定正在实现审核：普通 Ready 与高级 gate 分离，以固定行为 profile 加既有包完整性验证，不要求各平台二进制逐字一致。JSON 字符串合法转义导致的 known-profile 正控误拒已针对性修复，15 项回归通过；真实正式包链尚未验证、Current 仍空，不能称术语已开放。

共享三态动效存储/协议与语言双语呈现 helper 已通过针对性和运行时合同；各端消费者仍在整合，旧 bool UI 必须显式转换，`full` 必须覆盖系统减少动效。完整三态外观页尚未验收，不能单发存储变化造成开关无效。

## 接下来必须完成

- 过期重译确认、快捷键强制保存、撤销、术语行和引导旧页已添加逐次交互身份与版本校验；复核旧按钮、关闭后重开、新草稿和重复操作的最终平台回归，Windows 实际运行仍待 CI。
- 发布修补候选到同一开发分支，检查该精确提交的双端 CI；失败按实际日志修复，不降低断言。
- 对照原型检查同偏好的浅深色设置、托盘、教程、浮层和中断交互；截图必须确认实际目标窗口，教程被其它窗口遮挡的图不算通过；Windows 真实桌面与目标 GTK/Qt 模块各自保留独立验收证据。
- 质量尚未满足的术语应用、自然/正式风格和换种说法明确列为未完成，不以只做前端替代已批准范围。

## 持续约束

输入来源、焦点身份、IME 状态、租约、接受资格、写回与 Undo 安全条件不因界面重构而放宽。耗时敏感的桌面/浏览器验收与重 CPU 构建、模型采集统一排程；模型窗口仅在采集器就绪且其他动态作业确认结束后开始，等待准备期间继续静态实现和审查。物理按键、真实 IME、UIAccess 和第三方应用需要各自证据，软件注入不替代可信来源接受。新空库存默认 Index，保留已有 Argos/Hy、显式选择及方向；不自动下载或替换，修复失败保旧代。

已发布字体为随包 OFL Inter 与 Source Serif 4。未发布的统一离线 CJK 包准备与包装回归已通过，WPF 消费已编译、GTK 实际字形已验证；浏览器六组区域及字族实际字形与三态动效针对合同已验证；最新完整 80 项通过 79 项、菜单在 resize 观察结束时已脱离一项失败，已以只读证据确认测试 RAF/performance 混时基导致观察窗拉长，统一 performance 时基后针对例通过，待修后全量。合作模块实际字形、Source Serif 500 与光学尺寸仍待完成。模型权重不进入源码或产品运行包。当前未更新稳定版、`main` 或既有体验推荐。
