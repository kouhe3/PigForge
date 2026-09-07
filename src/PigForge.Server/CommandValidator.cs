using PigForge.Core.Construction;
using PigForge.Protocol;

namespace PigForge.Server;

public enum CommandStatus
{
    Accepted,
    Duplicate,
    StaleSequence,
    StaleTick,
    WrongMode,
    RuleRejected,
    UnknownKind
}

public readonly record struct CommandOutcome(ReplayCommand Command, CommandStatus Status, ConstructionError Error, uint EntityId = 0)
{
    public bool IsAccepted => Status == CommandStatus.Accepted;
}

/// <summary>
/// Deterministic command gate: per-player sequence tracking makes duplicate and stale
/// submissions idempotent, and tick/mode checks reject out-of-order or capability-invalid
/// commands. Accepted sequences are consumed even when a later rule rejects the command,
/// so retries cannot smuggle a rejected command past validation twice.
/// </summary>
public sealed class CommandValidator
{
    private readonly Dictionary<uint, uint> _lastSequenceByPlayer = new();

    public CommandStatus Validate(ReplayCommand command, RoomMode mode, uint currentTick)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_lastSequenceByPlayer.TryGetValue(command.PlayerId, out uint lastSequence))
        {
            if (command.Sequence == lastSequence)
            {
                return CommandStatus.Duplicate;
            }

            if (command.Sequence < lastSequence)
            {
                return CommandStatus.StaleSequence;
            }
        }

        CommandStatus modeStatus = ValidateModeAndTick(command, mode, currentTick);
        if (modeStatus != CommandStatus.Accepted)
        {
            return modeStatus;
        }

        _lastSequenceByPlayer[command.PlayerId] = command.Sequence;
        return CommandStatus.Accepted;
    }

    private static CommandStatus ValidateModeAndTick(ReplayCommand command, RoomMode mode, uint currentTick)
    {
        return (mode, command) switch
        {
            (RoomMode.Closed, _) => CommandStatus.WrongMode,
            (RoomMode.Building, StartSimulationCommand) => command.Tick == 0
                ? CommandStatus.Accepted
                : CommandStatus.StaleTick,
            // Build-phase commands are timeless pre-simulation inputs and always carry
            // Tick 0, including in a re-entered building phase (issue #7).
            (RoomMode.Building, PlacePartCommand or RotatePartCommand or RemovePartCommand) => command.Tick == 0
                ? CommandStatus.Accepted
                : CommandStatus.StaleTick,
            (RoomMode.Building, EnterBuildModeCommand) => CommandStatus.WrongMode,
            (RoomMode.Running, EnterBuildModeCommand) => command.Tick == currentTick
                ? CommandStatus.Accepted
                : CommandStatus.StaleTick,
            (RoomMode.Running, StartSimulationCommand or PlacePartCommand or RotatePartCommand or RemovePartCommand) => CommandStatus.WrongMode,
            _ => CommandStatus.UnknownKind
        };
    }
}

public enum RoomMode
{
    Building,
    Running,
    Closed
}
