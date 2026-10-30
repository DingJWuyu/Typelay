"""Explicit product-copy localization. Never apply to source text or user terms.

The bindings are installed on product-owned chrome at construction time. Later
application identities, glossary cells and translation result views are not bound.
Unknown diagnostics remain verbatim rather than being guessed or rewritten.
"""

from gi.repository import GLib, Gtk

ENGLISH = {
    "取消": "Cancel",
    "继续": "Continue",
    "关闭": "Close",
    "重试": "Retry",
    "移除": "Remove",
    "删除": "Delete",
    "强制保存": "Save Anyway",
    "继续编辑": "Keep Editing",
    "放弃修改": "Discard Changes",
    "重试保存": "Retry Save",
    "设置已保存": "Settings saved",
    "设置未保存": "Settings were not saved",
    "已保存，请重新打开设置确认当前状态。": "Saved. Reopen Settings to confirm its current state.",
    "未保存：请检查输入。草稿仍保留。": "Not saved. Check the value; your edit is retained.",
    "请检查当前用户目录与组织策略。": "Check the current user's data directory and organization policy.",
    "请选择支持的源语言和目标语言。": "Choose a supported source and target language.",
    "设置已更新，请检查当前值后重试。": "Settings changed. Review the current values and retry.",
    "设置保存未能确认。请检查设置目录权限或磁盘空间，再重新选择需要的设置。": "Save was not confirmed. Check folder permissions or free space, then choose the setting again.",
    "模型尚未就绪": "Model not ready",
    "本地翻译模型": "Local translation model",
    "当前模型无法确认": "Current model could not be confirmed",
    "尚未选择本地模型": "No local model selected",
    "本地翻译尚未就绪": "Local translation is not ready",
    "本地翻译已就绪": "Local translation is ready",
    "本地翻译组件已就绪": "Local translation components are ready",
    "本地翻译组件尚未准备": "Local translation components are not prepared",
    "正在准备本地翻译组件…": "Preparing local translation…",
    "正在启动本地翻译…": "Starting local translation…",
    "正在暂停…": "Pausing…",
    "当前模型不包含这个方向。请切换模型或调整语言。": "This model does not include the selected direction. Change the model or languages.",
    "尚未准备本地翻译 · 默认 Index Translate 2B": "Local translation is not prepared · Default: Index Translate 2B",
    "准备 Index 本地翻译…": "Prepare Index Translation…",
    "确认后下载并使用 Index；已有用户的模型选择不会自动改变。": "Download and use Index after confirmation. Existing model selections are not changed automatically.",
    "当前安装包缺少可用的 Index 运行组件，请使用完整安装包。旧模型仍可单独选择。": "This installation lacks usable Index runtime components. Use a complete package; existing models remain selectable.",
    "本地组件安装记录无法读取": "Local installation records could not be read",
    "未更改模型。请重新打开 TypeLay 检查安装记录。": "The model was not changed. Reopen TypeLay to check the installation.",
    "暂不可操作": "Unavailable",
    "请在下方选择修复": "Choose a repair action below",
    "试一试": "Try It",
    "选择接入方式": "Choose a Connection",
    "准备翻译": "Prepare Translation",
    "组件与安装说明": "Components and installation details",
    "模型详情与许可": "Model details and license",
    "模型许可证": "Model license",
    "微软运行库说明": "Microsoft Runtime Information",
    "重新准备": "Prepare Again",
    "完整修复本地翻译…": "Repair Local Translation…",
    "修复本地翻译组件…": "Repair Local Components…",
    "补齐另一方向": "Add the Other Direction",
    "下载并使用…": "Download and Use…",
    "使用此模型": "Use This Model",
    "修复…": "Repair…",
    "移除…": "Remove…",
    "补充中英方向…": "Add Chinese-English…",
    "当前安装包不含可用的 Hy 运行组件": "This installation has no usable Hy runtime components",
    "尚未安装": "Not installed",
    "当前选择": "Current selection",
    "已安装，未使用": "Installed, not selected",
    "已安装": "Installed",
    "不可用": "Unavailable",
    "预览": "Preview",
    "查看详情": "View Details",
    "检查连接": "Check Connection",
    "重新检查": "Check Again",
    "选择接入组件": "Choose Connection Component",
    "打开文件夹": "Open Folder",
    "打开浏览器": "Open Browser",
    "复制": "Copy",
    "复制扩展 ID": "Copy Extension ID",
    "桌面普通输入框": "Desktop text fields",
    "VS Code 手动翻译": "VS Code manual translation",
    "待启用扩展": "Enable extension",
    "正在更新连接…": "Updating connection…",
    "操作结果未确认": "Operation not confirmed",
    "已连接": "Connected",
    "未连接": "Not connected",
    "此发行版尚未配置 Chrome 连接。": "Chrome connection is not configured in this installation.",
    "无法复制，请稍后重试。": "Could not copy. Try again later.",
    "无法打开浏览器。请检查默认浏览器设置后重试。": "Could not open a browser. Check your default browser and retry.",
    "可重试移除，或确认重新接入。": "Retry removal, or confirm reconnection.",
    "输入服务暂不可用": "Input service unavailable",
    "正在连接输入服务": "Connecting input service",
    "输入服务已连接": "Input service connected",
    "输入监听尚未就绪": "Input monitoring is not ready",
    "输入服务连接中断，正在重试": "Input service disconnected; retrying",
    "仅在启用 TypeLay 的按需模式下生效。自动模式不注册这两个快捷键。": "These two shortcuts are registered only in enabled on-demand mode.",
    "新增快捷键仅在启用 TypeLay 的按需模式下生效。": "Wake and direct-replacement shortcuts are active only in enabled on-demand mode.",
    "新增快捷键仅在按需模式下启用。": "Wake and direct-replacement shortcuts are active only in on-demand mode.",
    "两个快捷键已启用；操作不会切换工作模式。": "Both shortcuts are active; using them does not change the work mode.",
    "快捷键注销未确认，当前不会执行按需命令。": "Shortcut release was not confirmed; on-demand commands are disabled.",
    "浏览器快捷键连接中。": "Connecting browser shortcuts.",
    "当前模型或语言设置不支持翻译风格。": "The current model or language direction does not support translation tone.",
    "请检查术语内容、长度和语言方向。": "Check term content, length and language direction.",
    "同一范围内的源词已有不同译法，请修改后再保存。": "This source term already has another translation in the same scope. Edit it before saving.",
    "设置已更新，当前修改未保存。请检查当前值后重试。": "Settings changed; this edit was not saved. Review the current values and retry.",
    "保存已取消，修改尚未保存。": "Save canceled. Your edit was not saved.",
    "当前无法保存，请检查翻译设置后重试。": "Cannot save now. Review translation settings and retry.",
    "保存结果未确认，请检查当前值后重试。": "Save was not confirmed. Review the current value and retry.",
    "较长文本": "Longer text",
    "完全本地的输入翻译": "Translation stays on your device",
    "自动": "Automatic",
    "按需": "On demand",
    "运行中": "Running",
    "已暂停": "Paused",
    "打开": "Open",
    "重新运行": "Run again",
    "浅色": "Light",
    "深色": "Dark",
    "无衬线": "Sans",
    "系统": "System",
    "尚未提供": "Not available",
    "CPU · 当前后端": "CPU · Current backend",
    "退出 TypeLay": "Quit TypeLay",
    "搜索应用": "Search apps",
    "添加应用…": "Add app…",
    "高级排除规则": "Advanced exclusion rules",
    "移除覆盖": "Remove override",
    "编辑": "Edit",
    "完成": "Done",
    "全局": "Global",
    "自然": "Natural",
    "正式": "Formal",
    "简洁": "Concise",
    "口语": "Casual",
    "简洁 · 尚未提供": "Concise · Not available",
    "口语 · 尚未提供": "Casual · Not available",
    "翻译风格": "Translation tone",
    "源词": "Source term",
    "指定译法": "Preferred translation",
    "作用范围": "Scope",
    "指定语言方向": "Selected language direction",
    "＋ 添加术语": "＋ Add term",
    "添加术语": "Add term",
    "重置翻译风格…": "Reset translation tone…",
    "还没有术语。添加常用词与指定译法。": "No terms yet. Add your preferred translations.",
    "术语保存在本机；当前模型方向尚未开放应用，不影响普通翻译。": "Terms are saved locally. Applying them is not released for this model direction; ordinary translation is unaffected.",
    "当前模型暂不支持风格。": "This model does not support tone changes.",
    "当前方向尚未开放风格。": "Tone changes are not released for this direction.",
    "人工稿存在，暂不能更改风格。": "Tone cannot change while a manual draft exists.",
    "翻译操作尚未结束，暂不能更改风格。": "Finish the translation operation before changing tone.",
    "翻译操作尚未结束，暂不能保存术语。": "Finish the translation operation before saving terms.",
    "当前模型暂不支持术语应用。": "This model does not support applying terms.",
    "当前不支持同时应用风格和术语。": "Combining tone and glossary is not available.",
    "当前模型方向尚未开放风格与术语同时应用。": "Combined tone and glossary are not released for this direction.",
    "请检查源词、指定译法和语言方向。": "Check the source term, preferred translation and language direction.",
    "风格未保存，请检查权限或磁盘空间。": "Tone was not saved. Check permissions or free space.",
    "术语未保存，请检查权限或磁盘空间。": "Terms were not saved. Check permissions or free space.",
    "设置已更改，请检查后再次保存。": "Settings changed. Review the current values and save again.",
    "当前无法保存这项翻译设置。": "This translation preference cannot be saved now.",
    "仍然使用": "Use anyway",
    "正在保存…": "Saving…",
    "未设置": "Not set",
    "按下快捷键…": "Press keys…",
    "撤销": "Undo",
    "已删除术语": "Term deleted",
    "未排除任何应用": "No apps excluded",
    "欢迎使用 TypeLay": "Welcome to TypeLay",
    "让每一次表达，都自然跨越语言。": "Every thought, naturally across languages.",
    "所有翻译均在本机运行。\n不上传输入内容，不保存翻译历史。": "Translation runs on your device.\nInput is not uploaded and translation history is not stored.",
    "从你熟悉的语言开始": "Start with your language",
    "选择源语言和目标语言。之后可随时在设置或快捷面板中更改。": "Choose your source and target languages. Change them later in Settings or the quick panel.",
    "选择语言…": "Choose languages…",
    "准备本地翻译": "Prepare local translation",
    "正在读取模型状态…": "Reading model status…",
    "模型安装需要你明确确认。Index、Argos 与 Hy 的可用性以当前实际安装为准。": "Model installation requires your confirmation. Index, Argos and Hy availability reflects the actual installation.",
    "管理本地模型…": "Manage local models…",
    "试着说一句": "Try a sentence",
    "输入非敏感练习文字，先完成输入法候选，再生成本地译文。本窗只预览，不向其他应用写入。": "Enter a non-sensitive practice sentence, finish IME composition, then generate a local preview. This window never writes into other apps.",
    "例如：你好，我们明天下午见。": "For example: Hello, let’s meet tomorrow afternoon.",
    "生成本地译文": "Generate local translation",
    "连接应用并开始练习": "Connect an app and practice",
    "稍后设置": "Set up later",
    "返回": "Back",
    "开始使用": "Get started",
    "原文改变后请重新生成。": "Generate again after changing the source.",
    "正在使用本机模型生成译文…": "Generating with the local model…",
    "本地译文已生成，请确认含义。继续练习请从下方进入应用支持。": "Local translation generated. Check its meaning, then connect an app below to practice.",
    "教程提示偏好未保存，请重试。": "Tutorial preference was not saved. Please retry.",
    "请先在设置中明确同意安装本地翻译组件，完成自检后再试译。": "Confirm local component installation in Settings and complete its check before trying translation.",
    "本地模型已就绪，可以开始练习。": "The local model is ready for practice.",
    "本地模型尚未就绪。请先准备或修复实际组件。": "The local model is not ready. Prepare or repair the actual components first.",
    "所有翻译均在本机运行。密码、敏感字段和无法确认的输入法状态始终受到保护。": "Translation stays on this device. Passwords, sensitive fields and unconfirmed IME states remain protected.",
    "本地翻译 · 不保存翻译历史\nTypeLay 不会自动提交或发送源应用中的内容。": "Local translation · No translation history\nTypeLay never submits or sends content from the source app.",
    "设置保存结果未确认，请重新打开检查。": "Save was not confirmed. Reopen Settings to check.",
    "已添加应用 · 默认启用，继承全局风格": "App added · Enabled by default, inherits global tone",
    "继承全局风格": "Inherit global tone",
    "尚无当前应用": "No current app",
    "○ 本地模型尚未就绪": "○ Local model not ready",
    "● 本地模型已就绪": "● Local model ready",
    "设置…": "Settings…",
    "关闭面板": "Close panel",
    "接受译文": "Accept translation",
    "退出当前翻译": "Dismiss translation",
    "暂停 / 恢复 TypeLay": "Pause / resume TypeLay",
    "唤醒翻译": "Show translation",
    "直接翻译替换": "Translate and replace",
    "术语设置已保存，但通知未完成。请重新打开设置检查。": "Terms were saved, but the notification failed. Reopen Settings to check.",
    "搜索术语 / Search glossary": "Search glossary",
    "正在读取应用…": "Loading apps…",
    "没有匹配的应用": "No matching apps",
    "正在启动…": "Starting…",
}

