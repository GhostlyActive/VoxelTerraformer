using Raylib_cs;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// Builds the vertex data of a chunk from a padded block snapshot.
/// Makes no raylib calls at all, which is what allows it to run on a worker thread.
/// </summary>
public static class ChunkMesher
{
    // Chunk plus a one-voxel border in every direction (for face culling and AO across chunk borders)
    public const int PaddedSize = Chunk.Size + 2;

    public static int PaddedLength(int worldHeight) => PaddedSize * PaddedSize * (worldHeight + 2);

    // Takes local coordinates -1..Size, or -1..worldHeight on Y
    public static int Index(int x, int y, int z) => (x + 1) + PaddedSize * ((z + 1) + PaddedSize * (y + 1));

    private struct FaceInfo
    {
        public int Nx, Ny, Nz;                  // normal
        public int Ux, Uy, Uz;                  // tangent u (for AO)
        public int Vx, Vy, Vz;                  // tangent v (for AO)
        public (int X, int Y, int Z)[] Corners; // 4 corners, counter-clockwise seen from outside
        public float Shade;                     // baked directional ambient (bright on top, dark below)
    }

    private static readonly FaceInfo[] _faces =
    {
        new() { Nx = 0, Ny = 1, Nz = 0, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 0, Vz = 1, Shade = 1.00f,
                Corners = new[] { (0, 1, 1), (1, 1, 1), (1, 1, 0), (0, 1, 0) } },
        new() { Nx = 0, Ny = -1, Nz = 0, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 0, Vz = 1, Shade = 0.55f,
                Corners = new[] { (0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1) } },
        new() { Nx = 1, Ny = 0, Nz = 0, Ux = 0, Uy = 0, Uz = 1, Vx = 0, Vy = 1, Vz = 0, Shade = 0.80f,
                Corners = new[] { (1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1) } },
        new() { Nx = -1, Ny = 0, Nz = 0, Ux = 0, Uy = 0, Uz = 1, Vx = 0, Vy = 1, Vz = 0, Shade = 0.80f,
                Corners = new[] { (0, 0, 1), (0, 1, 1), (0, 1, 0), (0, 0, 0) } },
        new() { Nx = 0, Ny = 0, Nz = 1, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 1, Vz = 0, Shade = 0.72f,
                Corners = new[] { (1, 0, 1), (1, 1, 1), (0, 1, 1), (0, 0, 1) } },
        new() { Nx = 0, Ny = 0, Nz = -1, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 1, Vz = 0, Shade = 0.72f,
                Corners = new[] { (0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0) } },
    };

    public static ChunkMeshData[] Build(MeshBuilder builder, byte[] padded, Dictionary<int, byte[]> refinements, int worldHeight, int worldX, int worldZ)
        => Build(builder, MeshGrid.FullDetail(padded, worldHeight), refinements, worldX, worldZ, 0, worldHeight);

    public static ChunkMeshData[] Build(MeshBuilder builder, byte[] padded, Dictionary<int, byte[]> refinements, int worldHeight,
        int worldX, int worldZ, int yStart, int yEnd)
        => Build(builder, MeshGrid.FullDetail(padded, worldHeight), refinements, worldX, worldZ, yStart, yEnd);

    /// <summary>
    /// Meshes only the blocks in [yStart, yEnd); sections partition a column seamlessly.
    ///
    /// Faces are merged greedily: a slice is turned into a mask of the faces it needs, and equal
    /// neighbours in that mask collapse into the largest rectangle they form. A flat field of grass
    /// becomes a handful of quads instead of a thousand, which is where most of the vertex count of
    /// a blocky world goes.
    /// </summary>
    /// <summary>
    /// Meshes the rows [yStart, yEnd) of <paramref name="grid"/>. Refinements only exist at full
    /// detail; a coarse grid passes an empty dictionary. Vertices come out in chunk-local metres.
    /// </summary>
    public static ChunkMeshData[] Build(MeshBuilder builder, in MeshGrid grid, Dictionary<int, byte[]> refinements,
        int worldX, int worldZ, int yStart, int yEnd)
        => Build(builder, in grid, refinements, worldX, worldZ, yStart, yEnd, 0, true);

