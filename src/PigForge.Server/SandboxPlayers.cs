using System.Runtime.CompilerServices;

// The server test project verifies per-player sandbox state directly.
[assembly: InternalsVisibleTo("PigForge.Server.Tests")]

namespace PigForge.Server;

/// <summary>
/// Per-player sandbox state: whether a player's layout is currently materialised into
/// authoritative bodies. Unknown players are registered on first sight and start in the
/// editing state. <see cref="KnownPlayers"/> iterates ascending player ids so the
/// per-tick out-of-bounds sweep stays deterministic.
internal sealed class SandboxPlayers
{
    private readonly Dictionary<uint, bool> _materializedByPlayer = new();
    private readonly List<uint> _knownPlayers = new();

    public IReadOnlyList<uint> KnownPlayers => _knownPlayers;

    public bool IsMaterialized(uint playerId) =>
        _materializedByPlayer.TryGetValue(playerId, out bool materialized) && materialized;

    public void Register(uint playerId)
    {
        if (_materializedByPlayer.ContainsKey(playerId))
        {
            return;
        }

        _materializedByPlayer.Add(playerId, false);
        int index = _knownPlayers.BinarySearch(playerId);
        _knownPlayers.Insert(index < 0 ? ~index : index, playerId);
    }

    public void MarkMaterialized(uint playerId)
    {
        Register(playerId);
        _materializedByPlayer[playerId] = true;
    }

    public void MarkEditing(uint playerId)
    {
        Register(playerId);
        _materializedByPlayer[playerId] = false;
    }

    /// <summary>Drops a player that left the room, so a long-lived host does not accumulate
    /// one entry per closed socket. Player ids are never reused, so nothing can look it up.</summary>
    public void Forget(uint playerId)
    {
        if (!_materializedByPlayer.Remove(playerId))
        {
            return;
        }

        int index = _knownPlayers.BinarySearch(playerId);
        if (index >= 0)
        {
            _knownPlayers.RemoveAt(index);
        }
    }
}
