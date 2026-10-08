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

    /// <summary>Version-gated additions; an older document simply omits them.</summary>
    private static readonly string[] OptionalRootProperties = { "terrain", "cameraLimits" };

    /// <summary>The two halves of a v5 `cameraLimits` block, and nothing else.</summary>
    private static readonly string[] CameraLimitProperties = { "topLeft", "size" };

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

        // The terrain walk below is version-gated (v3 requires a `fill` and a `collider` on every
        // terrain entry; v1/v2 forbid both), so the claimed version is kept. A document that names an
        // unsupported version is already an error, so the walk continues as the current version.
        int schemaVersion = LevelContentDocument.SchemaVersion;
        if (seen.Contains("schemaVersion")
            && root.TryGetProperty("schemaVersion", out JsonElement schemaVersionElement)
            && schemaVersionElement.TryGetInt32(out int claimed)
            && claimed is >= LevelContentDocument.LegacySchemaVersion and <= LevelContentDocument.SchemaVersion)
        {
            schemaVersion = claimed;
        }

        if (seen.Contains("schemaVersion")
            && (!root.TryGetProperty("schemaVersion", out JsonElement schemaVersionProperty)
                || !schemaVersionProperty.TryGetInt32(out int version)
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

        // v5: the level's own camera rectangle out of the level file's `PrefabOverrides` (that is the
        // pig's bound, `Pig.cs:396-403`). Only a v5 document carries one, and every v5 document must.
        CameraLimits? cameraLimits = null;
        if (seen.Contains("cameraLimits") && root.TryGetProperty("cameraLimits", out JsonElement cameraLimitsElement))
        {
            if (schemaVersion >= 5)
            {
                cameraLimits = ReadCameraLimits(cameraLimitsElement, "root.cameraLimits", errors);
            }
            else
            {
                errors.Add("root.cameraLimits: only a schemaVersion 5 document carries the level's camera limits.");
            }
        }
        else if (schemaVersion >= 5)
        {
            errors.Add("root.cameraLimits: a schemaVersion 5 document must carry the level's camera limits.");
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
                    ParseTerrain(terrainObject, $"root.terrain[{index}]", schemaVersion, terrain, errors);
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
            CameraLimits = cameraLimits,
        };
    }

    /// <summary>
    /// One terrain entry. A v1/v2 document has `{ "position": [x,y,z], "depth": 10,
    /// "loops": [[[x,y], ...], ...] }` and is treated as colliding with no fill; a v3 document adds
    /// `"collider": true|false` and a required `fill` block. The loops are the terrain's boundary
    /// polygons in the terrain's own local frame and the points are 2D (the extrusion supplies z), so
    /// each point is exactly two numbers.
    /// </summary>
    private static void ParseTerrain(
        JsonElement element,
        string path,
        int schemaVersion,
        List<LevelTerrainDefinition> terrain,
        List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: terrain must be a JSON object.");
            return;
        }

        // v3 is the version that describes every `e2dTerrain` the original ships: a collider bit and
        // the ground's fill; v4 adds the edge trim. Older documents carry neither, and a stray field
        // is a drift error rather than a field to ignore.
        bool modern = schemaVersion >= 3;
        bool trimmed = schemaVersion >= 4;
        HashSet<string> seen = new();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"{path}: duplicate property '{property.Name}'.");
            }
        }

        string[] required = trimmed
            ? ["position", "depth", "collider", "fill", "curve", "loops"]
            : modern
                ? ["position", "depth", "collider", "fill", "loops"]
                : ["position", "depth", "loops"];
        RequireExactly(seen, required, path, errors);
        foreach (string property in seen)
        {
            if (required.Contains(property))
            {
                continue;
            }

            if (property is "collider" or "fill")
            {
                errors.Add($"{path}.{property}: only a schemaVersion 3 terrain carries a collider bit or a fill.");
            }
            else if (property == "curve")
            {
                errors.Add($"{path}.curve: only a schemaVersion 4 terrain carries the edge trim.");
            }
            else
            {
                errors.Add($"{path}: unknown property '{property}'.");
            }
        }

        PhysicsVector3? position = ReadVector3(element, path, "position", errors, required: true);

        bool collider = true;
        if (seen.Contains("collider") && element.TryGetProperty("collider", out JsonElement colliderElement))
        {
            if (colliderElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors.Add($"{path}.collider: must be true or false.");
            }
            else
            {
                collider = colliderElement.GetBoolean();
            }
        }

        LevelTerrainFillDefinition? fill = null;
        if (seen.Contains("fill") && element.TryGetProperty("fill", out JsonElement fillElement))
        {
            fill = ParseFill(fillElement, $"{path}.fill", errors);
        }

        LevelCurveDefinition? curve = null;
        if (seen.Contains("curve") && element.TryGetProperty("curve", out JsonElement curveElement))
        {
            curve = ParseCurve(curveElement, $"{path}.curve", errors);
        }

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

        if (position is not null
            && depth > 0f
            && loops.Count > 0
            && (!modern || fill is not null)
            && (!trimmed || curve is not null))
        {
            terrain.Add(new LevelTerrainDefinition(position.Value, depth, loops, collider, fill, curve));
        }
    }

    /// <summary>
    /// A v4 `curve` block: `{ "nodes": [[x, y], ...], "stripe": [[x, y], ...],
    /// "textures": [{ "texture": "...", "wrap": "repeat" }, { ... }], "uScale": 10,
    /// "splat1": [[0, 12], ...] }` -- the original's `_curve` mesh (two rows of points, one per row
    /// vertex) and the `e2d/Curve` material inputs `LevelLoader.ReadTerrain` restores. The two rows
    /// must be the same length (an even vertex count), `splat1` must be a sorted, disjoint set of
    /// in-range runs, and the two layer textures are exactly `_Splat0` and `_Splat1`.
    /// </summary>
    private static LevelCurveDefinition? ParseCurve(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: must be a JSON object.");
            return null;
        }

        HashSet<string> seen = new();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"{path}: duplicate property '{property.Name}'.");
            }
        }

        RequireExactly(seen, ["nodes", "stripe", "textures", "uScale", "splat1"], path, errors);
        foreach (string property in seen)
        {
            if (property is not ("nodes" or "stripe" or "textures" or "uScale" or "splat1"))
            {
                errors.Add($"{path}: unknown property '{property}'.");
            }
        }

        IReadOnlyList<PhysicsVector3>? nodes = ReadPoints(element, seen, "nodes", path, errors, minimum: 2);
        IReadOnlyList<PhysicsVector3>? stripe = ReadPoints(element, seen, "stripe", path, errors, minimum: 2);
        if (nodes is not null && stripe is not null && nodes.Count != stripe.Count)
        {
            errors.Add($"{path}.stripe: {stripe.Count} points against {nodes.Count} in nodes -- the strip's two rows are one vertex per node.");
        }

        List<LevelCurveTextureDefinition> textures = new();
        if (seen.Contains("textures") && element.TryGetProperty("textures", out JsonElement texturesElement))
        {
            if (texturesElement.ValueKind != JsonValueKind.Array || texturesElement.GetArrayLength() != 2)
            {
                errors.Add($"{path}.textures: exactly two layers are supported (the shader samples _Splat0 and _Splat1).");
            }
            else
            {
                int textureIndex = 0;
                foreach (JsonElement textureElement in texturesElement.EnumerateArray())
                {
                    LevelCurveTextureDefinition? texture = ParseCurveTexture(textureElement, $"{path}.textures[{textureIndex}]", errors);
                    if (texture is not null)
                    {
                        textures.Add(texture);
                    }

                    textureIndex++;
                }
            }
        }

        float uScale = 0f;
        if (seen.Contains("uScale") && element.TryGetProperty("uScale", out JsonElement uScaleElement))
        {
            if (uScaleElement.ValueKind != JsonValueKind.Number || !IsFinite(uScaleElement) || uScaleElement.GetSingle() <= 0f)
            {
                errors.Add($"{path}.uScale: must be a finite positive number.");
            }
            else
            {
                uScale = uScaleElement.GetSingle();
            }
        }

        List<LevelCurveRun> splat1 = new();
        if (seen.Contains("splat1") && element.TryGetProperty("splat1", out JsonElement splatElement))
        {
            if (splatElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{path}.splat1: must be an array of [start, count] runs.");
            }
            else
            {
                int runIndex = 0;
                int previousEnd = -1;
                foreach (JsonElement runElement in splatElement.EnumerateArray())
                {
                    string runPath = $"{path}.splat1[{runIndex}]";
                    if (runElement.ValueKind != JsonValueKind.Array
                        || runElement.GetArrayLength() != 2
                        || !runElement[0].TryGetInt32(out int start)
                        || !runElement[1].TryGetInt32(out int count)
                        || start < 0
                        || count < 1)
                    {
                        errors.Add($"{runPath}: must be [start, count] with start >= 0 and count >= 1.");
                        runIndex++;
                        continue;
                    }

                    if (start < previousEnd)
                    {
                        errors.Add($"{runPath}: runs must be sorted by start and must not overlap.");
                    }

                    if (nodes is not null && start + count > nodes.Count)
                    {
                        errors.Add($"{runPath}: nodes {start}..{start + count - 1} are outside the {nodes.Count} node(s) the curve has.");
                    }

                    previousEnd = start + count;
                    splat1.Add(new LevelCurveRun(start, count));
                    runIndex++;
                }
            }
        }

        bool complete = nodes is not null
            && stripe is not null
            && nodes.Count == stripe.Count
            && textures.Count == 2
            && uScale > 0f;
        return complete ? new LevelCurveDefinition(nodes!, stripe!, textures, uScale, splat1) : null;
    }

    /// <summary>One `{ "texture": "Border.png", "wrap": "repeat" | "clamp" }` layer of a curve.</summary>
    private static LevelCurveTextureDefinition? ParseCurveTexture(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: must be a JSON object.");
            return null;
        }

        HashSet<string> seen = new();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"{path}: duplicate property '{property.Name}'.");
            }
        }

        RequireExactly(seen, ["texture", "wrap"], path, errors);
        foreach (string property in seen)
        {
            if (property is not ("texture" or "wrap"))
            {
                errors.Add($"{path}: unknown property '{property}'.");
            }
        }

        string? texture = null;
        if (seen.Contains("texture") && element.TryGetProperty("texture", out JsonElement textureElement))
        {
            if (textureElement.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{path}.texture: must be a string.");
            }
            else
            {
                string? name = textureElement.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    errors.Add($"{path}.texture: must be a file name.");
                }
                else if (name.Length > 128 || name.Any(char.IsWhiteSpace))
                {
                    errors.Add($"{path}.texture: must be 1 to 128 non-whitespace-padded characters.");
                }
                else
                {
                    texture = name;
                }
            }
        }

        LevelCurveWrap? wrap = null;
        if (seen.Contains("wrap") && element.TryGetProperty("wrap", out JsonElement wrapElement))
        {
            wrap = wrapElement.ValueKind == JsonValueKind.String && wrapElement.ValueEquals("repeat") ? LevelCurveWrap.Repeat
                : wrapElement.ValueKind == JsonValueKind.String && wrapElement.ValueEquals("clamp") ? LevelCurveWrap.Clamp
                : null;
            if (wrap is null)
            {
                errors.Add($"{path}.wrap: must be \"repeat\" or \"clamp\".");
            }
        }

        return texture is not null && wrap is not null ? new LevelCurveTextureDefinition(texture, wrap.Value) : null;
    }

    /// <summary>
    /// One row of a v4 curve's points (`"nodes"` or `"stripe"`): 2D points in the terrain's own local
    /// frame, one per node. `minimum` is the row's own lower bound (a strip needs two nodes).
    /// </summary>
    private static IReadOnlyList<PhysicsVector3>? ReadPoints(
        JsonElement parent,
        HashSet<string> seen,
        string field,
        string path,
        List<string> errors,
        int minimum)
    {
        if (!seen.Contains(field) || !parent.TryGetProperty(field, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.{field}: must be an array of points.");
            return null;
        }

        List<PhysicsVector3> points = new();
        int index = 0;
        foreach (JsonElement pointElement in element.EnumerateArray())
        {
            string pointPath = $"{path}.{field}[{index}]";
            index++;
            if (pointElement.ValueKind != JsonValueKind.Array
                || pointElement.GetArrayLength() != 2
                || !IsFinite(pointElement[0])
                || !IsFinite(pointElement[1]))
            {
                errors.Add($"{pointPath}: must be [x, y] with finite numbers.");
                continue;
            }

            points.Add(new PhysicsVector3(pointElement[0].GetSingle(), pointElement[1].GetSingle(), 0f));
        }

        if (points.Count < minimum)
        {
            errors.Add($"{path}.{field}: {points.Count} point(s), at least {minimum} are needed.");
            return null;
        }

        return points;
    }

    /// <summary>
    /// A v3 `fill` block: `{ "texture": "Ground_Rocks_Texture.png", "color": [255, 255, 255, 255],
    /// "tileOffset": [0, 6.2], "tileSize": [5, 5] }` -- the original's `e2d/Fill` inputs that the
    /// level names (`LevelLoader.cs:214-230`, `:279-290`), with the tile size read out of the terrain
    /// prefab by the converter.
    /// </summary>
    private static LevelTerrainFillDefinition? ParseFill(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: must be a JSON object.");
            return null;
        }

        HashSet<string> seen = new();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"{path}: duplicate property '{property.Name}'.");
            }
        }

        string[] keys = ["texture", "color", "tileOffset", "tileSize"];
        RequireExactly(seen, keys, path, errors);
        foreach (string property in seen)
        {
            if (!keys.Contains(property))
            {
                errors.Add($"{path}: unknown property '{property}'.");
            }
        }

        string? texture = null;
        if (seen.Contains("texture") && element.TryGetProperty("texture", out JsonElement textureElement))
        {
            if (textureElement.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{path}.texture: must be a file name.");
            }
            else
            {
                string value = textureElement.GetString()!;
                if (value.Length is 0 or > 128 || value.Trim().Length != value.Length)
                {
                    errors.Add($"{path}.texture: must be 1 to 128 non-whitespace-padded characters.");
                }
                else
                {
                    texture = value;
                }
            }
        }

        byte[]? color = null;
        if (seen.Contains("color") && element.TryGetProperty("color", out JsonElement colorElement))
        {
            if (colorElement.ValueKind != JsonValueKind.Array || colorElement.GetArrayLength() != 4)
            {
                errors.Add($"{path}.color: must be four bytes [r, g, b, a].");
            }
            else
            {
                byte[] channels = new byte[4];
                bool valid = true;
                for (int channel = 0; channel < channels.Length; channel++)
                {
                    if (colorElement[channel].ValueKind != JsonValueKind.Number
                        || !colorElement[channel].TryGetByte(out channels[channel]))
                    {
                        errors.Add($"{path}.color[{channel}]: must be an integer 0 to 255.");
                        valid = false;
                    }
                }

                if (valid)
                {
                    color = channels;
                }
            }
        }

        PhysicsVector3? tileOffset = ReadPoint2(element, path, "tileOffset", seen, errors);
        PhysicsVector3? tileSize = ReadPoint2(element, path, "tileSize", seen, errors);
        if (tileSize is not null && (tileSize.Value.X <= 0f || tileSize.Value.Y <= 0f))
        {
            errors.Add($"{path}.tileSize: both sides must be positive.");
            tileSize = null;
        }

        if (texture is null || color is null || tileOffset is null || tileSize is null)
        {
            return null;
        }

        return new LevelTerrainFillDefinition(
            texture,
            color[0],
            color[1],
            color[2],
            color[3],
            tileOffset.Value.X,
            tileOffset.Value.Y,
            tileSize.Value.X,
            tileSize.Value.Y);
    }

    /// <summary>An `[x, y]` pair of finite numbers, kept like a loop point (z is always zero).</summary>
    private static PhysicsVector3? ReadPoint2(
        JsonElement element,
        string path,
        string property,
        HashSet<string> seen,
        List<string> errors)
    {
        if (!seen.Contains(property) || !element.TryGetProperty(property, out JsonElement pair))
        {
            return null;
        }

        if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 || !IsFinite(pair[0]) || !IsFinite(pair[1]))
        {
            errors.Add($"{path}.{property}: must be [x, y] with finite numbers.");
            return null;
        }

        return new PhysicsVector3(pair[0].GetSingle(), pair[1].GetSingle(), 0f);
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

    /// <summary>
    /// The level's own camera rectangle (`root.cameraLimits`, v5): exactly `topLeft` and `size`, each
    /// an `[x, y]` pair of finite numbers -- `topLeft` is the rectangle's top-left corner and `size`
    /// extends right and down (`LevelManager.CameraLimits`, `LevelManager.cs:9-16`).
    /// </summary>
    private static CameraLimits? ReadCameraLimits(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: must be a JSON object.");
            return null;
        }

        HashSet<string> seen = new();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"{path}: duplicate property '{property.Name}'.");
            }
            else if (!CameraLimitProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add($"{path}: unknown property '{property.Name}'.");
            }
        }

        RequireExactly(seen, CameraLimitProperties, path, errors);

        PhysicsVector3? topLeft = ReadPoint2(element, path, "topLeft", seen, errors);
        PhysicsVector3? size = ReadPoint2(element, path, "size", seen, errors);
        if (topLeft is not { } corner || size is not { } extent)
        {
            return null;
        }

        if (extent.X <= 0f || extent.Y <= 0f)
        {
            errors.Add($"{path}.size: must be positive on both axes.");
            return null;
        }

        return new CameraLimits(corner.X, corner.Y, extent.X, extent.Y);
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
