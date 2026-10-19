# VS Code 可选本机翻译命令

这是额外的手动工作流，不替代 TypeLay 自动浮层、Tab 接受或普通输入框目标。扩展只处理本地 `plaintext` / `markdown` 文档。代码、终端、远程/虚拟文档、多个选区和未受信任工作区不启用正文读取。

## 手动安装和使用

先安装 Linux TypeLay 及本地模型。在 VS Code 的扩展视图中选择“从 VSIX 安装”，手动选择发行包提供的 `vscode/typelay-editor.vsix`。TypeLay 的普通安装不会替你安装或启用编辑器扩展。

在 VS Code 用户设置中配置 `typelay.hostPath`，值为已安装 TypeLay 启动器的绝对路径，例如 `/home/你的用户名/.local/bin/typelay`。请以实际安装位置为准，不使用 shell 命令、参数或 `~` 缩写。该设置为机器级；扩展仅使用用户配置，工作区和文件夹无法指定执行程序。

1. 先完成输入法候选，再选择要翻译的文字。
2. 运行命令“TypeLay: 翻译所选文字（先完成输入法候选）”。没有选区会提示选择，不会悄悄翻译整篇。
3. 等待本机推理，核对预览中的完整原文和译文。
4. 点击“确认替换”才写回。取消不改原文；成功后可用编辑器的一次撤销恢复。

如需整篇，明确运行“TypeLay: 翻译整篇文档”，先确认整篇范围，再预览和确认译文。已有选区不会改变这个明确整篇命令的范围。扩展不自动发送消息、不保存文件、不设置默认快捷键，也不占用 Tab。当前预览只供核对；需要调整译文时可取消或替换后在编辑器内继续修改。

翻译使用现有 TypeLay 的启用状态、语言方向和已安装模型。关闭 TypeLay 全局启用或模型未就绪时拒绝请求，不自动下载组件。推理进度通知可以取消；源文档、选区、编辑器或窗口焦点发生变化时旧结果作废，请重新翻译。

## 隐私和写回边界

扩展只在明确命令后读取允许的文档。内部保存原始 TextEditor、TextDocument 对象、版本、UTF-16 选区和正文用于核验；传给本地主机的只有选中范围、随机身份、版本和范围，不包含文件路径或范围外文本。整篇源文档上限为 8192 UTF-16 单位，超限拒绝，不截断。

扩展以参数数组、无 shell 的方式启动已配置的 `typelay --editor-host`，通过继承的有界标准输入/输出处理一次请求。桌面已运行时，仅将此显式 `translateEditorSnapshot` 快照经既有同 UID 私有本机 Unix socket 交给唯一 serving worker；此操作含选中正文，其它控制操作仍只传元数据。没有互联网服务、剪贴板、远程翻译或正文日志。超时、取消或对端断开立即撤销本次结果，已派发计算沿原期限收束，不强停共享 worker，也不自动重试。桌面不存在时才持启动锁运行受监督 one-shot，锁保留到真实进程族退出证明；控制连接失败不能回退另起模型。

确认时使用 `TextEditor.edit` 的文档版本事务，保留前后 undo stop。异常视为结果未知，不自动重试或补偿回写。选区和焦点观察在 Extension Host 异步到达，因此不能宣称与同步物理按键仲裁等价。

VS Code 的公开扩展 API 不提供 IME composition 生命周期。文档版本不是输入法空闲证明；Command Palette 也不是可靠提交屏障，上游在缺失原生 compositionend 的 blur 路径会人工结束内部状态。请先提交输入法候选并核对预览原文，再确认替换。这条手动命令不能证明 Fcitx5 组合输入、自动胶囊或物理 Tab 已安全支持。

## 自动路径尚缺的宿主能力

这里的限制来自编辑器扩展接口，不是 Wayland 的窗口访问限制：

