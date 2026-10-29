using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeLay.Core;
namespace TypeLay.Runtime;

public sealed record UserSettings(int Schema = 1, bool Enabled = true, string Source = "auto", string Target = "en",
    string[]? ExcludedApplications = null, bool Startup = false, string Theme = "system", bool OnboardingComplete = false, bool ExperimentalWriteback = false,
    WorkMode Mode = WorkMode.Automatic, string WakeShortcut = "Ctrl+Shift+F9", string DirectShortcut = "Ctrl+Shift+F10",
    [property: JsonConverter(typeof(SessionShortcutPreferenceConverter))] string AcceptShortcut = "Tab",
    [property: JsonConverter(typeof(SessionShortcutPreferenceConverter))] string ExitShortcut = "Esc",
    [property: JsonConverter(typeof(TranslationStylesConverter))]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TranslationStyles? Styles = null,
    [property: JsonConverter(typeof(TranslationGlossaryConverter))]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GlossarySnapshot? Glossary = null,
    [property: JsonConverter(typeof(SessionShortcutPreferenceConverter))] string Font = "claude",
    [property: JsonConverter(typeof(TextSizePreferenceConverter))] int TextSize = 15,
    [property: JsonConverter(typeof(SessionShortcutPreferenceConverter))] string Accent = "clay",
    [property: JsonConverter(typeof(SessionShortcutPreferenceConverter))] string UiLanguage = "system",
    [property: JsonConverter(typeof(BooleanPreferenceConverter))] bool ReduceMotion = false,
    [property: JsonConverter(typeof(BooleanPreferenceConverter))] bool ReduceTransparency = false,
    [property: JsonConverter(typeof(SessionShortcutPreferenceConverter))] string PauseShortcut = "",
    [property: JsonConverter(typeof(ApplicationListPreferenceConverter))]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ManagedApplications = null)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    public string[] Excluded => ExcludedApplications ?? [];
    [JsonIgnore]
    public string[] Managed => ManagedApplications ?? [];
    public LanguageDirection Direction => new(Source, Target);
}

// Only the settings file write failed. Host callbacks keep their original
// exceptions so callers cannot mistake an already-persisted update for failure.
public sealed class SettingsPersistenceException : IOException
{
    public SettingsPersistenceException(Exception innerException)
        : base("settings-persistence-failed", innerException) { }
}

