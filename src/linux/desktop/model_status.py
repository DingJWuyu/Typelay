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
