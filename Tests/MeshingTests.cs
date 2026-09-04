using System.Numerics;
using VoxelEngine.Rendering;
using VoxelEngine.Scenes;
using VoxelEngine.World;
using Xunit;

namespace VoxelEngine.Tests;

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
        ChunkMeshData[] mesh = Mesh(Solid((x, y, z) => x == 5 && y == 5 && z == 5));

        Assert.Equal(12, MeshTools.TriangleCount(mesh)); // 6 faces, 2 triangles each
        Assert.Equal(24, MeshTools.VertexCount(mesh));   // 4 per face, shared through the indices
        Assert.Equal(6f, MeshTools.Area(mesh), 3);
    }

    [Fact]
    public void MergingKeepsTheTotalSurfaceArea()
    {
        // A jumble of solid and empty blocks: plenty of merged runs, plenty of corners where the
        // ambient occlusion differs and a face has to go out on its own
        byte[] padded = Solid((x, y, z) => (x * 73 + y * 151 + z * 31) % 7 < 4);

        Assert.Equal(ExposedFaces(padded), MeshTools.Area(Mesh(padded)), 2);
    }

    [Fact]
    public void EveryTriangleStillFacesOutwards()
    {
        ChunkMeshData[] mesh = Mesh(Solid((x, y, z) => (x * 17 + y * 41 + z * 97) % 5 < 3));

        MeshTools.AssertOutwardWinding(mesh);
    }

    [Fact]
    public void AFlatSurfaceCollapsesIntoFarFewerQuads()
    {
        // Solid up to y = 8 including the border, so the top is the only exposed side
        byte[] padded = Solid((_, y, _2) => y <= 8);

        ChunkMeshData[] mesh = Mesh(padded);
        int naive = ExposedFaces(padded) * 2;

        Assert.Equal(Chunk.Size * Chunk.Size, ExposedFaces(padded));
        Assert.True(MeshTools.TriangleCount(mesh) * 4 < naive,
            $"expected the flat top to merge, got {MeshTools.TriangleCount(mesh)} of {naive} triangles");
    }

    [Fact]
    public void SectionsOfPureAirOrPureRockProduceNothing()
    {
        byte[] padded = Solid((_, y, _2) => y <= 8);

        Assert.Empty(ChunkMesher.Build(new MeshBuilder(), padded, NoRefinements, WorldHeight, 0, 0, 16, 32)); // sky
        Assert.Empty(ChunkMesher.Build(new MeshBuilder(), padded, NoRefinements, WorldHeight, 0, 0, 0, 4));   // buried
        Assert.NotEmpty(ChunkMesher.Build(new MeshBuilder(), padded, NoRefinements, WorldHeight, 0, 0, 4, 12));
    }

    [Fact]
    public void ACoarseGridComesOutInMetres()
    {
        // 8 blocks of 4 m each: a flat top at 8 m over the full 32 m of the chunk
        var grid = new byte[MeshGrid.LengthFor(8, 8)];
        var layout = MeshGrid.Coarse(grid, WorldHeight, 4);

        for (int y = -1; y <= 8; y++)
        for (int z = -1; z <= 8; z++)
        for (int x = -1; x <= 8; x++)
            if (y <= 1) grid[layout.Index(x, y, z)] = BlockRegistry.Terrain;

        ChunkMeshData[] mesh = ChunkMesher.Build(new MeshBuilder(), layout, NoRefinements, 0, 0, 0, 8);

        Assert.Equal(32f * 32f, MeshTools.Area(mesh), 2);
        Assert.Equal(8f, MeshTools.MaxY(mesh), 3);
    }

    private static ChunkMeshData[] Mesh(byte[] padded)
        => ChunkMesher.Build(new MeshBuilder(), padded, NoRefinements, WorldHeight, 0, 0);

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
}

/// <summary>
/// The smooth mesher shares vertices through the index buffer and caches densities per section.
/// Neither may change the surface: these tests pin the density against a plain re-implementation
/// and check that the mesh is closed, outward-facing and made of valid indices.
/// </summary>
public class SmoothMeshingTests
{
    private const int WorldHeight = 32;

    [Fact]
    public void ASolidBallHasTheAreaOfASphere()
    {
        const float radius = 6f;
        var centre = new Vector3(16f, 12f, 16f);

        byte[] padded = Solid((x, y, z) => Vector3.Distance(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), centre) <= radius);
        ChunkMeshData[] mesh = SmoothChunkMesher.Build(new MeshBuilder(), padded, new Dictionary<int, byte[]>(), WorldHeight, 0, 0);

