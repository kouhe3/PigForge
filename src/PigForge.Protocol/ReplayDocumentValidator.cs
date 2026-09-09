namespace PigForge.Protocol;

public static class ReplayDocumentValidator
{
    public static ReplayValidationResult Validate(ReplayDocument? document)
    {
        if (document is null)
        {
            return new ReplayValidationResult(new[] { "ReplayDocument is required." });
        }

        List<string> errors = new();
        ValidateCommon(document.Format, document.Header, document.InitialState, document.Commands, errors);
        ValidateFrames(document.Frames, document.Header?.SimulationTicks, errors);
        ValidateFinalResult(document.FinalResult, errors);

        if (document.Frames is { Count: > 0 } && document.FinalResult is not null &&
            document.Frames[^1] is ReplayFrame lastFrame &&
            document.FinalResult.CompletedTick < lastFrame.Tick)
        {
            errors.Add("FinalResult.CompletedTick cannot precede the last replay frame.");
        }

        return new ReplayValidationResult(errors);
    }

    public static ReplayValidationResult Validate(ReplayInput? input)
    {
        if (input is null)
        {
            return new ReplayValidationResult(new[] { "ReplayInput is required." });
        }

        List<string> errors = new();
        ValidateCommon(input.Format, input.Header, input.InitialState, input.Commands, errors);
        return new ReplayValidationResult(errors);
    }

    private static void ValidateCommon(
        string? format,
        ReplayHeader? header,
        ReplayInitialState? initialState,
        IReadOnlyList<ReplayCommand>? commands,
        ICollection<string> errors)
    {
        if (!string.Equals(format, ReplayFormat.Name, StringComparison.Ordinal))
        {
            errors.Add($"Format must be '{ReplayFormat.Name}'.");
        }

        ValidateHeader(header, errors);
        ValidateInitialState(initialState, errors);
        ValidateCommands(commands, header?.SimulationTicks, errors);
    }

