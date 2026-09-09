using PigForge.Protocol;

namespace PigForge.Protocol.Tests;

public sealed class ReplayContractTests
{
    [Fact]
    public void ValidReplayDocumentPassesValidation()
    {
        ReplayDocument document = ReplayFixtures.Valid();

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(ReplayFormat.CurrentVersion, document.Header.ProtocolVersion);
        Assert.Equal(ReplayFormat.Name, document.Format);
    }

    [Fact]
    public void UnsupportedProtocolVersionIsRejected()
    {
        ReplayDocument valid = ReplayFixtures.Valid();
        ReplayDocument document = valid with
        {
            Header = valid.Header with { ProtocolVersion = 99 }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.Contains(result.Errors, error => error.Contains("ProtocolVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void V1DocumentsAreRejectedByV2()
    {
        ReplayDocument valid = ReplayFixtures.Valid();
        ReplayDocument document = valid with
        {
            Header = valid.Header with { ProtocolVersion = 1 }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.Contains(result.Errors, error => error.Contains("ProtocolVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void SimulationTickLimitIsEnforced()
    {
        ReplayDocument valid = ReplayFixtures.Valid();
        ReplayDocument document = valid with
        {
            Header = valid.Header with { SimulationTicks = ReplayFormat.MaxSimulationTicks + 1 }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.Contains(result.Errors, error => error.Contains("SimulationTicks", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandsMustBeOrderedByTickAndSequence()
    {
        ReplayDocument document = ReplayFixtures.Valid() with
        {
            Commands = new ReplayCommand[]
            {
                new StartSimulationCommand(Tick: 2, Sequence: 2, PlayerId: 1),
                new StartSimulationCommand(Tick: 1, Sequence: 1, PlayerId: 1)
            }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.Contains(result.Errors, error => error.Contains("command order", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void JointMustReferenceTwoKnownBodies()
    {
        ReplayDocument valid = ReplayFixtures.Valid();
        ReplayDocument document = valid with
        {
            InitialState = valid.InitialState with
            {
                Joints = new[]
                {
                    new ReplayJointState(
                        JointId: 1,
                        BodyA: 1,
                        BodyB: 999,
                        Kind: ReplayJointKind.Fixed,
                        Constraints: 0,
                        BreakForce: 100,
                        BreakTorque: 100)
                }
            }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.Contains(result.Errors, error => error.Contains("invalid or identical bodies", StringComparison.Ordinal));
    }

    [Fact]
    public void NullLastFrameIsReportedWithoutThrowing()
    {
        ReplayDocument valid = ReplayFixtures.Valid();
        ReplayDocument document = valid with
        {
            Frames = new ReplayFrame[] { null! }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.Contains(result.Errors, error => error.Contains("null entries", StringComparison.Ordinal));
    }

    [Fact]
    public void NonFinitePhysicalValuesAreRejected()
    {
        ReplayDocument valid = ReplayFixtures.Valid();
        ReplayEntityState invalidEntity = valid.InitialState.Entities[0] with
        {
            Position = new ReplayVector3(float.NaN, 0, 0)
        };
        ReplayDocument document = valid with
        {
            InitialState = valid.InitialState with
            {
                Entities = new[] { invalidEntity }
            }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.Contains(result.Errors, error => error.Contains("non-finite", StringComparison.Ordinal));
    }

    [Fact]
    public void FinalStateHashMustBeSha256Hex()
    {
        ReplayDocument valid = ReplayFixtures.Valid();
        ReplayDocument document = valid with
        {
            FinalResult = valid.FinalResult with { StateHash = "invalid" }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.Contains(result.Errors, error => error.Contains("StateHash", StringComparison.Ordinal));
    }

    [Fact]
    public void EnterBuildModeCommandPassesValidation()
    {
        ReplayDocument document = ReplayFixtures.Valid() with
        {
            Commands = new ReplayCommand[]
            {
                new StartSimulationCommand(Tick: 1, Sequence: 1, PlayerId: 1),
                new EnterBuildModeCommand(Tick: 1, Sequence: 2, PlayerId: 1, Policy: BuildModePolicy.Keep)
            }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void EnterBuildModeCommandRejectsUndefinedPolicy()
    {
        ReplayDocument document = ReplayFixtures.Valid() with
        {
            Commands = new ReplayCommand[]
            {
                new EnterBuildModeCommand(Tick: 1, Sequence: 1, PlayerId: 1, Policy: (BuildModePolicy)42)
            }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.Contains(result.Errors, error => error.Contains("EnterBuildModeCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void MoveAndScaleCommandsPassValidation()
    {
        ReplayDocument document = ReplayFixtures.Valid() with
        {
            Commands = new ReplayCommand[]
            {
                new MovePartCommand(Tick: 1, Sequence: 1, PlayerId: 1, EntityId: 1, PositionX: 2f, PositionY: 3f),
                new ScalePartCommand(Tick: 1, Sequence: 2, PlayerId: 1, EntityId: 1, Scale: 2f)
            }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void MoveAndScaleCommandsRejectInvalidFields()
    {
        ReplayDocument document = ReplayFixtures.Valid() with
        {
            Commands = new ReplayCommand[]
            {
                new MovePartCommand(Tick: 1, Sequence: 1, PlayerId: 1, EntityId: 0, PositionX: float.NaN, PositionY: 0f),
                new ScalePartCommand(Tick: 1, Sequence: 2, PlayerId: 1, EntityId: 1, Scale: 5f)
            }
        };

        ReplayValidationResult result = ReplayDocumentValidator.Validate(document);

        Assert.Contains(result.Errors, error => error.Contains("MovePartCommand", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("ScalePartCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void SwitchCommandsValidateAndZeroIdsAreRejected()
    {
        ReplayDocument valid = ReplayFixtures.Valid();
        ReplayCommand[] commands =
        {
            new SetPartActiveCommand(Tick: 1, Sequence: 1, PlayerId: 1, EntityId: 9, Active: true),
            new SetPartTypeActiveCommand(Tick: 1, Sequence: 2, PlayerId: 1, PartTypeId: 11, Active: false)
        };

        ReplayValidationResult accepted = ReplayDocumentValidator.Validate(valid with { Commands = commands });
        Assert.True(accepted.IsValid, string.Join(Environment.NewLine, accepted.Errors));

        ReplayValidationResult rejected = ReplayDocumentValidator.Validate(valid with
        {
            Commands = new ReplayCommand[] { new SetPartActiveCommand(1, 1, 1, EntityId: 0, Active: true) }
        });
        Assert.Contains(rejected.Errors, error => error.Contains("SetPartActive", StringComparison.Ordinal));
    }
}

internal static class ReplayFixtures
{
    public static ReplayDocument Valid()
    {
        ReplayEntityState entity = new(
            EntityId: 1,
            PhysicsBodyId: 1,
            PartTypeId: 1,
            Position: new ReplayVector3(0, 1, 0),
            Rotation: ReplayQuaternion.Identity,
            LinearVelocity: ReplayVector3.Zero,
            AngularVelocity: ReplayVector3.Zero);

        return new ReplayDocument(
            Format: ReplayFormat.Name,
            Header: new ReplayHeader(
                ProtocolVersion: ReplayFormat.CurrentVersion,
                ContentVersion: "content-v1",
                PhysicsBehaviorVersion: "bple-legacy-v1",
                StateHashAlgorithm: ReplayHashAlgorithms.Sha256CanonicalV2,
                FixedTickRate: 60,
                SimulationTicks: 1,
                RandomSeed: 1234),
            InitialState: new ReplayInitialState(
                Entities: new[] { entity },
                Joints: Array.Empty<ReplayJointState>()),
            Commands: new ReplayCommand[]
            {
                new StartSimulationCommand(Tick: 1, Sequence: 1, PlayerId: 1)
            },
            Frames: new[]
            {
                new ReplayFrame(1, new[] { entity }, Array.Empty<ReplayEvent>())
            },
            FinalResult: new ReplayResult(
                Outcome: ReplayOutcome.Success,
                CompletedTick: 1,
                StateHash: new string('a', 64)));
    }
}
