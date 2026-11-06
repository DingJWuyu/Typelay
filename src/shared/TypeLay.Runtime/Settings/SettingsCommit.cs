using System.Runtime.ExceptionServices;
using TypeLay.Core;

namespace TypeLay.Runtime;

public enum SettingsCommitState
{
    NotCommitted, Unchanged, Committed, CommittedNotificationFailed, ExternalEffectUnknown
}

public enum SettingsStartupEffect
{
    NotRequested, Applied, OutcomeUnknown
}

// Revision is the version at this operation's commit point, even if a notification
// reenters the store and commits another value. Startup is an independent fact.
public sealed record SettingsCommitResult(SettingsCommitState State, long Revision,
    SettingsStartupEffect Startup = SettingsStartupEffect.NotRequested, bool? RequestedStartup = null,
    string? Reason = null, Exception? Error = null)
{
    public bool Saved => State is SettingsCommitState.Unchanged or SettingsCommitState.Committed
        or SettingsCommitState.CommittedNotificationFailed;
}

public sealed partial class SettingsStore
{
    // Called on the existing serial host loop. The UI supplies only its lifetime;
    // retirement, final version checking and the synchronous commit stay here.
    public Task<SettingsCommitResult> CommitAsync(long expectedRevision, UserSettings settings,
        Func<UserSettings, Task<bool>>? prepare = null, Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default) => CommitPreparedAsync(expectedRevision, settings, null,
            prepare, isCurrent is null ? null : () => isCurrent() ? null : "canceled", cancellationToken);

    internal Task<SettingsCommitResult> CommitStylesAsync(long expectedRevision, TranslationStyles? styles,
        Func<Task<bool>> prepare, Func<string?> check, CancellationToken cancellationToken, Action apply) =>
        CommitPreparedAsync(expectedRevision, Value with
        {
            Styles = styles
        }, "style", _ => prepare(), check,
            cancellationToken, apply);

    internal Task<SettingsCommitResult> CommitGlossaryAsync(long expectedRevision, GlossarySnapshot glossary,
        Func<Task<bool>> prepare, Func<string?> check, CancellationToken cancellationToken, Action apply) =>
        CommitPreparedAsync(expectedRevision, Value with
        {
            Glossary = glossary
        }, "glossary", _ => prepare(), check,
            cancellationToken, apply);

    private async Task<SettingsCommitResult> CommitPreparedAsync(long expectedRevision, UserSettings proposed,
        string? feature, Func<UserSettings, Task<bool>>? prepare, Func<string?>? check,
        CancellationToken cancellationToken, Action? apply = null)
    {
        var expected = Value;
        SettingsCommitResult Rejected(string reason, Exception? error = null) =>
            new(SettingsCommitState.NotCommitted, Revision, Reason: reason, Error: error);
        string? Check() => cancellationToken.IsCancellationRequested ? "canceled"
            : expectedRevision != Revision || !ReferenceEquals(expected, Value) || ChangesFromCommitted(Value)
                ? "settings-changed" : check?.Invoke();
        UserSettings settings;
        try
        {
            // Freeze the user's confirmed values before yielding to retirement.
            settings = Snapshot(Canonical(proposed));
            if (feature is null && !CanUpdateOrdinary(settings))
                throw new ArgumentException("style-change-requires-session-controller");
        }
        catch (ArgumentException error) { return Rejected("invalid-settings", error); }
        if (Check() is { } initial)
            return Rejected(initial);
        try
        {
            if (prepare is not null && (feature is not null || ChangesFromCommitted(settings)))
            {
                bool ready = await prepare(settings);
                if (Check() is { } retired)
                    return Rejected(retired);
                if (!ready)
                    return Rejected("practice-retirement-unconfirmed");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Rejected("canceled");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Rejected("preparation-failed", error);
        }
        if (Check() is { } final)
            return Rejected(final);
        return Save(settings, feature, apply);
    }

    // Existing synchronous setup/fixture clients retain their exception contract.
    // Production saves consume CommitAsync's result and never infer a commit.
    private static void RethrowFailure(SettingsCommitResult result)
    {
        if (result.Error is { } error)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}

public static class SettingsCommitPresentation
{
    public static string Message(SettingsCommitResult result) => result.State switch
    {
        SettingsCommitState.CommittedNotificationFailed => "已保存，请重新打开设置确认当前状态。",
        SettingsCommitState.ExternalEffectUnknown => "设置未保存。登录启动的系统状态未能确认，请检查系统登录启动设置。",
        _ when !result.Saved && result.Startup == SettingsStartupEffect.Applied =>
            "设置文件未保存，登录启动的系统设置已更改。请检查当前设置与系统登录启动状态。",
        _ when result.Saved => SettingsDraftPresentation.Create().SavedMessage,
        _ when result.Reason == "canceled" => "本次保存已取消。",
        _ when result.Reason == "settings-changed" => "设置已更新，请检查当前值后重试。",
        _ when result.Reason == "practice-retirement-unconfirmed" => "练习窗口尚未确认停止，设置未保存。请关闭练习窗口后再试。",
        _ when result.Reason == "preparation-failed" => "设置未保存，练习状态未能确认。请重新打开设置检查当前状态。",
        _ when result.Reason == "invalid-settings" => SettingsDraftPresentation.Create().InvalidMessage,
        _ => SettingsDraftPresentation.SaveUnconfirmedMessage
    };
}
