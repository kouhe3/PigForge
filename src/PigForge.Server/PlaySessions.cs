namespace PigForge.Server;

/// <summary>
/// Per-session player identity for the local play host. A connection may carry an opaque session
/// id (the client's <c>/play?session=</c> parameter): the first connection with a given id gets a
/// freshly allocated <see cref="PlayHost.NextPlayerId"/> id, and every later connection with the
/// same id resumes it, so a client that reconnects -- the 连接房间 button, a dropped socket -- is
/// still the owner of the parts it placed. A connection without a session id, or with one the host
/// has never seen, is a new player exactly as before, so "a disconnected player's parts stay in
/// the world" is untouched: an id is only ever resumed by the session it was assigned to, never
/// reclaimed by another player.
/// </summary>
internal sealed class PlaySessions(Func<uint> allocate)
{
    /// <summary>Shortest session id the host accepts, so a bare player id cannot be sent as one.</summary>
    internal const int MinSessionLength = 16;

    /// <summary>Longest session id the host accepts; longer values are treated as absent.</summary>
    internal const int MaxSessionLength = 64;

    /// <summary>Bound on tracked sessions, so a client cannot grow the map without limit. Past it
    /// a fresh connection still gets a player id, it just does not become resumable.</summary>
    internal const int MaxSessions = 1024;

    private readonly Dictionary<string, uint> _playerIdBySession = new(StringComparer.Ordinal);

    /// <summary>How many session ids the host has handed out (diagnostics and tests).</summary>
    public int SessionCount => _playerIdBySession.Count;

    /// <summary>The player id a known session id resumes, or false when it was never issued.</summary>
    public bool TryResolve(string? sessionId, out uint playerId)
    {
        if (!IsWellFormed(sessionId))
        {
            playerId = 0;
            return false;
        }

        return _playerIdBySession.TryGetValue(sessionId!, out playerId);
    }

    /// <summary>Resolves a connection's player id: the session's existing id when it has one,
    /// otherwise a freshly allocated, never-reused one. <paramref name="resumed"/> distinguishes
    /// the two so the host can log which connections continued an identity.</summary>
    public uint Resolve(string? sessionId, out bool resumed)
    {
        bool wellFormed = IsWellFormed(sessionId);
        if (wellFormed && _playerIdBySession.TryGetValue(sessionId!, out uint known))
        {
            resumed = true;
            return known;
        }

        resumed = false;
        uint allocated = allocate();
        if (wellFormed && _playerIdBySession.Count < MaxSessions)
        {
            _playerIdBySession.Add(sessionId!, allocated);
        }

        return allocated;
    }

    /// <summary>Resolves a connection's player id when the caller does not care whether the
    /// session was resumed.</summary>
    public uint Resolve(string? sessionId) => Resolve(sessionId, out _);

    /// <summary>
    /// Whether a value is usable as a session id. The id is opaque -- it only ever indexes this
    /// map, so it can never claim a player id directly -- and bounded to the alphabet a
    /// client-generated token uses, so a bare number is rejected instead of silently becoming an
    /// identity.
    /// </summary>
    internal static bool IsWellFormed(string? sessionId)
    {
        if (sessionId is null || sessionId.Length is < MinSessionLength or > MaxSessionLength)
        {
            return false;
        }

        foreach (char character in sessionId)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
