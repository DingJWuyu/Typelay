# 在 VS Code 中手动翻译文字

TypeLay 的 VS Code 扩展提供“选择文字，核对预览，确认替换”的本机工作流。它适合本地 `plaintext` 和 `markdown` 文档，不监听通用输入框，也不接管 Tab。自动浮层仍是另一条尚未完成的集成路径。

先安装 [Linux TypeLay](README.md)并准备本地模型。扩展只在本机 Linux 的受信任工作区中读取正文，支持本地文件与未保存文档。代码、终端、远程或虚拟文档、多个选区和未受信任工作区不启用此命令。

## 手动安装和使用

1. 在 VS Code 扩展视图选择“从 VSIX 安装”，加载发行包的 `vscode/typelay-editor.vsix`。TypeLay 普通安装不会代你启用编辑器扩展。
2. 在 VS Code 用户设置中填写 `typelay.hostPath`，值为已安装启动器的绝对路径，例如 `/home/你的用户名/.local/bin/typelay`。该值只是一条文件路径，不含参数或 `~`。扩展只读取用户级值，工作区和文件夹不能指定执行程序。
3. 先完成输入法候选，再选择要翻译的文字。整篇文档须在 8192 UTF-16 单元以内，即使只翻译一个选区也适用；超限会拒绝，不截断。
4. 运行“TypeLay: 翻译所选文字（先完成输入法候选）”。没有选区时，扩展提示你选择，不自动改成整篇翻译。
5. 等待本机推理，核对预览中的完整原文和译文，再点击“确认替换”。取消不改正文；成功替换后可用编辑器的一次撤销恢复。

需要整篇翻译时，明确运行“TypeLay: 翻译整篇文档”。扩展先确认整篇范围，再显示译文供你确认；已有选区不改变此命令的范围。

预览只供核对，不能在对话框中编辑译文。需要调整时，可以取消，或替换后在原编辑器中修改。扩展不自动保存文件、发送消息或设置默认快捷键。

翻译沿用 TypeLay 的启用状态、语言方向和已安装模型。全局禁用或模型未就绪时，命令会拒绝请求，不下载组件。推理通知支持取消。文档、选区、活动编辑器或窗口焦点变化都会使旧结果失效，需要重新发起翻译。

## 隐私和写回边界

扩展只在明确命令后捕获文档。它在内存中保留原 TextEditor、TextDocument、版本、选区和正文以供确认时核对；传给本地主机的只有选定范围的文字、随机身份、版本、范围及语言类型，不含文件路径或范围外正文。

扩展以参数数组启动 `typelay --editor-host`，不经 shell。一次请求通过有界标准输入输出传递。桌面已运行时，请求交给同用户私有 socket 中的唯一 serving worker；桌面不存在时，才在启动锁下运行受监督的一次性宿主。控制连接失败不会回退另起模型。该路径没有远程翻译、剪贴板传输或正文日志。

取消、超时或断连撤销本次结果。已经派发的共享推理按原期限结束，不强停共享 worker，也不自动重试。无桌面的一次性进程保留启动锁，直到确认其进程族退出。