    /// <summary>
    /// <paramref name="worldY"/> offsets the colour hash for grids that do not start at the bottom
    /// of the world (voxel bodies); <paramref name="directionalShade"/> off drops the baked
    /// "top is bright, bottom is dark", which on a sphere would fight the sunlight.
    /// </summary>
    public static ChunkMeshData[] Build(MeshBuilder builder, in MeshGrid grid, Dictionary<int, byte[]> refinements,
        int worldX, int worldZ, int yStart, int yEnd, int worldY, bool directionalShade)
    {
        builder.Clear();

        if (!MeshSectionScan.CanHaveSurface(in grid, refinements, yStart, yEnd)) return Array.Empty<ChunkMeshData>();

        byte[] padded = grid.Blocks;

        // Carved blocks carry their own sub-voxel shape and merge with nothing, so they go first
        if (refinements.Count > 0)
            for (int y = yStart; y < yEnd; y++)
            for (int z = 0; z < grid.Size; z++)
            for (int x = 0; x < grid.Size; x++)
            {
                int index = grid.Index(x, y, z);
                int id = padded[index];

                if (!BlockRegistry.IsSolid(id)) continue;
                if (!refinements.TryGetValue(index, out byte[]? field)) continue;

                EmitRefinedBlock(builder, in grid, refinements, x, y, z, field,
                    TerrainColors.ForBlock(id, worldX + x, worldY + y, worldZ + z), EmissiveOf(id));
            }

        // The mask holds one slice: the two axes across it are two of {Size, Size, Height}
        Cell[] mask = RentMask(grid.Size * Math.Max(grid.Size, grid.Height));

        for (int f = 0; f < _faces.Length; f++)
            GreedyFace(builder, in grid, refinements, mask,
                in _faces[f], f, worldX, worldY, worldZ, yStart, yEnd, directionalShade);

        return builder.Finish();
    }

    [ThreadStatic] private static Cell[]? _mask;

    private static Cell[] RentMask(int length)
    {
        if (_mask == null || _mask.Length < length) _mask = new Cell[length];
        return _mask;
    }

    /// <summary>
    /// One entry of the merge mask: everything two faces have to share to be interchangeable,
    /// packed into a single word. The mask is cleared, written and compared a few hundred thousand
    /// times per chunk, and one word does all three in a single instruction.
    ///
    /// Colour in the low 24 bits, emissive above it, then the four corner occlusion values at two
    /// bits each, and a bit on top marking the entry as filled. All zero means "no face here".
    /// </summary>
    private readonly record struct Cell(ulong Bits)
    {
        private const int EmissiveShift = 24;
        private const int OcclusionShift = 32;
        private const ulong PresentBit = 1UL << 40;

        public static Cell From(Color albedo, byte emissive, Span<int> occlusion) => new(
            albedo.R |
            ((ulong)albedo.G << 8) |
            ((ulong)albedo.B << 16) |
            ((ulong)emissive << EmissiveShift) |
            ((ulong)occlusion[0] << OcclusionShift) |
            ((ulong)occlusion[1] << (OcclusionShift + 2)) |
            ((ulong)occlusion[2] << (OcclusionShift + 4)) |
            ((ulong)occlusion[3] << (OcclusionShift + 6)) |
            PresentBit);

        public bool Present => (Bits & PresentBit) != 0;

        public byte R => (byte)Bits;
        public byte G => (byte)(Bits >> 8);
        public byte B => (byte)(Bits >> 16);
        public byte Emissive => (byte)(Bits >> EmissiveShift);

        public int Occlusion(int corner) => (int)((Bits >> (OcclusionShift + corner * 2)) & 3);
    }

