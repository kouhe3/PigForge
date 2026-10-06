using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using Xunit.Sdk;

namespace PigForge.Core.Tests;

/// <summary>
/// Build-grid cell occupancy over the real repository content (ADR-020): a part occupies the cells
/// its prefab's <c>m_gridXmin..m_gridYmax</c> declares (<c>tools/bple-grid</c>), resolved to the
/// grid coordinate it stands on, and its colliders overhang neighbouring cells freely. That is the
/// original's rule (<c>ConstructionUI.cs:1279,1344</c>; <c>BasePart.cs:197-200</c>) and the reason
/// an overhanging part -- a rotor's 2.55-wide blades, a glider wing -- can stand beside a wooden
/// frame at all, which the old collider-union occupancy rejected with <c>CellsOccupied</c>.
/// </summary>
public sealed class CellOccupancyTests
{
    private const uint PartFrame = 1; // wooden-block: canEnclose, one-cell collider
    private const uint PartPig = 4; // sphere r 0.42, jointConnectionType none
    private const uint PartKingPig = 24; // the 3x2 cell box x[-1, 1] y[0, 1]
    private const uint PartGlider = 31; // wooden glider wing: 1.9-wide collider, 1-cell box
    private const uint PartRotor = 37; // 2.55-wide blades, 1-cell box

    [Fact]
    public void RotorBladesOverhangTheFramesCellWithoutOccupyingIt()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        ConstructionResult frame = rules.Place(PartFrame, -11f, 10f, 0f, 1f, 0);
        ConstructionResult pig = rules.Place(PartPig, -11f, 10f, 0f, 1f, 0);
        ConstructionResult rotor = rules.Place(PartRotor, -10f, 10f, 0f, 1f, 0);

        // The user-visible bug: the second of these was rejected with error 6 (`CellsOccupied`)
        // because the rotor's 2.55-wide blade collider crossed the frame's cell. The pig shares the
        // frame's cell and is enclosed by it, not rejected.
        Assert.True(frame.IsSuccess);
        Assert.True(pig.IsSuccess);
        Assert.True(rotor.IsSuccess);
        Assert.Equal(frame.Entity, rules.EnclosedBy(pig.Entity));

        // The rotor still welds to the frame -- connection proximity uses the colliders, which do
        // overlap -- so the propulsion chassis gate sees the frame.
        Assert.Contains(frame.Entity.Value, rules.ConnectionsOf(rotor.Entity));
        Assert.True(rules.HasChassisNeighbor(rotor.Entity));

        // One cluster: the rotor welds to the frame, the frame encloses the pig.
        CompoundCluster cluster = Assert.Single(
            CompoundAssembler.Assemble(new[] { frame.Entity, pig.Entity, rotor.Entity }, rules, content).Clusters);
        Assert.Equal(3, cluster.Members.Count);

        // The footprint splits the two geometries: neighbouring cells that only touch (occupancy)
        // and blades that reach across the cell line (connection).
        PartFootprint frameFootprint = PartFootprint.ForPart(content.GetPart(PartFrame), -11f, 10f, 0f, false, 1f);
        PartFootprint rotorFootprint = PartFootprint.ForPart(content.GetPart(PartRotor), -10f, 10f, 0f, false, 1f);
        // The rule layer's tolerance, because touching boxes already count as overlapping at 0.
        Assert.False(rotorFootprint.Overlaps(frameFootprint, -ConstructionRules.OverlapTolerance));
        Assert.True(rotorFootprint.Touches(frameFootprint));

