using PigForge.Replay;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;

namespace PigForge.Replay.Tests;

public sealed class ReplayRunnerTests
{
    [Fact]
    public void RunsTickZeroCommandsBeforeFixedSimulationTicks()
    {
        RecordingReplaySimulation simulation = new();
        ReplayRunner runner = new(simulation);

        ReplayOutput output = runner.Run(ReplayRunnerFixtures.Input());

        Assert.Equal(new uint[] { 0, 2 }, simulation.AppliedCommands.Select(command => command.Tick));
        Assert.Equal(3, simulation.StepCount);
        Assert.Equal(new uint[] { 1, 2, 3 }, output.Frames.Select(frame => frame.Tick));
        Assert.Equal(3u, output.FinalResult.CompletedTick);
        Assert.Equal(
            ReplayStateHasher.Compute(3, output.Frames[^1].Snapshots),
            output.FinalResult.StateHash);
    }

    [Fact]
    public void InvalidInputIsRejectedBeforeSimulationStarts()
    {
        RecordingReplaySimulation simulation = new();
        ReplayRunner runner = new(simulation);
        ReplayInput validInput = ReplayRunnerFixtures.Input();
        ReplayInput input = validInput with
        {
            Header = validInput.Header with { SimulationTicks = 0 }
        };

        Assert.Throws<ReplayValidationException>(() => runner.Run(input));
        Assert.False(simulation.Loaded);
        Assert.Equal(0, simulation.StepCount);
    }

    [Fact]
    public void StateHashIsIndependentOfSnapshotInputOrder()
    {
        ReplayEntityState first = ReplayRunnerFixtures.Entity(1, 1);
        ReplayEntityState second = ReplayRunnerFixtures.Entity(2, 2);

        string forward = ReplayStateHasher.Compute(4, new[] { first, second });
        string reverse = ReplayStateHasher.Compute(4, new[] { second, first });

        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void StateHashCoversEntityScale()
    {
        ReplayEntityState unscaled = ReplayRunnerFixtures.Entity(1, 1);
        ReplayEntityState scaled = ReplayRunnerFixtures.Entity(1, 1) with { Scale = 2f };

        Assert.NotEqual(
            ReplayStateHasher.Compute(4, new[] { unscaled }),
            ReplayStateHasher.Compute(4, new[] { scaled }));
    }

    [Fact]
    public void BepuReplayProducesStableHashAndEventSequenceAcrossRuns()
    {
        ReplayInput input = BepuReplayFixtures.FallingBoxInput();

        ReplayOutput first = BepuReplayFixtures.Run(input);
        ReplayOutput second = BepuReplayFixtures.Run(input);

        Assert.Equal(first.FinalResult.StateHash, second.FinalResult.StateHash);
        Assert.Equal(first.FinalResult.Outcome, second.FinalResult.Outcome);
        Assert.Equal(first.Frames.SelectMany(frame => frame.Events), second.Frames.SelectMany(frame => frame.Events));
        Assert.Contains(
            first.Frames.SelectMany(frame => frame.Events),
            @event => @event.Kind == ReplayEventKind.ContactStarted);
    }

    [Fact]
    public void BepuReplayMapsRemovedBodyToEntityDestroyedEvent()
    {
        ReplayOutput output = BepuReplayFixtures.Run(BepuReplayFixtures.RemovalInput());

        ReplayEvent destroyed = Assert.Single(output.Frames[0].Events);
        Assert.Equal(ReplayEventKind.EntityDestroyed, destroyed.Kind);
        Assert.Equal((uint)2, destroyed.EntityId);
        Assert.Empty(output.Frames[0].Snapshots);
    }

    private sealed class RecordingReplaySimulation : IReplaySimulation
    {
        private ReplayEntityState _entity = ReplayRunnerFixtures.Entity(1, 1);

        public List<ReplayCommand> AppliedCommands { get; } = new();
        public bool Loaded { get; private set; }
        public int StepCount { get; private set; }
        public ReplayOutcome Outcome => ReplayOutcome.Success;

        public void LoadInitialState(ReplayInitialState initialState)
        {
            Loaded = true;
            _entity = initialState.Entities[0];
        }

        public void ApplyCommand(ReplayCommand command)
        {
            AppliedCommands.Add(command);
        }

        public void Step(FixedTimeStep timeStep)
        {
            Assert.Equal(1f / 60f, timeStep.Seconds, precision: 6);
            StepCount++;
            _entity = _entity with
            {
                Position = new ReplayVector3(StepCount, 0, 0)
            };
        }

        public IReadOnlyList<ReplayEntityState> CaptureSnapshots() => new[] { _entity };

        public IReadOnlyList<ReplayEvent> DrainEvents() => Array.Empty<ReplayEvent>();
    }
}

internal static class ReplayRunnerFixtures
{
    public static ReplayInput Input()
    {
        ReplayEntityState entity = Entity(1, 1);
        ReplayHeader header = new(
            ProtocolVersion: ReplayFormat.CurrentVersion,
            ContentVersion: "content-v1",
            PhysicsBehaviorVersion: "bple-legacy-v1",
            StateHashAlgorithm: ReplayHashAlgorithms.Sha256CanonicalV2,
            FixedTickRate: 60,
            SimulationTicks: 3,
            RandomSeed: 1234);

        return new ReplayInput(
            Format: ReplayFormat.Name,
            Header: header,
            InitialState: new ReplayInitialState(new[] { entity }, Array.Empty<ReplayJointState>()),
            Commands: new ReplayCommand[]
            {
                new StartSimulationCommand(Tick: 0, Sequence: 1, PlayerId: 1),
                new StartSimulationCommand(Tick: 2, Sequence: 2, PlayerId: 1)
            });
    }

