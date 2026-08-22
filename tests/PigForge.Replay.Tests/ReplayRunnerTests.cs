using PigForge.Replay;
using PigForge.Physics.Abstractions;
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

        public IReadOnlyList<ReplayEntityState> CaptureSnapshots()
        {
            return new[] { _entity };
        }

        public IReadOnlyList<ReplayEvent> DrainEvents()
        {
            return Array.Empty<ReplayEvent>();
        }
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
            StateHashAlgorithm: ReplayHashAlgorithms.Sha256CanonicalV1,
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
