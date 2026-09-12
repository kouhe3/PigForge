using PigForge.Protocol;

namespace PigForge.Protocol.Tests;

public sealed class SnapshotWireTests
{
    [Fact]
    public void RoundTripPreservesHeaderAndEntities()
    {
        SnapshotEntity[] entities = CreateEntities(3);
        Span<byte> buffer = stackalloc byte[SnapshotFrame.GetMaxByteCount(entities.Length)];
        Assert.True(SnapshotFrame.TryEncodeHeader(
            buffer,
            new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, Tick: 42, Phase: 1, EntityCount: (uint)entities.Length),
            out SnapshotFrameWriter writer));
        foreach (SnapshotEntity entity in entities)
        {
            Assert.True(writer.WriteEntity(entity));
        }

        Assert.True(SnapshotFrame.TryDecodeHeader(buffer[..writer.WrittenBytes], out SnapshotFrameHeader header, out SnapshotFrameReader reader));

        Assert.Equal(42u, header.Tick);
        Assert.Equal((byte)1, header.Phase);
        Assert.Equal((uint)entities.Length, header.EntityCount);
        foreach (SnapshotEntity expected in entities)
        {
            Assert.True(reader.TryReadEntity(out SnapshotEntity actual));
            Assert.Equal(expected, actual);
        }

        Assert.False(reader.TryReadEntity(out _));
    }

    [Fact]
    public void FrameSizeIsBoundedPerEntity()
    {
        Assert.Equal(15, SnapshotFrame.HeaderByteCount);
        Assert.Equal(73, SnapshotFrame.EntityByteCount);
        Assert.Equal(15 + (10 * 73), SnapshotFrame.GetMaxByteCount(10));
    }

    [Fact]
    public void TruncatedOrForeignBuffersAreRejected()
    {
        Span<byte> buffer = stackalloc byte[SnapshotFrame.GetMaxByteCount(1)];
        Assert.True(SnapshotFrame.TryEncodeHeader(
            buffer,
            new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, 1, 0, 1),
            out SnapshotFrameWriter writer));
        Assert.True(writer.WriteEntity(CreateEntities(1)[0]));

        Assert.False(SnapshotFrame.TryDecodeHeader(buffer[..8], out _, out _));

        Span<byte> foreign = stackalloc byte[SnapshotFrame.GetMaxByteCount(1)];
        buffer[..4].CopyTo(foreign);
        Assert.False(SnapshotFrame.TryDecodeHeader(foreign, out _, out _));

        Span<byte> badVersion = stackalloc byte[SnapshotFrame.GetMaxByteCount(1)];
        buffer.CopyTo(badVersion);
        badVersion[5] = 0xFF;
        Assert.False(SnapshotFrame.TryDecodeHeader(badVersion, out _, out _));
    }

    [Fact]
    public void OverflowedDestinationFailsWithoutCorruption()
    {
        SnapshotEntity[] entities = CreateEntities(2);
        Span<byte> buffer = stackalloc byte[SnapshotFrame.HeaderByteCount + SnapshotFrame.EntityByteCount];
        Assert.True(SnapshotFrame.TryEncodeHeader(
            buffer,
            new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, 1, 0, 2),
            out SnapshotFrameWriter writer));

        Assert.True(writer.WriteEntity(entities[0]));
        Assert.False(writer.WriteEntity(entities[1]));
    }

    [Fact]
    public void EncodingTenThousandFramesIsAllocationFree()
    {
        SnapshotEntity[] entities = CreateEntities(64);
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(entities.Length)];

        // warm-up (JIT, caches)
        EncodeFrames(buffer, entities, 16);

        long before = GC.GetAllocatedBytesForCurrentThread();
        EncodeFrames(buffer, entities, 10_000);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    private static void EncodeFrames(Span<byte> buffer, SnapshotEntity[] entities, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            Assert.True(SnapshotFrame.TryEncodeHeader(
                buffer,
                new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, (uint)frame, 0, (uint)entities.Length),
                out SnapshotFrameWriter writer));
            foreach (SnapshotEntity entity in entities)
            {
                Assert.True(writer.WriteEntity(entity));
            }
        }
    }

    private static SnapshotEntity[] CreateEntities(int count)
    {
        var entities = new SnapshotEntity[count];
        for (uint index = 0; index < (uint)count; index++)
        {
            entities[index] = new SnapshotEntity(
                EntityId: index + 1,
                PhysicsBodyId: index + 1,
                PartTypeId: 1,
                Position: new ReplayVector3(index, index * 2f, 0f),
                Rotation: new ReplayQuaternion(0f, 0f, 0.70710678f, 0.70710678f),
                LinearVelocity: new ReplayVector3(1f, -2f, 0f),
                AngularVelocity: ReplayVector3.Zero,
                Scale: 1f + index,
                AttachYaw: index * 0.5f,
                Flags: (byte)(index % 2));
        }

        return entities;
    }

    [Fact]
    public void PreviousVersionFramesAreRejected()
    {
        SnapshotEntity[] entities = CreateEntities(1);
        Span<byte> buffer = stackalloc byte[SnapshotFrame.GetMaxByteCount(1)];
        Assert.True(SnapshotFrame.TryEncodeHeader(
            buffer,
            new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, 1, 0, 1),
            out SnapshotFrameWriter writer));
        Assert.True(writer.WriteEntity(entities[0]));

        buffer[4] = 2;
        buffer[5] = 0;
        Assert.False(SnapshotFrame.TryDecodeHeader(buffer[..writer.WrittenBytes], out _, out _));
    }
}