    /// <summary>
    /// All the faces pointing one way. Every slice perpendicular to that direction is filled into a
    /// mask and then cut into rectangles.
    /// </summary>
    private static void GreedyFace(
        MeshBuilder builder,
        in MeshGrid grid,
        Dictionary<int, byte[]> refinements,
        Cell[] mask,
        in FaceInfo face,
        int faceIndex,
        int worldX, int worldY, int worldZ,
        int yStart, int yEnd,
        bool directionalShade)
    {
        byte[] padded = grid.Blocks;
        int scale = grid.Scale;
        float shade = directionalShade ? face.Shade : 1f;

        int normalAxis = AxisOf(face.Nx, face.Ny);
        int uAxis = AxisOf(face.Ux, face.Uy);
        int vAxis = AxisOf(face.Vx, face.Vy);

        // Which corner sits at which combination of the two tangents, indexed by u * 2 + v
        Span<int> cornerAt = stackalloc int[4];

        for (int i = 0; i < 4; i++)
        {
            (int cx, int cy, int cz) = face.Corners[i];
            int alongU = cx * face.Ux + cy * face.Uy + cz * face.Uz;
            int alongV = cx * face.Vx + cy * face.Vy + cz * face.Vz;

            cornerAt[alongU * 2 + alongV] = i;
        }

        (int sliceLow, int sliceHigh) = AxisRange(normalAxis, grid.Size, yStart, yEnd);
        (int uLow, int uHigh) = AxisRange(uAxis, grid.Size, yStart, yEnd);
        (int vLow, int vHigh) = AxisRange(vAxis, grid.Size, yStart, yEnd);

        int uCount = uHigh - uLow;
        int vCount = vHigh - vLow;

        Span<int> block = stackalloc int[3];
        Span<int> ambientOcclusion = stackalloc int[4];

        int normalStride = StrideOf(in grid, normalAxis);
        int uStride = StrideOf(in grid, uAxis);
        int vStride = StrideOf(in grid, vAxis);
        int toNeighbor = face.Nx * StrideOf(in grid, 0) + face.Ny * StrideOf(in grid, 1) + face.Nz * StrideOf(in grid, 2);

        // Only a chunk that has been carved has any refinements at all, and the volume is walked
        // once per face direction, so the probe per cell is worth skipping where there is nothing
        bool anyRefinements = refinements.Count > 0;

        for (int slice = sliceLow; slice < sliceHigh; slice++)
        {
            int sliceBase = grid.Index(0, 0, 0) + normalStride * slice;

            for (int v = 0; v < vCount; v++)
            {
                int rowBase = sliceBase + vStride * (vLow + v);

                for (int u = 0; u < uCount; u++)
                {
                    int cell = v * uCount + u;
                    mask[cell] = default;

                    int index = rowBase + uStride * (uLow + u);
                    int id = padded[index];

                    if (!BlockRegistry.IsSolid(id)) continue;
                    if (anyRefinements && refinements.ContainsKey(index)) continue; // has its own shape
                    if (NeighborOccludesAt(padded, refinements, index + toNeighbor, faceIndex)) continue;

                    // Everything past here is an actual face, which is the minority of cells, so
                    // this is where the block position is worth working out
                    block[normalAxis] = slice;
                    block[uAxis] = uLow + u;
                    block[vAxis] = vLow + v;

                    int x = block[0], y = block[1], z = block[2];

                    AmbientOcclusion(in grid, in face, x, y, z, ambientOcclusion);

                    mask[cell] = Cell.From(
                        TerrainColors.ForBlock(id, worldX + x * scale, worldY + y * scale, worldZ + z * scale), EmissiveOf(id), ambientOcclusion);
                }
            }

            for (int v = 0; v < vCount; v++)
            for (int u = 0; u < uCount; u++)
            {
                Cell cell = mask[v * uCount + u];
                if (!cell.Present) continue;

                // Ambient occlusion is interpolated across a face, so a face may only be stretched
                // along a tangent its shading does not change along - otherwise the merged rectangle
                // would spread a corner shadow over everything it swallowed. A face lit evenly is
                // flat both ways and grows into a rectangle; one with a dark edge still grows into a
                // strip running parallel to that edge.
                bool flatAlongU =
                    cell.Occlusion(cornerAt[0]) == cell.Occlusion(cornerAt[2]) &&
                    cell.Occlusion(cornerAt[1]) == cell.Occlusion(cornerAt[3]);

                bool flatAlongV =
                    cell.Occlusion(cornerAt[0]) == cell.Occlusion(cornerAt[1]) &&
                    cell.Occlusion(cornerAt[2]) == cell.Occlusion(cornerAt[3]);

                int width = 1;
                if (flatAlongU)
                    while (u + width < uCount && mask[v * uCount + u + width] == cell) width++;

                int height = 1;
                if (flatAlongV)
                    while (v + height < vCount && RowMatches(mask, uCount, u, v + height, width, cell)) height++;

                for (int j = 0; j < height; j++)
                for (int i = 0; i < width; i++)
                    mask[(v + j) * uCount + u + i] = default;

                block[normalAxis] = slice;
                block[uAxis] = uLow + u;
                block[vAxis] = vLow + v;

                EmitMergedFace(builder, in face,
                    block[0], block[1], block[2], uAxis, vAxis, width, height, scale, shade, cell);

                u += width - 1;
            }
        }
    }

