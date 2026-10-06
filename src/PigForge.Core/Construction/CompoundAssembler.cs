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
    float Mass,
    PartDamping Damping = default);

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
/// A compliant weld between two frames (the content's <c>canEnclose</c> parts). The original does
/// not merge a frame pair into one rigid body: it keeps two bodies and adds a real
/// <c>ConfigurableJoint</c> with all six degrees of freedom locked
/// (<c>Contraption.cs:1507-1546</c>), so a chain of frames bends under its own weight
/// (docs/specs/weld-compliance.md §0). PigForge reproduces that with a
/// <see cref="JointDefinition.Weld"/> between the two frame bodies instead of an ADR-011 union.
/// <para>
/// The anchors are the original's: half the vector to the other part's origin, expressed in each
/// part's own frame, so both name the same world point when the pair is built. They are stored in
/// the parts' frames because that is what the original's joint holds rigid; a room re-resolves them
/// into body frames at bind time (a frame may share its body with the parts it encloses).
/// </summary>
public readonly record struct CompoundWeld(
    EntityId Left,
    EntityId Right,
    PhysicsVector3 AnchorInLeft,
    PhysicsVector3 AnchorInRight,
    float BreakImpulse);

/// <summary>
/// A spring seam: a pair whose either end carries the content's <c>capabilities.spring</c>. The
/// original's <c>Spring</c> never welds — its own joint is elastic (<c>Spring.cs:100-134</c>) — so
/// the pair stays two bodies held by a soft, breakable distance link instead of one rigid
/// compound (docs/specs/spring-joint.md §3). The shape is the one ADR-024 gave frame pairs: keep
/// both bodies, register the joint.
/// <para>
/// The anchors are the original's <c>anchor (0, -0.5, 0)</c> expressed in each part's own frame
/// (<c>Spring.cs:106</c>). Unity auto-configures the far end to the same world point, so the
/// original's link settles at the pair's assembly spacing; PigForge uses the same local anchor on
/// both ends and takes their assembly separation as the joint's rest length, which the
/// <c>JointDefinition.Distance</c> contract needs to stay positive
/// (docs/specs/spring-joint.md §7).
/// </para>
/// <para>
/// The declaration defaults leave <c>StableSpringConnection</c> off, so every skin takes the
/// original's <c>ConfigurableJoint</c> y-soft-limit branch (the bungee <c>SpringJoint</c> arm is a
/// profile-B value, gaps G105/G107) — the seam carries no route discriminator.
/// <see cref="Stiffness"/>/<see cref="Damper"/>/<see cref="Limit"/>/<see cref="Bounciness"/>/
/// <see cref="BreakForce"/> are the declared content values in the original's own units
/// (N/m, N·s/m, m, -, N).
/// </para>
/// </summary>
public readonly record struct CompoundSpring(
    EntityId Left,
    EntityId Right,
    PhysicsVector3 AnchorInLeft,
    PhysicsVector3 AnchorInRight,
    float Stiffness,
    float Damper,
    float Limit,
    float Bounciness,
    float BreakForce);

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
    IReadOnlyList<CompoundHinge> Hinges,
    IReadOnlyList<CompoundWeld> Welds,
    IReadOnlyList<CompoundSpring> Springs);

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
        IReadOnlyList<CompoundAttachment>? attachments = null,
        PartDamping damping = default)
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
        Damping = damping;
    }

    public PhysicsVector3 WorldPosition { get; set; }

    public PhysicsQuaternion WorldRotation { get; set; }

    public float Mass { get; }

    /// <summary>
    /// The damping the merged body carries: the members' own values folded by mass
    /// (<c>sum(m * d) / sum(m)</c>), the same way the body's mass is the sum of its members'.
    /// The original gives every part its own rigidbody and therefore its own <c>Rigidbody.drag</c>
    /// (<c>tools/bple-damping</c>), which one rigid body per cluster cannot reproduce exactly; a
    /// mass-weighted mean keeps the strongest member from being averaged away by a light one. A
    /// lone member keeps its own value bit-for-bit, which is what a single-part cluster — a wheel,
    /// a balloon, a sandbag — always is. Hosted attachments contribute nothing: they are not
    /// members of this body (see <see cref="Attachments"/>), exactly as they contribute no mass.
    /// </summary>
    public PartDamping Damping { get; }

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

    /// <summary>
    /// Builds the physics body for this cluster. <paramref name="construction"/> is the build
    /// layout the body is spawned from: it resolves which conditional brackets are solid and which
    /// form a wing's box takes (see <see cref="ConnectionShapes.SpawnShapes"/>). The layout does
    /// not change while a room runs, so a body rebuilt after a split resolves the same shapes.
    /// </summary>
    public BodyDefinition CreateBodyDefinition(
        PartContentLibrary content,
        ConstructionRules construction,
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
            (PhysicsVector3 _, PartContentLibrary.WheelShape[] shapes) = content.DescribeWheel(
                wheel.PartTypeId,
                wheel.Scale,
                ConnectionShapes.SpawnShapes(wheel.Entity, construction, content));
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
                CreateBodyMaterial(content),
                constraints,
                Damping.Linear,
                Damping.Angular,
                content.MaximumAngularSpeed);
        }

        if (Members.Count == 1 && Attachments.Count == 0)
        {
            CompoundMember single = Members[0];
            PartContentLibrary.ShapePlacement[] placements = content.PlaceShapes(
                single.PartTypeId,
                single.Scale,
                ConnectionShapes.SpawnShapes(single.Entity, construction, content));
            if (placements.Length == 1
                && placements[0].Offset == PhysicsVector3.Zero
                && single.LocalOffset == PhysicsVector3.Zero
                && single.LocalRotation == PhysicsQuaternion.Identity)
            {
                // A single centred shape keeps the cheap primitive body (static-friendly). The
                // resolved list already excludes every conditional shape the layout hides, so this
                // is the same single shape the content-only body would build.
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
            foreach (PartContentLibrary.ShapePlacement placement in content.PlaceShapes(
                member.PartTypeId,
                member.Scale,
                ConnectionShapes.SpawnShapes(member.Entity, construction, content)))
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
            CreateBodyMaterial(content),
            constraints,
            Damping.Linear,
            Damping.Angular,
            content.MaximumAngularSpeed);
    }

    /// <summary>
    /// The material one merged body carries. The original gives every collider its own
    /// <c>PhysicMaterial</c> and Unity combines a contact pair by the highest-priority mode
    /// (ADR-016 decision 3), but a PigForge body has exactly one material, so the members'
    /// materials are folded with that same rule:
    /// restitution is the strongest member's (ADR-010 decision 5, the same value the rules layer
    /// aggregates per body), the combine mode is the highest priority any member carries
    /// (Average &lt; Minimum &lt; Multiply &lt; Maximum), and that mode is applied over every
    /// member's coefficient (Average = the members' mean, Multiply = their product,
    /// Minimum/Maximum = the extreme member). For a single member this is exactly its own
    /// material, so the wheel path is unchanged.
    /// <see cref="Attachments"/> are deliberately excluded: they are a hinged wheel's mounts,
    /// which ride the parent body, and the original keeps the hub on the parent's material
    /// (Contraption_PhysMat) instead of the tyre the wheel body carries (ADR-016 decision 1).
    /// </summary>
    private PhysicsMaterial CreateBodyMaterial(PartContentLibrary content)
    {
        float restitution = 0f;
        FrictionCombine combine = FrictionCombine.Average;
        float sum = 0f;
        float minimum = float.PositiveInfinity;
        float product = 1f;
        float maximum = 0f;
        for (int index = 0; index < Members.Count; index++)
        {
            PartDefinition member = content.GetPart(Members[index].PartTypeId);
            restitution = MathF.Max(restitution, member.Restitution);
            combine = (FrictionCombine)Math.Max((int)combine, (int)member.FrictionCombine);
            sum += member.Friction;
            minimum = MathF.Min(minimum, member.Friction);
            product *= member.Friction;
            maximum = MathF.Max(maximum, member.Friction);
        }

        float friction = combine switch
        {
            FrictionCombine.Average => sum / Members.Count,
            FrictionCombine.Minimum => minimum,
            FrictionCombine.Multiply => product,
            _ => maximum
        };
        return new PhysicsMaterial(restitution, friction, combine);
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
    /// The compliance of one frame-to-frame <see cref="CompoundWeld"/>, in Hz, at
    /// <see cref="FrameWeldSpringDampingRatio"/>. The original has no authored spring at all: its
    /// frames bend because a locked <c>ConfigurableJoint</c> is solved iteratively and a chain's
    /// root joint cannot converge under the whole chain's bending moment
    /// (docs/specs/weld-compliance.md §0.2). A compliant weld is PigForge's stand-in for that
    /// residual, fitted to the original's own measurement — the eight-frame chain of
    /// <c>tasks/weld-compliance-probe.json</c> (<c>chain8_ppon_gap0</c>: 1.84 m of tip drop,
    /// 10.18 deg at the worst joint, 22.66 deg of total curvature).
    /// <para>
    /// These two numbers are PigForge calibration like <see cref="DefaultSeamBreakImpulse"/> — the
    /// original defines no value to copy — and they are only as good as that one measurement: a
    /// chain's compliance in the original grows with the bending moment it carries (a longer chain
    /// sags disproportionately), while a spring's stays linear. The acceptance that pins them, and
    /// its ±25% band, live in <c>tests/PigForge.Physics.Tests/WeldComplianceTests.cs</c>.
    /// </para>
    /// </summary>
    public const float FrameWeldSpringFrequency = 20.5f;

    /// <summary>Critical damping (1.0): the original's joint has no damper either, and the fitted
    /// chain sits in the middle of its band for the whole 0.4…1.0 range, so the choice buys
    /// stability (no weld ringing in a stacked structure) at no fit cost.</summary>
    public const float FrameWeldSpringDampingRatio = 1f;

    /// <summary>
    /// The fraction of the declared <c>SPRING_LIMIT_SPRING</c> (250 N/m) the original's PhysX
    /// actually delivers on the y-soft-limit route — the only route the declaration defaults take
    /// (<c>StableSpringConnection</c> off, gaps G105/G107). The declared 250 is <b>not</b> the rate
    /// the solver applies: the original probe hangs a body of the spring part's own content mass
    /// (0.6 kg) and reads a 0.123418 m sag at a 5.863 N joint force — an effective stiffness of
    /// <b>47.50768 N/m</b> (<c>tasks/spring-probe.json</c> cell <c>limit_auto_mass0p6</c>, Unity
    /// 2021.3.45f2 with the original's own physics settings; the same configuration at the
    /// original's own 0.3 kg reads 26.198 N/m and at 1 kg 70.484 N/m, i.e. the delivered rate is
    /// load-dependent). Bepu's <c>Distance(min == max)</c> is an exact spring, so copying the
    /// declared 250 would make PigForge 5x stiffer than the original. This is a PigForge
    /// calibration like <see cref="FrameWeldSpringFrequency"/>; the residual +10.8% (0.1239 m
    /// against the original's own-mass cell 0.1118 m) is the load dependence a linear spring cannot
    /// express (docs/specs/spring-joint.md §7).
    /// </summary>
    public const float SpringEffectiveStiffnessScale = 0.1900307f;

    /// <summary>
    /// The same calibration for the damper. The probe's free-oscillation fit on that cell reads
    /// ζ = 0.539356, while the declared 20 N·s/m against the calibrated rate would be overdamped
    /// (ζ = 20 / (2 √(47.50768 · 0.6)) = 1.87, i.e. no visible release bounce at all). The
    /// delivered damper is 2 ζ √(k m) = 5.759212 N·s/m, this fraction of the declared value; the
    /// room feeds it to the shared <c>GameRoom.TrySpringResponse</c> so the frequency/damping maths
    /// stays in one place.
    /// </summary>
    public const float SpringEffectiveDampingScale = 0.2879606f;

    /// <summary>
    /// Separation of the two spring anchors past which the room tears the spring down: the
    /// original's <c>FixedUpdate</c> destroys the part's fixed joints and spawns the
    /// <c>SpringEndpoint</c> body once its two anchor points are more than 3 m apart and the
    /// contraption carries no SuperGlue (<c>Spring.cs:78-92</c>).
    /// </summary>
    public const float SpringBreakDistance = 3f;

    /// <summary>
    /// The original's spring anchor in the spring part's own frame: <c>Spring.cs:106</c> sets
    /// <c>anchor = (0, -0.5, 0)</c> on the part, and the auto-configured far end names the same
    /// world point. PigForge applies it to both ends — see <see cref="CompoundSpring"/>.
    /// </summary>
    private static readonly PhysicsVector3 SpringAnchor = new(0f, -0.5f, 0f);

    /// <summary>
    /// The strength the original's <c>Normal</c> enum resolves to under the vanilla declaration
    /// defaults (<c>InDeclarationSettingsExp.json</c>, <c>INFeature.ConnectionStrength = 1.0</c>):
    /// <c>Contraption.GetJointConnectionStrength</c> (Contraption.cs:1494-1503) reads 125 from
    /// <c>GameData.asset:101-105</c> and doubles <em>only</em> the Normal arm when the multiplier is
    /// above 1 (profile B's 2.0 — a mod value, gaps G105), so in vanilla Normal is 125, the same as
    /// Weak, while High/Extreme/HighlyExtreme are 600/900/1200. <c>AddJointToMap</c> scales every
    /// joint's breakForce by that same multiplier (Contraption.cs:2268), which in vanilla is 1, so a
    /// wooden pair breaks at 2 x 125 = 250 — the pair strength this class normalizes seams against.
    /// It is also the fallback for a part whose strength was never extracted.
    /// </summary>
    private const float NormalJointStrength = 125f;

    /// <summary>
    /// Welds the connected dynamic parts into clusters, splits them along preset seams, resolves the
    /// wheel hinges that keep wheels spinning on their own bodies, and lists the frame pairs that
    /// stay two bodies joined by a compliant weld instead of merging (docs/specs/weld-compliance.md).
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
        Dictionary<long, CompoundWeld> welds = new();
        Dictionary<long, CompoundSpring> springs = new();
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

                // The original keeps two frames as two bodies joined by a real joint, not one
                // merged compound: that joint is what lets a frame chain bend
                // (docs/specs/weld-compliance.md). Every such seam becomes a CompoundWeld the
                // room binds as a compliant weld, so it stays breakable on its own.
                if (IsFramePair(entity, neighbourEntity, construction))
                {
                    RegisterWeld(welds, entity, neighbourEntity, construction, content, seamBreakImpulse);
                    continue;
                }

                // The original's Spring never welds either: its own joint is elastic
                // (Spring.cs:100-134), so a seam whose either end carries the spring capability
                // stays two bodies held by a soft distance link (docs/specs/spring-joint.md §3).
                // Merging it would erase the elasticity outright.
                if (SpringOf(entity, construction, content) is not null
                    || SpringOf(neighbourEntity, construction, content) is not null)
                {
                    RegisterSpring(springs, entity, neighbourEntity, construction, content);
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

        List<CompoundWeld> weldList = new(welds.Values);
        weldList.Sort((left, right) =>
        {
            int compare = left.Left.Value.CompareTo(right.Left.Value);
            return compare != 0 ? compare : left.Right.Value.CompareTo(right.Right.Value);
        });

        List<CompoundSpring> springList = new(springs.Values);
        springList.Sort((left, right) =>
        {
            int compare = left.Left.Value.CompareTo(right.Left.Value);
            return compare != 0 ? compare : left.Right.Value.CompareTo(right.Right.Value);
        });

        return new CompoundAssembly(clusters, hinges, weldList, springList);
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
    /// Rebuilds a cluster without the given member entities, keeping only the seams whose both
    /// ends survive. A live compound can still list a member the room already destroyed (the
    /// cluster record is only rebuilt on a split), and splitting or rebinding such a cluster
    /// would try to respawn the dead part. Returns <c>null</c> when nothing survives.
    /// </summary>
    public static CompoundCluster? WithoutMembers(CompoundCluster cluster, IReadOnlyCollection<uint> removed)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(removed);
        if (removed.Count == 0)
        {
            return cluster;
        }

        List<CompoundMember> survivors = new(cluster.Members.Count);
        foreach (CompoundMember member in cluster.Members)
        {
            if (!removed.Contains(member.Entity.Value))
            {
                survivors.Add(member);
            }
        }

        if (survivors.Count == cluster.Members.Count)
        {
            return cluster;
        }

        if (survivors.Count == 0)
        {
            return null;
        }

        List<CompoundSeam> seams = new(cluster.Seams.Count);
        foreach (CompoundSeam candidate in cluster.Seams)
        {
            if (!removed.Contains(candidate.Left.Value) && !removed.Contains(candidate.Right.Value))
            {
                seams.Add(candidate);
            }
        }

        return Rebuild(survivors, seams, cluster);
    }

    /// <summary>
    /// Removes one seam. If the remaining graph disconnects, returns two clusters
    /// rebuilt at the members' current world poses; otherwise one cluster with the
    /// seam dropped.
    /// </summary>
    public static IReadOnlyList<CompoundCluster> SplitAlongSeam(CompoundCluster cluster, CompoundSeam seam) =>
        SplitAlongSeams(cluster, new[] { seam });

    /// <summary>
    /// Removes a set of seams and rebuilds what is left: one cluster when the graph stays
    /// connected, otherwise one cluster per connected component, ascending by their first member
    /// entity — the order <see cref="SplitAlongSeam"/> already used. A part whose every seam goes
    /// leaves the contraption this way, which is what the boxing glove's punch does to the part it
    /// hits (<c>SpringBoxingGlove.cs:224-262</c> destroys the target's FixedJoints;
    /// docs/specs/boxing-glove.md §4).
    /// </summary>
    public static IReadOnlyList<CompoundCluster> SplitAlongSeams(
        CompoundCluster cluster,
        IReadOnlyCollection<CompoundSeam> seams)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(seams);
        if (seams.Count == 0)
        {
            throw new ArgumentException("A split needs at least one seam.", nameof(seams));
        }

        HashSet<long> doomed = new(seams.Count);
        foreach (CompoundSeam seam in seams)
        {
            if (!cluster.Seams.Any(candidate => SameSeam(candidate, seam)))
            {
                throw new ArgumentException("The seam does not belong to this cluster.", nameof(seams));
            }

            doomed.Add(SeamKey(seam));
        }

        List<CompoundSeam> remaining = new(cluster.Seams.Count);
        foreach (CompoundSeam candidate in cluster.Seams)
        {
            if (!doomed.Contains(SeamKey(candidate)))
            {
                remaining.Add(candidate);
            }
        }

        Dictionary<uint, List<uint>> adjacency = new(cluster.Members.Count);
        foreach (CompoundMember member in cluster.Members)
        {
            adjacency[member.Entity.Value] = new List<uint>();
        }

        foreach (CompoundSeam leftover in remaining)
        {
            adjacency[leftover.Left.Value].Add(leftover.Right.Value);
            adjacency[leftover.Right.Value].Add(leftover.Left.Value);
        }

        // Components are discovered walking the members in their own order, so the partition never
        // depends on a hash order; the first component is always the one holding Members[0].
        Dictionary<uint, int> componentOf = new(cluster.Members.Count);
        int componentCount = 0;
        foreach (CompoundMember member in cluster.Members)
        {
            if (componentOf.ContainsKey(member.Entity.Value))
            {
                continue;
            }

            Stack<uint> stack = new();
            stack.Push(member.Entity.Value);
            componentOf[member.Entity.Value] = componentCount;
            while (stack.Count > 0)
            {
                uint current = stack.Pop();
                foreach (uint next in adjacency[current])
                {
                    if (componentOf.ContainsKey(next))
                    {
                        continue;
                    }

                    componentOf[next] = componentCount;
                    stack.Push(next);
                }
            }

            componentCount++;
        }

        if (componentCount == 1)
        {
            return new[] { Rebuild(cluster.Members, remaining, cluster) };
        }

        List<CompoundMember>[] members = new List<CompoundMember>[componentCount];
        List<CompoundSeam>[] componentSeams = new List<CompoundSeam>[componentCount];
        for (int index = 0; index < componentCount; index++)
        {
            members[index] = new List<CompoundMember>();
            componentSeams[index] = new List<CompoundSeam>();
        }

        foreach (CompoundMember member in cluster.Members)
        {
            members[componentOf[member.Entity.Value]].Add(member);
        }

        foreach (CompoundSeam leftover in remaining)
        {
            componentSeams[componentOf[leftover.Left.Value]].Add(leftover);
        }

        CompoundCluster[] pieces = new CompoundCluster[componentCount];
        for (int index = 0; index < componentCount; index++)
        {
            pieces[index] = Rebuild(members[index], componentSeams[index], cluster);
        }

        Array.Sort(pieces, static (left, right) => left.Members[0].Entity.Value.CompareTo(right.Members[0].Entity.Value));
        return pieces;
    }

    private static long SeamKey(in CompoundSeam seam) => ((long)seam.Left.Value << 32) | seam.Right.Value;

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

        return new CompoundCluster(com, PhysicsQuaternion.Identity, mass, relocated, relocatedSeams, damping: FoldDamping(relocated));
    }

    /// <summary>
    /// The damping one merged body carries: the members' own values folded by mass, so a heavy
    /// member's drag is not averaged away by a light one. A lone member keeps its own value
    /// exactly. See <see cref="CompoundCluster.Damping"/>.
    /// </summary>
    private static PartDamping FoldDamping(IReadOnlyList<CompoundMember> members)
    {
        float totalMass = 0f;
        float linear = 0f;
        float angular = 0f;
        for (int index = 0; index < members.Count; index++)
        {
            CompoundMember member = members[index];
            totalMass += member.Mass;
            linear += member.Mass * member.Damping.Linear;
            angular += member.Mass * member.Damping.Angular;
        }

        return totalMass > 0f ? new PartDamping(linear / totalMass, angular / totalMass) : default;
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
    /// Whether a weldable pair is two frames — the parts whose content sets <c>canEnclose</c>, the
    /// original's <c>Frame</c> family. Those are exactly the pairs the original joint-and-collide
    /// instead of merging, which is the seam a frame chain bends at
    /// (docs/specs/weld-compliance.md §0.2, §4.2 item 1).
    /// </summary>
    private static bool IsFramePair(EntityId left, EntityId right, ConstructionRules construction) =>
        construction.IsChassis(left) && construction.IsChassis(right);

    /// <summary>
    /// Registers the weld of one frame pair, once per pair, in ascending entity order. The anchors
    /// are the original's (<c>Contraption.AddFixedJoint</c>: half the vector to the other part's
    /// origin, in each part's own frame) and the break threshold reuses the seam's strength maths —
    /// a welded pair has no seam to break along, so the weld carries one itself.
    /// </summary>
    private static void RegisterWeld(
        Dictionary<long, CompoundWeld> welds,
        EntityId first,
        EntityId second,
        ConstructionRules construction,
        PartContentLibrary content,
        float seamBreakImpulse)
    {
        EntityId left = first.Value <= second.Value ? first : second;
        EntityId right = first.Value <= second.Value ? second : first;
        long key = ((long)left.Value << 32) | right.Value;
        if (welds.ContainsKey(key))
        {
            return;
        }

        construction.TryGetTransform(left, out EntityTransform leftTransform);
        construction.TryGetTransform(right, out EntityTransform rightTransform);
        PhysicsVector3 midpoint = (leftTransform.Position + rightTransform.Position) * 0.5f;
        float pairStrength =
            JointConnectionStrengthOf(left, construction, content)
            + JointConnectionStrengthOf(right, construction, content);
        welds.Add(key, new CompoundWeld(
            left,
            right,
            leftTransform.Rotation.Inverse.Rotate(midpoint - leftTransform.Position),
            rightTransform.Rotation.Inverse.Rotate(midpoint - rightTransform.Position),
            seamBreakImpulse * pairStrength / (2f * NormalJointStrength)));
    }

    /// <summary>
    /// The original's joint-connection strength for one part, in its own units: the enum
    /// resolved through the <c>GameData.asset:101-105</c> floats with the Normal-only doubling
    /// <c>ConnectionStrength</c> applies above 1 (Contraption.cs:1494-1503) — the vanilla
    /// declaration default is 1.0, so Normal is the same 125 as Weak. A part with no extracted
    /// strength (the three without a prefab) falls back to Normal.
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
    /// Registers one spring seam, once per pair, in ascending entity order. Both anchors are the
    /// original's <c>(0, -0.5, 0)</c> expressed in each part's own frame; the room turns their
    /// assembly separation into the distance joint's rest length (<see cref="CompoundSpring"/>).
    /// The declared numbers are the spring part's own content values (the extractor's per-skin
    /// route selects the calibration, not the numbers).
    /// </summary>
    private static void RegisterSpring(
        Dictionary<long, CompoundSpring> springs,
        EntityId first,
        EntityId second,
        ConstructionRules construction,
        PartContentLibrary content)
    {
        EntityId left = first.Value <= second.Value ? first : second;
        EntityId right = first.Value <= second.Value ? second : first;
        long key = ((long)left.Value << 32) | right.Value;
        if (springs.ContainsKey(key))
        {
            return;
        }

        PartSpring? spring = SpringOf(left, construction, content) ?? SpringOf(right, construction, content);
        if (spring is null)
        {
            return;
        }

        springs.Add(key, new CompoundSpring(
            left,
            right,
            SpringAnchor,
            SpringAnchor,
            spring.Stiffness,
            spring.Damper,
            spring.Limit,
            spring.Bounciness,
            spring.BreakForce));
    }

    /// <summary>
    /// The stiffness the solver applies for one registered spring, in N/m: the declared content
    /// value scaled by the probe calibration. This is the only place the calibration is applied;
    /// the room then converts it with the shared <c>GameRoom.TrySpringResponse</c>, so the
    /// frequency/damping-ratio maths is not duplicated (docs/specs/spring-joint.md §3).
    /// </summary>
    public static float EffectiveStiffness(in CompoundSpring spring) =>
        spring.Stiffness * SpringEffectiveStiffnessScale;

    /// <summary>The damping the solver applies for one registered spring, in N·s/m (see
    /// <see cref="SpringEffectiveDampingScale"/>).</summary>
    public static float EffectiveDamping(in CompoundSpring spring) =>
        spring.Damper * SpringEffectiveDampingScale;

    /// <summary>The spring capability of one part, or <c>null</c> when it has none.</summary>
    private static PartSpring? SpringOf(EntityId entity, ConstructionRules construction, PartContentLibrary content) =>
        construction.TryGetPartTypeId(entity, out uint partTypeId)
            ? content.GetPart(partTypeId).Capabilities?.Spring
            : null;

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
            float memberMass = content.MassOf(part, transform.Scale);
            mass += memberMass;
            // The physics backend recentres compound children onto their volume-weighted
            // centre, so the body pose must be that centre for the shapes to land where
            // the content places them.
            (PhysicsVector3 _, PartContentLibrary.WheelShape[] memberShapes) = content.DescribeWheel(
                partTypeId,
                transform.Scale,
                ConnectionShapes.SpawnShapes(entity, construction, content));
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
                memberMass,
                content.DampingOf(part));
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
                    // ConnectionStrength (Contraption.cs:1541-1543 then :2268). In the vanilla
                    // declaration defaults that multiplier is 1, so the break force is the plain sum
                    // and a wooden pair (Normal 125 + 125) is 250: the seam keeps the caller's
                    // fallback for Normal-Normal and scales by the strength ratio (wood-wood 1.0,
                    // weak-weak 1.0, wood-metal 2.9, metal-metal 4.8, timebomb-timebomb 9.6).
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

        return new CompoundCluster(com, bodyRotation, mass, members, seams, hingeAxle, attachments, FoldDamping(members));
    }
}
