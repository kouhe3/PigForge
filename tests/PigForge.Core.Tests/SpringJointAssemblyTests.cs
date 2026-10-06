using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// Spring seams in the pure assembler (docs/specs/spring-joint.md §3). The original's Spring never
/// welds its neighbour: its own joint is elastic (<c>Spring.cs:100-134</c>), so a pair whose either
/// end carries the content's spring capability has to stay two bodies held by one registered
/// <see cref="CompoundSpring"/> — merging it into an ADR-011 compound would erase the elasticity
/// outright. The shape is the one ADR-024 gave frame pairs.
/// </summary>
public sealed class SpringJointAssemblyTests
{
    private const uint PartFrame = 1;
    private const uint PartTnt = 9;
    private const uint PartSpring = 12;
    private const uint PartDetacher = 43;
    private const uint PartSpringLimit = 205;

    [Fact]
    public void ASpringSeamStaysTwoBodiesHeldByOneSpring()
    {
        PartContentLibrary content = LoadContent();
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        EntityId spring = rules.Place(PartSpring, 0f, 4f, 0f, 1f, 0).Entity;
        EntityId frame = rules.Place(PartFrame, 1f, 4f, 0f, 1f, 0).Entity;

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { spring, frame }, rules, content);

        // Two bodies, not one compound, and not a frame weld either: the spring's own elastic link.
        Assert.Equal(2, assembly.Clusters.Count);
        Assert.All(assembly.Clusters, cluster => Assert.False(cluster.IsMerged));
        Assert.Empty(assembly.Welds);
        CompoundSpring seam = Assert.Single(assembly.Springs);
        Assert.Equal(spring, seam.Left);
        Assert.Equal(frame, seam.Right);

        // The original's anchor, (0, -0.5, 0) expressed in each part's own frame (Spring.cs:106).
        Assert.Equal(new PhysicsVector3(0f, -0.5f, 0f), seam.AnchorInLeft);
        Assert.Equal(new PhysicsVector3(0f, -0.5f, 0f), seam.AnchorInRight);

