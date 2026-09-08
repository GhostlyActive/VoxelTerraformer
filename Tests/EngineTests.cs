using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.Input;
using VoxelEngine.MathTools;
using VoxelEngine.Rendering;
using VoxelEngine.World;
using Xunit;

namespace VoxelEngine.Tests;

/// <summary>
/// The engine's pure logic — everything that runs without a window. Rendering and audio need a GL
/// context and are covered by the smoke run instead.
/// </summary>
public class NoiseTests
{
    [Fact]
    public void SameSeedGivesSameValue()
    {
        Assert.Equal(Noise.Value2D(12.3f, 45.6f, 7), Noise.Value2D(12.3f, 45.6f, 7));
        Assert.Equal(Noise.Value3D(1.5f, 2.5f, 3.5f, 9), Noise.Value3D(1.5f, 2.5f, 3.5f, 9));
    }

    [Fact]
    public void DifferentSeedsDiverge()
    {
        Assert.NotEqual(Noise.Value2D(12.3f, 45.6f, 7), Noise.Value2D(12.3f, 45.6f, 8));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(3.7f, -12.4f)]
    [InlineData(-100.25f, 88.75f)]
    public void StaysInUnitRange(float x, float z)
    {
        Assert.InRange(Noise.Value2D(x, z, 1), 0f, 1f);
        Assert.InRange(Noise.Fbm2D(x, z, 1, 4, 0.5f, 2f), 0f, 1f);
        Assert.InRange(Noise.RidgeFbm2D(x, z, 1, 4, 0.5f, 2f), 0f, 1f);
        Assert.InRange(Noise.Fbm3D(x, 5f, z, 1, 3, 0.5f, 2f), 0f, 1f);
    }
}

public class SubVoxelTests
{
    [Fact]
    public void FullFieldIsFullAndEmptyFieldIsEmpty()
    {
        Assert.True(SubVoxels.IsFull(SubVoxels.NewFull()));
        Assert.True(SubVoxels.IsEmpty(SubVoxels.NewEmpty()));
        Assert.False(SubVoxels.IsEmpty(SubVoxels.NewFull()));
    }

    [Fact]
    public void CarvingOneCellBreaksFullness()
    {
        byte[] field = SubVoxels.NewFull();
        SubVoxels.Set(field, 3, 4, 5, 0f);

        Assert.False(SubVoxels.IsFull(field));
        Assert.False(SubVoxels.IsSolid(field, 3, 4, 5));
        Assert.True(SubVoxels.IsSolid(field, 3, 4, 6));
    }

    [Fact]
    public void FillBelowEpsilonRoundsToEmpty()
    {
        // Fill runs 0..255. Without this rounding the soft brush edge would leave a trace of
        // density everywhere and a hollowed-out block would never become air again.
        Assert.Equal(0, SubVoxels.Quantize(SubVoxels.Epsilon - 1f));
        Assert.True(SubVoxels.Quantize(SubVoxels.Epsilon + 1f) > 0);
        Assert.True(SubVoxels.Quantize(255f) >= SubVoxels.Iso);
    }

    [Fact]
    public void EveryCellIsAddressedExactlyOnce()
    {
        var seen = new HashSet<int>();

        for (int z = 0; z < SubVoxels.Divisions; z++)
        for (int y = 0; y < SubVoxels.Divisions; y++)
        for (int x = 0; x < SubVoxels.Divisions; x++)
            Assert.True(seen.Add(SubVoxels.CellIndex(x, y, z)));

        Assert.Equal(SubVoxels.CellCount, seen.Count);
    }
}

public class TerrainGeneratorTests
{
    private const int WorldHeight = 64;

