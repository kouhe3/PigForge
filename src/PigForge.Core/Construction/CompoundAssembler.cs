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
/// Revolute attachment: the wheel keeps its own body and hinges to the parent entity's body
/// at <paramref name="LocalAxle"/> (the wheel's spin centre in its own local frame, scaled),
/// so it can roll instead of skidding with the chassis.
/// </summary>
public readonly record struct CompoundHinge(EntityId Wheel, EntityId Parent, PhysicsVector3 LocalAxle);

/// <summary>
/// Shapes this body hosts that belong to no member: a hinged wheel's non-rotating colliders
/// (its support box) ride the parent body, which does not spin. The original keeps them off
/// the wheel pivot for the same reason — a rotating mount sweeps into the chassis.
/// </summary>
public readonly record struct CompoundAttachment(
    PhysicsVector3 WorldPosition,
    PhysicsQuaternion WorldRotation,
    uint PartTypeId,
    float Scale);

/// <summary>Everything <see cref="CompoundAssembler.Assemble"/> derives from one build layout.</summary>
public sealed record CompoundAssembly(
    IReadOnlyList<CompoundCluster> Clusters,
    IReadOnlyList<CompoundHinge> Hinges);

/// <summary>
/// A connected group of dynamic parts welded into one rigid body. Singletons
/// (no welds) are also represented so callers can spawn every part through one path.
public sealed class CompoundCluster
{
    public CompoundCluster(
        PhysicsVector3 worldPosition,
        PhysicsQuaternion worldRotation,
        float mass,
        IReadOnlyList<CompoundMember> members,
        IReadOnlyList<CompoundSeam> seams,
        PhysicsVector3? hingeAxle = null,
        IReadOnlyList<CompoundAttachment>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(seams);
        if (members.Count == 0)
        {
            throw new ArgumentException("A compound cluster requires at least one member.", nameof(members));
        }

        if (hingeAxle is not null && members.Count != 1)
        {
            throw new ArgumentException("A hinged wheel cluster holds exactly one part.", nameof(hingeAxle));
        }

        WorldPosition = worldPosition;
        WorldRotation = worldRotation;
        Mass = mass;
        Members = members;
        Seams = seams;
        HingeAxle = hingeAxle;
        Attachments = attachments ?? Array.Empty<CompoundAttachment>();
    }

    public PhysicsVector3 WorldPosition { get; set; }

    public PhysicsQuaternion WorldRotation { get; set; }

    public float Mass { get; }

    public IReadOnlyList<CompoundMember> Members { get; }

    public IReadOnlyList<CompoundSeam> Seams { get; }

    /// <summary>
    /// Non-null for a hinged wheel: its spin centre in its own local frame (scaled). The body
    /// is then posed on that axle and carries only the tire shapes, so the wheel rolls instead
    /// of orbiting the joint.
    /// </summary>
    public PhysicsVector3? HingeAxle { get; }

