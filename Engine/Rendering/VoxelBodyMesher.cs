using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// Builds the mesh of one sub-chunk of a <see cref="VoxelBody"/> with the same greedy mesher the
/// terrain uses: the sub-chunk plus a one-voxel rim of its neighbours goes into a
/// <see cref="MeshGrid"/>, and equal faces merge into rectangles. Without the merging a planet a
/// few hundred voxels across would be millions of vertices.
///
/// Unlike the terrain there is no directional shade per face. On a sphere every face direction
/// is equally valid, so a baked "top is bright" would fight the sunlight the shader computes.
/// </summary>
public static class VoxelBodyMesher
{
    private static readonly Dictionary<int, byte[]> NoRefinements = new();

    [ThreadStatic] private static byte[]? _grid;

    /// <summary>
    /// Vertices come out in body voxel coordinates, so every sub-chunk of a body is drawn with
    /// the same model matrix.
    /// </summary>
    public static ChunkMeshData[] Build(MeshBuilder builder, VoxelBody body, int originX, int originY, int originZ)
    {
        const int size = VoxelBody.ChunkSize;

        byte[] blocks = _grid ??= new byte[MeshGrid.LengthFor(size, size)];
        var grid = new MeshGrid(blocks, size, size, 1);

        for (int y = -1; y <= size; y++)
        for (int z = -1; z <= size; z++)
        for (int x = -1; x <= size; x++)
            blocks[grid.Index(x, y, z)] = (byte)body.Get(originX + x, originY + y, originZ + z);

        ChunkMeshData[] parts = ChunkMesher.Build(builder, in grid, NoRefinements,
            originX, originZ, 0, size, originY, directionalShade: false);

        // The mesher works in sub-chunk coordinates; the body wants them in its own
        foreach (ChunkMeshData part in parts)
            for (int i = 0; i < part.VertexCount; i++)
            {
                part.Vertices[i].X += originX;
                part.Vertices[i].Y += originY;
                part.Vertices[i].Z += originZ;
            }

        return parts;
    }
}