public sealed class SettingsConflictException : InvalidOperationException
{
    public SettingsConflictException() : base("settings-changed") { }
}

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Action<bool> _setStartup;
    private readonly Func<string, string> _normalizeApplication;
    private UserSettings _committedValue;
    public UserSettings Value
    {
        get; private set;
    }
    public long Revision
    {
        get; private set;
    }
    // Ordinary settings retain their existing host retirement semantics.
    public event Action? Changed;
    public event Action? StyleChanged;
    public event Action? GlossarySaved;
    public SettingsStore(string path, Action<bool>? setStartup = null, Func<string, string>? normalizeApplication = null)
    {
        _path = path;
        _setStartup = setStartup ?? (_ => throw new NotSupportedException("startup-unavailable"));
        _normalizeApplication = normalizeApplication ?? (app => app.Trim());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        try
        {
            Value = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Value = new(); }
        if (Value.Schema != 1 || !ModelCatalog.IsSupportedSetting(Value.Source, Value.Target)
            || Value.Theme is not ("system" or "light" or "dark") || Value.Excluded.Length > 256)
            Value = new();
        // New preferences must not discard an existing user's privacy exclusions.
        if (!Enum.IsDefined(Value.Mode))
            Value = Value with
            {
                Mode = WorkMode.Automatic
            };
        if (!TranslationShortcut.TryParse(Value.WakeShortcut, out var wake)
            || !TranslationShortcut.TryParse(Value.DirectShortcut, out var direct) || wake == direct)
            Value = Value with
            {
                WakeShortcut = "Ctrl+Shift+F9",
                DirectShortcut = "Ctrl+Shift+F10"
            };
        var sessionKeys = SessionShortcutBindings.Restore(Value.AcceptShortcut, Value.ExitShortcut);
        Value = Value with
        {
            AcceptShortcut = sessionKeys.Accept,
            ExitShortcut = sessionKeys.Exit,
            Font = ValidFont(Value.Font) ? Value.Font : "claude",
            TextSize = ValidTextSize(Value.TextSize) ? Value.TextSize : 15,
            Accent = ValidAccent(Value.Accent) ? Value.Accent : "clay",
            UiLanguage = ValidUiLanguage(Value.UiLanguage) ? Value.UiLanguage : "system"
        };
        var loadedChords = new HashSet<string>(StringComparer.Ordinal);
        TranslationShortcut.TryParse(Value.WakeShortcut, out wake);
        TranslationShortcut.TryParse(Value.DirectShortcut, out direct);
        loadedChords.Add(wake!.ToString());
        loadedChords.Add(direct!.ToString());
        if (!loadedChords.Add(Value.AcceptShortcut))
            Value = Value with { AcceptShortcut = "Tab" };
        if (!loadedChords.Add(Value.ExitShortcut))
            Value = Value with { ExitShortcut = "Esc" };
        Value = Value with
        {
            PauseShortcut = TranslationShortcut.TryParse(Value.PauseShortcut, out var pause)
                && loadedChords.Add(pause!.ToString()) ? pause.ToString() : ""
        };
        // Loaded shortcuts may be valid but not canonically spelled yet.
        ShortcutSettingsDraft.From(Value).TryApply(Value, out var canonical, out _);
        TranslationStyles? loadedStyles;
        try
        {
            loadedStyles = CanonicalStyles(canonical!.Styles);
        }
        catch (ArgumentException) { loadedStyles = null; }
        Value = canonical! with
        {
            Styles = loadedStyles,
            Glossary = ValidLoadedGlossary(canonical!.Glossary)
        };
        try
        {
            Value = Value with { ManagedApplications = CanonicalManagedApplications(Value.ManagedApplications) };
        }
        catch (ArgumentException) { Value = Value with { ManagedApplications = null }; }
        _committedValue = Snapshot(Value);
    }
    public void Update(UserSettings settings)
    {
        settings = Canonical(settings);
        if (!CanUpdateOrdinary(settings))
            throw new ArgumentException("style-change-requires-session-controller");
        Save(settings, feature: null);
    }
    // Immediate saves and toast undo use the revision observed with their draft.
    // A stale request must fail before startup or any persistent state changes.
    public void Update(long expectedRevision, UserSettings settings)
    {
        if (expectedRevision != Revision || ChangesFromCommitted(Value))
            throw new SettingsConflictException();
        Update(settings);
    }
    public bool CanUpdateOrdinary(UserSettings settings) => StylesEqual(CanonicalStyles(settings.Styles), _committedValue.Styles)
        && GlossaryEqual(settings.Glossary, _committedValue.Glossary);

    internal void CommitStyles(long expectedRevision, TranslationStyles? styles, Action apply)
    {
        if (expectedRevision != Revision || ChangesFromCommitted(Value))
            throw new InvalidOperationException("settings-changed");
        Save(Value with
        {
            Styles = CanonicalStyles(styles)
        }, feature: "style", apply);
    }

    internal void CommitGlossary(long expectedRevision, GlossarySnapshot glossary, Action apply)
    {
        if (expectedRevision != Revision || ChangesFromCommitted(Value))
            throw new InvalidOperationException("settings-changed");
        Save(Value with
        {
            Glossary = glossary
        }, feature: "glossary", apply);
    }

    private void Save(UserSettings settings, string? feature, Action? apply = null)
    {
        if (settings.Startup != Value.Startup)
            _setStartup(settings.Startup);
        string temporary = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new SettingsPersistenceException(ex);
        }
        bool changed = ChangesFromCommitted(settings);
        _committedValue = Snapshot(settings);
        Value = settings;
        if (changed)
        {
            Revision++;
            if (feature is not null)
            {
                apply!();
                if (feature == "style")
                    StyleChanged?.Invoke();
                else
                    GlossarySaved?.Invoke();
            }
            else
                Changed?.Invoke();
        }
    }
    // UserSettings is only shallowly immutable. Keep the comparison baseline
    // independent of arrays exposed through Value or supplied by its caller.
    private static UserSettings Snapshot(UserSettings settings) => settings with
    {
        ExcludedApplications = settings.ExcludedApplications?.ToArray(),
        ManagedApplications = settings.ManagedApplications?.ToArray(),
        Styles = CopyStyles(settings.Styles),
        ExtensionData = settings.ExtensionData?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone())
    };

    // Uses the same canonical values and committed array snapshot as Update.
    // Hosts can retire an owned practice before a real change, without disrupting
    // drafts and practice when a settings dialog merely saves the current values.
    public bool HasChanges(UserSettings settings) => ChangesFromCommitted(Canonical(settings));
    private bool ChangesFromCommitted(UserSettings settings) =>
        (settings with
        {
            ExcludedApplications = null,
            ManagedApplications = null,
            Styles = null,
            Glossary = null,
            ExtensionData = null
        }) != (_committedValue with
        {
            ExcludedApplications = null,
            ManagedApplications = null,
            Styles = null,
            Glossary = null,
            ExtensionData = null
        })
        || !settings.Excluded.SequenceEqual(_committedValue.Excluded)
        || !settings.Managed.SequenceEqual(_committedValue.Managed)
        || !StylesEqual(settings.Styles, _committedValue.Styles)
        || !GlossaryEqual(settings.Glossary, _committedValue.Glossary)
        || !ExtensionDataEqual(settings.ExtensionData, _committedValue.ExtensionData);
    private static bool ExtensionDataEqual(Dictionary<string, JsonElement>? left, Dictionary<string, JsonElement>? right) =>
        (left?.Count ?? 0) == (right?.Count ?? 0) && (left ?? []).All(pair =>
            right?.TryGetValue(pair.Key, out var value) == true && JsonElement.DeepEquals(pair.Value, value));
    private UserSettings Canonical(UserSettings settings)
    {
        settings.Direction.Validate();
        if (!ModelCatalog.IsSupportedSetting(settings.Source, settings.Target))
            throw new ArgumentException("请选择支持的源语言和目标语言。");
        if (!Enum.IsDefined(settings.Mode))
            throw new ArgumentException("请选择有效的工作模式。");
        if (settings.Theme is not ("system" or "light" or "dark") || !ValidFont(settings.Font)
            || !ValidTextSize(settings.TextSize) || !ValidAccent(settings.Accent) || !ValidUiLanguage(settings.UiLanguage))
            throw new ArgumentException("请选择有效的外观设置。");
        if (!ShortcutSettingsDraft.From(settings).TryApply(settings, out var canonical, out var error))
            throw new ArgumentException(SettingsDraftPresentation.ShortcutErrorMessage(error));
        return canonical! with
        {
            Styles = CanonicalStyles(canonical!.Styles),
            ManagedApplications = CanonicalManagedApplications(canonical.ManagedApplications)
        };
    }
    private string[]? CanonicalManagedApplications(string[]? applications)
    {
        if (applications is null)
            return null;
        if (applications.Length > 256)
            throw new ArgumentException("invalid-managed-applications");
        var result = new List<string>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string application in applications)
        {
            string? normalized = NormalizeStyleApplication(application);
            if (normalized is null)
                throw new ArgumentException("invalid-managed-applications");
            if (identities.Add(normalized))
                result.Add(normalized);
        }
        return result.ToArray();
    }
    private static bool ValidFont(string? value) => value is "claude" or "sans" or "system";
    private static bool ValidTextSize(int value) => value is >= 13 and <= 19;
    private static bool ValidAccent(string? value) => value is "clay" or "ocean" or "sage" or "iris" or "graphite";
    private static bool ValidUiLanguage(string? value) => value is "system" or "zh-CN" or "en";
    public string? NormalizeStyleApplication(string? application)
    {
        if (string.IsNullOrWhiteSpace(application))
            return null;
        string value = _normalizeApplication(application);
        return value.Length is > 0 and <= 128 && !value.Any(char.IsControl) ? value : null;
    }
    private TranslationStyles? CanonicalStyles(TranslationStyles? styles)
    {
        if (styles is null)
            return null;
        if (!Enum.IsDefined(styles.Default) || styles.Applications?.Count > 256)
            throw new ArgumentException("invalid-style-settings");
        var applications = new SortedDictionary<string, TranslationStyle>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in styles.Applications ?? new Dictionary<string, TranslationStyle>())
        {
            string? application = NormalizeStyleApplication(pair.Key);
            if (application is null || !Enum.IsDefined(pair.Value) || !applications.TryAdd(application, pair.Value))
                throw new ArgumentException("invalid-style-settings");
        }
        return new(styles.Default, new ReadOnlyDictionary<string, TranslationStyle>(applications));
    }
    private static TranslationStyles? CopyStyles(TranslationStyles? styles) => styles is null ? null
        : new(styles.Default, new ReadOnlyDictionary<string, TranslationStyle>(
            (styles.Applications ?? new Dictionary<string, TranslationStyle>()).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)));
    private static bool StylesEqual(TranslationStyles? left, TranslationStyles? right) => left is null ? right is null
        : right is not null && left.Default == right.Default
          && (left.Applications?.Count ?? 0) == (right.Applications?.Count ?? 0)
          && (left.Applications ?? new Dictionary<string, TranslationStyle>()).All(pair =>
              right.Applications?.TryGetValue(pair.Key, out var value) == true && value == pair.Value);

    private static bool GlossaryEqual(GlossarySnapshot? left, GlossarySnapshot? right) => left is null ? right is null
        : right is not null && left.Version == right.Version && left.SameRules(right);
    internal static GlossarySnapshot ValidateGlossary(GlossarySnapshot value)
    {
        if (value.Rules.Any(rule => rule.SourceLanguage is not null && !ModelCatalog.Packages.Packages.SelectMany(package => package.Directions).Any(direction =>
                direction.Source == rule.SourceLanguage && direction.Target == rule.TargetLanguage)))
            throw new ArgumentException("invalid-glossary");
        return value;
    }
    private static GlossarySnapshot? ValidLoadedGlossary(GlossarySnapshot? value)
    {
        try
        {
            return value is null ? null : ValidateGlossary(value);
        }
        catch (ArgumentException) { return null; }
    }

    public UserSettings Excluding(string app)
    {
        app = _normalizeApplication(app);
        if (app.Length is 0 or > 128 || Value.Excluded.Length >= 256)
            return Value;
        return Value with
        {
            ExcludedApplications = Value.Excluded.Append(app).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }
    public void Exclude(string app)
    {
        var next = Excluding(app);
        if (!ReferenceEquals(next, Value))
            Update(next);
    }
}