    /// <summary>Shapes this body hosts for other parts (see <see cref="CompoundAttachment"/>).</summary>
    public IReadOnlyList<CompoundAttachment> Attachments { get; }

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
        PhysicsVector3 angularVelocity = default,
        PhysicsConstraintMask constraints = PhysicsConstraintMask.None)
    {
        ArgumentNullException.ThrowIfNull(content);
        PartDefinition first = content.GetPart(Members[0].PartTypeId);
        if (HingeAxle is not null)
        {
            // A hinged wheel is a true wheel: the body sits on the axle and carries only the
            // tires, so spinning it moves nothing but the tires. Its mounts were handed to the
            // parent body (see Assemble) and are not duplicated here.
            CompoundMember wheel = Members[0];
            (PhysicsVector3 _, PartContentLibrary.WheelShape[] shapes) = content.DescribeWheel(wheel.PartTypeId, wheel.Scale);
            List<ShapeDefinition> tires = new(shapes.Length);
            foreach (PartContentLibrary.WheelShape shape in shapes)
            {
                if (shape.Spins)
                {
                    tires.Add(shape.Shape);
                }
            }

            return new BodyDefinition(
                PhysicsBodyMode.Dynamic,
                WorldPosition,
                WorldRotation,
                Mass,
                tires,
                linearVelocity,
                angularVelocity,
                new PhysicsMaterial(first.Restitution, first.Friction, first.FrictionCombine),
                constraints);
        }

        if (Members.Count == 1 && Attachments.Count == 0)
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
                    angularVelocity,
                    constraints);
            }
        }

        List<CompoundChild> children = new(Members.Count + Attachments.Count);
        for (int index = 0; index < Members.Count; index++)
        {
            CompoundMember member = Members[index];
            foreach (PartContentLibrary.ShapePlacement placement in content.EnumerateShapePlacements(member.PartTypeId, member.Scale))
            {
                PhysicsVector3 offset = member.LocalOffset + member.LocalRotation.Rotate(placement.Offset);
                children.Add(new CompoundChild(placement.Shape, offset, member.LocalRotation));
            }
        }

        foreach (CompoundAttachment attachment in Attachments)
        {
            PhysicsVector3 centre = attachment.WorldPosition - WorldPosition;
            PhysicsQuaternion rotation = WorldRotation.Inverse * attachment.WorldRotation;
            (PhysicsVector3 _, PartContentLibrary.WheelShape[] wheelShapes) = content.DescribeWheel(attachment.PartTypeId, attachment.Scale);
            foreach (PartContentLibrary.WheelShape shape in wheelShapes)
            {
                if (shape.Spins)
                {
                    continue;
                }

                children.Add(new CompoundChild(shape.Shape, centre + rotation.Rotate(shape.Offset), rotation));
            }
        }

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
            new PhysicsMaterial(first.Restitution, first.Friction, first.FrictionCombine),
            constraints);
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

    /// <summary>
    /// The strength the original's <c>Normal</c> enum resolves to under the shipped
    /// <c>INFeature.ConnectionStrength</c> of 2 (INSettingsBExp.json:208-210): the
    /// <c>Contraption.GetJointConnectionStrength</c> table (Contraption.cs:1494-1506) reads
    /// 125 from <c>GameData.asset:101-105</c> and doubles <em>only</em> the Normal arm
    /// (Contraption.cs:1500), so Normal is 250 while Weak stays 125 and High/Extreme/
    /// HighlyExtreme are 600/900/1200 unchanged. It is also the fallback for a part whose
    /// strength was never extracted.
    /// </summary>
    private const float NormalJointStrength = 250f;

    /// <summary>
    /// Welds the connected dynamic parts into clusters, splits them along preset seams, and
    /// resolves the wheel hinges that keep wheels spinning on their own bodies.
    /// </summary>
    public static CompoundAssembly Assemble(
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
                    || !CanMergePair(entity, neighbourEntity, construction, content))
                {
                    continue;
                }

                Union(entity.Value, neighbour);
            }
        }

        // An enclosed part is welded to its frame whatever its joint capability says — the
        // original adds a FixedJoint straight to the frame (Frame.cs:44-49), bypassing the
        // Contraption.cs:690 rule. One rigid body is also how the pair stops colliding with
        // itself (the physics contract has no IgnoreCollision). Ordered by EntityId so the
        // union/find roots stay deterministic.
        foreach (EntityId enclosed in entities.OrderBy(entity => entity.Value))
        {
            if (construction.EnclosedBy(enclosed) is not EntityId frame
                || !byValue.ContainsKey(frame.Value)
                || !CanMerge(enclosed, construction, content)
                || !CanMerge(frame, construction, content))
            {
                continue;
            }

            Union(enclosed.Value, frame.Value);
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

        List<CompoundHinge> hinges = CollectHinges(entities, construction, content);
        Dictionary<uint, CompoundHinge> hostedByWheel = new(hinges.Count);
        Dictionary<uint, List<CompoundHinge>> wheelsByParent = new(hinges.Count);
        foreach (CompoundHinge hinge in hinges)
        {
            // A wheel's fixed mounts move to its parent body, which does not spin (see
            // CompoundAttachment). Both ends must be dynamic: the physics contract has no
            // static compound, and a wheel welded to a static part keeps its own mounts.
            if (!construction.TryGetPartTypeId(hinge.Wheel, out uint wheelTypeId)
                || !construction.TryGetPartTypeId(hinge.Parent, out uint parentTypeId)
                || content.GetPart(wheelTypeId).Mode != PhysicsBodyMode.Dynamic
                || content.GetPart(parentTypeId).Mode != PhysicsBodyMode.Dynamic)
            {
                continue;
            }

            hostedByWheel.Add(hinge.Wheel.Value, hinge);
            if (!wheelsByParent.TryGetValue(hinge.Parent.Value, out List<CompoundHinge>? hosted))
            {
                hosted = new List<CompoundHinge>();
                wheelsByParent.Add(hinge.Parent.Value, hosted);
            }

            hosted.Add(hinge);
        }

        List<CompoundCluster> clusters = new(groups.Count);
        foreach (List<EntityId> group in groups.Values.OrderBy(value => value.Min(entity => entity.Value)))
        {
            group.Sort((left, right) => left.Value.CompareTo(right.Value));
            clusters.Add(BuildCluster(group, construction, content, seamBreakImpulse, hostedByWheel, wheelsByParent));
        }

        return new CompoundAssembly(clusters, hinges);
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
    /// Whether two adjacent parts weld. Verbatim <c>Contraption.cs:690</c> on top of
    /// <see cref="CanMerge"/>: both ends must carry a joint capability (neither
    /// <see cref="JointConnectionType.None"/>) and at least one must be
    /// <see cref="JointConnectionType.Source"/>. A pig (none) therefore welds to nothing —
    /// no special case, the data says so.
    /// </summary>
    public static bool CanMergePair(EntityId left, EntityId right, ConstructionRules construction, PartContentLibrary content)
    {
        ArgumentNullException.ThrowIfNull(construction);
        ArgumentNullException.ThrowIfNull(content);
        if (!CanMerge(left, construction, content) || !CanMerge(right, construction, content))
        {
            return false;
        }

        JointConnectionType leftType = JointConnectionTypeOf(left, construction, content);
        JointConnectionType rightType = JointConnectionTypeOf(right, construction, content);
        return leftType != JointConnectionType.None
            && rightType != JointConnectionType.None
            && (leftType == JointConnectionType.Source || rightType == JointConnectionType.Source);
    }

    private static JointConnectionType JointConnectionTypeOf(EntityId entity, ConstructionRules construction, PartContentLibrary content) =>
        construction.TryGetPartTypeId(entity, out uint partTypeId)
            ? content.GetPart(partTypeId).Capabilities?.JointConnectionType ?? JointConnectionType.None
            : JointConnectionType.None;

    /// <summary>
    /// The original's joint-connection strength for one part, in its own units: the enum
    /// resolved through the <c>GameData.asset:101-105</c> floats with the Normal-only x2 the
    /// shipped <c>ConnectionStrength</c> of 2 applies (Contraption.cs:1494-1506). A part with
    /// no extracted strength (the three without a prefab) falls back to Normal.
    /// </summary>
    private static float JointConnectionStrengthOf(EntityId entity, ConstructionRules construction, PartContentLibrary content)
    {
        if (!construction.TryGetPartTypeId(entity, out uint partTypeId))
        {
            return NormalJointStrength;
        }

        return content.GetPart(partTypeId).Capabilities?.JointConnectionStrength switch
        {
            JointConnectionStrength.Weak => 125f,
            JointConnectionStrength.High => 600f,
            JointConnectionStrength.Extreme => 900f,
            JointConnectionStrength.HighlyExtreme => 1200f,
            _ => NormalJointStrength
        };
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
                construction.TryGetPartTypeId(entity, out uint partTypeId);
                float scale = construction.TryGetTransform(entity, out EntityTransform transform) ? transform.Scale : 1f;
                hinges.Add(new CompoundHinge(entity, new EntityId(parentValue), content.DescribeWheel(partTypeId, scale).Axle));
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
        float seamBreakImpulse,
        Dictionary<uint, CompoundHinge> hostedByWheel,
        Dictionary<uint, List<CompoundHinge>> wheelsByParent)
    {
        CompoundHinge? soleHinge = group.Count == 1 && hostedByWheel.TryGetValue(group[0].Value, out CompoundHinge found)
            ? found
            : null;
        CompoundMember[] members = new CompoundMember[group.Count];
        List<CompoundAttachment> attachments = new();
        float mass = 0f;
        float shapeVolume = 0f;
        PhysicsVector3 volumeWeighted = PhysicsVector3.Zero;
        // Read only for a lone member, which is centred in its own frame (see below).
        PhysicsVector3 memberOffsetWeighted = PhysicsVector3.Zero;
        float memberShapeVolume = 0f;
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
            (PhysicsVector3 _, PartContentLibrary.WheelShape[] memberShapes) = content.DescribeWheel(partTypeId, transform.Scale);
            foreach (PartContentLibrary.WheelShape shape in memberShapes)
            {
                if (soleHinge is not null && !shape.Spins)
                {
                    // A hinged wheel spins: its fixed mounts move to the parent body below.
                    continue;
                }

                float volume = ShapeMetrics.Volume(shape.Shape);
                shapeVolume += volume;
                memberShapeVolume += volume;
                memberOffsetWeighted += shape.Offset * volume;
                volumeWeighted += (transform.Position + transform.Rotation.Rotate(shape.Offset)) * volume;
            }

            if (wheelsByParent.TryGetValue(entity.Value, out List<CompoundHinge>? hosted))
            {
                foreach (CompoundHinge hinge in hosted)
                {
                    // Hosted shapes need a dynamic host body: the physics contract has no static
                    // compound, so a wheel mounted on a static part keeps its own mounts.
                    if (hinge.Wheel == entity
                        || part.Mode != PhysicsBodyMode.Dynamic
                        || !construction.TryGetPartTypeId(hinge.Wheel, out uint wheelTypeId)
                        || !construction.TryGetTransform(hinge.Wheel, out EntityTransform wheelTransform)
                        || content.GetPart(wheelTypeId).Mode != PhysicsBodyMode.Dynamic)
                    {
                        continue;
                    }

                    attachments.Add(new CompoundAttachment(
                        wheelTransform.Position,
                        wheelTransform.Rotation,
                        wheelTypeId,
                        wheelTransform.Scale));
                    (PhysicsVector3 _, PartContentLibrary.WheelShape[] hostedShapes) = content.DescribeWheel(wheelTypeId, wheelTransform.Scale);
                    foreach (PartContentLibrary.WheelShape shape in hostedShapes)
                    {
                        if (shape.Spins)
                        {
                            continue;
                        }

                        float volume = ShapeMetrics.Volume(shape.Shape);
                        shapeVolume += volume;
                        volumeWeighted += (wheelTransform.Position + wheelTransform.Rotation.Rotate(shape.Offset)) * volume;
                    }
                }
            }

            members[index] = new CompoundMember(
                entity,
                partTypeId,
                PhysicsVector3.Zero,
                transform.Rotation,
                transform.Scale,
                memberMass);
        }

        PhysicsVector3? hingeAxle = null;
        PhysicsVector3 com;
        if (soleHinge is CompoundHinge wheelHinge)
        {
            // The wheel body sits exactly on its axle: the tire shapes are centred there by
            // construction, and using the content's axle keeps the pose free of float residue.
            construction.TryGetTransform(wheelHinge.Wheel, out EntityTransform wheelPose);
            hingeAxle = wheelHinge.LocalAxle;
            com = wheelPose.Position + wheelPose.Rotation.Rotate(wheelHinge.LocalAxle);
        }
        else if (group.Count == 1
            && attachments.Count == 0
            && memberShapeVolume > 0f
            && construction.TryGetTransform(group[0], out EntityTransform sole))
        {
            // A lone member is centred on its own shapes in its own frame. Summing its world
            // shape positions round-trips the position through multiply/divide, and the residue
            // would push the member onto the compound path — which static parts cannot use
            // (the physics contract has no static compound).
            com = sole.Position + sole.Rotation.Rotate(memberOffsetWeighted * (1f / memberShapeVolume));
        }
        else
        {
            com = shapeVolume > 0f ? volumeWeighted * (1f / shapeVolume) : members[0].LocalOffset;
        }

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
                    // The original's general path sums both ends' strengths and multiplies by
                    // ConnectionStrength (Contraption.cs:1541-1543 then :2268). That factor is a
                    // constant here, so it cancels against the Normal pair: the seam keeps the
                    // caller's fallback for Normal-Normal and scales by the strength ratio
                    // (wood-wood 1.0, wood-metal 1.7, metal-metal 2.4, timebomb-timebomb 4.8).
                    float pairStrength =
                        JointConnectionStrengthOf(new EntityId(left), construction, content)
                        + JointConnectionStrengthOf(new EntityId(right), construction, content);
                    seams.Add(new CompoundSeam(
                        new EntityId(left),
                        new EntityId(right),
                        midpoint - com,
                        seamBreakImpulse * pairStrength / (2f * NormalJointStrength)));
                }
            }

            seams.Sort((left, right) =>
            {
                int compare = left.Left.Value.CompareTo(right.Left.Value);
                return compare != 0 ? compare : left.Right.Value.CompareTo(right.Right.Value);
            });
        }

        return new CompoundCluster(com, bodyRotation, mass, members, seams, hingeAxle, attachments);
    }
}
