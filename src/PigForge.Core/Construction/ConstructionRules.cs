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
    InvalidScale,
    InvalidPosition,
    FootprintTooLarge,
    CellsOccupied,
    PartLimitReached,
    ConnectionLimitReached,
    EntityNotFound,
    NotAConstructionEntity,
    RotationBlocked,
    FrozenEntity,
    UnsupportedShape
}

public readonly record struct ConstructionResult(EntityId Entity, ConstructionError Error)
{
    public bool IsSuccess => Error == ConstructionError.None;
}

/// <summary>
/// Free-placement construction rules (issue #4): parts go down at arbitrary planar poses
/// and scales, occupancy is exact footprint overlap behind a coarse spatial hash, and
/// parts whose footprints come within <see cref="ConnectionProximity"/> connect. Frozen
/// entities (issue #7) keep occupying space at their world poses but are not editable.
/// Deterministic by construction: the same command script produces the same layout,
/// rejections, and state hash. No Unity, physics-native, or renderer types.
/// </summary>
public sealed class ConstructionRules
{
    public const float CellSize = 1f;
    public const float ConnectionProximity = 0.15f;

    /// <summary>Marginal penetrations count as legal touching, not overlap.</summary>
    public const float OverlapTolerance = 0.01f;

    public const float MaxScale = 4f;

    private const float BucketSize = 4f;

    private readonly EntityStore _entities;
    private readonly PartStore _parts;
    private readonly TransformStore _transforms;
    private readonly PartContentLibrary _content;
    private readonly ConstructionLimits _limits;

    // Occupancy and adjacency are build-phase state, not tick hot path, so dictionary
    // allocations here are acceptable.
    private readonly Dictionary<long, List<uint>> _entitiesByBucket = new();
    private readonly Dictionary<uint, PartFootprint> _footprintByEntity = new();
    private readonly Dictionary<uint, HashSet<uint>> _connectionsByEntity = new();
    private readonly HashSet<uint> _frozenEntities = new();

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

    public int PartCount => _footprintByEntity.Count;

    /// <summary>Placed (player-built) entity ids, including frozen ones; level-authoring
    /// spawns are not construction entities and are excluded.</summary>
    public IReadOnlyCollection<uint> PlacedEntities => (IReadOnlyCollection<uint>)_footprintByEntity.Keys;

    public int FrozenCount => _frozenEntities.Count;

    public bool IsAlive(EntityId entity) => _entities.IsAlive(entity);

    public bool IsFrozen(uint entityValue) => _frozenEntities.Contains(entityValue);

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

