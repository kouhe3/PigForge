using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using Xunit.Sdk;

namespace PigForge.Core.Tests;

/// <summary>
/// The level's own build limits (schemaVersion 9, <see cref="LevelBuildLimits"/>): the original's
/// build grid (<c>LevelManager.CanPlacePartAtGridCell</c>) gating where a part may sit and its
/// per-family quota (<c>BaseGameMode.GetPartCount</c>) gating how many an owner may place.
/// </summary>
public sealed class ConstructionBuildLimitsTests
{
    private const uint PartWoodenBlock = 1;
    private const uint PartPig = 4;
    private const uint PartTnt = 9;
    private const uint PartWoodenBlockSkin = 79;

    private const uint OwnerA = 1;
    private const uint OwnerB = 2;

    // Level_05's own values: the LevelStart instance position the grid anchors on, and the
    // LevelManager.m_constructionGridRows bit masks (four columns, two rows).
    private const float Level05OriginX = 2.4379761f;
    private const float Level05OriginY = 5.4575901f;

    private static readonly int[] Level05Rows = [15, 15, 0];

    // A packed build grid puts eight corner-touching neighbours around an interior cell, well past
    // the default connection cap; the geometry enumerations do not care about connectivity.
    private static readonly ConstructionLimits GridProbeLimits = new(MaxParts: 256, MaxConnectionsPerPart: 64, MaxFootprintCells: 64);

    [Fact]
    public void Level05GridAllowsItsEightCellsAndRejectsEverythingElse()
    {
        // The real grid geometry with the quota relaxed, so all eight cells can be probed; the real
        // Level_05 allowance is covered by Level05RealAllowanceCapsPigAndWoodenBlock.
        ConstructionRules rules = CreateRules(Limits(Level05OriginX, Level05OriginY, Level05Rows, (PartWoodenBlock, 64)), GridProbeLimits);

        // Rows[15, 15, 0] -> columns 4, height 2, xMin -1, xMax 2: local x in {-1..2}, local y in
        // {0, 1}, and a cell's own point is the anchor plus its offsets.
        for (int x = -1; x <= 2; x++)
        {
            for (int y = 0; y <= 1; y++)
            {
                ConstructionResult placed = rules.Place(
                    PartWoodenBlock,
                    Level05OriginX + x,
                    Level05OriginY + y,
                    0f,
                    1f,
                    OwnerA);
                Assert.True(placed.IsSuccess, $"cell ({x}, {y}): {placed.Error}");
            }
        }

        Assert.Equal(8, rules.PartCount);

        // One cell left, one right, one below and one above the grid.
        Assert.Equal(
            ConstructionError.OutsideConstructionGrid,
            rules.Place(PartWoodenBlock, Level05OriginX - 2f, Level05OriginY, 0f, 1f, OwnerA).Error);
        Assert.Equal(
            ConstructionError.OutsideConstructionGrid,
            rules.Place(PartWoodenBlock, Level05OriginX + 3f, Level05OriginY, 0f, 1f, OwnerA).Error);
        Assert.Equal(
            ConstructionError.OutsideConstructionGrid,
            rules.Place(PartWoodenBlock, Level05OriginX, Level05OriginY - 1f, 0f, 1f, OwnerA).Error);
        Assert.Equal(
            ConstructionError.OutsideConstructionGrid,
            rules.Place(PartWoodenBlock, Level05OriginX, Level05OriginY + 2f, 0f, 1f, OwnerA).Error);

        Assert.Equal(8, rules.PartCount);
    }

