
namespace PigForge.Protocol;

public static class ProtocolVersion
{
	public const ushort Current = 2;
}

public enum ClientCommandKind
{
	PlacePart,
	RemovePart,
	RotatePart,
	StartSimulation,
	EnterBuildMode,
	Retry,
	MovePart,
	ScalePart
}

public sealed record ClientCommandEnvelope(
	ushort Version,
	uint Sequence,
	ClientCommandKind Kind)
{
	public static ClientCommandEnvelope Create(uint sequence, ClientCommandKind kind)
	{
		if (sequence == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(sequence), "Command sequence numbers start at one.");
		}

		return new ClientCommandEnvelope(ProtocolVersion.Current, sequence, kind);
	}
}

public sealed record EntitySnapshot(
    uint Entity,
	uint PhysicsBody,
	float PositionX,
	float PositionY,
	float PositionZ,
	float RotationX,
	float RotationY,
	float RotationZ,
	float RotationW);

public sealed record ServerSnapshot(
	ushort Version,
	uint Tick,
	IReadOnlyList<EntitySnapshot> Entities);