    public ConstructionResult Place(uint partTypeId, float positionX, float positionY, float angle, float scale)
    {
        if (!float.IsFinite(angle))
        {
            return Failure(ConstructionError.InvalidRotation);
        }

        if (!float.IsFinite(scale) || scale is <= 0f or > MaxScale)
        {
            return Failure(ConstructionError.InvalidScale);
        }

        if (!float.IsFinite(positionX) || !float.IsFinite(positionY))
        {
            return Failure(ConstructionError.InvalidPosition);
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

        PartFootprint footprint = PartFootprint.ForPart(part, positionX, positionY, angle, scale);
        (float minX, float minY, float maxX, float maxY) = footprint.Bounds();
        int areaInCells = (int)MathF.Ceiling(maxX - minX) * (int)MathF.Ceiling(maxY - minY);
        if (areaInCells > _limits.MaxFootprintCells)
        {
            return Failure(ConstructionError.FootprintTooLarge);
        }

        if (_footprintByEntity.Count >= _limits.MaxParts)
        {
            return Failure(ConstructionError.PartLimitReached);
        }

        if (CollectOverlapping(footprint, -OverlapTolerance, exclude: 0).Count > 0)
        {
            return Failure(ConstructionError.CellsOccupied);
        }

        List<uint> neighbours = CollectOverlapping(footprint, ConnectionProximity, exclude: 0);
        if (neighbours.Count > _limits.MaxConnectionsPerPart
            || neighbours.Any(neighbour => ConnectionCount(neighbour) + 1 > _limits.MaxConnectionsPerPart))
        {
            return Failure(ConstructionError.ConnectionLimitReached);
        }

        EntityId entity = _entities.Create();
        SetFootprint(entity.Value, footprint);
        _parts.Set(entity, new PartLink(partTypeId));
        _transforms.Set(entity, new EntityTransform(
            new PhysicsVector3(positionX, positionY, 0f),
            RotationQuaternion(angle),
            scale));

        foreach (uint neighbour in neighbours)
        {
            Link(neighbour, entity.Value);
        }

        _connectionsByEntity.Add(entity.Value, new HashSet<uint>(neighbours));
        return new ConstructionResult(entity, ConstructionError.None);
    }

    public ConstructionResult Rotate(EntityId entity, float angle)
    {
        if (!float.IsFinite(angle))
        {
            return Failure(ConstructionError.InvalidRotation);
        }

        if (!_entities.IsAlive(entity) || !_parts.TryGet(entity, out PartLink link))
        {
            return _entities.IsAlive(entity)
                ? Failure(ConstructionError.NotAConstructionEntity)
                : Failure(ConstructionError.EntityNotFound);
        }

        if (_frozenEntities.Contains(entity.Value))
        {
            return Failure(ConstructionError.FrozenEntity);
        }

        _transforms.TryGet(entity, out EntityTransform current);
        PartDefinition part = _content.GetPart(link.PartTypeId);
        PartFootprint candidate = PartFootprint.ForPart(part, current.Position.X, current.Position.Y, angle, current.Scale);
        if (CollectOverlapping(candidate, -OverlapTolerance, exclude: entity.Value).Count > 0)
        {
            return Failure(ConstructionError.RotationBlocked);
        }

        HashSet<uint> previousNeighbours = new(_connectionsByEntity.TryGetValue(entity.Value, out HashSet<uint>? connections)
            ? connections
            : Array.Empty<uint>());
        UnsetFootprint(entity.Value);
        SetFootprint(entity.Value, candidate);
        _transforms.Set(entity, new EntityTransform(current.Position, RotationQuaternion(angle), current.Scale));

        Reconnect(entity.Value);
        foreach (uint previous in previousNeighbours)
        {
            Reconnect(previous);
        }

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

        if (_frozenEntities.Contains(entity.Value))
        {
            return Failure(ConstructionError.FrozenEntity);
        }

        UnsetFootprint(entity.Value);
        UnlinkAll(entity.Value);
        _connectionsByEntity.Remove(entity.Value);
        _parts.Remove(entity);
        _transforms.Remove(entity);
        _entities.Destroy(entity);
        return new ConstructionResult(entity, ConstructionError.None);
    }

    /// <summary>
    /// Freezes every build entity into a kept group (issue #7): transforms move to the
    /// supplied world poses (entities without a pose keep their build transform), the
    /// group becomes non-editable, and its footprints keep occupying space so fresh
    /// placements cannot intersect the wreckage.
    /// </summary>
    public void FreezeAll(IReadOnlyDictionary<uint, (PhysicsVector3 Position, PhysicsQuaternion Rotation)> worldPoses)
    {
        ArgumentNullException.ThrowIfNull(worldPoses);

        foreach (uint entityValue in _footprintByEntity.Keys.ToArray())
        {
            EntityId entity = new(entityValue);
            _transforms.TryGet(entity, out EntityTransform current);
            EntityTransform target = worldPoses.TryGetValue(entityValue, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) pose)
                ? new EntityTransform(pose.Position, pose.Rotation, current.Scale)
                : current;
            _transforms.Set(entity, target);

            if (!_parts.TryGet(entity, out PartLink link))
            {
                continue;
            }

            UnsetFootprint(entityValue);
            SetFootprint(entityValue, PartFootprint.ForPart(_content.GetPart(link.PartTypeId), target.Position, target.Rotation, target.Scale));
            _frozenEntities.Add(entityValue);
        }
    }

    /// <summary>Destroys every construction entity (build and frozen) in ascending entity order.</summary>
    public List<uint> ResetAll()
    {
        List<uint> destroyed = new(_footprintByEntity.Count);
        destroyed.AddRange(_footprintByEntity.Keys);
        destroyed.Sort();

        _entitiesByBucket.Clear();
        _footprintByEntity.Clear();
        _connectionsByEntity.Clear();
        _frozenEntities.Clear();
        foreach (uint entityValue in destroyed)
        {
            EntityId entity = new(entityValue);
            if (!_entities.IsAlive(entity))
            {
                // An entity may have been destroyed by runtime rules (e.g. TNT blast,
                // detached construction member) during the run without the
                // construction registry being told. Never re-destroy a stale handle.
                _parts.Remove(entity);
                _transforms.Remove(entity);
                continue;
            }

            _parts.Remove(entity);
            _transforms.Remove(entity);
            _entities.Destroy(entity);
        }

        return destroyed;
    }

