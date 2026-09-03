using System.Numerics;
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
            Assert.False(BlockRegistry.IsSolid(blocks[Chunk.Index(x, VoxelWorld.WorldHeight - 1, z)]));
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
        var blocks = new byte[Chunk.Size * VoxelWorld.WorldHeight * Chunk.Size];
        generator.Generate(coord, blocks, VoxelWorld.WorldHeight);

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

/// <summary>
/// Greedy meshing merges neighbouring faces into rectangles. That is only allowed to change how the
/// surface is cut up, never the surface itself, so these tests pin the total area, the direction the
/// triangles face, and that the merging actually happens.
/// </summary>
public class GreedyMeshingTests
{
    private const int WorldHeight = 32;

    private static readonly Dictionary<int, byte[]> NoRefinements = new();

    [Fact]
    public void AnIsolatedBlockKeepsItsSixFaces()
    {
        ChunkMeshData mesh = Mesh(Solid((x, y, z) => x == 5 && y == 5 && z == 5));

        Assert.Equal(36, mesh.VertexCount); // 6 faces, 2 triangles each
        Assert.Equal(6f, Area(mesh), 3);
    }

    [Fact]
    public void MergingKeepsTheTotalSurfaceArea()
    {
        // A jumble of solid and empty blocks: plenty of merged runs, plenty of corners where the
        // ambient occlusion differs and a face has to go out on its own
        byte[] padded = Solid((x, y, z) => (x * 73 + y * 151 + z * 31) % 7 < 4);

        Assert.Equal(ExposedFaces(padded), Area(Mesh(padded)), 2);
    }

    [Fact]
    public void EveryTriangleStillFacesOutwards()
    {
        ChunkMeshData mesh = Mesh(Solid((x, y, z) => (x * 17 + y * 41 + z * 97) % 5 < 3));

        for (int triangle = 0; triangle < mesh.VertexCount / 3; triangle++)
        {
            Vector3 a = Vertex(mesh, triangle * 3);
            Vector3 b = Vertex(mesh, triangle * 3 + 1);
            Vector3 c = Vertex(mesh, triangle * 3 + 2);

            var stored = new Vector3(
                mesh.Normals[triangle * 9], mesh.Normals[triangle * 9 + 1], mesh.Normals[triangle * 9 + 2]);

            // A wrongly stretched corner would flip the winding and the face would vanish
            Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), stored) > 0f, $"triangle {triangle} is inside out");
        }
    }

    [Fact]
    public void AFlatSurfaceCollapsesIntoFarFewerQuads()
    {
        // Solid up to y = 8 including the border, so the top is the only exposed side
        byte[] padded = Solid((_, y, _2) => y <= 8);

        ChunkMeshData mesh = Mesh(padded);
        int naive = ExposedFaces(padded) * 6;

        Assert.Equal(Chunk.Size * Chunk.Size, ExposedFaces(padded));
        Assert.True(mesh.VertexCount * 4 < naive,
            $"expected the flat top to merge, got {mesh.VertexCount} of {naive} vertices");
    }

    private static ChunkMeshData Mesh(byte[] padded)
        => ChunkMesher.Build(padded, NoRefinements, WorldHeight, 0, 0);

    private static byte[] Solid(Func<int, int, int, bool> shape)
    {
        var padded = new byte[ChunkMesher.PaddedLength(WorldHeight)];

        for (int y = -1; y <= WorldHeight; y++)
        for (int z = -1; z <= Chunk.Size; z++)
        for (int x = -1; x <= Chunk.Size; x++)
            if (shape(x, y, z)) padded[ChunkMesher.Index(x, y, z)] = BlockRegistry.Terrain;

        return padded;
    }

    /// <summary>What the unmerged mesher would have emitted: one unit face per open side</summary>
    private static int ExposedFaces(byte[] padded)
    {
        (int X, int Y, int Z)[] directions = { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };
        int faces = 0;

        for (int y = 0; y < WorldHeight; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            if (!BlockRegistry.IsSolid(padded[ChunkMesher.Index(x, y, z)])) continue;

            foreach ((int dx, int dy, int dz) in directions)
                if (!BlockRegistry.IsSolid(padded[ChunkMesher.Index(x + dx, y + dy, z + dz)])) faces++;
        }

        return faces;
    }

    private static float Area(ChunkMeshData mesh)
    {
        float area = 0f;

        for (int triangle = 0; triangle < mesh.VertexCount / 3; triangle++)
            area += Vector3.Cross(
                Vertex(mesh, triangle * 3 + 1) - Vertex(mesh, triangle * 3),
                Vertex(mesh, triangle * 3 + 2) - Vertex(mesh, triangle * 3)).Length() * 0.5f;

        return area;
    }

    private static Vector3 Vertex(ChunkMeshData mesh, int index)
        => new(mesh.Vertices[index * 3], mesh.Vertices[index * 3 + 1], mesh.Vertices[index * 3 + 2]);
}
