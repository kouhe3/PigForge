using System.Text.Json;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Content;

public sealed class LevelContentException : Exception
{
    public LevelContentException(IReadOnlyList<string> errors)
        : base($"Level content was rejected with {errors.Count} error(s):{Environment.NewLine}{string.Join(Environment.NewLine, errors.Select(error => $"  - {error}"))}")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Parses and validates level-content JSON. Same startup-gate principles as
/// <see cref="PartContentParser"/>: unknown properties, duplicate keys, non-finite
/// values, and inverted zones are rejected before the level can be used.
/// </summary>
public static class LevelContentParser
{
    private static readonly string[] RequiredRootProperties = { "format", "schemaVersion", "contentVersion", "goalZone", "bounds", "spawns" };

    /// <summary>v2 additions; a v1 document simply omits them.</summary>
    private static readonly string[] OptionalRootProperties = { "terrain" };

    public static LevelContentDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);

        List<string> errors = new();
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new LevelContentException(new[] { "root: level must be a JSON object." });
        }

        HashSet<string> seen = new();
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"root: duplicate property '{property.Name}'.");
            }
        }

        RequireExactly(seen, RequiredRootProperties, "root", errors);
        foreach (string property in seen)
        {
            if (!RequiredRootProperties.Contains(property) && !OptionalRootProperties.Contains(property))
            {
                errors.Add($"root: unknown property '{property}'.");
            }
        }

        if (seen.Contains("format") && (!root.TryGetProperty("format", out JsonElement format) || format.GetString() != LevelContentDocument.Format))
        {
            errors.Add($"root.format: must be '{LevelContentDocument.Format}'.");
        }

        if (seen.Contains("schemaVersion")
            && (!root.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
                || !schemaVersion.TryGetInt32(out int version)
                || version is < LevelContentDocument.LegacySchemaVersion or > LevelContentDocument.SchemaVersion))
        {
            errors.Add(
                $"root.schemaVersion: versions {LevelContentDocument.LegacySchemaVersion} to {LevelContentDocument.SchemaVersion} are supported.");
        }

        string? contentVersion = null;
        if (seen.Contains("contentVersion") && root.TryGetProperty("contentVersion", out JsonElement contentVersionElement))
        {
            contentVersion = ReadVersion(contentVersionElement, "root.contentVersion", errors);
        }

        GameplayZone? goalZone = null;
        if (seen.Contains("goalZone") && root.TryGetProperty("goalZone", out JsonElement goalZoneElement))
        {
            goalZone = ReadZone(goalZoneElement, "root.goalZone", errors);
        }

        GameplayZone? mapBounds = null;
        if (seen.Contains("bounds") && root.TryGetProperty("bounds", out JsonElement boundsElement))
        {
            mapBounds = ReadZone(boundsElement, "root.bounds", errors);
        }

        List<LevelSpawnDefinition> spawns = new();
        if (seen.Contains("spawns") && root.TryGetProperty("spawns", out JsonElement spawnsElement))
        {
            if (spawnsElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add("root.spawns: must be an array.");
            }
            else
            {
                int index = 0;
                foreach (JsonElement spawnElement in spawnsElement.EnumerateArray())
                {
                    ParseSpawn(spawnElement, $"root.spawns[{index}]", spawns, errors);
                    index++;
                }
            }
        }

        List<LevelTerrainDefinition> terrain = new();
        if (seen.Contains("terrain") && root.TryGetProperty("terrain", out JsonElement terrainElement))
        {
            if (terrainElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add("root.terrain: must be an array.");
            }
            else
            {
                int index = 0;
                foreach (JsonElement terrainObject in terrainElement.EnumerateArray())
                {
                    ParseTerrain(terrainObject, $"root.terrain[{index}]", terrain, errors);
                    index++;
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new LevelContentException(errors);
        }

        return new LevelContentDocument(contentVersion!, goalZone!.Value, mapBounds!.Value, spawns)
        {
            Terrain = terrain,
        };
    }

    /// <summary>
    /// One terrain entry: `{ "position": [x,y,z], "depth": 10, "loops": [[[x,y], ...], ...] }`. The
    /// loops are the terrain's boundary polygons in the terrain's own local frame and the points are
    /// 2D (the extrusion supplies z), so each point is exactly two numbers.
    /// </summary>
    private static void ParseTerrain(JsonElement element, string path, List<LevelTerrainDefinition> terrain, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: terrain must be a JSON object.");
            return;
        }

        HashSet<string> seen = new();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"{path}: duplicate property '{property.Name}'.");
            }
        }

        string[] allowed = { "position", "depth", "loops" };
        RequireExactly(seen, new[] { "position", "depth", "loops" }, path, errors);
        foreach (string property in seen)
        {
            if (!allowed.Contains(property))
            {
                errors.Add($"{path}: unknown property '{property}'.");
            }
        }

        PhysicsVector3? position = ReadVector3(element, path, "position", errors, required: true);

        float depth = 0f;
        if (seen.Contains("depth") && element.TryGetProperty("depth", out JsonElement depthElement))
        {
            if (depthElement.ValueKind != JsonValueKind.Number || !IsFinite(depthElement) || depthElement.GetSingle() <= 0f)
            {
                errors.Add($"{path}.depth: must be a finite positive number.");
            }
            else
            {
                depth = depthElement.GetSingle();
            }
        }

        List<IReadOnlyList<PhysicsVector3>> loops = new();
        if (seen.Contains("loops") && element.TryGetProperty("loops", out JsonElement loopsElement))
        {
            if (loopsElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{path}.loops: must be an array of polygons.");
            }
            else
            {
                int loopIndex = 0;
                foreach (JsonElement loopElement in loopsElement.EnumerateArray())
                {
                    IReadOnlyList<PhysicsVector3>? loop = ReadLoop(loopElement, $"{path}.loops[{loopIndex}]", errors);
                    if (loop is not null)
                    {
                        loops.Add(loop);
                    }

                    loopIndex++;
                }
            }
        }

        if (loops.Count == 0)
        {
            errors.Add($"{path}.loops: a terrain needs at least one outline loop.");
        }

        if (position is not null && depth > 0f && loops.Count > 0)
        {
            terrain.Add(new LevelTerrainDefinition(position.Value, depth, loops));
        }
    }

    /// <summary>One outline loop: at least three `[x, y]` points, each finite.</summary>
    private static IReadOnlyList<PhysicsVector3>? ReadLoop(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}: an outline loop must be an array of points.");
            return null;
        }

        List<PhysicsVector3> points = new();
        int index = 0;
        foreach (JsonElement pointElement in element.EnumerateArray())
        {
            string pointPath = $"{path}[{index}]";
            index++;
            if (pointElement.ValueKind != JsonValueKind.Array
                || pointElement.GetArrayLength() != 2
                || !IsFinite(pointElement[0])
                || !IsFinite(pointElement[1]))
            {
                errors.Add($"{pointPath}: an outline point must be [x, y] with finite numbers.");
                continue;
            }

            points.Add(new PhysicsVector3(pointElement[0].GetSingle(), pointElement[1].GetSingle(), 0f));
        }

        if (points.Count < 3)
        {
            errors.Add($"{path}: an outline loop needs at least three points.");
            return null;
        }

        return points;
    }

    private static void ParseSpawn(JsonElement element, string path, List<LevelSpawnDefinition> spawns, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: spawn must be a JSON object.");
            return;
        }

        HashSet<string> seen = new();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"{path}: duplicate property '{property.Name}'.");
            }
        }

        string[] allowed = { "partTypeId", "position", "role", "tntFuseTicks", "motorImpulsePerTick", "motorDirectionX", "wheel", "angle" };
        RequireExactly(seen, new[] { "partTypeId", "position" }, path, errors);
        foreach (string property in seen)
        {
            if (!allowed.Contains(property))
            {
                errors.Add($"{path}: unknown property '{property}'.");
            }
        }

        uint partTypeId = 0;
        if (seen.Contains("partTypeId") && element.TryGetProperty("partTypeId", out JsonElement idElement))
        {
            if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetUInt32(out partTypeId) || partTypeId == 0)
            {
                errors.Add($"{path}.partTypeId: must be a positive 32-bit integer.");
                partTypeId = 0;
            }
        }

        PhysicsVector3? position = ReadVector3(element, path, "position", errors, required: true);

        LevelActorRole role = LevelActorRole.Part;
        if (seen.Contains("role") && element.TryGetProperty("role", out JsonElement roleElement))
        {
            if (roleElement.ValueKind != JsonValueKind.String || !Enum.TryParse(roleElement.GetString(), ignoreCase: true, out role))
            {
                errors.Add($"{path}.role: must be 'part', 'pig' or 'tnt'.");
                role = LevelActorRole.Part;
            }
        }

        ushort fuse = 1;
        if (seen.Contains("tntFuseTicks") && element.TryGetProperty("tntFuseTicks", out JsonElement fuseElement))
        {
            if (fuseElement.ValueKind != JsonValueKind.Number || !fuseElement.TryGetUInt32(out uint fuseValue) || fuseValue > ushort.MaxValue)
            {
                errors.Add($"{path}.tntFuseTicks: must be a 16-bit non-negative integer.");
            }
            else
            {
                fuse = (ushort)fuseValue;
            }
        }

        float motorImpulse = 0f;
        if (seen.Contains("motorImpulsePerTick") && element.TryGetProperty("motorImpulsePerTick", out JsonElement impulseElement))
        {
            if (impulseElement.ValueKind != JsonValueKind.Number || !IsFinite(impulseElement))
            {
                errors.Add($"{path}.motorImpulsePerTick: must be a finite number.");
            }
            else
            {
                motorImpulse = impulseElement.GetSingle();
            }
        }

        float motorDirection = 0f;
        if (seen.Contains("motorDirectionX") && element.TryGetProperty("motorDirectionX", out JsonElement directionElement))
        {
            if (directionElement.ValueKind != JsonValueKind.Number || !IsFinite(directionElement)
                || directionElement.GetSingle() is not (-1f or 0f or 1f))
            {
                errors.Add($"{path}.motorDirectionX: must be -1, 0 or 1.");
            }
            else
            {
                motorDirection = directionElement.GetSingle();
            }
        }

        bool wheel = false;
        if (seen.Contains("wheel") && element.TryGetProperty("wheel", out JsonElement wheelElement))
        {
            if (wheelElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors.Add($"{path}.wheel: must be a boolean.");
            }
            else
            {
                wheel = wheelElement.GetBoolean();
            }
        }

        float angle = 0f;
        if (seen.Contains("angle") && element.TryGetProperty("angle", out JsonElement angleElement))
        {
            if (angleElement.ValueKind != JsonValueKind.Number || !IsFinite(angleElement))
            {
                errors.Add($"{path}.angle: must be a finite number.");
            }
            else
            {
                angle = angleElement.GetSingle();
            }
        }

        spawns.Add(new LevelSpawnDefinition(
            partTypeId,
            position ?? default,
            role,
            role == LevelActorRole.Tnt ? fuse : (ushort)1,
            motorImpulse,
            motorDirection,
            wheel,
            angle));
    }

    private static GameplayZone ReadZone(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: zone must be a JSON object.");
            return default;
        }

        PhysicsVector3? min = null;
        PhysicsVector3? max = null;
        if (element.TryGetProperty("min", out JsonElement minElement))
        {
            min = ReadVector3(element, path, "min", errors, required: true);
        }
        else
        {
            errors.Add($"{path}: missing required property 'min'.");
        }

        if (element.TryGetProperty("max", out JsonElement maxElement))
        {
            max = ReadVector3(element, path, "max", errors, required: true);
        }
        else
        {
            errors.Add($"{path}: missing required property 'max'.");
        }

        if (min is { } minVector && max is { } maxVector
            && (maxVector.X < minVector.X || maxVector.Y < minVector.Y || maxVector.Z < minVector.Z))
        {
            errors.Add($"{path}: zone max must be greater than or equal to min on every axis.");
        }

        return new GameplayZone(min ?? default, max ?? default);
    }

    private static PhysicsVector3? ReadVector3(JsonElement parent, string path, string field, List<string> errors, bool required)
    {
        if (!parent.TryGetProperty(field, out JsonElement value))
        {
            if (required)
            {
                errors.Add($"{path}.{field}: must be an array of three finite numbers.");
            }

            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3
            || value.EnumerateArray().Any(component => component.ValueKind != JsonValueKind.Number || !IsFinite(component)))
        {
            errors.Add($"{path}.{field}: must be an array of three finite numbers.");
            return null;
        }

        float[] components = value.EnumerateArray().Select(component => component.GetSingle()).ToArray();
        return new PhysicsVector3(components[0], components[1], components[2]);
    }

    private static string? ReadVersion(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}: must be a string.");
            return null;
        }

        string value = element.GetString()!;
        if (value.Length is 0 or > 128 || value.Trim().Length != value.Length)
        {
            errors.Add($"{path}: must contain 1 to 128 non-whitespace-padded characters.");
            return null;
        }

        return value;
    }

    private static void RequireExactly(HashSet<string> seen, string[] required, string path, List<string> errors)
    {
        foreach (string name in required)
        {
            if (!seen.Contains(name))
            {
                errors.Add($"{path}: missing required property '{name}'.");
            }
        }
    }

    private static bool IsFinite(JsonElement value) =>
        !float.IsNaN(value.GetSingle()) && !float.IsInfinity(value.GetSingle());
}

/// <summary>Startup gate over a parsed level document.</summary>
public sealed class LevelContentLibrary
{
    public LevelContentLibrary(LevelContentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
    }

    public static LevelContentLibrary Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new LevelContentLibrary(Parse(File.ReadAllText(path)));
    }

    public static LevelContentDocument Parse(string json) => LevelContentParser.Parse(json);

    public LevelContentDocument Document { get; }
}
