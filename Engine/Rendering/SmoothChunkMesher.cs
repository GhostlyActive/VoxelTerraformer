using Raylib_cs;
using System.Numerics;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// The third voxel mode: marching cubes over the sub-voxel density field, smoothed with a box
/// filter. Because the cells carry real fill values rather than just on and off, the iso surface
/// lands at the exact spot between two grid points, which gives rounded shapes instead of stairs.
/// Soft normals come from the density gradient. The world data is untouched, which is what makes
/// switching between the modes lossless.
///
/// The density of a grid point depends on its position alone, so it is computed once per section
/// and cached; neighbouring blocks share it instead of each summing the same sub-voxels again.
/// Likewise a vertex on a cell edge is shared by every cell around that edge through the index
/// buffer, which is what keeps the surface at a fraction of the memory of a triangle soup.
///
/// A coarse grid (distant level of detail) is meshed with one cell per block: the density is then
/// the share of solid blocks around a grid point, which still rounds the far hills off.
/// </summary>
public static class SmoothChunkMesher
{
    /// <summary>
    /// Marching-cubes cells per block at full detail; has to divide 8 (1, 2, 4 or 8).
    /// 2 gives 0.5 m cells: a very soft landscape. 4 gives 0.25 m cells: finer, but four times the geometry.
    /// </summary>
    public const int Divisions = 2;

    private const float Iso = SubVoxels.Iso / 255f;
    private const int GridOffset = 1;

    // Edge axis and lower corner of each of the 12 cube edges, derived from the tables
    private static readonly int[] _edgeAxis = new int[12];
    private static readonly int[] _edgeOrigin = new int[12];

    // Which edges touch which corner; saves computing gradients that are never used
    private static readonly int[] _cornerEdgeMask = new int[8];

    static SmoothChunkMesher()
    {
        for (int edge = 0; edge < 12; edge++)
        {
            int a = MarchingCubesTables.EdgeCornerA[edge];
            int b = MarchingCubesTables.EdgeCornerB[edge];

            _cornerEdgeMask[a] |= 1 << edge;
            _cornerEdgeMask[b] |= 1 << edge;

            if (MarchingCubesTables.CornerX[a] != MarchingCubesTables.CornerX[b])
            {
                _edgeAxis[edge] = 0;
                _edgeOrigin[edge] = MarchingCubesTables.CornerX[a] < MarchingCubesTables.CornerX[b] ? a : b;
            }
            else if (MarchingCubesTables.CornerY[a] != MarchingCubesTables.CornerY[b])
            {
                _edgeAxis[edge] = 1;
                _edgeOrigin[edge] = MarchingCubesTables.CornerY[a] < MarchingCubesTables.CornerY[b] ? a : b;
            }
            else
            {
                _edgeAxis[edge] = 2;
                _edgeOrigin[edge] = MarchingCubesTables.CornerZ[a] < MarchingCubesTables.CornerZ[b] ? a : b;
            }
        }
    }

    /// <summary>
    /// Scratch memory of one worker thread: the density, normal and edge-vertex caches of the
    /// section being built, plus which blocks carry a refinement. The caches are stamped rather
    /// than cleared, so starting a section costs nothing.
    /// </summary>
    private sealed class Scratch
    {
        public float[] Density = Array.Empty<float>();
        public Vector3[] Normal = Array.Empty<Vector3>();
        public int[] DensityStamp = Array.Empty<int>();
        public int[] NormalStamp = Array.Empty<int>();
        public int[] EdgeVertex = Array.Empty<int>();
        public int[] EdgeStamp = Array.Empty<int>();
        public byte[] Refined = Array.Empty<byte>();
        public readonly byte[] NeighborId = new byte[27];
        public readonly bool[] NeighborSolid = new bool[27];

        public int Stamp;
        public int GridX;
        public int OriginY;
        public int EdgeGridX;
        public int EdgeOriginY;

