using PigForge.Physics.Abstractions;
using PigForge.Protocol;
using PigForge.Physics.Jolt;
using PigForge.Replay;

namespace PigForge.Replay.Tests;

public sealed class ReplayDiffTests
{
    [Fact]
    public void IdenticalOutputsProduceEmptyReport()
    {
        ReplayOutput output = DiffFixtures.Output(3, positionX: 1, contactTick: 2);

        ReplayDiffReport report = ReplayComparer.Compare(output, output);

        Assert.True(report.IsEmpty, string.Join(Environment.NewLine, report.Differences.Select(d => d.ToString())));
    }

    [Fact]
    public void ReportAnchorsSnapshotDifferencesToTickAndEntity()
    {
        ReplayOutput left = DiffFixtures.Output(3, positionX: 1, contactTick: 2);
        ReplayOutput right = DiffFixtures.Output(3, positionX: 1.5f, contactTick: 2);

        ReplayDiffReport report = ReplayComparer.Compare(left, right);

        Assert.Contains(report.Differences, diff =>
            diff.Kind == ReplayDiffKind.SnapshotField
            && diff.Tick == 2
            && diff.EntityId == 7
            && diff.Field == "position");
        Assert.Equal(ReplayDiffKind.FinalStateHash, report.Differences[^1].Kind);
    }

    [Fact]
    public void ReportFlagsMissingEntitiesEventsAndFinalResult()
    {
        ReplayOutput left = DiffFixtures.Output(3, positionX: 1, contactTick: 2);
        ReplayOutput right = DiffFixtures.Output(3, positionX: 1, contactTick: 2);
        right = DiffFixtures.WithMutations(right,
            dropEntityAtTick: 3,
            extraRightEventAtTick: 1,
            outcome: ReplayOutcome.Failure);

        ReplayDiffReport report = ReplayComparer.Compare(left, right);

        Assert.Contains(report.Differences, diff => diff.Kind == ReplayDiffKind.EntityMissing && diff.Tick == 3 && diff.EntityId == 7);
        Assert.Contains(report.Differences, diff => diff.Kind == ReplayDiffKind.EventMissing && diff.Tick == 1 && diff.Left == "missing");
        Assert.Contains(report.Differences, diff => diff.Kind == ReplayDiffKind.FinalOutcome && diff.Right == "Failure");
    }

    [Fact]
    public void EventTickToleranceAbsorbsSmallTimingDrift()
    {
        ReplayOutput left = DiffFixtures.Output(3, positionX: 1, contactTick: 2);
        ReplayOutput right = DiffFixtures.Output(3, positionX: 1, contactTick: 3);

        ReplayDiffReport strict = ReplayComparer.Compare(left, right, new ReplayDiffOptions { EventTickTolerance = 0 });
        ReplayDiffReport tolerant = ReplayComparer.Compare(left, right, new ReplayDiffOptions { EventTickTolerance = 1 });

        Assert.Contains(strict.Differences, diff => diff.Kind == ReplayDiffKind.EventMissing);
        Assert.DoesNotContain(tolerant.Differences, diff => diff.Kind == ReplayDiffKind.EventMissing);
    }

    [Fact]
    public void BepuAndJoltReplaysAgreeAtEventLevelOnSimpleScene()
    {
        ReplayInput input = BepuReplayFixtures.FallingBoxInput();
        ReplayOutput bepu = BepuReplayFixtures.Run(input);
        ReplayOutput jolt = DiffFixtures.Run(input, new PhysicsVector3(0, -9.81f, 0));

        // Event-level comparison: the contract-relevant first contact must align within the
        // tick window; genuine solver divergence (bouncing vs settling) is reported, not hidden.
        ReplayDiffReport report = ReplayComparer.Compare(bepu, jolt, new ReplayDiffOptions
        {
            EventTickTolerance = 10,
            PositionTolerance = 0.05f,
            RotationTolerance = 0.05f,
            VelocityTolerance = 0.25f
        });

        Assert.DoesNotContain(report.Differences, diff =>
            diff.Kind == ReplayDiffKind.EventMissing
            && (diff.Left.Contains("ContactStarted") || diff.Right.Contains("ContactStarted")));
        Assert.Equal(bepu.FinalResult.Outcome, jolt.FinalResult.Outcome);
        Assert.Equal(bepu.FinalResult.CompletedTick, jolt.FinalResult.CompletedTick);
        uint bepuContactTick = FirstContactTick(bepu);
        uint joltContactTick = FirstContactTick(jolt);
        Assert.True(Math.Abs((long)bepuContactTick - joltContactTick) <= 10,
            $"Contact timing drift {bepuContactTick} vs {joltContactTick} exceeds the tolerance window.");

        // The report anchors the expected physical divergence by tick and entity.
        Assert.Contains(report.Differences, diff => diff.Kind == ReplayDiffKind.SnapshotField || diff.Kind == ReplayDiffKind.FinalStateHash);
    }

