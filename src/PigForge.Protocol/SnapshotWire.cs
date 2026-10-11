using System.Buffers.Binary;

namespace PigForge.Protocol;

public readonly record struct SnapshotEntity(
	uint EntityId,
	uint PhysicsBodyId,
	uint PartTypeId,
	ReplayVector3 Position,
	ReplayQuaternion Rotation,
	ReplayVector3 LinearVelocity,
	ReplayVector3 AngularVelocity,
	float Scale,
	float AttachYaw = 0f,
	byte Flags = 0);

public readonly record struct SnapshotFrameHeader(ushort Version, uint Tick, byte Phase, uint EntityCount);

/// <summary>
/// Binary wire format for published authoritative room snapshots (v6).
/// Layout, little-endian: magic "PGFS" | version:u16 | tick:u32 | phase:u8 | entityCount:u32,
/// then per entity: entityId:u32 | physicsBodyId:u32 | partTypeId:u32 | position:3f |
/// rotation:4f | linearVelocity:3f | angularVelocity:3f | scale:f | attachYaw:f | flags:u8.
/// Flags bit0 is the part switch state; bit1 marks a runtime sub-entity (a boxing glove's fist,
/// a broken spring's endpoint -- ADR-027) that borrows its host's part type, so the client can
/// draw its own art instead of the host's; bit2 is the build pose's mirror (ADR-030), the
/// handedness a float yaw cannot carry; bits 3-7 are reserved (written 0, ignored on read).
/// `attachYaw` is the world Z yaw, in radians, of the frame the part's non-spinning sprites are
/// rigidly attached to: the hinge's parent body frame for a hinged wheel (so its axle follows the
/// chassis), the part's own frame otherwise. Rotation is the part's own body rotation, which for
/// a wheel carries its accumulated spin, so the two differ exactly by the roll (ADR-009).
/// The hot path is span-based: no JSON and no allocations.
/// </summary>
public static class SnapshotFrame
{
	// v7 appends the collected challenge markers after the entities: `uint16 count` then `count` little
	// endian `uint32` prop indices (indices into the level document's own `props`,
	// `docs/specs/level-challenges.md` §5). A frame always carries the section -- two zero bytes when
	// nothing has been collected.
	public const ushort CurrentVersion = 7;

	/// <summary>Flags bit 0: the part's switch state.</summary>
	public const byte FlagActive = 0x01;

	/// <summary>Flags bit 1: a runtime sub-entity (ADR-027/ADR-028).</summary>
	public const byte FlagSubEntity = 0x02;

	/// <summary>Flags bit 2: the build pose is mirrored (ADR-030).</summary>
	public const byte FlagMirrored = 0x04;

	public const byte BuildingPhase = 0x10;

	public const int HeaderByteCount = 15;

	public const int EntityByteCount = 13 + (4 * 15);

	/// <summary>One collected prop's index on the wire.</summary>
	public const int CollectedEntryByteCount = 4;

	/// <summary>How many markers a frame carries before its entries.</summary>
	public const int CollectedCountByteCount = 2;

	public static int GetMaxByteCount(int entityCount, int collectedCount = 0) =>
		HeaderByteCount + (entityCount * EntityByteCount) + CollectedCountByteCount + (collectedCount * CollectedEntryByteCount);

	public static bool TryEncodeHeader(Span<byte> destination, SnapshotFrameHeader header, out SnapshotFrameWriter writer)
	{
		if (destination.Length < HeaderByteCount || header.Version != CurrentVersion)
		{
			writer = default;
			return false;
		}

		destination[0] = (byte)'P';
		destination[1] = (byte)'G';
		destination[2] = (byte)'F';
		destination[3] = (byte)'S';
		BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], header.Version);
		BinaryPrimitives.WriteUInt32LittleEndian(destination[6..], header.Tick);
		destination[10] = header.Phase;
		BinaryPrimitives.WriteUInt32LittleEndian(destination[11..], header.EntityCount);
		writer = new SnapshotFrameWriter(destination, HeaderByteCount);
		return true;
	}

	public static bool TryDecodeHeader(ReadOnlySpan<byte> source, out SnapshotFrameHeader header, out SnapshotFrameReader reader)
	{
		if (source.Length < HeaderByteCount
			|| source[0] != (byte)'P' || source[1] != (byte)'G' || source[2] != (byte)'F' || source[3] != (byte)'S')
		{
			header = default;
			reader = default;
			return false;
		}

		ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
		if (version != CurrentVersion)
		{
			header = default;
			reader = default;
			return false;
		}

		header = new SnapshotFrameHeader(
			version,
			BinaryPrimitives.ReadUInt32LittleEndian(source[6..]),
			source[10],
			BinaryPrimitives.ReadUInt32LittleEndian(source[11..]));
		reader = new SnapshotFrameReader(source, HeaderByteCount, header.EntityCount);
		return true;
	}
}

