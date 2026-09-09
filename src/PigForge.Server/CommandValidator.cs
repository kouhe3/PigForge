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
/// so retries cannot smuggle a rejected command past validation twice. In sandbox mode
/// the tick field is ignored and gating is per-player: editing players may place,
/// remove, rotate, move, scale, start, or reset; a materialised player may only reset.
/// </summary>
public sealed class CommandValidator
{
    private readonly Dictionary<uint, uint> _lastSequenceByPlayer = new();

    public CommandStatus Validate(ReplayCommand command, RoomMode mode, uint currentTick) =>
        Validate(command, mode, currentTick, sandboxMode: false, materialized: false);

    public CommandStatus Validate(ReplayCommand command, RoomMode mode, uint currentTick, bool sandboxMode, bool materialized)
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

        CommandStatus modeStatus = sandboxMode
            ? ValidateSandboxMode(command, materialized)
            : ValidateModeAndTick(command, mode, currentTick);
        if (modeStatus != CommandStatus.Accepted)
        {
            return modeStatus;
        }

        _lastSequenceByPlayer[command.PlayerId] = command.Sequence;
        return CommandStatus.Accepted;
    }

    private static CommandStatus ValidateSandboxMode(ReplayCommand command, bool materialized)
    {
        if (materialized)
        {
            return command switch
            {
                RetryCommand => CommandStatus.Accepted,
                PlacePartCommand or RemovePartCommand or RotatePartCommand or MovePartCommand or ScalePartCommand or StartSimulationCommand or EnterBuildModeCommand => CommandStatus.WrongMode,
                _ => CommandStatus.UnknownKind
            };
        }

        return command switch
        {
            PlacePartCommand or RemovePartCommand or RotatePartCommand or MovePartCommand or ScalePartCommand or StartSimulationCommand or RetryCommand => CommandStatus.Accepted,
            EnterBuildModeCommand => CommandStatus.WrongMode,
            _ => CommandStatus.UnknownKind
        };
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
            (RoomMode.Building, PlacePartCommand or RotatePartCommand or RemovePartCommand or MovePartCommand or ScalePartCommand) => command.Tick == 0
                ? CommandStatus.Accepted
                : CommandStatus.StaleTick,
            (RoomMode.Building, EnterBuildModeCommand) => CommandStatus.WrongMode,
            (RoomMode.Running, EnterBuildModeCommand) => command.Tick == currentTick
                ? CommandStatus.Accepted
                : CommandStatus.StaleTick,
            // Retry is a single deferred "return to build" command: the sequence gate
            // already orders it, so a stale-tick lag from the client's last snapshot
            // must not reject it. Only a "future" tick (ahead of the room) is refused.
            (RoomMode.Running, RetryCommand) => command.Tick <= currentTick
                ? CommandStatus.Accepted
                : CommandStatus.StaleTick,
            (RoomMode.Running, StartSimulationCommand or PlacePartCommand or RotatePartCommand or RemovePartCommand or MovePartCommand or ScalePartCommand) => CommandStatus.WrongMode,
            (RoomMode.Building, RetryCommand) => CommandStatus.WrongMode,
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