// These newly introduced preferences must not make an older settings file lose
// unrelated preferences when their JSON value has an invalid type.
internal sealed class ApplicationListPreferenceConverter : JsonConverter<string[]>
{
    public override string[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 256
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            return null;
        return value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

internal sealed class TextSizePreferenceConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
            return value;
        reader.Skip();
        return 15;
    }
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

internal sealed class BooleanPreferenceConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            return reader.GetBoolean();
        reader.Skip();
        return false;
    }
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

internal sealed class SessionShortcutPreferenceConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();
        reader.Skip();
        return null;
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

// Null keeps existing installations on the ordinary translation path until an
// explicit style choice. Persisted choices survive an unsupported model switch.
public sealed record TranslationStyles(TranslationStyle Default = TranslationStyle.Natural,
    IReadOnlyDictionary<string, TranslationStyle>? Applications = null)
{
    public TranslationStyle Resolve(string? application) => application is not null
        && Applications?.TryGetValue(application, out var style) == true ? style : Default;
}

internal sealed class TranslationStylesConverter : JsonConverter<TranslationStyles>
{
    public override TranslationStyles? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("Default", out var global)
            || !TryStyle(global, out var style))
            return null;
        var applications = new Dictionary<string, TranslationStyle>(StringComparer.OrdinalIgnoreCase);
        if (value.TryGetProperty("Applications", out var overrides))
        {
            if (overrides.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var pair in overrides.EnumerateObject())
            {
                if (applications.Count >= 256 || string.IsNullOrWhiteSpace(pair.Name) || pair.Name.Length > 128
                    || pair.Name.Any(char.IsControl) || !TryStyle(pair.Value, out var selected)
                    || !applications.TryAdd(pair.Name, selected))
                    return null;
            }
        }
        return new(style, new ReadOnlyDictionary<string, TranslationStyle>(applications));
    }
    private static bool TryStyle(JsonElement value, out TranslationStyle style)
    {
        style = value.ValueKind == JsonValueKind.String ? value.GetString() switch
        {
            "natural" => TranslationStyle.Natural,
            "formal" => TranslationStyle.Formal,
            _ => (TranslationStyle)0
        } : (TranslationStyle)0;
        return Enum.IsDefined(style);
    }
    public override void Write(Utf8JsonWriter writer, TranslationStyles value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Default", Name(value.Default));
        writer.WriteStartObject("Applications");
        foreach (var pair in value.Applications ?? new Dictionary<string, TranslationStyle>())
            writer.WriteString(pair.Key, Name(pair.Value));
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
    private static string Name(TranslationStyle style) => style switch
    {
        TranslationStyle.Natural => "natural",
        TranslationStyle.Formal => "formal",
        _ => throw new JsonException("invalid-style")
    };
}

