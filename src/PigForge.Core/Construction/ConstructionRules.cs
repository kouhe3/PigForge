using PigForge.Physics.Abstractions;

using PigForge.Core.Content;

namespace PigForge.Core.Construction;

public sealed record ConstructionLimits(
    int MaxParts,
    int MaxConnectionsPerPart,
    int MaxFootprintCells)
{
    public static ConstructionLimits Default { get; } = new(MaxParts: 256, MaxConnectionsPerPart: 6, MaxFootprintCells: 64);
}

public enum ConstructionError
{
    None,
    UnknownPartType,
    InvalidRotation,
    FootprintTooLarge,
    CellsOccupied,
    PartLimitReached,
    ConnectionLimitReached,
    EntityNotFound,
    NotAConstructionEntity,
    RotationBlocked
}

public readonly record struct ConstructionResult(EntityId Entity, ConstructionError Error)
{
    public bool IsSuccess => Error == ConstructionError.None;
}

/// <summary>
/// Grid-based construction rules (place, remove, rotate, occupancy, adjacency
/// connections and caps) expressed purely with Core handles and content — no Unity,
/// physics-native, or renderer types. Deterministic by construction: the same command
/// script produces the same layout, rejections, and state hash.
/// </summary>
public sealed class ConstructionRules
{
    public const float CellSize = 1f;

    private readonly EntityStore _entities;
    private readonly PartStore _parts;
    private readonly TransformStore _transforms;
    private readonly PartContentLibrary _content;
    private readonly ConstructionLimits _limits;

    // Cell ownership and per-entity adjacency are build-phase state, not tick hot path,
    // so dictionary allocations here are acceptable.
    private readonly Dictionary<long, uint> _ownerByCell = new();
    private readonly Dictionary<uint, List<long>> _cellsByEntity = new();
    private readonly Dictionary<uint, HashSet<uint>> _connectionsByEntity = new();

