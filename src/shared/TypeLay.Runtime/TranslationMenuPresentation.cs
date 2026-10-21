using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed record SessionStyleRequest(SessionViewToken Token, long SettingsRevision, TranslationStyle? Style);
public sealed record TranslationMenuState(long SettingsRevision, bool StyleReleased, bool CanChangeStyle,
    bool CanInheritStyle, TranslationStyle? ApplicationStyle, string? StyleUnavailableReason,
    string? InheritUnavailableReason, bool AlternativeReleased, bool CanRequestAlternative,
    string? AlternativeUnavailableReason, string? Feedback, bool GlossaryReleased = false,
    bool CanOpenGlossary = false, string? GlossaryUnavailableReason = null);

internal static class TranslationMenuPresentation
{
    internal static string? Reason(string? code) => code switch
    {
        null => null,
        "manual-style-locked" => "请先完成当前编辑",
        "style-busy" or "alternative-busy" or "practice-retirement-unconfirmed" => "请等待当前操作完成",
        "style-stale" or "style-application-unavailable" or "alternative-source-unavailable" => "当前输入已变化，请重新打开菜单",
        "style-save-failed" => "未能保存，请重试",
        "style-canceled" => "操作已取消",
        "glossary-combination-unavailable" => "暂不支持同时应用风格与术语表",
        "glossary-combination-not-released" => "当前模型方向尚未开放风格与术语同时应用",
        "style-not-released" => "当前模型方向尚未开放翻译风格",
        "alternative-not-released" => "当前模型方向尚未开放换种说法",
        "style-settings-full" => "应用风格数量已达上限",
        "invalid-style" => "请选择有效的翻译风格",
        _ => "当前状态暂不可用"
    };
}

public sealed partial class SessionController
{
    private (SessionViewToken Token, long Revision, string Text)? _translationMenuFeedback;
    private (SessionViewToken Token, long Revision)? _alternativeMenuRequest;

    // Read-only projection of the same qualification used by each actual command.
    private TranslationMenuState GetTranslationMenuState()
    {
        var style = GetStyleSettingsState(StyleScope.CurrentApplication, TranslationStyle.Natural);
        var inherit = GetStyleSettingsState(StyleScope.CurrentApplication, null);
        var alternative = GetAlternativeState();
        string? glossaryReason = QualifyGlossaryEntry(style.Session);
        TranslationStyle? applicationStyle = style.Application is { } application
            && style.SavedPreferences?.Applications?.TryGetValue(application, out var selected) == true ? selected : null;
        string? feedback = _translationMenuFeedback is { } current && current.Token == CurrentTranslationToken()
            && current.Revision == _commandFeedbackRevision ? current.Text : null;
        return new(style.SettingsRevision, style.IsReleased, style.IsReleased && style.CanChange,
            style.IsReleased && inherit.CanChange, applicationStyle, TranslationMenuPresentation.Reason(style.UnavailableReason),
            TranslationMenuPresentation.Reason(inherit.UnavailableReason), alternative.IsReleased,
            alternative.IsReleased && alternative.CanRequest, TranslationMenuPresentation.Reason(alternative.UnavailableReason), feedback,
            _translationRelease.IsReleased(TranslationOperation.Glossary), glossaryReason is null,
            GlossaryEntryPresentation.Message(glossaryReason));
    }

    private async Task<StyleChangeResult> ChangeViewStyleAsync(SessionStyleRequest request)
    {
        long feedbackRevision = _commandFeedbackRevision;
        var result = !_translationRelease.IsReleased(TranslationOperation.Style)
            ? new StyleChangeResult(false, "style-not-released")
            : await ChangeStyleAsync(StyleScope.CurrentApplication, request.Style, request.SettingsRevision, request.Token);
        if (!result.Saved)
            SetTranslationMenuFeedback(request.Token, feedbackRevision, TranslationMenuPresentation.Reason(result.Code));
        return result;
    }

    private void SetTranslationMenuFeedback(SessionViewToken token, long revision, string? text)
    {
        if (text is null || token != CurrentTranslationToken() || revision != _commandFeedbackRevision)
            return;
        _translationMenuFeedback = (token, revision, text);
        Render();
    }

    private void SetAlternativeMenuStatus(string text, long generation, Guid commandGeneration, TargetId target, long revision)
    {
        SetCommandStatus(text, revision);
        var token = new SessionViewToken(target, commandGeneration, generation);
        if (_alternativeMenuRequest == (token, revision))
            SetTranslationMenuFeedback(token, revision, text);
    }
}
