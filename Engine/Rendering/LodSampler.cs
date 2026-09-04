using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// Downsamples a chunk and the rim of its neighbours into a coarse <see cref="MeshGrid"/> for a
/// distant level of detail. A coarse block is solid as soon as any of the fine blocks inside it is:
/// that dilation keeps the coarse surface at or above the fine one, so where two levels meet the
/// coarse mesh covers the seam instead of leaving a crack to see through. The material is the one
/// on top, which is what the colour of the surface should follow.
/// </summary>
public static class LodSampler
{
    /// <summary>
    /// <paramref name="neighbours"/> is the 3x3 neighbourhood as [dx + 1 + 3 * (dz + 1)], with the
    /// chunk itself in the middle; a missing neighbour reads as air. Rows below the world count as
    /// solid, so the never-visible underside produces no faces.
    /// </summary>
    public static void Sample(Chunk?[] neighbours, int worldHeight, int scale, byte[] target)
    {
        int size = Chunk.Size / scale;
        int height = worldHeight / scale;
        var grid = new MeshGrid(target, size, height, scale);

        for (int cy = -1; cy <= height; cy++)
        for (int cz = -1; cz <= size; cz++)
        for (int cx = -1; cx <= size; cx++)
        {
            byte block;

            if (cy < 0) block = BlockRegistry.Terrain;
            else if (cy >= height) block = BlockRegistry.Air;
            else block = TopmostSolid(neighbours, worldHeight, cx * scale, cy * scale, cz * scale, scale);

            target[grid.Index(cx, cy, cz)] = block;
        }
    }

    private static byte TopmostSolid(Chunk?[] neighbours, int worldHeight, int fineX, int fineY, int fineZ, int scale)
    {
        // The cell lies inside exactly one chunk of the neighbourhood: cells never straddle a border
        int dx = fineX < 0 ? -1 : fineX >= Chunk.Size ? 1 : 0;
        int dz = fineZ < 0 ? -1 : fineZ >= Chunk.Size ? 1 : 0;

        Chunk? chunk = neighbours[dx + 1 + 3 * (dz + 1)];
        if (chunk == null) return BlockRegistry.Air;

        int localX = fineX - dx * Chunk.Size;
        int localZ = fineZ - dz * Chunk.Size;

        for (int y = fineY + scale - 1; y >= fineY; y--)
        for (int z = localZ; z < localZ + scale; z++)
        for (int x = localX; x < localX + scale; x++)
        {
            int id = chunk.GetLocal(x, y, z, worldHeight);
            if (BlockRegistry.IsSolid(id)) return (byte)id;
        }

        return BlockRegistry.Air;
    }
}
