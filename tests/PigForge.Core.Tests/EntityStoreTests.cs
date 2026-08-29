using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

public sealed class EntityStoreTests
{
    [Fact]
    public void CreateIssuesSequentialValidHandles()
    {
        EntityStore store = new();

        EntityId first = store.Create();
        EntityId second = store.Create();

        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(0u, first.SlotIndex);
        Assert.Equal(1u, second.SlotIndex);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void DestroyedSlotIsReusedWithBumpedGenerationAndOldHandleDies()
    {
        EntityStore store = new();
        EntityId original = store.Create();

        store.Destroy(original);
        EntityId replacement = store.Create();

        Assert.Equal(original.SlotIndex, replacement.SlotIndex);
        Assert.Equal(original.Generation + 1, replacement.Generation);
        Assert.NotEqual(original, replacement);
        Assert.False(store.IsAlive(original));
        Assert.True(store.IsAlive(replacement));
        Assert.Equal(1, store.Count);
        Assert.Throws<KeyNotFoundException>(() => store.Destroy(original));
    }

    [Fact]
    public void DestroyedHandlesStayInvalidAcrossRecycleCyclesUntilGenerationWrap()
    {
        EntityStore store = new();
        EntityId original = store.Create();
        store.Destroy(original);

        // Stay strictly inside one generation window: every recycle keeps the old handle dead.
        uint cycles = EntityId.GenerationModulus - 3;
        for (uint cycle = 0; cycle < cycles; cycle++)
        {
            EntityId recycled = store.Create();
            Assert.Equal(original.SlotIndex, recycled.SlotIndex);
            Assert.NotEqual(original, recycled);
            Assert.False(store.IsAlive(original), $"Handle resurrected at recycle {cycle}.");
            store.Destroy(recycled);
        }

        // The 12-bit generation wraps after 4094 recycles; the handle aliases again by design.
        EntityId lastBeforeWrap = store.Create();
        Assert.NotEqual(original, lastBeforeWrap);
        store.Destroy(lastBeforeWrap);
        EntityId aliased = store.Create();
        Assert.Equal(original, aliased);
        Assert.True(store.IsAlive(original));
    }
}

public sealed class ComponentStoreTests
{
    [Fact]
    public void ComponentsAreStoredAndRemovedByHandle()
    {
        EntityStore entities = new();
        TransformStore transforms = new(entities);
        EntityId entity = entities.Create();

        transforms.Set(entity, new EntityTransform(new PhysicsVector3(1, 2, 3), PhysicsQuaternion.Identity));

        Assert.True(transforms.TryGet(entity, out EntityTransform stored));
        Assert.Equal(new PhysicsVector3(1, 2, 3), stored.Position);
        Assert.Equal(1, transforms.Count);

        Assert.True(transforms.Remove(entity));
        Assert.False(transforms.TryGet(entity, out _));
        Assert.Equal(0, transforms.Count);
        Assert.False(transforms.Remove(entity));
    }

    [Fact]
    public void RecycledSlotDoesNotLeakComponentsToOldHandles()
    {
        EntityStore entities = new();
        PartStore parts = new(entities);
        EntityId original = entities.Create();
        parts.Set(original, new PartLink(7));

        entities.Destroy(original);
        EntityId replacement = entities.Create();
        parts.Set(replacement, new PartLink(9));

        Assert.False(parts.TryGet(original, out _));
        Assert.True(parts.TryGet(replacement, out PartLink link));
        Assert.Equal(9u, link.PartTypeId);
    }

    [Fact]
    public void EnumeratorYieldsOnlyLiveComponents()
    {
        EntityStore entities = new();
        TransformStore transforms = new(entities);
        EntityId kept = entities.Create();
        EntityId dropped = entities.Create();
        transforms.Set(kept, new EntityTransform(PhysicsVector3.Zero, PhysicsQuaternion.Identity));
        transforms.Set(dropped, new EntityTransform(new PhysicsVector3(9, 9, 9), PhysicsQuaternion.Identity));

        entities.Destroy(dropped);
        transforms.Remove(dropped);

        var enumerator = transforms.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(kept, enumerator.CurrentId);
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void StaleHandlesCannotWriteComponents()
    {
        EntityStore entities = new();
        TransformStore transforms = new(entities);
        EntityId entity = entities.Create();
        entities.Destroy(entity);

        Assert.Throws<KeyNotFoundException>(() =>
            transforms.Set(entity, new EntityTransform(PhysicsVector3.Zero, PhysicsQuaternion.Identity)));
    }

    [Fact]
    public void SteadyStateTickPathDoesNotAllocate()
    {
        EntityStore entities = new();
        TransformStore transforms = new(entities);
        const int batch = 512;
        EntityId[] buffer = new EntityId[batch];

        // Warm-up: grow internal arrays and fill the free-slot list so the measured
        // section exercises the steady-state hot path only.
        for (int index = 0; index < batch; index++)
        {
            buffer[index] = entities.Create();
            transforms.Set(buffer[index], new EntityTransform(PhysicsVector3.Zero, PhysicsQuaternion.Identity));
        }

        foreach (EntityId id in buffer)
        {
            transforms.Remove(id);
            entities.Destroy(id);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int tick = 0; tick < 16; tick++)
        {
            for (int index = 0; index < batch; index++)
            {
                EntityId entity = entities.Create();
                transforms.Set(entity, new EntityTransform(new PhysicsVector3(index, tick, 0), PhysicsQuaternion.Identity));
                buffer[index] = entity;
            }

            var enumerator = transforms.GetEnumerator();
            while (enumerator.MoveNext())
            {
                _ = enumerator.CurrentValue;
            }

            for (int index = 0; index < batch; index++)
            {
                transforms.Remove(buffer[index]);
                entities.Destroy(buffer[index]);
            }
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }
}
