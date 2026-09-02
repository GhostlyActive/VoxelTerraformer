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
/// </summary>
public static class SmoothChunkMesher
{
    /// <summary>
    /// Marching-cubes cells per block; has to divide 8 (1, 2, 4 or 8).
    /// 2 gives 0.5 m cells: a very soft landscape, around 8M vertices at full view distance.
    /// 4 gives 0.25 m cells: finer, but four times the geometry.
    /// </summary>
    public const int Divisions = 2;

    private const int SubPerCell = SubVoxels.Divisions / Divisions;
    private const float CellSize = 1f / Divisions;
    private const float Iso = SubVoxels.Iso / 255f;

    // Density grid per block: indices -1 .. Divisions+1 (one extra ring for the gradients)
    private const int GridSize = Divisions + 3;
    private const int GridOffset = 1;

    public static ChunkMeshData Build(
        byte[] padded,
        Dictionary<int, byte[]> refinements,
        int worldHeight,
        int worldX,
        int worldZ)
    {
        var vertices = new List<float>(16384);
        var normals = new List<float>(16384);
        var colors = new List<byte>(24576);

        var neighborId = new byte[27];
        var neighborSolid = new bool[27];
        var neighborField = new byte[27][];
        var axisSpan = new AxisSpan[GridSize];
        var density = new float[GridSize * GridSize * GridSize];

        Span<float> cornerDensity = stackalloc float[8];
        Span<Vector3> cornerNormal = stackalloc Vector3[8];
        Span<Vector3> edgePosition = stackalloc Vector3[12];
        Span<Vector3> edgeNormal = stackalloc Vector3[12];

        for (int y = 0; y < worldHeight; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            if (!GatherNeighborhood(padded, refinements, x, y, z, neighborId, neighborSolid, neighborField, out bool anyRefined))
                continue;

            if (anyRefined) BuildDensity(neighborSolid, neighborField, density);
            else BuildBlockDensity(neighborSolid, axisSpan, density);

            (Color albedo, byte emissive) = SurfaceMaterial(neighborId, neighborSolid, x, y, z, worldX, worldZ);

            for (int cz = 0; cz < Divisions; cz++)
            for (int cy = 0; cy < Divisions; cy++)
            for (int cx = 0; cx < Divisions; cx++)
            {
                int cubeIndex = 0;
                for (int corner = 0; corner < 8; corner++)
                {
                    int gx = cx + MarchingCubesTables.CornerX[corner];
                    int gy = cy + MarchingCubesTables.CornerY[corner];
                    int gz = cz + MarchingCubesTables.CornerZ[corner];

                    float value = density[GridIndex(gx, gy, gz)];
                    cornerDensity[corner] = value;
                    if (value < Iso) cubeIndex |= 1 << corner;
                }

                int edges = MarchingCubesTables.EdgeMask[cubeIndex];
                if (edges == 0) continue;

                for (int corner = 0; corner < 8; corner++)
                {
                    if ((edges & CornerEdgeMask[corner]) == 0) continue;
                    cornerNormal[corner] = GradientNormal(
                        density,
                        cx + MarchingCubesTables.CornerX[corner],
                        cy + MarchingCubesTables.CornerY[corner],
                        cz + MarchingCubesTables.CornerZ[corner]);
                }

                for (int edge = 0; edge < 12; edge++)
                {
                    if ((edges & (1 << edge)) == 0) continue;

                    int a = MarchingCubesTables.EdgeCornerA[edge];
                    int b = MarchingCubesTables.EdgeCornerB[edge];

                    float da = cornerDensity[a];
                    float db = cornerDensity[b];
                    float t = MathF.Abs(db - da) < 1e-6f ? 0.5f : (Iso - da) / (db - da);
                    t = Math.Clamp(t, 0f, 1f);

                    var pa = new Vector3(
                        x + (cx + MarchingCubesTables.CornerX[a]) * CellSize,
                        y + (cy + MarchingCubesTables.CornerY[a]) * CellSize,
                        z + (cz + MarchingCubesTables.CornerZ[a]) * CellSize);
                    var pb = new Vector3(
                        x + (cx + MarchingCubesTables.CornerX[b]) * CellSize,
                        y + (cy + MarchingCubesTables.CornerY[b]) * CellSize,
                        z + (cz + MarchingCubesTables.CornerZ[b]) * CellSize);

                    edgePosition[edge] = Vector3.Lerp(pa, pb, t);
                    edgeNormal[edge] = SafeNormalize(Vector3.Lerp(cornerNormal[a], cornerNormal[b], t));
                }

                for (int i = 0; i < 15; i += 3)
                {
                    int e0 = MarchingCubesTables.TriTable[cubeIndex * 16 + i];
                    if (e0 < 0) break;

                    int e1 = MarchingCubesTables.TriTable[cubeIndex * 16 + i + 1];
                    int e2 = MarchingCubesTables.TriTable[cubeIndex * 16 + i + 2];

                    // The table assumes "inside" = below the iso value; here inside is the high
                    // density, so the order is flipped, or every face would point inwards
                    Emit(vertices, normals, colors, edgePosition[e0], edgeNormal[e0], albedo, emissive);
                    Emit(vertices, normals, colors, edgePosition[e2], edgeNormal[e2], albedo, emissive);
                    Emit(vertices, normals, colors, edgePosition[e1], edgeNormal[e1], albedo, emissive);
                }
            }
        }

        return new ChunkMeshData
        {
            Vertices = vertices.ToArray(),
            Normals = normals.ToArray(),
            Colors = colors.ToArray(),
            VertexCount = vertices.Count / 3,
        };
    }

