using System.Buffers.Binary;
using System.Security.Cryptography;
using PigForge.Physics.Abstractions;
using PigForge.Protocol;

namespace PigForge.Replay;

public interface IReplaySimulation
{
    void LoadInitialState(ReplayInitialState initialState);
    void ApplyCommand(ReplayCommand command);
    void Step(FixedTimeStep timeStep);
    IReadOnlyList<ReplayEntityState> CaptureSnapshots();
    IReadOnlyList<ReplayEvent> DrainEvents();
    ReplayOutcome Outcome { get; }
}

public sealed class ReplayValidationException : Exception
{
    public ReplayValidationException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

public sealed class ReplayRunner
{
    private readonly IReplaySimulation _simulation;

    public ReplayRunner(IReplaySimulation simulation)
    {
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
    }

    public ReplayOutput Run(ReplayInput input)
    {
        ReplayValidationResult inputValidation = ReplayDocumentValidator.Validate(input);
        if (!inputValidation.IsValid)
        {
            throw new ReplayValidationException(inputValidation.Errors);
        }

        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(1f / input.Header.FixedTickRate);
        _simulation.LoadInitialState(input.InitialState);

        IReadOnlyList<ReplayCommand> commands = input.Commands;
        int commandIndex = 0;
        ApplyCommandsForTick(commands, ref commandIndex, 0);

        List<ReplayFrame> frames = new(checked((int)input.Header.SimulationTicks));
        for (uint tick = 1; tick <= input.Header.SimulationTicks; tick++)
        {
            ApplyCommandsForTick(commands, ref commandIndex, tick);
            _simulation.Step(timeStep);

            IReadOnlyList<ReplayEntityState> snapshots = _simulation.CaptureSnapshots();
            IReadOnlyList<ReplayEvent> events = _simulation.DrainEvents();
            if (snapshots is null || events is null)
            {
                throw new InvalidOperationException("Replay simulation returned a null snapshot or event list.");
            }

            frames.Add(new ReplayFrame(
                tick,
                snapshots.ToArray(),
                events.ToArray()));
        }

        ReplayFrame finalFrame = frames[^1];
        ReplayResult finalResult = new(
            _simulation.Outcome,
            input.Header.SimulationTicks,
            ReplayStateHasher.Compute(input.Header.SimulationTicks, finalFrame.Snapshots));
        ReplayOutput output = new(frames, finalResult);

        ReplayDocument document = new(
            input.Format,
            input.Header,
            input.InitialState,
            input.Commands,
            output.Frames,
            output.FinalResult);
        ReplayValidationResult outputValidation = ReplayDocumentValidator.Validate(document);
        if (!outputValidation.IsValid)
        {
            throw new ReplayValidationException(outputValidation.Errors);
        }

        return output;
    }

    private void ApplyCommandsForTick(
        IReadOnlyList<ReplayCommand> commands,
        ref int commandIndex,
        uint tick)
    {
        while (commandIndex < commands.Count && commands[commandIndex].Tick == tick)
        {
            _simulation.ApplyCommand(commands[commandIndex]);
            commandIndex++;
        }
    }
}

public static class ReplayStateHasher
{
    private static ReadOnlySpan<byte> HashPrefix => "pigforge.state.v1\0"u8;

    public static string Compute(uint completedTick, IReadOnlyList<ReplayEntityState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        ReplayEntityState[] orderedStates = states.ToArray();
        Array.Sort(orderedStates, static (left, right) => left.EntityId.CompareTo(right.EntityId));

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(HashPrefix);
        Span<byte> scratch = stackalloc byte[4];
        AppendUInt32(hash, scratch, completedTick);
        AppendUInt32(hash, scratch, checked((uint)orderedStates.Length));

        foreach (ReplayEntityState state in orderedStates)
        {
            AppendUInt32(hash, scratch, state.EntityId);
            AppendUInt32(hash, scratch, state.PhysicsBodyId);
            AppendUInt32(hash, scratch, state.PartTypeId);
            AppendVector3(hash, scratch, state.Position);
            AppendQuaternion(hash, scratch, state.Rotation);
            AppendVector3(hash, scratch, state.LinearVelocity);
            AppendVector3(hash, scratch, state.AngularVelocity);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendVector3(
        IncrementalHash hash,
        Span<byte> scratch,
        ReplayVector3 value)
    {
        AppendSingle(hash, scratch, value.X);
        AppendSingle(hash, scratch, value.Y);
        AppendSingle(hash, scratch, value.Z);
    }

    private static void AppendQuaternion(
        IncrementalHash hash,
        Span<byte> scratch,
        ReplayQuaternion value)
    {
        AppendSingle(hash, scratch, value.X);
        AppendSingle(hash, scratch, value.Y);
        AppendSingle(hash, scratch, value.Z);
        AppendSingle(hash, scratch, value.W);
    }

    private static void AppendSingle(
        IncrementalHash hash,
        Span<byte> scratch,
        float value)
    {
        AppendUInt32(hash, scratch, unchecked((uint)BitConverter.SingleToInt32Bits(value)));
    }

    private static void AppendUInt32(
        IncrementalHash hash,
        Span<byte> scratch,
        uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, value);
        hash.AppendData(scratch);
    }
}
