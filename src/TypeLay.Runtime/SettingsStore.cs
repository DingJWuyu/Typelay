using System.Text.Json;
using TypeLay.Core;
namespace TypeLay.Runtime;

public sealed record UserSettings(int Schema = 1, bool Enabled = true, string Source = "auto", string Target = "en",
    string[]? ExcludedApplications = null, bool Startup = false, string Theme = "system", bool OnboardingComplete = false, bool ExperimentalWriteback = false,
    WorkMode Mode = WorkMode.Automatic, string WakeShortcut = "Ctrl+Shift+F9", string DirectShortcut = "Ctrl+Shift+F10")
{
    public string[] Excluded => ExcludedApplications ?? [];
    public LanguageDirection Direction => new(Source, Target);
}
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Action<bool> _setStartup;
    private readonly Func<string, string> _normalizeApplication;
    public UserSettings Value
    {
        get; private set;
    }
    public event Action? Changed;
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
        if (Value.Schema != 1 || Value.Source is not ("auto" or "zh" or "en") || Value.Target is not ("zh" or "en")
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
    }
    public void Update(UserSettings settings)
    {
        settings.Direction.Validate();
        if (settings.Source is not ("auto" or "zh" or "en") || settings.Target is not ("zh" or "en"))
            throw new ArgumentException("请选择支持的源语言和目标语言。");
        if (!Enum.IsDefined(settings.Mode)
            || !TranslationShortcut.TryParse(settings.WakeShortcut, out var wake)
            || !TranslationShortcut.TryParse(settings.DirectShortcut, out var direct) || wake == direct)
            throw new ArgumentException("请选择有效模式和两个不同的快捷键组合。");
        settings = settings with
        {
            WakeShortcut = wake!.ToString(),
            DirectShortcut = direct!.ToString()
        };
        if (settings.Startup != Value.Startup)
            _setStartup(settings.Startup);
        string temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        File.Move(temporary, _path, true);
        Value = settings;
        Changed?.Invoke();
    }
    public void Exclude(string app)
    {
        app = _normalizeApplication(app);
        if (app.Length is 0 or > 128 || Value.Excluded.Length >= 256)
            return;
        Update(Value with
        {
            ExcludedApplications = Value.Excluded.Append(app).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        });
    }
}
