# 应用接入与练习

用户在应用支持页检查目标应用、明确连接，并在实际输入框或教程练习中确认翻译。模型就绪和应用连接是两件事。

## Sub-features

- `applications/manual-gtk` 经正式合作入口启动普通 GTK 应用。
- `applications/managed` 通过应用页选择已注册应用、打开和结束本次连接。
- `applications/practice` 完成首次配置并打开真实练习输入框。
- `applications/extensions` 在浏览器或 VS Code 中明确连接扩展并验证相应交互。

## How to get to it (user POV)

- 在设置的“应用”页检查连接，选择受管应用或练习。
- 从首次配置进入“试一试”，或运行 `typelay --tutorial`。
- 高级使用者通过合作包的 daemon 和 launch 入口启动 GTK/Qt 应用。
- 浏览器从扩展页加载随包扩展并批准连接。VS Code 从 VSIX 安装后手动选择文字翻译。

## Driving it with private niri

Preconditions: 应用和输入法都位于本次私有会话，合作包预检通过。所有模型安装都消费私有会话已复制的下载缓存，默认离线，缺项时报告失败。受管入口和高级手动入口用不同运行，不能同时占用一个 Fcitx 总线名称。

- **手动 GTK。** Launch 的 `gtk-preview.py` 先运行合作包 `preflight`，再启动 `daemon`，最后通过 `launch` 打开 TypeLay 与 `gtk3-demo`。观察真实字段 `sourceSupported=true`、`nativePeers=1` 及译文。该路径证明高级手动接入。
- **应用页。** 另开全新私有会话，执行 `"$TYPELAY_VERIFY_PACKAGE/typelay" --settings`，通过 `select_settings_page(pid, "应用")` 选择实际页面。在私有会话没有原输入法的前提下，沿界面确认没有原输入法，再选择已注册应用并打开。观察连接状态与该应用实际输入框，结束连接后检查自有进程和回执。这里不预先运行手动 daemon。
- **教程。** 执行 `"$TYPELAY_VERIFY_PACKAGE/typelay" --tutorial`，沿欢迎、语言选择、下载、本地试译与练习逐页操作。保留各页截图和真正输入、接受后的进度，不能仅打开教程就记录完成。
- **分开运行教程与设置。** 准备独立正式模型库存后，既有 `native_e2e.py --inside --desktop --desktop-scope setup-tutorial` 和 `--desktop-scope setup-settings` 分别验证各自链。为每次提供私有 `--package`、`--native "$PWD/artifacts/linux/native"`、新建的 `--output` 目录和私有 DISPLAY。分别用 `timeout --kill-after=10s 240s` 和 `timeout --kill-after=10s 360s` 包住命令，保留原有预算并逐项读取结果。两条链独立运行，任何一条失败都保留原件，不能算另一条已通过。
- **扩展路径。** 按 `docs/platforms/linux/browser.md` 或 `vscode.md` 的扩展前提准备一次性浏览器/编辑器 profile。把对应真实扩展测试命令放入本技能的私有旅程脚本，所有浏览器、编辑器和其输入均留在私有显示中。浏览器要观察真实网页字段；VS Code 要观察明确选择、手动预览和确认替换。

## Gotchas

- 系统未修改的 Fcitx 不提供合作能力。服务名称相同或模型 Ready 不能替代实际接入状态。
- 空 HOME 不包含用户安装的浏览器扩展、编辑器配置和已选应用。缺少这些前提的旅程应报告失败原因，不读取用户现用 profile 补齐。
- 本技能基础包不包含可选 Qt 插件。Qt 需要同版 Qt 6.11.2 和正式插件构建，按 [Qt 入口](qt.md)分别验证交互和语言搜索。
- VS Code 当前提供手动翻译，不提供自动浮层、Tab 接受或 F10 直接替换。
- 普通 GTK 成功不等于受管应用、教程或扩展入口通过。Windows 只读取 Actions 证据。
