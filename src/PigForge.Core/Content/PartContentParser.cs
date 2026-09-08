using System.Text.Json;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Content;

public sealed class PartContentException : Exception
{
    public PartContentException(IReadOnlyList<string> errors)
        : base($"Part content was rejected with {errors.Count} error(s):{Environment.NewLine}{string.Join(Environment.NewLine, errors.Select(error => $"  - {error}"))}")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Parses and validates part-content JSON. Unknown properties, engine asset references,
/// duplicate ids, and per-kind shape field violations are rejected before the document
/// can be used, so callers can fail fast at startup.
/// </summary>
public static class PartContentParser
{
    private const string GuidHint = "guid";

    public static PartContentDocument Parse(string json)
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
            throw new PartContentException(new[] { "root: content must be a JSON object." });
        }

        HashSet<string> seen = new();
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"root: duplicate property '{property.Name}'.");
            }
        }

        RequireExactly(seen, new[] { "format", "schemaVersion", "contentVersion", "parts" }, "root", errors);
        RejectEngineAssetReferences(seen, "root", errors);

        if (seen.Contains("format") && (root.TryGetProperty("format", out JsonElement format) is false || format.ValueKind != JsonValueKind.String || format.GetString() != PartContentDocument.Format))
        {
            errors.Add($"root.format: must be '{PartContentDocument.Format}'.");
        }

        if (seen.Contains("schemaVersion") && (!root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) || !schemaVersion.TryGetInt32(out int version) || version != PartContentDocument.SchemaVersion))
        {
            errors.Add($"root.schemaVersion: only version {PartContentDocument.SchemaVersion} is supported.");
        }

        string? contentVersion = null;
        if (seen.Contains("contentVersion") && root.TryGetProperty("contentVersion", out JsonElement contentVersionElement))
        {
            contentVersion = ReadVersion(contentVersionElement, "root.contentVersion", errors);
        }

        List<PartDefinition> parts = new();
        if (seen.Contains("parts") && root.TryGetProperty("parts", out JsonElement partsElement))
        {
            if (partsElement.ValueKind != JsonValueKind.Array || partsElement.GetArrayLength() == 0)
            {
                errors.Add("root.parts: must be a non-empty array.");
            }
            else
            {
                int index = 0;
                foreach (JsonElement partElement in partsElement.EnumerateArray())
                {
                    ParsePart(partElement, $"root.parts[{index}]", parts, errors);
                    index++;
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new PartContentException(errors);
        }

        return new PartContentDocument(contentVersion!, parts);
    }

    private static void ParsePart(JsonElement element, string path, List<PartDefinition> parts, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: part must be a JSON object.");
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

        RequireExactly(seen, new[] { "partTypeId", "name", "mode", "mass", "shapes" }, path, errors, "material", "capabilities");
        RejectEngineAssetReferences(seen, path, errors);
        uint partTypeId = 0;
        if (seen.Contains("partTypeId") && element.TryGetProperty("partTypeId", out JsonElement idElement))
        {
            if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetUInt32(out partTypeId) || partTypeId == 0)
            {
                errors.Add($"{path}.partTypeId: must be a positive 32-bit integer.");
                partTypeId = 0;
            }
        }

        if (seen.Contains("name") && element.TryGetProperty("name", out JsonElement nameElement))
        {
            if (nameElement.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{path}.name: must be a string.");
            }
            else
            {
                string nameValue = nameElement.GetString()!;
                if (nameValue.Length is 0 or > 64)
                {
                    errors.Add($"{path}.name: must contain 1 to 64 characters.");
                }
            }
        }

        PhysicsBodyMode mode = PhysicsBodyMode.Dynamic;
        if (seen.Contains("mode") && element.TryGetProperty("mode", out JsonElement modeElement))
        {
            if (modeElement.ValueKind != JsonValueKind.String || !Enum.TryParse(modeElement.GetString(), ignoreCase: true, out mode))
            {
                errors.Add($"{path}.mode: must be 'static' or 'dynamic'.");
                mode = PhysicsBodyMode.Dynamic;
            }
        }

        if (seen.Contains("mass") && element.TryGetProperty("mass", out JsonElement massElement))
        {
            if (massElement.ValueKind != JsonValueKind.Number || !IsFiniteNumber(massElement))
            {
                errors.Add($"{path}.mass: must be a finite number.");
            }
            else
            {
                float mass = massElement.GetSingle();
                if (mode == PhysicsBodyMode.Dynamic && mass <= 0)
                {
                    errors.Add($"{path}.mass: a dynamic part must have a positive mass.");
                }
                else if (mode == PhysicsBodyMode.Static && mass != 0)
                {
                    errors.Add($"{path}.mass: a static part must have a mass of zero.");
                }
            }
        }

        float restitution = 0f;
        float friction = 0.8f;
        if (seen.Contains("material") && element.TryGetProperty("material", out JsonElement materialElement))
        {
            if (materialElement.ValueKind != JsonValueKind.Object
                || !TryReadMaterial(materialElement, path, out restitution, out friction))
            {
                errors.Add($"{path}.material: must be an object with restitution in [0, 1] and friction in [0, 4].");
                restitution = 0f;
                friction = 0.8f;
            }
        }

        PartCapabilities? capabilities = ParseCapabilities(element, seen, path, errors);

        List<PartShapeDefinition> shapes = new();
        if (seen.Contains("shapes") && element.TryGetProperty("shapes", out JsonElement shapesElement))
        {
            if (shapesElement.ValueKind != JsonValueKind.Array || shapesElement.GetArrayLength() == 0)
            {
                errors.Add($"{path}.shapes: must be a non-empty array.");
            }
            else
            {
                int shapeIndex = 0;
                foreach (JsonElement shapeElement in shapesElement.EnumerateArray())
                {
                    ParseShape(shapeElement, $"{path}.shapes[{shapeIndex}]", shapes, errors);
                    shapeIndex++;
                }
            }
        }

        if (partTypeId != 0 && parts.Any(part => part.PartTypeId == partTypeId))
        {
            errors.Add($"{path}.partTypeId: part type id {partTypeId} is declared more than once.");
        }

        parts.Add(new PartDefinition(
            partTypeId,
            seen.Contains("name") && element.TryGetProperty("name", out JsonElement nameProperty) && nameProperty.ValueKind == JsonValueKind.String
                ? nameProperty.GetString()!
                : string.Empty,
            mode,
            seen.Contains("mass") && element.TryGetProperty("mass", out JsonElement massValue) && IsFiniteNumber(massValue)
                ? massValue.GetSingle()
                : 0f,
            restitution,
            friction,
            shapes,
            capabilities));
    }
    private static PartCapabilities? ParseCapabilities(JsonElement element, HashSet<string> seen, string path, List<string> errors)
    {
        if (!seen.Contains("capabilities") || !element.TryGetProperty("capabilities", out JsonElement capabilitiesElement))
        {
            return null;
        }

        if (capabilitiesElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.capabilities: must be an object.");
            return null;
        }

        bool isPig = false;
        bool isWheel = false;
        float? motorThrust = null;
        float? motorDirection = null;
        ushort? tntFuse = null;
        float? balloonLift = null;
        float? fanThrust = null;
        float? fanDirectionX = null;
        float? fanDirectionY = null;
        bool hasError = false;

        HashSet<string> seenKeys = new();
        foreach (JsonProperty property in capabilitiesElement.EnumerateObject())
        {
            if (!seenKeys.Add(property.Name))
            {
                errors.Add($"{path}.capabilities: duplicate property '{property.Name}'.");
            }
        }

        if (seenKeys.Contains("pig"))
        {
            if (!capabilitiesElement.TryGetProperty("pig", out JsonElement pigElement) || pigElement.ValueKind != JsonValueKind.True && pigElement.ValueKind != JsonValueKind.False)
            {
                errors.Add($"{path}.capabilities.pig: must be a boolean.");
                hasError = true;
            }
            else
            {
                isPig = pigElement.GetBoolean();
            }
        }

        if (seenKeys.Contains("wheel"))
        {
            if (!capabilitiesElement.TryGetProperty("wheel", out JsonElement wheelElement) || wheelElement.ValueKind != JsonValueKind.True && wheelElement.ValueKind != JsonValueKind.False)
            {
                errors.Add($"{path}.capabilities.wheel: must be a boolean.");
                hasError = true;
            }
            else
            {
                isWheel = wheelElement.GetBoolean();
            }
        }

        if (seenKeys.Contains("motor"))
        {
            if (!capabilitiesElement.TryGetProperty("motor", out JsonElement motorElement) || !TryReadMotor(motorElement, path, out motorThrust, out motorDirection))
            {
                errors.Add($"{path}.capabilities.motor: must be an object with a finite thrustPerTick and a directionX in the set -1, 0, 1.");
                hasError = true;
            }
        }

        if (seenKeys.Contains("tnt"))
        {
            if (!capabilitiesElement.TryGetProperty("tnt", out JsonElement tntElement) || !TryReadTnt(tntElement, path, out tntFuse))
            {
                errors.Add($"{path}.capabilities.tnt: must be an object with a fuseTicks integer in [0, 65535].");
                hasError = true;
            }
        }

        if (seenKeys.Contains("balloon"))
        {
            if (!capabilitiesElement.TryGetProperty("balloon", out JsonElement balloonElement)
                || balloonElement.ValueKind != JsonValueKind.Number
                || !IsFiniteNumber(balloonElement))
            {
                errors.Add($"{path}.capabilities.balloon: must be a finite liftPerTick number.");
                hasError = true;
            }
            else
            {
                balloonLift = balloonElement.GetSingle();
            }
        }

        if (seenKeys.Contains("fan"))
        {
            if (!capabilitiesElement.TryGetProperty("fan", out JsonElement fanElement) || !TryReadFan(fanElement, path, out fanThrust, out fanDirectionX, out fanDirectionY))
            {
                errors.Add($"{path}.capabilities.fan: must be an object with a finite thrustPerTick and finite directionX/directionY (at least one non-zero).");
                hasError = true;
            }
        }

        foreach (string key in seenKeys)
        {
            if (key is not ("pig" or "wheel" or "motor" or "tnt" or "balloon" or "fan"))
            {
                errors.Add($"{path}.capabilities: unknown property '{key}'.");
                hasError = true;
            }
        }

        if (hasError)
        {
            return null;
        }

        return new PartCapabilities(isPig, isWheel, motorThrust, motorDirection, tntFuse, balloonLift, fanThrust, fanDirectionX, fanDirectionY);
    }

    private static bool TryReadMotor(JsonElement element, string path, out float? thrust, out float? direction)
    {
        thrust = null;
        direction = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty("thrustPerTick", out JsonElement thrustElement)
            || thrustElement.ValueKind != JsonValueKind.Number
            || !IsFiniteNumber(thrustElement)
            || !thrustElement.TryGetSingle(out float thrustValue))
        {
            return false;
        }

        if (!element.TryGetProperty("directionX", out JsonElement directionElement)
            || directionElement.ValueKind != JsonValueKind.Number
            || !directionElement.TryGetInt32(out int directionValue)
            || directionValue is not (-1 or 0 or 1))
        {
            return false;
        }

        thrust = thrustValue;
        direction = directionValue;
        return true;
    }

    private static bool TryReadFan(JsonElement element, string path, out float? thrust, out float? directionX, out float? directionY)
    {
        thrust = null;
        directionX = null;
        directionY = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty("thrustPerTick", out JsonElement thrustElement)
            || thrustElement.ValueKind != JsonValueKind.Number
            || !IsFiniteNumber(thrustElement)
            || !thrustElement.TryGetSingle(out float thrustValue))
        {
            return false;
        }

        if (!TryReadFanDirection(element, out float directionXValue, out float directionYValue))
        {
            return false;
        }

        thrust = thrustValue;
        directionX = directionXValue;
        directionY = directionYValue;
        return true;
    }

    private static bool TryReadFanDirection(JsonElement element, out float directionX, out float directionY)
    {
        directionX = 1f;
        directionY = 0f;
        bool hasX = false;
        bool hasY = false;
        if (element.TryGetProperty("directionX", out JsonElement xElement))
        {
            if (xElement.ValueKind == JsonValueKind.Number && IsFiniteNumber(xElement) && xElement.TryGetSingle(out float x))
            {
                hasX = true;
                directionX = x;
            }
        }

        if (element.TryGetProperty("directionY", out JsonElement yElement))
        {
            if (yElement.ValueKind == JsonValueKind.Number && IsFiniteNumber(yElement) && yElement.TryGetSingle(out float y))
            {
                hasY = true;
                directionY = y;
            }
        }

        return hasX || hasY;
    }

    private static bool TryReadTnt(JsonElement element, string path, out ushort? fuse)
    {
        fuse = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty("fuseTicks", out JsonElement fuseElement)
            || fuseElement.ValueKind != JsonValueKind.Number
            || !fuseElement.TryGetUInt16(out ushort fuseValue))
        {
            return false;
        }

        fuse = fuseValue;
        return true;
    }

    private static void ParseShape(JsonElement element, string path, List<PartShapeDefinition> shapes, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: shape must be a JSON object.");
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

        if (!seen.Contains("kind"))
        {
            errors.Add($"{path}: shape requires a 'kind'.");
            return;
        }

        if (!element.TryGetProperty("kind", out JsonElement kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || !Enum.TryParse(kindElement.GetString(), ignoreCase: true, out PhysicsShapeKind kind))
        {
            errors.Add($"{path}.kind: '{kindElement.GetString()}' is not a supported shape kind.");
            return;
        }

        RejectEngineAssetReferences(seen, path, errors);

        string kindStart = char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];
        string[] allowed = kind switch
        {
            PhysicsShapeKind.Box => new[] { "kind", "halfExtents", "offset" },
            PhysicsShapeKind.Sphere => new[] { "kind", "radius", "offset" },
            PhysicsShapeKind.Capsule => new[] { "kind", "radius", "cylinderHalfHeight", "offset" },
            PhysicsShapeKind.ConvexMesh => new[] { "kind", "vertices", "offset" },
            PhysicsShapeKind.TriangleMesh => new[] { "kind", "vertices", "triangles", "offset" },
            _ => new[] { "kind" }
        };

        foreach (string property in seen)
        {
            if (!allowed.Contains(property))
            {
                errors.Add($"{path}: property '{property}' is not valid for kind '{kindStart}'.");
            }
        }

        float[]? halfExtents = null;
        if (kind == PhysicsShapeKind.Box)
        {
            halfExtents = ReadPositiveVector3(element, path, "halfExtents", errors);
        }

        float? radius = null;
        if (allowed.Contains("radius"))
        {
            radius = ReadPositiveNumber(element, path, "radius", errors);
        }

        float? cylinderHalfHeight = null;
        if (kind == PhysicsShapeKind.Capsule)
        {
            cylinderHalfHeight = ReadPositiveNumber(element, path, "cylinderHalfHeight", errors);
        }

        float[][]? vertices = null;
        if (allowed.Contains("vertices"))
        {
            vertices = ReadVertices(element, path, errors);
        }

        uint[]? triangles = null;
        if (kind == PhysicsShapeKind.TriangleMesh && seen.Contains("triangles"))
        {
            triangles = ReadTriangles(element, path, vertices, errors);
        }

        float[]? offset = null;
        if (seen.Contains("offset"))
        {
            offset = ReadVector3(element, path, "offset", errors);
        }

        shapes.Add(new PartShapeDefinition(
            kind,
            halfExtents,
            radius,
            cylinderHalfHeight,
            vertices,
            triangles,
            offset));
    }

    private static bool TryReadMaterial(JsonElement element, string path, out float restitution, out float friction)
    {
        restitution = 0f;
        friction = 0.8f;
        HashSet<string> seen = new();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return false;
            }
        }

        if (!seen.SetEquals(new HashSet<string> { "restitution", "friction" })
            || !element.TryGetProperty("restitution", out JsonElement restitutionElement)
            || !element.TryGetProperty("friction", out JsonElement frictionElement)
            || restitutionElement.ValueKind != JsonValueKind.Number
            || frictionElement.ValueKind != JsonValueKind.Number
            || !IsFiniteNumber(restitutionElement)
            || !IsFiniteNumber(frictionElement))
        {
            return false;
        }

        restitution = restitutionElement.GetSingle();
        friction = frictionElement.GetSingle();
        return restitution is >= 0f and <= 1f && friction is >= 0f and <= 4f;
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

    private static float[]? ReadPositiveVector3(JsonElement element, string path, string field, List<string> errors)
    {
        float[]? value = ReadVector3(element, path, field, errors);
        if (value is not null && value.Any(component => component <= 0))
        {
            errors.Add($"{path}.{field}: all components must be positive.");
            return null;
        }

        return value;
    }

    private static float? ReadPositiveNumber(JsonElement element, string path, string field, List<string> errors)
    {
        if (!element.TryGetProperty(field, out JsonElement value))
        {
            errors.Add($"{path}: kind requires a positive '{field}'.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !IsFiniteNumber(value) || value.GetSingle() <= 0)
        {
            errors.Add($"{path}.{field}: must be a finite positive number.");
            return null;
        }

        return value.GetSingle();
    }

    private static float[]? ReadVector3(JsonElement element, string path, string field, List<string> errors)
    {
        if (!element.TryGetProperty(field, out JsonElement value))
        {
            errors.Add($"{path}: kind requires '{field}'.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3 || value.EnumerateArray().Any(component => component.ValueKind != JsonValueKind.Number || !IsFiniteNumber(component)))
        {
            errors.Add($"{path}.{field}: must be an array of three finite numbers.");
            return null;
        }

        return value.EnumerateArray().Select(component => component.GetSingle()).ToArray();
    }

    private static float[][]? ReadVertices(JsonElement element, string path, List<string> errors)
    {
        if (!element.TryGetProperty("vertices", out JsonElement value))
        {
            errors.Add($"{path}: mesh kind requires 'vertices'.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 4)
        {
            errors.Add($"{path}.vertices: meshes require at least four vertices.");
            return null;
        }

        List<float[]> vertices = new();
        int index = 0;
        foreach (JsonElement vertex in value.EnumerateArray())
        {
            if (vertex.ValueKind != JsonValueKind.Array || vertex.GetArrayLength() != 3 || vertex.EnumerateArray().Any(component => component.ValueKind != JsonValueKind.Number || !IsFiniteNumber(component)))
            {
                errors.Add($"{path}.vertices[{index}]: must be an array of three finite numbers.");
                return null;
            }

            vertices.Add(vertex.EnumerateArray().Select(component => component.GetSingle()).ToArray());
            index++;
        }

        return vertices.ToArray();
    }

    private static uint[]? ReadTriangles(JsonElement element, string path, float[][]? vertices, List<string> errors)
    {
        if (!element.TryGetProperty("triangles", out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.triangles: must be an array of vertex indices.");
            return null;
        }

        List<uint> triangles = new();
        int index = 0;
        foreach (JsonElement triangle in value.EnumerateArray())
        {
            if (triangle.ValueKind != JsonValueKind.Number || !triangle.TryGetUInt32(out uint vertexIndex)
                || (vertices is not null && vertexIndex >= vertices.Length))
            {
                errors.Add($"{path}.triangles[{index}]: must reference a valid vertex index.");
                return null;
            }

            triangles.Add(vertexIndex);
            index++;
        }

        return triangles.ToArray();
    }

    private static void RejectEngineAssetReferences(HashSet<string> properties, string path, List<string> errors)
    {
        foreach (string property in properties)
        {
            if (property.Contains(GuidHint, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{path}: property '{property}' looks like an engine asset reference. Part content must stay engine-agnostic.");
            }
        }
    }

    private static void RequireExactly(HashSet<string> seen, string[] required, string path, List<string> errors, params string[] optionalProperties)
    {
        foreach (string name in required)
        {
            if (!seen.Contains(name))
            {
                errors.Add($"{path}: missing required property '{name}'.");
            }
        }

        foreach (string name in seen)
        {
            if (!required.Contains(name) && !optionalProperties.Contains(name))
            {
                errors.Add($"{path}: unknown property '{name}'.");
            }
        }
    }

    private static bool IsFiniteNumber(JsonElement value) =>
        !float.IsNaN(value.GetSingle()) && !float.IsInfinity(value.GetSingle());
}