ENGLISH.update(
    {
        "自动识别": "Auto detect",
        "中文": "Chinese",
        "英语": "English",
        "更多语言 · 尚未提供": "More languages · Not available",
        "本地模型已就绪": "Local model ready",
        "本地模型尚未就绪。请在模型页确认准备。": "The local model is not ready. Check preparation on the Local model page.",
    }
)

ENGLISH.update(
    {
        "连接应用": "Connect apps",
        "连接详情": "Connection details",
        "选择接入方式": "Choose a connection",
        "尚未检查": "Not checked",
        "还没有可靠的检查结果。请选择目标应用并检查连接。": "There is no reliable check result yet. Choose an app and check its connection.",
        "查看详情": "View details",
        "未安装本地模型": "No local model installed",
        "安装方向（可选）": "Installation directions (optional)",
        "模型与其他后端": "Models and other backends",
        "术语已保存": "Terms saved",
        "撤销 / Undo": "Undo",
        "已停用此应用": "App disabled",
        "已启用此应用": "App enabled",
        "当前 · 已就绪": "Selected · Ready",
        "当前 · 尚未就绪": "Selected · Not ready",
        "安装状态无法确认": "Installation state unconfirmed",
        "当前安装包不含可用的 Index 运行组件": "This installation has no usable Index runtime components",
        "查看接入步骤": "Connection instructions",
        "选择并检查组件…": "Choose and check components…",
        "安装接入组件…": "Install connection components…",
        "连接应用…": "Connect app…",
        "修复连接…": "Repair connection…",
        "结束本次连接…": "End this connection…",
        "移除接入…": "Remove connection…",
        "开始练习…": "Start practice…",
        "选择应用…": "Choose app…",
        "通过 TypeLay 打开": "Open with TypeLay",
        "内置接入可用": "Built-in connection available",
        "需要安装或连接": "Installation or connection needed",
        "等待用户授权": "Waiting for approval",
        "需要重新打开应用": "Reopen the app",
        "已注册，尚未连接": "Registered, not connected",
        "当前尚不支持": "Not currently supported",
        "接入异常": "Connection error",
        "连接已确认；具体输入框仍需单独检查。": "Connection confirmed; each field still requires its own check.",
        "缺少所需的接入组件。请先安装或选择完整组件，再检查连接。": "Required connection components are missing. Install or choose complete components, then check again.",
        "尚未配置此应用的 TypeLay 接入。请按接入步骤完成连接。": "TypeLay is not configured for this app. Follow the connection instructions.",
        "接入配置不完整或已失效。请修复后重新检查。": "Connection configuration is incomplete or invalid. Repair it and check again.",
        "检测到不属于 TypeLay 的接入配置，未覆盖或移除。请查看详情处理冲突。": "Another connection configuration was found and was not overwritten or removed. Check details to resolve the conflict.",
        "请在应用或浏览器中完成扩展及权限批准，然后重新检查。": "Approve the extension and permissions in the app or browser, then check again.",
        "组件已检查，仍需你按接入步骤确认启用；尚未连接应用。": "Components were checked. You still need to enable them using the instructions; the app is not connected.",
        "接入配置已更新。请保存工作并重新打开应用，然后检查连接。": "Configuration updated. Save your work, reopen the app, then check its connection.",
        "接入配置已注册，但尚未收到实际连接。请打开应用或浏览器的普通输入框后重新检查。": "Configuration is registered, but no actual connection was received. Open a regular text field and check again.",
        "此平台尚未实现这项接入，不能通过安装模型或反复检查启用。": "This connection is not implemented on this platform. Installing models or repeating the check cannot enable it.",
        "TypeLay 已暂停，请先在通用设置中启用后再检查。": "TypeLay is paused. Enable it in General, then check again.",
        "尚未检查目标输入框。请切回需要翻译的普通文本框试一试，再查看检查结果。": "The target field has not been checked. Try the intended regular text field, then review the result.",
        "尚未取得目标输入框的可靠观察结果，不能确认预览或替换能力。": "No reliable field observation is available. Preview and replacement capabilities cannot be confirmed.",
        "目前仅确认显示译文；Tab 接受和直接替换尚不可用。": "Only translation preview is confirmed. Tab acceptance and direct replacement are unavailable.",
        "当前输入框未提供直接替换能力，不会改写其他输入框。": "This field does not allow direct replacement. Other fields will not be modified.",
        "仅已检查的普通文本控件可用；密码、敏感、代码及不兼容字段继续排除。": "Only checked regular text controls are usable. Passwords, sensitive, code and incompatible fields remain excluded.",
        "接入配置未完成。请查看详情并在处理原因后重试，原有数据会保留。": "Connection setup did not finish. Review details, resolve the cause and retry. Existing data is preserved.",
        "尚未识别到有效的浏览器扩展。请按接入步骤选择或启用 TypeLay 扩展，再重新检查。": "No valid browser extension was identified. Choose or enable TypeLay using the instructions, then check again.",
        "未能完成连接检查。请查看详情并重新检查，当前不确认任何能力。": "The connection check did not finish. Review details and check again. No capability is currently confirmed.",
        "操作未完成。请查看详情，处理原因后再试；未将这次操作标为成功。": "The operation did not complete. Check details, resolve the cause and retry. It is not marked successful.",
        "检查连接只读取状态，不会自动安装、授权或修改应用配置。": "Checking reads status only. It never installs, grants permissions or changes app configuration.",
        "按接入步骤安装或启用组件。安装完成不代表已连接，完成后请重新检查。": "Follow the instructions to install or enable components. Installation alone does not mean connected; check again afterwards.",
        "请自行完成应用或系统要求的批准。取消后可回到这里继续；TypeLay 不会绕过权限提示。": "Complete approval in the app or system. You can return here after canceling; TypeLay never bypasses permission prompts.",
        "先保存应用中的工作，再由你重新打开应用。TypeLay 不会自动结束应用进程。": "Save your work, then reopen the app yourself. TypeLay never terminates app processes automatically.",
        "打开目标应用，确认扩展或接入组件已启用，再到普通输入框试一试。": "Open the app, check that its extension or connection is enabled, then try a regular text field.",
        "到实际输入框检查预览与允许的替换。连接正常不表示应用内所有字段均可使用。": "Check preview and permitted replacement in the actual field. A connection does not mean every field is supported.",
        "这项接入仍待实现，当前不会提供无效的安装或修复操作。": "This connection is not implemented. No ineffective install or repair action is offered.",
        "查看原因和详情，再选择可用的恢复操作；修复或移除接入会保留设置、模型及个人数据。": "Review the reason and details, then choose an available recovery action. Repair and removal preserve settings, models and personal data.",
        "无法确认带启动参数的应用进程，请在高级编辑中填写可执行文件名。": "The launcher has arguments, so its process identity cannot be confirmed. No policy change is allowed.",
        "无法确认沙箱应用的真实进程，请在高级编辑中填写可执行文件名。": "The sandboxed process identity cannot be confirmed. No policy change is allowed.",
        "无法确认包装启动器的应用进程，请在高级编辑中填写可执行文件名。": "The wrapper process identity cannot be confirmed. No policy change is allowed.",
        "无法确认应用进程，请在高级编辑中填写可执行文件名。": "The process identity cannot be confirmed. No policy change is allowed.",
    }
)

