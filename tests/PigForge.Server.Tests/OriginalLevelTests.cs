using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The converted original level pack: `tools/bple-levels/build-levels.mjs` writes 277 level-content
/// v2 documents under `content/levels/original/**`, one per `<scene>_data.bytes` in the original
/// (see docs/specs/original-level-pack.md). These tests are the playability proof the converter
/// cannot give on its own:
/// <list type="bullet">
/// <item>every shipped document loads through <see cref="LevelContentLibrary.Parse"/> and carries
/// terrain, at the counts the extractor measured (277 levels / 1648 terrain entries / 1652 loops /
/// 20 placed parts / 14 levels without a goal);</item>
/// <item>a real Bepu room built from one of them holds the shipped pig up: it is dropped above the
/// level's own terrain, and after 4 s its height is inside that level's own terrain y-range --
/// the bounds come from the document, not from a constant in the test;</item>
/// <item>the identical document with its <c>terrain</c> entry removed drops the same pig straight
/// through, so the comparison that passes above fails here.</item>
/// </list>
/// The landing spot is derived too: the first converted level (in path order) with no pre-placed
/// parts and at least two terrain objects, taking the terrain with the most outline points whose
/// own high ground sits strictly inside the level's y-range, and dropping in the middle of that
/// high ground (the average x of its own top points). The room keeps a far goal zone and far map
/// bounds so the level's own goal cannot end the run and PigForge's out-of-bounds reset (a PigForge
/// addition, G78 -- the original has neither) cannot reset the falling control.
/// </summary>
public sealed class OriginalLevelTests
{
    private const uint PartPig = 4;
    private const uint PartPlayer = 1;
    private const uint SequencePlace = 1;
    private const uint SequenceStart = 2;

    private const int SampleTicks = 30;
    private const int Ticks = 240;

    [Fact]
    public void EveryConvertedLevelLoadsAndCarriesTerrain()
    {
        string root = FindRepositoryRoot();
        string[] files = LevelFiles(root);

        // The pack is all nine bundles: 45+45+45+45+30+45 + 8 + 10 + 4.
        Assert.Equal(277, files.Length);

        int terrainEntries = 0;
        int loops = 0;
        int spawns = 0;
        int levelsWithoutTerrain = 0;
        int levelsWithoutGoal = 0;
        foreach (string file in files)
        {
            LevelContentDocument document = LevelContentLibrary.Load(file).Document;

            // One name per level, and the file name is that name: the parser's contentVersion
            // pattern rejects whitespace, and the builder replaces the three scene names with
            // spaces ("Episode_6_Dark Sandbox") with dashes so the file system and the field agree.
            Assert.Equal(Path.GetFileNameWithoutExtension(file), document.ContentVersion);
            Assert.DoesNotContain(' ', document.ContentVersion);

            if (document.Terrain.Count == 0)
            {
                levelsWithoutTerrain++;
                continue;
            }

            terrainEntries += document.Terrain.Count;
            foreach (LevelTerrainDefinition terrain in document.Terrain)
            {
                Assert.True(terrain.Depth > 0f, $"{file}: depth {terrain.Depth}");
                Assert.NotEmpty(terrain.Loops);
                loops += terrain.Loops.Count;
                foreach (IReadOnlyList<PhysicsVector3> loop in terrain.Loops)
                {
                    Assert.True(loop.Count >= 3, $"{file}: a loop has {loop.Count} points");
                }
            }

            spawns += document.Spawns.Count;
            if (document.Spawns.Count > 0)
            {
                // Spawns only ever mean "a part the original placed in this level" (role part).
                Assert.All(document.Spawns, spawn => Assert.Equal(LevelActorRole.Part, spawn.Role));
            }

            if (document.GoalZone.Min.X > document.MapBounds.Max.X)
            {
                // The builder parks a level without a Goal* instance just outside its own bounds.
                levelsWithoutGoal++;
            }
        }

        // Measured 2026-10-06 over the pristine pack (docs/specs/original-level-pack.md §6):
        // 2146 terrain objects, 1648 with a collider; the boundary walk splits the 4 pinch terrains,
        // so those 1648 become 1652 loops; 8 part prefabs are placed 20 times; 14 sandbox/MM levels
        // have no Goal* instance (the builder parks their goal zone outside the map bounds).
        Assert.Equal(0, levelsWithoutTerrain);
        Assert.Equal(1648, terrainEntries);
        Assert.Equal(1652, loops);
        Assert.Equal(20, spawns);
        Assert.Equal(14, levelsWithoutGoal);
    }

