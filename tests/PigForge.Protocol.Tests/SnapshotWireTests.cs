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
        // v8's tail: the collected section is two bytes when empty, four per marker when not, then
        // the run state's five bytes (elapsed:f then challengeFlags:u8) that every frame carries.
        Assert.Equal(4, SnapshotFrame.ElapsedByteCount);
        Assert.Equal(1, SnapshotFrame.ChallengeFlagsByteCount);
        Assert.Equal(5, SnapshotFrame.RunStateByteCount);
        Assert.Equal(15 + (10 * 73) + 2 + 5, SnapshotFrame.GetMaxByteCount(10));
        Assert.Equal(15 + (10 * 73) + 2 + (3 * 4) + 5, SnapshotFrame.GetMaxByteCount(10, 3));
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

            Assert.True(writer.TryWriteCollected(ReadOnlySpan<uint>.Empty));
            Assert.True(writer.TryWriteRunState(elapsedSeconds: frame, challengeFlags: 0));
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
                Flags: (byte)(index % 8));
        }

        return entities;
    }

    [Fact]
    public void EverySnapshotFlagBitIsDeclaredAndSurvivesTheRoundTrip()
    {
        // v6 (ADR-030): bit0 the switch, bit1 a runtime sub-entity, bit2 the build pose's mirror.
        Assert.Equal(0x01, SnapshotFrame.FlagActive);
        Assert.Equal(0x02, SnapshotFrame.FlagSubEntity);
        Assert.Equal(0x04, SnapshotFrame.FlagMirrored);

        SnapshotEntity flagged = CreateEntities(1)[0] with { Flags = 0b111 };
        Span<byte> buffer = stackalloc byte[SnapshotFrame.GetMaxByteCount(1)];
        Assert.True(SnapshotFrame.TryEncodeHeader(
            buffer,
            new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, 1, 0, 1),
            out SnapshotFrameWriter writer));
        Assert.True(writer.WriteEntity(flagged));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer[..writer.WrittenBytes], out _, out SnapshotFrameReader reader));
        Assert.True(reader.TryReadEntity(out SnapshotEntity decoded));
        Assert.Equal((byte)0b111, decoded.Flags);
        Assert.Equal(8, SnapshotFrame.CurrentVersion);
    }

    /// <summary>
    /// v8's tail: the run timer and the challenge bits (`docs/specs/level-stars.md` §5). A frame
    /// always carries the section, so a fresh run's zeros are real state, and the flags byte keeps
    /// every bit it was written with.
    /// </summary>
    [Fact]
    public void RunStateSurvivesTheRoundTrip()
    {
        SnapshotEntity[] entities = CreateEntities(2);
        uint[] collected = [4, 91];
        Span<byte> buffer = stackalloc byte[SnapshotFrame.GetMaxByteCount(2, collected.Length)];
        Assert.True(SnapshotFrame.TryEncodeHeader(
            buffer,
            new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, 9, 1, 2),
            out SnapshotFrameWriter writer));
        foreach (SnapshotEntity entity in entities)
        {
            Assert.True(writer.WriteEntity(entity));
        }

        Assert.True(writer.TryWriteCollected(collected));
        Assert.True(writer.TryWriteRunState(12.5f, 0b101));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer[..writer.WrittenBytes], out _, out SnapshotFrameReader reader));
        Assert.True(reader.TryReadEntity(out _));
        Assert.True(reader.TryReadEntity(out _));
        Span<uint> read = stackalloc uint[2];
        Assert.True(reader.TryReadCollected(read, out int count));
        Assert.Equal(2, count);
        Assert.Equal(collected, read[..count].ToArray());
        Assert.True(reader.TryReadRunState(out float elapsedSeconds, out byte challengeFlags));
        Assert.Equal(12.5f, elapsedSeconds);
        Assert.Equal((byte)0b101, challengeFlags);
        Assert.Equal(0u, reader.Remaining);

        // A run that has neither started nor won anything still writes both sections: the zeros are
        // the state, not an absent section.
        Span<byte> fresh = stackalloc byte[SnapshotFrame.GetMaxByteCount(0)];
        Assert.True(SnapshotFrame.TryEncodeHeader(fresh, new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, 1, 0, 0), out SnapshotFrameWriter freshWriter));
        Assert.True(freshWriter.TryWriteCollected(ReadOnlySpan<uint>.Empty));
        Assert.True(freshWriter.TryWriteRunState(0f, 0));
        Assert.Equal(SnapshotFrame.HeaderByteCount + SnapshotFrame.CollectedCountByteCount + SnapshotFrame.RunStateByteCount, freshWriter.WrittenBytes);
        Assert.True(SnapshotFrame.TryDecodeHeader(fresh[..freshWriter.WrittenBytes], out _, out SnapshotFrameReader freshReader));
        Assert.True(freshReader.TryReadCollected(Span<uint>.Empty, out _));
        Assert.True(freshReader.TryReadRunState(out float zero, out byte noFlags));
        Assert.Equal(0f, zero);
        Assert.Equal((byte)0, noFlags);
    }

    /// <summary>
    /// The run state is the last thing a frame writes, so a tail that stops inside it is refused
    /// rather than read as a partial reading -- and a frame whose tail ends where a pre-v8 frame's
    /// did (right after the collected markers) reports no run state instead of fabricating zeros.
    /// Old-version frames themselves never reach a reader: the header's version gate turns them away.
    /// </summary>
    [Fact]
    public void RunStateIsRequiredAfterTheCollectedSection()
    {
        SnapshotEntity[] entities = CreateEntities(1);
        Span<byte> buffer = stackalloc byte[SnapshotFrame.GetMaxByteCount(1)];
        Assert.True(SnapshotFrame.TryEncodeHeader(
            buffer,
            new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, 1, 0, 1),
            out SnapshotFrameWriter writer));
        Assert.True(writer.WriteEntity(entities[0]));
        Assert.True(writer.TryWriteCollected(ReadOnlySpan<uint>.Empty));
        Assert.True(writer.TryWriteRunState(3.25f, 0b11));
        int complete = writer.WrittenBytes;
        Assert.Equal(
            SnapshotFrame.HeaderByteCount + SnapshotFrame.EntityByteCount + SnapshotFrame.CollectedCountByteCount + SnapshotFrame.RunStateByteCount,
            complete);

        for (int missing = 1; missing <= SnapshotFrame.RunStateByteCount; missing++)
        {
            Assert.True(SnapshotFrame.TryDecodeHeader(buffer[..(complete - missing)], out _, out SnapshotFrameReader reader));
            Assert.True(reader.TryReadEntity(out _));
            Assert.True(reader.TryReadCollected(Span<uint>.Empty, out _));
            Assert.False(reader.TryReadRunState(out _, out _));
        }

        // The pre-v8 shape: the same frame with the five bytes never written.
        int legacyLength = complete - SnapshotFrame.RunStateByteCount;
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer[..legacyLength], out SnapshotFrameHeader legacyHeader, out SnapshotFrameReader legacyReader));
        Assert.Equal(SnapshotFrame.CurrentVersion, legacyHeader.Version);
        Assert.True(legacyReader.TryReadEntity(out _));
        Assert.True(legacyReader.TryReadCollected(Span<uint>.Empty, out _));
        Assert.False(legacyReader.TryReadRunState(out float elapsedSeconds, out byte challengeFlags));
        Assert.Equal(0f, elapsedSeconds);
        Assert.Equal((byte)0, challengeFlags);
    }

    /// <summary>
    /// v7's tail: the challenge markers a level has collected (`docs/specs/level-challenges.md` §5). A
    /// frame always carries the section, so the reader can tell "nothing collected" from a truncated
    /// frame, and the indices come back in the order they were written.
    /// </summary>
    [Fact]
    public void CollectedMarkersSurviveTheRoundTrip()
    {
        SnapshotEntity[] entities = CreateEntities(2);
        uint[] collected = [4, 91];
        Span<byte> buffer = stackalloc byte[SnapshotFrame.GetMaxByteCount(2, collected.Length)];
        Assert.True(SnapshotFrame.TryEncodeHeader(
            buffer,
            new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, 5, 0, 2),
            out SnapshotFrameWriter writer));
        foreach (SnapshotEntity entity in entities)
        {
            Assert.True(writer.WriteEntity(entity));
        }

        Assert.True(writer.TryWriteCollected(collected));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer[..writer.WrittenBytes], out _, out SnapshotFrameReader reader));
        Assert.True(reader.TryReadEntity(out _));
        Assert.True(reader.TryReadEntity(out _));
        Assert.Equal(0u, reader.Remaining);
        Span<uint> read = stackalloc uint[4];
        Assert.True(reader.TryReadCollected(read, out int count));
        Assert.Equal(2, count);
        Assert.Equal(collected, read[..count].ToArray());

        // An empty section still writes its count, and a frame with room for none reports it.
        Span<byte> empty = stackalloc byte[SnapshotFrame.GetMaxByteCount(0)];
        Assert.True(SnapshotFrame.TryEncodeHeader(empty, new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, 1, 0, 0), out SnapshotFrameWriter emptyWriter));
        Assert.True(emptyWriter.TryWriteCollected(ReadOnlySpan<uint>.Empty));
        Assert.Equal(SnapshotFrame.HeaderByteCount + SnapshotFrame.CollectedCountByteCount, emptyWriter.WrittenBytes);
        Assert.True(SnapshotFrame.TryDecodeHeader(empty[..emptyWriter.WrittenBytes], out _, out SnapshotFrameReader emptyReader));
        Assert.True(emptyReader.TryReadCollected(Span<uint>.Empty, out int none));
        Assert.Equal(0, none);

        // A destination that cannot hold every marker is refused rather than truncated.
        Assert.False(writer.TryWriteCollected(collected));
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

        // v5 is the frame the previous client speaks; the mirror bit is what made it v6.
        buffer[4] = 5;
        buffer[5] = 0;
        Assert.False(SnapshotFrame.TryDecodeHeader(buffer[..writer.WrittenBytes], out _, out _));

        // v7 is the frame the previous client speaks; the run state's five bytes are what made it v8.
        buffer[4] = 7;
        buffer[5] = 0;
        Assert.False(SnapshotFrame.TryDecodeHeader(buffer[..writer.WrittenBytes], out _, out _));
    }
}