    private static uint FirstContactTick(ReplayOutput output)
    {
        foreach (ReplayFrame frame in output.Frames)
        {
            if (frame.Events.Any(@event => @event.Kind == ReplayEventKind.ContactStarted))
            {
                return frame.Tick;
            }
        }

        return uint.MaxValue;
    }
}

internal static class DiffFixtures
{
    public static ReplayOutput Output(uint ticks, float positionX, uint contactTick)
    {
        ReplayEntityState entity = Entity(positionX);
        List<ReplayFrame> frames = new();
        for (uint tick = 1; tick <= ticks; tick++)
        {
            ReplayEvent[] events = tick == contactTick
                ? new[] { new ReplayEvent(ReplayEventKind.ContactStarted, BodyA: 1, BodyB: 2) }
                : Array.Empty<ReplayEvent>();
            frames.Add(new ReplayFrame(
                tick,
                new[] { entity with { Position = new ReplayVector3(positionX, 0, 0) } },
                events));
        }

        ReplayResult finalResult = new(
            ReplayOutcome.Success,
            ticks,
            ReplayStateHasher.Compute(ticks, frames[^1].Snapshots));
        return new ReplayOutput(frames, finalResult);
    }

    public static ReplayOutput WithMutations(
        ReplayOutput output,
        uint dropEntityAtTick,
        uint extraRightEventAtTick,
        ReplayOutcome outcome)
    {
        List<ReplayFrame> frames = new();
        foreach (ReplayFrame frame in output.Frames)
        {
            if (frame.Tick == dropEntityAtTick)
            {
                frames.Add(frame with { Snapshots = Array.Empty<ReplayEntityState>() });
                continue;
            }

            if (frame.Tick == extraRightEventAtTick)
            {
                frames.Add(frame with { Events = new[] { new ReplayEvent(ReplayEventKind.ContactEnded, BodyA: 3, BodyB: 4) } });
                continue;
            }

            frames.Add(frame);
        }

        return output with { Frames = frames, FinalResult = output.FinalResult with { Outcome = outcome } };
    }

    public static ReplayOutput Run(ReplayInput input, PhysicsVector3 gravity)
    {
        using PhysicsReplaySimulation simulation = new(new JoltPhysicsWorld(gravity), new JoltContentCatalog());
        return new ReplayRunner(simulation).Run(input);
    }

    private static ReplayEntityState Entity(float positionX) => new(
        EntityId: 7,
        PhysicsBodyId: 1,
        PartTypeId: 1,
        Position: new ReplayVector3(positionX, 0, 0),
        Rotation: ReplayQuaternion.Identity,
        LinearVelocity: ReplayVector3.Zero,
        AngularVelocity: ReplayVector3.Zero);

    private sealed class JoltContentCatalog : IReplayPhysicsContent
    {
        public BodyDefinition CreateBody(ReplayEntityState entity)
        {
            return entity.PartTypeId switch
            {
                1 => new BodyDefinition(
                    PhysicsBodyMode.Static,
                    ToPhysics(entity.Position),
                    ToPhysics(entity.Rotation),
                    0,
                    new ShapeDefinition[] { new BoxShapeDefinition(10, 0.5f, 10) }),
                2 => new BodyDefinition(
                    PhysicsBodyMode.Dynamic,
                    ToPhysics(entity.Position),
                    ToPhysics(entity.Rotation),
                    1,
                    new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) },
                    ToPhysics(entity.LinearVelocity),
                    ToPhysics(entity.AngularVelocity)),
                _ => throw new NotSupportedException($"Unknown test part type {entity.PartTypeId}.")
            };
        }

        private static PhysicsVector3 ToPhysics(ReplayVector3 value) => new(value.X, value.Y, value.Z);

        private static PhysicsQuaternion ToPhysics(ReplayQuaternion value) => new(value.X, value.Y, value.Z, value.W);
    }
}
