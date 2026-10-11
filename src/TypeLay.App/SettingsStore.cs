using Microsoft.Win32;
using System.Text.Json;
using TypeLay.Core;
namespace TypeLay.App;
internal sealed record UserSettings(int Schema = 1, bool Enabled = true, string Source = "auto", string Target = "en",
    string[]? ExcludedApplications = null, bool Startup = false, string Theme = "system", bool OnboardingComplete = false, bool ExperimentalWriteback = false)
{
    public string[] Excluded => ExcludedApplications ?? [];
    public LanguageDirection Direction => new(Source, Target);
}
internal sealed class SettingsStore
{
    private readonly string _path;
    public UserSettings Value { get; private set; }
    public event Action? Changed;
    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.Data, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        try { Value = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Value = new(); }
        if (Value.Schema != 1 || Value.Source is not ("auto" or "zh" or "en") || Value.Target is not ("zh" or "en")
            || Value.Theme is not ("system" or "light" or "dark") || Value.Excluded.Length > 256) Value = new();
    }
    public void Update(UserSettings settings)
    {
        settings.Direction.Validate();
        if (settings.Startup != Value.Startup) SetStartup(settings.Startup);
        string temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
        File.Move(temporary, _path, true);
        Value = settings; Changed?.Invoke();
    }
    public void Exclude(string app)
    {
        app = Path.GetFileNameWithoutExtension(app).Trim().ToLowerInvariant();
        if (app.Length is 0 or > 128 || Value.Excluded.Length >= 256) return;
        Update(Value with { ExcludedApplications = Value.Excluded.Append(app).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
    }
    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("TypeLay", "\"" + Path.Combine(AppPaths.Home, "TypeLay.exe") + "\"");
        else key.DeleteValue("TypeLay", false);
    }
}
