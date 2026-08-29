using System.Globalization;
using PigForge.Protocol;

namespace PigForge.Replay;

public enum ReplayDiffKind
{
    FrameMissing,
    EntityMissing,
    SnapshotField,
    EventMissing,
    FinalOutcome,
    FinalCompletedTick,
    FinalStateHash
}

/// <summary>A single divergence between two replay runs, anchored by tick and entity.</summary>
public sealed record ReplayDifference(
    ReplayDiffKind Kind,
    uint Tick,
    uint EntityId,
    string Field,
    string Left,
    string Right);

public sealed record ReplayDiffReport(IReadOnlyList<ReplayDifference> Differences)
{
    public bool IsEmpty => Differences.Count == 0;
}

public sealed record ReplayDiffOptions
{
    public float PositionTolerance { get; init; } = 1e-3f;
    public float RotationTolerance { get; init; } = 1e-3f;
    public float VelocityTolerance { get; init; } = 1e-3f;

    /// <summary>
    /// Event alignment window in ticks. Cross-backend contact timing legitimately drifts
    /// by a few ticks, so event streams are matched within this window before reporting.
    /// </summary>
    public uint EventTickTolerance { get; init; } = 0;
}

/// <summary>
/// Compares two replay outputs (any backends that emit the shared replay schema) and
/// reports snapshot, event, and final-result differences by tick and entity id.
/// </summary>
public static class ReplayComparer
{
    public static ReplayDiffReport Compare(ReplayOutput left, ReplayOutput right, ReplayDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        options ??= new ReplayDiffOptions();

        List<ReplayDifference> differences = new();
        CompareFrames(left, right, options, differences);
        CompareFinalResult(left, right, differences);
        return new ReplayDiffReport(differences);
    }

    private static void CompareFrames(ReplayOutput left, ReplayOutput right, ReplayDiffOptions options, List<ReplayDifference> differences)
    {
        uint maxTicks = Math.Max((uint)left.Frames.Count, (uint)right.Frames.Count);
        for (uint tick = 1; tick <= maxTicks; tick++)
        {
            ReplayFrame? leftFrame = FindFrame(left, tick);
            ReplayFrame? rightFrame = FindFrame(right, tick);
            if (leftFrame is null || rightFrame is null)
            {
                differences.Add(new ReplayDifference(
                    ReplayDiffKind.FrameMissing,
                    tick,
                    EntityId: 0,
                    Field: "frame",
                    Left: leftFrame is null ? "missing" : "present",
                    Right: rightFrame is null ? "missing" : "present"));
                continue;
            }

            CompareSnapshots(tick, leftFrame.Snapshots, rightFrame.Snapshots, options, differences);
        }

        CompareEvents(left, right, options, differences);
    }

    private static void CompareSnapshots(
        uint tick,
        IReadOnlyList<ReplayEntityState> left,
        IReadOnlyList<ReplayEntityState> right,
        ReplayDiffOptions options,
        List<ReplayDifference> differences)
    {
        Dictionary<uint, ReplayEntityState> rightByEntity = right.ToDictionary(state => state.EntityId);
        Dictionary<uint, ReplayEntityState> leftByEntity = new();

        foreach (ReplayEntityState leftState in left)
        {
            if (!leftByEntity.TryAdd(leftState.EntityId, leftState))
            {
                differences.Add(Duplicate(tick, leftState.EntityId, "left"));
                continue;
            }

            if (!rightByEntity.TryGetValue(leftState.EntityId, out ReplayEntityState? rightState))
            {
                differences.Add(new ReplayDifference(
                    ReplayDiffKind.EntityMissing, tick, leftState.EntityId, "snapshot", Format(leftState), "missing"));
                continue;
            }

            CompareVector(tick, leftState.EntityId, "position", leftState.Position, rightState.Position, options.PositionTolerance, differences);
            CompareQuaternion(tick, leftState.EntityId, "rotation", leftState.Rotation, rightState.Rotation, options.RotationTolerance, differences);
            CompareVector(tick, leftState.EntityId, "linearVelocity", leftState.LinearVelocity, rightState.LinearVelocity, options.VelocityTolerance, differences);
            CompareVector(tick, leftState.EntityId, "angularVelocity", leftState.AngularVelocity, rightState.AngularVelocity, options.VelocityTolerance, differences);
        }

        foreach (ReplayEntityState rightState in right)
        {
            if (!leftByEntity.ContainsKey(rightState.EntityId))
            {
                differences.Add(new ReplayDifference(
                    ReplayDiffKind.EntityMissing, tick, rightState.EntityId, "snapshot", "missing", Format(rightState)));
            }
        }
    }