        public void Begin(int paddedLength, int size, int divisions, int yStart, int yEnd)
        {
            int rows = yEnd - yStart;

            GridX = size * divisions + 3;
            OriginY = yStart * divisions;
            EdgeGridX = size * divisions + 1;
            EdgeOriginY = yStart * divisions;

            int densitySize = GridX * (rows * divisions + 3) * GridX;
            if (Density.Length < densitySize)
            {
                Density = new float[densitySize];
                Normal = new Vector3[densitySize];
                DensityStamp = new int[densitySize];
                NormalStamp = new int[densitySize];
            }

            int edgeSize = 3 * EdgeGridX * (rows * divisions + 1) * EdgeGridX;
            if (EdgeVertex.Length < edgeSize)
            {
                EdgeVertex = new int[edgeSize];
                EdgeStamp = new int[edgeSize];
            }

            if (Refined.Length < paddedLength) Refined = new byte[paddedLength];
            else Array.Clear(Refined, 0, paddedLength);

            // Stamps wrap after two billion sections; clearing then keeps the caches honest
            if (++Stamp > int.MaxValue - 4096)
            {
                Array.Clear(DensityStamp);
                Array.Clear(NormalStamp);
                Array.Clear(EdgeStamp);
                Stamp = 1;
            }
        }

        public int PointIndex(int gx, int gy, int gz)
            => (gx + GridOffset) + GridX * ((gz + GridOffset) + GridX * (gy - OriginY + GridOffset));

        public int EdgeIndex(int axis, int gx, int gy, int gz)
            => axis + 3 * (gx + EdgeGridX * (gz + EdgeGridX * (gy - EdgeOriginY)));
    }

    /// <summary>Everything one section build reads, bundled so the helpers stay short</summary>
    private readonly struct Context
    {
        public readonly Scratch Scratch;
        public readonly MeshGrid Grid;
        public readonly Dictionary<int, byte[]> Refinements;
        public readonly int SubPerCell;

        public Context(Scratch scratch, in MeshGrid grid, Dictionary<int, byte[]> refinements, int divisions)
        {
            Scratch = scratch;
            Grid = grid;
            Refinements = refinements;
            SubPerCell = SubVoxels.Divisions / divisions;
        }
    }

    [ThreadStatic] private static Scratch? _scratch;

    public static ChunkMeshData[] Build(
        MeshBuilder builder, byte[] padded, Dictionary<int, byte[]> refinements, int worldHeight, int worldX, int worldZ)
        => Build(builder, MeshGrid.FullDetail(padded, worldHeight), refinements, worldX, worldZ, 0, worldHeight);

    public static ChunkMeshData[] Build(
        MeshBuilder builder, byte[] padded, Dictionary<int, byte[]> refinements, int worldHeight, int worldX, int worldZ,
        int yStart, int yEnd)
        => Build(builder, MeshGrid.FullDetail(padded, worldHeight), refinements, worldX, worldZ, yStart, yEnd);

