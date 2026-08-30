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
	float Scale);

public readonly record struct SnapshotFrameHeader(ushort Version, uint Tick, byte Phase, uint EntityCount);

/// <summary>
/// Binary wire format for published authoritative room snapshots (v2).
/// Layout, little-endian: magic "PGFS" | version:u16 | tick:u32 | phase:u8 | entityCount:u32,
/// then per entity: entityId:u32 | physicsBodyId:u32 | partTypeId:u32 | position:3f |
/// rotation:4f | linearVelocity:3f | angularVelocity:3f | scale:f.
/// The hot path is span-based: no JSON and no allocations.
/// </summary>
public static class SnapshotFrame
{
	public const ushort CurrentVersion = 2;

	public const int HeaderByteCount = 15;

	public const int EntityByteCount = 12 + (4 * 14);

	public static int GetMaxByteCount(int entityCount) => HeaderByteCount + (entityCount * EntityByteCount);

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
		_position += SnapshotFrame.EntityByteCount;
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
			BinaryPrimitives.ReadSingleLittleEndian(span[64..]));
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
