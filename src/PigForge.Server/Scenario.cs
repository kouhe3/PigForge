using System.Text.Json;

namespace PigForge.Server;

/// <summary>One part a scenario places before the first frame (world coordinates, owner 0).</summary>
internal sealed record ScenarioPart(uint PartTypeId, float X, float Y, float Angle, float Scale, bool Mirrored);

/// <summary>
/// A development scenario (`docs/specs/dev-harness.md`): a level plus the parts it starts with, and
/// whether the room starts running immediately. Scenarios are content, not level documents: they
/// exist so one URL can verify one feature without building a contraption by hand.
/// </summary>
internal sealed record ScenarioDocument(
    string Id,
    string Name,
    string Description,
    string Level,
    IReadOnlyList<ScenarioPart> Build,
    bool Start);

/// <summary>
/// Strict reader for `content/scenarios/*.json`. Unknown properties, non-finite numbers and a
/// missing required key are hard errors: a scenario that silently drops a part is worse than one
/// that refuses to load.
/// </summary>
internal static class ScenarioLoader
{
    public const string Format = "pigforge.scenario";
    public const int SchemaVersion = 1;
    private const float MaxScale = 4f;

    private static readonly HashSet<string> RootKeys =
    [
        "format", "schemaVersion", "id", "name", "description", "level", "build", "start",
    ];

    private static readonly HashSet<string> PartKeys =
    [
        "partTypeId", "x", "y", "angle", "scale", "mirrored",
    ];

    public static ScenarioDocument Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Error(path, "root: must be a JSON object");
        }

        RejectUnknown(root, RootKeys, path, "root");

        string format = RequiredString(root, "format", path);
        if (format != Format)
        {
            throw Error(path, $"format: expected '{Format}', got '{format}'");
        }

        int version = RequiredInt(root, "schemaVersion", path);
        if (version != SchemaVersion)
        {
            throw Error(path, $"schemaVersion: only {SchemaVersion} is supported");
        }

        string id = RequiredString(root, "id", path);
        if (!IsIdentifier(id))
        {
            throw Error(path, "id: must be 1-64 lowercase letters, digits or hyphens");
        }

        string name = RequiredString(root, "name", path);
        string description = OptionalString(root, "description", path) ?? string.Empty;
        string level = RequiredString(root, "level", path);
        bool start = OptionalBool(root, "start", path, "root") ?? false;

        if (!root.TryGetProperty("build", out JsonElement buildElement) || buildElement.ValueKind != JsonValueKind.Array)
        {
            throw Error(path, "build: must be an array");
        }

        List<ScenarioPart> build = new();
        int index = 0;
        foreach (JsonElement item in buildElement.EnumerateArray())
        {
            string where = $"build[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Error(path, $"{where}: must be an object");
            }

            RejectUnknown(item, PartKeys, path, where);
            uint partTypeId = RequiredUInt(item, "partTypeId", path, where);
            float x = RequiredFloat(item, "x", path, where);
            float y = RequiredFloat(item, "y", path, where);
            float angle = OptionalFloat(item, "angle", path, where) ?? 0f;
            float scale = OptionalFloat(item, "scale", path, where) ?? 1f;
            bool mirrored = OptionalBool(item, "mirrored", path, where) ?? false;
            if (scale is <= 0f or > MaxScale)
            {
                throw Error(path, $"{where}.scale: must be in (0, {MaxScale}]");
            }

            build.Add(new ScenarioPart(partTypeId, x, y, angle, scale, mirrored));
            index++;
        }

        return new ScenarioDocument(id, name, description, level, build, start);
    }

    /// <summary>Every scenario under <c>content/scenarios</c>, ordered by id.</summary>
    public static IReadOnlyList<ScenarioDocument> List(string repositoryRoot) =>
        ListWithErrors(repositoryRoot).Scenarios;

    /// <summary>
    /// The success list and the per-file errors: one broken scenario must not hide the others from
    /// the dev drawer, but it must also never be silently dropped (`/dev/worlds` reports it).
    /// </summary>
    public static (IReadOnlyList<ScenarioDocument> Scenarios, IReadOnlyList<string> Errors) ListWithErrors(string repositoryRoot)
    {
        string directory = Path.Combine(repositoryRoot, "content", "scenarios");
        List<ScenarioDocument> scenarios = new();
        List<string> errors = new();
        if (!Directory.Exists(directory))
        {
            return (scenarios, errors);
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                scenarios.Add(Load(file));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
            {
                errors.Add(exception.Message);
            }
        }

        scenarios.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        errors.Sort(StringComparer.Ordinal);
        return (scenarios, errors);
    }

    private static void RejectUnknown(JsonElement element, HashSet<string> allowed, string path, string where)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Error(path, $"{where}: unknown property '{property.Name}'");
            }
        }
    }

    private static string RequiredString(JsonElement root, string key, string path)
    {
        if (!root.TryGetProperty(key, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            throw Error(path, $"{key}: must be a string");
        }

        string value = element.GetString() ?? string.Empty;
        if (value.Length == 0)
        {
            throw Error(path, $"{key}: must not be empty");
        }

        return value;
    }

    private static string? OptionalString(JsonElement root, string key, string path)
    {
        if (!root.TryGetProperty(key, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw Error(path, $"{key}: must be a string");
        }

        return element.GetString();
    }

    private static int RequiredInt(JsonElement root, string key, string path)
    {
        if (!root.TryGetProperty(key, out JsonElement element) || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int value))
        {
            throw Error(path, $"{key}: must be an integer");
        }

        return value;
    }

    private static uint RequiredUInt(JsonElement root, string key, string path, string where)
    {
        if (!root.TryGetProperty(key, out JsonElement element) || element.ValueKind != JsonValueKind.Number
            || !element.TryGetUInt32(out uint value) || value == 0)
        {
            throw Error(path, $"{where}.{key}: must be a positive integer");
        }

        return value;
    }

    private static float RequiredFloat(JsonElement root, string key, string path, string where)
    {
        float? value = OptionalFloat(root, key, path, where);
        if (value is null)
        {
            throw Error(path, $"{where}.{key}: must be a finite number");
        }

        return value.Value;
    }

    private static float? OptionalFloat(JsonElement root, string key, string path, string where)
    {
        if (!root.TryGetProperty(key, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double value) || !double.IsFinite(value))
        {
            throw Error(path, $"{where}.{key}: must be a finite number");
        }

        return (float)value;
    }

    private static bool? OptionalBool(JsonElement root, string key, string path, string where)
    {
        if (!root.TryGetProperty(key, out JsonElement element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Error(path, $"{where}.{key}: must be true or false"),
        };
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length is 0 or > 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static InvalidDataException Error(string path, string message) =>
        new($"{Path.GetFileName(path)}: {message}");
}