    private static void CompareEvents(
        ReplayOutput left,
        ReplayOutput right,
        ReplayDiffOptions options,
        List<ReplayDifference> differences)
    {
        // Right-side event instances are indexed per event key; a left event consumes the
        // nearest unconsumed right instance with the same payload within the tick window.
        Dictionary<string, List<uint>> rightTicksByKey = new();
        foreach (ReplayFrame frame in right.Frames)
        {
            foreach (ReplayEvent rightEvent in frame.Events)
            {
                string key = EventKey(rightEvent);
                if (!rightTicksByKey.TryGetValue(key, out List<uint>? ticks))
                {
                    ticks = new List<uint>();
                    rightTicksByKey.Add(key, ticks);
                }

                ticks.Add(frame.Tick);
            }
        }

        HashSet<string> consumed = new(StringComparer.Ordinal);
        foreach (ReplayFrame frame in left.Frames)
        {
            foreach (ReplayEvent leftEvent in frame.Events)
            {
                if (FindMatch(rightTicksByKey, leftEvent, frame.Tick, options.EventTickTolerance, consumed) == uint.MaxValue)
                {
                    differences.Add(new ReplayDifference(
                        ReplayDiffKind.EventMissing, frame.Tick, leftEvent.EntityId ?? 0, "event", Describe(leftEvent), "missing"));
                }
            }
        }

        foreach (ReplayFrame frame in right.Frames)
        {
            foreach (ReplayEvent rightEvent in frame.Events)
            {
                string key = EventKey(rightEvent);
                if (!rightTicksByKey.TryGetValue(key, out List<uint>? ticks))
                {
                    continue;
                }

                for (int index = 0; index < ticks.Count; index++)
                {
                    if (!consumed.Contains(EventUse(key, ticks[index], index)))
                    {
                        differences.Add(new ReplayDifference(
                            ReplayDiffKind.EventMissing, frame.Tick, rightEvent.EntityId ?? 0, "event", "missing", Describe(rightEvent)));
                        break;
                    }
                }
            }
        }
    }

