using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Construction;

/// <summary>One part inside a merged compound, posed in the body's centre-of-mass frame.</summary>
public readonly record struct CompoundMember(
    EntityId Entity,
    uint PartTypeId,
    PhysicsVector3 LocalOffset,
    PhysicsQuaternion LocalRotation,
    float Scale,
    float Mass);

/// <summary>
/// Preset weld between two members. Splitting is instantaneous per ADR-002:
/// <c>break = impactImpulse &gt; BreakImpulse</c>, no accumulated damage.
/// </summary>
public readonly record struct CompoundSeam(
    EntityId Left,
    EntityId Right,
    PhysicsVector3 LocalMidpoint,
    float BreakImpulse);

/// <summary>
/// Revolute attachment: the wheel keeps its own body and hinges to the parent entity's
/// body at the wheel's axle, so it can spin instead of skidding with the chassis.
/// </summary>
public readonly record struct CompoundHinge(EntityId Wheel, EntityId Parent);

/// <summary>
/// A connected group of dynamic parts welded into one rigid body. Singletons
/// (no welds) are also represented so callers can spawn every part through one path.
/// </summary>
public sealed class CompoundCluster
{
    public CompoundCluster(
        PhysicsVector3 worldPosition,
        PhysicsQuaternion worldRotation,
        float mass,
        IReadOnlyList<CompoundMember> members,
        IReadOnlyList<CompoundSeam> seams)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(seams);
        if (members.Count == 0)
        {
            throw new ArgumentException("A compound cluster requires at least one member.", nameof(members));
        }

        WorldPosition = worldPosition;
        WorldRotation = worldRotation;
        Mass = mass;
        Members = members;
        Seams = seams;
    }

    public PhysicsVector3 WorldPosition { get; set; }

    public PhysicsQuaternion WorldRotation { get; set; }

    public float Mass { get; }

    public IReadOnlyList<CompoundMember> Members { get; }

    public IReadOnlyList<CompoundSeam> Seams { get; }

    public bool IsMerged => Members.Count > 1;

    public PhysicsVector3 WorldPositionOf(CompoundMember member) =>
        WorldPosition + WorldRotation.Rotate(member.LocalOffset);

    public PhysicsQuaternion WorldRotationOf(CompoundMember member) =>
        WorldRotation * member.LocalRotation;

    public PhysicsVector3 WorldMidpoint(CompoundSeam seam) =>
        WorldPosition + WorldRotation.Rotate(seam.LocalMidpoint);

    public BodyDefinition CreateBodyDefinition(
        PartContentLibrary content,
        PhysicsVector3 linearVelocity = default,
        PhysicsVector3 angularVelocity = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Members.Count == 1)
        {
            CompoundMember single = Members[0];
            PartContentLibrary.ShapePlacement[] placements = content.EnumerateShapePlacements(single.PartTypeId, single.Scale);
            if (placements.Length == 1
                && placements[0].Offset == PhysicsVector3.Zero
                && single.LocalOffset == PhysicsVector3.Zero
                && single.LocalRotation == PhysicsQuaternion.Identity)
            {
                // A single centred shape keeps the cheap primitive body (static-friendly).
                return content.CreateBodyDefinition(
                    single.PartTypeId,
                    WorldPosition,
                    WorldRotation,
                    single.Scale,
                    linearVelocity,
                    angularVelocity);
            }
        }

        List<CompoundChild> children = new(Members.Count);
        for (int index = 0; index < Members.Count; index++)
        {
            CompoundMember member = Members[index];
            foreach (PartContentLibrary.ShapePlacement placement in content.EnumerateShapePlacements(member.PartTypeId, member.Scale))
            {
                PhysicsVector3 offset = member.LocalOffset + member.LocalRotation.Rotate(placement.Offset);
                children.Add(new CompoundChild(placement.Shape, offset, member.LocalRotation));
            }
        }

        PartDefinition first = content.GetPart(Members[0].PartTypeId);
        if (first.Mode != PhysicsBodyMode.Dynamic)
        {
            throw new NotSupportedException($"Part type {first.PartTypeId} is static and uses offset or multi-shape colliders, which the physics contract does not support yet.");
        }

        return new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            WorldPosition,
            WorldRotation,
            Mass,
            new ShapeDefinition[] { new CompoundShapeDefinition(children) },
            linearVelocity,
            angularVelocity,
            new PhysicsMaterial(first.Restitution, first.Friction));
    }

    /// <summary>Stable hash over member identity, local poses, and remaining seams.</summary>
    public long ComputeHash()
    {
        long hash = 17;
        hash = unchecked((hash * 31) + Members.Count);
        foreach (CompoundMember member in Members)
        {
            hash = unchecked(hash * 31 + (int)member.Entity.Value);
            hash = unchecked(hash * 31 + (int)member.PartTypeId);
            hash = unchecked(hash * 31 + member.LocalOffset.GetHashCode());
            hash = unchecked(hash * 31 + member.LocalRotation.GetHashCode());
            hash = unchecked(hash * 31 + member.Scale.GetHashCode());
        }

        foreach (CompoundSeam seam in Seams)
        {
            hash = unchecked(hash * 31 + (int)seam.Left.Value);
            hash = unchecked(hash * 31 + (int)seam.Right.Value);
            hash = unchecked(hash * 31 + seam.BreakImpulse.GetHashCode());
        }

        return hash;
    }
}

