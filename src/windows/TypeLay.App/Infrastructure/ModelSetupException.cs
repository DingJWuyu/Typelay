using System.ComponentModel;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using TypeLay.Runtime;

namespace TypeLay.App;

internal enum ModelSetupStage
{
    Prepare, Runtime, Dependencies, ModelDownload, ModelExtract, ModelVerify, SelfCheck, Activate
}

// Keep exception details, paths and server responses out of the user-visible failure contract.
internal sealed class ModelSetupException(SetupFailureCategory category, ModelSetupStage stage,
    SetupFailureCategory? recoveryFailureCategory = null)
    : Exception($"setup-failed:{category}:{stage}")
{
    public SetupFailureCategory Category { get; } = category;
    public ModelSetupStage Stage { get; } = stage;
    public SetupFailureCategory? RecoveryFailureCategory { get; } = recoveryFailureCategory;

    internal static ModelSetupException From(Exception error, ModelSetupStage stage)
    {
        if (error is ModelSetupException failure)
            return failure;
        int code = error is Win32Exception native ? native.NativeErrorCode : error.HResult & 0xffff;
        SetupFailureCategory category = error switch
        {
            UnauthorizedAccessException or SecurityException => SetupFailureCategory.Permission,
            IOException or Win32Exception when code is 39 or 112 => SetupFailureCategory.DiskSpace,
            IOException or Win32Exception when code is 5 or 32 or 33 => SetupFailureCategory.Permission,
            _ when stage == ModelSetupStage.SelfCheck => SetupFailureCategory.Runtime,
            HttpRequestException or OperationCanceledException or TimeoutException => SetupFailureCategory.Network,
            InvalidDataException or JsonException or FormatException or KeyNotFoundException => SetupFailureCategory.Integrity,
            InvalidOperationException when stage is not ModelSetupStage.Prepare => SetupFailureCategory.Integrity,
            _ => SetupFailureCategory.Unknown
        };
        return new(category, stage);
    }
}
