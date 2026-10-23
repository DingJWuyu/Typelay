using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeLay.Runtime;

public sealed record ModelDirection(string Id, string Source, string Target, string SourceName,
    string TargetName, string Version, string Url, string SelfCheckText, ModelArchivePin? ArchivePin = null);
// Project-reviewed archive identity, not an upstream signature or proof about an installed copy.
public sealed record ModelArchivePin(string Sha256, long Bytes, long UnpackedBytes);
public sealed record ModelLanguageChoice(string Code, string Label);
public sealed record ModelPackageFile(string Path, string Sha256);

// This release's fixed preparation catalog. It is not evidence that any local
// model is installed or ready; only the worker handshake grants that capability.
public static class ModelCatalog
{
    public static ModelPackageCatalog Packages
    {
        get;
    }
    public static IReadOnlyList<ModelDirection> LegacyDirections => Packages.LegacyDirections;
    public static IReadOnlyList<ModelDirection> DefaultDirections => Packages.DefaultDirections;

    public static IReadOnlyList<ModelDirection> Directions
    {
        get;
    }
    public static IReadOnlyList<ModelLanguageChoice> SourceChoices
    {
        get;
    }
    public static IReadOnlyList<ModelLanguageChoice> TargetChoices
    {
        get;
    }

    static ModelCatalog()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.ModelCatalog.json")
            ?? throw new InvalidDataException("model-catalog-missing");
        using var document = JsonDocument.Parse(stream);
        Packages = ModelPackageCatalog.Parse(document.RootElement);
        Directions = LegacyDirections;
        var languages = Directions.Select(direction => new ModelLanguageChoice(direction.Source, direction.SourceName))
            .Concat(Directions.Select(direction => new ModelLanguageChoice(direction.Target, direction.TargetName)))
            .DistinctBy(language => language.Code).ToArray();
        SourceChoices = Array.AsReadOnly(new[] { new ModelLanguageChoice("auto", "Auto") }
            .Concat(languages.Where(language => Directions.Any(direction => direction.Source == language.Code))).ToArray());
        TargetChoices = Array.AsReadOnly(languages.Where(language => Directions.Any(direction => direction.Target == language.Code)).ToArray());
    }

    public static IReadOnlyList<ModelPackageSelection> ReadInstallationSelection(JsonElement inventory) =>
        Packages.ReadInstallationSelection(inventory);

    public static IReadOnlyList<ModelPackageSelection> ReadActiveSelection(JsonElement inventory) =>
        Packages.ReadActiveSelection(inventory);

    // Installation intent is not verified runtime capability. Only a worker
    // that has checked files and loaded every requested model can grant Ready.
    public static IReadOnlyList<string> ReadInstallationDirections(JsonElement inventory)
    {
        if (inventory.ValueKind != JsonValueKind.Object
            || !inventory.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version)
            || version is not (1 or 2))
            throw new InvalidDataException("model-inventory");
        if (!inventory.TryGetProperty("directions", out var value))
        {
            if (version != 1)
                throw new InvalidDataException("model-inventory");
            return Array.AsReadOnly(DefaultDirections.Select(direction => direction.Id).ToArray());
        }
        if (value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("model-inventory");
        string[] ids = value.EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (!IsValidDirectionSet(ids) || version == 1 && !IsCompleteDirectionSet(ids))
            throw new InvalidDataException("model-inventory");
        return Array.AsReadOnly(Directions.Where(direction => ids.Contains(direction.Id, StringComparer.Ordinal))
            .Select(direction => direction.Id).ToArray());
    }

    public static ModelDirection Get(string id) => Directions.FirstOrDefault(direction => direction.Id == id)
        ?? throw new ArgumentException("model-direction", nameof(id));

    // Settings remain valid while models are absent or offline. Same-language
    // choices preserve the worker's existing no-translation/manual-append path.
    public static bool IsSupportedLegacySetting(string source, string target) =>
        TargetChoices.Any(choice => choice.Code == target)
        && (source == "auto" || source == target && SourceChoices.Any(choice => choice.Code == source)
            || Directions.Any(direction => direction.Source == source && direction.Target == target));

    public static bool IsSupportedSetting(string source, string target)
    {
        var directions = Packages.Packages.SelectMany(package => package.Directions).ToArray();
        return directions.Any(direction => direction.Target == target)
            && (source == "auto" || source == target
                || directions.Any(direction => direction.Source == source && direction.Target == target));
    }

    public static long? PackageDownloadBytes(ModelPackage package)
    {
        if (package.Backend == "argos")
            return package.ArchivePin?.Bytes;
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.HyModelLock.json")
            ?? throw new InvalidDataException("model-lock-missing");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (package.Backend != "hy-mt-llama-cpu" || package.Id != root.GetProperty("package_id").GetString()
            || package.Version != root.GetProperty("version").GetString())
            return null;
        return checked(root.GetProperty("model").GetProperty("bytes").GetInt64()
            + root.GetProperty("model_license").GetProperty("bytes").GetInt64());
    }

    // Catalog availability does not expand installation consent. The fixed Hy
    // model lock keeps new format-1 installs at twelve even in a candidate build.
    public static IReadOnlyList<ModelDirection> DefaultInstallationDirections(ModelPackage package)
    {
        if (package.Backend == "argos")
            return package.Directions;
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.HyModelLock.json")
            ?? throw new InvalidDataException("model-lock-missing");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (package.Backend != "hy-mt-llama-cpu" || package.Id != root.GetProperty("package_id").GetString()
            || package.Version != root.GetProperty("version").GetString())
            throw new InvalidDataException("model-lock-identity");
        return Array.AsReadOnly(root.GetProperty("directions").EnumerateArray().Select(item =>
            package.Directions.SingleOrDefault(direction => direction.Id == item.GetString())
                ?? throw new InvalidDataException("model-lock-directions")).ToArray());
    }

    // Request syntax is separate from loaded pair availability. The worker owns
    // Auto, same-language and manual append ordering before pair lookup.
    public static bool IsSupportedPackageRequest(string source, string target)
    {
        var languages = Packages.Packages.SelectMany(package => package.Directions)
            .SelectMany(direction => new[] { direction.Source, direction.Target }).ToHashSet(StringComparer.Ordinal);
        return languages.Contains(target) && (source == "auto" || languages.Contains(source));
    }

    public static bool IsCompleteDirectionSet(IEnumerable<string>? ids) =>
        ValidatedDirectionCount(ids) == Directions.Count;

    public static bool IsValidDirectionSet(IEnumerable<string>? ids) => ValidatedDirectionCount(ids) > 0;

    private static int ValidatedDirectionCount(IEnumerable<string>? ids)
    {
        if (ids is null)
            return 0;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (id is null || !seen.Add(id) || !Directions.Any(direction => direction.Id == id))
                return 0;
        }
        return seen.Count;
    }
}