    /// <summary>Stable hash over part types, poses, scales, and connections.</summary>
    public long ComputeLayoutHash()
    {
        long hash = 17;
        foreach (uint entityValue in _footprintByEntity.Keys.OrderBy(value => value))
        {
            hash = unchecked(hash * 31 + entityValue);
            if (_parts.TryGet(new EntityId(entityValue), out PartLink link))
            {
                hash = unchecked(hash * 31 + link.PartTypeId);
            }

            if (_transforms.TryGet(new EntityId(entityValue), out EntityTransform transform))
            {
                hash = unchecked(hash * 31 + transform.Position.GetHashCode());
                hash = unchecked(hash * 31 + transform.Rotation.GetHashCode());
                hash = unchecked(hash * 31 + transform.Scale.GetHashCode());
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
        if (!_footprintByEntity.TryGetValue(entityValue, out PartFootprint footprint))
        {
            return;
        }

        List<uint> neighbours = CollectOverlapping(footprint, ConnectionProximity, entityValue);
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

    /// <summary>Coarse bucket query followed by the exact footprint test.</summary>
    private List<uint> CollectOverlapping(in PartFootprint footprint, float margin, uint exclude)
    {
        HashSet<uint> seen = new();
        List<uint> results = new();
        (float minX, float minY, float maxX, float maxY) = footprint.Bounds(margin);
        int minBucketX = BucketIndex(minX);
        int maxBucketX = BucketIndex(maxX);
        int minBucketY = BucketIndex(minY);
        int maxBucketY = BucketIndex(maxY);
        for (int bucketY = minBucketY; bucketY <= maxBucketY; bucketY++)
        {
            for (int bucketX = minBucketX; bucketX <= maxBucketX; bucketX++)
            {
                if (!_entitiesByBucket.TryGetValue(PackBucket(bucketX, bucketY), out List<uint>? bucket))
                {
                    continue;
                }

                foreach (uint entityValue in bucket)
                {
                    if (entityValue == exclude || !seen.Add(entityValue) || !_footprintByEntity.TryGetValue(entityValue, out PartFootprint other))
                    {
                        continue;
                    }

                    if (other.Overlaps(footprint, margin))
                    {
                        results.Add(entityValue);
                    }
                }
            }
        }

        return results;
    }

    private void SetFootprint(uint entityValue, in PartFootprint footprint)
    {
        (float minX, float minY, float maxX, float maxY) = footprint.Bounds();
        for (int bucketY = BucketIndex(minY); bucketY <= BucketIndex(maxY); bucketY++)
        {
            for (int bucketX = BucketIndex(minX); bucketX <= BucketIndex(maxX); bucketX++)
            {
                long key = PackBucket(bucketX, bucketY);
                if (!_entitiesByBucket.TryGetValue(key, out List<uint>? bucket))
                {
                    bucket = new List<uint>();
                    _entitiesByBucket.Add(key, bucket);
                }

                bucket.Add(entityValue);
            }
        }

        _footprintByEntity[entityValue] = footprint;
    }

    private void UnsetFootprint(uint entityValue)
    {
        if (!_footprintByEntity.Remove(entityValue, out PartFootprint footprint))
        {
            return;
        }

        (float minX, float minY, float maxX, float maxY) = footprint.Bounds();
        for (int bucketY = BucketIndex(minY); bucketY <= BucketIndex(maxY); bucketY++)
        {
            for (int bucketX = BucketIndex(minX); bucketX <= BucketIndex(maxX); bucketX++)
            {
                if (_entitiesByBucket.TryGetValue(PackBucket(bucketX, bucketY), out List<uint>? bucket))
                {
                    bucket.Remove(entityValue);
                }
            }
        }
    }

    private static int BucketIndex(float value) => (int)MathF.Floor(value / BucketSize);

    private static PhysicsQuaternion RotationQuaternion(float angle)
    {
        float halfAngle = angle / 2f;
        return new PhysicsQuaternion(0f, 0f, MathF.Sin(halfAngle), MathF.Cos(halfAngle));
    }

    private static ConstructionResult Failure(ConstructionError error) => new(default, error);

    private static long PackBucket(int x, int y) => ((long)(uint)x << 32) | (uint)y;
}