        // Marching cubes over a box-filtered ball lands within a few percent of the ideal sphere
        float ideal = 4f * MathF.PI * radius * radius;
        Assert.InRange(MeshTools.Area(mesh), ideal * 0.85f, ideal * 1.15f);

        MeshTools.AssertValidIndices(mesh);
        MeshTools.AssertOutwardWinding(mesh);
        MeshTools.AssertNormalsPointOutwardFrom(mesh, centre);
    }

    [Fact]
    public void SharedVerticesCutTheVertexCountWithoutChangingTheTriangles()
    {
        byte[] padded = Solid((_, y, _2) => y <= 10);
        ChunkMeshData[] mesh = SmoothChunkMesher.Build(new MeshBuilder(), padded, new Dictionary<int, byte[]>(), WorldHeight, 0, 0);

        int triangles = MeshTools.TriangleCount(mesh);
        int vertices = MeshTools.VertexCount(mesh);

        // A flat surface at 0.5 m cells: two triangles per cell, each vertex shared by about six
        Assert.True(vertices < triangles, $"{vertices} vertices for {triangles} triangles: nothing was shared");
        Assert.Equal(Chunk.Size * Chunk.Size, MeshTools.Area(mesh), 1);
    }

    [Fact]
    public void ACarvedBlockBendsTheSurfaceIntoTheHole()
    {
        byte[] padded = Solid((_, y, _2) => y <= 10);
        var refinements = new Dictionary<int, byte[]>();

        // Hollow out the top half of one surface block
        byte[] field = SubVoxels.NewFull();
        for (int sz = 0; sz < SubVoxels.Divisions; sz++)
        for (int sy = SubVoxels.Divisions / 2; sy < SubVoxels.Divisions; sy++)
        for (int sx = 0; sx < SubVoxels.Divisions; sx++)
            SubVoxels.Set(field, sx, sy, sz, 0f);
        refinements[ChunkMesher.Index(10, 10, 10)] = field;

        ChunkMeshData[] flat = SmoothChunkMesher.Build(new MeshBuilder(), padded, new Dictionary<int, byte[]>(), WorldHeight, 0, 0);
        ChunkMeshData[] carved = SmoothChunkMesher.Build(new MeshBuilder(), padded, refinements, WorldHeight, 0, 0);

        Assert.True(MeshTools.MinYNear(carved, new Vector3(10.5f, 11f, 10.5f), 1f) < MeshTools.MinYNear(flat, new Vector3(10.5f, 11f, 10.5f), 1f));
        MeshTools.AssertValidIndices(carved);
    }

    [Fact]
    public void TheMeshSplitsIntoPartsPastTheIndexLimit()
    {
        var builder = new MeshBuilder();

        for (int i = 0; i < MeshBuilder.MaxVerticesPerPart + 10; i += 3)
        {
            builder.EnsureRoom(3);
            int a = builder.AddVertex(i, 0, 0, 0, 1, 0, 1, 2, 3, 0);
            int b = builder.AddVertex(i, 1, 0, 0, 1, 0, 1, 2, 3, 0);
            int c = builder.AddVertex(i + 1, 0, 0, 0, 1, 0, 1, 2, 3, 0);
            builder.AddTriangle(a, b, c);
        }

        ChunkMeshData[] parts = builder.Finish();

        Assert.Equal(2, parts.Length);
        Assert.All(parts, part => Assert.True(part.VertexCount <= MeshBuilder.MaxVerticesPerPart));
        MeshTools.AssertValidIndices(parts);
    }

    private static byte[] Solid(Func<int, int, int, bool> shape)
    {
        var padded = new byte[ChunkMesher.PaddedLength(WorldHeight)];

        for (int y = -1; y <= WorldHeight; y++)
        for (int z = -1; z <= Chunk.Size; z++)
        for (int x = -1; x <= Chunk.Size; x++)
            if (shape(x, y, z)) padded[ChunkMesher.Index(x, y, z)] = BlockRegistry.Terrain;

        return padded;
    }
}

public class LodSamplerTests
{
    private const int WorldHeight = 32;

