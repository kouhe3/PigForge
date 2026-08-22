using PigForge.Core;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

public sealed class FoundationContractTests
{
    [Fact]
    public void WorldStateStoresEntitiesByStableIds()
    {
        WorldState world = new();
        EntityState entity = new(new EntityId(7), new PhysicsBodyId(3));

        world.AddEntity(entity);

        Assert.True(world.TryGetEntity(new EntityId(7), out EntityState? stored));
        Assert.Same(entity, stored);
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void FixedTimeStepRejectsNonPositiveOrNonFiniteValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FixedTimeStep.FromSeconds(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FixedTimeStep.FromSeconds(float.NaN));
    }
}