/// <summary>
/// Builds compound clusters from construction connections and splits them along
/// preset seams. Pure rules: no Unity or physics-native types.
/// </summary>
public static class CompoundAssembler
{
    public const float DefaultSeamBreakImpulse = 10f;

    public static List<CompoundCluster> Assemble(
        IReadOnlyList<EntityId> entities,
        ConstructionRules construction,
        PartContentLibrary content,
        float seamBreakImpulse = DefaultSeamBreakImpulse)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(construction);
        ArgumentNullException.ThrowIfNull(content);
        if (!float.IsFinite(seamBreakImpulse) || seamBreakImpulse < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seamBreakImpulse), seamBreakImpulse, "Seam break impulse must be finite and non-negative.");
        }

        Dictionary<uint, uint> parent = new(entities.Count);
        Dictionary<uint, EntityId> byValue = new(entities.Count);
        foreach (EntityId entity in entities)
        {
            parent[entity.Value] = entity.Value;
            byValue[entity.Value] = entity;
        }

        uint Find(uint value)
        {
            uint root = value;
            while (parent[root] != root)
            {
                root = parent[root];
            }

            uint cursor = value;
            while (cursor != root)
            {
                uint next = parent[cursor];
                parent[cursor] = root;
                cursor = next;
            }

            return root;
        }

        void Union(uint left, uint right)
        {
            uint rootLeft = Find(left);
            uint rootRight = Find(right);
            if (rootLeft == rootRight)
            {
                return;
            }

            if (rootLeft < rootRight)
            {
                parent[rootRight] = rootLeft;
            }
            else
            {
                parent[rootLeft] = rootRight;
            }
        }

        foreach (EntityId entity in entities)
        {
            if (!CanMerge(entity, construction, content))
            {
                continue;
            }

            foreach (uint neighbour in construction.ConnectionsOf(entity))
            {
                if (!byValue.TryGetValue(neighbour, out EntityId neighbourEntity)
                    || !CanMerge(neighbourEntity, construction, content))
                {
                    continue;
                }

                Union(entity.Value, neighbour);
            }
        }

        Dictionary<uint, List<EntityId>> groups = new();
        foreach (EntityId entity in entities)
        {
            uint root = Find(entity.Value);
            if (!groups.TryGetValue(root, out List<EntityId>? group))
            {
                group = new List<EntityId>();
                groups.Add(root, group);
            }

            group.Add(entity);
        }

        List<CompoundCluster> clusters = new(groups.Count);
        foreach (List<EntityId> group in groups.Values.OrderBy(value => value.Min(entity => entity.Value)))
        {
            group.Sort((left, right) => left.Value.CompareTo(right.Value));
            clusters.Add(BuildCluster(group, construction, content, seamBreakImpulse));
        }

        return clusters;
    }

    public static CompoundSeam? NearestSeam(CompoundCluster cluster, PhysicsVector3 worldPoint)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if (cluster.Seams.Count == 0)
        {
            return null;
        }

        CompoundSeam? best = null;
        float bestDistance = float.PositiveInfinity;
        foreach (CompoundSeam seam in cluster.Seams)
        {
            float distance = PhysicsVector3.Distance(cluster.WorldMidpoint(seam), worldPoint);
            if (distance < bestDistance
                || (distance == bestDistance
                    && best is CompoundSeam current
                    && (seam.Left.Value < current.Left.Value
                        || (seam.Left.Value == current.Left.Value && seam.Right.Value < current.Right.Value))))
            {
                bestDistance = distance;
                best = seam;
            }
        }

        return best;
    }

    /// <summary>
    /// Removes one seam. If the remaining graph disconnects, returns two clusters
    /// rebuilt at the members' current world poses; otherwise one cluster with the
    /// seam dropped.
    /// </summary>
    public static IReadOnlyList<CompoundCluster> SplitAlongSeam(CompoundCluster cluster, CompoundSeam seam)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        bool found = false;
        List<CompoundSeam> remaining = new(cluster.Seams.Count);
        foreach (CompoundSeam candidate in cluster.Seams)
        {
            if (SameSeam(candidate, seam))
            {
                found = true;
                continue;
            }

            remaining.Add(candidate);
        }

        if (!found)
        {
            throw new ArgumentException("The seam does not belong to this cluster.", nameof(seam));
        }

        Dictionary<uint, List<uint>> adjacency = new();
        foreach (CompoundMember member in cluster.Members)
        {
            adjacency[member.Entity.Value] = new List<uint>();
        }

        foreach (CompoundSeam leftover in remaining)
        {
            adjacency[leftover.Left.Value].Add(leftover.Right.Value);
            adjacency[leftover.Right.Value].Add(leftover.Left.Value);
        }

        HashSet<uint> firstComponent = Walk(cluster.Members[0].Entity.Value, adjacency);
        if (firstComponent.Count == cluster.Members.Count)
        {
            return new[] { Rebuild(cluster.Members, remaining, cluster) };
        }

        List<CompoundMember> first = new();
        List<CompoundMember> second = new();
        foreach (CompoundMember member in cluster.Members)
        {
            if (firstComponent.Contains(member.Entity.Value))
            {
                first.Add(member);
            }
            else
            {
                second.Add(member);
            }
        }

        List<CompoundSeam> firstSeams = new();
        List<CompoundSeam> secondSeams = new();
        foreach (CompoundSeam leftover in remaining)
        {
            if (firstComponent.Contains(leftover.Left.Value))
            {
                firstSeams.Add(leftover);
            }
            else
            {
                secondSeams.Add(leftover);
            }
        }

        CompoundCluster left = Rebuild(first, firstSeams, cluster);
        CompoundCluster right = Rebuild(second, secondSeams, cluster);
        return left.Members[0].Entity.Value < right.Members[0].Entity.Value
            ? new[] { left, right }
            : new[] { right, left };
    }

    private static CompoundCluster Rebuild(
        IReadOnlyList<CompoundMember> members,
        IReadOnlyList<CompoundSeam> seams,
        CompoundCluster source)
    {
        float mass = 0f;
        PhysicsVector3 weighted = PhysicsVector3.Zero;
        foreach (CompoundMember member in members)
        {
            PhysicsVector3 world = source.WorldPositionOf(member);
            mass += member.Mass;
            weighted += world * member.Mass;
        }

        PhysicsVector3 com = mass > 0f ? weighted * (1f / mass) : source.WorldPositionOf(members[0]);
        CompoundMember[] relocated = new CompoundMember[members.Count];
        Dictionary<uint, PhysicsVector3> worldByEntity = new();
        for (int index = 0; index < members.Count; index++)
        {
            CompoundMember member = members[index];
            PhysicsVector3 world = source.WorldPositionOf(member);
            worldByEntity[member.Entity.Value] = world;
            relocated[index] = member with
            {
                LocalOffset = world - com,
                LocalRotation = source.WorldRotationOf(member)
            };
        }

        CompoundSeam[] relocatedSeams = new CompoundSeam[seams.Count];
        for (int index = 0; index < seams.Count; index++)
        {
            CompoundSeam seam = seams[index];
            PhysicsVector3 midpoint = (worldByEntity[seam.Left.Value] + worldByEntity[seam.Right.Value]) * 0.5f;
            relocatedSeams[index] = seam with { LocalMidpoint = midpoint - com };
        }

        return new CompoundCluster(com, PhysicsQuaternion.Identity, mass, relocated, relocatedSeams);
    }

    private static HashSet<uint> Walk(uint start, Dictionary<uint, List<uint>> adjacency)
    {
        HashSet<uint> seen = new();
        Stack<uint> stack = new();
        stack.Push(start);
        while (stack.Count > 0)
        {
            uint current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (uint next in adjacency[current])
            {
                stack.Push(next);
            }
        }

        return seen;
    }

    private static bool SameSeam(CompoundSeam left, CompoundSeam right) =>
        left.Left == right.Left && left.Right == right.Right;

    private static bool CanMerge(EntityId entity, ConstructionRules construction, PartContentLibrary content)
    {
        if (!construction.TryGetPartTypeId(entity, out uint partTypeId))
        {
            return false;
        }

        PartDefinition part = content.GetPart(partTypeId);
        if (part.Mode != PhysicsBodyMode.Dynamic)
        {
            return false;
        }

        // Wheels attach through a revolute joint instead of welding, so their body can
        // spin about its axle (see CollectHinges).
        if (part.Capabilities?.IsWheel == true)
        {
            return false;
        }

        // Welding must cover every kind the physics backends can build a compound child
        // from (Bepu: box + sphere). Anything else stays a singleton so body creation
        // never fails on an unsupported child.
        for (int index = 0; index < part.Shapes.Count; index++)
        {
            PhysicsShapeKind kind = part.Shapes[index].Kind;
            if (kind is not (PhysicsShapeKind.Box or PhysicsShapeKind.Sphere))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Revolute attachments for wheel parts: each wheel keeps its own body and hinges to
    /// one neighbour (the lowest-id non-wheel neighbour, else the lowest-id neighbour).
    /// </summary>
    public static List<CompoundHinge> CollectHinges(
        IReadOnlyList<EntityId> entities,
        ConstructionRules construction,
        PartContentLibrary content)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(construction);
        ArgumentNullException.ThrowIfNull(content);
        Dictionary<uint, EntityId> byValue = new(entities.Count);
        foreach (EntityId entity in entities)
        {
            byValue[entity.Value] = entity;
        }

        List<CompoundHinge> hinges = new();
        foreach (EntityId entity in entities)
        {
            if (!IsHingePart(entity, construction, content))
            {
                continue;
            }

            uint? fallback = null;
            uint? parent = null;
            foreach (uint neighbour in construction.ConnectionsOf(entity))
            {
                if (!byValue.ContainsKey(neighbour))
                {
                    continue;
                }

                if (IsHingePart(new EntityId(neighbour), construction, content))
                {
                    if (fallback is null || neighbour < fallback)
                    {
                        fallback = neighbour;
                    }

                    continue;
                }

                if (parent is null || neighbour < parent)
                {
                    parent = neighbour;
                }
            }

            uint? target = parent ?? fallback;
            if (target is uint parentValue)
            {
                hinges.Add(new CompoundHinge(entity, new EntityId(parentValue)));
            }
        }

        hinges.Sort((left, right) => left.Wheel.Value.CompareTo(right.Wheel.Value));
        return hinges;
    }

    private static bool IsHingePart(EntityId entity, ConstructionRules construction, PartContentLibrary content) =>
        construction.TryGetPartTypeId(entity, out uint partTypeId)
        && content.GetPart(partTypeId).Capabilities?.IsWheel == true;

    private static CompoundCluster BuildCluster(
        List<EntityId> group,
        ConstructionRules construction,
        PartContentLibrary content,
        float seamBreakImpulse)
    {
        CompoundMember[] members = new CompoundMember[group.Count];
        float mass = 0f;
        float shapeVolume = 0f;
        PhysicsVector3 volumeWeighted = PhysicsVector3.Zero;
        PhysicsQuaternion bodyRotation = group.Count == 1
            ? (construction.TryGetTransform(group[0], out EntityTransform singletonPose) ? singletonPose.Rotation : PhysicsQuaternion.Identity)
            : PhysicsQuaternion.Identity;
        for (int index = 0; index < group.Count; index++)
        {
            EntityId entity = group[index];
            construction.TryGetPartTypeId(entity, out uint partTypeId);
            construction.TryGetTransform(entity, out EntityTransform transform);
            PartDefinition part = content.GetPart(partTypeId);
            float memberMass = part.Mass * (transform.Scale * transform.Scale * transform.Scale);
            mass += memberMass;
            // The physics backend recentres compound children onto their volume-weighted
            // centre, so the body pose must be that centre for the shapes to land where
            // the content places them.
            foreach (PartContentLibrary.ShapePlacement placement in content.EnumerateShapePlacements(partTypeId, transform.Scale))
            {
                float volume = ShapeMetrics.Volume(placement.Shape);
                shapeVolume += volume;
                volumeWeighted += (transform.Position + transform.Rotation.Rotate(placement.Offset)) * volume;
            }

            members[index] = new CompoundMember(
                entity,
                partTypeId,
                PhysicsVector3.Zero,
                transform.Rotation,
                transform.Scale,
                memberMass);
        }

        PhysicsVector3 com = shapeVolume > 0f ? volumeWeighted * (1f / shapeVolume) : members[0].LocalOffset;

        for (int index = 0; index < members.Length; index++)
        {
            construction.TryGetTransform(members[index].Entity, out EntityTransform transform);
            members[index] = members[index] with
            {
                LocalOffset = bodyRotation.Inverse.Rotate(transform.Position - com),
                LocalRotation = bodyRotation.Inverse * transform.Rotation,
            };
        }

        List<CompoundSeam> seams = new();
        if (group.Count > 1)
        {
            HashSet<long> seen = new();
            foreach (CompoundMember member in members)
            {
                foreach (uint neighbour in construction.ConnectionsOf(member.Entity))
                {
                    if (!group.Exists(candidate => candidate.Value == neighbour))
                    {
                        continue;
                    }

                    uint left = Math.Min(member.Entity.Value, neighbour);
                    uint right = Math.Max(member.Entity.Value, neighbour);
                    long key = ((long)left << 32) | right;
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    construction.TryGetTransform(new EntityId(left), out EntityTransform leftTransform);
                    construction.TryGetTransform(new EntityId(right), out EntityTransform rightTransform);
                    PhysicsVector3 midpoint = (leftTransform.Position + rightTransform.Position) * 0.5f;
                    seams.Add(new CompoundSeam(new EntityId(left), new EntityId(right), midpoint - com, seamBreakImpulse));
                }
            }

            seams.Sort((left, right) =>
            {
                int compare = left.Left.Value.CompareTo(right.Left.Value);
                return compare != 0 ? compare : left.Right.Value.CompareTo(right.Right.Value);
            });
        }

        return new CompoundCluster(com, bodyRotation, mass, members, seams);
    }
}