确认时，扩展再次核对原文、选区、焦点与版本，然后调用 `TextEditor.edit`，并保留前后 undo stop。VS Code 会把文档版本带到实际应用编辑的一侧；选区和焦点通知仍经 Extension Host 异步到达，不能据此宣称物理按键时的同步仲裁。异常按 `OutcomeUnknown` 处理，不重试或补偿回写。版本绑定依据见 [TextEditor API](https://code.visualstudio.com/api/references/vscode-api#TextEditor)及[扩展端实现](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/api/common/extHostTextEditor.ts)。

VS Code 公开扩展 API 没有与文档版本绑定的输入法组合生命周期。文档版本变化、短暂停顿或打开命令面板都不是输入法空闲证明。因此使用前必须完成候选，并在确认时核对预览原文；这条手动命令不证明 Fcitx 组合输入或自动 Tab 接受已经支持。

## 自动路径尚缺的宿主能力

当前缺口在编辑器接口，不是单纯的 Wayland 窗口访问问题。文档身份和版本事务已经可用，但同次操作还缺少输入法、当前选区与焦点的同步证明。renderer 的组合输入可以先更新文档模型，因此文档事件加 debounce 不能区分已提交正文；内部 `isComposing` 上下文键也不等于公开的、与版本绑定的组合观察接口。

扩展还缺少按键失败后的原行为交还能力。自定义 Tab 命令经 Extension Host 异步处理，匹配成功后，其他键绑定不再执行；事后编辑拒绝时，再发一次 `tab` 命令不能恢复原按键的补全、缩进或焦点语义，见[快捷键规则](https://code.visualstudio.com/docs/configure/keybindings#_keyboard-rules)。

宿主的 inline completion 有原生展示和接受机制，但稳定接口的替换范围限于同一行，不能直接覆盖当前跨行选区或整段替换要求。Decoration 不提供可编辑胶囊，Webview 位于独立视图；公开 TextEditor 不提供光标屏幕矩形，扩展也不能访问 VS Code UI 的 DOM。依据见 [InlineCompletionItem](https://code.visualstudio.com/api/references/vscode-api#InlineCompletionItem)和[扩展限制](https://code.visualstudio.com/api/extension-capabilities/overview#restrictions)。

要实现现规格的自动路径，需要 renderer 提供组合状态、布局锚点及按键时同步核验与执行的合作入口，再接共享会话。当前普通 VSIX 没有这些能力；另一种自动预览交互也不能记为现规格完成。此结论不外推到所有 Electron 应用。

## 开发回归

使用真实 VS Code ELF 二进制。`--code` 不能指向 `/usr/bin/code` 这类 CLI 包装器；发行版拆分 Electron 与 Code 时，再提供 `--app`。以下路径只是 Arch 安装形式的例子，Electron 版本目录以实际安装为准。容器需要足够的字体共享内存，例如 1 GiB `/dev/shm`。

检查实际 Extension Host 的文档事务时运行：

```sh
DISPLAY=:99 python tests/linux/vscode_e2e.py \
  --code /usr/lib/electron42/electron --app /usr/lib/code
```

检查发行 VSIX、本地模型和真实确认/取消对话框时，提供实际包及已准备的模型目录：

```sh
DISPLAY=:99 python tests/linux/vscode_e2e.py \
  --code /usr/lib/electron42/electron --app /usr/lib/code \
  --vsix /绝对路径/typelay-editor.vsix --code-cli /usr/bin/code \
  --host /绝对路径/发行包/typelay \
  --model-data /绝对路径/测试数据/typelay
```

测试只在临时 HOME、用户数据和扩展目录安装 VSIX，模型 components 链接到已有测试数据。真实 Extension Host 回归覆盖 UTF-16、整篇范围、undo/redo、版本竞争、选区变化、关闭文档及重复接受。完整 UI 模式通过命令面板操作已安装扩展，由 Playwright 点击实际对话框，核对文件内容、撤销与取消；它不替换编辑器对象、消息 API 或模型。

完整 UI 模式使用普通启动。VS Code 的 [DialogService](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/services/dialogs/common/dialogService.ts)在扩展测试模式下会跳过或拒绝对话框，不能用该模式证明真实预览和确认流程。

日志、截图和 `test-result.json` 默认写入 `artifacts/linux/vscode/`。只有本次真实运行完成全部断言并报告 `passed` 才算通过；缺模型、超时、未启动和失败都保留原状态。脚本结束时清理自有进程和临时 profile。结果仍不代表物理中文输入法、niri 多窗口或自动浮层已验收。

<a id="api-依据"></a>

## 实现与 API 依据

命令及确认流程位于 `src/linux/vscode/extension.js`，文档身份和一次性替换位于 `src/linux/vscode/transaction.js`，进程通信位于 `src/linux/vscode/host-client.js`。本机请求校验和翻译由 `src/linux/TypeLay.Linux/EditorHost.cs`处理，回归入口是 `tests/linux/vscode_e2e.py`。

检查输入法假设时，查看 VS Code 的[文本区组合处理](https://github.com/microsoft/vscode/blob/main/src/vs/editor/browser/controller/editContext/textArea/textAreaEditContextInput.ts)；检查实际版本事务时，查看[主线程编辑实现](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/api/browser/mainThreadEditor.ts)。这些上游接口的变化需要重新核对，不能从一次扩展测试外推所有版本。

TypeLay 调用公开 API，没有复制第三方翻译插件。VS Code 和相关上游实现的 MIT 声明仍由各自源码保留；发行包中的许可处理见 Linux 开发指南，可从 [README 开发与验收导航](README.md#开发与验收)打开。
