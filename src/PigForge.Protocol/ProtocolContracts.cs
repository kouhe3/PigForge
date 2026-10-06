
namespace PigForge.Protocol;

public static class ProtocolVersion
{
	/// <summary>
	/// The client command protocol (PGFC). v3 added the build pose's mirror to the two placement
	/// commands (ADR-030); <see cref="CommandFrame.Version"/> is this constant, so the envelope
	/// contract and the wire can never disagree.
	/// </summary>
	public const ushort Current = 3;
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
	ScalePart,
	SetPartActive,
	SetPartTypeActive
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