    [Fact]
    public void APigRestsOnAConvertedLevelsOwnTerrain()
    {
        string root = FindRepositoryRoot();
        LandingCandidate candidate = FindLandingCandidate(root);
        LevelContentDocument level = LevelContentLibrary.Load(candidate.File).Document;

        // The drop point is the middle of the level's own high ground, 3 m above it.
        FallResult withTerrain = Fall(level, candidate.Drop);
        Assert.True(
            withTerrain.Height < candidate.Drop.Y - 1f,
            $"{candidate.File}: the pig must fall from its drop point (dropped {candidate.Drop.Y}, rests {withTerrain.Height})");

        // The pig rests on that level's own terrain: its height is inside the y-range of the
        // document's own terrain, derived here from the loops, not a constant.
        Assert.InRange(withTerrain.Height, candidate.LevelMinY, candidate.LevelMaxY);
        Assert.True(
            withTerrain.Height > candidate.LevelMinY && withTerrain.Height < candidate.LevelMaxY,
            $"{candidate.File}: the resting height {withTerrain.Height} must be strictly inside the terrain y-range " +
                $"[{candidate.LevelMinY}, {candidate.LevelMaxY}]");

        // ... and specifically on the candidate terrain's own top surface: the shipped pig is a
        // sphere (radius 0.42, offset y -0.04 -- content/parts.json), so its centre rests ~0.38 m
        // above the surface it stands on, i.e. just above that terrain's own top.
        Assert.InRange(withTerrain.Height, candidate.TerrainTop + 0.1f, candidate.TerrainTop + 1.2f);
        Assert.True(withTerrain.Speed < 1f, $"{candidate.File}: the pig must have settled: |v| {withTerrain.Speed}");

        // The same document with its terrain removed fails the same comparison: nothing holds the
        // pig, so it falls out of the level's own y-range.
        LevelContentDocument withoutTerrain = level with { Terrain = Array.Empty<LevelTerrainDefinition>() };
        FallResult without = Fall(withoutTerrain, candidate.Drop);
        Assert.False(
            without.Height >= candidate.LevelMinY && without.Height <= candidate.LevelMaxY,
            $"{candidate.File}: without terrain the pig must not rest inside the terrain y-range (y = {without.Height})");
        Assert.True(
            without.Height < candidate.LevelMinY - 5f,
            $"{candidate.File}: without terrain the pig must fall well below the terrain (y = {without.Height}, min {candidate.LevelMinY})");
    }

    private readonly record struct FallResult(float Height, float Speed);

    private sealed record LandingCandidate(string File, PhysicsVector3 Drop, float TerrainTop, float LevelMinY, float LevelMaxY);