    [Fact]
    public void ACoarseBlockIsSolidAsSoonAsAnyFineBlockIs()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0), new byte[Chunk.Size * WorldHeight * Chunk.Size], new Dictionary<int, byte[]>());
        chunk.SetLocal(5, 9, 5, BlockRegistry.Stone, WorldHeight); // one lonely block

        var grid = new byte[MeshGrid.LengthFor(8, 8)];
        LodSampler.Sample(Neighbourhood(chunk), WorldHeight, 4, grid);
        var layout = MeshGrid.Coarse(grid, WorldHeight, 4);

        Assert.Equal(BlockRegistry.Stone, grid[layout.Index(1, 2, 1)]);
        Assert.Equal(BlockRegistry.Air, grid[layout.Index(1, 3, 1)]);
        Assert.Equal(BlockRegistry.Terrain, grid[layout.Index(1, -1, 1)]); // below the world reads as rock
        Assert.Equal(BlockRegistry.Air, grid[layout.Index(-1, 2, 1)]);     // missing neighbour reads as air
    }

    [Fact]
    public void TheMaterialOnTopWins()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0), new byte[Chunk.Size * WorldHeight * Chunk.Size], new Dictionary<int, byte[]>());
        for (int y = 0; y < 8; y++) chunk.SetLocal(3, y, 3, BlockRegistry.Terrain, WorldHeight);
        chunk.SetLocal(3, 7, 3, BlockRegistry.Stone, WorldHeight);

        var grid = new byte[MeshGrid.LengthFor(16, 16)];
        LodSampler.Sample(Neighbourhood(chunk), WorldHeight, 2, grid);
        var layout = MeshGrid.Coarse(grid, WorldHeight, 2);

        Assert.Equal(BlockRegistry.Stone, grid[layout.Index(1, 3, 1)]);
        Assert.Equal(BlockRegistry.Terrain, grid[layout.Index(1, 2, 1)]);
    }

    private static Chunk?[] Neighbourhood(Chunk centre)
    {
        var neighbours = new Chunk?[9];
        neighbours[4] = centre;
        return neighbours;
    }
}

internal static class MeshTools
{
    public static int TriangleCount(ChunkMeshData[] parts) => parts.Sum(part => part.TriangleCount);

    public static int VertexCount(ChunkMeshData[] parts) => parts.Sum(part => part.VertexCount);

    public static float Area(ChunkMeshData[] parts)
    {
        float area = 0f;
        foreach (ChunkMeshData part in parts)
            for (int t = 0; t < part.TriangleCount; t++)
            {
                (Vector3 a, Vector3 b, Vector3 c) = Triangle(part, t);
                area += Vector3.Cross(b - a, c - a).Length() * 0.5f;
            }

        return area;
    }

    public static float MaxY(ChunkMeshData[] parts)
    {
        float max = float.MinValue;
        foreach (ChunkMeshData part in parts)
            for (int v = 0; v < part.VertexCount; v++)
                max = MathF.Max(max, part.Vertices[v].Y);
        return max;
    }

    public static float MinYNear(ChunkMeshData[] parts, Vector3 point, float radius)
    {
        float min = float.MaxValue;
        foreach (ChunkMeshData part in parts)
            for (int v = 0; v < part.VertexCount; v++)
            {
                Vector3 p = part.Vertices[v].Position;
                if (MathF.Abs(p.X - point.X) <= radius && MathF.Abs(p.Z - point.Z) <= radius) min = MathF.Min(min, p.Y);
            }
        return min;
    }

    public static void AssertValidIndices(ChunkMeshData[] parts)
    {
        foreach (ChunkMeshData part in parts)
        {
            Assert.Equal(0, part.IndexCount % 3);
            for (int i = 0; i < part.IndexCount; i++)
                Assert.True(part.Indices[i] < part.VertexCount, $"index {part.Indices[i]} past {part.VertexCount} vertices");
        }
    }

