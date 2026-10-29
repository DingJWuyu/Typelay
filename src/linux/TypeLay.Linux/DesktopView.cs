using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.Linux;

internal sealed class DesktopView(DesktopLink link, DesktopInput input) : ISessionView
{
    private string? _last;
    private bool _editing;
    private bool _sourceGlossary;
    private SessionGlossaryFocus? _glossaryFocus;
    public Func<SessionViewToken, SessionGlossaryEntry?>? BeginGlossaryRequested
    {
        get; set;
    }
    public Func<SessionGlossaryRequest, Task<GlossaryChangeResult>>? AddGlossaryRequested
    {
        get; set;
    }
    public Action<Guid>? CloseGlossaryRequested
    {
        get; set;
    }
    public SessionGlossaryFocus? GlossaryEntryFocus => _glossaryFocus is { } focus
        && (_editing || _sourceGlossary) && focus.Token == Token && input.GetFocus(focus.Token.Target) == FocusMember.Overlay ? focus : null;
    public bool SupportsSourceGlossaryEntry(SessionViewToken token) => token == Token
        && input.SupportsSourceGlossaryEntry(token);
    public async Task<bool> ReturnGlossaryToSourceAsync(SessionGlossaryEntry entry)
    {
        if (!entry.SourceOrigin || !_sourceGlossary || entry.Token != Token
            || GlossaryEntryFocus is not { Ime: ImeState.Clear } focus || focus.FormId != entry.FormId)
            return false;
        var reply = await link.Ask("sessionGlossaryReturn", new
        {
            entry.FormId,
            entry.Token
        });
        return entry.Token == Token && input.GetFocus(entry.Token.Target) == FocusMember.Source
            && reply.ValueKind == JsonValueKind.Object && reply.EnumerateObject().Count() == 3
            && reply.TryGetProperty("formId", out var id) && id.TryGetGuid(out var formId) && formId == entry.FormId
            && reply.TryGetProperty("token", out var token) && token.Deserialize<SessionViewToken>(DesktopLink.Json) == entry.Token
            && reply.TryGetProperty("returned", out var returned) && returned.ValueKind == JsonValueKind.True;
    }
    public void SetGlossaryFocus(SessionGlossaryFocus? focus)
    {
        _glossaryFocus = focus;
        if (GlossaryEntryFocus is null)
            _glossaryFocus = null;
    }
    public void RefreshGlossaryFocus()
    {
        if (GlossaryEntryFocus is null)
            _glossaryFocus = null;
    }
    private ModelPackageManagementFact? _packages;
    public SessionViewToken? Token
    {
        get; private set;
    }
    public Func<SessionViewToken, bool>? BeginEditing
    {
        get; set;
    }
    public Action<string>? DraftChanged
    {
        get; set;
    }
    public Action<SessionViewToken>? ReturnRequested
    {
        get; set;
    }
    public Action<SessionViewToken>? EscapeRequested
    {
        get; set;
    }
    public Action<SessionViewToken>? AlternativeRequested
    {
        get; set;
    }
    public Func<SessionStyleRequest, Task<StyleChangeResult>>? StyleRequested
    {
        get; set;
    }
    public Action? RegenerateRequested
    {
        get; set;
    }
    public Action? ExcludeRequested
    {
        get; set;
    }
    public Action<long>? ResumeRequested
    {
        get; set;
    }
    public bool EditingComposition
    {
        get; set;
    }
    public void Configure(UserSettings settings)
    {
        link.Send("settings", new
        {
            settings.Schema,
            settings.Enabled,
            settings.Source,
            settings.Target,
            settings.ExcludedApplications,
            settings.ManagedApplications,
            settings.Startup,
            settings.Theme,
            settings.OnboardingComplete,
            settings.ExperimentalWriteback,
            settings.Mode,
            settings.WakeShortcut,
            settings.DirectShortcut,
            settings.AcceptShortcut,
            settings.ExitShortcut,
            settings.PauseShortcut,
            settings.Font,
            settings.TextSize,
            settings.Accent,
            settings.UiLanguage,
            settings.ReduceMotion,
            settings.ReduceTransparency
        });
        ConfigureGuidance(settings);
    }
    public void ConfigureModelPackages(ModelPackageManagementFact packages, UserSettings settings)
    {
        _packages = packages;
        ConfigureGuidance(settings);
    }
    private void ConfigureGuidance(UserSettings settings)
    {
        var guidance = SettingsGuidancePresentation.Create(settings, exclusionsNeedSave: true);
        string defaultTarget = new UserSettings().Target;
        link.Send("settingsGuidance", new
        {
            guidance.ModeDescription,
            guidance.AcceptanceDescription,
            guidance.SaveScopeDescription,
            SettingsDraftPresentation.SaveUnconfirmedMessage,
            SettingsDraftPresentation.UnknownSaveMessage,
            languageChoices = new
            {
                source = LanguageChoices(settings.Source, source: true),
                target = LanguageChoices(settings.Target, source: false).OrderBy(choice => choice.Code == defaultTarget ? 0 : 1)
            }
        });
    }
    private ModelLanguageChoice[] LanguageChoices(string saved, bool source)
    {
        var legacy = source ? ModelCatalog.SourceChoices : ModelCatalog.TargetChoices;
        var installed = _packages is { InventoryKnown: true } facts
            ? facts.InstalledPackages.Where(item => item.Package.Backend == "argos"
                || item.Package.Backend == "hy-mt-llama-cpu" && facts is { HyRuntimeAvailable: true, HyUnavailableReason: null }
                || item.Package.Backend == "index-translate-llama-cpu" && facts is { IndexRuntimeAvailable: true, IndexUnavailableReason: null }).SelectMany(item => item.Directions)
            : [];
        var choices = legacy.Concat(installed.Select(direction => source
                ? new ModelLanguageChoice(direction.Source, direction.SourceName)
                : new ModelLanguageChoice(direction.Target, direction.TargetName)))
            .DistinctBy(choice => choice.Code).Select(LanguageChoice).ToList();
        if (choices.All(choice => choice.Code != saved))
        {
            var known = ModelCatalog.Packages.Packages.SelectMany(package => package.Directions)
                .SelectMany(direction => new[] { new ModelLanguageChoice(direction.Source, direction.SourceName),
                    new ModelLanguageChoice(direction.Target, direction.TargetName) }).FirstOrDefault(choice => choice.Code == saved);
            choices.Add(new(saved, (known?.Label ?? saved) + "（当前不可用）"));
        }
        return choices.ToArray();
    }
    private static ModelLanguageChoice LanguageChoice(ModelLanguageChoice choice) => choice with
    {
        Label = choice.Code switch
        {
            "auto" => "自动识别",
            "en" => "英语",
            _ => choice.Label
        }
    };
    public void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null, SessionViewContext? context = null)
    {
        var presentation = Presentation.From(session.State, canAccept, session.Preview.Length > 0,
            shortcuts: context?.Bindings);
        if (!presentation.Visible || session.Current is not { } source)
        {
            HideQuietly();
            return;
        }
        Token = context?.Token;
        _editing = session.State == SessionState.EditingTranslation;
        _sourceGlossary = session.State is SessionState.ValidTranslation or SessionState.EditedTranslationPending;
        RefreshGlossaryFocus();
        var value = new
        {
            presentation,
            viewContext = context,
            sessionShortcuts = SettingsDraftPresentation.SessionShortcuts(
                context?.Bindings ?? SessionShortcutBindings.Restore(settings.AcceptShortcut, settings.ExitShortcut)),
            canAccept,
            state = session.State,
            source = new
            {
                source.Target,
                source.Bounds,
                source.Ime
            },
            text = presentation.EditorOwnsText ? session.Draft : session.Preview,
            editable = session.Candidate.Length > 0 && context?.CanEdit is true,
            status = statusOverride ?? presentation.Status,
            sourceLanguage = settings.Source,
            targetLanguage = settings.Target
        };
        string key = JsonSerializer.Serialize(value, DesktopLink.Json);
        if (key == _last)
            return;
        _last = key;
        link.Send("render", value);
    }
    public void RenderPaused(PauseAnchor anchor, long revision)
    {
        Token = null;
        _editing = _sourceGlossary = false;
        _glossaryFocus = null;
        var value = new
        {
            presentation = Presentation.From(SessionState.Paused, false, hasPauseAnchor: true),
            canAccept = false,
            state = SessionState.Paused,
            source = new
            {
                anchor.Target,
                anchor.Bounds
            },
            pauseRevision = revision
        };
        string key = JsonSerializer.Serialize(value, DesktopLink.Json);
        if (key == _last)
            return;
        _last = key;
        EditingComposition = false;
        link.Send("render", value);
    }
    public void HideQuietly()
    {
        Token = null;
        _editing = _sourceGlossary = false;
        _glossaryFocus = null;
        if (_last == "hidden")
            return;
        _last = "hidden";
        EditingComposition = false;
        link.Send("hide");
    }
    public void ResumeEditor() => link.Send("resumeEditor");
}