public sealed class ModelPackage
{
    internal ModelPackage(string id, string version, string backend, bool preview, string url,
        ModelDirection[] directions, ModelArchivePin? archivePin)
    {
        Id = id;
        Version = version;
        Backend = backend;
        Preview = preview;
        Url = url;
        Directions = Array.AsReadOnly(directions);
        ArchivePin = archivePin;
    }

    public string Id
    {
        get;
    }
    public string Version
    {
        get;
    }
    public string Backend
    {
        get;
    }
    public bool Preview
    {
        get;
    }
    public string Url
    {
        get;
    }
    public IReadOnlyList<ModelDirection> Directions
    {
        get;
    }
    public ModelArchivePin? ArchivePin
    {
        get;
    }
}

// A selection is intent only. It does not check files, load a backend or grant Ready.
public sealed class ModelPackageSelection
{
    internal ModelPackageSelection(ModelPackage package, ModelDirection[] directions, ModelPackageFile[]? files = null)
    {
        Package = package;
        Directions = Array.AsReadOnly(directions);
        Files = Array.AsReadOnly(files ?? []);
    }

    public IReadOnlyList<ModelPackageFile> Files
    {
        get;
    }

    public ModelPackage Package
    {
        get;
    }
    public IReadOnlyList<ModelDirection> Directions
    {
        get;
    }
}

public sealed class ModelPackageCatalog
{
    private static readonly (string Package, string Direction)[] LegacyPackages =
        [("argos-zh-en", "zh_en"), ("argos-en-zh", "en_zh")];

    private ModelPackageCatalog(ModelPackage[] packages) => Packages = Array.AsReadOnly(packages);

    public IReadOnlyList<ModelPackage> Packages
    {
        get;
    }
    public IReadOnlyList<ModelDirection> DefaultDirections => LegacyDirections;
    public IReadOnlyList<ModelDirection> LegacyDirections => Array.AsReadOnly(LegacyPackages.Select(identity =>
    {
        var package = Packages.FirstOrDefault(package => package.Id == identity.Package);
        if (package is not { Backend: "argos", Version: "1.9" })
            throw new InvalidDataException("model-catalog-invalid");
        return package.Directions.FirstOrDefault(direction => direction.Id == identity.Direction)
            ?? throw new InvalidDataException("model-catalog-invalid");
    }).ToArray());

