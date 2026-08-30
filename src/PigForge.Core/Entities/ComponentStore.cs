namespace PigForge.Core;

/// <summary>
/// Sparse component storage indexed by entity slot. Keys keep the full generation-safe
/// <see cref="EntityId"/>, so stale handles read as "absent" instead of returning the
/// recycling slot's payload. Steady-state reads, writes, and iteration do not allocate.
/// </summary>
public class ComponentStore<T>
{
    private readonly EntityStore _entities;
    private EntityId[] _keys;
    private T[] _values;
    private int _count;

    public ComponentStore(EntityStore entities)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _keys = new EntityId[InitialCapacity];
        _values = new T[InitialCapacity];
    }

    private const int InitialCapacity = 64;

    public int Count => _count;

    public void Set(EntityId id, T value)
    {
        ThrowIfStale(id);
        uint slot = id.SlotIndex;
        if (slot >= (uint)_keys.Length)
        {
            Grow((int)slot + 1);
        }

        if (_keys[slot].IsValid == false)
        {
            _count++;
        }

        _keys[slot] = id;
        _values[slot] = value;
    }

    public bool TryGet(EntityId id, out T value)
    {
        if (id.IsValid)
        {
            uint slot = id.SlotIndex;
            if (slot < (uint)_keys.Length && _keys[slot] == id)
            {
                value = _values[slot];
                return true;
            }
        }

        value = default!;
        return false;
    }

    public bool Remove(EntityId id)
    {
        if (!id.IsValid)
        {
            return false;
        }

        uint slot = id.SlotIndex;
        if (slot >= (uint)_keys.Length || _keys[slot] != id)
        {
            return false;
        }

        _keys[slot] = default;
        _values[slot] = default!;
        _count--;
        return true;
    }

    /// <summary>Drops every component entry at once (mode transitions, level resets).</summary>
    public void Clear()
    {
        Array.Clear(_keys, 0, _keys.Length);
        Array.Clear(_values, 0, _values.Length);
        _count = 0;
    }

    public Enumerator GetEnumerator() => new(this);

    private void ThrowIfStale(EntityId id)
    {
        if (!_entities.IsAlive(id))
        {
            throw new KeyNotFoundException($"Entity {id.Value} is stale or does not exist.");
        }
    }

    private void Grow(int minimum)
    {
        int newCapacity = Math.Max(_keys.Length * 2, minimum);
        Array.Resize(ref _keys, newCapacity);
        Array.Resize(ref _values, newCapacity);
    }

    public ref struct Enumerator
    {
        private readonly ComponentStore<T> _store;
        private int _index;

        internal Enumerator(ComponentStore<T> store)
        {
            _store = store;
            _index = -1;
        }

        public EntityId CurrentId => _store._keys[_index];

        public ref T CurrentValue => ref _store._values[_index];

        public bool MoveNext()
        {
            var keys = _store._keys;
            while ((uint)++_index < (uint)keys.Length)
            {
                // Skip slots whose entity was destroyed without an explicit Remove.
                if (keys[_index].IsValid && _store._entities.IsAlive(keys[_index]))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