    private static uint FindMatch(
        Dictionary<string, List<uint>> rightTicksByKey,
        ReplayEvent leftEvent,
        uint tick,
        uint tolerance,
        HashSet<string> consumed)
    {
        string key = EventKey(leftEvent);
        if (!rightTicksByKey.TryGetValue(key, out List<uint>? ticks))
        {
            return uint.MaxValue;
        }

        long bestDistance = long.MaxValue;
        int bestIndex = -1;
        for (int index = 0; index < ticks.Count; index++)
        {
            uint candidate = ticks[index];
            if (consumed.Contains(EventUse(key, candidate, index)))
            {
                continue;
            }

            long distance = Math.Abs((long)candidate - tick);
            if (distance <= tolerance && distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        if (bestIndex < 0)
        {
            return uint.MaxValue;
        }

        consumed.Add(EventUse(key, ticks[bestIndex], bestIndex));
        return ticks[bestIndex];
    }

    private static void CompareFinalResult(ReplayOutput left, ReplayOutput right, List<ReplayDifference> differences)
    {
        if (left.FinalResult.Outcome != right.FinalResult.Outcome)
        {
            differences.Add(new ReplayDifference(
                ReplayDiffKind.FinalOutcome, 0, 0, "outcome",
                left.FinalResult.Outcome.ToString(), right.FinalResult.Outcome.ToString()));
        }

        if (left.FinalResult.CompletedTick != right.FinalResult.CompletedTick)
        {
            differences.Add(new ReplayDifference(
                ReplayDiffKind.FinalCompletedTick, 0, 0, "completedTick",
                left.FinalResult.CompletedTick.ToString(CultureInfo.InvariantCulture),
                right.FinalResult.CompletedTick.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.Equals(left.FinalResult.StateHash, right.FinalResult.StateHash, StringComparison.Ordinal))
        {
            differences.Add(new ReplayDifference(
                ReplayDiffKind.FinalStateHash, 0, 0, "stateHash",
                left.FinalResult.StateHash, right.FinalResult.StateHash));
        }
    }

    private static ReplayDifference Duplicate(uint tick, uint entityId, string side) => new(
        ReplayDiffKind.EntityMissing, tick, entityId, "snapshot", $"{side}:duplicate entity", string.Empty);

    private static void CompareVector(
        uint tick,
        uint entityId,
        string field,
        ReplayVector3 left,
        ReplayVector3 right,
        float tolerance,
        List<ReplayDifference> differences)
    {
        if (Math.Abs(left.X - right.X) > tolerance || Math.Abs(left.Y - right.Y) > tolerance || Math.Abs(left.Z - right.Z) > tolerance)
        {
            differences.Add(new ReplayDifference(
                ReplayDiffKind.SnapshotField, tick, entityId, field, Format(left), Format(right)));
        }
    }

    private static void CompareQuaternion(
        uint tick,
        uint entityId,
        string field,
        ReplayQuaternion left,
        ReplayQuaternion right,
        float tolerance,
        List<ReplayDifference> differences)
    {
        if (Math.Abs(left.X - right.X) > tolerance || Math.Abs(left.Y - right.Y) > tolerance
            || Math.Abs(left.Z - right.Z) > tolerance || Math.Abs(left.W - right.W) > tolerance)
        {
            differences.Add(new ReplayDifference(
                ReplayDiffKind.SnapshotField, tick, entityId, field, Format(left), Format(right)));
        }
    }

    private static ReplayFrame? FindFrame(ReplayOutput output, uint tick)
    {
        foreach (ReplayFrame frame in output.Frames)
        {
            if (frame.Tick == tick)
            {
                return frame;
            }
        }

        return null;
    }

    private static string EventKey(ReplayEvent @event) =>
        FormattableString.Invariant(
            $"{@event.Kind}|{@event.BodyA?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{@event.BodyB?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{@event.JointId?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{@event.EntityId?.ToString(CultureInfo.InvariantCulture) ?? "-"}");

    private static string EventUse(string key, uint tick, int index) =>
        FormattableString.Invariant($"{key}#{tick}#{index}");

    private static string Describe(ReplayEvent @event)
    {
        var parts = new List<string> { @event.Kind.ToString() };
        if (@event.BodyA is uint bodyA)
        {
            parts.Add(FormattableString.Invariant($"bodyA={bodyA}"));
        }

        if (@event.BodyB is uint bodyB)
        {
            parts.Add(FormattableString.Invariant($"bodyB={bodyB}"));
        }

        if (@event.JointId is uint jointId)
        {
            parts.Add(FormattableString.Invariant($"jointId={jointId}"));
        }

        if (@event.EntityId is uint entityId)
        {
            parts.Add(FormattableString.Invariant($"entityId={entityId}"));
        }

        return string.Join(" ", parts);
    }

    private static string Format(ReplayVector3 value) =>
        FormattableString.Invariant($"({value.X:G9}, {value.Y:G9}, {value.Z:G9})");

    private static string Format(ReplayQuaternion value) =>
        FormattableString.Invariant($"({value.X:G9}, {value.Y:G9}, {value.Z:G9}, {value.W:G9})");

    private static string Format(ReplayEntityState state) =>
        FormattableString.Invariant($"entity {state.EntityId} at {Format(state.Position)}");
}