    private static void Emit(
        List<float> vertices, List<float> normals, List<byte> colors,
        Vector3 position, Vector3 normal, Color albedo, byte emissive)
    {
        vertices.Add(position.X);
        vertices.Add(position.Y);
        vertices.Add(position.Z);

        normals.Add(normal.X);
        normals.Add(normal.Y);
        normals.Add(normal.Z);

        colors.Add(albedo.R);
        colors.Add(albedo.G);
        colors.Add(albedo.B);
        colors.Add(emissive);
    }

    /// <summary>
    /// Collect the block types and sub-voxel density fields of the 3x3x3 neighbourhood.
    /// Returns false when no surface can run through here (everything full or everything empty).
    /// </summary>
    private static bool GatherNeighborhood(
        byte[] padded,
        Dictionary<int, byte[]> refinements,
        int x, int y, int z,
        byte[] neighborId,
        bool[] neighborSolid,
        byte[]?[] neighborField,
        out bool anyRefined)
    {
        bool anySolid = false;
        bool anyOpen = false;
        anyRefined = false;

        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int slot = (dx + 1) + 3 * ((dy + 1) + 3 * (dz + 1));
            int index = ChunkMesher.Index(x + dx, y + dy, z + dz);

            byte id = padded[index];
            bool isSolid = BlockRegistry.IsSolid(id);

            neighborId[slot] = id;
            neighborSolid[slot] = isSolid;
            neighborField[slot] = null;

            if (isSolid)
            {
                anySolid = true;
                if (refinements.TryGetValue(index, out byte[]? field))
                {
                    neighborField[slot] = field;
                    anyOpen = true;    // edited, so it contains empty space too
                    anyRefined = true;
                }
            }
            else
            {
                anyOpen = true;
            }
        }

