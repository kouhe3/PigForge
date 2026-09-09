using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

public sealed class ConstructionRulesTests
{
    private const uint PartBlock = 1;
    private const uint PartPlank = 2;

    private const uint OwnerA = 1;
    private const uint OwnerB = 2;

    [Fact]
    public void PlaceCreatesEntityWithPartsTransformAndConnections()
    {
        (ConstructionRules rules, _) = CreateRules();

        ConstructionResult first = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0);
        ConstructionResult second = rules.Place(PartBlock, 1.5f, 0.5f, 0f, 1f, 0);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, rules.PartCount);
        Assert.Equal(new PhysicsVector3(0.5f, 0.5f, 0f), TransformOf(rules, first.Entity).Position);
        Assert.Equal(new[] { second.Entity.Value }, rules.ConnectionsOf(first.Entity));
        Assert.Equal(new[] { first.Entity.Value }, rules.ConnectionsOf(second.Entity));
    }

    [Fact]
    public void PlaceOnOverlappingFootprintIsRejected()
    {
        (ConstructionRules rules, _) = CreateRules();

        Assert.True(rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0).IsSuccess);
        ConstructionResult blocked = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0);

        Assert.Equal(ConstructionError.CellsOccupied, blocked.Error);
        Assert.Equal(1, rules.PartCount);
    }

    [Theory]
    [InlineData(99u, 0f, ConstructionError.UnknownPartType)]
    [InlineData(PartBlock, float.NaN, ConstructionError.InvalidRotation)]
    public void InvalidPlacementCommandsAreRejected(uint partTypeId, float angle, ConstructionError expected)
    {
        (ConstructionRules rules, _) = CreateRules();

        ConstructionResult result = rules.Place(partTypeId, 0.5f, 0.5f, angle, 1f, 0);

        Assert.Equal(expected, result.Error);
        Assert.Equal(0, rules.PartCount);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(-1f)]
    [InlineData(5f)]
    public void InvalidScalesAreRejected(float scale)
    {
        (ConstructionRules rules, _) = CreateRules();

        Assert.Equal(ConstructionError.InvalidScale, rules.Place(PartBlock, 0.5f, 0.5f, 0f, scale, 0).Error);
    }

    [Fact]
    public void PartLimitIsEnforced()
    {
        (ConstructionRules rules, _) = CreateRules(new ConstructionLimits(MaxParts: 2, MaxConnectionsPerPart: 6, MaxFootprintCells: 64));

        Assert.True(rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0).IsSuccess);
        Assert.True(rules.Place(PartBlock, 5.5f, 0.5f, 0f, 1f, 0).IsSuccess);
        ConstructionResult third = rules.Place(PartBlock, 9.5f, 0.5f, 0f, 1f, 0);

        Assert.Equal(ConstructionError.PartLimitReached, third.Error);
    }

    [Fact]
    public void ConnectionLimitIsEnforced()
    {
        (ConstructionRules rules, _) = CreateRules(new ConstructionLimits(MaxParts: 64, MaxConnectionsPerPart: 2, MaxFootprintCells: 64));

        Assert.True(rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0).IsSuccess);
        Assert.True(rules.Place(PartBlock, 1.5f, 0.5f, 0f, 1f, 0).IsSuccess);
        Assert.True(rules.Place(PartBlock, -0.5f, 0.5f, 0f, 1f, 0).IsSuccess);
        ConstructionResult thirdNeighbour = rules.Place(PartBlock, 0.5f, 1.5f, 0f, 1f, 0);

        Assert.Equal(ConstructionError.ConnectionLimitReached, thirdNeighbour.Error);
    }

    [Fact]
    public void RemoveDestroysEntityAndFreesSpaceForReuse()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult placed = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0);

        ConstructionResult removed = rules.Remove(placed.Entity, 0);
        ConstructionResult replaced = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0);

        Assert.True(removed.IsSuccess);
        Assert.False(rules.IsAlive(placed.Entity));
        Assert.True(replaced.IsSuccess);
        Assert.NotEqual(placed.Entity, replaced.Entity);
    }

    [Fact]
    public void RemoveOfUnknownOrNonConstructionEntityIsRejected()
    {
        (ConstructionRules rules, EntityStore entities) = CreateRules();

        Assert.Equal(ConstructionError.EntityNotFound, rules.Remove(new EntityId(12345), 0).Error);

        EntityId foreign = entities.Create();
        Assert.Equal(ConstructionError.NotAConstructionEntity, rules.Remove(foreign, 0).Error);
        Assert.True(rules.IsAlive(foreign), "A rejected removal must leave the entity intact.");
    }

    [Fact]
    public void ForeignOwnerRotateAndRemoveAreRejected()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult placed = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA);
        Assert.True(placed.IsSuccess);

        Assert.Equal(ConstructionError.NotOwnedByPlayer, rules.Rotate(placed.Entity, 0.5f, OwnerB).Error);
        Assert.Equal(ConstructionError.NotOwnedByPlayer, rules.Remove(placed.Entity, OwnerB).Error);

        Assert.True(rules.IsAlive(placed.Entity));
        Assert.Equal(1, rules.PartCount);
        Assert.Equal(new[] { placed.Entity.Value }, rules.PlacedEntitiesOf(OwnerA));
    }

    [Fact]
    public void ErrorOrderChecksExistenceAndMembershipBeforeOwnership()
    {
        (ConstructionRules rules, EntityStore entities) = CreateRules();

        Assert.Equal(ConstructionError.EntityNotFound, rules.Rotate(new EntityId(12345), 0f, OwnerB).Error);
        Assert.Equal(ConstructionError.EntityNotFound, rules.Remove(new EntityId(12345), OwnerB).Error);

        EntityId foreign = entities.Create();
        Assert.Equal(ConstructionError.NotAConstructionEntity, rules.Rotate(foreign, 0f, OwnerB).Error);
        Assert.Equal(ConstructionError.NotAConstructionEntity, rules.Remove(foreign, OwnerB).Error);
    }

    [Fact]
    public void PartLimitIsEnforcedPerOwner()
    {
        (ConstructionRules rules, _) = CreateRules(new ConstructionLimits(MaxParts: 2, MaxConnectionsPerPart: 6, MaxFootprintCells: 64));

        Assert.True(rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA).IsSuccess);
        Assert.True(rules.Place(PartBlock, 5.5f, 0.5f, 0f, 1f, OwnerA).IsSuccess);
        Assert.Equal(ConstructionError.PartLimitReached, rules.Place(PartBlock, 9.5f, 0.5f, 0f, 1f, OwnerA).Error);

        Assert.True(rules.Place(PartBlock, 0.5f, 5.5f, 0f, 1f, OwnerB).IsSuccess);
        Assert.True(rules.Place(PartBlock, 5.5f, 5.5f, 0f, 1f, OwnerB).IsSuccess);
        Assert.Equal(ConstructionError.PartLimitReached, rules.Place(PartBlock, 9.5f, 5.5f, 0f, 1f, OwnerB).Error);
        Assert.Equal(4, rules.PartCount);

        Assert.Equal(2, rules.ResetOwned(OwnerA).Count);

        Assert.True(rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA).IsSuccess);
        Assert.True(rules.Place(PartBlock, 5.5f, 0.5f, 0f, 1f, OwnerA).IsSuccess);
        Assert.Equal(ConstructionError.PartLimitReached, rules.Place(PartBlock, 9.5f, 0.5f, 0f, 1f, OwnerA).Error);
        Assert.Equal(4, rules.PartCount);
    }

    [Fact]
    public void OverlapAcrossOwnersIsRejected()
    {
        (ConstructionRules rules, _) = CreateRules();

        Assert.True(rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA).IsSuccess);
        ConstructionResult blocked = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerB);

        Assert.Equal(ConstructionError.CellsOccupied, blocked.Error);
        Assert.Equal(1, rules.PartCount);
        Assert.Empty(rules.PlacedEntitiesOf(OwnerB));
    }

    [Fact]
    public void AdjacentPartsOfDifferentOwnersDoNotConnect()
    {
        (ConstructionRules rules, _) = CreateRules();

        ConstructionResult left = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA);
        ConstructionResult right = rules.Place(PartBlock, 1.5f, 0.5f, 0f, 1f, OwnerB);

        Assert.True(left.IsSuccess);
        Assert.True(right.IsSuccess);
        Assert.Empty(rules.ConnectionsOf(left.Entity));
        Assert.Empty(rules.ConnectionsOf(right.Entity));
    }

    [Fact]
    public void RotateRecomputesOccupancyTransformAndConnections()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult plank = rules.Place(PartPlank, 1.0f, 0.5f, 0f, 1f, 0);
        ConstructionResult block = rules.Place(PartBlock, 2.5f, 0.5f, 0f, 1f, 0);
        Assert.True(plank.IsSuccess);
        Assert.True(block.IsSuccess);
        Assert.NotEmpty(rules.ConnectionsOf(plank.Entity));

        ConstructionResult rotated = rules.Rotate(plank.Entity, angle: MathF.PI / 2f, owner: 0);

        Assert.True(rotated.IsSuccess);
        EntityTransform transform = TransformOf(rules, plank.Entity);
        Assert.Equal(1.0f, transform.Position.X, 3f);
        Assert.Equal(0.5f, transform.Position.Y, 3f);
        Assert.Equal(MathF.Sin(MathF.PI / 4f), transform.Rotation.Z, 3f);
        // The rotated plank no longer reaches the block through proximity.
        Assert.Empty(rules.ConnectionsOf(plank.Entity));
        Assert.Empty(rules.ConnectionsOf(block.Entity));
    }

    [Fact]
    public void RotateIntoOccupiedFootprintIsBlocked()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult plank = rules.Place(PartPlank, 1.0f, 0.5f, 0f, 1f, 0);
        // Sits exactly where the 90°-rotated plank would sweep, flush to its rest pose.
        Assert.True(rules.Place(PartBlock, 1.0f, 1.5f, 0f, 1f, 0).IsSuccess);

        ConstructionResult rotated = rules.Rotate(plank.Entity, angle: MathF.PI / 2f, owner: 0);

        Assert.Equal(ConstructionError.TransformBlocked, rotated.Error);
        EntityTransform transform = TransformOf(rules, plank.Entity);
        Assert.Equal(0f, transform.Rotation.Z, precision: 4);
        Assert.Single(rules.ConnectionsOf(plank.Entity));
    }

    [Fact]
    public void ResetOwnedDestroysOnlyThatOwnersLayout()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult a1 = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA);
        ConstructionResult a2 = rules.Place(PartBlock, 1.5f, 0.5f, 0f, 1f, OwnerA);
        ConstructionResult b1 = rules.Place(PartBlock, 0.5f, 5.5f, 0f, 1f, OwnerB);
        ConstructionResult b2 = rules.Place(PartBlock, 1.5f, 5.5f, 0f, 1f, OwnerB);
        Assert.True(a1.IsSuccess && a2.IsSuccess && b1.IsSuccess && b2.IsSuccess);
        Assert.Equal(new[] { b2.Entity.Value }, rules.ConnectionsOf(b1.Entity));
        long hashB = rules.ComputeLayoutHash(OwnerB);

        List<uint> destroyed = rules.ResetOwned(OwnerA);

        Assert.Equal(new[] { a1.Entity.Value, a2.Entity.Value }, destroyed);
        Assert.False(rules.IsAlive(a1.Entity));
        Assert.False(rules.IsAlive(a2.Entity));
        Assert.Empty(rules.PlacedEntitiesOf(OwnerA));
        Assert.Equal(new[] { b1.Entity.Value, b2.Entity.Value }, rules.PlacedEntitiesOf(OwnerB));
        Assert.Equal(new[] { b2.Entity.Value }, rules.ConnectionsOf(b1.Entity));
        Assert.Equal(new[] { b1.Entity.Value }, rules.ConnectionsOf(b2.Entity));
        Assert.Equal(hashB, rules.ComputeLayoutHash(OwnerB));
        Assert.Equal(2, rules.PartCount);
    }

    [Fact]
    public void OwnerLayoutHashIsStableAcrossRuns()
    {
        (long hashA, long hashB, ConstructionError[] rejections) first = RunOwnerFixtureScript();
        (long hashA, long hashB, ConstructionError[] rejections) second = RunOwnerFixtureScript();

        Assert.Equal(first.hashA, second.hashA);
        Assert.Equal(first.hashB, second.hashB);
        Assert.Equal(first.rejections, second.rejections);
        Assert.Equal(
            new[] { ConstructionError.CellsOccupied, ConstructionError.CellsOccupied, ConstructionError.NotOwnedByPlayer },
            first.rejections);
    }

    [Fact]
    public void ForgetFreesTheFootprintOfADestroyedEntity()
    {
        (ConstructionRules rules, EntityStore entities) = CreateRules();
        ConstructionResult placed = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA);
        Assert.True(placed.IsSuccess);

        entities.Destroy(placed.Entity);
        rules.Forget(placed.Entity);
        rules.Forget(placed.Entity);
        rules.Forget(new EntityId(12345));

        Assert.Equal(0, rules.PartCount);
        Assert.Empty(rules.PlacedEntitiesOf(OwnerA));
        Assert.False(rules.IsAlive(placed.Entity));

        Assert.True(rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerB).IsSuccess);
        Assert.Equal(1, rules.PartCount);
    }

    [Fact]
    public void ForgetOfLiveEntityClearsBookkeepingWithoutDestroyingIt()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult placed = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA);

        rules.Forget(placed.Entity);

        Assert.True(rules.IsAlive(placed.Entity));
        Assert.Equal(0, rules.PartCount);
        Assert.False(rules.TryGetPartTypeId(placed.Entity, out _));
        Assert.Empty(rules.PlacedEntitiesOf(OwnerA));
        Assert.True(rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerB).IsSuccess);
    }

    [Fact]
    public void CommandScriptReplaysDeterministically()
    {
        (long hash, ConstructionError[] rejections) firstRun = RunFixtureScript();
        (long hash, ConstructionError[] rejections) secondRun = RunFixtureScript();

        Assert.Equal(firstRun.hash, secondRun.hash);
        Assert.Equal(firstRun.rejections, secondRun.rejections);
        Assert.Contains(ConstructionError.CellsOccupied, firstRun.rejections);
        Assert.Contains(ConstructionError.InvalidRotation, firstRun.rejections);
        Assert.Contains(ConstructionError.InvalidScale, firstRun.rejections);
        Assert.Contains(ConstructionError.InvalidPosition, firstRun.rejections);
    }

    [Fact]
    public void MoveMigratesFootprintAndRecomputesConnections()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult first = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0);
        ConstructionResult second = rules.Place(PartBlock, 1.5f, 0.5f, 0f, 1f, 0);
        ConstructionResult third = rules.Place(PartBlock, 5.5f, 0.5f, 0f, 1f, 0);
        Assert.True(first.IsSuccess && second.IsSuccess && third.IsSuccess);
        Assert.Equal(new[] { second.Entity.Value }, rules.ConnectionsOf(first.Entity));

        ConstructionResult moved = rules.Move(first.Entity, 4.5f, 0.5f, 0);

        Assert.True(moved.IsSuccess);
        EntityTransform transform = TransformOf(rules, first.Entity);
        Assert.Equal(4.5f, transform.Position.X, 4);
        Assert.Equal(0.5f, transform.Position.Y, 4);
        Assert.Equal(new[] { third.Entity.Value }, rules.ConnectionsOf(first.Entity));
        Assert.Empty(rules.ConnectionsOf(second.Entity));
        Assert.Equal(new[] { first.Entity.Value }, rules.ConnectionsOf(third.Entity));

        // The vacated footprint is buildable again.
        Assert.True(rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0).IsSuccess);
    }

    [Fact]
    public void ScaleResizesFootprintAndRejectsOversizedResults()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult placed = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0);
        Assert.True(placed.IsSuccess);

        ConstructionResult scaled = rules.Scale(placed.Entity, 2f, 0);

        Assert.True(scaled.IsSuccess);
        Assert.Equal(2f, TransformOf(rules, placed.Entity).Scale, 4);
        // The enlarged footprint now covers cells that were free before.
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartBlock, 1.5f, 0.5f, 0f, 1f, 0).Error);

        (ConstructionRules small, _) = CreateRules(new ConstructionLimits(MaxParts: 64, MaxConnectionsPerPart: 6, MaxFootprintCells: 4));
        ConstructionResult plank = small.Place(PartPlank, 1.0f, 0.5f, 0f, 1f, 0);
        Assert.True(plank.IsSuccess);

        ConstructionResult tooLarge = small.Scale(plank.Entity, 2f, 0);

        Assert.Equal(ConstructionError.FootprintTooLarge, tooLarge.Error);
        Assert.Equal(1f, TransformOf(small, plank.Entity).Scale, 4);
    }

    [Fact]
    public void MoveAndScaleOntoAnotherPartAreTransformBlocked()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult first = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0);
        ConstructionResult second = rules.Place(PartBlock, 2.5f, 0.5f, 0f, 1f, 0);
        Assert.True(first.IsSuccess && second.IsSuccess);

        ConstructionResult moved = rules.Move(first.Entity, 2.5f, 0.5f, 0);
        ConstructionResult scaled = rules.Scale(second.Entity, 4f, 0);

        Assert.Equal(ConstructionError.TransformBlocked, moved.Error);
        Assert.Equal(ConstructionError.TransformBlocked, scaled.Error);
        Assert.Equal(0.5f, TransformOf(rules, first.Entity).Position.X, 4);
        Assert.Equal(1f, TransformOf(rules, second.Entity).Scale, 4);
        Assert.Equal(2, rules.PartCount);
    }

    [Fact]
    public void TransformErrorMatrixIsSharedByMoveScaleAndRotate()
    {
        (ConstructionRules rules, EntityStore entities) = CreateRules();
        ConstructionResult owned = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA);
        ConstructionResult other = rules.Place(PartBlock, 5.5f, 0.5f, 0f, 1f, OwnerB);
        Assert.True(owned.IsSuccess && other.IsSuccess);

        Assert.Equal(ConstructionError.EntityNotFound, rules.Move(new EntityId(12345), 1f, 1f, OwnerA).Error);
        Assert.Equal(ConstructionError.EntityNotFound, rules.Scale(new EntityId(12345), 2f, OwnerA).Error);
        EntityId foreign = entities.Create();
        Assert.Equal(ConstructionError.NotAConstructionEntity, rules.Move(foreign, 1f, 1f, OwnerA).Error);
        Assert.Equal(ConstructionError.NotAConstructionEntity, rules.Scale(foreign, 2f, OwnerA).Error);

        Assert.Equal(ConstructionError.NotOwnedByPlayer, rules.Move(owned.Entity, 1f, 1f, OwnerB).Error);
        Assert.Equal(ConstructionError.NotOwnedByPlayer, rules.Scale(owned.Entity, 2f, OwnerB).Error);

        Assert.Equal(ConstructionError.InvalidPosition, rules.Move(owned.Entity, float.NaN, 1f, OwnerA).Error);
        Assert.Equal(ConstructionError.InvalidPosition, rules.Move(owned.Entity, 1f, float.PositiveInfinity, OwnerA).Error);
        Assert.Equal(ConstructionError.InvalidScale, rules.Scale(owned.Entity, 0f, OwnerA).Error);
        Assert.Equal(ConstructionError.InvalidScale, rules.Scale(owned.Entity, float.NaN, OwnerA).Error);
        Assert.Equal(ConstructionError.InvalidScale, rules.Scale(owned.Entity, 5f, OwnerA).Error);
        Assert.Equal(ConstructionError.InvalidRotation, rules.Rotate(owned.Entity, float.NaN, OwnerA).Error);

        // A rejected transform leaves the pose untouched.
        EntityTransform transform = TransformOf(rules, owned.Entity);
        Assert.Equal(0.5f, transform.Position.X, 4);
        Assert.Equal(1f, transform.Scale, 4);
    }

    [Fact]
    public void FrozenEntitiesRejectMoveScaleAndRotate()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult placed = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA);
        Assert.True(placed.IsSuccess);

        rules.FreezeAll(new Dictionary<uint, (PhysicsVector3 Position, PhysicsQuaternion Rotation)>());

        Assert.True(rules.IsFrozen(placed.Entity.Value));
        Assert.Equal(ConstructionError.FrozenEntity, rules.Move(placed.Entity, 1f, 1f, OwnerA).Error);
        Assert.Equal(ConstructionError.FrozenEntity, rules.Scale(placed.Entity, 2f, OwnerA).Error);
        Assert.Equal(ConstructionError.FrozenEntity, rules.Rotate(placed.Entity, 1f, OwnerA).Error);
    }

    [Fact]
    public void TransformConnectionLimitsAreEnforced()
    {
        (ConstructionRules rules, _) = CreateRules(new ConstructionLimits(MaxParts: 64, MaxConnectionsPerPart: 2, MaxFootprintCells: 64));
        ConstructionResult first = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, 0);
        ConstructionResult middle = rules.Place(PartBlock, 1.5f, 0.5f, 0f, 1f, 0);
        ConstructionResult last = rules.Place(PartBlock, 2.5f, 0.5f, 0f, 1f, 0);
        ConstructionResult far = rules.Place(PartBlock, 0.5f, 3.5f, 0f, 1f, 0);
        Assert.True(first.IsSuccess && middle.IsSuccess && last.IsSuccess && far.IsSuccess);
        Assert.Equal(2, rules.ConnectionsOf(middle.Entity).Count);

        ConstructionResult moved = rules.Move(far.Entity, 1.5f, 1.5f, 0);

        Assert.Equal(ConstructionError.ConnectionLimitReached, moved.Error);
        EntityTransform transform = TransformOf(rules, far.Entity);
        Assert.Equal(0.5f, transform.Position.X, 4);
        Assert.Equal(3.5f, transform.Position.Y, 4);
    }

    [Fact]
    public void TransformScriptReplaysDeterministically()
    {
        (long hash, ConstructionError[] rejections) first = RunTransformFixtureScript();
        (long hash, ConstructionError[] rejections) second = RunTransformFixtureScript();

        Assert.Equal(first.hash, second.hash);
        Assert.Equal(first.rejections, second.rejections);
        Assert.Contains(ConstructionError.TransformBlocked, first.rejections);
        Assert.Contains(ConstructionError.InvalidScale, first.rejections);
    }

    private static (long Hash, ConstructionError[] Rejections) RunTransformFixtureScript()
    {
        (ConstructionRules rules, _) = CreateRules();
        List<ConstructionError> rejections = new();
        ConstructionResult first = rules.Place(PartBlock, 0.5f, 0.5f, 0f, 1f, OwnerA);
        ConstructionResult second = rules.Place(PartBlock, 3.5f, 0.5f, 0f, 1f, OwnerA);
        ConstructionResult plank = rules.Place(PartPlank, 1.0f, 4.5f, 0f, 1f, OwnerA);
        Assert.True(first.IsSuccess && second.IsSuccess && plank.IsSuccess);

        ConstructionResult moved = rules.Move(first.Entity, 2.5f, 0.5f, OwnerA);
        if (!moved.IsSuccess)
        {
            rejections.Add(moved.Error);
        }

        ConstructionResult scaled = rules.Scale(plank.Entity, 2f, OwnerA);
        if (!scaled.IsSuccess)
        {
            rejections.Add(scaled.Error);
        }

        ConstructionResult blocked = rules.Move(second.Entity, 2.5f, 0.5f, OwnerA);
        if (!blocked.IsSuccess)
        {
            rejections.Add(blocked.Error);
        }

        ConstructionResult rotated = rules.Rotate(plank.Entity, MathF.PI / 4f, OwnerA);
        if (!rotated.IsSuccess)
        {
            rejections.Add(rotated.Error);
        }

        ConstructionResult invalid = rules.Scale(first.Entity, 0f, OwnerA);
        if (!invalid.IsSuccess)
        {
            rejections.Add(invalid.Error);
        }

        return (rules.ComputeLayoutHash(), rejections.ToArray());
    }

    private static EntityTransform TransformOf(ConstructionRules rules, EntityId entity)
    {
        Assert.True(rules.TryGetTransform(entity, out EntityTransform transform));
        return transform;
    }

    private static (ConstructionRules Rules, EntityStore Entities) CreateRules(ConstructionLimits? limits = null)
    {
        PartContentLibrary content = new(PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "construction-test-v1",
            "parts": [
                { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 2, "name": "plank", "mode": "dynamic", "mass": 0.5, "shapes": [ { "kind": "box", "halfExtents": [1.0, 0.5, 0.5] } ] }
            ]
        }
        """));
        EntityStore entities = new();
        return (new ConstructionRules(entities, new PartStore(entities), new TransformStore(entities), content, limits), entities);
    }

    private static (long Hash, ConstructionError[] Rejections) RunFixtureScript()
    {
        (ConstructionRules rules, _) = CreateRules();
        List<ConstructionError> rejections = new();
        List<EntityId> entities = new();

        (char op, uint part, float x, float y, float angle, float scale)[] script =
        {
            ('p', PartBlock, 0.5f, 0.5f, 0f, 1f),
            ('p', PartBlock, 1.5f, 0.5f, 0f, 1f),
            ('p', PartPlank, 4.0f, 0.5f, 0f, 1f),
            ('r', 0, 0, 0, MathF.PI / 2f, 1f),
            ('p', PartBlock, 1.5f, 0.5f, 0f, 1f),
            ('p', PartBlock, 9.5f, 0.5f, 0f, 0f),
            ('p', PartBlock, 8.5f, 0.5f, float.NaN, 1f),
            ('p', PartBlock, float.NaN, 0.5f, 0f, 1f),
            ('p', PartBlock, 6.0f, 6.0f, 0.4f, 1f),
            ('x', 0, 0, 0, 0f, 1f),
        };

        foreach ((char op, uint part, float x, float y, float angle, float scale) in script)
        {
            switch (op)
            {
                case 'p':
                    ConstructionResult placed = rules.Place(part, x, y, angle, scale, 0);
                    if (placed.IsSuccess)
                    {
                        entities.Add(placed.Entity);
                    }
                    else
                    {
                        rejections.Add(placed.Error);
                    }

                    break;
                case 'r':
                    ConstructionResult rotated = rules.Rotate(entities[^1], angle, 0);
                    if (!rotated.IsSuccess)
                    {
                        rejections.Add(rotated.Error);
                    }

                    break;
                case 'x':
                    ConstructionResult removed = rules.Remove(entities[0], 0);
                    if (!removed.IsSuccess)
                    {
                        rejections.Add(removed.Error);
                    }

                    break;
            }
        }

        return (rules.ComputeLayoutHash(), rejections.ToArray());
    }

    private static (long HashA, long HashB, ConstructionError[] Rejections) RunOwnerFixtureScript()
    {
        (ConstructionRules rules, _) = CreateRules();
        List<ConstructionError> rejections = new();
        List<EntityId> ownedA = new();
        List<EntityId> ownedB = new();

        (uint owner, uint part, float x, float y, float angle, float scale)[] placements =
        {
            (OwnerA, PartBlock, 0.5f, 0.5f, 0f, 1f),
            (OwnerB, PartBlock, 0.5f, 5.5f, 0f, 1f),
            (OwnerA, PartBlock, 1.5f, 0.5f, 0f, 1f),
            (OwnerB, PartBlock, 1.5f, 5.5f, 0f, 1f),
            (OwnerB, PartBlock, 0.5f, 5.5f, 0f, 1f),
            (OwnerA, PartBlock, 0.5f, 5.5f, 0f, 1f),
            (OwnerA, PartPlank, 4.0f, 0.5f, 0f, 1f),
        };

        foreach ((uint owner, uint part, float x, float y, float angle, float scale) in placements)
        {
            ConstructionResult placed = rules.Place(part, x, y, angle, scale, owner);
            if (placed.IsSuccess)
            {
                (owner == OwnerA ? ownedA : ownedB).Add(placed.Entity);
            }
            else
            {
                rejections.Add(placed.Error);
            }
        }

        Assert.True(rules.Rotate(ownedA[^1], MathF.PI / 2f, OwnerA).IsSuccess);
        rejections.Add(rules.Rotate(ownedB[0], 0f, OwnerA).Error);
        Assert.True(rules.Remove(ownedB[^1], OwnerB).IsSuccess);

        return (rules.ComputeLayoutHash(OwnerA), rules.ComputeLayoutHash(OwnerB), rejections.ToArray());
    }
}