    private static bool RowMatches(Cell[] mask, int uCount, int u, int v, int width, Cell cell)
    {
        for (int i = 0; i < width; i++)
            if (mask[v * uCount + u + i] != cell) return false;

        return true;
    }

    /// <summary>
    /// How far one step along a world axis moves in the padded array. The index is linear in all
    /// three, which lets a slice be walked with additions instead of a multiply per cell.
    /// </summary>
    private static int StrideOf(in MeshGrid grid, int axis) => axis switch
    {
        0 => 1,
        1 => grid.Stride * grid.Stride,
        _ => grid.Stride,
    };

    /// <summary>Which world axis a unit vector of the face basis points along</summary>
    private static int AxisOf(int componentX, int componentY) => componentX != 0 ? 0 : componentY != 0 ? 1 : 2;

    private static (int Low, int High) AxisRange(int axis, int size, int yStart, int yEnd)
        => axis == 1 ? (yStart, yEnd) : (0, size);

    private static byte EmissiveOf(int id) => (byte)(Math.Clamp(BlockRegistry.Get(id).Emissive, 0f, 1f) * 255f);

    /// <summary>
    /// A merged rectangle, and with a width and height of 1 an ordinary single face. The corner
    /// offsets of a face are 0 or 1 per axis; along the two tangents a 1 stretches to the size of
    /// the rectangle, and along the normal it stays put.
    /// </summary>
    private static void EmitMergedFace(
        MeshBuilder builder,
        in FaceInfo face,
        int x, int y, int z,
        int uAxis, int vAxis,
        int width, int height,
        int scale,
        float shade,
        Cell cell)
    {
        builder.EnsureRoom(4);

        Span<int> corner = stackalloc int[3];
        Span<int> vertex = stackalloc int[4];

        for (int i = 0; i < 4; i++)
        {
            (int cx, int cy, int cz) = face.Corners[i];

            corner[0] = cx;
            corner[1] = cy;
            corner[2] = cz;

            corner[uAxis] *= width;
            corner[vAxis] *= height;

            float light = shade * (0.45f + 0.55f * (cell.Occlusion(i) / 3f));

            vertex[i] = builder.AddVertex(
                (x + corner[0]) * scale, (y + corner[1]) * scale, (z + corner[2]) * scale,
                face.Nx, face.Ny, face.Nz,
                (byte)(cell.R * light), (byte)(cell.G * light), (byte)(cell.B * light), cell.Emissive);
        }

        // Split the quad along its brighter diagonal, or the darkened corner smears across both
        // triangles and flat ground picks up a visible grain
        if (cell.Occlusion(0) + cell.Occlusion(2) >= cell.Occlusion(1) + cell.Occlusion(3))
            builder.AddQuad(vertex[0], vertex[1], vertex[2], vertex[3]);
        else
            builder.AddQuad(vertex[1], vertex[2], vertex[3], vertex[0]);
    }

    // A neighbour only hides a face when it is solid AND its facing sub-voxel border layer is solid
    // all the way through (full blocks are implicitly full)
    private static bool NeighborOccludesAt(byte[] padded, Dictionary<int, byte[]> refinements, int index, int faceIndex)
    {
        if (!BlockRegistry.IsSolid(padded[index])) return false;
        if (refinements.Count == 0) return true; // buried blocks hit this six times over
        if (!refinements.TryGetValue(index, out byte[]? field)) return true;

        return SubVoxels.LayerFull(field!, faceIndex ^ 1);
    }

