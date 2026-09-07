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
/// A connected group of dynamic box parts welded into one rigid body. Singletons
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
            CompoundMember member = Members[0];
            return content.CreateBodyDefinition(
                member.PartTypeId,
                WorldPosition,
                WorldRotation,
                member.Scale,
                linearVelocity,
                angularVelocity);
        }

        CompoundChild[] children = new CompoundChild[Members.Count];
        for (int index = 0; index < Members.Count; index++)
        {
            CompoundMember member = Members[index];
            BodyDefinition leaf = content.CreateBodyDefinition(
                member.PartTypeId,
                PhysicsVector3.Zero,
                PhysicsQuaternion.Identity,
                member.Scale);
            children[index] = new CompoundChild(leaf.Shapes[0], member.LocalOffset, member.LocalRotation);
        }

        PartDefinition first = content.GetPart(Members[0].PartTypeId);
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

        for (int index = 0; index < part.Shapes.Count; index++)
        {
            if (part.Shapes[index].Kind != PhysicsShapeKind.Box)
            {
                return false;
            }
        }

        return true;
    }

    private static CompoundCluster BuildCluster(
        List<EntityId> group,
        ConstructionRules construction,
        PartContentLibrary content,
        float seamBreakImpulse)
    {
        CompoundMember[] members = new CompoundMember[group.Count];
        float mass = 0f;
        PhysicsVector3 weighted = PhysicsVector3.Zero;
        PhysicsQuaternion bodyRotation = PhysicsQuaternion.Identity;
        for (int index = 0; index < group.Count; index++)
        {
            EntityId entity = group[index];
            construction.TryGetPartTypeId(entity, out uint partTypeId);
            construction.TryGetTransform(entity, out EntityTransform transform);
            PartDefinition part = content.GetPart(partTypeId);
            float memberMass = part.Mass * (transform.Scale * transform.Scale * transform.Scale);
            mass += memberMass;
            weighted += transform.Position * memberMass;
            members[index] = new CompoundMember(
                entity,
                partTypeId,
                PhysicsVector3.Zero,
                transform.Rotation,
                transform.Scale,
                memberMass);
            if (group.Count == 1)
            {
                bodyRotation = transform.Rotation;
            }
        }

        PhysicsVector3 com = mass > 0f ? weighted * (1f / mass) : members[0].LocalOffset;
        if (group.Count == 1)
        {
            construction.TryGetTransform(group[0], out EntityTransform singleton);
            com = singleton.Position;
        }

        for (int index = 0; index < members.Length; index++)
        {
            construction.TryGetTransform(members[index].Entity, out EntityTransform transform);
            PhysicsVector3 local = group.Count == 1
                ? PhysicsVector3.Zero
                : transform.Position - com;
            PhysicsQuaternion localRotation = group.Count == 1
                ? PhysicsQuaternion.Identity
                : transform.Rotation;
            members[index] = members[index] with { LocalOffset = local, LocalRotation = localRotation };
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