public ref struct SnapshotFrameWriter
{
	private Span<byte> _destination;
	private int _position;

	internal SnapshotFrameWriter(Span<byte> destination, int startPosition)
	{
		_destination = destination;
		_position = startPosition;
	}

	public int WrittenBytes => _position;

	public bool WriteEntity(in SnapshotEntity entity)
	{
		if (_destination.Length - _position < SnapshotFrame.EntityByteCount)
		{
			return false;
		}

		Span<byte> span = _destination[_position..];
		BinaryPrimitives.WriteUInt32LittleEndian(span, entity.EntityId);
		BinaryPrimitives.WriteUInt32LittleEndian(span[4..], entity.PhysicsBodyId);
		BinaryPrimitives.WriteUInt32LittleEndian(span[8..], entity.PartTypeId);
		WriteVector3(span[12..], entity.Position);
		WriteQuaternion(span[24..], entity.Rotation);
		WriteVector3(span[40..], entity.LinearVelocity);
		WriteVector3(span[52..], entity.AngularVelocity);
		BinaryPrimitives.WriteSingleLittleEndian(span[64..], entity.Scale);
		BinaryPrimitives.WriteSingleLittleEndian(span[68..], entity.AttachYaw);
		span[72] = entity.Flags;
		_position += SnapshotFrame.EntityByteCount;
		return true;
	}

	/// <summary>
	/// Appends the collected challenge markers (v7): `uint16 count` then the indices, in the caller's own
	/// order. Always written -- an empty span writes the two zero bytes a frame's tail needs.
	/// </summary>
	public bool TryWriteCollected(ReadOnlySpan<uint> collectedProps)
	{
		int required = SnapshotFrame.CollectedCountByteCount + (collectedProps.Length * SnapshotFrame.CollectedEntryByteCount);
		if (_destination.Length - _position < required)
		{
			return false;
		}

		Span<byte> span = _destination[_position..];
		BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)collectedProps.Length);
		for (int index = 0; index < collectedProps.Length; index++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(span[(SnapshotFrame.CollectedCountByteCount + (index * SnapshotFrame.CollectedEntryByteCount))..], collectedProps[index]);
		}

		_position += required;
		return true;
	}

	private static void WriteVector3(Span<byte> span, ReplayVector3 value)
	{
		BinaryPrimitives.WriteSingleLittleEndian(span, value.X);
		BinaryPrimitives.WriteSingleLittleEndian(span[4..], value.Y);
		BinaryPrimitives.WriteSingleLittleEndian(span[8..], value.Z);
	}

	private static void WriteQuaternion(Span<byte> span, ReplayQuaternion value)
	{
		BinaryPrimitives.WriteSingleLittleEndian(span, value.X);
		BinaryPrimitives.WriteSingleLittleEndian(span[4..], value.Y);
		BinaryPrimitives.WriteSingleLittleEndian(span[8..], value.Z);
		BinaryPrimitives.WriteSingleLittleEndian(span[12..], value.W);
	}
}

public ref struct SnapshotFrameReader
{
	private readonly ReadOnlySpan<byte> _source;
	private int _position;
	private uint _remaining;

	internal SnapshotFrameReader(ReadOnlySpan<byte> source, int startPosition, uint entityCount)
	{
		_source = source;
		_position = startPosition;
		_remaining = entityCount;
	}

	public uint Remaining => _remaining;

	/// <summary>
	/// Reads the collected challenge markers (v7) once every entity has been read. The destination is the
	/// caller's own buffer, so the hot path stays allocation-free; a frame with more markers than the
	/// destination holds reports false rather than truncating.
	/// </summary>
	public bool TryReadCollected(Span<uint> destination, out int count)
	{
		count = 0;
		if (_remaining != 0 || _source.Length - _position < SnapshotFrame.CollectedCountByteCount)
		{
			return false;
		}

		ushort written = BinaryPrimitives.ReadUInt16LittleEndian(_source[_position..]);
		int required = written * SnapshotFrame.CollectedEntryByteCount;
		if (destination.Length < written || _source.Length - _position - SnapshotFrame.CollectedCountByteCount < required)
		{
			return false;
		}

		int start = _position + SnapshotFrame.CollectedCountByteCount;
		for (int index = 0; index < written; index++)
		{
			destination[index] = BinaryPrimitives.ReadUInt32LittleEndian(_source[(start + (index * SnapshotFrame.CollectedEntryByteCount))..]);
		}

		_position = start + required;
		count = written;
		return true;
	}

	public bool TryReadEntity(out SnapshotEntity entity)
	{
		if (_remaining == 0 || _source.Length - _position < SnapshotFrame.EntityByteCount)
		{
			entity = default;
			return false;
		}

		ReadOnlySpan<byte> span = _source.Slice(_position, SnapshotFrame.EntityByteCount);
		entity = new SnapshotEntity(
			BinaryPrimitives.ReadUInt32LittleEndian(span),
			BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
			BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
			ReadVector3(span[12..]),
			ReadQuaternion(span[24..]),
			ReadVector3(span[40..]),
			ReadVector3(span[52..]),
			BinaryPrimitives.ReadSingleLittleEndian(span[64..]),
			BinaryPrimitives.ReadSingleLittleEndian(span[68..]),
			span[72]);
		_position += SnapshotFrame.EntityByteCount;
		_remaining--;
		return true;
	}

	private static ReplayVector3 ReadVector3(ReadOnlySpan<byte> span) => new(
		BinaryPrimitives.ReadSingleLittleEndian(span),
		BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
		BinaryPrimitives.ReadSingleLittleEndian(span[8..]));

	private static ReplayQuaternion ReadQuaternion(ReadOnlySpan<byte> span) => new(
		BinaryPrimitives.ReadSingleLittleEndian(span),
		BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
		BinaryPrimitives.ReadSingleLittleEndian(span[8..]),
		BinaryPrimitives.ReadSingleLittleEndian(span[12..]));
}