ENGLISH.update(
    {
        "接受默认使用 Tab，退出默认使用 Esc；也可选择 F1-F24 或 Ctrl+F1-F24。接受与退出须使用不同主键；当前输入适配器须确认实际键对。唤醒与直接替换另支持 Shift+功能键（不含 F12），不适用于接受与退出。不能完整检测每个应用内部的快捷键，未发现冲突不代表没有冲突。": "Accept defaults to Tab and dismiss to Esc. F1-F24 or Ctrl+F1-F24 are also available. Accept and dismiss require different base keys and confirmation from the active input adapter. Wake and direct replacement additionally support Shift+function keys except F12; these combinations are not available for accept or dismiss. Conflicts inside other apps cannot all be detected.",
        "●  Auto → EN                         Tab  接受": "●  Auto → EN                         Tab  Accept",
        "设置保存结果未确认，请重新打开检查。": "Save was not confirmed. Reopen Settings to check.",
        "教程选择已保存；可随时重新打开。": "Tutorial preference saved. You can reopen it anytime.",
        "翻译设置已保存，但状态通知未完成。请重新打开设置检查。": "Translation settings were saved, but status notification failed. Reopen Settings to check.",
        "无法确认翻译设置。草稿仍保留，请重试保存或重新打开设置检查。": "Translation settings could not be confirmed. Your draft is retained; retry saving or reopen Settings.",
        "保存结果未确认。草稿仍保留，请重新打开设置检查。": "Save was not confirmed. Your draft is retained; reopen Settings to check.",
        "原文和指定译法不能为空。": "Source term and preferred translation cannot be empty.",
        "请检查源词、指定译法与语言方向。": "Check the source term, preferred translation and direction.",
        "请先选择术语源语言和目标语言。": "Choose source and target languages for the term.",
        "设置已变化，无法撤销这次删除。": "Settings changed; this deletion could not be undone.",
        "同名术语已经存在，没有覆盖它。": "A term with this identity already exists and was not overwritten.",
    }
)