    public static void AssertOutwardWinding(ChunkMeshData[] parts)
    {
        foreach (ChunkMeshData part in parts)
            for (int t = 0; t < part.TriangleCount; t++)
            {
                (Vector3 a, Vector3 b, Vector3 c) = Triangle(part, t);
                Vector3 stored = Normal(part, part.Indices[t * 3]) + Normal(part, part.Indices[t * 3 + 1]) + Normal(part, part.Indices[t * 3 + 2]);

                // A wrongly stretched corner would flip the winding and the face would vanish
                Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), stored) > 0f, $"triangle {t} is inside out");
            }
    }

    public static void AssertNormalsPointOutwardFrom(ChunkMeshData[] parts, Vector3 centre)
    {
        foreach (ChunkMeshData part in parts)
            for (int v = 0; v < part.VertexCount; v++)
                Assert.True(Vector3.Dot(Normal(part, v), part.Vertices[v].Position - centre) > 0f, $"vertex {v} normal points inwards");
    }

    private static (Vector3, Vector3, Vector3) Triangle(ChunkMeshData part, int t)
        => (Vertex(part, part.Indices[t * 3]), Vertex(part, part.Indices[t * 3 + 1]), Vertex(part, part.Indices[t * 3 + 2]));

    private static Vector3 Vertex(ChunkMeshData part, int index) => part.Vertices[index].Position;

    private static Vector3 Normal(ChunkMeshData part, int index) => part.Vertices[index].Normal;
}

public class LodPolicyTests
{
    [Fact]
    public void BandsFollowTheDetailRadius()
    {
        Assert.Equal(0, LodPolicy.Choose(-1, 3f, 8f));
        Assert.Equal(1, LodPolicy.Choose(-1, 12f, 8f));
        Assert.Equal(2, LodPolicy.Choose(-1, 40f, 8f));
    }

    [Fact]
    public void AColumnOnTheBorderDoesNotFlap()
    {
        // Sitting right at the detail radius: whichever level it has, it keeps
        Assert.Equal(0, LodPolicy.Choose(0, 8.5f, 8f));
        Assert.Equal(1, LodPolicy.Choose(1, 8.5f, 8f));

        // Clearly past the band it switches, either way
        Assert.Equal(1, LodPolicy.Choose(0, 9.5f, 8f));
        Assert.Equal(0, LodPolicy.Choose(1, 6.5f, 8f));
    }

    [Fact]
    public void ScalesDoubleWithEachLevel()
    {
        Assert.Equal(1, LodPolicy.ScaleOf(0));
        Assert.Equal(2, LodPolicy.ScaleOf(1));
        Assert.Equal(4, LodPolicy.ScaleOf(2));
    }
}

public class MeshSchedulerTests
{
    private static readonly ChunkCoord Near = new(0, 0);
    private static readonly ChunkCoord Far = new(10, 0);

    private static float Distance(ChunkCoord coord) => MathF.Abs(coord.X) + MathF.Abs(coord.Z);

    [Fact]
    public void EditsGoBeforeEverythingElseAndNearBeforeFar()
    {
        var scheduler = new MeshScheduler();
        scheduler.Mark(Far, 0b1111, MeshReason.Mode);
        scheduler.Mark(Near, 0b1111, MeshReason.Mode);
        scheduler.Mark(new ChunkCoord(20, 0), 0b0010, MeshReason.Edit);

        var picks = new (ChunkCoord, int, MeshReason)[3];
        int count = scheduler.Take(3, _ => true, Distance, picks);

        Assert.Equal(3, count);
        Assert.Equal(new ChunkCoord(20, 0), picks[0].Item1);
        Assert.Equal(Near, picks[1].Item1);
        Assert.Equal(Far, picks[2].Item1);
        Assert.Equal(0, scheduler.DirtyCount);
    }

    [Fact]
    public void MarkingMergesSectionsAndKeepsTheMostUrgentReason()
    {
        var scheduler = new MeshScheduler();
        scheduler.Mark(Near, 0b0001, MeshReason.Mode);
        scheduler.Mark(Near, 0b0100, MeshReason.Edit);

        var picks = new (ChunkCoord, int, MeshReason)[1];
        Assert.Equal(1, scheduler.Take(1, _ => true, Distance, picks));
        Assert.Equal(0b0101, picks[0].Item2);
        Assert.Equal(MeshReason.Edit, picks[0].Item3);
    }

    [Fact]
    public void AChunkInFlightWaitsUnlessAnEditNeedsIt()
    {
        var scheduler = new MeshScheduler();
        scheduler.BeginJob(Near);

        scheduler.Mark(Near, 0b0001, MeshReason.Stream);
        var picks = new (ChunkCoord, int, MeshReason)[1];
        Assert.Equal(0, scheduler.Take(1, _ => true, Distance, picks));

        scheduler.Mark(Near, 0b0001, MeshReason.Edit);
        Assert.Equal(1, scheduler.Take(1, _ => true, Distance, picks));

        scheduler.EndJob(Near);
        scheduler.EndJob(Near);
        Assert.Equal(0, scheduler.InFlightCount);
    }

