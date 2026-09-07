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
}
