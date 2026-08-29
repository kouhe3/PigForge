namespace PigForge.Core;

/// <summary>
/// Generation-safe entity registry: slots are recycled through a free list and every
/// recycle bumps the slot generation, so handles captured before a destroy stay invalid.
/// All operations are allocation-free once the internal arrays have reached high water.
/// </summary>
public sealed class EntityStore
{
    private uint[] _generations = new uint[InitialCapacity];
    private int[] _freeSlots = new int[InitialCapacity];
    private int _freeCount;
    private uint _slotHighWater;
    private int _aliveCount;

    private const int InitialCapacity = 64;

    public int Count => _aliveCount;

    public uint SlotHighWater => _slotHighWater;

    public EntityId Create()
    {
        uint slot;
        if (_freeCount > 0)
        {
            slot = (uint)_freeSlots[--_freeCount];
        }
        else
        {
            slot = _slotHighWater;
            if (slot >= EntityId.SlotMask)
            {
                throw new InvalidOperationException($"The entity slot space is exhausted (limit {EntityId.SlotMask}).");
            }

            _slotHighWater++;
            if (_slotHighWater > (uint)_generations.Length)
            {
                Grow();
            }
        }

        uint generation = _generations[slot];
        if (generation == 0)
        {
            generation = 1;
        }

        _generations[slot] = generation;
        _aliveCount++;
        return EntityId.FromSlotAndGeneration(slot, generation);
    }

    public bool IsAlive(EntityId id)
    {
        if (!id.IsValid)
        {
            return false;
        }

        uint slot = id.SlotIndex;
        return slot < _slotHighWater
            && _generations[slot] == id.Generation
            && _generations[slot] != 0;
    }

    public void Destroy(EntityId id)
    {
        if (!IsAlive(id))
        {
            throw new KeyNotFoundException($"Entity {id.Value} is stale or does not exist.");
        }

        uint slot = id.SlotIndex;
        uint generation = _generations[slot];
        generation++;
        if (generation >= EntityId.GenerationModulus)
        {
            generation = 1;
        }

        _generations[slot] = generation;
        _freeSlots[_freeCount++] = (int)slot;
        _aliveCount--;
    }

    private void Grow()
    {
        int newCapacity = _generations.Length * 2;
        Array.Resize(ref _generations, newCapacity);
        Array.Resize(ref _freeSlots, newCapacity);
    }
}