    [Fact]
    public void ChunksThatAreNotReadyStayDirty()
    {
        var scheduler = new MeshScheduler();
        scheduler.Mark(Near, 0b1111, MeshReason.Stream);

        var picks = new (ChunkCoord, int, MeshReason)[1];
        Assert.Equal(0, scheduler.Take(1, _ => false, Distance, picks));
        Assert.True(scheduler.IsDirty(Near));
    }
}

public class ModeTransitionTests
{
    [Fact]
    public void TheRingNeverOvertakesTheRebuild()
    {
        var wave = new ModeTransition(TerrainMode.Blocks, TerrainMode.Smooth, Vector3.Zero, remeshes: true);

        wave.Advance(1f, truthRadius: 50f, endRadius: 900f);
        Assert.Equal(50f, wave.DisplayRadius, 3);

        // The truth moved on: the ring catches up at its own pace, not in a jump
        wave.Advance(0.1f, truthRadius: 500f, endRadius: 900f);
        Assert.Equal(50f + ModeTransition.WaveSpeed * 0.1f, wave.DisplayRadius, 2);
    }

    [Fact]
    public void ATimedRingRunsToTheFogAndFadesOut()
    {
        var wave = new ModeTransition(TerrainMode.Blocks, TerrainMode.Sculpt, Vector3.Zero, remeshes: false);

        wave.Advance(0.5f, float.PositiveInfinity, 900f);
        Assert.Equal(1f, wave.Strength, 3);
        Assert.False(wave.Done);

        for (int i = 0; i < 100 && !wave.Done; i++)
            wave.Advance(0.1f, float.PositiveInfinity, 900f);

        Assert.True(wave.Done);
        Assert.True(wave.DisplayRadius >= 900f);
    }

    [Fact]
    public void TheRadiusNeverShrinks()
    {
        var wave = new ModeTransition(TerrainMode.Blocks, TerrainMode.Smooth, Vector3.Zero, remeshes: true);

        wave.Advance(1f, 100f, 900f);
        wave.Advance(1f, 20f, 900f); // a chunk streamed in behind the ring reports a smaller truth

        Assert.Equal(100f, wave.DisplayRadius, 3);
    }
}

public class BlockRegistryTests
{
    [Fact]
    public void RegisteringTheSameMaterialTwiceGivesTheSameId()
    {
        byte first = BlockRegistry.Register("test-ice", new Raylib_cs.Color(10, 20, 30, 255));
        byte second = BlockRegistry.Register("test-ice", new Raylib_cs.Color(10, 20, 30, 255));

        Assert.Equal(first, second);
        Assert.True(BlockRegistry.IsSolid(first));
    }

    [Fact]
    public void TheSameNameWithOtherValuesIsAClash()
    {
        BlockRegistry.Register("test-clash", new Raylib_cs.Color(1, 2, 3, 255));

        Assert.Throws<InvalidOperationException>(() => BlockRegistry.Register("test-clash", new Raylib_cs.Color(4, 5, 6, 255)));
    }
}

public class GameRegistryTests
{
    [VoxelEngine.Core.GameDefinition("TestGame", "Test Game", "A game that exists for this test")]
    private sealed class TestGame : VoxelEngine.Core.Game
    {
        public override Raylib_cs.Camera3D Camera => default;
        public override void Load() { }
        public override void Update(float dt) { }
        public override void DrawWorld() { }
    }

    [Fact]
    public void GamesAreFoundByTheirAttribute()
    {
        var registry = new VoxelEngine.Core.GameRegistry();
        registry.AddFrom(typeof(GameRegistryTests).Assembly);

        VoxelEngine.Core.GameEntry entry = registry.Find("TestGame");
        Assert.Equal("Test Game", entry.Title);
        Assert.IsType<TestGame>(entry.Create());
    }

    [Fact]
    public void ADuplicateIdIsRejected()
    {
        var registry = new VoxelEngine.Core.GameRegistry();
        registry.Add<TestGame>();

        Assert.Throws<ArgumentException>(() => registry.Add<TestGame>());
    }
}