    private static void EmitRefinedBlock(
        MeshBuilder builder,
        in MeshGrid grid,
        Dictionary<int, byte[]> refinements,
        int x, int y, int z,
        byte[] field,
        Color albedo,
        byte emissive)
    {
        for (int sz = 0; sz < SubVoxels.Divisions; sz++)
        for (int sy = 0; sy < SubVoxels.Divisions; sy++)
        for (int sx = 0; sx < SubVoxels.Divisions; sx++)
        {
            if (!SubVoxels.IsSolid(field, sx, sy, sz)) continue;

            for (int f = 0; f < _faces.Length; f++)
            {
                ref readonly FaceInfo face = ref _faces[f];

                int nsx = sx + face.Nx;
                int nsy = sy + face.Ny;
                int nsz = sz + face.Nz;

                bool occluded;
                if (nsx >= 0 && nsx < SubVoxels.Divisions &&
                    nsy >= 0 && nsy < SubVoxels.Divisions &&
                    nsz >= 0 && nsz < SubVoxels.Divisions)
                {
                    occluded = SubVoxels.IsSolid(field, nsx, nsy, nsz);
                }
                else
                {
                    // Across the block border: check the facing sub-voxel of the neighbouring block
                    int index = grid.Index(x + face.Nx, y + face.Ny, z + face.Nz);
                    if (!BlockRegistry.IsSolid(grid.Blocks[index]))
                    {
                        occluded = false;
                    }
                    else if (!refinements.TryGetValue(index, out byte[]? neighborField))
                    {
                        occluded = true; // Vollblock
                    }
                    else
                    {
                        occluded = SubVoxels.IsSolid(
                            neighborField!,
                            (nsx + SubVoxels.Divisions) & SubVoxels.LowMask,
                            (nsy + SubVoxels.Divisions) & SubVoxels.LowMask,
                            (nsz + SubVoxels.Divisions) & SubVoxels.LowMask);
                    }
                }

                if (occluded) continue;

                EmitSubFace(builder, in face, x, y, z, sx, sy, sz, albedo, emissive);
            }
        }
    }

    private static void EmitSubFace(
        MeshBuilder builder,
        in FaceInfo face,
        int x, int y, int z,
        int sx, int sy, int sz,
        Color albedo,
        byte emissive)
    {
        const float cell = SubVoxels.CellSize;
        float originX = x + sx * cell;
        float originY = y + sy * cell;
        float originZ = z + sz * cell;

        // No sub-voxel AO here; darkened a little so hollows do not read as flat and bright
        float light = face.Shade * 0.92f;
        byte r = (byte)(albedo.R * light);
        byte g = (byte)(albedo.G * light);
        byte b = (byte)(albedo.B * light);

        builder.EnsureRoom(4);

        Span<int> vertex = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            (int cx, int cy, int cz) = face.Corners[i];
            vertex[i] = builder.AddVertex(
                originX + cx * cell, originY + cy * cell, originZ + cz * cell,
                face.Nx, face.Ny, face.Nz, r, g, b, emissive);
        }

        builder.AddQuad(vertex[0], vertex[1], vertex[2], vertex[3]);
    }

    /// <summary>
    /// How much light reaches each of the four corners, 0 (fully tucked in) to 3 (open). Sampled
    /// from the air cell in front of the face, so a corner with neighbours on both sides goes dark.
    /// </summary>
    private static void AmbientOcclusion(in MeshGrid grid, in FaceInfo face, int x, int y, int z, Span<int> result)
    {
        byte[] padded = grid.Blocks;
        int airX = x + face.Nx;
        int airY = y + face.Ny;
        int airZ = z + face.Nz;

        for (int i = 0; i < 4; i++)
        {
            (int cx, int cy, int cz) = face.Corners[i];

            // Signs of the tangents towards this corner
            int signU = (cx * face.Ux + cy * face.Uy + cz * face.Uz) == 1 ? 1 : -1;
            int signV = (cx * face.Vx + cy * face.Vy + cz * face.Vz) == 1 ? 1 : -1;

            bool side1 = BlockRegistry.IsSolid(padded[grid.Index(
                airX + signU * face.Ux, airY + signU * face.Uy, airZ + signU * face.Uz)]);
            bool side2 = BlockRegistry.IsSolid(padded[grid.Index(
                airX + signV * face.Vx, airY + signV * face.Vy, airZ + signV * face.Vz)]);
            bool corner = BlockRegistry.IsSolid(padded[grid.Index(
                airX + signU * face.Ux + signV * face.Vx,
                airY + signU * face.Uy + signV * face.Vy,
                airZ + signU * face.Uz + signV * face.Vz)]);

            result[i] = (side1 && side2)
                ? 0
                : 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
        }
    }
}
