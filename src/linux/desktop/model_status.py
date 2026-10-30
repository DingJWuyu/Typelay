"""Read-only model facts shared by the GTK settings and quick panel.

Presentation never grants model, translation or source-field authority. Missing
or malformed facts stay unconfirmed instead of becoming ready or unavailable.
"""


def activity_status(enabled, ready):
    if enabled is False:
        return "paused", "已暂停", "Paused"
    if enabled is not True or type(ready) is not bool:
        return "checking", "正在确认…", "Checking…"
    if ready:
        return "ready", "运行中", "Running"
    return "warn", "模型未就绪", "Model not ready"


def model_notice(ready, installed, phase="idle"):
    phases = {
        "failed": ("bad", "本地模型准备失败", "Local model setup failed"),
        "paused": ("paused", "模型下载已暂停", "Model download paused"),
        "pausing": ("warn", "正在暂停模型准备…", "Pausing model setup…"),
        "starting": ("warn", "正在启动本地翻译模型…", "Starting the local translation model…"),
        "running": ("warn", "正在准备本地翻译模型…", "Preparing the local translation model…"),
    }
    if phase in phases:
        tone, chinese, english = phases[phase]
        if ready is True:
            # The serving model stays usable while a replacement is prepared.
            # Its readiness must not hide a separate setup failure or pause.
            chinese = {
                "failed": "新模型准备失败",
                "paused": "模型下载已暂停",
                "pausing": "正在暂停新模型准备",
                "starting": "正在启动新模型",
                "running": "正在准备新模型",
            }[phase] + "；当前模型仍可用"
            english = {
                "failed": "New model setup failed",
                "paused": "Model download paused",
                "pausing": "Pausing new model setup",
                "starting": "Starting the new model",
                "running": "Preparing the new model",
            }[phase] + "; current model remains available"
        return tone, chinese, english
    if ready is True:
        return "ready", "本地模型就绪", "Local model ready"
    if type(ready) is not bool:
        return "checking", "正在确认本地模型状态…", "Checking local model status…"
    if installed is False:
        return "warn", "需要下载本地翻译模型", "Download the local translation model"
    return "warn", "本地翻译模型尚未就绪", "Local translation model is not ready"


def set_status_tone(widget, tone):
    context = widget.get_style_context()
    for candidate in ("paused", "checking", "warn", "bad"):
        (context.add_class if candidate == tone else context.remove_class)(candidate)


def download_presentation(snapshot, state, tr):
    """Truthful current-file progress; no aggregate percentage or ETA."""
    if snapshot is None or state not in ("running", "pausing", "paused", "failed"):
        return "", None
    stage = snapshot["stage"]
    state_label = {
        "paused": ("已暂停", "Paused"),
        "pausing": ("正在暂停", "Pausing"),
        "failed": ("准备失败", "Setup failed"),
    }.get(state)
    stages = {
        "prepare": ("正在准备…", "Preparing…"),
        "runtime": ("正在准备运行环境…", "Preparing runtime…"),
        "dependencies": ("正在准备依赖…", "Preparing dependencies…"),
        "model-extract": ("正在解包…", "Extracting…"),
        "model-verify": ("正在校验模型…", "Verifying model…"),
        "self-check": ("正在检查模型…", "Checking model…"),
        "activate": ("正在等待启用确认…", "Awaiting activation…"),
    }
    if stage != "model-download":
        return tr(*(state_label or stages[stage])), None
    resource = {
        "argos-zh-en": "Argos ZH→EN",
        "argos-en-zh": "Argos EN→ZH",
        "hy-model": "HY-MT",
        "hy-license": "HY-MT LICENSE",
        "index-model": "Index Translate",
        "index-license": "Index LICENSE",
        "unknown": tr("模型文件", "Model file"),
    }[snapshot["resource"]]
    count, total = snapshot["downloaded"], snapshot["total"]

    def size(value):
        if value < 1024:
            return f"{value} B"
        if value < 1024 * 1024:
            return f"{value / 1024:.1f} KiB"
        return f"{value / (1024 * 1024):.1f} MiB"

    amount = size(count)
    amount += " / " + size(total) if total is not None else tr(" / 总量未知", " / total unknown")
    phase = (
        state_label
        or {
            "Headers": ("正在连接", "Connecting"),
            "Body": ("正在下载", "Downloading"),
            "Verify": ("正在校验文件", "Verifying file"),
            "Complete": ("文件已下载，尚未启用", "File downloaded, not activated"),
        }[snapshot["downloadPhase"]]
    )
    label = tr("当前文件", "Current file") + f": {resource} · {amount} · " + tr(*phase)
    return label, count / total if total is not None else None


def render_download(widgets, snapshot, state, tr, current, task):
    """Render only while this exact read-only snapshot still owns the view."""
    message, fraction = download_presentation(snapshot, state, tr)
    caption, bar = widgets
    identity = (task, snapshot["resource"], snapshot["attempt"]) if snapshot else None
    for action in (
        lambda: caption.set_text(message),
        lambda: caption.set_visible(bool(message)),
        lambda: bar.present_progress(fraction, identity, state, current),
        lambda: bar.set_visible(bool(message) and fraction is not None),
        lambda: bar.get_accessible().set_name(message),
    ):
        if not current():
            return
        action()
