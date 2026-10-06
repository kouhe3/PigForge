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
    TransformBlocked,
    FrozenEntity,
    UnsupportedShape,
    NotOwnedByPlayer,
    PartNotSwitchable,
    PartNotMirrorable
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

    /// <summary>Cells an attachment searches for its anchor (INSettingsBExp.json:
    /// <c>SandbagConnectionDistance</c>/<c>BalloonConnectionDistance</c> = 10).</summary>
    public const int AttachmentSearchCells = 10;

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

    // Enclosure is build-phase state like occupancy: one level deep, one part per frame
    // (spec §2.2-5). The paired lookups let both directions be queried and released.
    private readonly Dictionary<uint, uint> _enclosedBy = new();
    private readonly Dictionary<uint, uint> _enclosedPart = new();

    private readonly Dictionary<uint, uint> _ownerByEntity = new();
    private readonly Dictionary<uint, int> _partCountByOwner = new();

    // The build pose's handedness, kept beside the pose because a quaternion cannot carry it
    // unambiguously (see BuildPose). It survives FreezeAll: starting the simulation rewrites the
    // transforms with physics poses, and the mirror is a property of how the part was built.
    private readonly HashSet<uint> _mirroredEntities = new();

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

    /// <summary>The owner recorded for a placed entity, or null when the registry does not track it.</summary>
    public uint? OwnerOf(EntityId entity) =>
        _ownerByEntity.TryGetValue(entity.Value, out uint owner) ? owner : null;

    /// <summary>
    /// Whether the part was built mirrored (the original's `FlipVertically` handedness, ADR-030).
    /// Content gates it: only a part whose prefab declares `m_autoAlign == 2` -- the wing and tail
    /// families -- may be mirrored, exactly as the original only flips those.
    /// </summary>
    public bool IsMirrored(EntityId entity) => _mirroredEntities.Contains(entity.Value);

    /// <summary>Placed entity ids owned by the given player, ascending.</summary>
    public IReadOnlyCollection<uint> PlacedEntitiesOf(uint owner)
    {
        List<uint> owned = new();
        foreach ((uint entityValue, uint entityOwner) in _ownerByEntity)
        {
            if (entityOwner == owner)
            {
                owned.Add(entityValue);
            }
        }

        if (owned.Count == 0)
        {
            return Array.Empty<uint>();
        }

        owned.Sort();
        return owned;
    }

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

    /// <summary>The frame this entity is enclosed in, or null when it is free-standing cargo.</summary>
    public EntityId? EnclosedBy(EntityId entity) =>
        _enclosedBy.TryGetValue(entity.Value, out uint frame) ? new EntityId(frame) : null;

    /// <summary>The part enclosed in this frame, or null when the frame's slot is free.</summary>
    public EntityId? EnclosedPart(EntityId frame) =>
        _enclosedPart.TryGetValue(frame.Value, out uint part) ? new EntityId(part) : null;

    /// <summary>
    /// The original's chassis gate: a propulsion part is only valid when at least one of its grid
    /// neighbours is part of the chassis. <c>Frame.IsPartOfChassis()</c> is the only override that
    /// returns true (<c>Frame.cs:37-40</c>; the base is <c>BasePart.cs:1169-1172</c>), and
    /// <c>BasePropulsion.ValidatePart</c> counts those neighbours (<c>BasePropulsion.cs:13-20</c>;
    /// <c>Wings.cs:14-31</c> and <c>Tail.cs:12-29</c> repeat the same loop). PigForge has no build
    /// grid, so a neighbour is a connection (the proximity adjacency
    /// <see cref="ConnectionsOf"/> exposes) and a chassis is a part the content marks
    /// <c>canEnclose</c> — the WoodenFrame/MetalFrame families (ADR-011 decision 1;
    /// <c>tools/bple-joints/apply-joints.mjs:10</c>). The enclosure edge counts as well: the
    /// original bolts an enclosed part to its frame with a FixedJoint (<c>Frame.cs:44-50</c>), so
    /// it is as attached to the chassis as a grid neighbour is.
    /// </summary>
    public bool HasChassisNeighbor(EntityId entity)
    {
        if (_connectionsByEntity.TryGetValue(entity.Value, out HashSet<uint>? connections))
        {
            foreach (uint neighbour in connections)
            {
                if (IsChassis(neighbour))
                {
                    return true;
                }
            }
        }

        return (_enclosedBy.TryGetValue(entity.Value, out uint enclosingFrame) && IsChassis(enclosingFrame))
            || (_enclosedPart.TryGetValue(entity.Value, out uint heldPart) && IsChassis(heldPart));
    }

    /// <summary>True when the placed entity is a frame: the content's <c>canEnclose</c> flag
    /// (ADR-011 decision 1), which mirrors the original's <c>Frame.IsPartOfChassis</c>.</summary>
    public bool IsChassis(EntityId entity) => IsChassis(entity.Value);

    private bool IsChassis(uint entityValue) =>
        _parts.TryGet(new EntityId(entityValue), out PartLink link)
        && _content.GetPart(link.PartTypeId).Capabilities?.CanEnclose == true;

    /// <summary>
    /// The first legal runtime-attachment anchor along a direction, or null. The original walks
    /// the build grid one cell at a time (Sandbag.cs:96-102, Balloon.cs:104-107) up to
    /// <c>SandbagConnectionDistance</c>/<c>BalloonConnectionDistance</c> = 10 cells
    /// (INSettingsBExp.json), discarding every part that is neither chassis nor pig — a wheel, a
    /// TNT, another sandbag — and stopping at the first that is. A balloon's extra Kicker
    /// exemption needs no case here: the kicker is a <see cref="JointConnectionType.Source"/>
    /// part in our content, so the chassis test already accepts it. Deterministic: cells are
    /// walked outward from the part's own cell and each cell's parts are taken in ascending
    /// EntityId order.
    /// </summary>
    public EntityId? FindAttachmentTarget(EntityId attach, int directionX, int directionY)
    {
        if ((directionX == 0 && directionY == 0)
            || !_transforms.TryGet(attach, out EntityTransform origin)
            || !_ownerByEntity.TryGetValue(attach.Value, out uint owner))
        {
            return null;
        }

        int cellX = (int)MathF.Floor(origin.Position.X / CellSize);
        int cellY = (int)MathF.Floor(origin.Position.Y / CellSize);
        int stepX = Math.Sign(directionX);
        int stepY = Math.Sign(directionY);
        for (int distance = 1; distance <= AttachmentSearchCells; distance++)
        {
            EntityId anchor = FindAnchorInCell(cellX + (stepX * distance), cellY + (stepY * distance), attach.Value, owner);
            if (anchor.IsValid)
            {
                return anchor;
            }
        }

        return null;
    }

    private EntityId FindAnchorInCell(int cellX, int cellY, uint attach, uint owner)
    {
        float minX = cellX * CellSize;
        float minY = cellY * CellSize;
        float maxX = minX + CellSize;
        float maxY = minY + CellSize;
        EntityId anchor = default;
        HashSet<uint>? tested = null;
        for (int bucketY = BucketIndex(minY); bucketY <= BucketIndex(maxY); bucketY++)
        {
            for (int bucketX = BucketIndex(minX); bucketX <= BucketIndex(maxX); bucketX++)
            {
                if (!_entitiesByBucket.TryGetValue(PackBucket(bucketX, bucketY), out List<uint>? bucket))
                {
                    continue;
                }

                foreach (uint entityValue in bucket)
                {
                    if (entityValue == attach
                        || (tested ??= new HashSet<uint>()).Add(entityValue) is false
                        || !IsOwnedBy(entityValue, owner)
                        || (anchor.IsValid && entityValue > anchor.Value)
                        || !_footprintByEntity.TryGetValue(entityValue, out PartFootprint footprint)
                        || !_parts.TryGet(new EntityId(entityValue), out PartLink link))
                    {
                        continue;
                    }

                    (float otherMinX, float otherMinY, float otherMaxX, float otherMaxY) = footprint.Bounds();
                    if (otherMinX >= maxX || otherMaxX <= minX || otherMinY >= maxY || otherMaxY <= minY)
                    {
                        continue;
                    }

                    PartCapabilities? capabilities = _content.GetPart(link.PartTypeId).Capabilities;
                    if (capabilities?.JointConnectionType != JointConnectionType.Source
                        && capabilities?.IsPig != true)
                    {
                        continue;
                    }

                    anchor = new EntityId(entityValue);
                }
            }
        }

        return anchor;
    }

    /// <summary>
    /// Puts a part down at a planar pose. <paramref name="mirrored"/> is the pose's handedness
    /// (ADR-030) and is only accepted for content that declares `capabilities.mirror` -- the
    /// original only flips the parts whose prefab says <c>m_autoAlign == FlipVertically</c>.
    /// </summary>
    public ConstructionResult Place(uint partTypeId, float positionX, float positionY, float angle, float scale, uint owner, bool mirrored = false)
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

        if (mirrored && part.Capabilities?.Mirror != true)
        {
            return Failure(ConstructionError.PartNotMirrorable);
        }

        PartFootprint footprint = PartFootprint.ForPart(part, positionX, positionY, angle, mirrored, scale);
        (float minX, float minY, float maxX, float maxY) = footprint.Bounds();
        int areaInCells = (int)MathF.Ceiling(maxX - minX) * (int)MathF.Ceiling(maxY - minY);
        if (areaInCells > _limits.MaxFootprintCells)
        {
            return Failure(ConstructionError.FootprintTooLarge);
        }

        if (OwnerPartCount(owner) >= _limits.MaxParts)
        {
            return Failure(ConstructionError.PartLimitReached);
        }

        List<uint> overlaps = CollectOverlapping(footprint, -OverlapTolerance, exclude: 0, connect: false);
        uint enclosingFrame = 0;
        if (overlaps.Count > 0
            && !TryResolveEnclosure(part, owner, overlaps, self: 0, out enclosingFrame))
        {
            return Failure(ConstructionError.CellsOccupied);
        }

        List<uint> candidates = CollectOverlapping(footprint, ConnectionProximity, exclude: 0, connect: true);
        List<uint> neighbours = new(candidates.Count);
        foreach (uint candidate in candidates)
        {
            if (IsOwnedBy(candidate, owner))
            {
                neighbours.Add(candidate);
            }
        }

        if (neighbours.Count > _limits.MaxConnectionsPerPart
            || neighbours.Any(neighbour => ConnectionCount(neighbour) + 1 > _limits.MaxConnectionsPerPart))
        {
            return Failure(ConstructionError.ConnectionLimitReached);
        }

        EntityId entity = _entities.Create();
        SetFootprint(entity.Value, footprint);
        _ownerByEntity.Add(entity.Value, owner);
        _partCountByOwner[owner] = OwnerPartCount(owner) + 1;
        _parts.Set(entity, new PartLink(partTypeId));
        _transforms.Set(entity, new EntityTransform(
            new PhysicsVector3(positionX, positionY, 0f),
            BuildPose.Rotation(angle, mirrored),
            scale));
        if (mirrored)
        {
            _mirroredEntities.Add(entity.Value);
        }

        foreach (uint neighbour in neighbours)
        {
            Link(neighbour, entity.Value);
        }

        _connectionsByEntity.Add(entity.Value, new HashSet<uint>(neighbours));
        if (enclosingFrame != 0)
        {
            _enclosedBy[entity.Value] = enclosingFrame;
            _enclosedPart[enclosingFrame] = entity.Value;
        }

        return new ConstructionResult(entity, ConstructionError.None);
    }

    public ConstructionResult Move(EntityId entity, float positionX, float positionY, uint owner) =>
        Retransform(entity, owner, positionX, positionY, angle: null, mirrored: null, scale: null);

    public ConstructionResult Scale(EntityId entity, float scale, uint owner) =>
        Retransform(entity, owner, positionX: null, positionY: null, angle: null, mirrored: null, scale: scale);

    /// <summary>
    /// Re-aims the pose's yaw and keeps its handedness: this is what the original's
    /// <c>RotateClockwise</c> does (it never clears <c>m_flipped</c>).
    /// </summary>
    public ConstructionResult Rotate(EntityId entity, float angle, uint owner) =>
        Retransform(entity, owner, positionX: null, positionY: null, angle: angle, mirrored: null, scale: null);

    /// <summary>
    /// Sets the pose's yaw <b>and</b> handedness together -- the wire's absolute form (ADR-030):
    /// a client's rotate carries the mirror it is showing rather than toggling anything.
    /// </summary>
    public ConstructionResult Rotate(EntityId entity, float angle, uint owner, bool mirrored) =>
        Retransform(entity, owner, positionX: null, positionY: null, angle: angle, mirrored: mirrored, scale: null);

    /// <summary>
    /// Shared path for every post-placement transform (move/rotate/scale): the target pose
    /// is the current pose with the supplied components replaced, then re-validated
    /// (existence, ownership, frozen state, finite values, footprint area, overlap, and
    /// connection limits) before the footprint is migrated, the transform written, and the
    /// entity and its previous neighbours reconnected.
    /// </summary>
    private ConstructionResult Retransform(
        EntityId entity,
        uint owner,
        float? positionX,
        float? positionY,
        float? angle,
        bool? mirrored,
        float? scale)
    {
        if (!_entities.IsAlive(entity))
        {
            return Failure(ConstructionError.EntityNotFound);
        }

        if (!_parts.TryGet(entity, out PartLink link))
        {
            return Failure(ConstructionError.NotAConstructionEntity);
        }

        if (!IsOwnedBy(entity.Value, owner))
        {
            return Failure(ConstructionError.NotOwnedByPlayer);
        }

        if (_frozenEntities.Contains(entity.Value))
        {
            return Failure(ConstructionError.FrozenEntity);
        }

        if ((positionX.HasValue && !float.IsFinite(positionX.Value))
            || (positionY.HasValue && !float.IsFinite(positionY.Value)))
        {
            return Failure(ConstructionError.InvalidPosition);
        }

        if (angle.HasValue && !float.IsFinite(angle.Value))
        {
            return Failure(ConstructionError.InvalidRotation);
        }

        if (scale.HasValue && (!float.IsFinite(scale.Value) || scale.Value is <= 0f or > MaxScale))
        {
            return Failure(ConstructionError.InvalidScale);
        }

        _transforms.TryGet(entity, out EntityTransform current);
        float targetX = positionX ?? current.Position.X;
        float targetY = positionY ?? current.Position.Y;
        float targetScale = scale ?? current.Scale;
        PartDefinition part = _content.GetPart(link.PartTypeId);
        bool targetMirrored = mirrored ?? _mirroredEntities.Contains(entity.Value);
        if (targetMirrored && part.Capabilities?.Mirror != true)
        {
            return Failure(ConstructionError.PartNotMirrorable);
        }

        // The pose is yaw + handedness, so a move/scale keeps both from the stored pose: a mirrored
        // part's quaternion cannot be asked for its yaw (see BuildPose).
        float targetYaw = angle ?? BuildPose.YawOf(current.Rotation, _mirroredEntities.Contains(entity.Value));
        PhysicsQuaternion targetRotation = BuildPose.Rotation(targetYaw, targetMirrored);
        PartFootprint candidate = PartFootprint.ForPart(part, targetX, targetY, targetYaw, targetMirrored, targetScale);
        (float minX, float minY, float maxX, float maxY) = candidate.Bounds();
        int areaInCells = (int)MathF.Ceiling(maxX - minX) * (int)MathF.Ceiling(maxY - minY);
        if (areaInCells > _limits.MaxFootprintCells)
        {
            return Failure(ConstructionError.FootprintTooLarge);
        }

        List<uint> overlaps = CollectOverlapping(candidate, -OverlapTolerance, exclude: entity.Value, connect: false);
        uint enclosingFrame = 0;
        if (overlaps.Count > 0
            && !TryResolveEnclosure(part, owner, overlaps, self: entity.Value, out enclosingFrame))
        {
            return Failure(ConstructionError.TransformBlocked);
        }

        HashSet<uint> previousNeighbours = new(_connectionsByEntity.TryGetValue(entity.Value, out HashSet<uint>? connections)
            ? connections
            : Array.Empty<uint>());
        List<uint> candidates = CollectOverlapping(candidate, ConnectionProximity, exclude: entity.Value, connect: true);
        List<uint> neighbours = new(candidates.Count);
        foreach (uint candidateEntity in candidates)
        {
            if (IsOwnedBy(candidateEntity, owner))
            {
                neighbours.Add(candidateEntity);
            }
        }

        if (neighbours.Count > _limits.MaxConnectionsPerPart
            || neighbours.Any(neighbour => ConnectionCount(neighbour) + (previousNeighbours.Contains(neighbour) ? 0 : 1) > _limits.MaxConnectionsPerPart))
        {
            return Failure(ConstructionError.ConnectionLimitReached);
        }

        // A transform always drops the old enclosure (the part or the frame moved); it is
        // re-recorded below when the new pose still lands inside a frame.
        ReleaseEnclosureLinks(entity.Value);
        UnsetFootprint(entity.Value);
        SetFootprint(entity.Value, candidate);
        _transforms.Set(entity, new EntityTransform(
            new PhysicsVector3(targetX, targetY, current.Position.Z),
            targetRotation,
            targetScale));
        if (targetMirrored)
        {
            _mirroredEntities.Add(entity.Value);
        }
        else
        {
            _mirroredEntities.Remove(entity.Value);
        }

        Reconnect(entity.Value);
        if (enclosingFrame != 0)
        {
            _enclosedBy[entity.Value] = enclosingFrame;
            _enclosedPart[enclosingFrame] = entity.Value;
        }

        foreach (uint previous in previousNeighbours)
        {
            Reconnect(previous);
        }

        return new ConstructionResult(entity, ConstructionError.None);
    }

    public ConstructionResult Remove(EntityId entity, uint owner)
    {
        if (!_entities.IsAlive(entity))
        {
            return Failure(ConstructionError.EntityNotFound);
        }

        if (!_parts.TryGet(entity, out _))
        {
            return Failure(ConstructionError.NotAConstructionEntity);
        }

        if (!IsOwnedBy(entity.Value, owner))
        {
            return Failure(ConstructionError.NotOwnedByPlayer);
        }

        if (_frozenEntities.Contains(entity.Value))
        {
            return Failure(ConstructionError.FrozenEntity);
        }

        DetachEntity(entity.Value);
        _entities.Destroy(entity);
        return new ConstructionResult(entity, ConstructionError.None);
    }

    /// <summary>Drops construction bookkeeping for an entity without touching the entity
    /// store; used when runtime rules destroy a part behind the registry's back.
    /// Idempotent, and a no-op for entities the registry does not track.</summary>
    public void Forget(EntityId entity)
    {
        DetachEntity(entity.Value);
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
        _enclosedBy.Clear();
        _enclosedPart.Clear();
        _ownerByEntity.Clear();
        _partCountByOwner.Clear();
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

    /// <summary>Destroys one owner's construction entities in ascending entity order,
    /// leaving every other owner's footprint, connections and transforms untouched.</summary>
    public List<uint> ResetOwned(uint owner)
    {
        List<uint> destroyed = new();
        foreach ((uint entityValue, uint entityOwner) in _ownerByEntity)
        {
            if (entityOwner == owner)
            {
                destroyed.Add(entityValue);
            }
        }

        destroyed.Sort();
        foreach (uint entityValue in destroyed)
        {
            EntityId entity = new(entityValue);
            bool alive = _entities.IsAlive(entity);
            DetachEntity(entityValue);
            if (alive)
            {
                _entities.Destroy(entity);
            }
        }

        return destroyed;
    }

    /// <summary>Stable hash over every construction entity's part type, pose, scale, and connections.</summary>
    public long ComputeLayoutHash() => ComputeLayoutHashCore(owner: null);

    /// <summary>Stable hash over one owner's part types, poses, scales, and connections.</summary>
    public long ComputeLayoutHash(uint owner) => ComputeLayoutHashCore(owner);

    private long ComputeLayoutHashCore(uint? owner)
    {
        long hash = 17;
        foreach (uint entityValue in _footprintByEntity.Keys.OrderBy(value => value))
        {
            if (owner.HasValue && !IsOwnedBy(entityValue, owner.Value))
            {
                continue;
            }

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
                // The pose quaternion already differs under the mirror, but the bit is the state's
                // own representation of it and must hash even if a pose is ever normalised.
                hash = unchecked(hash * 31 + (_mirroredEntities.Contains(entityValue) ? 1 : 0));
            }

            IEnumerable<uint> orderedConnections = _connectionsByEntity.TryGetValue(entityValue, out HashSet<uint>? connections)
                ? connections.OrderBy(value => value)
                : Array.Empty<uint>();
            foreach (uint connection in orderedConnections)
            {
                hash = unchecked(hash * 31 + connection);
            }

            // The enclosure relation is part of the build state: a frame's slot being taken
            // changes the assembly, so it must change the layout hash too.
            uint enclosedByFrame = _enclosedBy.TryGetValue(entityValue, out uint enclosingFrame) ? enclosingFrame : 0;
            if (enclosedByFrame != 0)
            {
                hash = unchecked(hash * 31 + enclosedByFrame);
            }
        }

        return hash;
    }

    /// <summary>
    /// Resolves a footprint that overlaps existing parts to the frame that will enclose the
    /// candidate, or rejects it. Original rules: only a frame encloses (Frame.cs:32 /
    /// BasePart.cs:1143), the candidate must be enclosable (BasePart.cs:1148-1166), a frame
    /// holds one part at a time and never a same-type second one (Contraption.cs:1729-1749,
    /// ConstructionUI.cs:1212). Anything else — a pig against a pig, a free overlap with a
    /// non-frame, a frame against a frame — stays a plain occupancy conflict.
    /// <paramref name="self"/> is the entity being transformed: it may keep its own frame slot.
    /// </summary>
    private bool TryResolveEnclosure(PartDefinition candidate, uint owner, List<uint> overlaps, uint self, out uint frame)
    {
        frame = 0;
        if (candidate.Capabilities?.CanEnclose == true)
        {
            return false;
        }

        overlaps.Sort();
        foreach (uint candidateValue in overlaps)
        {
            if (!_parts.TryGet(new EntityId(candidateValue), out PartLink link)
                || !IsOwnedBy(candidateValue, owner))
            {
                return false;
            }

            if (_content.GetPart(link.PartTypeId).Capabilities?.CanEnclose != true)
            {
                return false;
            }

            // A frozen frame is kept wreckage (issue #7): it still occupies its cells, but it is
            // not editable, so it cannot take on a new enclosed part either.
            if (_frozenEntities.Contains(candidateValue))
            {
                return false;
            }

            if (_enclosedPart.TryGetValue(candidateValue, out uint held))
            {
                if (held == self)
                {
                    continue;
                }

                return false;
            }

            if (frame == 0)
            {
                frame = candidateValue;
            }
        }

        return frame != 0;
    }

    /// <summary>Drops an entity's enclosure in both directions: the slot it sits in, and the
    /// slot it hosts when it is a frame. Idempotent.</summary>
    private void ReleaseEnclosureLinks(uint entityValue)
    {
        if (_enclosedBy.Remove(entityValue, out uint frame))
        {
            _enclosedPart.Remove(frame);
        }

        if (_enclosedPart.Remove(entityValue, out uint heldPart))
        {
            _enclosedBy.Remove(heldPart);
        }
    }

    private void Reconnect(uint entityValue)
    {
        if (!_footprintByEntity.TryGetValue(entityValue, out PartFootprint footprint)
            || !_ownerByEntity.TryGetValue(entityValue, out uint owner))
        {
            return;
        }

        List<uint> candidates = CollectOverlapping(footprint, ConnectionProximity, entityValue, connect: true);
        List<uint> neighbours = new(candidates.Count);
        foreach (uint candidate in candidates)
        {
            if (IsOwnedBy(candidate, owner))
            {
                neighbours.Add(candidate);
            }
        }

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

    private bool IsOwnedBy(uint entityValue, uint owner) =>
        _ownerByEntity.TryGetValue(entityValue, out uint entityOwner) && entityOwner == owner;

    private int OwnerPartCount(uint owner) =>
        _partCountByOwner.TryGetValue(owner, out int count) ? count : 0;

    /// <summary>Removes construction bookkeeping only; the entity store is never touched here.</summary>
    private void DetachEntity(uint entityValue)
    {
        ReleaseEnclosureLinks(entityValue);
        UnsetFootprint(entityValue);
        UnlinkAll(entityValue);
        _connectionsByEntity.Remove(entityValue);
        _frozenEntities.Remove(entityValue);
        _mirroredEntities.Remove(entityValue);
        if (_ownerByEntity.Remove(entityValue, out uint owner)
            && _partCountByOwner.TryGetValue(owner, out int count))
        {
            if (count <= 1)
            {
                _partCountByOwner.Remove(owner);
            }
            else
            {
                _partCountByOwner[owner] = count - 1;
            }
        }

        EntityId entity = new(entityValue);
        _parts.Remove(entity);
        _transforms.Remove(entity);
    }

    /// <summary>
    /// Entities whose footprint is within <paramref name="margin"/> of <paramref name="footprint"/>.
    /// <paramref name="connect"/> picks the connection semantic: it includes a part's conditional
    /// attachment brackets, while occupancy (<c>false</c>) ignores them.
    /// </summary>
    private List<uint> CollectOverlapping(in PartFootprint footprint, float margin, uint exclude, bool connect)
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

                    if (connect ? other.Touches(footprint, margin) : other.Overlaps(footprint, margin))
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

    private static ConstructionResult Failure(ConstructionError error) => new(default, error);

    private static long PackBucket(int x, int y) => ((long)(uint)x << 32) | (uint)y;
}