        // The declared content numbers, in the original's own units (extracted, never authored).
        // No route discriminator: the declaration defaults leave StableSpringConnection off, so every
        // skin is the ConfigurableJoint y soft limit (gaps G105/G107).
        Assert.Equal(250f, seam.Stiffness);
        Assert.Equal(20f, seam.Damper);
        Assert.Equal(0.1f, seam.Limit);
        Assert.Equal(1f, seam.Bounciness);
        Assert.Equal(250f, seam.BreakForce);
    }

    [Fact]
    public void ASpringSeamToAFrameIsASpringNotAWeld()
    {
        // Both rules want this pair: the frame pair rule would weld it and the spring rule would keep
        // it elastic. The spring wins, because merging it is what made the old spring bounce pad.
        PartContentLibrary content = LoadContent();
        (ConstructionRules rules, EntityId spring, EntityId metal) = PlacePair(content, PartSpring, 18);

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { spring, metal }, rules, content);

        Assert.Equal(2, assembly.Clusters.Count);
        Assert.Empty(assembly.Welds);
        Assert.Single(assembly.Springs);
    }

    [Fact]
    public void APlainPairStillMergesIntoOneCompound()
    {
        // The spring rule must not leak: two connected non-spring parts weld exactly as before.
        PartContentLibrary content = LoadContent();
        (ConstructionRules rules, EntityId left, EntityId right) = PlacePair(content, PartDetacher, PartTnt);

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { left, right }, rules, content);

        CompoundCluster cluster = Assert.Single(assembly.Clusters);
        Assert.True(cluster.IsMerged);
        Assert.Empty(assembly.Springs);
    }

    [Fact]
    public void ASpringPartWithTwoNeighboursRegistersTwoSeams()
    {
        PartContentLibrary content = LoadContent();
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        EntityId spring = rules.Place(PartSpring, 0f, 4f, 0f, 1f, 0).Entity;
        EntityId left = rules.Place(PartFrame, -1f, 4f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(PartFrame, 1f, 4f, 0f, 1f, 0).Entity;

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { spring, left, right }, rules, content);

        Assert.Equal(3, assembly.Clusters.Count);
        Assert.Equal(2, assembly.Springs.Count);
        Assert.Equal(spring, assembly.Springs[0].Left);
        Assert.Equal(left, assembly.Springs[0].Right);
        Assert.Equal(spring, assembly.Springs[1].Left);
        Assert.Equal(right, assembly.Springs[1].Right);
    }

    [Fact]
    public void TheSpringCalibrationIsTheProbeFittedRate()
    {
        // The declared SPRING_LIMIT_SPRING (250 N/m) is not what the original's PhysX delivers:
        // tasks/spring-probe.json measures 0.123418 m of sag at a 5.863 N load on the cell that
        // matches this content's own spring mass (0.6 kg), i.e. 47.50768 N/m against the declared
        // 250, and a free-oscillation damping ratio of 0.539356 where the declared 20 N*s/m would
        // be overdamped (1.87). These scales are how Bepu reproduces the measured link instead of
        // the declared numbers (docs/specs/spring-joint.md §3/§7).
        Assert.Equal(0.1900307f, CompoundAssembler.SpringEffectiveStiffnessScale);
        Assert.Equal(0.2879606f, CompoundAssembler.SpringEffectiveDampingScale);
        Assert.Equal(3f, CompoundAssembler.SpringBreakDistance);

        const float contentMass = 0.6f;
        CompoundSpring spring = new(default, default, default, default, 250f, 20f, 0.1f, 1f, 250f);
        Assert.Equal(47.5077f, CompoundAssembler.EffectiveStiffness(spring), precision: 2);
        Assert.Equal(5.7592f, CompoundAssembler.EffectiveDamping(spring), precision: 3);

        // The probe cell's own load at the calibrated rate, against its measured 0.123418 m.
        float sag = contentMass * 9.81f / CompoundAssembler.EffectiveStiffness(spring);
        Assert.InRange(sag, 0.123418f * 0.75f, 0.123418f * 1.25f);

        // Non-vacuous: the declared 250 N/m misses the ±25% band by a wide margin (0.0235 m), which
        // is the 5x-too-stiff reading the spec records.
        Assert.True(contentMass * 9.81f / 250f < 0.123418f * 0.75f);

        // And the damping non-vacuously: the declared 20 N*s/m gives the solver's zeta = 1.87,
        // where the original's own free oscillation reads 0.54.
        float zeta = 20f / (2f * MathF.Sqrt(CompoundAssembler.EffectiveStiffness(spring) * contentMass));
        Assert.True(zeta > 1.5f);
    }

    [Fact]
    public void EverySkinTakesTheOneVanillaCalibration()
    {
        PartContentLibrary content = LoadContent();
        (ConstructionRules rules, EntityId spring, EntityId frame) = PlacePair(content, PartSpringLimit, PartFrame);

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { spring, frame }, rules, content);

        CompoundSpring seam = Assert.Single(assembly.Springs);
        // The other historical skin (205) is the same configuration in the declaration defaults.
        Assert.Equal(47.5077f, CompoundAssembler.EffectiveStiffness(seam), precision: 2);
        Assert.Equal(5.7592f, CompoundAssembler.EffectiveDamping(seam), precision: 3);
    }

    [Fact]
    public void TheSpringAssemblyIsDeterministicAcrossRuns()
    {
        Assert.Equal(RunSpringAssembly(), RunSpringAssembly());
    }


    private static long RunSpringAssembly()
    {
        PartContentLibrary content = LoadContent();
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        List<EntityId> placed = new()
        {
            rules.Place(PartSpring, 0f, 4f, 0f, 1f, 0).Entity,
            rules.Place(PartFrame, 1f, 4f, 0f, 1f, 0).Entity,
            rules.Place(PartSpringLimit, 2f, 4f, 0f, 1f, 0).Entity,
        };

        CompoundAssembly assembly = CompoundAssembler.Assemble(placed, rules, content);
        long hash = 17;
        foreach (CompoundSpring spring in assembly.Springs)
        {
            hash = unchecked((hash * 31) + spring.Left.Value.GetHashCode());
            hash = unchecked((hash * 31) + spring.Right.Value.GetHashCode());
            hash = unchecked((hash * 31) + spring.AnchorInLeft.GetHashCode());
            hash = unchecked((hash * 31) + spring.AnchorInRight.GetHashCode());
            hash = unchecked((hash * 31) + spring.Stiffness.GetHashCode());
            hash = unchecked((hash * 31) + spring.BreakForce.GetHashCode());
        }

        foreach (CompoundCluster cluster in assembly.Clusters)
        {
            hash = unchecked((hash * 31) + cluster.ComputeHash());
        }

        return hash;
    }

    private static (ConstructionRules Rules, EntityId Left, EntityId Right) PlacePair(
        PartContentLibrary content,
        uint leftPart,
        uint rightPart)
    {
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        EntityId left = rules.Place(leftPart, 0f, 4f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(rightPart, 1f, 4f, 0f, 1f, 0).Entity;
        return (rules, left, right);
    }

    private static PartContentLibrary LoadContent() =>
        PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relativePath);
    }
}