    // Catalog schema 2 describes packages. installed.json has a separate schema.
    public static ModelPackageCatalog Parse(JsonElement value)
    {
        const string error = "model-catalog-invalid";
        RequireUniqueObjectKeys(value, error);
        if (value.ValueKind != JsonValueKind.Object || Schema(value, error) != 2
            || value.TryGetProperty("directions", out _))
            throw new InvalidDataException(error);
        var entries = NonemptyArray(value, "packages", error);
        List<ModelPackage> packages = [];
        HashSet<string> packageIds = new(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            string id = Text(entry, "id", error), version = Text(entry, "version", error);
            string backend = Text(entry, "backend", error), address = Text(entry, "url", error);
            if (!Regex.IsMatch(id, @"\A[a-z][a-z0-9-]{0,63}\z") || !packageIds.Add(id)
                || backend is not ("argos" or "hy-mt-llama-cpu")
                || !Regex.IsMatch(version, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")
                || !entry.TryGetProperty("preview", out var preview)
                || preview.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrEmpty(url.Host) || url.UserInfo.Length != 0 || url.Port != 443
                || url.Query.Length != 0 || url.Fragment.Length != 0
                || backend == "argos" && (url.Host != "argos-net.com" || !Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\z")))
                throw new InvalidDataException(error);
            List<ModelDirection> directions = [];
            var pin = ArchivePin(entry, backend, error);
            HashSet<string> directionIds = new(StringComparer.Ordinal);
            foreach (var item in NonemptyArray(entry, "directions", error).EnumerateArray())
            {
                string identity = Text(item, "id", error), source = Text(item, "source", error);
                string target = Text(item, "target", error);
                var direction = new ModelDirection(identity, source, target,
                    Text(item, "sourceName", error), Text(item, "targetName", error), version, address,
                    Text(item, "selfCheckText", error), pin);
                if (!LanguageCode(source) || !LanguageCode(target) || source == target
                    || identity != source + "_" + target || !directionIds.Add(identity)
                    || item.TryGetProperty("version", out _) || item.TryGetProperty("url", out _)
                    || item.TryGetProperty("archivePin", out _))
                    throw new InvalidDataException(error);
                directions.Add(direction);
            }
            if (pin is not null && directions.Count != 1)
                throw new InvalidDataException(error);
            packages.Add(new ModelPackage(id, version, backend, preview.GetBoolean(), address, directions.ToArray(), pin));
        }
        return new ModelPackageCatalog(packages.ToArray());
    }

    private static void RequireUniqueObjectKeys(JsonElement value, string error)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(error);
                RequireUniqueObjectKeys(property.Value, error);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                RequireUniqueObjectKeys(item, error);
    }

    private static ModelArchivePin? ArchivePin(JsonElement package, string backend, string error)
    {
        if (!package.TryGetProperty("archivePin", out var pin))
            return null;
        if (backend != "argos" || pin.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(error);
        string[] keys = pin.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        if (!keys.SequenceEqual(new[] { "bytes", "sha256", "unpackedBytes" }, StringComparer.Ordinal)
            || pin.GetProperty("sha256").ValueKind != JsonValueKind.String
            || !Regex.IsMatch(pin.GetProperty("sha256").GetString()!, @"\A[0-9a-f]{64}\z")
            || pin.GetProperty("bytes").ValueKind != JsonValueKind.Number
            || !pin.GetProperty("bytes").TryGetInt64(out long bytes) || bytes is <= 0 or > 1073741824L
            || pin.GetProperty("unpackedBytes").ValueKind != JsonValueKind.Number
            || !pin.GetProperty("unpackedBytes").TryGetInt64(out long unpacked) || unpacked is <= 0 or > 2147483648L)
            throw new InvalidDataException(error);
        return new(pin.GetProperty("sha256").GetString()!, bytes, unpacked);
    }

    public IReadOnlyList<ModelPackageSelection> ReadInstallationSelection(JsonElement inventory)
    {
        const string error = "model-inventory";
        int schema = Schema(inventory, error);
        if (schema is 1 or 2)
        {
            if (inventory.TryGetProperty("packages", out _) || inventory.TryGetProperty("activePackages", out _))
                throw new InvalidDataException(error);
            var legacy = LegacyDirections;
            string[] ids;
            if (!inventory.TryGetProperty("directions", out var value))
            {
                if (schema != 1)
                    throw new InvalidDataException(error);
                ids = legacy.Select(direction => direction.Id).ToArray();
            }
            else
            {
                ids = DirectionIds(value, error);
                if (ids.Any(id => !legacy.Any(direction => direction.Id == id))
                    || schema == 1 && ids.Length != legacy.Count)
                    throw new InvalidDataException(error);
            }
            return Array.AsReadOnly(LegacyPackages.Where(identity => ids.Contains(identity.Direction, StringComparer.Ordinal))
                .Select(identity => new ModelPackageSelection(Packages.First(package => package.Id == identity.Package),
                    [legacy.First(direction => direction.Id == identity.Direction)])).ToArray());
        }
        if (schema != 3 || inventory.TryGetProperty("directions", out _) || inventory.TryGetProperty("files", out _))
            throw new InvalidDataException(error);
        Dictionary<string, (string[] Directions, ModelPackageFile[] Files)> selected = new(StringComparer.Ordinal);
        foreach (var item in NonemptyArray(inventory, "packages", error).EnumerateArray())
        {
            string id = Text(item, "id", error), version = Text(item, "version", error);
            var package = Packages.FirstOrDefault(package => package.Id == id);
            string[] ids = DirectionIds(NonemptyArray(item, "directions", error), error);
            if (package is null || package.Version != version || !selected.TryAdd(id, (ids, FileManifest(item, error)))
                || ids.Any(direction => !package.Directions.Any(candidate => candidate.Id == direction)))
                throw new InvalidDataException(error);
        }
        return Array.AsReadOnly(Packages.Where(package => selected.ContainsKey(package.Id))
            .Select(package => new ModelPackageSelection(package,
                package.Directions.Where(direction => selected[package.Id].Directions.Contains(direction.Id, StringComparer.Ordinal)).ToArray(), selected[package.Id].Files)).ToArray());
    }

    public IReadOnlyList<ModelPackageSelection> ReadActiveSelection(JsonElement inventory)
    {
        const string error = "model-inventory";
        var installed = ReadInstallationSelection(inventory);
        if (Schema(inventory, error) is 1 or 2)
            return installed;
        string[] ids = DirectionIds(NonemptyArray(inventory, "activePackages", error), error);
        if (ids.Any(id => !installed.Any(item => item.Package.Id == id)))
            throw new InvalidDataException(error);
        var active = installed.Where(item => ids.Contains(item.Package.Id, StringComparer.Ordinal)).ToArray();
        bool argos = active.All(item => item.Package is { Backend: "argos", Version: "1.9" }
            && LegacyPackages.Any(identity => identity.Package == item.Package.Id));
        bool hy = active.Length == 1 && active[0].Package.Backend == "hy-mt-llama-cpu";
        string[] directions = active.SelectMany(item => item.Directions).Select(direction => direction.Id).ToArray();
        if (!(argos || hy) || directions.Distinct(StringComparer.Ordinal).Count() != directions.Length)
            throw new InvalidDataException(error);
        return Array.AsReadOnly(active);
    }

    private static ModelPackageFile[] FileManifest(JsonElement package, string error)
    {
        var files = NonemptyArray(package, "files", error);
        if (files.GetArrayLength() > 20000)
            throw new InvalidDataException(error);
        List<ModelPackageFile> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.EnumerateArray())
        {
            string path = Text(file, "path", error), hash = Text(file, "sha256", error);
            if (path.Split('/').Any(part => part.Length == 0 || part is "." or ".."
                || part.EndsWith(' ') || part.EndsWith('.')
                || part.Any(character => character < 32 || "\\:\0<>\"|?*".Contains(character))
                || Regex.IsMatch(part.Split('.')[0], @"\A(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z"))
                || !seen.Add(path) || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException(error);
            result.Add(new ModelPackageFile(path, hash.ToLowerInvariant()));
        }
        return result.ToArray();
    }

    private static string[] DirectionIds(JsonElement value, string error)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidDataException(error);
        string[] ids = value.EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidDataException(error);
        return ids;
    }

    private static int Schema(JsonElement value, string error)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version))
            throw new InvalidDataException(error);
        return version;
    }

    private static string Text(JsonElement value, string key, string error)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var text)
            || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))
            throw new InvalidDataException(error);
        return text.GetString()!;
    }

    private static JsonElement NonemptyArray(JsonElement value, string key, string error)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var array)
            || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
            throw new InvalidDataException(error);
        return array;
    }

    private static bool LanguageCode(string code) => code is { Length: 2 }
        && code.All(character => character is >= 'a' and <= 'z');
}