    [Fact]
    public void SameCoordinateGivesTheSameChunk()
    {
        var generator = new DefaultTerrainGenerator();

        byte[] first = Generate(generator, new ChunkCoord(3, -7));
        byte[] second = Generate(generator, new ChunkCoord(3, -7));

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentSeedsGiveDifferentTerrain()
    {
        byte[] first = Generate(new DefaultTerrainGenerator { Seed = 1 }, new ChunkCoord(0, 0));
        byte[] second = Generate(new DefaultTerrainGenerator { Seed = 2 }, new ChunkCoord(0, 0));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void LeavesTheTopAndBottomOfTheWorldAlone()
    {
        byte[] blocks = Generate(new DefaultTerrainGenerator(), new ChunkCoord(11, 4));

        for (int x = 0; x < Chunk.Size; x++)
        for (int z = 0; z < Chunk.Size; z++)
        {
            Assert.True(BlockRegistry.IsSolid(blocks[Chunk.Index(x, 0, z)]));
            Assert.False(BlockRegistry.IsSolid(blocks[Chunk.Index(x, WorldHeight - 1, z)]));
        }
    }

    [Fact]
    public void CavesHollowOutRockThatTheFlatGeneratorLeavesSolid()
    {
        var solid = new DefaultTerrainGenerator { Caves = 0f, Spires = 0f };
        var hollow = new DefaultTerrainGenerator { Caves = 1f, Spires = 0f };

        Assert.True(CountSolid(Generate(hollow, new ChunkCoord(2, 2)))
                    < CountSolid(Generate(solid, new ChunkCoord(2, 2))));
    }

    private static byte[] Generate(ITerrainGenerator generator, ChunkCoord coord)
    {
        var blocks = new byte[Chunk.Size * WorldHeight * Chunk.Size];
        generator.Generate(coord, blocks, WorldHeight);

        return blocks;
    }

    private static int CountSolid(byte[] blocks)
    {
        int solid = 0;
        foreach (byte block in blocks)
            if (BlockRegistry.IsSolid(block)) solid++;

        return solid;
    }
}

public class VoxelBodyTests
{
    private static VoxelBody Sphere(float voxelScale = 2f)
    {
        var body = new VoxelBody("test", size: 32, voxelScale: voxelScale);
        body.FillSphere(radiusVoxels: 14f, crust: BlockRegistry.Stone, core: BlockRegistry.Terrain,
            seed: 5, roughness: 0f, crustDepth: 3);

        return body;
    }

    [Fact]
    public void SizeMustBeAMultipleOfTheChunkSize()
        => Assert.Throws<ArgumentException>(() => new VoxelBody("bad", size: 40, voxelScale: 1f));

    [Fact]
    public void FillSphereSetsTheRadiiAndFillsTheMiddle()
    {
        VoxelBody body = Sphere();

        Assert.Equal(28f, body.SurfaceRadius);     // 14 voxels * scale 2
        Assert.Equal(22f, body.CoreRadius);        // (14 - 3) voxels * scale 2
        Assert.True(body.CountSolid() > 0);
        Assert.True(body.IsSolidAt(body.Position));
    }

    [Fact]
    public void CarveRemovesMaterialAndReportsWhatItTook()
    {
        VoxelBody body = Sphere();
        int before = body.CountSolid();

        var reported = new List<byte>();
        int removed = body.Carve(body.Position, 10f, BlockRegistry.Terrain, out int coreHits,
            (_, block) => reported.Add(block));

        Assert.True(removed > 0);
        Assert.Equal(before - removed, body.CountSolid());
        Assert.Equal(removed, reported.Count);
        Assert.True(coreHits > 0);
        Assert.False(body.IsSolidAt(body.Position));
    }

    [Fact]
    public void CarveWellOutsideTheBodyChangesNothing()
    {
        VoxelBody body = Sphere();

        Assert.Equal(0, body.Carve(body.Position + new Vector3(10_000f, 0f, 0f), 5f));
    }

    [Fact]
    public void ReportEveryTakesASample()
    {
        VoxelBody body = Sphere();

        int reported = 0;
        int removed = body.Carve(body.Position, 12f, BlockRegistry.Air, out _, (_, _) => reported++, 10);

        Assert.Equal(removed / 10, reported);
    }

    [Fact]
    public void WorldAndLocalCoordinatesRoundTrip()
    {
        var body = new VoxelBody("spun", size: 32, voxelScale: 3f) { Position = new Vector3(100f, -20f, 7f) };
        body.Spin = 0.9f;

        var point = new Vector3(112f, -8f, 15f);
        Vector3 there = body.ToWorld(body.ToLocal(point));

        Assert.True(Vector3.Distance(point, there) < 0.01f);
    }

    [Fact]
    public void CarvingDirtiesOnlyTheSectionsItTouched()
    {
        var body = new VoxelBody("big", size: 96, voxelScale: 1f);
        body.FillSphere(44f, BlockRegistry.Stone, BlockRegistry.Stone, seed: 1, roughness: 0f, crustDepth: 96);

        for (int i = 0; i < body.ChunkCount; i++)
            body.MarkChunkClean(i);

        // A small bite well inside the sphere must not force the whole body to be remeshed
        int removed = body.Carve(body.ToWorld(new Vector3(30f, 48f, 48f)), 3f);
        Assert.True(removed > 0);

        int dirty = 0;
        for (int i = 0; i < body.ChunkCount; i++)
            if (body.IsChunkDirty(i)) dirty++;

        Assert.InRange(dirty, 1, 8);
    }
}

public class DayNightCycleTests
{
    [Fact]
    public void SunAngleRoundTrips()
    {
        var cycle = new DayNightCycle { DayLengthSeconds = 240f, SunAngleDegrees = 217f };

        Assert.Equal(217f, cycle.SunAngleDegrees, 2);
    }

    [Fact]
    public void SunAngleWrapsIntoZeroToThreeSixty()
    {
        var cycle = new DayNightCycle { DayLengthSeconds = 240f, SunAngleDegrees = -30f };

        Assert.Equal(330f, cycle.SunAngleDegrees, 2);
    }

    [Fact]
    public void NoonIsBrightAndMidnightIsNot()
    {
        var noon = new DayNightCycle { AutoAdvance = false, SunAngleDegrees = 90f };
        var midnight = new DayNightCycle { AutoAdvance = false, SunAngleDegrees = 270f };

        noon.Update(0f);
        midnight.Update(0f);

        Assert.True(noon.Daylight01 > 0.9f);
        Assert.True(midnight.Daylight01 < 0.1f);
    }

    [Fact]
    public void AStoppedClockDoesNotMoveTheSun()
    {
        var cycle = new DayNightCycle { AutoAdvance = false, SunAngleDegrees = 90f, TimeScale = 5f };
        cycle.Update(10f);

        Assert.Equal(90f, cycle.SunAngleDegrees, 2);
    }
}

public class FreeFlyControllerTests
{
    private static FreeFlyController Ship() => new(Vector3.Zero, new EngineSettings { MouseSensitivity = 0.5f });

    private static FlightInput Look(float x, float y) => FlightInput.None with { Look = new Vector2(x, y) };

    [Fact]
    public void StartsLevelAlongZ()
    {
        FreeFlyController ship = Ship();

        Assert.True(Vector3.Distance(ship.Forward, Vector3.UnitZ) < 1e-4f);
        Assert.True(Vector3.Distance(ship.Up, Vector3.UnitY) < 1e-4f);
    }

    [Fact]
    public void MouseRightTurnsRightAndMouseUpPitchesUp()
    {
        FreeFlyController ship = Ship();
        Vector3 right = ship.Right;

        ship.Step(Look(40f, 0f), 0.016f);
        Assert.True(Vector3.Dot(ship.Forward, right) > 0.2f, "mouse right should turn the nose right");

        ship = Ship();
        ship.Step(Look(0f, -40f), 0.016f);
        Assert.True(ship.Forward.Y > 0.2f, "mouse up should lift the nose");
    }

    [Fact]
    public void PullingUpLongEnoughLoopsInsteadOfStoppingAtTheZenith()
    {
        FreeFlyController ship = Ship();

        // 180 degrees of pitch in small steps: a horizon-locked camera would stop at 89
        for (int i = 0; i < 360; i++)
            ship.Step(Look(0f, -1f), 0.016f);

        Assert.True(Vector3.Distance(ship.Forward, -Vector3.UnitZ) < 0.05f, $"nose should point back, was {ship.Forward}");
        Assert.True(Vector3.Distance(ship.Up, -Vector3.UnitY) < 0.05f, $"ship should be upside down, was {ship.Up}");
    }

    [Fact]
    public void RollTiltsTheUpTowardsTheRight()
    {
        FreeFlyController ship = Ship();
        Vector3 right = ship.Right;

        ship.Step(FlightInput.None with { Roll = 1f }, 0.5f);

        Assert.True(Vector3.Dot(ship.Up, right) > 0.3f, "rolling right should tip the ship over its right side");
        Assert.True(Vector3.Distance(ship.Forward, Vector3.UnitZ) < 1e-3f, "roll must not move the nose");
    }

    [Fact]
    public void ThrustPushesAlongTheShipsOwnAxes()
    {
        FreeFlyController ship = Ship();
        ship.Thrust = 100f;
        ship.Damping = 0f;

        // Nose down first, then forward thrust: the ship dives, it does not slide along Z
        for (int i = 0; i < 180; i++)
            ship.Step(Look(0f, 1f), 0.016f);

        ship.Step(FlightInput.None with { Move = Vector3.UnitZ }, 0.5f);

        Assert.True(ship.Velocity.Y < -20f, $"thrust should follow the dive, velocity was {ship.Velocity}");
    }

    [Fact]
    public void PointAtLooksAtTheTargetWithTheWorldUpOnScreen()
    {
        FreeFlyController ship = Ship();
        ship.Position = new Vector3(10f, 5f, -3f);

        ship.PointAt(new Vector3(-40f, 5f, 60f));

        Vector3 expected = Vector3.Normalize(new Vector3(-50f, 0f, 63f));
        Assert.True(Vector3.Distance(ship.Forward, expected) < 1e-3f);
        Assert.True(ship.Up.Y > 0.99f);
    }
}

public class JetpackTests
{
    private const int Ground = 40;

    private sealed class FlatGenerator : ITerrainGenerator
    {
        public void Generate(ChunkCoord coord, byte[] blocks, int worldHeight)
        {
            for (int y = 0; y < Ground; y++)
            for (int z = 0; z < Chunk.Size; z++)
            for (int x = 0; x < Chunk.Size; x++)
                blocks[Chunk.Index(x, y, z)] = BlockRegistry.Terrain;
        }
    }

    private static (VoxelWorld World, PlayerController Player) Standing(bool jetpack)
    {
        var world = new VoxelWorld(null, new FlatGenerator(), viewDistanceChunks: 2, worldHeight: 64);
        var start = new Vector3(16f, Ground + 0.01f, 16f);
        world.EnsureAround(start, 1);

        var player = new PlayerController(start, new EngineSettings(), new TerrainSettings()) { JetpackEnabled = jetpack };
        player.Step(world, PlayerInput.None, 0.016f); // settle onto the ground

        return (world, player);
    }

    private static float Fly(VoxelWorld world, PlayerController player, float seconds, bool hold)
    {
        float highest = player.Position.Y;
        var first = PlayerInput.None with { JumpPressed = true, JumpHeld = hold };
        var held = PlayerInput.None with { JumpHeld = hold };

        for (float t = 0f; t < seconds; t += 0.016f)
        {
            player.Step(world, t == 0f ? first : held, 0.016f);
            highest = MathF.Max(highest, player.Position.Y);
        }

        return highest;
    }

    [Fact]
    public void HoldingJumpInTheAirClimbsHigherThanAJump()
    {
        (VoxelWorld world, PlayerController walker) = Standing(jetpack: true);
        float jump = Fly(world, walker, 3f, hold: false);
        world.Dispose();

        (world, PlayerController flyer) = Standing(jetpack: true);
        float flight = Fly(world, flyer, 3f, hold: true);
        world.Dispose();

        Assert.InRange(jump - Ground, 1f, 6f);
        Assert.True(flight > jump + 8f, $"jetpack reached {flight - Ground:0.0} m, a jump {jump - Ground:0.0} m");
    }

    [Fact]
    public void WithoutAJetpackHoldingJumpIsJustAJump()
    {
        (VoxelWorld world, PlayerController player) = Standing(jetpack: false);
        float height = Fly(world, player, 3f, hold: true);
        world.Dispose();

        Assert.InRange(height - Ground, 1f, 6f);
        Assert.Equal(1f, player.Fuel01);
    }

    [Fact]
    public void GodmodeHoversInsteadOfFalling()
    {
        (VoxelWorld world, PlayerController player) = Standing(jetpack: true);
        player.Godmode = true;

        float startedAt = player.Position.Y;
        for (float t = 0f; t < 3f; t += 0.016f)
            player.Step(world, PlayerInput.None, 0.016f);

        world.Dispose();

        // Not pinned to the millimetre: the velocity the player arrived with is eased out rather
        // than cut, so a few centimetres of drift are the point. Gravity would cost metres.
        Assert.InRange(player.Position.Y, startedAt - 0.1f, startedAt + 0.1f);
    }

    [Fact]
    public void GodmodeRisesOnJumpAndSinksOnDescendStraightThroughTheGround()
    {
        (VoxelWorld world, PlayerController player) = Standing(jetpack: false);
        player.Godmode = true;

        float startedAt = player.Position.Y;
        for (float t = 0f; t < 2f; t += 0.016f)
            player.Step(world, PlayerInput.None with { JumpHeld = true }, 0.016f);

        float climbed = player.Position.Y;
        Assert.True(climbed > startedAt + 20f, $"free flight climbed {climbed - startedAt:0.0} m in two seconds");

        // Down through solid terrain: godmode ignores collision, so the floor is no floor
        for (float t = 0f; t < 4f; t += 0.016f)
            player.Step(world, PlayerInput.None with { Descend = true }, 0.016f);

        world.Dispose();
        Assert.True(player.Position.Y < Ground - 5f, $"descending stopped at {player.Position.Y:0.0}, ground is {Ground}");
    }

    [Fact]
    public void LeavingGodmodeHandsBackAFullTankAndGravity()
    {
        (VoxelWorld world, PlayerController player) = Standing(jetpack: true);
        player.Godmode = true;

        for (float t = 0f; t < 2f; t += 0.016f)
            player.Step(world, PlayerInput.None with { JumpHeld = true }, 0.016f);

        player.Godmode = false;
        Assert.Equal(1f, player.Fuel01);

        for (float t = 0f; t < 12f; t += 0.016f)
            player.Step(world, PlayerInput.None, 0.016f);

        world.Dispose();
        Assert.True(player.IsGrounded, $"the player came down to {player.Position.Y:0.0} instead of the ground at {Ground}");
    }

    [Fact]
    public void TheTankEmptiesInTheAirAndRefillsOnTheGround()
    {
        (VoxelWorld world, PlayerController player) = Standing(jetpack: true);

        Fly(world, player, 2f, hold: true);
        Assert.True(player.Fuel01 < 0.4f, $"fuel after a long burn was {player.Fuel01}");

        // Let go and wait: the player comes down and the tank fills back up
        for (float t = 0f; t < 12f; t += 0.016f)
            player.Step(world, PlayerInput.None, 0.016f);

        Assert.True(player.IsGrounded);
        Assert.True(player.Fuel01 > 0.99f, $"fuel after resting was {player.Fuel01}");
        world.Dispose();
    }
}
