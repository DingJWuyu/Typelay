namespace TypeLay.App;

// UI copy only. Source text, translated content and glossary terms never pass
// through this table; model identity and arbitrary diagnostics remain untouched.
internal static class UiStrings
{
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["取消"] = "Cancel", ["继续"] = "Continue", ["关闭"] = "Close", ["重试"] = "Retry", ["移除"] = "Remove", ["删除"] = "Delete",
        ["强制保存"] = "Save Anyway", ["继续编辑"] = "Keep Editing", ["放弃修改"] = "Discard Changes", ["重试保存"] = "Retry Save",
        ["设置已保存"] = "Settings saved", ["设置未保存"] = "Settings were not saved", ["已保存，请重新打开设置确认当前状态。"] = "Saved. Reopen Settings to confirm its current state.",
        ["未保存：请检查输入。草稿仍保留。"] = "Not saved. Check the value; your edit is retained.",
        ["请检查当前用户目录与组织策略。"] = "Check the current user's data directory and organization policy.",
        ["请选择支持的源语言和目标语言。"] = "Choose a supported source and target language.",
        ["设置已更新，请检查当前值后重试。"] = "Settings changed. Review the current values and retry.",
        ["设置保存未能确认。请检查设置目录权限或磁盘空间，再重新选择需要的设置。"] = "Save was not confirmed. Check folder permissions or free space, then choose the setting again.",
        ["模型尚未就绪"] = "Model not ready", ["本地翻译模型"] = "Local translation model", ["当前模型无法确认"] = "Current model could not be confirmed",
        ["尚未选择本地模型"] = "No local model selected", ["本地翻译尚未就绪"] = "Local translation is not ready", ["本地翻译已就绪"] = "Local translation is ready",
        ["本地翻译组件已就绪"] = "Local translation components are ready", ["本地翻译组件尚未准备"] = "Local translation components are not prepared",
        ["正在准备本地翻译组件…"] = "Preparing local translation…", ["正在启动本地翻译…"] = "Starting local translation…", ["正在暂停…"] = "Pausing…",
        ["当前模型不包含这个方向。请切换模型或调整语言。"] = "This model does not include the selected direction. Change the model or languages.",
        ["尚未准备本地翻译 · 默认 Index Translate 2B"] = "Local translation is not prepared · Default: Index Translate 2B",
        ["准备 Index 本地翻译…"] = "Prepare Index Translation…", ["确认后下载并使用 Index；已有用户的模型选择不会自动改变。"] = "Download and use Index after confirmation. Existing model selections are not changed automatically.",
        ["当前安装包缺少可用的 Index 运行组件，请使用完整安装包。旧模型仍可单独选择。"] = "This installation lacks usable Index runtime components. Use a complete package; existing models remain selectable.",
        ["本地组件安装记录无法读取"] = "Local installation records could not be read", ["未更改模型。请重新打开 TypeLay 检查安装记录。"] = "The model was not changed. Reopen TypeLay to check the installation.",
        ["暂不可操作"] = "Unavailable", ["请在下方选择修复"] = "Choose a repair action below", ["试一试"] = "Try It", ["选择接入方式"] = "Choose a Connection",
        ["准备翻译"] = "Prepare Translation", ["组件与安装说明"] = "Components and installation details", ["模型详情与许可"] = "Model details and license",
        ["模型许可证"] = "Model license", ["微软运行库说明"] = "Microsoft Runtime Information", ["重新准备"] = "Prepare Again", ["完整修复本地翻译…"] = "Repair Local Translation…",
        ["修复本地翻译组件…"] = "Repair Local Components…", ["补齐另一方向"] = "Add the Other Direction", ["下载并使用…"] = "Download and Use…",
        ["使用此模型"] = "Use This Model", ["修复…"] = "Repair…", ["移除…"] = "Remove…", ["补充中英方向…"] = "Add Chinese-English…",
        ["当前安装包不含可用的 Hy 运行组件"] = "This installation has no usable Hy runtime components", ["尚未安装"] = "Not installed", ["当前选择"] = "Current selection",
        ["已安装，未使用"] = "Installed, not selected", ["已安装"] = "Installed", ["不可用"] = "Unavailable", ["预览"] = "Preview",
        ["查看详情"] = "View Details", ["检查连接"] = "Check Connection", ["重新检查"] = "Check Again", ["选择接入组件"] = "Choose Connection Component",
        ["打开文件夹"] = "Open Folder", ["打开浏览器"] = "Open Browser", ["复制"] = "Copy", ["复制扩展 ID"] = "Copy Extension ID",
        ["桌面普通输入框"] = "Desktop text fields", ["VS Code 手动翻译"] = "VS Code manual translation", ["待启用扩展"] = "Enable extension",
        ["正在更新连接…"] = "Updating connection…", ["操作结果未确认"] = "Operation not confirmed", ["已连接"] = "Connected", ["未连接"] = "Not connected",
        ["此发行版尚未配置 Chrome 连接。"] = "Chrome connection is not configured in this installation.", ["无法复制，请稍后重试。"] = "Could not copy. Try again later.",
        ["无法打开浏览器。请检查默认浏览器设置后重试。"] = "Could not open a browser. Check your default browser and retry.",
        ["可重试移除，或确认重新接入。"] = "Retry removal, or confirm reconnection.",
        ["输入服务暂不可用"] = "Input service unavailable", ["正在连接输入服务"] = "Connecting input service", ["输入服务已连接"] = "Input service connected",
        ["输入监听尚未就绪"] = "Input monitoring is not ready", ["输入服务连接中断，正在重试"] = "Input service disconnected; retrying",
        ["仅在启用 TypeLay 的按需模式下生效。自动模式不注册这两个快捷键。"] = "These two shortcuts are registered only in enabled on-demand mode.",
        ["新增快捷键仅在启用 TypeLay 的按需模式下生效。"] = "Wake and direct-replacement shortcuts are active only in enabled on-demand mode.",
        ["新增快捷键仅在按需模式下启用。"] = "Wake and direct-replacement shortcuts are active only in on-demand mode.",
        ["两个快捷键已启用；操作不会切换工作模式。"] = "Both shortcuts are active; using them does not change the work mode.",
        ["快捷键注销未确认，当前不会执行按需命令。"] = "Shortcut release was not confirmed; on-demand commands are disabled.",
        ["浏览器快捷键连接中。"] = "Connecting browser shortcuts.",
        ["当前模型或语言设置不支持翻译风格。"] = "The current model or language direction does not support translation tone.",
        ["请检查术语内容、长度和语言方向。"] = "Check term content, length and language direction.",
        ["同一范围内的源词已有不同译法，请修改后再保存。"] = "This source term already has another translation in the same scope. Edit it before saving.",
        ["设置已更新，当前修改未保存。请检查当前值后重试。"] = "Settings changed; this edit was not saved. Review the current values and retry.",
        ["保存已取消，修改尚未保存。"] = "Save canceled. Your edit was not saved.",
        ["当前无法保存，请检查翻译设置后重试。"] = "Cannot save now. Review translation settings and retry.",
        ["保存结果未确认，请检查当前值后重试。"] = "Save was not confirmed. Review the current value and retry.",
        ["较长文本"] = "Longer text", ["完全本地的输入翻译"] = "Translation stays on your device"
    };

    public static string Translate(string value)
    {
        if (!UiTheme.English || value.Length == 0) return value;
        if (English.TryGetValue(value, out string? translated)) return translated;
        if (value.Contains('\n')) return string.Join('\n', value.Split('\n').Select(Translate));
        foreach (var (prefix, replacement) in new[] { ("当前选择：", "Current selection: "), ("本地翻译已就绪 · ", "Local translation is ready · "), ("当前模型支持 ", "Available translation directions: ") })
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return replacement + value[prefix.Length..].Replace(" 个翻译方向", "", StringComparison.Ordinal);
        return value;
    }
}