    /// <summary>
    /// Meshes the rows [yStart, yEnd) of <paramref name="grid"/>. Every block emits its own cells,
    /// so sections partition a column without seams or duplicated geometry — but a block reads its
    /// 3x3x3 neighbourhood, so the caller has to dirty one section beyond an edit. Refinements only
    /// exist at full detail; a coarse grid passes an empty dictionary and gets one cell per block.
    /// </summary>
    public static ChunkMeshData[] Build(
        MeshBuilder builder, in MeshGrid grid, Dictionary<int, byte[]> refinements, int worldX, int worldZ, int yStart, int yEnd)
    {
        builder.Clear();

        if (!MeshSectionScan.CanHaveSurface(in grid, refinements, yStart, yEnd)) return Array.Empty<ChunkMeshData>();

        int divisions = grid.Scale == 1 ? Divisions : 1;
        float cellSize = grid.Scale / (float)divisions;

        Scratch scratch = _scratch ??= new Scratch();
        scratch.Begin(grid.Length, grid.Size, divisions, yStart, yEnd);

        foreach (int index in refinements.Keys)
            scratch.Refined[index] = 1;

        var context = new Context(scratch, in grid, refinements, divisions);

        Span<float> cornerDensity = stackalloc float[8];
        Span<Vector3> cornerNormal = stackalloc Vector3[8];
        Span<int> edgeVertex = stackalloc int[12];

        for (int y = yStart; y < yEnd; y++)
        for (int z = 0; z < grid.Size; z++)
        for (int x = 0; x < grid.Size; x++)
        {
            if (!GatherNeighborhood(in grid, scratch, x, y, z)) continue;

            (Color albedo, byte emissive) = SurfaceMaterial(
                scratch.NeighborId, scratch.NeighborSolid, x, y, z, worldX, worldZ, grid.Scale);

            for (int cz = 0; cz < divisions; cz++)
            for (int cy = 0; cy < divisions; cy++)
            for (int cx = 0; cx < divisions; cx++)
            {
                int gx0 = x * divisions + cx;
                int gy0 = y * divisions + cy;
                int gz0 = z * divisions + cz;

                int cubeIndex = 0;
                for (int corner = 0; corner < 8; corner++)
                {
                    float value = DensityAt(in context,
                        gx0 + MarchingCubesTables.CornerX[corner],
                        gy0 + MarchingCubesTables.CornerY[corner],
                        gz0 + MarchingCubesTables.CornerZ[corner]);

                    cornerDensity[corner] = value;
                    if (value < Iso) cubeIndex |= 1 << corner;
                }

                int edges = MarchingCubesTables.EdgeMask[cubeIndex];
                if (edges == 0) continue;

                // A part boundary invalidates every cached vertex index: bump the stamp so the
                // edge cache starts over with the new part (the density cache is carried over)
                if (builder.EnsureRoom(12)) RestartEdgeCache(scratch);

                for (int corner = 0; corner < 8; corner++)
                {
                    if ((edges & _cornerEdgeMask[corner]) == 0) continue;

                    cornerNormal[corner] = GradientNormal(in context,
                        gx0 + MarchingCubesTables.CornerX[corner],
                        gy0 + MarchingCubesTables.CornerY[corner],
                        gz0 + MarchingCubesTables.CornerZ[corner]);
                }

                for (int edge = 0; edge < 12; edge++)
                {
                    if ((edges & (1 << edge)) == 0) continue;

                    int origin = _edgeOrigin[edge];
                    int key = scratch.EdgeIndex(_edgeAxis[edge],
                        gx0 + MarchingCubesTables.CornerX[origin],
                        gy0 + MarchingCubesTables.CornerY[origin],
                        gz0 + MarchingCubesTables.CornerZ[origin]);

                    if (scratch.EdgeStamp[key] == scratch.Stamp)
                    {
                        edgeVertex[edge] = scratch.EdgeVertex[key];
                        continue;
                    }

                    int a = MarchingCubesTables.EdgeCornerA[edge];
                    int b = MarchingCubesTables.EdgeCornerB[edge];

                    float da = cornerDensity[a];
                    float db = cornerDensity[b];
                    float t = MathF.Abs(db - da) < 1e-6f ? 0.5f : (Iso - da) / (db - da);
                    t = Math.Clamp(t, 0f, 1f);

                    var pa = new Vector3(
                        (gx0 + MarchingCubesTables.CornerX[a]) * cellSize,
                        (gy0 + MarchingCubesTables.CornerY[a]) * cellSize,
                        (gz0 + MarchingCubesTables.CornerZ[a]) * cellSize);
                    var pb = new Vector3(
                        (gx0 + MarchingCubesTables.CornerX[b]) * cellSize,
                        (gy0 + MarchingCubesTables.CornerY[b]) * cellSize,
                        (gz0 + MarchingCubesTables.CornerZ[b]) * cellSize);

                    Vector3 position = Vector3.Lerp(pa, pb, t);
                    Vector3 normal = SafeNormalize(Vector3.Lerp(cornerNormal[a], cornerNormal[b], t));

                    int vertex = builder.AddVertex(
                        position.X, position.Y, position.Z, normal.X, normal.Y, normal.Z,
                        albedo.R, albedo.G, albedo.B, emissive);

                    scratch.EdgeVertex[key] = vertex;
                    scratch.EdgeStamp[key] = scratch.Stamp;
                    edgeVertex[edge] = vertex;
                }

                for (int i = 0; i < 15; i += 3)
                {
                    int e0 = MarchingCubesTables.TriTable[cubeIndex * 16 + i];
                    if (e0 < 0) break;

                    int e1 = MarchingCubesTables.TriTable[cubeIndex * 16 + i + 1];
                    int e2 = MarchingCubesTables.TriTable[cubeIndex * 16 + i + 2];

                    // The table assumes "inside" = below the iso value; here inside is the high
                    // density, so the order is flipped, or every face would point inwards
                    builder.AddTriangle(edgeVertex[e0], edgeVertex[e2], edgeVertex[e1]);
                }
            }
        }

        return builder.Finish();
    }