- 文档、选区、身份及版本观察已经可用，`TextEditor.edit` 也会在 renderer 应用编辑前核验文档版本。缺口不是普通并发改文没有版本事务，而是同一次操作没有包含当前选区、焦点及 IME 证明。
- renderer 在 `compositionupdate` 期间就会向模型发送 `type`/`compositionType`，所以文档变化加 debounce 不能证明观察到的是已提交正文。内部确有 [`isComposing` 上下文键](https://github.com/microsoft/vscode/blob/main/src/vs/editor/common/editorContextKeys.ts)，可限制快捷键的 `when`，但公开扩展 API 没有与文档版本绑定的组合事件或已提交范围查询。
- 自定义 Tab 命令经过 Extension Host 异步状态。按键匹配后，[其余绑定不再处理](https://code.visualstudio.com/docs/configure/keybindings#_keyboard-rules)；之后编辑被拒绝不能把原键完整交还原补全、缩进或焦点行为。不能通过补发另一个 `tab` 命令冒充原按键。
- [InlineCompletionItem](https://code.visualstudio.com/api/references/vscode-api#InlineCompletionItem) 可以使用宿主的展示和接受机制，但稳定接口的替换范围限于单行；宿主 Tab 接受还要求无选区，不能覆盖现规格的跨行选区或整段替换。Decoration 不是可编辑胶囊，Webview 在独立视图中；公开 TextEditor 没有光标屏幕矩形，扩展也[不能访问 VS Code DOM](https://code.visualstudio.com/api/extension-capabilities/overview#restrictions)。

因此，自动预览或 inline completion 是可能的另一种交互，但不能直接记为当前自动胶囊/Tab 规格完成。闭合当前路径需要 renderer 侧提供组合状态、布局锚点和按键时同步核验/执行的合作入口，再接现有共享会话。当前普通 VSIX 没有这项集成；也不能将此结论外推成所有 Electron 应用都不可能支持。

## 开发回归

使用官方 VS Code 原生二进制，或发行版拆分的 Electron + Code 应用目录。`--code` 必须是真实 ELF，而非 `/usr/bin/code` CLI 包装器；Arch 的 Electron 版本目录按实际安装确认。容器应给字体共享内存足够容量，例如 1 GiB `/dev/shm`。

```sh
DISPLAY=:99 python tests/linux/vscode_e2e.py \
  --code /usr/lib/electron42/electron --app /usr/lib/code
```

该回归使用真实 Extension Host，覆盖 UTF-16 范围、显式整篇、一次 undo/redo、旧版本、选区改变、编辑器切换、文档关闭、重复接受、renderer 并发版本拒绝与不支持区域。没有编辑器对象替身。

验证完整发行 VSIX、本机模型和实际确认/取消对话框时，额外提供测试发行包和已经安装好模型的测试数据目录：

```sh
DISPLAY=:99 python tests/linux/vscode_e2e.py \
  --code /usr/lib/electron42/electron --app /usr/lib/code \
  --vsix /绝对路径/typelay-editor.vsix --code-cli /usr/bin/code \
  --host /绝对路径/发行包/typelay \
  --model-data /绝对路径/测试数据/typelay
```

测试只往临时 HOME、用户数据和扩展目录安装 VSIX，模型 components 只链接到已准备的测试数据。Playwright 通过仅供该测试使用的本机调试端口操作真实预览按钮，不替换消息 API 或模型。完整 UI 模式启动普通 Code，不启用 extensionTestsPath 或扩展开发模式，因为 VS Code 的 DialogService 明确禁止扩展测试模式弹出模态提示。它通过命令面板触发已安装扩展，并检查临时文件的实际保存、一次撤销和取消结果；独立 Extension Host 回归保留版本竞争等事务检查。日志、实际 UI 截图和 `test-result.json` 进入 `artifacts/linux/vscode/`。只有真实运行全部断言后产生的 `passed` 被接受；缺少模型、未启动、超时或测试失败都不算通过。结束后清理临时 profile 和自有进程。

这些检查仍不代表物理中文输入法、niri 多窗口体验或自动浮层产品验收。

## API 依据

- [VS Code TextEditor 与 TextEditorEdit](https://code.visualstudio.com/api/references/vscode-api#TextEditor)提供版本约束编辑和 undo stop。
- [扩展端版本绑定](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/api/common/extHostTextEditor.ts)与[主线程应用](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/api/browser/mainThreadEditor.ts)说明版本事务边界。
- [原生文本区组合状态处理](https://github.com/microsoft/vscode/blob/main/src/vs/editor/browser/controller/editContext/textArea/textAreaEditContextInput.ts)说明 blur 不能当作正常输入法提交证明。
- [DialogService 的扩展测试模式限制](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/services/dialogs/common/dialogService.ts)要求实际模态 UI 回归使用普通启动。
- [官方 vsce 打包实现](https://github.com/microsoft/vscode-vsce/blob/main/src/package.ts)定义 VSIX manifest 和内容类型。

Microsoft 的 VS Code、扩展示例和 vsce 源码采用 MIT 许可。此实现调用公开 API，没有复制上游实现或引入第三方翻译插件。
