using PigForge.Protocol;

namespace PigForge.Protocol.Tests;

public sealed class CommandWireTests
{
    [Fact]
    public void PlaceRoundTripPreservesFields()
    {
        PlacePartCommand original = new(0, 1, 1, 4, -5.25f, 4.5f, -0.35f, 1.5f);
        Span<byte> buffer = stackalloc byte[CommandFrame.PlaceByteCount];
        Assert.True(CommandFrame.TryEncode(buffer, original, out int written));
        Assert.Equal(CommandFrame.PlaceByteCount, written);
        Assert.True(CommandFrame.TryDecode(buffer[..written], out ReplayCommand? decoded, out string error));
        Assert.Equal(string.Empty, error);
        PlacePartCommand place = Assert.IsType<PlacePartCommand>(decoded);
        Assert.Equal(original.PartTypeId, place.PartTypeId);
        Assert.Equal(original.PositionX, place.PositionX);
        Assert.Equal(original.PositionY, place.PositionY);
        Assert.Equal(original.Angle, place.Angle);
        Assert.Equal(original.Scale, place.Scale);
        Assert.Equal(original.Sequence, place.Sequence);
    }

    [Fact]
    public void StartAndAckRoundTrip()
    {
        StartSimulationCommand start = new(0, 2, 1);
        Span<byte> buffer = stackalloc byte[CommandFrame.StartByteCount];
        Assert.True(CommandFrame.TryEncode(buffer, start, out int written));
        Assert.True(CommandFrame.TryDecode(buffer[..written], out ReplayCommand? decoded, out _));
        Assert.IsType<StartSimulationCommand>(decoded);

        Span<byte> ack = stackalloc byte[CommandFrame.AckByteCount];
        Assert.True(CommandFrame.TryEncodeAck(ack, 2, status: 0, constructionError: 0, entityId: 7));
        Assert.True(CommandFrame.TryDecodeAck(ack, out uint sequence, out byte status, out byte error, out uint entityId));
        Assert.Equal(2u, sequence);
        Assert.Equal((byte)0, status);
        Assert.Equal((byte)0, error);
        Assert.Equal(7u, entityId);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'X', (byte)'G', (byte)'F', (byte)'C' })]
    public void BadMagicIsRejected(byte[] prefix)
    {
        byte[] buffer = new byte[CommandFrame.StartByteCount];
        prefix.CopyTo(buffer, 0);
        Assert.False(CommandFrame.TryDecode(buffer, out _, out string error));
        Assert.Contains("PGFC", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedPlaceIsRejected()
    {
        PlacePartCommand original = new(0, 1, 1, 1, 0f, 0f, 0f, 1f);
        byte[] buffer = new byte[CommandFrame.PlaceByteCount];
        Assert.True(CommandFrame.TryEncode(buffer, original, out _));
        Assert.False(CommandFrame.TryDecode(buffer.AsSpan(0, CommandFrame.HeaderByteCount + 2), out _, out string error));
        Assert.Contains("truncated", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidScaleIsRejected()
    {
        PlacePartCommand original = new(0, 1, 1, 1, 0f, 0f, 0f, 1f);
        byte[] buffer = new byte[CommandFrame.PlaceByteCount];
        Assert.True(CommandFrame.TryEncode(buffer, original, out _));
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(35), 8f);
        Assert.False(CommandFrame.TryDecode(buffer, out _, out string error));
        Assert.Contains("scale", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MoveRoundTripPreservesFields()
    {
        MovePartCommand original = new(0, 3, 2, 9, -5.25f, 4.5f);
        Span<byte> buffer = stackalloc byte[CommandFrame.MoveByteCount];
        Assert.True(CommandFrame.TryEncode(buffer, original, out int written));
        Assert.Equal(CommandFrame.MoveByteCount, written);
        Assert.True(CommandFrame.TryDecode(buffer[..written], out ReplayCommand? decoded, out string error));
        Assert.Equal(string.Empty, error);
        MovePartCommand move = Assert.IsType<MovePartCommand>(decoded);
        Assert.Equal(original.EntityId, move.EntityId);
        Assert.Equal(original.PositionX, move.PositionX);
        Assert.Equal(original.PositionY, move.PositionY);
        Assert.Equal(original.Sequence, move.Sequence);
        Assert.Equal(ClientCommandKind.MovePart, move.Kind);
    }

    [Fact]
    public void ScaleRoundTripPreservesFields()
    {
        ScalePartCommand original = new(0, 4, 2, 9, 1.75f);
        Span<byte> buffer = stackalloc byte[CommandFrame.ScaleByteCount];
        Assert.True(CommandFrame.TryEncode(buffer, original, out int written));
        Assert.Equal(CommandFrame.ScaleByteCount, written);
        Assert.True(CommandFrame.TryDecode(buffer[..written], out ReplayCommand? decoded, out string error));
        Assert.Equal(string.Empty, error);
        ScalePartCommand scale = Assert.IsType<ScalePartCommand>(decoded);
        Assert.Equal(original.EntityId, scale.EntityId);
        Assert.Equal(original.Scale, scale.Scale);
        Assert.Equal(original.Sequence, scale.Sequence);
        Assert.Equal(ClientCommandKind.ScalePart, scale.Kind);
    }

    [Fact]
    public void TruncatedMoveAndScaleAreRejected()
    {
        MovePartCommand move = new(0, 1, 1, 9, 1f, 2f);
        byte[] moveBuffer = new byte[CommandFrame.MoveByteCount];
        Assert.True(CommandFrame.TryEncode(moveBuffer, move, out _));
        Assert.False(CommandFrame.TryDecode(moveBuffer.AsSpan(0, CommandFrame.HeaderByteCount + 2), out _, out string moveError));
        Assert.Contains("truncated", moveError, StringComparison.OrdinalIgnoreCase);

        ScalePartCommand scale = new(0, 2, 1, 9, 1.5f);
        byte[] scaleBuffer = new byte[CommandFrame.ScaleByteCount];
        Assert.True(CommandFrame.TryEncode(scaleBuffer, scale, out _));
        Assert.False(CommandFrame.TryDecode(scaleBuffer.AsSpan(0, CommandFrame.HeaderByteCount + 2), out _, out string scaleError));
        Assert.Contains("truncated", scaleError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZeroEntityIdIsRejectedForMoveAndScale()
    {
        MovePartCommand move = new(0, 1, 1, 0, 1f, 2f);
        byte[] moveBuffer = new byte[CommandFrame.MoveByteCount];
        Assert.True(CommandFrame.TryEncode(moveBuffer, move, out _));
        Assert.False(CommandFrame.TryDecode(moveBuffer, out _, out string moveError));
        Assert.Contains("entity id", moveError, StringComparison.OrdinalIgnoreCase);

        ScalePartCommand scale = new(0, 2, 1, 0, 1.5f);
        byte[] scaleBuffer = new byte[CommandFrame.ScaleByteCount];
        Assert.True(CommandFrame.TryEncode(scaleBuffer, scale, out _));
        Assert.False(CommandFrame.TryDecode(scaleBuffer, out _, out string scaleError));
        Assert.Contains("entity id", scaleError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(5f)]
    public void InvalidScalePartScaleIsRejected(float scale)
    {
        ScalePartCommand original = new(0, 1, 1, 9, scale);
        byte[] buffer = new byte[CommandFrame.ScaleByteCount];
        Assert.True(CommandFrame.TryEncode(buffer, original, out _));
        Assert.False(CommandFrame.TryDecode(buffer, out _, out string error));
        Assert.Contains("scale", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NonFiniteMovePositionIsRejected(float positionX)
    {
        MovePartCommand original = new(0, 1, 1, 9, positionX, 2f);
        byte[] buffer = new byte[CommandFrame.MoveByteCount];
        Assert.True(CommandFrame.TryEncode(buffer, original, out _));
        Assert.False(CommandFrame.TryDecode(buffer, out _, out string error));
        Assert.Contains("position", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KindBeyondScalePartIsRejected()
    {
        PlacePartCommand original = new(0, 1, 1, 1, 0f, 0f, 0f, 1f);
        byte[] buffer = new byte[CommandFrame.PlaceByteCount];
        Assert.True(CommandFrame.TryEncode(buffer, original, out _));
        buffer[6] = (byte)ClientCommandKind.ScalePart + 1;
        Assert.False(CommandFrame.TryDecode(buffer, out _, out string error));
        Assert.Contains("Unknown", error, StringComparison.Ordinal);
    }
}
