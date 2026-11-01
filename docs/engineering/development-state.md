# 当前开发状态与安排

本页只记录当前项目状态；要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准。

## 当前身份与范围

截至 **2026-11-01**，唯一开发发布分支为 `work/linux-usability`。恢复时已从 GitHub 回读并重新 clone 精确 [`8d3cd433faf5aa9f4897fdaf85271544a9522e65`](https://github.com/DingJWuyu/Typelay/commit/8d3cd433faf5aa9f4897fdaf85271544a9522e65)，已提交源码、原型和技能可读。`main` 仍为 `09fe613a606073876483b51446211030a66a9597`，`trunk` 仍为 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

用户已确认待发布 31 文件范围，但原工作区及未发布原件在本次恢复时不可读。旧本地 tree `8cac726a`、Browser 后继 `2289adb8`、Qt 后继 `61784ed9` 的源码、补丁与验证日志均未在当前可访问工作区找到；精确 8cac726a GitHub tree 查询为 404。不能从记忆重造原件或继承通过。当前从 8d3 重建同范围的成熟子集，使用新源码身份、新审查与新检查；本地已合 16 个代码/测试文件，另含必要现役文档，尚无本次远端写入。后继 Browser 完整确认、Qt 搜索壳及 S09 命名修不混入本次原 31 范围。

实现范围不变：原型 I–III 的真实用户流程与界面；IV 的 N06–N11 仅真实禁用前端和[能力缺口](../design/implementation-gaps.md)；V 的模拟桌面和定时译文不做。既有同语、旧模型、源选择、撤销与全部输入安全条件保持。没有两小时截止，不以单批 CI 代替完整交付。

## 当前可核证据与工具

- [CI572](https://github.com/DingJWuyu/Typelay/actions/runs/41157421806) 精确对应 8d3，已终态 failure；scope/shared 与双端 Index 生命周期通过，Windows/Linux 主检查失败，Qt iteration 按范围跳过。没有新 CI。
- 已重新下载并核官方 SHA 后安全解压 CI572 Windows contracts/source 与 Linux UI/desktop 四份小 artifact；原件在 ignored `artifacts/recovery/ci572/`。Windows 完整 artifact 360361246 字节也已核 SHA 恢复。Linux 完整 artifact 为 702105114 字节，既有 512MiB 工具拒绝不重试或换路线。
- 当前没有可用 DISPLAY/WAYLAND_DISPLAY。系统 Python 可导入 GTK3/Gdk/Atk/cairo，但不等于真实桌面可执行；旧 Xorg/Chromium socket 与系统安装拒绝不因环境替换绕过。Qt/GTK4 SDK 与真实 WPF/Browser 行为仍需目标平台。
- 私有 .NET SDK 10.0.401 已按 global.json 从官方元数据指定 URL 下载并核 SHA512 后解压；Ruff 0.14.0/fontTools 4.61.1 私有环境已按锁恢复；32 面字体从该 CI 正式包提取后通过现役字体锁验证，真实 native DLL 32768 字节亦重新提取核 SHA `b9ce5e64`。这仅是构建输入，不是本机 native/WPF 通过。新检查须记录实际输入与退出结果。
- 未发布候选只保存本地；不承诺跨环境持久备份。当前不上传到额外存储目标。

## CI572 已恢复的失败依据

重新解析 contracts TRX：App metadata total 1444，实际执行 1422、通过 1417、失败 5；Runtime 1520、Core 310、Protocol 28、Windows.Contracts 211、独立两项均通过。这些是已发布 8d3 的实际结果，不是重建候选通过。

| 场景 | 已有事实与边界 | 当前动作 |
| --- | --- | --- |
| Onboarding | 原公开回归 Failed；Hide 后 native 不可见且目标 active，旧 Show 返回后窗口 native visible/active 又为 true，WPF IsVisible 仍 false | 候选只阻退休 hidden opening 的旧原生提交：提交前撤销 SHOWWINDOW 并用公开 Hide 同步 WPF 状态；保 nested/inherited Show、属性策略、取消/Close/异常，不事后 Hide 或抢焦掩盖 |
| Queue 两分支 | 初始 WM_GETTEXTLENGTH 100ms/1460，旧诊断标签 exact-install-not-flushed；标签不能证明未 flush，更不是已证下游 Tab/写入故障 | 新候选先保原失败/四元组/时刻，再在原 25s 总界内至多 1s 只读同 trace；区分缺文件/无精确完整记录/未完尾，不重试输入或续票 |
| FirstWake | True 总链 157ms 超原 150ms而安全拒绝 | 不预热、延时或删身份门；分段证据不足仍未关闭 |
| Appearance | 第二选择测试的人工中段采样前置未建立 | 核实际已观察非端点再触发第二操作，保连续性、目标收敛和原 200ms/总界，不称修产品动画 |
| Settings | 实图录制 Ctrl+C 成为 Ctrl+Shift+C；client 截图全透明而 owned compositor 图可见 | 仅修公共 helper 字母 keysym 映射及复用既有 owned 九状态采集；真实键投递/九图待验 |
| GIMP | 末尾 Disconnect 实图已可见，公共 focus/fullclip 仍失败 | 复用原观察补失败谓词，不把本头称旧 1×1 展开问题，不放宽门 |
| GTK4 | 主浮层首 paint 前退出，子件零 allocation | 缺当时最小尺寸证据，不猜修、不额外 present/主动 measure 冒因果 |
| Qt6 | compact 440×89/editor 412×39 已正确，editing 主题后166与目标167且未 settled | 保原几何与动画预算；必要时复用 motion trace 区分未静止与真实布局差异 |

本头生产 Browser 八场、Linux Browser/GTK3 与 Tutorial 的已提交路径通过，Notepad 两项可信物理输入依赖未运行；这些边界按 job 与恢复后的结构化原件核对，不外推新 Browser 确认或真实写入资格。

## 当前责任线与下一动作

所有 Agent 目标配置为 `gpt-6-astra` / `xhigh`；派发工具只确认任务标识，实际模型/强度未独立核实。作者只写各自 ignored 候选，集成唯一修改 live、现役文档和发布。

| 责任线 / owner | 当前动作 | 完成依据与停止条件 |
| --- | --- | --- |
| 集成 / resume_typelay_ui_delivery | 恢复原件/工具；编排同原 31 范围新候选、独立审查与适用并集；发布前核新树差量与授权范围、回读远端防覆盖 | 每份源码身份对应实际检查，验证 blob/tree/commit/ref 后跟精确 CI；拒绝即停该动作，不换路线 |
| Onboarding / rebuild_onboarding | 新四文件已独立 Standards/Spec 通过；locked restore/托管 build 零警告错误，64.65s 无漂/owned 空。folder 原五处 whitespace 纯布局修正，独立反向逐字重建编译输入，当前 folder exit 0 | 原失败与 nested/inherited/Close/异常/策略公开回归；编译不替代真 WPF |
| owned Replace / rebuild_owned_replace | 从现 Keep/真实 Tab/fresh Core/Bind 路径重建专用一次意图；新 Runtime 公开 RED 在完整绑定后 Jobs 1≠2，30.906s、974 输入无漂/owned 空；同一测试新最小 GREEN 1/1，9.421s 无漂/owned 空；Windows 接线/负控在隔离候选，不混入成熟子集 | 原操作起 500ms 不续，与接受/写回分离；合法未受理失败保稿，明确失权按 Keep 退休；公开 Runtime 与真实 WPF 分层 |
| Linux 消费者 / rebuild_linux_evidence | 字母映射受控 11 组新 RED→GREEN，正式导入缺 Atspi 单列；透明/坏 PNG 新 RED 后截图 helper 10 项通过（真实解码、非桌面），原测试 GI 加载失败也保留；focus 诊断 RED 已取得，最终六套 56 tests 全过、输入无漂/owned 空；双轴独审通过 | 同公开 helper RED→GREEN、邻近检查；受控导入不能冒真实 GTK/键投递/截图 |
| Qt 菜单 / rebuild_qt_menus | 新五文件 v2 静态/header/Ruff 通过、双轴独审通过；修正审查发现的真实 menuOnly 提前返回遗漏退休，公开 fixture 补同 revision 动作更新后关闭/重开，未运行 Qt GUI | 沿 operation 更新真实 fixture/desktop 消费者；无 SDK/GUI时明确未编译/未验，不混入后继搜索协议 |
| 独立 Standards / rebuild_standards | 成熟 16 文件各固定源审查均无剩余阻塞；Onboarding 纯布局与 Qt v2 另核 delta，owned 后续独立接续 | 不继承丢失候选的审查结论 |
| Root / Spec与advisor | 用户沟通、产品边界与新候选独立 Spec；持续核原型余项 | 真实用户收益、原门不放宽，非仅测试列表完成 |

当前成熟子集为 Onboarding 4 文件、Qt 菜单 5 文件、Linux 测试消费者 5 文件、Appearance 触发前置 1 文件、Queue 失败取证 1 文件。Appearance 保原 200ms 与所有后置，仅由实际已画非端点触发第二选择，不能称产品动画根因已修。成熟并集现已实际完成 locked restore、App.Tests Release 托管构建（0 警告/错误）与六个受影响 C# 文件 folder-only，三项 exit 0、50.04s、870 非文档输入前后无漂、owned 清理为空。它不替代 WPF、Qt/GTK、真实键投递或新九图验收。原 31 范围内尚未完整闭合的 owned Replace 留独立候选，不追加半链或后继 Browser/Qt 搜索功能。

所有重 CPU/动态检查统一单 owner：READY 后 fresh GO 才 START，终态先 END 与 owned 清理再分析；记录命令、cwd、输入、session、期限和 exit。不确定结果先核，不盲重跑。

## 仍未关闭的产品边界

完整双端逐页/状态验收未完成。owned 完整 Replace、Browser 全键盘确认、Qt/GTK4/GTK3 O04 搜索及实际配置链、S08 材质/中间组合像素、跨页 Full 动效仍有缺口。后继 Browser/Qt 旧实现原件当前缺失，只能按实际基线重建并重验，不能称仍已本地交付。

Index 仍默认，不扩模型。固定 Index profile 的中英双向 Glossary 发布行已在已提交源码中；风格/换种说法先前有界裁决未达标准，仍是未完成产品能力，不归 N06–N11 前端例外，也不重启优化研究。旧模型原始证据缺失，不声称重新评审。

输入来源、IME、焦点身份、接受资格、写回和 Undo 不因界面重构放宽；原 down+500ms、FirstWake 150ms 等预算保持。字体只按锁定来源校验准备，产品离线；安全退役和明确材质回退不能冒充原型完整呈现。