    /// <summary>
    /// The edge cache is keyed on the stamp, so a new stamp forgets every vertex index. The density
    /// and normal caches have to survive the cut and are re-stamped by hand. A cut is rare (over
    /// 65535 vertices in one section), so the pass over the caches is affordable.
    /// </summary>
    private static void RestartEdgeCache(Scratch scratch)
    {
        int old = scratch.Stamp;
        scratch.Stamp++;

        for (int i = 0; i < scratch.DensityStamp.Length; i++)
        {
            if (scratch.DensityStamp[i] == old) scratch.DensityStamp[i] = scratch.Stamp;
            if (scratch.NormalStamp[i] == old) scratch.NormalStamp[i] = scratch.Stamp;
        }
    }

    /// <summary>
    /// Collect the block types of the 3x3x3 neighbourhood. Returns false when no surface can run
    /// through here (everything full or everything empty).
    /// </summary>
    private static bool GatherNeighborhood(in MeshGrid grid, Scratch scratch, int x, int y, int z)
    {
        bool anySolid = false;
        bool anyOpen = false;

        byte[] padded = grid.Blocks;
        byte[] refined = scratch.Refined;
        byte[] neighborId = scratch.NeighborId;
        bool[] neighborSolid = scratch.NeighborSolid;

        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int slot = (dx + 1) + 3 * ((dy + 1) + 3 * (dz + 1));
            int index = grid.Index(x + dx, y + dy, z + dz);

            byte id = padded[index];
            bool isSolid = BlockRegistry.IsSolid(id);

            neighborId[slot] = id;
            neighborSolid[slot] = isSolid;

            if (isSolid)
            {
                anySolid = true;
                if (refined[index] != 0) anyOpen = true; // edited, so it contains empty space too
            }
            else
            {
                anyOpen = true;
            }
        }

