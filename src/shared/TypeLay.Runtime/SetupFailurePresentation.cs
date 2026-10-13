namespace TypeLay.Runtime;

public enum SetupFailureCategory
{
    Network, DiskSpace, Integrity, Permission, Runtime, Unknown
}

public sealed record SetupFailureView(string Code, string Message, string NextStep, string RetryLabel);

// Installers classify their own platform errors. Only fixed product guidance crosses
// into presentation; exception text, URLs and local paths are never accepted here.
public static class SetupFailurePresentation
{
    public static string PausedMessage => "准备已暂停。已下载的部分会保留，可继续准备。";
    public static string ResumeLabel => "继续准备";

    public static SetupFailureView FromCode(string? code) => Create(code switch
    {
        "network" => SetupFailureCategory.Network,
        "disk-space" => SetupFailureCategory.DiskSpace,
        "integrity" => SetupFailureCategory.Integrity,
        "permission" => SetupFailureCategory.Permission,
        "runtime" => SetupFailureCategory.Runtime,
        _ => SetupFailureCategory.Unknown
    });

    public static SetupFailureView Create(SetupFailureCategory category) => category switch
    {
        SetupFailureCategory.Network => new("network", "下载连接失败，请检查网络。",
            "检查网络或代理后重新准备；已验证的下载进度可继续使用。", "重新准备"),
        SetupFailureCategory.DiskSpace => new("disk-space", "组件安装空间不足。",
            "释放安装位置和下载缓存所在磁盘的空间后重新准备。", "重新准备"),
        SetupFailureCategory.Integrity => new("integrity", "组件校验失败。",
            "重新准备以重新获取损坏的组件；若仍失败，请检查安装包来源。", "重新准备"),
        SetupFailureCategory.Permission => new("permission", "组件文件访问受限。",
            "检查文件占用和安装位置、下载缓存的访问权限后重新准备。", "重新准备"),
        SetupFailureCategory.Runtime => new("runtime", "翻译运行环境准备或自检失败。",
            "重新准备本地翻译组件；若仍失败，请查看安装诊断。", "重新准备"),
        _ => new("unknown", "组件准备失败，原因尚未确定。",
            "请重新准备；若仍失败，请查看安装诊断。", "重新准备")
    };
}