        // The spatial hash still spans the blades, because it must cover both geometries.
        (float minX, _, float maxX, _) = rotorFootprint.Bounds();
        Assert.Equal(2.55f, maxX - minX, precision: 3);
    }

    [Fact]
    public void PartsInOneCellAreRejectedAtAnyDistanceBelowAHalfCell()
    {
        (ConstructionRules rules, _) = CreateRules();
        Assert.True(rules.Place(PartRotor, -10f, 10f, 0f, 1f, 0).IsSuccess);

        // 0.4 of a cell away is still the same cell -- a rotor's own cell is one cell, whatever the
        // angle, so the second placement is an occupancy conflict and not an enclosure.
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartRotor, -10.4f, 10.4f, 0f, 1f, 0).Error);
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartRotor, -10.4f, 9.6f, 0.6f, 1f, 0).Error);

        // Half a cell away is the neighbouring cell: legal, and the blades overlap freely.
        Assert.True(rules.Place(PartRotor, -10.6f, 10f, 0f, 1f, 0).IsSuccess);
    }

    [Fact]
    public void KingPigOccupiesItsThreeByTwoBox()
    {
        (ConstructionRules rules, _) = CreateRules();
        Assert.True(rules.Place(PartKingPig, 0f, 0f, 0f, 1f, 0).IsSuccess);

        // x[-1, 1] y[0, 1] around its own cell: all six cells are taken.
        foreach ((float x, float y) in new[] { (-1f, 0f), (1f, 0f), (-1f, 1f), (0f, 1f), (1f, 1f) })
        {
            Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartFrame, x, y, 0f, 1f, 0).Error);
        }

        // ...and only those six: the cells just outside the box are free.
        Assert.True(rules.Place(PartFrame, 2f, 0f, 0f, 1f, 0).IsSuccess);
        Assert.True(rules.Place(PartFrame, 0f, -1f, 0f, 1f, 0).IsSuccess);
    }

    [Fact]
    public void KingPigBoxTurnsWithThePartAndBlocksTheTurnIntoAnOccupiedCell()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult king = rules.Place(PartKingPig, 0f, 0f, 0f, 1f, 0);
        // A pig, not a frame: overlapping a free frame would enclose the king pig instead of
        // blocking the turn (TryResolveEnclosure), which is the other legal outcome.
        ConstructionResult blocker = rules.Place(PartPig, 0f, -1f, 0f, 1f, 0);

        // x[-1, 1] y[0, 1] does not reach the cell below; a quarter turn swings it to
        // x[-1.5, 0.5] y[-1.5, 1.5] and into the blocker's cell, so the turn is blocked.
        Assert.True(blocker.IsSuccess);
        Assert.Equal(ConstructionError.TransformBlocked, rules.Rotate(king.Entity, MathF.PI / 2f, 0).Error);

        // With the blocker gone the same turn lands on cells (-1..0, -1..1).
        Assert.True(rules.Remove(blocker.Entity, 0).IsSuccess);
        Assert.True(rules.Rotate(king.Entity, MathF.PI / 2f, 0).IsSuccess);
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartFrame, 0f, -1f, 0f, 1f, 0).Error);
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartFrame, -1f, 1f, 0f, 1f, 0).Error);
        Assert.True(rules.Place(PartFrame, 1f, 0f, 0f, 1f, 0).IsSuccess);
    }

    [Fact]
    public void GliderWingStillSnapsFlushAgainstAFrame()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult frame = rules.Place(PartFrame, 1f, 0f, 0f, 1f, 0);
        Assert.True(frame.IsSuccess);

        // ADR-018's measured editor snap: the wing's bracket left edge lands on the frame's right
        // edge (1.5) with the wing's origin at 1.7404, i.e. inside the next cell. Collider-union
        // occupancy rejected this pose outright (`CellsOccupied`); the wing's own cell box is the
        // one to the right of the frame, so the pose is legal and the wing welds to the frame.
        ConstructionResult wing = rules.Place(PartGlider, 1.7404f, 0f, 0f, 1f, 0);
        Assert.True(wing.IsSuccess);
        Assert.Contains(wing.Entity.Value, rules.ConnectionsOf(frame.Entity));

        // Its cell is taken: nothing else fits at 2.4.
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartFrame, 2.4f, 0f, 0f, 1f, 0).Error);

        // Dropped on the frame's own cell instead it is *rejected*: vanilla only lets the classes
        // that override CanBeEnclosed() into a frame, and a wing is not one of them
        // (BasePart.cs:1148-1165, tools/bple-joints; gaps G108), so the overlap stays a plain
        // occupancy conflict. The wide 1.9 collider still only touches -- the frame's cell test
        // sees the wing's own cell box, which the rejected pose does not change.
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartGlider, 1f, 0f, 0f, 1f, 0).Error);
    }

    [Fact]
    public void CellBoxScalesWithThePart()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        PartDefinition king = content.GetPart(PartKingPig);

        // Scale 1: the box is 3x2 cells, so a probe two cells out is free.
        PartFootprint single = PartFootprint.ForPart(king, 0f, 0f, 0f, false, 1f);
        PartFootprint probe = PartFootprint.ForPart(content.GetPart(PartFrame), 2f, 0f, 0f, false, 1f);
        Assert.False(single.Overlaps(probe, -ConstructionRules.OverlapTolerance));

        // Scale 2: the same box covers 6x4 cells (x[-3, 3] y[-1, 3]) and now reaches the probe.
        PartFootprint scaled = PartFootprint.ForPart(king, 0f, 0f, 0f, false, 2f);
        Assert.True(scaled.Overlaps(probe));

        // The rule layer sees the same box: the cell at (2, 0) is free for the unscaled pig and
        // taken by the scaled one. A pig, not a frame, so the overlap cannot be resolved as an
        // enclosure instead.
        Assert.True(rules.Place(PartPig, 2f, 0f, 0f, 1f, 0).IsSuccess);
        Assert.True(rules.Place(PartKingPig, 0f, 0f, 0f, 1f, 0).IsSuccess);
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartKingPig, 0f, 0f, 0f, 2f, 0).Error);
    }

    private static (ConstructionRules Rules, PartContentLibrary Content) CreateRules()
    {
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));
        EntityStore entities = new();
        return (new ConstructionRules(entities, new PartStore(entities), new TransformStore(entities), content), content);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new XunitException($"Could not locate {relativePath} above {AppContext.BaseDirectory}.");
        }

        return Path.Combine(directory.FullName, relativePath);
    }
}
