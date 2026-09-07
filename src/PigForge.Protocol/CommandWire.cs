using System.Buffers.Binary;

namespace PigForge.Protocol;

/// <summary>
/// Binary client command (PGFC) and acknowledgement (PGFA) frames. Little-endian,
/// no JSON. Kind bytes match <see cref="ClientCommandKind"/>.
/// </summary>
public static class CommandFrame
{
    public const ushort Version = 2;
    public const int HeaderByteCount = 19;
    public const int AckByteCount = 16;

    public static int PlaceByteCount => HeaderByteCount + 20;
    public static int RemoveByteCount => HeaderByteCount + 4;
    public static int RotateByteCount => HeaderByteCount + 8;
    public static int StartByteCount => HeaderByteCount;

    public static bool TryEncode(Span<byte> destination, ReplayCommand command, out int written)
    {
        written = 0;
        int size = command switch
        {
            PlacePartCommand => PlaceByteCount,
            RemovePartCommand => RemoveByteCount,
            RotatePartCommand => RotateByteCount,
            StartSimulationCommand => StartByteCount,
            _ => 0
        };
        if (size == 0 || destination.Length < size)
        {
            return false;
        }

        destination[0] = (byte)'P';
        destination[1] = (byte)'G';
        destination[2] = (byte)'F';
        destination[3] = (byte)'C';
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Version);
        destination[6] = (byte)command.Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[7..], command.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[11..], command.PlayerId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[15..], command.Tick);

        switch (command)
        {
            case PlacePartCommand place:
                BinaryPrimitives.WriteUInt32LittleEndian(destination[19..], place.PartTypeId);
                BinaryPrimitives.WriteSingleLittleEndian(destination[23..], place.PositionX);
                BinaryPrimitives.WriteSingleLittleEndian(destination[27..], place.PositionY);
                BinaryPrimitives.WriteSingleLittleEndian(destination[31..], place.Angle);
                BinaryPrimitives.WriteSingleLittleEndian(destination[35..], place.Scale);
                break;
            case RemovePartCommand remove:
                BinaryPrimitives.WriteUInt32LittleEndian(destination[19..], remove.EntityId);
                break;
            case RotatePartCommand rotate:
                BinaryPrimitives.WriteUInt32LittleEndian(destination[19..], rotate.EntityId);
                BinaryPrimitives.WriteSingleLittleEndian(destination[23..], rotate.Angle);
                break;
        }

        written = size;
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out ReplayCommand? command, out string error)
    {
        command = null;
        error = string.Empty;
        if (source.Length < HeaderByteCount
            || source[0] != (byte)'P' || source[1] != (byte)'G' || source[2] != (byte)'F' || source[3] != (byte)'C')
        {
            error = "Command magic must be PGFC.";
            return false;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        if (version != Version)
        {
            error = $"Command version {version} is unsupported.";
            return false;
        }

        byte kindByte = source[6];
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(source[7..]);
        uint playerId = BinaryPrimitives.ReadUInt32LittleEndian(source[11..]);
        uint tick = BinaryPrimitives.ReadUInt32LittleEndian(source[15..]);
        if (sequence == 0)
        {
            error = "Command sequence numbers start at one.";
            return false;
        }

        if (kindByte > (byte)ClientCommandKind.StartSimulation)
        {
            error = "Unknown command kind.";
            return false;
        }

        ClientCommandKind kind = (ClientCommandKind)kindByte;
        switch (kind)
        {
            case ClientCommandKind.PlacePart:
            {
                if (source.Length < PlaceByteCount)
                {
                    error = "PlacePart payload is truncated.";
                    return false;
                }

                uint partTypeId = BinaryPrimitives.ReadUInt32LittleEndian(source[19..]);
                float x = BinaryPrimitives.ReadSingleLittleEndian(source[23..]);
                float y = BinaryPrimitives.ReadSingleLittleEndian(source[27..]);
                float angle = BinaryPrimitives.ReadSingleLittleEndian(source[31..]);
                float scale = BinaryPrimitives.ReadSingleLittleEndian(source[35..]);
                if (partTypeId == 0 || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(angle)
                    || !float.IsFinite(scale) || scale <= 0f || scale > 4f)
                {
                    error = "PlacePartCommand has invalid part type, position, angle or scale.";
                    return false;
                }

                command = new PlacePartCommand(tick, sequence, playerId, partTypeId, x, y, angle, scale);
                return true;
            }
            case ClientCommandKind.RemovePart:
            {
                if (source.Length < RemoveByteCount)
                {
                    error = "RemovePart payload is truncated.";
                    return false;
                }

                uint entityId = BinaryPrimitives.ReadUInt32LittleEndian(source[19..]);
                if (entityId == 0)
                {
                    error = "RemovePartCommand has invalid entity id.";
                    return false;
                }

                command = new RemovePartCommand(tick, sequence, playerId, entityId);
                return true;
            }
            case ClientCommandKind.RotatePart:
            {
                if (source.Length < RotateByteCount)
                {
                    error = "RotatePart payload is truncated.";
                    return false;
                }

                uint entityId = BinaryPrimitives.ReadUInt32LittleEndian(source[19..]);
                float angle = BinaryPrimitives.ReadSingleLittleEndian(source[23..]);
                if (entityId == 0 || !float.IsFinite(angle))
                {
                    error = "RotatePartCommand has invalid entity id or angle.";
                    return false;
                }

                command = new RotatePartCommand(tick, sequence, playerId, entityId, angle);
                return true;
            }
            case ClientCommandKind.StartSimulation:
                command = new StartSimulationCommand(tick, sequence, playerId);
                return true;
            default:
                error = "Unknown command kind.";
                return false;
        }
    }

    public static bool TryEncodeAck(Span<byte> destination, uint sequence, byte status, byte constructionError, uint entityId)
    {
        if (destination.Length < AckByteCount)
        {
            return false;
        }

        destination[0] = (byte)'P';
        destination[1] = (byte)'G';
        destination[2] = (byte)'F';
        destination[3] = (byte)'A';
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[6..], sequence);
        destination[10] = status;
        destination[11] = constructionError;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], entityId);
        return true;
    }

    public static bool TryDecodeAck(ReadOnlySpan<byte> source, out uint sequence, out byte status, out byte constructionError, out uint entityId)
    {
        sequence = 0;
        status = 0;
        constructionError = 0;
        entityId = 0;
        if (source.Length < AckByteCount
            || source[0] != (byte)'P' || source[1] != (byte)'G' || source[2] != (byte)'F' || source[3] != (byte)'A')
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(source[4..]) != Version)
        {
            return false;
        }

        sequence = BinaryPrimitives.ReadUInt32LittleEndian(source[6..]);
        status = source[10];
        constructionError = source[11];
        entityId = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);
        return true;
    }
}
