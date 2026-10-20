using System.Text;
using System.Text.Json;

namespace PigForge.Server;

/// <summary>
/// One world a dev connection asked for (`docs/specs/dev-harness.md`): a scenario, a level under
/// <c>content/levels</c>, or the default sandbox. The level file is normalized to a
/// <c>content/levels</c> relative path; the JSON is the exact document the room parsed.
/// </summary>
internal sealed record DevWorld(string? ScenarioId, string LevelFile, string LevelJson, ScenarioDocument? Scenario, bool Sandbox);

/// <summary>Resolves the <c>?scenario=</c> / <c>?level=</c> query and lists the available worlds.</summary>
internal static class DevWorlds
{
    public const string DefaultLevel = "terrain-v1.json";

    /// <summary>
    /// Resolves one query. Unknown keys, both `scenario` and `level`, a malformed name and a path
    /// that escapes <c>content/levels</c> are all rejected, never normalized into a different world.
    /// </summary>
    public static DevWorld Resolve(string repositoryRoot, string? scenarioId, string? levelFile)
    {
        if (!string.IsNullOrEmpty(scenarioId) && !string.IsNullOrEmpty(levelFile))
        {
            throw new InvalidDataException("A world query carries either scenario or level, not both.");
        }

        if (!string.IsNullOrEmpty(scenarioId))
        {
            string path = SafePath(Path.Combine(repositoryRoot, "content", "scenarios"), $"{scenarioId}.json");
            ScenarioDocument scenario = ScenarioLoader.Load(path);
            string levelPath = SafePath(Path.Combine(repositoryRoot, "content", "levels"), scenario.Level);
            return new DevWorld(
                scenario.Id,
                Normalize(scenario.Level),
                File.ReadAllText(levelPath),
                scenario,
                Sandbox: false);
        }

        if (!string.IsNullOrEmpty(levelFile))
        {
            string levelPath = SafePath(Path.Combine(repositoryRoot, "content", "levels"), levelFile);
            return new DevWorld(null, Normalize(levelFile), File.ReadAllText(levelPath), null, Sandbox: false);
        }

        string defaultPath = SafePath(Path.Combine(repositoryRoot, "content", "levels"), DefaultLevel);
        return new DevWorld(null, DefaultLevel, File.ReadAllText(defaultPath), null, Sandbox: true);
    }

    /// <summary>The dev drawer's list: scenarios (with their own errors) and every level document.</summary>
    public static byte[] ListJson(string repositoryRoot)
    {
        (IReadOnlyList<ScenarioDocument> scenarios, IReadOnlyList<string> errors) = ScenarioLoader.ListWithErrors(repositoryRoot);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("scenarios");
            foreach (ScenarioDocument scenario in scenarios)
            {
                writer.WriteStartObject();
                writer.WriteString("id", scenario.Id);
                writer.WriteString("name", scenario.Name);
                writer.WriteString("description", scenario.Description);
                writer.WriteString("level", scenario.Level);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("levels");
            foreach ((string path, string area, string name) in EnumerateLevels(repositoryRoot))
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteString("area", area);
                writer.WriteString("name", name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("errors");
            foreach (string error in errors)
            {
                writer.WriteStringValue(error);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Every level document under <c>content/levels</c>, as `(relative path, area, name)`.</summary>
    public static IReadOnlyList<(string Path, string Area, string Name)> EnumerateLevels(string repositoryRoot)
    {
        string directory = Path.Combine(repositoryRoot, "content", "levels");
        List<(string Path, string Area, string Name)> levels = new();
        if (!Directory.Exists(directory))
        {
            return levels;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            int slash = relative.IndexOf('/');
            string area = slash < 0 ? string.Empty : relative[..slash];
            levels.Add((relative, area, Path.GetFileNameWithoutExtension(file)));
        }

        levels.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return levels;
    }

    /// <summary>
    /// A path under `root` that cannot escape it: the resolved full path must stay inside, and the
    /// input must be relative (an absolute path or `..` segment is rejected before touching disk).
    /// </summary>
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"'{relative}' is not a relative path.");
        }

        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            throw new InvalidDataException($"'{relative}' is not a file under {Path.GetFileName(Path.TrimEndingDirectorySeparator(root))}.");
        }

        return full;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