        return anySolid && anyOpen;
    }

    /// <summary>
    /// Density at a grid point = the mean of the SubPerCell³ sub-voxels around it, cached per
    /// section. The boxes of neighbouring grid points tile without gaps, so every sub-voxel counts
    /// exactly once, and because it depends on position alone, neighbouring blocks and chunks agree
    /// at their borders.
    /// </summary>
    private static float DensityAt(in Context context, int gx, int gy, int gz)
    {
        Scratch scratch = context.Scratch;

        int cacheIndex = scratch.PointIndex(gx, gy, gz);
        if (scratch.DensityStamp[cacheIndex] == scratch.Stamp) return scratch.Density[cacheIndex];

        float value = ComputeDensity(in context, gx, gy, gz);

        scratch.Density[cacheIndex] = value;
        scratch.DensityStamp[cacheIndex] = scratch.Stamp;
        return value;
    }

    private static float ComputeDensity(in Context context, int gx, int gy, int gz)
    {
        int subPerCell = context.SubPerCell;
        float inverseTaps = 1f / (subPerCell * subPerCell * subPerCell * 255f);

        MeshGrid grid = context.Grid;
        byte[] padded = grid.Blocks;
        byte[] refined = context.Scratch.Refined;

        // Sub-voxel box around the grid point, in block-local sub-voxel coordinates of the chunk
        int sx0 = gx * subPerCell - subPerCell / 2;
        int sy0 = gy * subPerCell - subPerCell / 2;
        int sz0 = gz * subPerCell - subPerCell / 2;

        int bx0 = sx0 >> SubVoxels.Shift, bx1 = (sx0 + subPerCell - 1) >> SubVoxels.Shift;
        int by0 = sy0 >> SubVoxels.Shift, by1 = (sy0 + subPerCell - 1) >> SubVoxels.Shift;
        int bz0 = sz0 >> SubVoxels.Shift, bz1 = (sz0 + subPerCell - 1) >> SubVoxels.Shift;

        int sum = 0;

        for (int bz = bz0; bz <= bz1; bz++)
        {
            (int zFrom, int zTo) = Overlap(sz0, subPerCell, bz);

            for (int by = by0; by <= by1; by++)
            {
                (int yFrom, int yTo) = Overlap(sy0, subPerCell, by);

                for (int bx = bx0; bx <= bx1; bx++)
                {
                    // With one cell per block the box of a border grid point reaches half a block
                    // past the padding; that sliver reads the border block instead, which is close
                    // enough for a normal at a far level of detail
                    int index = grid.Index(
                        Math.Clamp(bx, -1, grid.Size), Math.Clamp(by, -1, grid.Height), Math.Clamp(bz, -1, grid.Size));

                    if (!BlockRegistry.IsSolid(padded[index])) continue;

                    (int xFrom, int xTo) = Overlap(sx0, subPerCell, bx);

                    if (refined[index] == 0)
                    {
                        sum += 255 * (xTo - xFrom) * (yTo - yFrom) * (zTo - zFrom);
                        continue;
                    }

                    byte[] field = context.Refinements[index];
                    for (int sz = zFrom; sz < zTo; sz++)
                    for (int sy = yFrom; sy < yTo; sy++)
                    for (int sx = xFrom; sx < xTo; sx++)
                        sum += field[SubVoxels.CellIndex(sx, sy, sz)];
                }
            }
        }

        return sum * inverseTaps;
    }

    /// <summary>The part of the box [start, start + span) that falls into block b, as local sub-voxel indices</summary>
    private static (int From, int To) Overlap(int start, int span, int block)
    {
        int blockStart = block << SubVoxels.Shift;
        int from = Math.Max(start, blockStart) - blockStart;
        int to = Math.Min(start + span, blockStart + SubVoxels.Divisions) - blockStart;
        return (from, to);
    }

    /// <summary>The outward normal is the opposite of the density gradient; cached per grid point like the density</summary>
    private static Vector3 GradientNormal(in Context context, int gx, int gy, int gz)
    {
        Scratch scratch = context.Scratch;

        int cacheIndex = scratch.PointIndex(gx, gy, gz);
        if (scratch.NormalStamp[cacheIndex] == scratch.Stamp) return scratch.Normal[cacheIndex];

        var gradient = new Vector3(
            DensityAt(in context, gx + 1, gy, gz) - DensityAt(in context, gx - 1, gy, gz),
            DensityAt(in context, gx, gy + 1, gz) - DensityAt(in context, gx, gy - 1, gz),
            DensityAt(in context, gx, gy, gz + 1) - DensityAt(in context, gx, gy, gz - 1));

        Vector3 normal = SafeNormalize(-gradient);

        scratch.Normal[cacheIndex] = normal;
        scratch.NormalStamp[cacheIndex] = scratch.Stamp;
        return normal;
    }

    private static Vector3 SafeNormalize(Vector3 v)
        => v.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(v);

    /// <summary>Colour of the block carrying the surface; for air, the nearest solid neighbour (the floor preferred)</summary>
    private static (Color Albedo, byte Emissive) SurfaceMaterial(
        byte[] neighborId, bool[] neighborSolid, int x, int y, int z, int worldX, int worldZ, int scale)
    {
        const int below = 10; // slot of (0,-1,0): floors should keep their own colour

        int bestSlot = 13; // centre
        if (!neighborSolid[13])
        {
            bestSlot = neighborSolid[below] ? below : -1;
            for (int slot = 0; slot < 27 && bestSlot < 0; slot++)
                if (neighborSolid[slot])
                    bestSlot = slot;

            if (bestSlot < 0) return (Color.Magenta, 0); // cannot happen: GatherNeighborhood requires anySolid
        }

        int dx = bestSlot % 3 - 1;
        int dy = bestSlot / 3 % 3 - 1;
        int dz = bestSlot / 9 - 1;

        byte id = neighborId[bestSlot];
        Color albedo = TerrainColors.ForBlock(id, worldX + (x + dx) * scale, (y + dy) * scale, worldZ + (z + dz) * scale);
        byte emissive = (byte)(Math.Clamp(BlockRegistry.Get(id).Emissive, 0f, 1f) * 255f);

        return (albedo, emissive);
    }
}
