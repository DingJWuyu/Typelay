# VS Code 编辑事务验证

当前目录 `src/TypeLay.Linux/vscode/` 是可由真实 Extension Host 加载的开发验证扩展。没有命令注册、激活事件、模型进程或网络调用，不包含在默认 Linux 安装流程内，不向用户编辑器永久安装。它不是可用的 TypeLay 翻译后端。

## 验证范围

`EditorTransaction` 保存原始 TextEditor 和 TextDocument 对象、文档版本、UTF-16 选区与正文。不同编辑器、关闭文档、修改正文、改变选区、失去窗口焦点均拒绝旧结果。只有 `plaintext` / `markdown` 的本地文件或未保存文档允许进入此实验；没有选区必须由调用者明确选择整文。传给未来模型入口的快照只含已选择范围，不包含文件名、路径或范围外正文。整个源文档仍受 Core 的 8192 UTF-16 单位限制，不截断。

编辑使用 `TextEditor.edit`，保持前后 undo stop，成功可以一次撤销。版本比较后的编辑请求携带原文档版本，由 VS Code renderer 在应用时再次拒绝过期编辑；异常视为结果未知，不重试、不剪贴板粘贴、不补偿回写。选区/窗口事件在 Extension Host 异步到达，因此本地校验不能证明 renderer 当前焦点或输入法状态。

VS Code 公开扩展 API 未提供 IME composition 生命周期或同步的物理 Tab 接受仲裁。文档版本和内容事件不能证明输入法空闲。这个原型没有声明 Clear、没有自动捕获源字段，也没有实现胶囊、人工译文编辑或 Tab 接受。特别不能据此声称 Fcitx5 5.1.22 的组合输入已经安全支持。命令式快照工作流不同于冻结产品交互，交付前需用户明确选择。

## 真实 Extension Host 回归

先准备官方 VS Code 或发行版 Code OSS 和独立显示环境。以下命令创建临时 HOME、用户数据和扩展目录，不读取现有工作区；退出时删除临时目录并清理自有进程组：

```sh
DISPLAY=:99 python tests/linux/vscode_e2e.py --code /usr/bin/code
```

测试覆盖选区范围外文本、UTF-16 emoji、显式整文、一次 undo/redo、旧版本拒绝、选区变化、编辑器切换、文档关闭、重复接受、并发 renderer 版本拒绝、代码区与超长文档拒绝。未使用编辑器对象替身或翻译模型替身。缺少 Code 可执行文件、显示环境或运行超时是失败，不能跳过为通过。

这些回归只验证编辑事务，不能证明真实模型、niri 聚焦、物理中文输入法或产品视觉交互。当前可调用的翻译集成、命令 UI、模型生命周期和包安装均尚未提供。

## API 与复用边界

实现使用 [VS Code 的 TextEditor.edit 与 TextEditorEdit 公共 API](https://code.visualstudio.com/api/references/vscode-api#TextEditor)。[上游扩展端](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/api/common/extHostTextEditor.ts)保存 documentVersionId，[主线程编辑器](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/api/browser/mainThreadEditor.ts)负责核验与应用。

[Microsoft 扩展示例](https://github.com/microsoft/vscode-extension-samples)与 [VS Code 源码](https://github.com/microsoft/vscode)采用 MIT 许可。此实现只调用公开 API，没有复制上游代码；没有引入第三方翻译插件、远程翻译服务或额外运行时依赖。