        return anySolid && anyOpen;
    }

    /// <summary>
    /// Density at each grid point = the mean of the SubPerCell³ sub-voxels around it.
    /// The boxes of neighbouring grid points tile without gaps, so every sub-voxel counts exactly once.
    /// It depends on position alone, which is why neighbouring blocks agree at their borders.
    /// </summary>
    private static void BuildDensity(bool[] neighborSolid, byte[]?[] neighborField, float[] density)
    {
        const float inverseTaps = 1f / (SubPerCell * SubPerCell * SubPerCell * 255f);

        for (int gz = -GridOffset; gz < GridSize - GridOffset; gz++)
        for (int gy = -GridOffset; gy < GridSize - GridOffset; gy++)
        for (int gx = -GridOffset; gx < GridSize - GridOffset; gx++)
        {
            int sx = gx * SubPerCell;
            int sy = gy * SubPerCell;
            int sz = gz * SubPerCell;

            int sum = 0;
            for (int oz = -SubPerCell / 2; oz < SubPerCell / 2; oz++)
            for (int oy = -SubPerCell / 2; oy < SubPerCell / 2; oy++)
            for (int ox = -SubPerCell / 2; ox < SubPerCell / 2; ox++)
                sum += Fill(neighborSolid, neighborField, sx + ox, sy + oy, sz + oz);

            density[GridIndex(gx, gy, gz)] = sum * inverseTaps;
        }
    }

    /// <summary>
    /// The fast path for neighbourhoods without any density field at all, which is by far most of
    /// the world. There every block is either completely full or completely empty, so the box filter
    /// does not need its 64 individual samples: it is enough to know how many of them fall into
    /// which neighbouring block per axis. The result is bit-identical to <see cref="BuildDensity"/>.
    /// </summary>
    private static void BuildBlockDensity(bool[] neighborSolid, AxisSpan[] axisSpan, float[] density)
    {
        const float inverseTaps = 1f / (SubPerCell * SubPerCell * SubPerCell);

        for (int g = -GridOffset; g < GridSize - GridOffset; g++)
            axisSpan[g + GridOffset] = SpanFor(g);

        for (int gz = -GridOffset; gz < GridSize - GridOffset; gz++)
        for (int gy = -GridOffset; gy < GridSize - GridOffset; gy++)
        for (int gx = -GridOffset; gx < GridSize - GridOffset; gx++)
        {
            AxisSpan spanX = axisSpan[gx + GridOffset];
            AxisSpan spanY = axisSpan[gy + GridOffset];
            AxisSpan spanZ = axisSpan[gz + GridOffset];

            int taps = 0;
            for (int pz = 0; pz < 2; pz++)
            {
                int countZ = pz == 0 ? spanZ.CountA : spanZ.CountB;
                if (countZ == 0) continue;
                int offsetZ = pz == 0 ? spanZ.OffsetA : spanZ.OffsetA + 1;

                for (int py = 0; py < 2; py++)
                {
                    int countY = py == 0 ? spanY.CountA : spanY.CountB;
                    if (countY == 0) continue;
                    int offsetY = py == 0 ? spanY.OffsetA : spanY.OffsetA + 1;

                    for (int px = 0; px < 2; px++)
                    {
                        int countX = px == 0 ? spanX.CountA : spanX.CountB;
                        if (countX == 0) continue;
                        int offsetX = px == 0 ? spanX.OffsetA : spanX.OffsetA + 1;

                        int slot = (offsetX + 1) + 3 * ((offsetY + 1) + 3 * (offsetZ + 1));
                        if (neighborSolid[slot]) taps += countX * countY * countZ;
                    }
                }
            }

            density[GridIndex(gx, gy, gz)] = taps * inverseTaps;
        }
    }

    /// <summary>How the SubPerCell sample points of one axis are split across two neighbouring blocks</summary>
    private readonly record struct AxisSpan(int OffsetA, int CountA, int CountB);

    private static AxisSpan SpanFor(int g)
    {
        int start = g * SubPerCell - SubPerCell / 2;
        int offsetA = start >> SubVoxels.Shift; // arithmetischer Shift = Abrunden, auch negativ

        int countA = 0;
        for (int o = 0; o < SubPerCell; o++)
            if (((start + o) >> SubVoxels.Shift) == offsetA) countA++;

        return new AxisSpan(offsetA, countA, SubPerCell - countA);
    }

    /// <summary>Fill 0..255; coordinates are block-local sub-voxels and may reach into the neighbours</summary>
    private static int Fill(bool[] neighborSolid, byte[]?[] neighborField, int sx, int sy, int sz)
    {
        int slot = ((sx >> SubVoxels.Shift) + 1)
                 + 3 * (((sy >> SubVoxels.Shift) + 1)
                 + 3 * ((sz >> SubVoxels.Shift) + 1));

        byte[]? field = neighborField[slot];
        if (field == null) return neighborSolid[slot] ? 255 : 0;

        return SubVoxels.Get(field, sx & SubVoxels.LowMask, sy & SubVoxels.LowMask, sz & SubVoxels.LowMask);
    }

    /// <summary>The outward normal is the opposite of the density gradient</summary>
    private static Vector3 GradientNormal(float[] density, int gx, int gy, int gz)
    {
        var gradient = new Vector3(
            density[GridIndex(gx + 1, gy, gz)] - density[GridIndex(gx - 1, gy, gz)],
            density[GridIndex(gx, gy + 1, gz)] - density[GridIndex(gx, gy - 1, gz)],
            density[GridIndex(gx, gy, gz + 1)] - density[GridIndex(gx, gy, gz - 1)]);

        return SafeNormalize(-gradient);
    }

    private static Vector3 SafeNormalize(Vector3 v)
        => v.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(v);

    /// <summary>Colour of the block carrying the surface; for air, the nearest solid neighbour (the floor preferred)</summary>
    private static (Color Albedo, byte Emissive) SurfaceMaterial(
        byte[] neighborId, bool[] neighborSolid, int x, int y, int z, int worldX, int worldZ)
    {
        const int below = 10; // slot of (0,-1,0): floors should keep their own colour

        int bestSlot = 13; // Mitte
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
        Color albedo = TerrainColors.ForBlock(id, worldX + x + dx, y + dy, worldZ + z + dz);
        byte emissive = (byte)(Math.Clamp(BlockRegistry.Get(id).Emissive, 0f, 1f) * 255f);

        return (albedo, emissive);
    }

    private static int GridIndex(int gx, int gy, int gz)
        => (gx + GridOffset) + GridSize * ((gy + GridOffset) + GridSize * (gz + GridOffset));

    // Which edges touch which corner; saves computing gradients that are never used
    private static readonly int[] CornerEdgeMask = BuildCornerEdgeMask();

    private static int[] BuildCornerEdgeMask()
    {
        var masks = new int[8];
        for (int edge = 0; edge < 12; edge++)
        {
            masks[MarchingCubesTables.EdgeCornerA[edge]] |= 1 << edge;
            masks[MarchingCubesTables.EdgeCornerB[edge]] |= 1 << edge;
        }
        return masks;
    }
}