    [Fact]
    public void Level05RealAllowanceCapsPigAndWoodenBlock()
    {
        ConstructionRules rules = CreateRules(Limits(Level05OriginX, Level05OriginY, Level05Rows, (PartPig, 1), (PartWoodenBlock, 3)));

        // The pig is allowed once (partCounts 4 -> 1) on any legal cell.
        Assert.True(rules.Place(PartPig, Level05OriginX, Level05OriginY, 0f, 1f, OwnerA).IsSuccess);
        ConstructionResult secondPig = rules.Place(PartPig, Level05OriginX + 1f, Level05OriginY, 0f, 1f, OwnerA);
        Assert.Equal(ConstructionError.PartAllowanceExceeded, secondPig.Error);

        // The wooden block is allowed three times, and the fourth is refused.
        Assert.True(rules.Place(PartWoodenBlock, Level05OriginX, Level05OriginY + 1f, 0f, 1f, OwnerA).IsSuccess);
        Assert.True(rules.Place(PartWoodenBlock, Level05OriginX + 1f, Level05OriginY + 1f, 0f, 1f, OwnerA).IsSuccess);
        Assert.True(rules.Place(PartWoodenBlock, Level05OriginX + 2f, Level05OriginY + 1f, 0f, 1f, OwnerA).IsSuccess);
        ConstructionResult fourthBlock = rules.Place(PartWoodenBlock, Level05OriginX - 1f, Level05OriginY, 0f, 1f, OwnerA);
        Assert.Equal(ConstructionError.PartAllowanceExceeded, fourthBlock.Error);

        // A family the level's table never names allows none at all -- the original hides a
        // zero-count type from the build bar (ConstructionUI.CheckUnlockedParts).
        ConstructionRules tntRules = CreateRules(Limits(Level05OriginX, Level05OriginY, Level05Rows, (PartPig, 1), (PartWoodenBlock, 3)));
        Assert.Equal(
            ConstructionError.PartAllowanceExceeded,
            tntRules.Place(PartTnt, Level05OriginX, Level05OriginY, 0f, 1f, OwnerA).Error);

        // The grid is checked first: an off-grid tnt is rejected for its cell, not its family.
        Assert.Equal(
            ConstructionError.OutsideConstructionGrid,
            tntRules.Place(PartTnt, Level05OriginX + 3f, Level05OriginY, 0f, 1f, OwnerA).Error);
    }

    [Fact]
    public void WideRowsDescribeFifteenCells()
    {
        // Rows[31, 31, 31, 0] -> columns 5, height 3, xMin -2, xMax 2.
        ConstructionRules rules = CreateRules(Limits(0f, 0f, [31, 31, 31, 0], (PartWoodenBlock, 64)), GridProbeLimits);

        int placedCount = 0;
        for (int x = -2; x <= 2; x++)
        {
            for (int y = 0; y <= 2; y++)
            {
                ConstructionResult placed = rules.Place(PartWoodenBlock, x, y, 0f, 1f, OwnerA);
                Assert.True(placed.IsSuccess, $"cell ({x}, {y}): {placed.Error}");
                placedCount++;
            }
        }

        Assert.Equal(15, placedCount);
        Assert.Equal(15, rules.PartCount);
        Assert.Equal(ConstructionError.OutsideConstructionGrid, rules.Place(PartWoodenBlock, 3f, 0f, 0f, 1f, OwnerA).Error);
        Assert.Equal(ConstructionError.OutsideConstructionGrid, rules.Place(PartWoodenBlock, -3f, 0f, 0f, 1f, OwnerA).Error);
        Assert.Equal(ConstructionError.OutsideConstructionGrid, rules.Place(PartWoodenBlock, 0f, 3f, 0f, 1f, OwnerA).Error);
    }

    [Fact]
    public void SparseRowsLeaveTheirHoleUnplaceable()
    {
        // Rows[1, 0, 1] -> columns 1, height 3, xMin 0, xMax 0 and the middle row's mask is zero, so
        // the formula leaves local y = 1 without a cell while y = 0 and y = 2 exist. This shape is
        // engineered to exercise the row indexing (Rows[height - 1 - j]); no original level carries
        // it.
        ConstructionRules rules = CreateRules(Limits(0f, 0f, [1, 0, 1], (PartWoodenBlock, 8)));

        Assert.True(rules.Place(PartWoodenBlock, 0f, 0f, 0f, 1f, OwnerA).IsSuccess);
        Assert.Equal(ConstructionError.OutsideConstructionGrid, rules.Place(PartWoodenBlock, 0f, 1f, 0f, 1f, OwnerA).Error);
        Assert.True(rules.Place(PartWoodenBlock, 0f, 2f, 0f, 1f, OwnerA).IsSuccess);

        // The single column is one cell wide: there is no local x = 1.
        Assert.Equal(ConstructionError.OutsideConstructionGrid, rules.Place(PartWoodenBlock, 1f, 0f, 0f, 1f, OwnerA).Error);
        Assert.Equal(2, rules.PartCount);
    }