public enum StyleScope
{
    Global, CurrentApplication
}
public sealed record StyleChangeResult(bool Saved, string Code);
public sealed record StyleOverrideRemovalState(long SettingsRevision, bool CanRemove, string? UnavailableReason);
// Technical availability is not language-quality approval or an open UI entry.
public sealed record StyleSettingsState(long SettingsRevision, TranslationStyles? SavedPreferences,
    TranslationStyle? EffectiveStyle, string? Application, SessionViewToken? Session,
    bool TechnicalAvailable, string? UnavailableReason, bool CanChange = false, bool CanReset = false, string? ResetUnavailableReason = null, bool IsReleased = false);

internal sealed class TranslationGlossaryConverter : JsonConverter<GlossarySnapshot>
{
    public override GlossarySnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("Version", out var version) || !version.TryGetInt64(out long revision)
                || !root.TryGetProperty("Rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
                return null;
            var parsed = new List<GlossaryRule>();
            foreach (var rule in rules.EnumerateArray())
            {
                if (parsed.Count >= GlossarySnapshot.MaxRules || rule.ValueKind != JsonValueKind.Object
                    || rule.EnumerateObject().Any(property => property.Name is not ("SourceTerm" or "TargetTerm" or "SourceLanguage" or "TargetLanguage"))
                    || rule.EnumerateObject().Select(property => property.Name).Distinct().Count() != rule.EnumerateObject().Count())
                    return null;
                parsed.Add(new(rule.GetProperty("SourceTerm").GetString()!, rule.GetProperty("TargetTerm").GetString()!,
                    rule.TryGetProperty("SourceLanguage", out var a) ? a.GetString() : null,
                    rule.TryGetProperty("TargetLanguage", out var b) ? b.GetString() : null));
            }
            return SettingsStore.ValidateGlossary(new(revision, parsed));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            return null;
        }
    }
    public override void Write(Utf8JsonWriter writer, GlossarySnapshot value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("Version", value.Version);
        writer.WritePropertyName("Rules");
        JsonSerializer.Serialize(writer, value.Rules, options);
        writer.WriteEndObject();
    }
}
public sealed record GlossaryChangeResult(bool Saved, string Code);
public sealed record GlossarySettingsState(long SettingsRevision, GlossarySnapshot? SavedSnapshot,
    GlossarySnapshot? AppliedSnapshot, SessionViewToken? Session, bool TechnicalAvailable, string? UnavailableReason,
    bool CanSave = false, bool CanApply = false, string? ApplyUnavailableReason = null, bool IsReleased = false);
