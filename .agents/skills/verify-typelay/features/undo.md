# Linux 浏览器原生 Undo

## Sub-features

`writeback/browser-undo-index` 是必过旅程，覆盖真实 Index 替换和宿主 Undo/Redo。

## How to get to it (user POV)

用户在设置的通用页开启“原生输入框替换”。在 Linux 的 Tiptap 编辑器选中 `hello`，看到真实 Index 候选后按默认 Tab 接受，再按宿主的 `Ctrl+Z` 恢复原文。`Ctrl+Shift+Z` 重新应用同一替换。

## Driving it with private niri

先按[构建指南](../references/build.md)准备当前源码的完整包、合作包和 Index 下载缓存。桌面解释器与 Chromium 的选择遵循[网页旅程](writeback.md#driving-it-with-private-niri)。Tiptap 使用现有锁文件，离线准备真实编辑器资源：

```bash
mkdir -p artifacts/editor-probe
cp tests/linux/fixtures/editor/package*.json artifacts/editor-probe/
unshare --user --map-current-user --net npm ci --offline \
  --prefix artifacts/editor-probe --ignore-scripts --no-audit --no-fund
cp tests/linux/fixtures/editor/entry.js artifacts/editor-probe/
unshare --user --map-current-user --net node \
  artifacts/editor-probe/node_modules/esbuild/bin/esbuild \
  artifacts/editor-probe/entry.js --bundle --format=iife \
  --outfile=artifacts/editor-probe/fixture.js
```

缓存缺失时保留 npm 错误并报告准确包名，停止该步骤。准备成功后，通过唯一私有会话入口执行：

```bash
/usr/bin/python3 "$PWD/.agents/skills/verify-typelay/scripts/private-session.py" \
  --package artifacts/verification-build/package \
  --cooperative artifacts/verification-build/cooperation/TypeLay-Fcitx-cooperative \
  --output "$PWD/artifacts/verification/browser-undo-index-$(date +%Y%m%d-%H%M%S)" \
  -- bash -c 'exec /usr/bin/python3 tests/linux/native_e2e.py \
    --inside --desktop --desktop-scope browser-undo-index \
    --package "$TYPELAY_VERIFY_PACKAGE" --native "$PWD/artifacts/linux/native" \
    --index-cache "$TYPELAY_VERIFY_MODEL_CACHE" --chromium /usr/bin/chromium \
    --editor-bundle "$PWD/artifacts/editor-probe/fixture.js" \
    --output "$TYPELAY_VERIFY_OUTPUT/browser-undo-index"'
```

每次重复都使用新的输出目录。runner 从空数据正式安装默认 Index，并记录包、模型 profile、浏览器版本与编辑器 bundle 摘要。它只预置中英方向、教程完成状态和默认自动模式，扩展仍使用正式注册与生产消息链。“原生输入框替换”从默认关闭状态经设置 UI 开启，`settings-writeback.png` 与 `settings-saved.json` 保存结果。

### 验收证据

检查 `browser-undo-index/result.json`，要求 `prepare-default`、`native-undo` 和 `nativeUndo` 均为 `passed`。逐项打开对应 JSON 与截图：

- `undo-before` 保存选区 8..13、原始 HTML 和可见候选。原文为 `Before hello world after untouched`，`hello world` 加粗，`untouched` 斜体，撤销与重做历史均为空。
- `undo-accepted` 保存实际 Index 替换。选区外文字和格式保持，宿主只有一个可撤销事务。`undo-receipt.json` 必须只有一次 Verified 终态。
- `undo-restored` 保存宿主 `Ctrl+Z` 后的正文、HTML 和选区。三者必须恢复原状态，历史变为零个撤销、一个重做。
- `undo-redone` 保存宿主 `Ctrl+Shift+Z` 后的相同替换。键盘事件必须可信、落在 `editor-source`，并且撤销与重做期间没有新增 TypeLay 写入回执。

逐句核对 `samples.json` 中 `hello` 的译文是否保留问候含义，检查截图的文字、裁剪和焦点。最后核对外层 `cleanup.json`，要求 `remainingPids=[]`、`scratchRemoved=true`、`runtimeRemoved=true`，上述原件仍存在。

## Gotchas

宿主是固定版本的真实 Tiptap/ProseMirror，页面与初始文档是受控 fixture。初始 HTML 和选区只在输入前由 fixture 建立；接受后只读取实际状态。Tab、Undo 和 Redo 都经私有 XTest 传入嵌套桌面，宿主自己的历史插件处理快捷键。DOM 的 `isTrusted=true` 只说明浏览器接受了这些注入事件，不证明物理可信输入。runner 不调用 `editorProbeHistory()`，也不重新输入或赋值原文。

该入口证明 Linux 合作浏览器、真实 Index 和真实编辑器原生事务。普通 textarea 的 `setRangeText`、GTK3 编辑分组、Windows、物理键盘与真实中文输入法各有独立边界，不能由此旅程替代。