    [Fact]
    public void VariantCountsAgainstItsBaseFamily()
    {
        // A variant draws on its base's allowance: with one wooden block allowed, the skin's own id
        // (79, variantOf 1) cannot smuggle a second one in.
        ConstructionRules single = CreateRules(Limits(0f, 0f, [31, 31, 31, 0], (PartWoodenBlock, 1)));
        Assert.True(single.Place(PartWoodenBlockSkin, -1.5f, 0.5f, 0f, 1f, OwnerA).IsSuccess);
        ConstructionResult secondVariant = single.Place(PartWoodenBlock, 0.5f, 0.5f, 0f, 1f, OwnerA);
        Assert.Equal(ConstructionError.PartAllowanceExceeded, secondVariant.Error);

        // With two allowed, the base and its variant both fit; the third from either id is refused.
        ConstructionRules pair = CreateRules(Limits(0f, 0f, [31, 31, 31, 0], (PartWoodenBlock, 2)));
        Assert.True(pair.Place(PartWoodenBlock, -1.5f, 0.5f, 0f, 1f, OwnerA).IsSuccess);
        Assert.True(pair.Place(PartWoodenBlockSkin, 0.5f, 0.5f, 0f, 1f, OwnerA).IsSuccess);
        ConstructionResult third = pair.Place(PartWoodenBlockSkin, 1.5f, 0.5f, 0f, 1f, OwnerA);
        Assert.Equal(ConstructionError.PartAllowanceExceeded, third.Error);
    }

    [Fact]
    public void AllowanceIsPerOwner()
    {
        ConstructionRules rules = CreateRules(Limits(0f, 0f, [31, 31, 31, 0], (PartWoodenBlock, 1)));

        // Each owner gets the level's full allowance; neither spends the other's.
        Assert.True(rules.Place(PartWoodenBlock, -1.5f, 0.5f, 0f, 1f, OwnerA).IsSuccess);
        Assert.True(rules.Place(PartWoodenBlock, 0.5f, 0.5f, 0f, 1f, OwnerB).IsSuccess);
        Assert.Equal(ConstructionError.PartAllowanceExceeded, rules.Place(PartWoodenBlock, 1.5f, 0.5f, 0f, 1f, OwnerA).Error);
        Assert.Equal(ConstructionError.PartAllowanceExceeded, rules.Place(PartWoodenBlock, 2.5f, 0.5f, 0f, 1f, OwnerB).Error);
        Assert.Equal(2, rules.PartCount);
    }

    [Fact]
    public void MovingOutsideTheGridIsRejectedWhileTheQuotaIsNotConsulted()
    {
        ConstructionRules rules = CreateRules(Limits(0f, 0f, [31, 31, 31, 0], (PartWoodenBlock, 1)));
        ConstructionResult placed = rules.Place(PartWoodenBlock, -1.5f, 0.5f, 0f, 1f, OwnerA);
        Assert.True(placed.IsSuccess);

        // The owner is at the family's quota already; a move within the grid still succeeds because
        // a transform never changes the part count.
        ConstructionResult moved = rules.Move(placed.Entity, 1.5f, 0.5f, OwnerA);
        Assert.True(moved.IsSuccess, moved.Error.ToString());
        Assert.True(rules.TryGetTransform(placed.Entity, out EntityTransform transform));
        Assert.Equal(1.5f, transform.Position.X);

        // A move whose anchor leaves the grid is refused and leaves the pose untouched.
        ConstructionResult offGrid = rules.Move(placed.Entity, 3.5f, 0.5f, OwnerA);
        Assert.Equal(ConstructionError.OutsideConstructionGrid, offGrid.Error);
        Assert.True(rules.TryGetTransform(placed.Entity, out transform));
        Assert.Equal(1.5f, transform.Position.X);
    }

    [Fact]
    public void ClearingLimitsRestoresUnrestrictedPlacement()
    {
        ConstructionRules rules = CreateRules(Limits(Level05OriginX, Level05OriginY, Level05Rows, (PartPig, 1), (PartWoodenBlock, 3)));

        Assert.Equal(ConstructionError.OutsideConstructionGrid, rules.Place(PartWoodenBlock, 100.5f, 100.5f, 0f, 1f, OwnerA).Error);

        rules.UseLevelBuildLimits(null);

        // Far from the level's own grid and unlisted in its family table: both gates are gone.
        Assert.True(rules.Place(PartWoodenBlock, 100.5f, 100.5f, 0f, 1f, OwnerA).IsSuccess);
        Assert.True(rules.Place(PartTnt, 101.5f, 100.5f, 0f, 1f, OwnerA).IsSuccess);
    }

    private static LevelBuildLimits Limits(float originX, float originY, int[] rows, params (uint PartTypeId, uint Count)[] counts) =>
        new(originX, originY, rows, [.. counts.Select(count => new LevelPartAllowance(count.PartTypeId, count.Count))]);

    private static ConstructionRules CreateRules(LevelBuildLimits? limits, ConstructionLimits? constructionLimits = null)
    {
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content, constructionLimits);
        rules.UseLevelBuildLimits(limits);
        return rules;
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