    /// <summary>Drops the shipped pig (4) above <paramref name="drop"/>, starts the room and returns
    /// its height and speed after <see cref="Ticks"/> ticks of the real Bepu world.</summary>
    private static FallResult Fall(LevelContentDocument level, PhysicsVector3 drop)
    {
        using GameRoom room = CreateRoom(level);
        Assert.Equal(level.Terrain.Count, room.TerrainBodyCount);

        CommandOutcome placed = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, SequencePlace, 0, PartPig, drop.X, drop.Y, Angle: 0f, Scale: 1f),
            PartPlayer));
        Assert.True(placed.IsAccepted, $"place: {placed.Status}/{placed.Error}");
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, SequenceStart, 0), PartPlayer)).IsAccepted);

        float height = drop.Y;
        float speed = 0f;
        for (int tick = 0; tick < Ticks; tick += SampleTicks)
        {
            room.RunTicks(SampleTicks);
            SnapshotEntity pig = Publish(room).Single(entity => entity.EntityId == placed.EntityId);
            height = pig.Position.Y;
            speed = MathF.Sqrt(
                (pig.LinearVelocity.X * pig.LinearVelocity.X)
                + (pig.LinearVelocity.Y * pig.LinearVelocity.Y)
                + (pig.LinearVelocity.Z * pig.LinearVelocity.Z));
        }

        return new FallResult(height, speed);
    }

    /// <summary>The y-range of a document's collider terrain, in level coordinates: every loop point
    /// moved by its terrain's own position.</summary>
    private static (float Min, float Max) TerrainYRange(LevelContentDocument document)
    {
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        foreach (LevelTerrainDefinition terrain in document.Terrain)
        {
            foreach (IReadOnlyList<PhysicsVector3> loop in terrain.Loops)
            {
                foreach (PhysicsVector3 point in loop)
                {
                    min = MathF.Min(min, point.Y + terrain.Position.Y);
                    max = MathF.Max(max, point.Y + terrain.Position.Y);
                }
            }
        }

        return (min, max);
    }

    /// <summary>
    /// The first converted level (paths sorted) that has no pre-placed parts, at least two terrain
    /// objects, and a terrain whose own high ground sits strictly inside the level's y-range, so the
    /// pig's resting height is a value the document defines rather than the highest or lowest point
    /// of the level. The terrain with the most outline points wins, and the drop x is the average x
    /// of that terrain's own top points (within 0.5 m of its top), i.e. the middle of its high
    /// ground.
    /// </summary>
    private static LandingCandidate FindLandingCandidate(string root)
    {
        foreach (string file in LevelFiles(root))
        {
            LevelContentDocument document = LevelContentLibrary.Load(file).Document;
            if (document.Spawns.Count != 0 || document.Terrain.Count < 2)
            {
                continue;
            }

            (float levelMin, float levelMax) = TerrainYRange(document);
            if (levelMax - levelMin < 5f)
            {
                continue;
            }

            LevelTerrainDefinition? best = null;
            float bestTop = 0f;
            float bestX = 0f;
            int bestPoints = 0;
            foreach (LevelTerrainDefinition terrain in document.Terrain)
            {
                float top = float.NegativeInfinity;
                int points = 0;
                foreach (IReadOnlyList<PhysicsVector3> loop in terrain.Loops)
                {
                    foreach (PhysicsVector3 point in loop)
                    {
                        top = MathF.Max(top, point.Y + terrain.Position.Y);
                        points++;
                    }
                }

                if (top >= levelMax - 1f || top <= levelMin + 1f || points <= bestPoints)
                {
                    continue;
                }

                float cut = top - 0.5f;
                float sum = 0f;
                int count = 0;
                foreach (IReadOnlyList<PhysicsVector3> loop in terrain.Loops)
                {
                    foreach (PhysicsVector3 point in loop)
                    {
                        if (point.Y + terrain.Position.Y >= cut)
                        {
                            sum += point.X + terrain.Position.X;
                            count++;
                        }
                    }
                }

                best = terrain;
                bestTop = top;
                bestX = sum / count;
                bestPoints = points;
            }

            if (best is not null)
            {
                return new LandingCandidate(file, new PhysicsVector3(bestX, bestTop + 3f, best.Position.Z), bestTop, levelMin, levelMax);
            }
        }

        throw new InvalidOperationException($"No converted level under {root} offers a landing candidate.");
    }

    private static GameRoom CreateRoom(LevelContentDocument level)
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));

        // Far zones: the level's own goal must not end the run mid-fall, and the out-of-bounds reset
        // must not reset the falling control. Both are PigForge behaviours the original lacks.
        GameplayConfig config = new(
            new GameplayZone(new PhysicsVector3(4000f, 4000f, 4000f), new PhysicsVector3(4001f, 4001f, 4001f)),
            new GameplayZone(new PhysicsVector3(-5000f, -5000f, -5000f), new PhysicsVector3(5000f, 5000f, 5000f)),
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 1200,
            ObjectivesEnabled: false);
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: true));
        room.SetupFromLevel(level);
        return room;
    }

    private static List<SnapshotEntity> Publish(GameRoom room)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(256)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out _, out SnapshotFrameReader reader));
        List<SnapshotEntity> entities = new();
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            entities.Add(entity);
        }

        return entities;
    }

    private static string[] LevelFiles(string root)
    {
        string directory = Path.Combine(root, "content", "levels", "original");
        Assert.True(Directory.Exists(directory), $"Converted levels are missing: {directory}");
        string[] files = Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "content", "parts.json")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException($"Could not locate content/parts.json above {AppContext.BaseDirectory}.");
        }

        return directory.FullName;
    }
}