    private static void ValidateHeader(ReplayHeader? header, ICollection<string> errors)
    {
        if (header is null)
        {
            errors.Add("Header is required.");
            return;
        }

        if (header.ProtocolVersion != ReplayFormat.CurrentVersion)
        {
            errors.Add($"ProtocolVersion {header.ProtocolVersion} is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(header.ContentVersion))
        {
            errors.Add("ContentVersion is required.");
        }

        if (string.IsNullOrWhiteSpace(header.PhysicsBehaviorVersion))
        {
            errors.Add("PhysicsBehaviorVersion is required.");
        }

        if (!string.Equals(header.StateHashAlgorithm, ReplayHashAlgorithms.Sha256CanonicalV2, StringComparison.Ordinal))
        {
            errors.Add($"StateHashAlgorithm must be '{ReplayHashAlgorithms.Sha256CanonicalV2}'.");
        }

        if (header.FixedTickRate is < 1 or > 240)
        {
            errors.Add("FixedTickRate must be between 1 and 240.");
        }

        if (header.SimulationTicks is 0 or > ReplayFormat.MaxSimulationTicks)
        {
            errors.Add($"SimulationTicks must be between 1 and {ReplayFormat.MaxSimulationTicks}.");
        }
    }

    private static void ValidateInitialState(ReplayInitialState? initialState, ICollection<string> errors)
    {
        if (initialState is null)
        {
            errors.Add("InitialState is required.");
            return;
        }

        HashSet<uint> bodyIds = ValidateEntityStates(initialState.Entities, "InitialState.Entities", errors);
        ValidateJoints(initialState.Joints, bodyIds, errors);
    }

    private static void ValidateCommands(
        IReadOnlyList<ReplayCommand>? commands,
        uint? simulationTicks,
        ICollection<string> errors)
    {
        if (commands is null)
        {
            errors.Add("Commands is required.");
            return;
        }

        bool hasPrevious = false;
        uint previousTick = 0;
        uint previousSequence = 0;

        foreach (ReplayCommand? command in commands)
        {
            if (command is null)
            {
                errors.Add("Commands cannot contain null entries.");
                continue;
            }

            if (command.Sequence == 0)
            {
                errors.Add("Command sequence must be positive.");
            }

            if (command.PlayerId == 0)
            {
                errors.Add("Command player ID must be positive.");
            }

            if (simulationTicks.HasValue && command.Tick > simulationTicks.Value)
            {
                errors.Add($"Command sequence {command.Sequence} is outside SimulationTicks.");
            }

            if (hasPrevious && (command.Tick < previousTick ||
                                 command.Tick == previousTick && command.Sequence <= previousSequence))
            {
                errors.Add("Commands must use increasing command order by Tick and Sequence.");
            }

            ClientCommandKind? expectedKind = command switch
            {
                PlacePartCommand => ClientCommandKind.PlacePart,
                RemovePartCommand => ClientCommandKind.RemovePart,
                RotatePartCommand => ClientCommandKind.RotatePart,
                MovePartCommand => ClientCommandKind.MovePart,
                ScalePartCommand => ClientCommandKind.ScalePart,
                StartSimulationCommand => ClientCommandKind.StartSimulation,
                EnterBuildModeCommand => ClientCommandKind.EnterBuildMode,
                RetryCommand => ClientCommandKind.Retry,
                SetPartActiveCommand => ClientCommandKind.SetPartActive,
                SetPartTypeActiveCommand => ClientCommandKind.SetPartTypeActive,
                _ => null
            };

            if (expectedKind is null || command.Kind != expectedKind.Value)
            {
                errors.Add("Command kind does not match its command type.");
            }

            if (command is PlacePartCommand placePart && (placePart.PartTypeId == 0
                || !float.IsFinite(placePart.PositionX)
                || !float.IsFinite(placePart.PositionY)
                || !float.IsFinite(placePart.Angle)
                || !float.IsFinite(placePart.Scale)
                || placePart.Scale is <= 0f or > 4f))
            {
                errors.Add("PlacePartCommand has invalid part type, position, angle or scale.");
            }

            if (command is RotatePartCommand rotatePart && (rotatePart.EntityId == 0 || !float.IsFinite(rotatePart.Angle)))
            {
                errors.Add("RotatePartCommand has invalid entity or angle.");
            }

            if (command is MovePartCommand movePart && (movePart.EntityId == 0
                || !float.IsFinite(movePart.PositionX)
                || !float.IsFinite(movePart.PositionY)))
            {
                errors.Add("MovePartCommand has invalid entity or position.");
            }

            if (command is ScalePartCommand scalePart && (scalePart.EntityId == 0
                || !float.IsFinite(scalePart.Scale)
                || scalePart.Scale is <= 0f or > 4f))
            {
                errors.Add("ScalePartCommand has invalid entity or scale.");
            }

            if (command is SetPartActiveCommand setPartActive && setPartActive.EntityId == 0)
            {
                errors.Add("SetPartActiveCommand entity ID must be positive.");
            }

            if (command is SetPartTypeActiveCommand setPartTypeActive && setPartTypeActive.PartTypeId == 0)
            {
                errors.Add("SetPartTypeActiveCommand part type ID must be positive.");
            }

            if (command is RemovePartCommand removePart && removePart.EntityId == 0)
            {
                errors.Add("RemovePartCommand entity ID must be positive.");
            }

            if (command is EnterBuildModeCommand enterBuildMode && !Enum.IsDefined(enterBuildMode.Policy))
            {
                errors.Add("EnterBuildModeCommand has an invalid policy.");
            }

            previousTick = command.Tick;
            previousSequence = command.Sequence;
            hasPrevious = true;
        }
    }

    private static void ValidateFrames(
        IReadOnlyList<ReplayFrame>? frames,
        uint? simulationTicks,
        ICollection<string> errors)
    {
        if (frames is null)
        {
            errors.Add("Frames is required.");
            return;
        }

        if (simulationTicks.HasValue && frames.Count != simulationTicks.Value)
        {
            errors.Add("Frames count must equal SimulationTicks.");
        }

        bool hasPrevious = false;
        uint previousTick = 0;
        uint expectedTick = 1;

        foreach (ReplayFrame? frame in frames)
        {
            if (frame is null)
            {
                errors.Add("Frames cannot contain null entries.");
                expectedTick++;
                continue;
            }

            if (frame.Tick == 0 || hasPrevious && frame.Tick <= previousTick)
            {
                errors.Add("Frames must use strictly increasing positive Tick values.");
            }

            if (frame.Tick != expectedTick)
            {
                errors.Add($"Frame Tick {frame.Tick} is not the expected Tick {expectedTick}.");
            }

            ValidateEntityStates(frame.Snapshots, $"Frame[{frame.Tick}].Snapshots", errors);
            ValidateEvents(frame.Events, frame.Tick, errors);

            previousTick = frame.Tick;
            expectedTick++;
            hasPrevious = true;
        }
    }

    private static void ValidateEvents(IReadOnlyList<ReplayEvent>? events, uint tick, ICollection<string> errors)
    {
        if (events is null)
        {
            errors.Add($"Frame[{tick}].Events is required.");
            return;
        }

        foreach (ReplayEvent? @event in events)
        {
            if (@event is null)
            {
                errors.Add($"Frame[{tick}].Events cannot contain null entries.");
                continue;
            }

            if (!Enum.IsDefined(@event.Kind))
            {
                errors.Add($"Frame[{tick}] contains an invalid event kind.");
                continue;
            }

            if ((@event.Kind is ReplayEventKind.ContactStarted or ReplayEventKind.ContactPersisted or ReplayEventKind.ContactEnded) &&
                (@event.BodyA is null or 0 || @event.BodyB is null or 0))
            {
                errors.Add($"Frame[{tick}] contact events require two body IDs.");
            }

            if (@event.Kind == ReplayEventKind.JointBroken && (@event.JointId is null or 0))
            {
                errors.Add($"Frame[{tick}] joint break events require a joint ID.");
            }

            if ((@event.Kind is ReplayEventKind.EntityCreated or ReplayEventKind.EntityDestroyed) &&
                (@event.EntityId is null or 0))
            {
                errors.Add($"Frame[{tick}] entity lifecycle events require an entity ID.");
            }
        }
    }

    private static void ValidateFinalResult(ReplayResult? finalResult, ICollection<string> errors)
    {
        if (finalResult is null)
        {
            errors.Add("FinalResult is required.");
            return;
        }

        if (!Enum.IsDefined(finalResult.Outcome))
        {
            errors.Add("FinalResult.Outcome is invalid.");
        }

        if (!IsSha256Hex(finalResult.StateHash))
        {
            errors.Add("FinalResult.StateHash must contain exactly 64 hexadecimal characters.");
        }
    }

    private static HashSet<uint> ValidateEntityStates(
        IReadOnlyList<ReplayEntityState>? states,
        string path,
        ICollection<string> errors)
    {
        HashSet<uint> bodyIds = new();

        if (states is null)
        {
            errors.Add($"{path} is required.");
            return bodyIds;
        }

        HashSet<uint> entityIds = new();

        foreach (ReplayEntityState? state in states)
        {
            if (state is null)
            {
                errors.Add($"{path} cannot contain null entries.");
                continue;
            }

            if (state.EntityId == 0 || !entityIds.Add(state.EntityId))
            {
                errors.Add($"{path} contains a duplicate or invalid EntityId.");
            }

            if (state.PhysicsBodyId == 0 || !bodyIds.Add(state.PhysicsBodyId))
            {
                errors.Add($"{path} contains a duplicate or invalid PhysicsBodyId.");
            }

            if (state.PartTypeId == 0)
            {
                errors.Add($"{path} contains an entity without a PartTypeId.");
            }

            if (!AreFinite(state.Position, state.Rotation, state.LinearVelocity, state.AngularVelocity))
            {
                errors.Add($"{path} contains a non-finite physical value.");
            }

            if (!float.IsFinite(state.Scale) || state.Scale <= 0)
            {
                errors.Add($"{path} contains an invalid entity scale.");
            }
        }

        return bodyIds;
    }

    private static void ValidateJoints(
        IReadOnlyList<ReplayJointState>? joints,
        IReadOnlySet<uint> bodyIds,
        ICollection<string> errors)
    {
        if (joints is null)
        {
            errors.Add("InitialState.Joints is required.");
            return;
        }

        HashSet<uint> jointIds = new();

        foreach (ReplayJointState? joint in joints)
        {
            if (joint is null)
            {
                errors.Add("InitialState.Joints cannot contain null entries.");
                continue;
            }

            if (joint.JointId == 0 || !jointIds.Add(joint.JointId))
            {
                errors.Add("InitialState.Joints contains a duplicate or invalid JointId.");
            }

            if (joint.BodyA == 0 || !bodyIds.Contains(joint.BodyA) ||
                joint.BodyB == 0 || !bodyIds.Contains(joint.BodyB) || joint.BodyA == joint.BodyB)
            {
                errors.Add($"Joint {joint.JointId} references invalid or identical bodies.");
            }

            if (!Enum.IsDefined(joint.Kind))
            {
                errors.Add($"Joint {joint.JointId} has an invalid Kind.");
            }

            if (!float.IsFinite(joint.BreakForce) || joint.BreakForce < 0 ||
                !float.IsFinite(joint.BreakTorque) || joint.BreakTorque < 0)
            {
                errors.Add($"Joint {joint.JointId} has invalid break limits.");
            }
        }
    }

    private static bool AreFinite(
        ReplayVector3 position,
        ReplayQuaternion rotation,
        ReplayVector3 linearVelocity,
        ReplayVector3 angularVelocity)
    {
        return IsFinite(position) && IsFinite(rotation) &&
               IsFinite(linearVelocity) && IsFinite(angularVelocity);
    }

    private static bool IsFinite(ReplayVector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(ReplayQuaternion value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) && float.IsFinite(value.W);
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool isDigit = character is >= '0' and <= '9';
            bool isLowerHex = character is >= 'a' and <= 'f';
            bool isUpperHex = character is >= 'A' and <= 'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }
}