    public static ReplayEntityState Entity(uint entityId, uint bodyId)
    {
        return new ReplayEntityState(
            EntityId: entityId,
            PhysicsBodyId: bodyId,
            PartTypeId: 1,
            Position: ReplayVector3.Zero,
            Rotation: ReplayQuaternion.Identity,
            LinearVelocity: ReplayVector3.Zero,
            AngularVelocity: ReplayVector3.Zero);
    }
}

internal static class BepuReplayFixtures
{
    public static ReplayOutput Run(ReplayInput input)
    {
        using PhysicsReplaySimulation simulation = new(
            new BepuPhysicsWorld(new PhysicsVector3(0, -9.81f, 0)),
            new BepuContentCatalog());
        return new ReplayRunner(simulation).Run(input);
    }

    public static ReplayInput FallingBoxInput()
    {
        return new ReplayInput(
            ReplayFormat.Name,
            Header(180),
            new ReplayInitialState(
                new[]
                {
                    Entity(1, 1, 1, new ReplayVector3(0, 0, 0)),
                    Entity(2, 2, 2, new ReplayVector3(0, 4, 0))
                },
                Array.Empty<ReplayJointState>()),
            Array.Empty<ReplayCommand>());
    }

    public static ReplayInput RemovalInput()
    {
        return new ReplayInput(
            ReplayFormat.Name,
            Header(1),
            new ReplayInitialState(
                new[] { Entity(2, 2, 2, new ReplayVector3(0, 2, 0)) },
                Array.Empty<ReplayJointState>()),
            new ReplayCommand[]
            {
                new RemovePartCommand(0, 1, 1, 2)
            });
    }

    private static ReplayHeader Header(uint simulationTicks) => new(
        ReplayFormat.CurrentVersion,
        "content-v1",
        "bepu-2.4.0-v1",
        ReplayHashAlgorithms.Sha256CanonicalV2,
        60,
        simulationTicks,
        1234);

    private static ReplayEntityState Entity(uint entityId, uint bodyId, uint partTypeId, ReplayVector3 position) => new(
        entityId,
        bodyId,
        partTypeId,
        position,
        ReplayQuaternion.Identity,
        ReplayVector3.Zero,
        ReplayVector3.Zero);

    private sealed class BepuContentCatalog : IReplayPhysicsContent
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
                    new ShapeDefinition[] { new BoxShapeDefinition(10 * entity.Scale, 0.5f * entity.Scale, 10) }),
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