class Localizer:
    def __init__(self, settings):
        self.settings = settings
        self.bindings = []
        self.bound = set()
        self.changing = False
        self.reverse = {value: key for key, value in ENGLISH.items()}

    def text(self, value):
        value = self.reverse.get(value, value)
        language = (self.settings() or {}).get("uiLanguage", "system")
        english = (
            language == "en"
            or language == "system"
            and not any(name.startswith("zh") for name in GLib.get_language_names())
        )
        if not english:
            return value
        if value in ENGLISH:
            return ENGLISH[value]
        return "\n".join(ENGLISH.get(line, line) for line in value.split("\n"))

    def model_text(self, value):
        language = (self.settings() or {}).get("uiLanguage", "system")
        english = (
            language == "en"
            or language == "system"
            and not any(name.startswith("zh") for name in GLib.get_language_names())
        )
        if not english:
            return value
        result = self.text(value)
        for chinese, translated in (
            ("中文多语", "Chinese multilingual"),
            ("推荐默认", "Recommended default"),
            ("目录可用：", "Catalog: "),
            (" GB 下载", " GB download"),
            ("当前选择：", "Selected: "),
            ("本地翻译已就绪 · ", "Local translation ready · "),
            ("中文", "Chinese"),
            (" · 预览", " · Preview"),
            ("（已安装）", " (installed)"),
        ):
            result = result.replace(chinese, translated)
        return result

    def bind(self, widget):
        if widget in self.bound or getattr(widget, "typelay_user_content", False):
            return
        self.bound.add(widget)
        if isinstance(widget, Gtk.Label):
            self.bind_property(widget, "label", widget.get_text, widget.set_text)
        elif isinstance(widget, Gtk.Entry):
            self.bind_property(
                widget,
                "placeholder-text",
                lambda: widget.get_placeholder_text() or "",
                widget.set_placeholder_text,
            )
        elif isinstance(widget, Gtk.Button) and (
            widget.get_label() is not None or widget.get_child() is None
        ):
            self.bind_property(widget, "label", lambda: widget.get_label() or "", widget.set_label)
        elif isinstance(widget, Gtk.Expander) and widget.get_label() is not None:
            self.bind_property(widget, "label", widget.get_label, widget.set_label)
        if isinstance(widget, Gtk.Container):
            for child in widget.get_children():
                self.bind(child)

    def bind_property(self, widget, name, read, write):
        def update(*_):
            if self.changing:
                return
            value = read()
            translated = self.text(value)
            if translated != value:
                self.changing = True
                try:
                    write(translated)
                finally:
                    self.changing = False

        self.bindings.append(update)
        widget.connect("notify::" + name, update)
        update()

    def refresh(self):
        for update in self.bindings:
            update()