    public ConstructionRules(
        EntityStore entities,
        PartStore parts,
        TransformStore transforms,
        PartContentLibrary content,
        ConstructionLimits? limits = null)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _parts = parts ?? throw new ArgumentNullException(nameof(parts));
        _transforms = transforms ?? throw new ArgumentNullException(nameof(transforms));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _limits = limits ?? ConstructionLimits.Default;
    }

    public int PartCount => _cellsByEntity.Count;

    public bool IsAlive(EntityId entity) => _entities.IsAlive(entity);

    public bool TryGetTransform(EntityId entity, out EntityTransform transform) => _transforms.TryGet(entity, out transform);

    public bool TryGetPartTypeId(EntityId entity, out uint partTypeId)
    {
        if (_parts.TryGet(entity, out PartLink link))
        {
            partTypeId = link.PartTypeId;
            return true;
        }

        partTypeId = 0;
        return false;
    }

    public IReadOnlyCollection<uint> ConnectionsOf(EntityId entity)
    {
        return _connectionsByEntity.TryGetValue(entity.Value, out HashSet<uint>? connections)
            ? (IReadOnlyCollection<uint>)connections
            : Array.Empty<uint>();
    }

    public ConstructionResult Place(uint partTypeId, int gridX, int gridY, byte rotation)
    {
        if (!IsValidRotation(rotation))
        {
            return Failure(ConstructionError.InvalidRotation);
        }

        PartDefinition part;
        try
        {
            part = _content.GetPart(partTypeId);
        }
        catch (KeyNotFoundException)
        {
            return Failure(ConstructionError.UnknownPartType);
        }

        (int width, int height) = FootprintInCells(part, rotation);
        if (width * height > _limits.MaxFootprintCells)
        {
            return Failure(ConstructionError.FootprintTooLarge);
        }

        if (_cellsByEntity.Count >= _limits.MaxParts)
        {
            return Failure(ConstructionError.PartLimitReached);
        }

        List<long> cells = AcquireCells(gridX, gridY, width, height);
        foreach (long cell in cells)
        {
            if (_ownerByCell.ContainsKey(cell))
            {
                return Failure(ConstructionError.CellsOccupied);
            }
        }

        List<uint> neighbours = CollectNeighbours(cells, exclude: 0);
        if (neighbours.Count > _limits.MaxConnectionsPerPart
            || neighbours.Any(neighbour => ConnectionCount(neighbour) + 1 > _limits.MaxConnectionsPerPart))
        {
            return Failure(ConstructionError.ConnectionLimitReached);
        }

        EntityId entity = _entities.Create();
        foreach (long cell in cells)
        {
            _ownerByCell.Add(cell, entity.Value);
        }

        _cellsByEntity.Add(entity.Value, cells);
        _parts.Set(entity, new PartLink(partTypeId));
        _transforms.Set(entity, new EntityTransform(
            CellCenter(gridX, gridY, width, height),
            RotationQuaternion(rotation)));

        foreach (uint neighbour in neighbours)
        {
            Link(neighbour, entity.Value);
        }

        _connectionsByEntity.Add(entity.Value, new HashSet<uint>(neighbours));
        return new ConstructionResult(entity, ConstructionError.None);
    }

    public ConstructionResult Remove(EntityId entity)
    {
        if (!_entities.IsAlive(entity))
        {
            return Failure(ConstructionError.EntityNotFound);
        }

        if (!_parts.TryGet(entity, out _))
        {
            return Failure(ConstructionError.NotAConstructionEntity);
        }

        foreach (long cell in _cellsByEntity[entity.Value])
        {
            _ownerByCell.Remove(cell);
        }

        _cellsByEntity.Remove(entity.Value);
        UnlinkAll(entity.Value);
        _parts.Remove(entity);
        _transforms.Remove(entity);
        _entities.Destroy(entity);
        return new ConstructionResult(entity, ConstructionError.None);
    }

    public ConstructionResult Rotate(EntityId entity, byte rotation)
    {
        if (!IsValidRotation(rotation))
        {
            return Failure(ConstructionError.InvalidRotation);
        }

        if (!_entities.IsAlive(entity) || !_parts.TryGet(entity, out PartLink link))
        {
            return _entities.IsAlive(entity)
                ? Failure(ConstructionError.NotAConstructionEntity)
                : Failure(ConstructionError.EntityNotFound);
        }

        List<long> currentCells = _cellsByEntity[entity.Value];
        (int anchorX, int anchorY) = CellAnchor(currentCells);
        PartDefinition part = _content.GetPart(link.PartTypeId);
        (int width, int height) = FootprintInCells(part, rotation);
        List<long> targetCells = AcquireCells(anchorX, anchorY, width, height);
        bool blocked = targetCells.Any(cell =>
            _ownerByCell.TryGetValue(cell, out uint owner) && owner != entity.Value);

        if (blocked)
        {
            return Failure(ConstructionError.RotationBlocked);
        }

        var affectedNeighbours = new HashSet<uint>(_connectionsByEntity[entity.Value]);
        foreach (long cell in currentCells)
        {
            _ownerByCell.Remove(cell);
        }

        foreach (long cell in targetCells)
        {
            _ownerByCell.Add(cell, entity.Value);
        }

        _cellsByEntity[entity.Value] = targetCells;
        _transforms.Set(entity, new EntityTransform(
            CellCenter(anchorX, anchorY, width, height),
            RotationQuaternion(rotation)));

        Reconnect(entity.Value);
        foreach (uint neighbour in affectedNeighbours)
        {
            Reconnect(neighbour);
        }

        return new ConstructionResult(entity, ConstructionError.None);
    }

    /// <summary>Stable hash over layout, transforms, connections, and rejections-friendly state.</summary>
    public long ComputeLayoutHash()
    {
        long hash = 17;
        foreach (uint entityValue in _cellsByEntity.Keys.OrderBy(value => value))
        {
            hash = unchecked(hash * 31 + entityValue);
            foreach (long cell in _cellsByEntity[entityValue].OrderBy(cell => cell))
            {
                hash = unchecked(hash * 31 + cell);
            }

            if (_parts.TryGet(new EntityId(entityValue), out PartLink link))
            {
                hash = unchecked(hash * 31 + link.PartTypeId);
            }

            if (_transforms.TryGet(new EntityId(entityValue), out EntityTransform transform))
            {
                hash = unchecked(hash * 31 + transform.Position.GetHashCode());
                hash = unchecked(hash * 31 + transform.Rotation.GetHashCode());
            }

            IEnumerable<uint> orderedConnections = _connectionsByEntity.TryGetValue(entityValue, out HashSet<uint>? connections)
                ? connections.OrderBy(value => value)
                : Array.Empty<uint>();
            foreach (uint connection in orderedConnections)
            {
                hash = unchecked(hash * 31 + connection);
            }
        }

        return hash;
    }

    private void Reconnect(uint entityValue)
    {
        if (!_cellsByEntity.TryGetValue(entityValue, out List<long>? cells))
        {
            return;
        }

        List<uint> neighbours = CollectNeighbours(cells, entityValue);
        UnlinkAll(entityValue);
        _connectionsByEntity[entityValue] = new HashSet<uint>(neighbours);
        foreach (uint neighbour in neighbours)
        {
            Link(neighbour, entityValue);
        }
    }

    private void Link(uint left, uint right)
    {
        if (!_connectionsByEntity.TryGetValue(left, out HashSet<uint>? connections))
        {
            connections = new HashSet<uint>();
            _connectionsByEntity.Add(left, connections);
        }

        connections.Add(right);
    }

    private void UnlinkAll(uint entityValue)
    {
        if (_connectionsByEntity.Remove(entityValue, out HashSet<uint>? connections))
        {
            foreach (uint neighbour in connections)
            {
                if (_connectionsByEntity.TryGetValue(neighbour, out HashSet<uint>? other))
                {
                    other.Remove(entityValue);
                }
            }
        }
    }

    private int ConnectionCount(uint entityValue) =>
        _connectionsByEntity.TryGetValue(entityValue, out HashSet<uint>? connections) ? connections.Count : 0;

    private List<uint> CollectNeighbours(List<long> cells, uint exclude)
    {
        var neighbours = new HashSet<uint>();
        foreach (long cell in cells)
        {
            (int x, int y) = UnpackCell(cell);
            TryAddNeighbour(neighbours, x - 1, y, exclude);
            TryAddNeighbour(neighbours, x + 1, y, exclude);
            TryAddNeighbour(neighbours, x, y - 1, exclude);
            TryAddNeighbour(neighbours, x, y + 1, exclude);
        }

        return neighbours.ToList();
    }

    private void TryAddNeighbour(HashSet<uint> neighbours, int x, int y, uint exclude)
    {
        if (_ownerByCell.TryGetValue(PackCell(x, y), out uint owner) && owner != exclude)
        {
            neighbours.Add(owner);
        }
    }

    private static List<long> AcquireCells(int gridX, int gridY, int width, int height)
    {
        var cells = new List<long>(width * height);
        for (int offsetY = 0; offsetY < height; offsetY++)
        {
            for (int offsetX = 0; offsetX < width; offsetX++)
            {
                cells.Add(PackCell(gridX + offsetX, gridY + offsetY));
            }
        }

        return cells;
    }

    private (int AnchorX, int AnchorY) CellAnchor(List<long> cells)
    {
        (int firstX, int firstY) = UnpackCell(cells[0]);
        int minX = firstX;
        int minY = firstY;
        foreach (long cell in cells)
        {
            (int x, int y) = UnpackCell(cell);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
        }

        return (minX, minY);
    }

    private static (int Width, int Height) FootprintInCells(PartDefinition part, byte rotation)
    {
        PartShapeDefinition shape = part.Shapes[0];
        (float width, float height) = shape.Kind switch
        {
            PhysicsShapeKind.Box when shape.BoxHalfExtents is { Length: 3 } halfExtents => (halfExtents[0] * 2f, halfExtents[1] * 2f),
            PhysicsShapeKind.Sphere when shape.Radius is float radius => (radius * 2f, radius * 2f),
            _ => throw new NotSupportedException($"Part type {part.PartTypeId} has no grid footprint rule for shape kind {shape.Kind}.")
        };

        int widthCells = Math.Max(1, (int)MathF.Ceiling(width / CellSize));
        int heightCells = Math.Max(1, (int)MathF.Ceiling(height / CellSize));
        return rotation % 2 == 0 ? (widthCells, heightCells) : (heightCells, widthCells);
    }

    private static PhysicsVector3 CellCenter(int gridX, int gridY, int width, int height) => new(
        (gridX + width / 2f) * CellSize,
        (gridY + height / 2f) * CellSize,
        0f);

    private static PhysicsQuaternion RotationQuaternion(byte rotation)
    {
        float halfAngle = rotation * MathF.PI / 4f;
        return new PhysicsQuaternion(0f, 0f, MathF.Sin(halfAngle), MathF.Cos(halfAngle));
    }

    private static bool IsValidRotation(byte rotation) => rotation <= 3;

    private static ConstructionResult Failure(ConstructionError error) => new(default, error);

    private static long PackCell(int x, int y) => ((long)(uint)y << 32) | (uint)x;

    private static (int X, int Y) UnpackCell(long cell) => ((int)(uint)cell, (int)(uint)(cell >> 32));
}
